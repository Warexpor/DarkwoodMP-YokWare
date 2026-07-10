using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Automatic desync detection (v0.6). Every machine periodically broadcasts
/// a digest of convergent shared state ("synccheck"); receivers compare it
/// against their own. A mismatch only counts when it is STABLE - the same
/// local and remote values mismatching on two consecutive checks - because
/// a digest is always a snapshot from RTT+interval ago and in-flight changes
/// (a flag that just synced, a door mid-replay) look like transient
/// mismatches. Confirmed desyncs are logged as [SyncCheck] DESYNC, surfaced
/// in chat, and answered with SAFE correctives: every corrective rides an
/// existing idempotent channel (forward-only clock, max-based workbench
/// level, replay-tolerant flag/lock/build snapshots), so a spurious
/// correction can never make things worse.
///
/// Digest fields and why they are comparable across machines:
///  - day / clock / workbench level: global Controller state, synced.
///  - flags: convergent session store (local sends + applied remotes).
///  - locks / builds: session histories that record both sides' actions.
/// Deliberately NOT in the digest: enemy populations and door-open states
/// (differ legitimately with loaded chunks), inventories (per player),
/// map/skills (per player by design).
/// </summary>
public class SyncCheck
{
    private const float SendInterval = 20f;
    private const float CorrectiveCooldown = 60f;
    private const float ChatNoticeCooldown = 300f;
    private const float ClockRelativeTolerance = 0.05f;

    private readonly NetworkLayer _network;
    private float _nextSend;

    private class Strike
    {
        public string Local = "";
        public string Remote = "";
        public int Count;
    }

    // (senderId|field) -> stability tracker
    private readonly Dictionary<string, Strike> _strikes = new();
    private readonly Dictionary<string, float> _lastCorrective = new();
    private readonly Dictionary<string, float> _lastChatNotice = new();

    public SyncCheck(NetworkLayer network)
    {
        _network = network;
    }

    public static uint Fnv1a(string text)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in text)
            {
                hash ^= c;
                hash *= 16777619u;
            }
            return hash;
        }
    }

    private class Digest
    {
        public int Day;
        public float Clock;
        public int WorkbenchLevel;
        public int FlagCount; public uint FlagHash;
        public int LockCount; public uint LockHash;
        public int BuildCount; public uint BuildHash;

        public string Serialize()
        {
            var inv = CultureInfo.InvariantCulture;
            return string.Join(":", new[]
            {
                "v1",
                Day.ToString(inv),
                Clock.ToString("F1", inv),
                WorkbenchLevel.ToString(inv),
                FlagCount.ToString(inv), FlagHash.ToString("X8"),
                LockCount.ToString(inv), LockHash.ToString("X8"),
                BuildCount.ToString(inv), BuildHash.ToString("X8"),
            });
        }

        public static Digest? Parse(string payload)
        {
            var p = payload.Split(':');
            if (p.Length < 10 || p[0] != "v1") return null;
            var inv = CultureInfo.InvariantCulture;
            try
            {
                return new Digest
                {
                    Day = int.Parse(p[1], inv),
                    Clock = float.Parse(p[2], NumberStyles.Float, inv),
                    WorkbenchLevel = int.Parse(p[3], inv),
                    FlagCount = int.Parse(p[4], inv), FlagHash = uint.Parse(p[5], NumberStyles.HexNumber, inv),
                    LockCount = int.Parse(p[6], inv), LockHash = uint.Parse(p[7], NumberStyles.HexNumber, inv),
                    BuildCount = int.Parse(p[8], inv), BuildHash = uint.Parse(p[9], NumberStyles.HexNumber, inv),
                };
            }
            catch { return null; }
        }
    }

    private Digest? BuildLocalDigest()
    {
        var controller = UnityEngine.Object.FindObjectOfType<Controller>();
        if (controller == null) return null; // no world loaded yet

        var digest = new Digest
        {
            Day = controller.day,
            Clock = controller.gameTime,
            WorkbenchLevel = controller.workbenchLevel,
        };

        Patches.Flags_Patch.GetDigest(out digest.FlagCount, out digest.FlagHash);
        DependencyInjection.ServiceLocator.Resolve<LockSync>()?.GetDigest(out digest.LockCount, out digest.LockHash);
        DependencyInjection.ServiceLocator.Resolve<BuildSync>()?.GetDigest(out digest.BuildCount, out digest.BuildHash);
        return digest;
    }

    public void OnUpdate()
    {
        if (!_network.IsConnected) return;
        if (Time.unscaledTime < _nextSend) return;
        _nextSend = Time.unscaledTime + SendInterval;

        try
        {
            var digest = BuildLocalDigest();
            if (digest == null) return;

            // Unreliable on purpose: the digest repeats every 20s anyway
            _network.Send(new SyncCheckDigestPacket
            {
                PlayerId = Math.Max(_network.LocalClientId, 0),
                Digest = digest.Serialize()
            });
            if (NetworkLayer.VerboseLogging)
                ModLogger.Msg($"[SyncCheck] Sent digest {digest.Serialize()}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[SyncCheck] Send failed: {ex.Message}");
        }
    }

    public void OnRemoteDigest(int senderId, string payload)
    {
        try
        {
            var remote = Digest.Parse(payload);
            var local = BuildLocalDigest();
            if (remote == null || local == null) return;

            CompareField(senderId, "day", local.Day.ToString(), remote.Day.ToString(),
                local.Day == remote.Day,
                () => CorrectDay(local));

            // Clock only compares within the same day (a day mismatch already
            // fires above and dominates every clock difference)
            if (local.Day == remote.Day)
            {
                var inv = CultureInfo.InvariantCulture;
                var tolerance = ClockRelativeTolerance
                    * Math.Max(Math.Max(Math.Abs(local.Clock), Math.Abs(remote.Clock)), 1f);
                CompareField(senderId, "clock",
                    local.Clock.ToString("F1", inv), remote.Clock.ToString("F1", inv),
                    Math.Abs(local.Clock - remote.Clock) <= tolerance,
                    () => CorrectClock(local, remote));
            }

            CompareField(senderId, "workbench", local.WorkbenchLevel.ToString(), remote.WorkbenchLevel.ToString(),
                local.WorkbenchLevel == remote.WorkbenchLevel,
                () => CorrectWorkbench(local, remote));

            // Histories are additive: a mismatch means one side is MISSING
            // entries, but the hash cannot say which side. So each machine
            // resends its OWN history - the missing side gets the entries it
            // lacks, the other side dedupes the replay. Resending only on the
            // senior would leave a junior-missing entry stranded forever.
            CompareField(senderId, "flags",
                local.FlagCount + "/" + local.FlagHash.ToString("X8"),
                remote.FlagCount + "/" + remote.FlagHash.ToString("X8"),
                local.FlagCount == remote.FlagCount && local.FlagHash == remote.FlagHash,
                ResendSessionFlags);

            CompareField(senderId, "locks",
                local.LockCount + "/" + local.LockHash.ToString("X8"),
                remote.LockCount + "/" + remote.LockHash.ToString("X8"),
                local.LockCount == remote.LockCount && local.LockHash == remote.LockHash,
                () => ResendSnapshot<LockSync>(s => s.CollectSnapshot));

            CompareField(senderId, "builds",
                local.BuildCount + "/" + local.BuildHash.ToString("X8"),
                remote.BuildCount + "/" + remote.BuildHash.ToString("X8"),
                local.BuildCount == remote.BuildCount && local.BuildHash == remote.BuildHash,
                () => ResendSnapshot<BuildSync>(s => s.CollectSnapshot));
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[SyncCheck] Compare failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Stability-based mismatch detection: a field only counts as desynced
    /// when the SAME local and remote values mismatch on two consecutive
    /// digests. Any change on either side resets the strike - in-flight
    /// updates are not desyncs.
    /// </summary>
    private void CompareField(int senderId, string field, string localValue, string remoteValue,
        bool matches, Action corrective)
    {
        var key = senderId + "|" + field;
        if (matches)
        {
            _strikes.Remove(key);
            return;
        }

        if (!_strikes.TryGetValue(key, out var strike)
            || strike.Local != localValue || strike.Remote != remoteValue)
        {
            _strikes[key] = new Strike { Local = localValue, Remote = remoteValue, Count = 1 };
            if (NetworkLayer.VerboseLogging)
                ModLogger.Msg($"[SyncCheck] Transient mismatch {field} (local={localValue} remote({senderId})={remoteValue}) - watching");
            return;
        }

        strike.Count++;
        if (strike.Count < 2) return;

        ModLogger.Warning("SyncCheck", $"DESYNC {field}: local={localValue} remote(p{senderId})={remoteValue} (stable over {strike.Count} checks)");
        NotifyChat(field, senderId);

        if (Time.unscaledTime - GetTime(_lastCorrective, field) < CorrectiveCooldown) return;
        _lastCorrective[field] = Time.unscaledTime;
        try
        {
            corrective();
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[SyncCheck] Corrective for {field} failed: {ex.Message}");
        }
    }

    private static float GetTime(Dictionary<string, float> map, string key)
        => map.TryGetValue(key, out var t) ? t : float.MinValue;

    private void NotifyChat(string field, int senderId)
    {
        if (Time.unscaledTime - GetTime(_lastChatNotice, field) < ChatNoticeCooldown) return;
        _lastChatNotice[field] = Time.unscaledTime;
        ChatManager.Instance?.AddSystemMessage($"Desync detected ({field}, vs player {senderId}) - auto-correcting");
    }

    // ------------------------------------------------------------------
    // Correctives - every one of them rides an existing idempotent channel
    // ------------------------------------------------------------------

    /// <summary>The time authority re-broadcasts its day/night; others adopt via the normal path.</summary>
    private void CorrectDay(Digest local)
    {
        var manager = NetworkManager.Instance;
        if (manager == null || !manager.IsTimeAuthority) return;
        ModLogger.Msg($"[SyncCheck] Corrective: re-broadcasting day {local.Day}");
        DependencyInjection.ServiceLocator.Resolve<WorldSync>()?.SyncDayNightChange(local.Day, manager.IsNight);
    }

    /// <summary>The machine that is AHEAD re-broadcasts its clock (receivers are forward-only).</summary>
    private void CorrectClock(Digest local, Digest remote)
    {
        if (local.Clock <= remote.Clock) return;
        ModLogger.Msg($"[SyncCheck] Corrective: re-broadcasting clock {local.Clock:F1}");
        _network.SendReliable(new GameTimeSyncPacket
        {
            PlayerId = Math.Max(_network.LocalClientId, 0),
            GameTime = local.Clock
        });
    }

    /// <summary>The higher level wins (receivers take max) - only the higher side sends.</summary>
    private void CorrectWorkbench(Digest local, Digest remote)
    {
        if (local.WorkbenchLevel <= remote.WorkbenchLevel) return;
        ModLogger.Msg($"[SyncCheck] Corrective: re-broadcasting workbench level {local.WorkbenchLevel}");
        _network.SendReliable(new WorkbenchLevelPacket
        {
            PlayerId = Math.Max(_network.LocalClientId, 0),
            Level = local.WorkbenchLevel
        });
    }

    /// <summary>Re-send every session flag (setFlag with an unchanged value is a no-op).</summary>
    private void ResendSessionFlags()
    {
        var flags = Patches.Flags_Patch.GetSessionFlags();
        ModLogger.Msg($"[SyncCheck] Corrective: re-broadcasting {flags.Count} session flags");
        var playerId = Math.Max(_network.LocalClientId, 0);
        foreach (var (name, isInt, value) in flags)
        {
            _network.SendReliable(new FlagDeltaPacket
            {
                PlayerId = playerId,
                IsInt = isInt,
                FlagName = name,
                Value = value
            });
        }
    }

    /// <summary>Re-broadcast a module's join-snapshot packets (receive side is idempotent).</summary>
    private void ResendSnapshot<T>(Func<T, Action<List<Packet>>> collect) where T : class
    {
        var module = DependencyInjection.ServiceLocator.Resolve<T>();
        if (module == null) return;
        var packets = new List<Packet>();
        collect(module)(packets);
        ModLogger.Msg($"[SyncCheck] Corrective: re-broadcasting {packets.Count} {typeof(T).Name} snapshot packets");
        foreach (var packet in packets)
            _network.SendReliable(packet);
    }

    public void Reset()
    {
        _strikes.Clear();
        _lastCorrective.Clear();
        _lastChatNotice.Clear();
        _nextSend = 0f;
    }
}

using System;
using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Shared dream session model (night parity):
    /// all connected players enter the initiated dream; death → spectate until session ends.
    /// Host alone decides when the session ends (story outcome or all dead).
    /// Sole authority for the party's completed-preset set (persisted with the world, see
    /// <see cref="EnsureSeeded"/>) and the level slot(s) the running dream is for.
    ///
    /// Level dreams are per player, as in vanilla: <c>Dreams.hadDreamAtLvl2/3/5/6/7</c> are each
    /// player's own. The first player to reach a level brings that level's dream to everyone who
    /// is there, and every player who was in it has that level's slot marked. Someone who was in
    /// it does not get it again on their own level-up; someone who was not (a late joiner, a
    /// player sitting it out dead) still gets a dream for that level, one the party has not
    /// played (a played dream is never repeated; the bunker's slot rolls a random one instead).
    /// </summary>
    internal static class DreamSession
    {
        public enum State
        {
            Idle,
            Starting,
            Active,
            Ending
        }

        public static State Current { get; private set; } = State.Idle;
        public static string PresetName { get; private set; }
        public static int SessionId { get; private set; }

        private static int _nextSessionId = 1; // process-scoped: must stay monotonic across reconnects (peers compare session ids)
        private static readonly HashSet<string> _completedPresets =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Host-resolved random pick for clients still in getPreset("") before DreamStarted.
        /// Vanilla sleep/skill dreams call prepareDream("") and roll inside getPreset.
        /// </summary>
        private static string _pendingHostPreset;

        /// <summary>Bit0=lvl2,1=lvl3,2=lvl5,3=lvl6,4=lvl7 (matches Dreams.hadDreamAtLvl*).</summary>
        public const byte LvlFlag2 = 1 << 0;
        public const byte LvlFlag3 = 1 << 1;
        public const byte LvlFlag5 = 1 << 2;
        public const byte LvlFlag6 = 1 << 3;
        public const byte LvlFlag7 = 1 << 4;

        /// <summary>Level slot(s) the running dream is for (0 = not a level dream). On the wire.</summary>
        internal static byte LevelBits { get; private set; } // reset-in: Reset
        /// <summary>Host: level slot(s) the next dream to begin is for (its own level-up, or the request it takes).</summary>
        internal static byte NextLevelBits; // reset-in: Reset
        /// <summary>Client: level slot(s) its own confirmed level-up wants a dream for (sent with the request).</summary>
        internal static byte PendingRequestBits; // reset-in: Reset

        internal static byte TakePendingRequestBits()
        {
            byte b = PendingRequestBits;
            PendingRequestBits = 0;
            return b;
        }

        /// <summary>Profile the completed set was seeded from (-1 = not seeded).</summary>
        private static int _seededProfile = -1; // reset-in: ResetIncludingCompletions

        internal const string BunkerPreset = "dream_bunker_underground_01";

        /// <summary>How long a session may sit in Starting before the watchdog cleans it up.</summary>
        private const float StartingTimeoutSec = 60f;
        private static int _startingEpoch; // process-scoped: monotonic watchdog epoch

        public static bool IsActive =>
            Current == State.Starting || Current == State.Active || Current == State.Ending;

        public static bool IsStarting => Current == State.Starting;

        public static string PendingHostPreset => _pendingHostPreset;

        public static void SetPendingHostPreset(string presetName)
        {
            if (string.IsNullOrEmpty(presetName)) return;
            _pendingHostPreset = presetName;
        }

        /// <summary>Peek without clearing; getPreset may run more than once for the same pick.</summary>
        public static bool TryGetPendingHostPreset(out string presetName)
        {
            presetName = _pendingHostPreset;
            return !string.IsNullOrEmpty(presetName);
        }

        public static void ClearPendingHostPreset()
        {
            _pendingHostPreset = null;
        }

        /// <summary>
        /// Mirror vanilla one-shot random pool: empty getPreset removes the pick from presetList.
        /// Named and Resources.Load paths do not. Remotes and host named prepare must remove by name.
        /// </summary>
        public static void MirrorPoolRemove(string presetName)
        {
            if (string.IsNullOrEmpty(presetName)) return;
            var d = Dreams.Instance;
            if (d?.presetList == null || d.presetList.Count == 0) return;

            for (int i = d.presetList.Count - 1; i >= 0; i--)
            {
                var p = d.presetList[i];
                if (p == null) continue;
                string n = p.gameObject != null ? p.gameObject.name : p.name;
                if (string.Equals(n, presetName, StringComparison.OrdinalIgnoreCase))
                {
                    d.presetList.RemoveAt(i);
                    ModLog.Event(LogCat.Dream, "MirrorPoolRemove: " + presetName);
                }
            }
        }

        public static string ResolvePresetName(DreamPreset preset)
        {
            if (preset == null) return null;
            if (preset.gameObject != null && !string.IsNullOrEmpty(preset.gameObject.name))
                return preset.gameObject.name;
            return preset.name;
        }

        public static bool IsPresetCompleted(string preset)
        {
            EnsureSeeded();
            return !string.IsNullOrEmpty(preset) && _completedPresets.Contains(preset);
        }

        /// <summary>
        /// The party's played dreams used to live only in memory: after the host restarted, a
        /// played story dream (the bunker) could be offered again. The host's world keeps them
        /// in its co-op sidecar (<see cref="CoopWorldCopyMeta.CompletedDreams"/>), read back once
        /// per loaded profile. Worlds from before that: a host that had its level-2 dream counts
        /// the bunker as played (its level-2 dream was the bunker, or the bunker was done).
        /// </summary>
        internal static void EnsureSeeded()
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Host)
                return;
            if (Core.currentProfile == null || GameScreen.AtTitle)
                return;
            int pid = Core.currentProfile.id;
            if (_seededProfile == pid)
                return;
            if (_seededProfile != -1)
                _completedPresets.Clear(); // another world
            _seededProfile = pid;
            var meta = CoopWorldCopyMeta.TryLoad(pid);
            if (meta?.CompletedDreams != null)
            {
                foreach (string n in meta.CompletedDreams)
                {
                    if (!string.IsNullOrEmpty(n))
                        _completedPresets.Add(n);
                }
            }
            if (Dreams.Instance != null && Dreams.Instance.hadDreamAtLvl2)
                _completedPresets.Add(BunkerPreset);
            ModLog.Event(LogCat.Dream, "Seeded played dreams from the world: " + _completedPresets.Count);
        }

        public static string[] GetCompletedPresets()
        {
            EnsureSeeded();
            if (_completedPresets.Count == 0)
                return Array.Empty<string>();
            var arr = new string[_completedPresets.Count];
            _completedPresets.CopyTo(arr);
            return arr;
        }

        public static void MarkCompleted(string preset)
        {
            if (string.IsNullOrEmpty(preset)) return;
            _completedPresets.Add(preset);
            // Keep random pool aligned so depleted-list refill cannot re-offer it.
            MirrorPoolRemove(preset);
        }

        /// <summary>Host (or local solo) begins a session. Returns false if blocked.</summary>
        public static bool TryBegin(string presetName)
        {
            if (string.IsNullOrEmpty(presetName)) return false;
            if (IsActive)
            {
                // Same preset while Starting = duplicate prepare; treat as success for caller ignore.
                if (Current == State.Starting
                    && string.Equals(PresetName, presetName, StringComparison.OrdinalIgnoreCase))
                {
                    ModLog.Event(LogCat.Dream, "TryBegin no-op — already Starting " + presetName);
                    return false;
                }
                ModLog.Event(LogCat.Dream, $"Reject begin — already active ({Current}): {presetName}");
                return false;
            }

            // Party-once: any peer already finished this preset → do not start again.
            if (IsPresetCompleted(presetName))
            {
                ModLog.Event(LogCat.Dream,
                    "Reject begin — party already completed: " + presetName);
                MirrorPoolRemove(presetName);
                return false;
            }

            SessionId = _nextSessionId++;
            PresetName = presetName;
            Current = State.Starting;
            LevelBits = NextLevelBits;
            NextLevelBits = 0;
            SetPendingHostPreset(presetName);
            FinalDreamsceneManager.OnDreamStarted();
            ArmStartingWatchdog();
            ModLog.Event(LogCat.Dream, $"Starting session {SessionId} preset={presetName}");
            return true;
        }

        /// <summary>
        /// Client begins from host DreamStarted, using the host SessionId rather than minting locally.
        /// (TryBegin+Adopt was minting SessionId=N+1 then adopting N, desyncing logs/guards.)
        /// </summary>
        public static bool BeginFromHost(string presetName, int hostSessionId)
        {
            if (string.IsNullOrEmpty(presetName)) return false;
            if (IsActive)
            {
                if (!string.IsNullOrEmpty(presetName)
                    && !string.Equals(PresetName, presetName, StringComparison.OrdinalIgnoreCase))
                    UpdateActivePreset(presetName);
                if (hostSessionId != 0)
                    AdoptSessionId(hostSessionId);
                return false;
            }

            if (hostSessionId != 0)
            {
                SessionId = hostSessionId;
                if (hostSessionId >= _nextSessionId)
                    _nextSessionId = hostSessionId + 1;
            }
            else
            {
                SessionId = _nextSessionId++;
            }

            PresetName = presetName;
            Current = State.Starting;
            SetPendingHostPreset(presetName);
            FinalDreamsceneManager.OnDreamStarted();
            ArmStartingWatchdog();
            ModLog.Event(LogCat.Dream, $"Starting session {SessionId} preset={presetName} (from host)");
            return true;
        }

        /// <summary>
        /// Host random roll / chain resolved a different preset after TryBegin with a stale name
        /// (prepareDream("") still had previous Dreams.preset). Keep SessionId, swap PresetName.
        /// </summary>
        public static void UpdateActivePreset(string presetName)
        {
            if (string.IsNullOrEmpty(presetName)) return;
            if (!IsActive) return;
            if (string.Equals(PresetName, presetName, StringComparison.OrdinalIgnoreCase))
            {
                SetPendingHostPreset(presetName);
                return;
            }
            ModLog.Event(LogCat.Dream,
                $"UpdateActivePreset {PresetName} → {presetName} (session {SessionId})");
            PresetName = presetName;
            SetPendingHostPreset(presetName);
            // Keep ResolveActivePresetName on the live pocket (prefers _localDreamPreset).
            DreamSyncManager.NoteLocalDreamPreset(presetName);
        }

        /// <summary>Client adopts host SessionId from DreamStarted / bulk (no local mint).</summary>
        public static void AdoptSessionId(int sessionId)
        {
            if (sessionId == 0) return;
            SessionId = sessionId;
            if (sessionId >= _nextSessionId)
                _nextSessionId = sessionId + 1;
        }

        /// <summary>OutcomeName sentinel used when the host rejects story end.</summary>
        public static bool IsRejectedOutcome(string outcomeName)
            => DreamOutcomePolicy.IsRejectedOutcome(outcomeName);

        /// <summary>
        /// Cleanup reasons that are not a successful story end.
        /// Must not MarkCompleted or grant the default outcome.
        /// </summary>
        public static bool IsFailureCleanup(string reason)
            => DreamOutcomePolicy.IsFailureCleanup(reason);

        /// <summary>
        /// Wire / cleanup outcomes that must not fall through to the preset's
        /// <c>default</c> reward when the named outcome is missing.
        /// </summary>
        public static bool IsNonRewardOutcome(string outcomeName)
            => DreamOutcomePolicy.IsNonRewardOutcome(outcomeName);

        public static string BuildRejectedOutcome(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "rejected";
            return "rejected:" + reason;
        }

        /// <summary>
        /// transferToDream: mark previous pocket completed, stay in session, swap preset (no Idle).
        /// </summary>
        public static void SetChainedPreset(string nextPreset)
        {
            if (string.IsNullOrEmpty(nextPreset)) return;
            if (!IsActive)
            {
                TryBegin(nextPreset);
                SetPendingHostPreset(nextPreset);
                return;
            }
            // Already on this pocket (a second chain call for the same transfer): marking the
            // current preset completed here would block startDreaming ("party already completed").
            if (string.Equals(PresetName, nextPreset, StringComparison.OrdinalIgnoreCase))
                return;
            if (!string.IsNullOrEmpty(PresetName))
                MarkCompleted(PresetName);
            PresetName = nextPreset;
            Current = State.Starting;
            SetPendingHostPreset(nextPreset);
            ArmStartingWatchdog();
            // Same session: dead peers stay dead and spectating. Do not wipe the roster.
            FinalDreamsceneManager.OnDreamChained();
            DreamSyncManager.NoteLocalDreamPreset(nextPreset);
            DreamSyncManager.ClearDreamEndBroadcastLatch();
            DreamSyncManager.ResetHostEndClaim();
            ModLog.Event(LogCat.Dream, $"Chained preset → {nextPreset} (session {SessionId})");
        }

        /// <summary>
        /// A host session whose prepareDream never reaches startDreaming (prepareLocation failed,
        /// coroutine killed) stayed Starting forever: every later start was rejected as "already
        /// active". Bounded wait, then the same failure cleanup a prepare error uses.
        /// </summary>
        private static void ArmStartingWatchdog()
        {
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl == null) return;
            int epoch = ++_startingEpoch;
            ctrl.StartCoroutine(StartingWatchdog(epoch, SessionId));
        }

        private static System.Collections.IEnumerator StartingWatchdog(int epoch, int sessionId)
        {
            float deadline = Time.realtimeSinceStartup + StartingTimeoutSec;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (epoch != _startingEpoch || Current != State.Starting || SessionId != sessionId)
                    yield break;
                yield return null;
            }
            if (epoch != _startingEpoch || Current != State.Starting || SessionId != sessionId)
                yield break;

            var dreams = Dreams.Instance;
            if (dreams != null && dreams.dreaming && dreams.dreamLocation != null)
            {
                // The pad is live; only the Starting→Active flip was missed.
                ModLog.Event(LogCat.Dream,
                    $"Starting watchdog: session {sessionId} is dreaming — marking Active");
                MarkActive();
                yield break;
            }

            ModRuntime.Log?.LogWarning(
                $"[DreamSession] Session {sessionId} preset={PresetName} stuck in Starting for "
                + $"{StartingTimeoutSec:F0}s — cleaning up");
            DreamSyncManager.ForceLocalDreamCleanup("prepare_failed");
            // Force cleanup covers the session; make sure nothing is left latched either way.
            if (Current == State.Starting && SessionId == sessionId)
                AbortStarting("prepare_failed");
        }

        public static void MarkActive()
        {
            if (Current == State.Starting)
            {
                Current = State.Active;
                _startingEpoch++;
            }
        }

        public static void End(string outcomeName = "")
        {
            if (Current == State.Idle) return;
            Current = State.Ending;
            // Reject / disconnect / prepare fail must not party-lock the preset.
            if (DreamOutcomePolicy.ShouldMarkCompletedOnEnd(outcomeName))
                MarkCompleted(PresetName);
            ModLog.Event(LogCat.Dream,
                $"Ending session {SessionId} preset={PresetName} outcome={outcomeName}");
            FinalDreamsceneManager.OnDreamEnded();
            Current = State.Idle;
            PresetName = null;
            _pendingHostPreset = null;
            LevelBits = 0;
        }

        /// <summary>Abort Starting/Active when prepare or pad load failed (no completion mark).</summary>
        public static void AbortStarting(string reason)
        {
            if (Current != State.Starting && Current != State.Active) return;
            ModLog.Event(LogCat.Dream, "Abort session (" + Current + "): " + reason);
            FinalDreamsceneManager.OnDreamEnded();
            Current = State.Idle;
            PresetName = null;
            _pendingHostPreset = null;
            LevelBits = 0;
        }

        public static void Reset()
        {
            Current = State.Idle;
            PresetName = null;
            SessionId = 0;
            _pendingHostPreset = null;
            LevelBits = 0;
            NextLevelBits = 0;
            PendingRequestBits = 0;
        }

        public static void ResetIncludingCompletions()
        {
            Reset();
            _completedPresets.Clear();
            _seededProfile = -1;
        }

        /// <summary>
        /// A party dream is on. The prologue's dreams are never party dreams (each player plays its
        /// own, <see cref="PersonalPrologue"/>), so joining during the host's prologue is allowed.
        /// </summary>
        public static bool ShouldRejectNewConnections => IsActive && !IsEnding;

        /// <summary>
        /// The ending is on (any epilogue part). Joins are let in and pulled onto its pad by the
        /// session bulk: a finished game reloads straight into the ending (vanilla saves with the
        /// dream wanted), so refusing joins there kept friends out of the replayed ending for good.
        /// </summary>
        public static bool IsEnding
        {
            get
            {
                if (IsEpiloguePreset(PresetName))
                    return true;
                Dreams d = Dreams.Instance;
                return d != null && (d.dreaming || d.dreamPrepared)
                    && (IsEpiloguePreset(d.preset != null ? d.preset.name : null)
                        || (d.dreamLocation != null && d.dreamLocation.isEpilogueLocation));
            }
        }

        private static bool IsEpiloguePreset(string name)
            => !string.IsNullOrEmpty(name) && name.StartsWith("epilog", System.StringComparison.OrdinalIgnoreCase);

        // ── Snapshot (level flags + completed) ───────────────────────────

        public static byte ReadLocalLvlFlags()
        {
            var d = Dreams.Instance;
            if (d == null) return 0;
            byte b = 0;
            if (d.hadDreamAtLvl2) b |= LvlFlag2;
            if (d.hadDreamAtLvl3) b |= LvlFlag3;
            if (d.hadDreamAtLvl5) b |= LvlFlag5;
            if (d.hadDreamAtLvl6) b |= LvlFlag6;
            if (d.hadDreamAtLvl7) b |= LvlFlag7;
            return b;
        }

        /// <summary>This player was in a dream for these level slots: mark them as had.</summary>
        public static void ApplyLvlFlags(byte flags)
        {
            if (flags == 0) return;
            var d = Dreams.Instance;
            if (d == null) return;
            if ((flags & LvlFlag2) != 0) d.hadDreamAtLvl2 = true;
            if ((flags & LvlFlag3) != 0) d.hadDreamAtLvl3 = true;
            if ((flags & LvlFlag5) != 0) d.hadDreamAtLvl5 = true;
            if ((flags & LvlFlag6) != 0) d.hadDreamAtLvl6 = true;
            if ((flags & LvlFlag7) != 0) d.hadDreamAtLvl7 = true;
            ModLog.Event(LogCat.Dream, "Level dream slots marked: " + flags);
        }

        /// <summary>Exactly this player's own slots (rejoin restore over the host's world).</summary>
        public static void SetLocalLvlFlags(byte flags)
        {
            var d = Dreams.Instance;
            if (d == null) return;
            d.hadDreamAtLvl2 = (flags & LvlFlag2) != 0;
            d.hadDreamAtLvl3 = (flags & LvlFlag3) != 0;
            d.hadDreamAtLvl5 = (flags & LvlFlag5) != 0;
            d.hadDreamAtLvl6 = (flags & LvlFlag6) != 0;
            d.hadDreamAtLvl7 = (flags & LvlFlag7) != 0;
        }

        /// <summary>Older backups without slots: every dream level already passed counts as had.</summary>
        public static byte LvlFlagsPassedAt(int level)
        {
            byte b = 0;
            if (level >= 2) b |= LvlFlag2;
            if (level >= 3) b |= LvlFlag3;
            if (level >= 5) b |= LvlFlag5;
            if (level >= 6) b |= LvlFlag6;
            if (level >= 7) b |= LvlFlag7;
            return b;
        }

        /// <summary>Merge the host's played-dream set (union, never clear remote-unknown).</summary>
        public static void ApplySnapshot(string[] completed)
        {
            if (completed != null)
            {
                for (int i = 0; i < completed.Length; i++)
                {
                    if (string.IsNullOrEmpty(completed[i])) continue;
                    _completedPresets.Add(completed[i]);
                    MirrorPoolRemove(completed[i]);
                }
            }
            ModLog.Event(LogCat.Dream,
                "Applied session snapshot completed=" + _completedPresets.Count);
        }
    }
}

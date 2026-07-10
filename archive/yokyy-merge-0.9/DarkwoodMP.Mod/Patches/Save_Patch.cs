using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Save-system sync: save-on-authority-beat (v0.7). Darkwood saves at meaningful
/// story beats - sleeping, day transitions, entering/leaving outside locations,
/// and manual/quit saves - all routed through SaveManager.Save (verified: it is
/// synchronous, no-ops on !force at night or while loading, and snapshots the
/// whole world incl. Flags.SaveState.dialogueStates).
///
/// In co-op the two machines otherwise save on their own independent schedules,
/// so a mid-session rejoin could load a world captured at a different beat than
/// the partner's. This patch makes the AUTHORITY (in-game host, or the elected
/// lowest-id client on a dedicated server) broadcast a "savebeat" when it saves;
/// every other machine then flushes its own save at the same beat, keeping the
/// on-disk state convergent. Live state already replicates through the per-system
/// channels - this only pins WHEN each side persists it.
///
/// Loop-safety: the remote save runs under RemoteApply.Active so its own postfix
/// never re-broadcasts; only the authority sends; and a short debounce coalesces
/// the bursts of Save() the game fires around a single transition.
/// </summary>
public sealed class Save_Patch : IPatch
{
    private const double DebounceSeconds = 4.0;
    private static DateTime _lastBeat = DateTime.MinValue;

    /// <summary>
    /// Set by callers (e.g. the world-transfer flush) that Save purely for their
    /// own bookkeeping and must not emit a network save beat.
    /// </summary>
    public static bool SuppressBeat;

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target,
            postfix: new HarmonyMethod(typeof(Save_Patch).GetMethod(nameof(SavePostfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("SaveManager", "Save");
    }

    public static void SavePostfix()
    {
        try
        {
            if (RemoteApply.Active || SuppressBeat) return;

            var manager = NetworkManager.Instance;
            if (manager == null || !manager.IsConnected) return;

            // Every connected machine: push per-player progression backup to host.
            try { ClientStateBackup.EmitToHost(); } catch { }

            if (!manager.IsTimeAuthority) return;

            var now = DateTime.UtcNow;
            if ((now - _lastBeat).TotalSeconds < DebounceSeconds) return;
            _lastBeat = now;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            network.SendReliableCritical(new SaveBeatPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                UtcTicks = now.Ticks
            });
            ModLogger.Msg("[Save_Patch] Ironbark SaveBeat - partners notified to save");

            // Dedicated-server canonical save: push the fresh world to the
            // server (throttled inside; no-op for a P2P in-game host)
            manager.NotifyAuthoritySaved();
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Save_Patch] {ex.Message}");
        }
    }

    /// <summary>
    /// Receiver: flush a local save to match the authority's beat. force=true so
    /// the beat lands even at night; guarded so it never re-broadcasts.
    /// </summary>
    public static void ApplyRemoteBeat()
    {
        try
        {
            var now = DateTime.UtcNow;
            if ((now - _lastBeat).TotalSeconds < DebounceSeconds) return;
            _lastBeat = now;

            var sm = Singleton<SaveManager>.Instance;
            if (sm == null) return;

            RemoteApply.Active = true;
            try
            {
                // doJson, doSaveProfile, force, forceSaveStatic, showSavingIndicator,
                // closeAndOpenStadiaSave, doubleBackupFiles
                sm.Save(true, true, true, false, false, false, false);
                ModLogger.Msg("[Save_Patch] Saved to match authority beat");
            }
            finally
            {
                RemoteApply.Active = false;
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Save_Patch] ApplyRemoteBeat failed: {ex.Message}");
        }
    }
}

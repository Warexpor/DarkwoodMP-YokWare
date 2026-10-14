using System.Collections.Generic;
using System.Reflection;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
// CoopWorldCopyMeta in DWMPHorde.Networking

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Connected clients must not write DynamicSave / Flags (vanilla Save persists
    /// <c>Flags.SaveState</c>). Host-coordinated SaveSync sets
    /// <see cref="LanNetworkManager.RemoteSaveInProgress"/> and is allowed so the
    /// peer checkpoint matches the host. Other client Saves redirect to personal
    /// backup + host SaveSync via <see cref="SaveSyncPatch"/> Postfix.
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), "Save")]
    public static class ClientConnectedWorldSaveBlockPatch
    {
        [HarmonyPriority(Priority.High)]
        private static bool Prefix(bool showSavingIndicator)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return true;
            if (ModRuntime.Network.Role != NetworkRole.Client)
                return true;
            if (!ClientWorldSavePolicy.ShouldBlockConnectedClientWorldSave(
                    connectedClient: true,
                    hostCoordinatedSaveInProgress: LanNetworkManager.RemoteSaveInProgress))
                return true;

            ModLog.Event(LogCat.Save,
                "Client world Save blocked — host owns Flags/DynamicSave; backup + SaveSync only");
            // Vanilla shows the corner "Saving" indicator at the end of every save that asks for
            // it (SaveManager.finishSaving). Blocked here, the client never saw it where the host
            // did: the dream entry, a level-up, a sleep.
            if (showSavingIndicator)
            {
                try
                {
                    UI ui = Singleton<UI>.Instance;
                    if (ui != null)
                        ui.showSavingIndicator();
                }
                catch { /* UI not up */ }
            }
            return false;
        }
    }

    /// <summary>
    /// How long a Save holds the frame (vanilla's write plus every co-op step around it). Saves
    /// were the regular ~320 ms hitch in playtests; this tells the next log what is left of it.
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), "Save")]
    public static class SaveDurationPatch
    {
        private static readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch(); // process-scoped: timer

        [HarmonyPriority(Priority.First + 1)]
        private static void Prefix()
        {
            _gcBefore = System.GC.CollectionCount(0);
            SaveStageTiming.Begin();
            _sw.Restart();
        }

        private static int _gcBefore; // process-scoped: timing scratch

        [HarmonyPriority(Priority.Last - 1)]
        private static void Postfix(bool __runOriginal)
        {
            _sw.Stop();
            if (__runOriginal && _sw.ElapsedMilliseconds >= 50 && ModRuntime.Network != null && ModRuntime.Network.IsConnected)
                ModLog.Event(LogCat.Save, "[SaveTiming] Save held the frame " + _sw.ElapsedMilliseconds + " ms, collections="
                    + (System.GC.CollectionCount(0) - _gcBefore) + " | " + SaveStageTiming.Describe(_sw.Elapsed.TotalMilliseconds));
            SaveStageTiming.End();
        }
    }

    /// <summary>
    /// Where a Save's frame goes, for the <c>[SaveTiming]</c> line: vanilla's own body (and in it
    /// the profile file, the file moves, the id pass, reading each saved object off its
    /// GameObject, writing the objects, the static file, the closing step) against the co-op
    /// steps before and after it.
    /// </summary>
    internal static class SaveStageTiming
    {
        internal enum Stage { Vanilla, Profiles, Backup, Ids, Capture, WriteObjs, Static, Finish, Count }

        private static readonly double[] _ms = new double[(int)Stage.Count]; // reset-in: Begin
        private static readonly long[] _start = new long[(int)Stage.Count]; // reset-in: Begin
        private static readonly int[] _depth = new int[(int)Stage.Count]; // reset-in: Begin
        private static bool _on; // process-scoped: set and cleared around each Save

        internal static void Begin()
        {
            System.Array.Clear(_ms, 0, _ms.Length);
            System.Array.Clear(_depth, 0, _depth.Length);
            _on = true;
        }

        internal static void End()
        {
            _on = false;
        }

        internal static void Enter(Stage st)
        {
            if (!_on || _depth[(int)st]++ > 0)
                return;
            _start[(int)st] = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        internal static void Exit(Stage st)
        {
            if (!_on || _depth[(int)st] == 0 || --_depth[(int)st] > 0)
                return;
            _ms[(int)st] += (System.Diagnostics.Stopwatch.GetTimestamp() - _start[(int)st]) * 1000.0
                / System.Diagnostics.Stopwatch.Frequency;
        }

        internal static string Describe(double totalMs)
        {
            double v = _ms[(int)Stage.Vanilla];
            double parts = _ms[(int)Stage.Profiles] + _ms[(int)Stage.Backup] + _ms[(int)Stage.Ids] + _ms[(int)Stage.Capture]
                + _ms[(int)Stage.WriteObjs] + _ms[(int)Stage.Static] + _ms[(int)Stage.Finish];
            return "vanilla=" + v.ToString("F0") + " (profiles=" + _ms[(int)Stage.Profiles].ToString("F0")
                + " files=" + _ms[(int)Stage.Backup].ToString("F0") + " ids=" + _ms[(int)Stage.Ids].ToString("F0")
                + " objRead=" + _ms[(int)Stage.Capture].ToString("F0") + " objWrite=" + _ms[(int)Stage.WriteObjs].ToString("F0")
                + " static=" + _ms[(int)Stage.Static].ToString("F0") + " finish=" + _ms[(int)Stage.Finish].ToString("F0")
                + " rest=" + System.Math.Max(0, v - parts).ToString("F0") + ") coop=" + System.Math.Max(0, totalMs - v).ToString("F0");
        }
    }

    [HarmonyPatch(typeof(SaveManager), "Save")]
    internal static class SaveStageVanillaPatch
    {
        // Right around the original: every other prefix runs before, every other postfix after.
        [HarmonyPriority(Priority.Last)] private static void Prefix() => SaveStageTiming.Enter(SaveStageTiming.Stage.Vanilla);
        [HarmonyPriority(Priority.First)] private static void Postfix() => SaveStageTiming.Exit(SaveStageTiming.Stage.Vanilla);
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.saveGameProfiles))]
    internal static class SaveStageProfilesPatch
    {
        private static void Prefix() => SaveStageTiming.Enter(SaveStageTiming.Stage.Profiles);
        private static void Postfix() => SaveStageTiming.Exit(SaveStageTiming.Stage.Profiles);
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.backupFile))]
    internal static class SaveStageBackupPatch
    {
        private static void Prefix() => SaveStageTiming.Enter(SaveStageTiming.Stage.Backup);
        private static void Postfix() => SaveStageTiming.Exit(SaveStageTiming.Stage.Backup);
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.assignUniqueIds), typeof(List<UnityEngine.Transform>))]
    internal static class SaveStageIdsPatch
    {
        private static void Prefix() => SaveStageTiming.Enter(SaveStageTiming.Stage.Ids);
        private static void Postfix() => SaveStageTiming.Exit(SaveStageTiming.Stage.Ids);
    }

    [HarmonyPatch(typeof(SaveManager.SavedObj), nameof(SaveManager.SavedObj.StoreObject))]
    internal static class SaveStageCapturePatch
    {
        private static void Prefix() => SaveStageTiming.Enter(SaveStageTiming.Stage.Capture);
        private static void Postfix() => SaveStageTiming.Exit(SaveStageTiming.Stage.Capture);
    }

    [HarmonyPatch(typeof(SaveManager), "saveObjs")]
    internal static class SaveStageWriteObjsPatch
    {
        private static void Prefix() => SaveStageTiming.Enter(SaveStageTiming.Stage.WriteObjs);
        private static void Postfix() => SaveStageTiming.Exit(SaveStageTiming.Stage.WriteObjs);
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.saveStatic))]
    internal static class SaveStageStaticPatch
    {
        private static void Prefix() => SaveStageTiming.Enter(SaveStageTiming.Stage.Static);
        private static void Postfix() => SaveStageTiming.Exit(SaveStageTiming.Stage.Static);
    }

    [HarmonyPatch(typeof(SaveManager), "finishSaving")]
    internal static class SaveStageFinishPatch
    {
        private static void Prefix() => SaveStageTiming.Enter(SaveStageTiming.Stage.Finish);
        private static void Postfix() => SaveStageTiming.Exit(SaveStageTiming.Stage.Finish);
    }

    /// <summary>
    /// Vanilla ends every Save with <c>GC.Collect()</c> (<c>finishSaving</c>): a full collection
    /// with every script thread stopped until it is done. Alone that is part of the save's frozen
    /// frame and no more, the sound card's mixing thread is not a script thread and plays on. With
    /// voice chat it is one (<see cref="Audio.VoiceChatService.MixerRunsScripts"/>), so the whole
    /// game's sound stopped with the frame: a gap in the music and every sound at each save, heard
    /// most at daybreak, where a save always runs under the morning's sounds. The collection is
    /// left to the runtime then (it collects in slices of a few ms, <c>gc-max-time-slice</c>), and
    /// what the save allocated goes over the next frames.
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), "finishSaving")]
    public static class SaveNoFullCollectPatch
    {
        private static void Collect()
        {
            if (!Audio.VoiceChatService.MixerRunsScripts)
                System.GC.Collect();
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo collect = AccessTools.Method(typeof(System.GC), nameof(System.GC.Collect), System.Type.EmptyTypes);
            MethodInfo ours = AccessTools.Method(typeof(SaveNoFullCollectPatch), nameof(Collect));
            var list = new List<CodeInstruction>(instructions);
            int patched = 0;
            if (collect != null && ours != null)
            {
                foreach (var ins in list)
                {
                    if (ins.Calls(collect))
                    {
                        ins.operand = ours;
                        patched++;
                    }
                }
            }
            if (patched == 0)
                ModLog.Error(LogCat.Save, "[SaveTiming] SaveManager.finishSaving collection hook not applied");
            return list;
        }
    }

    /// <summary>
    /// A Save a co-op prefix skipped (connected client, night-dead peer) still answers
    /// <c>onSaved</c>, as vanilla's own <c>dontSave</c> skip does. Its one listener is the walk
    /// out of a pad by day (<c>OutsideLocations.returningOnSaved</c>): unanswered, its
    /// <c>returningToWorld</c> stayed set, so every later <c>returnToWorld</c> was ignored (the
    /// player stayed in the cellar) and inputs stayed held, until a host SaveSync happened to
    /// run a real Save. A night-dead host never sends one, so it and its clients were stuck.
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), "Save")]
    public static class SkippedSaveAnswersOnSavedPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(SaveManager __instance, bool __runOriginal)
        {
            if (__runOriginal || __instance == null || __instance.onSaved == null)
                return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            __instance.onSaved();
        }
    }

    /// <summary>
    /// Host: a world save that really writes (vanilla skips one at night without force, while
    /// loading, or with dontSave) holds everything removed so far; the list replayed to joiners
    /// starts over.
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), "Save")]
    public static class RemovedSinceSaveResetPatch
    {
        private static void Prefix(SaveManager __instance, bool force, out bool __state)
        {
            __state = __instance != null && !__instance.dontSave
                && !((!Core.isDay() && !force) || Core.loadingGame);
        }

        private static void Postfix(bool __state, bool __runOriginal)
        {
            if (!__state || !__runOriginal)
                return;
            if (ModRuntime.Network != null && ModRuntime.Network.Role == NetworkRole.Host)
                WorldPhysicsSyncService.HostWorldSaved();
        }
    }

    /// <summary>
    /// Co-op coordinated save: local <see cref="SaveManager.Save"/> notifies the host;
    /// host rate-limits then broadcasts SaveSync so clients run full Save, with the Saving
    /// indicator only when this Save asked for it (vanilla's new day Save does not).
    /// <see cref="LanNetworkManager.RemoteSaveInProgress"/> prevents rebroadcast loops.
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), "Save")]
    public static class SaveSyncPatch
    {
        private static void Postfix(bool showSavingIndicator, bool __runOriginal)
        {
            if (LanNetworkManager.RemoteSaveInProgress)
                return;
            // Host applying client dialog must never fan out Saving UI (see DialogHostSilentClose).
            if (HostApplyGuard.Active)
                return;
            if (ModRuntime.Network == null)
                return;
            if (!ModRuntime.Network.IsConnected)
            {
                // A host saving alone: its character goes with this save all the same (the world a
                // later joiner downloads, and whoever hosts that copy next, pair it with this bag).
                if (__runOriginal && ModRuntime.Network.Role == NetworkRole.Host && !NightDeathSavePatch.IsHeld()
                    && !IsDreamEntrySaveWindow() && !DreamSyncManager.IsLocalMidDream)
                    ModRuntime.Network.SaveHandlers.HostKeepOwnCharacter();
                return;
            }
            if (ModRuntime.Network.Role == NetworkRole.Offline)
                return;

            // NightDeathSavePatch skipped the real Save (first night death / party wipe), but a
            // Harmony Prefix returning false does not stop this Postfix: no backup, no SaveSync
            // fan-out, or peers would persist the death the host deliberately held back.
            if (NightDeathSavePatch.IsHeld())
                return;

            // Vanilla prepareDream force-Saves mid entry. Connected clients block the
            // disk write (ClientConnectedWorldSaveBlockPatch); still skip SaveSync fanout
            // so peers are not hitch-saved during the transition video/load.
            if (IsDreamEntrySaveWindow())
            {
                ModLog.Event(LogCat.Save,
                    "SaveSync suppressed (dream entry window) — no peer fanout");
                return;
            }

            // A client's own Save was blocked: it is not a world save, so its character is not
            // stored here. The host's SaveSync (asked for below) brings the real one.

            // Permanent local co-op copy: re-fingerprint sav files after every local Save.
            // Host-coordinated client SaveSync still writes; blocked client Saves skip this
            // (files unchanged) — Refresh is cheap/no-op when fingerprints match.
            try { CoopWorldCopyMeta.RefreshAfterLocalSave(); }
            catch { /* non-fatal */ }

            ModLog.Event(LogCat.Save,
                "Local Save path (" + ModRuntime.Network.Role
                + ") → SaveSync request/broadcast (host debounced fan-out)");
            ModRuntime.Network.SendSaveSync(hostAlreadySavedLocally: true, showIndicator: showSavingIndicator);

            // After the first post-death Save fans out, suppress the peer Save storm.
            if (DeathStateTracker.ConsumeDeathSaveSuppressArm()
                && ModRuntime.Network is LanNetworkManager lnm)
                lnm.NoteDeathSaveSyncWindow();
        }

        /// <summary>
        /// prepareDream / entry video / mid-dream — not endDreaming (dreaming already false).
        /// Do not key off IsDreamActive alone: session may still be true when end Save runs.
        /// </summary>
        private static bool IsDreamEntrySaveWindow()
        {
            if (Core.EnteringDream)
                return true;
            Dreams d = Dreams.Instance;
            if (d == null)
                return false;
            return d.dreaming || d.dreamPrepared || d.wantToDream || d.switchingDream;
        }
    }
}

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
        private static bool Prefix()
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
            return false;
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
    /// The host's world was written to disk: the clients' live snapshots (taken between saves,
    /// held in memory) are their state in that saved world and are stored with it.
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), "Save")]
    public static class HostSaveStoresLiveClientBackupsPatch
    {
        private static void Prefix(SaveManager __instance, out System.DateTime __state)
        {
            __state = __instance.lastTimeSaved;
        }

        private static void Postfix(SaveManager __instance, System.DateTime __state, bool __runOriginal)
        {
            // Vanilla leaves Save early (dontSave, an unforced night Save, a load in progress)
            // without touching lastTimeSaved; a co-op prefix may skip it altogether.
            if (!__runOriginal || __instance == null || __instance.lastTimeSaved == __state)
                return;
            if (ModRuntime.Network is LanNetworkManager net && net.SaveHandlers != null)
                net.SaveHandlers.StoreLiveClientBackupsAtWorldSave();
        }
    }

    /// <summary>
    /// Co-op coordinated save: local <see cref="SaveManager.Save"/> notifies the host;
    /// host rate-limits then broadcasts SaveSync so clients run full Save with Saving UI.
    /// <see cref="LanNetworkManager.RemoteSaveInProgress"/> prevents rebroadcast loops.
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), "Save")]
    public static class SaveSyncPatch
    {
        private static void Postfix()
        {
            if (LanNetworkManager.RemoteSaveInProgress)
                return;
            // Host applying client dialog must never fan out Saving UI (see DialogHostSilentClose).
            if (HostApplyGuard.Active)
                return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
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
                // No world save follows this one: the client's state goes to the host as a live
                // snapshot, written to disk at the host's next world save.
                if (ModRuntime.Network.Role == NetworkRole.Client)
                {
                    try { ModRuntime.Network.SendClientStateBackup(atWorldSave: false); }
                    catch { /* non-fatal */ }
                }
                return;
            }

            // The client's own Save was not a world save (the host's is, and its SaveSync brings
            // the save-time snapshot): a live snapshot, in case the host does not save now.
            if (ModRuntime.Network.Role == NetworkRole.Client)
            {
                try { ModRuntime.Network.SendClientStateBackup(atWorldSave: false); }
                catch { /* non-fatal */ }
            }

            // Permanent local co-op copy: re-fingerprint sav files after every local Save.
            // Host-coordinated client SaveSync still writes; blocked client Saves skip this
            // (files unchanged) — Refresh is cheap/no-op when fingerprints match.
            try { CoopWorldCopyMeta.RefreshAfterLocalSave(); }
            catch { /* non-fatal */ }

            ModLog.Event(LogCat.Save,
                "Local Save path (" + ModRuntime.Network.Role
                + ") → SaveSync request/broadcast (host debounced fan-out)");
            ModRuntime.Network.SendSaveSync(hostAlreadySavedLocally: true);

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

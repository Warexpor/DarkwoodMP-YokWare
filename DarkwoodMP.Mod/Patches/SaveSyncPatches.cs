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
    /// <see cref="LanNetworkManager._isRemoteSaveInProgress"/> and is allowed so the
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
                    hostCoordinatedSaveInProgress: LanNetworkManager._isRemoteSaveInProgress))
                return true;

            ModLog.Event(LogCat.Save,
                "Client world Save blocked — host owns Flags/DynamicSave; backup + SaveSync only");
            return false;
        }
    }

    /// <summary>
    /// Co-op coordinated save: local <see cref="SaveManager.Save"/> notifies the host;
    /// host rate-limits then broadcasts SaveSync so clients run full Save with Saving UI.
    /// <see cref="LanNetworkManager._isRemoteSaveInProgress"/> prevents rebroadcast loops.
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), "Save")]
    public static class SaveSyncPatch
    {
        private static void Postfix()
        {
            if (LanNetworkManager._isRemoteSaveInProgress)
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
                // Client still needs personal backup when the disk Save was blocked.
                if (ModRuntime.Network.Role == NetworkRole.Client)
                {
                    try { ModRuntime.Network.SendClientStateBackup(); }
                    catch { /* non-fatal */ }
                }
                return;
            }

            // Clients also push personal inventory backup to host (multi-client keyed files).
            if (ModRuntime.Network.Role == NetworkRole.Client)
            {
                try { ModRuntime.Network.SendClientStateBackup(); }
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

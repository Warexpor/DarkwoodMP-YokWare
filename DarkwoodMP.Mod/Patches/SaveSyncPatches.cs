using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;
// CoopWorldCopyMeta in DWMPHorde.Networking

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Co-op coordinated save: whoever finishes a local <see cref="SaveManager.Save"/>
    /// notifies peers. Peers run their own full Save with the vanilla Saving indicator.
    /// <see cref="LanNetworkManager._isRemoteSaveInProgress"/> prevents rebroadcast loops.
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), "Save")]
    public static class SaveSyncPatch
    {
        private static void Postfix()
        {
            if (LanNetworkManager._isRemoteSaveInProgress)
                return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (ModRuntime.Network.Role == NetworkRole.Offline)
                return;

            // Clients also push personal inventory backup to host (multi-client keyed files).
            if (ModRuntime.Network.Role == NetworkRole.Client)
            {
                try { ModRuntime.Network.SendClientStateBackup(); }
                catch { /* non-fatal */ }
            }

            // Permanent local co-op copy: re-fingerprint sav files after every local Save.
            try { CoopWorldCopyMeta.RefreshAfterLocalSave(); }
            catch { /* non-fatal */ }

            ModLog.Event(LogCat.Save,
                "Local Save complete (" + ModRuntime.Network.Role
                + ") → SaveSync so all peers Save with Saving UI");
            ModRuntime.Network.SendSaveSync();
        }
    }
}

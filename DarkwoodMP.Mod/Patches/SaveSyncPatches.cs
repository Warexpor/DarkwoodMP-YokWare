using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Patches <see cref="SaveManager.Save"/> to trigger a save on the remote peer
    /// so that both sides persist their state simultaneously.
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

            // Client sends inventory/skills/state backup to host on local save
            if (ModRuntime.Network.Role == NetworkRole.Client)
                ModRuntime.Network.SendClientStateBackup();

            ModRuntime.Network.SendSaveSync();
        }
    }
}

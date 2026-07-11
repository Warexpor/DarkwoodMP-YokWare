using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host Save → notify peers. Clients must NOT full-world Save (see ClientCoopSaveBlockPatch);
    /// they only push ClientStateBackup. Old "both sides Save" design corrupted client slot 5.
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

            // Client world Save is blocked; if we ever reach here Role is Host (or offline).
            if (ModRuntime.Network.Role == NetworkRole.Client)
            {
                // Safety: should not run after ClientCoopSaveBlockPatch — keep backup only.
                ModRuntime.Network.SendClientStateBackup();
                return;
            }

            if (ModRuntime.Network.Role == NetworkRole.Host)
            {
                ModLog.Event(LogCat.Save, "Host Save → SaveSync notify peers (clients will NOT rewrite world files)");
                ModRuntime.Network.SendSaveSync();
            }
        }
    }
}

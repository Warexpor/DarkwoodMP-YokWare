using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Co-op allows world Save on every machine (coordinated multi-save + permanent copies).
    /// This patch is a no-op gate kept for load order / logging of client Saves.
    /// Historical note: blocking client Save avoided half-sim corruption of slot 5; product
    /// now wants every player to Save on their end when anyone initiates (see SaveSync).
    /// </summary>
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(SaveManager), "Save")]
    public static class ClientCoopSaveBlockPatch
    {
        private static bool Prefix(
            bool doJson, bool doSaveProfile, bool force, bool forceSaveStatic,
            bool showSavingIndicator, bool closeAndOpenStadiaSave, bool doubleBackupFiles)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected)
                return true;
            if (net.Role != NetworkRole.Client)
                return true;

            int profId = Core.currentProfile != null ? Core.currentProfile.id : -1;
            string via = LanNetworkManager._isRemoteSaveInProgress
                ? "SaveSync peer request"
                : "local initiate (will fan-out)";
            ModLog.Event(LogCat.Save,
                "Client world Save allowed (" + via + ") force=" + force
                + " showIndicator=" + showSavingIndicator
                + " profileId=" + profId);

            return true;
        }
    }
}

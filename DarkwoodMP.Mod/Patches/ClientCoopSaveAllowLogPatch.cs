using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Dead gate: always returns true (never blocks Save). Kept for Harmony load order and
    /// client Save logging only — product allows coordinated multi-save via SaveSync.
    /// Historical name "Block" is misleading; class was renamed but Harmony still patches
    /// <see cref="SaveManager.Save"/> the same way.
    /// </summary>
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(SaveManager), "Save")]
    public static class ClientCoopSaveAllowLogPatch
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

            return true; // no-op: allow vanilla Save (night-death still gated by NightDeathSavePatch)
        }
    }
}

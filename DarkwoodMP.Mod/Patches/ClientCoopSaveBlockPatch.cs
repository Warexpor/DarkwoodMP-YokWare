using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Co-op client must never write sav.dat / savs.dat while connected.
    /// Host owns the world; client's sim is half-synced (AI mute, phantoms, sticky bulk).
    /// Old path: host Save → SaveSync → client Save(force) overwrote slot 5 with garbage
    /// ("everything becomes corrupted" on the client profile).
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

            // Remote save flag is only set while HandleSaveSync runs — still block.
            string reason = LanNetworkManager._isRemoteSaveInProgress
                ? "SaveSync from host"
                : "local Save while co-op client";

            int profId = Core.currentProfile != null ? Core.currentProfile.id : -1;
            ModLog.Event(LogCat.Save,
                "BLOCKED client world Save (" + reason + ") force=" + force
                + " forceStatic=" + forceSaveStatic
                + " doJson=" + doJson
                + " doProfile=" + doSaveProfile
                + " profileId=" + profId
                + " — host owns sav.dat/savs.dat; personal state uses ClientStateBackup only");

            // Personal inventory/skills still go to host (and local backup file).
            try { net.SendClientStateBackup(); }
            catch (System.Exception ex)
            {
                ModLog.Warn(LogCat.Save, "ClientStateBackup after blocked Save failed: " + ex.Message);
            }

            return false;
        }
    }
}

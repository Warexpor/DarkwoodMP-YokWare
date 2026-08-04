using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host: after brand-new worldgen, push save share to connected clients.
    /// Client: block finishing a brand-new gen while connected (divergent forests);
    /// load / chapter-load paths still run.
    /// </summary>
    [HarmonyPatch(typeof(WorldGenerator), "onFinished")]
    public static class WorldGenSharePatch
    {
        private static bool Prefix()
        {
            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Client && net.IsConnected)
            {
                if (Core.loadingGame || Core.loadedGame || Core.doLoadChapterSave)
                    return true;

                if (net.WorldSaveShare != null
                    && WorldSharePolicy.IsShareFailureMessage(net.WorldSaveShare.ProgressText))
                {
                    ModLog.Warn(LogCat.World,
                        "Client blocked worldgen — terminal world share failure: "
                        + net.WorldSaveShare.ProgressText);
                    return false;
                }

                ModLog.Warn(LogCat.World,
                    "Client blocked new worldgen finish while connected — use host share / join pipeline "
                    + "(prevents divergent landmark forests)");
                return false;
            }
            return true;
        }

        private static void Postfix()
        {
            // Load path / chapter reload: world came from disk — not a fresh generation.
            if (Core.loadingGame || Core.loadedGame || Core.doLoadChapterSave)
                return;

            // Brand-new forest → new campaign id even when offline / not hosting yet.
            // Old bug: mint only ran as Host, so "new world then host MP" reused the
            // previous CampaignId and pushed stale ClientBackup inv/skills onto day-1.
            if (Core.currentProfile != null)
                CoopWorldCopyMeta.MintNewCampaignId(Core.currentProfile.id);

            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Host)
                return;

            if (!net.IsConnected || !net.IsHandshakeComplete)
            {
                ModLog.Event(LogCat.Save,
                    "New world ready but no connected clients — skip world share");
                return;
            }

            net.WorldSaveShare?.ScheduleHostShareAfterNewWorld();
        }
    }
}

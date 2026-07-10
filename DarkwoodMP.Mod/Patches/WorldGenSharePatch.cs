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

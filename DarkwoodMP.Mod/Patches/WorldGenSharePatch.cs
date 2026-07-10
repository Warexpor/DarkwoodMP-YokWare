using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// After the host finishes generating a brand-new world (not a load), push save files
    /// to every already-connected client so they share that world without manual file copy.
    /// </summary>
    [HarmonyPatch(typeof(WorldGenerator), "onFinished")]
    public static class WorldGenSharePatch
    {
        private static void Postfix()
        {
            // Load path / chapter reload: world came from disk — not a fresh generation.
            // Matches WorldGenerator's own "save after new gen" conditions.
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

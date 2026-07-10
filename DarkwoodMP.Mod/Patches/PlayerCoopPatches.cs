using DWMPHorde.Players;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Intercepts Player.registerMe so that coop clones and remote players
    /// are registered as secondary (P2) instead of replacing the main player.
    /// </summary>
    [HarmonyPatch(typeof(Player), "registerMe")]
    public static class PlayerRegisterMePatch
    {
        private static bool Prefix(Player __instance)
        {
            // Suppress vanilla registration for spawn-phase clones and already-marked proxies
            if (PlayerProxyBuilder.IsSpawningCoopClone
                || __instance.GetComponent<CoopPlayerMarker>() != null)
            {
                PlayerControlRouter.RegisterSecond(__instance);
                return false;
            }

            // If there is already a main player, register subsequent players as secondary
            if (PlayerControlRouter.MainPlayer != null && __instance != PlayerControlRouter.MainPlayer)
            {
                PlayerControlRouter.RegisterSecond(__instance);
                return false;
            }

            PlayerControlRouter.RegisterMain(__instance);
            return true;
        }
    }

    /// <summary>
    /// Skips full Player.Start for coop clones and remote proxies,
    /// running a lightweight bootstrap instead to avoid re-initializing
    /// systems already set up by the main player.
    /// </summary>
    [HarmonyPatch(typeof(Player), "Start")]
    public static class PlayerStartPatch
    {
        private static bool Prefix(Player __instance)
        {
            if (!PlayerProxyBuilder.IsSpawningCoopClone
                && __instance.GetComponent<CoopPlayerMarker>() == null)
                return true;

            CoopPlayerBootstrap.RunLightweightStart(__instance, PlayerControlRouter.MainPlayer);
            return false;
        }
    }
}

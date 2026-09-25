using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Client characters are a view of the host. Vanilla die() fires death
    /// triggers, night-spawner bookkeeping, and can end the night on a trader
    /// kill. The client only plays the death.
    /// </summary>
    [HarmonyPatch(typeof(Character), "die")]
    public static class CharacterDeathCorpsePatch
    {
        private static bool Prefix(Character __instance)
        {
            if (__instance == null) return true;

            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return true;
            if (Player.Instance != null && __instance.gameObject == Player.Instance.gameObject)
                return true;

            if (!__instance.alive)
                return false;

            short id = 0;
            CharacterTracker.TryGetStableId(__instance, out id);
            if (__instance.hasPreDeath)
            {
                ClientEntityInterpolationService.PresentHostDowned(__instance, id, null);
                return false;
            }

            ClientEntityInterpolationService.PresentHostDeath(__instance, id, null, -1);
            return false;
        }
    }
}

using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// When network owns flare lifetime (thrown / host-tracked), skip vanilla waitToDie
    /// so peers keep flicker without a second full-longevity die clock.
    /// </summary>
    [HarmonyPatch(typeof(Flare), "waitToDie")]
    public static class FlareWaitToDieNetworkPatch
    {
        private static bool Prefix(Flare __instance)
        {
            if (__instance == null) return true;
            var auth = __instance.GetComponent<NetworkFlareLifetime>()
                ?? __instance.GetComponentInParent<NetworkFlareLifetime>();
            if (auth != null && auth.NetworkOwnsDie)
                return false; // skip vanilla longevity die — host track fades
            return true;
        }
    }
}

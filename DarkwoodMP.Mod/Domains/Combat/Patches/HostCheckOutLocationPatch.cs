using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A creature that comes to check out a location heads first for the waypoint closest to
    /// the player it hunts when that player is indoors (vanilla Character.checkOutLocation reads
    /// Player.Instance: the host only). For a player's stand-in it took a random waypoint of the
    /// location and wandered the house instead of coming for the player. Host only.
    /// </summary>
    [HarmonyPatch(typeof(Character), nameof(Character.checkOutLocation))]
    public static class HostCheckOutLocationPatch
    {
        private static void Postfix(Character __instance)
        {
            if (__instance == null || !__instance.checkingOutLocation || __instance.superTarget == null)
                return;
            if (!HostPlayerIdentity.HostWithRemotes())
                return;
            RemotePlayerProxy proxy = __instance.superTarget.GetComponent<RemotePlayerProxy>();
            CharBase body = proxy != null ? proxy.CachedCharBase : null;
            Location loc = __instance.locationCheckingOut;
            if (body == null || !body.isInside || loc == null || loc.waypointsInside == null)
                return;
            Vector3 at = proxy.transform.position;
            Waypoint best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < loc.waypointsInside.Count; i++)
            {
                Waypoint w = loc.waypointsInside[i];
                if (w == null)
                    continue;
                float d = (w.transform.position - at).sqrMagnitude;
                if (d < bestD)
                {
                    bestD = d;
                    best = w;
                }
            }
            if (best != null)
                __instance.gotoNextWaypoint(best);
        }
    }
}

using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Location events pick their spot "closest to the player" (vanilla
    /// <c>Location.GetObject.getClosestToPlayer</c>: where a night event lands in the hideout, which
    /// window, which furniture). On the host that read the host's position even when the host was
    /// out and only a peer was in that location (events run at a peer's hideout then). The player
    /// is the one in that location: the host when it is there (vanilla), else a peer standing in it.
    /// </summary>
    internal static class LocationPlayerBody
    {
        internal static bool TryPeerIn(Location loc, out Vector3 pos)
        {
            pos = default;
            if (loc == null || !NetGuard.ConnectedHost(out LanNetworkManager net))
                return false;
            Player host = Player.Instance;
            if (host == null)
                return false;
            if (Location.getAtPos(host.transform.position) == loc)
                return false;
            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy == null)
                    continue;
                CharBase cb = proxy.CachedCharBase;
                if (cb != null && !cb.alive)
                    continue;
                if (Location.getAtPos(proxy.transform.position) == loc)
                {
                    pos = proxy.transform.position;
                    return true;
                }
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(Location.GetObject), nameof(Location.GetObject.returnObjects))]
    public static class LocationReturnObjectsClosestPatch
    {
        private static bool Prefix(Location.GetObject __instance, List<GameObject> returnToList, Location _location)
        {
            if (__instance == null || !__instance.getClosestToPlayer || __instance.getAll || __instance.getClosest)
                return true;
            if (!LocationPlayerBody.TryPeerIn(_location, out Vector3 peer))
                return true;
            List<GameObject> objects = _location.getObjects(__instance);
            if (objects == null || objects.Count == 0)
                return true; // vanilla logs the miss
            GameObject go = Core.getClosestFrom(objects, peer);
            if (go != null)
                returnToList.Add(go);
            return false;
        }
    }

    [HarmonyPatch(typeof(Location.GetObject), nameof(Location.GetObject.returnObject))]
    public static class LocationReturnObjectClosestPatch
    {
        private static bool Prefix(Location.GetObject __instance, Location _location, ref GameObject __result)
        {
            if (__instance == null || !__instance.getClosestToPlayer || __instance.getClosest)
                return true;
            if (!LocationPlayerBody.TryPeerIn(_location, out Vector3 peer))
                return true;
            List<GameObject> objects = _location.getObjects(__instance);
            if (objects == null || objects.Count == 0)
                return true;
            __result = Core.getClosestFrom(objects, peer);
            return false;
        }
    }
}

using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// What a night scene's creature goes for (the door, window or barricade of the hideout).
    /// Vanilla picks it from the player's own location: a spawned creature's
    /// <c>Activity.assignTarget</c> reads <c>Player.Instance.whereAmI.bigLocation</c>, and
    /// "closest to the player" measures from <c>Player.Instance</c>. The host plays a scene in every
    /// hideout a living player stands in (<see cref="NightEventAnchor"/>): a creature of a peer's
    /// hideout took its target from the host's hideout across the map, or, with the host out in the
    /// forest, got none ("Player location not found for spawned character", "No activity target")
    /// and stood idle outside the peer's door. It now goes by the player the scene plays for: the
    /// acting player while the scene's step runs, else the living player nearest the creature
    /// standing in a world location (the hideout it was spawned at).
    /// </summary>
    internal static class NightSceneTargets
    {
        /// <summary>A peer's body the running step belongs to, or null (the host's own, or nobody).</summary>
        internal static Transform RemoteActor()
        {
            if (GeFireActorContext.Depth == 0)
                return null;
            Transform body = GeFireActorContext.ActorBody();
            return body != null && !PlayerTargetArbiter.IsHostBody(body) ? body : null;
        }

        /// <summary>The living player nearest <paramref name="from"/> who stands in a world location.</summary>
        internal static Transform NearestHousedPlayer(Vector3 from, out Location home)
        {
            home = null;
            Transform best = null;
            float bestSq = float.MaxValue;
            Player host = Player.Instance;
            if (host != null && host.alive && host.whereAmI != null && !NightEventAnchor.HostInOutsideLocation())
            {
                Location big = host.whereAmI.bigLocation;
                if (big != null && !big.isOutsideLocation)
                {
                    best = host._transform;
                    bestSq = (host._transform.position - from).sqrMagnitude;
                    home = big;
                }
            }
            var net = ModRuntime.Network;
            if (net == null)
                return best;
            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy == null)
                    continue;
                CharBase cb = proxy.CachedCharBase;
                if (cb != null && !cb.alive)
                    continue;
                Location big = BigAt(proxy.transform.position);
                if (big == null || big.isOutsideLocation)
                    continue;
                float d = (proxy.transform.position - from).sqrMagnitude;
                if (d < bestSq)
                {
                    bestSq = d;
                    best = proxy.transform;
                    home = big;
                }
            }
            return best;
        }

        internal static Location BigAt(Vector3 pos)
        {
            Location at = Location.getAtPos(pos);
            return at != null && at.bigLocation != null ? at.bigLocation : at;
        }

        /// <summary>Vanilla <c>Location.GetObject.returnObject</c> with "closest to the player" measured from <paramref name="player"/>.</summary>
        internal static GameObject Pick(Location.GetObject get, Location loc, GameObject parent, Vector3 player)
        {
            List<GameObject> objects = loc.getObjects(get);
            if (objects == null || objects.Count == 0)
            {
                Debug.LogError("Invalid (or 0 returned) gameObjects for " + parent.name + ". Type: " + get.type, parent);
                return null;
            }
            if (get.getClosest)
                return Core.getClosestFrom(objects, parent.transform.position);
            if (get.getClosestToPlayer)
                return Core.getClosestFrom(objects, player);
            return objects[Random.Range(0, objects.Count)];
        }
    }

    /// <summary>A spawned creature's location target comes from the hideout of the player its scene plays for.</summary>
    [HarmonyPatch(typeof(Character.Activity), "assignTarget")]
    public static class NightSceneSpawnedTargetPatch
    {
        [HarmonyPriority(Priority.High)]
        private static bool Prefix(Character.Activity __instance)
        {
            if (__instance == null || !__instance.getFromLocation || __instance.locationGetObject == null)
                return true;
            Character ch = __instance.thisCharacter;
            if (ch == null || !ch.temporarySpawned || !HostPlayerIdentity.HostWithRemotes())
                return true;
            Transform body = NightSceneTargets.RemoteActor();
            Location home = body != null ? NightSceneTargets.BigAt(body.position) : null;
            if (home == null)
                body = NightSceneTargets.NearestHousedPlayer(ch.transform.position, out home);
            if (body == null || home == null || PlayerTargetArbiter.IsHostBody(body))
                return true; // the host's own hideout: vanilla
            __instance.target = NightSceneTargets.Pick(__instance.locationGetObject, home, ch.gameObject, body.position);
            // Vanilla's last step (HostActivityAssignTargetPatch rebinds it to the right body).
            if (__instance.playerIsTarget)
                __instance.target = Player.Instance.gameObject;
            return false;
        }
    }

    /// <summary>A scene step played for a peer: "closest to the player" is that peer.</summary>
    [HarmonyPatch(typeof(Location.GetObject), nameof(Location.GetObject.returnObject))]
    public static class NightSceneClosestObjectPatch
    {
        private static bool Prefix(Location.GetObject __instance, Location _location, GameObject parent, ref GameObject __result)
        {
            if (__instance == null || !__instance.getClosestToPlayer || __instance.getClosest || _location == null || parent == null)
                return true;
            if (!NetGuard.ConnectedHost(out _))
                return true;
            Transform body = NightSceneTargets.RemoteActor();
            if (body == null)
                return true;
            __result = NightSceneTargets.Pick(__instance, _location, parent, body.position);
            return false;
        }
    }

    [HarmonyPatch(typeof(Location.GetObject), nameof(Location.GetObject.returnObjects))]
    public static class NightSceneClosestObjectsPatch
    {
        private static bool Prefix(Location.GetObject __instance, List<GameObject> returnToList, Location _location, GameObject parent)
        {
            if (__instance == null || !__instance.getClosestToPlayer || __instance.getClosest || __instance.getAll
                || _location == null || parent == null || returnToList == null)
                return true;
            if (!NetGuard.ConnectedHost(out _))
                return true;
            Transform body = NightSceneTargets.RemoteActor();
            if (body == null)
                return true;
            GameObject go = NightSceneTargets.Pick(__instance, _location, parent, body.position);
            if (go != null)
                returnToList.Add(go);
            return false;
        }
    }
}

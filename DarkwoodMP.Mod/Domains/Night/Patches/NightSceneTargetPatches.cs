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

    /// <summary>
    /// A scene step played for another player: "closest to the player" is that player. On the host
    /// for a peer's hideout; on a client replaying the scene of its hideout, which the host played
    /// for one player there: the vortex and its sound landed by this client's own body, away from
    /// the host's copy, whose later fires then found nothing here ("no GameEvents near ...
    /// name='vortex_move' after 60s, dropped").
    /// </summary>
    [HarmonyPatch(typeof(Location.GetObject), nameof(Location.GetObject.returnObject))]
    public static class NightSceneClosestObjectPatch
    {
        // Before LocationReturnObjectClosestPatch ("a peer in that location"): the scene's own player wins.
        [HarmonyPriority(Priority.High)]
        private static bool Prefix(Location.GetObject __instance, Location _location, GameObject parent, ref GameObject __result)
        {
            if (__instance == null || !__instance.getClosestToPlayer || __instance.getClosest || _location == null || parent == null)
                return true;
            if (!NetGuard.Connected(out _))
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
        [HarmonyPriority(Priority.High)]
        private static bool Prefix(Location.GetObject __instance, List<GameObject> returnToList, Location _location, GameObject parent)
        {
            if (__instance == null || !__instance.getClosestToPlayer || __instance.getClosest || __instance.getAll
                || _location == null || parent == null || returnToList == null)
                return true;
            if (!NetGuard.Connected(out _))
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

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A scripted step's random pick in a location (a random waypoint, door or window: where the
    /// banshee's intro stands, which spot a scene piece takes). Every machine running the step
    /// rolled its own: the host's piece stood at one waypoint and a client's replay at another, and
    /// the host's later fires of it found nothing there ("no GameEvents near ...
    /// name='NightEvent_weird_bansheeIntro_01B' after 60s, dropped (nearest of that name 624
    /// away)"). In co-op the pick comes from what every machine shares: the day, the step's object
    /// and the location, so they all take the same one (the location's lists are the same scene
    /// on every machine).
    /// </summary>
    internal static class SharedRandomPick
    {
        /// <summary>
        /// A scripted step's pick (its GameEvents object), not a creature's: creatures are the
        /// host's alone, and the same pick for every redneck of a night sent them all to one window.
        /// </summary>
        internal static bool Applies(Location.GetObject get, Location loc, GameObject parent)
            => get != null && !get.getAll && !get.getClosest && !get.getClosestToPlayer && loc != null && parent != null
               && ModRuntime.Network != null && ModRuntime.Network.IsConnected
               && parent.GetComponent<GameEvents>() != null && parent.GetComponent<Character>() == null;

        private static readonly List<GameObject> _sorted = new List<GameObject>(); // process-scoped: scratch

        /// <summary>
        /// The shared pick among <paramref name="objects"/>. The candidates are taken in the order
        /// of their spots, not of the location's lists: a location rebuilt from the save on a
        /// client lists its waypoints in another order than the host's, and the same index was
        /// another waypoint (the night mushroom grew at one spot on the host and another on the
        /// client, and the host's removal of it missed there).
        /// </summary>
        internal static GameObject Pick(Location.GetObject get, Location loc, GameObject parent, List<GameObject> objects)
        {
            _sorted.Clear();
            for (int i = 0; i < objects.Count; i++)
                if (objects[i] != null)
                    _sorted.Add(objects[i]);
            if (_sorted.Count == 0)
                return null;
            _sorted.Sort(CompareSpot);
            GameObject go = _sorted[Index(get, loc, parent, _sorted.Count)];
            ModRuntime.LegacyInfo($"[SharedPick] {parent.name} in {loc.name} {get.type} '{get.string1}': {go.name}@{go.transform.position} of {_sorted.Count}");
            _sorted.Clear();
            return go;
        }

        private static int CompareSpot(GameObject a, GameObject b)
        {
            Vector3 pa = a.transform.position, pb = b.transform.position;
            int c = Mathf.RoundToInt(pa.x).CompareTo(Mathf.RoundToInt(pb.x));
            if (c != 0) return c;
            c = Mathf.RoundToInt(pa.z).CompareTo(Mathf.RoundToInt(pb.z));
            if (c != 0) return c;
            return string.CompareOrdinal(a.name, b.name);
        }

        internal static int Index(Location.GetObject get, Location loc, GameObject parent, int count)
        {
            uint h = 2166136261u;
            Mix(ref h, Singleton<Controller>.Instance != null ? Singleton<Controller>.Instance.day.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
            Mix(ref h, parent.name);
            Mix(ref h, loc.name);
            Mix(ref h, ((int)get.type).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Mix(ref h, get.string1 ?? "");
            return (int)(h % (uint)count);
        }

        private static void Mix(ref uint h, string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 16777619u;
            }
            h ^= '|';
            h *= 16777619u;
        }
    }

    [HarmonyPatch(typeof(Location.GetObject), nameof(Location.GetObject.returnObject))]
    public static class SharedRandomPickObjectPatch
    {
        [HarmonyPriority(Priority.Low)]
        private static bool Prefix(Location.GetObject __instance, Location _location, GameObject parent, ref GameObject __result, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;
            if (!SharedRandomPick.Applies(__instance, _location, parent))
                return true;
            List<GameObject> objects = _location.getObjects(__instance);
            if (objects == null || objects.Count == 0)
                return true; // vanilla logs the miss
            GameObject pick = SharedRandomPick.Pick(__instance, _location, parent, objects);
            if (pick == null)
                return true;
            __result = pick;
            return false;
        }
    }

    [HarmonyPatch(typeof(Location.GetObject), nameof(Location.GetObject.returnObjects))]
    public static class SharedRandomPickObjectsPatch
    {
        [HarmonyPriority(Priority.Low)]
        private static bool Prefix(Location.GetObject __instance, List<GameObject> returnToList, Location _location, GameObject parent, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;
            if (returnToList == null || !SharedRandomPick.Applies(__instance, _location, parent))
                return true;
            List<GameObject> objects = _location.getObjects(__instance);
            if (objects == null || objects.Count == 0)
                return true;
            GameObject go = SharedRandomPick.Pick(__instance, _location, parent, objects);
            if (go == null)
                return true;
            returnToList.Add(go);
            return false;
        }
    }
}

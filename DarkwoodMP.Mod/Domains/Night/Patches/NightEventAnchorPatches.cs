using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Where night events happen. Vanilla runs them for the one player, in the location it stands
    /// in (<c>whereAmI.location</c>), and parents a location event (the scene: knocking, a voice,
    /// a visitor) under that location. In co-op every living player in a world location gets the
    /// scene where it stands, as in its own game: the host plays the event's GameEvents once per
    /// such location (the host's own, each peer's) with that player as the actor, and tells the
    /// clients which locations those were. Never into a location pad, and the night still runs on
    /// when the host is out in the forest while a peer is home. It used to go to the host's
    /// location only (or to the first peer found when the host was away), while every client
    /// replayed the scene in its own location: a client in another hideout heard the knocking and
    /// the visitor never came, and a second peer's hideout got nothing.
    /// </summary>
    internal static class NightEventAnchor
    {
        private static string _firedAnchors; // reset-in: Reset

        /// <summary>
        /// Host is starting a scene's GameEvents copy. Its own fire is not fanned out as a
        /// GameEventsFired: the copy exists only where it was spawned, and clients in that location
        /// replay the scene from ScenarioEventFired (others would queue a search for it for nothing).
        /// </summary>
        internal static bool PlayingScene; // reset-in: Reset

        internal static void Reset()
        {
            _firedAnchors = null;
            PlayingScene = false;
            _sceneAnchors.Clear();
        }

        /// <summary>Host: each scene copy played tonight → the peers who replayed it (in its location then).</summary>
        private static readonly Dictionary<Transform, List<int>> _sceneAnchors = new Dictionary<Transform, List<int>>(); // reset-in: Reset
        private static readonly List<Transform> _sceneScratch = new List<Transform>(8); // process-scoped: filled and emptied within one call

        internal static void NoteScene(Transform scene, Location loc)
        {
            if (scene == null || loc == null)
                return;
            // Copies are destroyed when their night ends: drop the dead keys first.
            _sceneScratch.Clear();
            foreach (Transform t in _sceneAnchors.Keys)
                if (t == null)
                    _sceneScratch.Add(t);
            foreach (Transform t in _sceneScratch)
                _sceneAnchors.Remove(t);
            var peers = new List<int>(2);
            PeersIn(loc, peers);
            _sceneAnchors[scene] = peers;
        }

        /// <summary>A piece a scene's step spawned (parented at a door, a waypoint): same peers as its scene.</summary>
        internal static void NoteScenePiece(Transform piece, List<int> peers)
        {
            if (piece != null && peers != null)
                _sceneAnchors[piece] = peers;
        }

        /// <summary>
        /// Host: the peers who have the scene copy <paramref name="t"/> belongs to, or null when it is
        /// not a scene copy. A later fire inside a copy (its own timers and triggers) concerns them
        /// only: they replayed the scene where they stood, the others have no copy and searched for
        /// it a minute ("no GameEvents near ... dropped").
        /// </summary>
        internal static List<int> ScenePeersOf(Transform t)
        {
            if (_sceneAnchors.Count == 0)
                return null;
            for (Transform cur = t; cur != null; cur = cur.parent)
                if (_sceneAnchors.TryGetValue(cur, out List<int> peers))
                    return peers;
            return null;
        }

        /// <summary>Peers standing in <paramref name="loc"/>.</summary>
        internal static void PeersIn(Location loc, List<int> into)
        {
            into.Clear();
            var net = ModRuntime.Network;
            if (net == null || loc == null)
                return;
            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy == null || proxy.PlayerId <= 0)
                    continue;
                Location at = Location.getAtPos(proxy.transform.position);
                Location big = at != null && at.bigLocation != null ? at.bigLocation : at;
                if (big == loc)
                    into.Add(proxy.PlayerId);
            }
        }

        /// <summary>Host checkFrequencies postfix: the locations the event that just started played in.</summary>
        internal static string TakeFiredAnchors()
        {
            string a = _firedAnchors ?? string.Empty;
            _firedAnchors = null;
            return a;
        }

        /// <summary>Host stands in a world location (not a pad): vanilla's own gate holds.</summary>
        internal static bool HostUsesVanilla()
        {
            if (!NetGuard.ConnectedHost(out _))
                return true;
            if (LanNetworkManager.IsApplyingRemoteState)
                return true;
            Player host = Player.Instance;
            if (host == null || host.whereAmI == null)
                return true;
            return !HostInOutsideLocation() && host.whereAmI.location != null;
        }

        internal static bool HostInOutsideLocation()
        {
            var ol = Singleton<OutsideLocations>.Instance;
            return ol != null && ol.playerInOutsideLocation;
        }

        /// <summary>A living peer stands in a world location.</summary>
        internal static bool AnyPeerAnchor()
        {
            var list = new List<KeyValuePair<Location, int>>(4);
            CollectPeers(list);
            return list.Count > 0;
        }

        /// <summary>
        /// Each world location a living player stands in, with one player there (the actor its
        /// scene plays for). The host's own first, as vanilla parents it.
        /// </summary>
        internal static void Collect(List<KeyValuePair<Location, int>> into)
        {
            into.Clear();
            var net = ModRuntime.Network as LanNetworkManager;
            Player host = Player.Instance;
            if (net != null && host != null && host.whereAmI != null && !HostInOutsideLocation())
            {
                Location big = host.whereAmI.bigLocation;
                if (big != null && !big.isOutsideLocation)
                    into.Add(new KeyValuePair<Location, int>(big, net.LocalPlayerId));
            }
            CollectPeers(into);
        }

        private static void CollectPeers(List<KeyValuePair<Location, int>> into)
        {
            var net = ModRuntime.Network;
            if (net == null)
                return;
            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy == null)
                    continue;
                CharBase cb = proxy.CachedCharBase;
                if (cb != null && !cb.alive)
                    continue;
                Location at = Location.getAtPos(proxy.transform.position);
                Location big = at != null && at.bigLocation != null ? at.bigLocation : at;
                if (big == null || big.isOutsideLocation)
                    continue;
                bool seen = false;
                for (int i = 0; i < into.Count; i++)
                    seen |= into[i].Key == big;
                if (!seen)
                    into.Add(new KeyValuePair<Location, int>(big, proxy.PlayerId));
            }
        }

        /// <summary>The event asks about "the player" (a perk, its health, the creatures after it).</summary>
        internal static bool HasPlayerRequirement(RandomEvent e)
        {
            if (e == null || e.requirements == null)
                return false;
            for (int i = 0; i < e.requirements.Count; i++)
            {
                EventTriggerRequirement r = e.requirements[i];
                if (r != null && r.type == EventTriggerRequirement.Type.playerState)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The player a scene would play for meets the event's player requirements on its own body,
        /// as "the player" does in its own game: the Shadows perk's night waves come to the player
        /// with the perk, a knock at the door waits while that player is being attacked.
        /// </summary>
        internal static bool AnchorPasses(RandomEvent e, int actor)
        {
            bool pushed = actor > 0;
            if (pushed)
                GeFireActorContext.Push(actor);
            try
            {
                for (int i = 0; i < e.requirements.Count; i++)
                {
                    EventTriggerRequirement r = e.requirements[i];
                    if (r != null && r.type == EventTriggerRequirement.Type.playerState && !r.requirementsMet())
                        return false;
                }
                return true;
            }
            finally
            {
                if (pushed)
                    GeFireActorContext.Pop();
            }
        }

        /// <summary>Drops the anchors whose player does not meet the event's player requirements.</summary>
        internal static void KeepPassing(RandomEvent e, List<KeyValuePair<Location, int>> anchors)
        {
            if (!HasPlayerRequirement(e))
                return;
            for (int i = anchors.Count - 1; i >= 0; i--)
            {
                if (!AnchorPasses(e, anchors[i].Value))
                    anchors.RemoveAt(i);
            }
        }

        internal static void NoteFired(List<KeyValuePair<Location, int>> anchors)
        {
            var names = new List<string>(anchors.Count);
            for (int i = 0; i < anchors.Count; i++)
                names.Add(anchors[i].Key.name);
            _firedAnchors = string.Join("|", names);
        }

        /// <summary>Client: it stands in one of the host's anchor locations.</summary>
        internal static bool LocalIn(string anchors)
        {
            if (string.IsNullOrEmpty(anchors))
                return false;
            Player p = Player.Instance;
            if (p == null || p.whereAmI == null || HostInOutsideLocation())
                return false;
            Location big = p.whereAmI.bigLocation;
            if (big == null || big.isOutsideLocation)
                return false;
            foreach (string name in anchors.Split('|'))
                if (name == big.name)
                    return true;
            return false;
        }

        /// <summary>
        /// Client: a location event that played elsewhere still started tonight (vanilla
        /// <c>CustomEvent.fire</c> bookkeeping without its scene).
        /// </summary>
        internal static void MarkStarted(CustomEvent ce)
        {
            ce.timeStarted = Singleton<Controller>.Instance.CurrentTimeAndDay;
            for (int i = 0; i < ce.categories.Count; i++)
                Singleton<Events>.Instance.addCategory(ce.categories[i].ToString());
            if (Singleton<NightScenarios>.Instance.currentScenario != null)
                Singleton<NightScenarios>.Instance.currentScenario.currentEvent = ce;
            ce.started = true;
            if (ce.theEvent != null)
                ce.theEvent.startedToday = true;
        }
    }

    /// <summary>
    /// Host not in a world location: run vanilla <c>NightScenario.checkFrequencies</c> without its
    /// "player is in a location" gate when a peer is in one; skip it inside a location pad.
    /// <see cref="HostCheckFrequenciesPostfix"/> still sends whatever fired.
    /// </summary>
    [HarmonyPatch(typeof(NightScenario), "checkFrequencies")]
    public static class HostNightEventsAwayPatch
    {
        private static bool Prefix(NightScenario __instance)
        {
            if (NightEventAnchor.HostUsesVanilla())
                return true;
            if (!NightEventAnchor.AnyPeerAnchor())
                // In a pad: vanilla would fire events into it. Out in the forest: vanilla returns.
                return !NightEventAnchor.HostInOutsideLocation();
            CheckFrequencies(__instance);
            return false;
        }

        /// <summary>Vanilla NightScenario.checkFrequencies after its location gate.</summary>
        private static void CheckFrequencies(NightScenario s)
        {
            Controller ctrl = Singleton<Controller>.Instance;
            if (Core.isDay())
                return;
            if (s.currentEvent != null && s.currentEvent.shouldEnd())
            {
                s.currentEvent = null;
                s.setLastTimeCheckedForEvents();
                return;
            }
            if (s.currentEvent != null)
                return;
            if (ctrl.CurrentTime != (int)ctrl.nightTime)
            {
                for (int i = 0; i < s.customEventAndInts.Count; i++)
                {
                    var cei = s.customEventAndInts[i];
                    if (ctrl.currentTimeDifference((int)ctrl.nightTime) == cei.timeToStart
                        && cei.customEvent != null && cei.customEvent.theEvent != null)
                    {
                        cei.customEvent.lastTimeCheckedIfWantToFire = ctrl.CurrentTimeAndDay;
                        cei.customEvent.fire(force: true);
                        return;
                    }
                }
            }
            var list = new List<CustomEvent>();
            for (int j = 0; j < s.customEventAndInts.Count; j++)
            {
                var cei = s.customEventAndInts[j];
                if (cei.customEvent != null && cei.timeToStart == 0 && cei.customEvent.theEvent != null
                    && cei.customEvent.frequencyMet())
                {
                    cei.customEvent.lastTimeCheckedIfWantToFire = ctrl.CurrentTimeAndDay;
                    if (cei.customEvent.requirementsMet())
                        list.Add(cei.customEvent);
                }
            }
            if (list.Count > 0)
                list[Random.Range(0, list.Count)].fire();
        }
    }

    /// <summary>
    /// Host location events (<c>RandomEvent.Type.locationEvent</c>): play the scene in every world
    /// location a living player stands in (<see cref="NightEventAnchor"/>), each with that player
    /// as the actor. Nobody in one: nothing plays (vanilla would hit a pad or a null location).
    /// </summary>
    [HarmonyPatch(typeof(RandomEvent), "fire")]
    public static class HostLocationEventAnchorsPatch
    {
        private static readonly System.Action<RandomEvent> RemoveMe =
            AccessTools.MethodDelegate<System.Action<RandomEvent>>(AccessTools.Method(typeof(RandomEvent), "removeMe"));

        // Filled and emptied within one call.
        private static readonly List<KeyValuePair<Location, int>> _anchors = new List<KeyValuePair<Location, int>>(4); // process-scoped

        private static bool Prefix(RandomEvent __instance, bool checkIfRequirementsMet, bool force)
        {
            if (__instance.type != RandomEvent.Type.locationEvent)
                return true;
            if (!NetGuard.ConnectedHost(out _) || LanNetworkManager.IsApplyingRemoteState)
                return true;

            RandomEvent e = __instance;
            // Vanilla fire's entry gate, unchanged.
            if ((e.disabled && !force)
                || !((((checkIfRequirementsMet && e.requirementsMet()) || !checkIfRequirementsMet)
                      && (!e.startedToday || e.multipleTimesPerDay)) || force))
                return false;

            NightEventAnchor.Collect(_anchors);
            // A forced (fixed-time) event ignores its requirements, as vanilla's force does.
            if (!force)
                NightEventAnchor.KeepPassing(e, _anchors);
            bool fired = false;
            for (int a = 0; a < _anchors.Count; a++)
            {
                Location loc = _anchors[a].Key;
                GeFireActorContext.Push(_anchors[a].Value);
                try
                {
                    for (int i = 0; i < e.gameEvents.Count; i++)
                    {
                        if (e.gameEvents[i] == null)
                            continue;
                        GameEvents scene = Core.AddPrefab(e.gameEvents[i].gameObject, Vector3.zero,
                            Quaternion.identity, loc.gameObject).GetComponent<GameEvents>();
                        NightEventAnchor.NoteScene(scene.transform, loc);
                        NightEventAnchor.PlayingScene = true;
                        try { scene.fire(); }
                        finally { NightEventAnchor.PlayingScene = false; }
                        fired = true;
                    }
                }
                finally
                {
                    GeFireActorContext.Pop();
                }
            }
            if (fired)
                NightEventAnchor.NoteFired(_anchors);
            _anchors.Clear();
            e.randomizeStartTime();
            if (fired)
            {
                e.startedToday = true;
                if (e.removeOnFire)
                    RemoveMe(e);
            }
            return false;
        }
    }

    /// <summary>
    /// Host with peers: a location event's player requirements (the Shadows perk, the player's
    /// health, nobody attacking it) are about the player the scene plays for. Vanilla reads them off
    /// the one player; the host's body alone decided for everyone, so a client with the Shadows perk
    /// never got its night waves (and a host with it got them in a peer's hideout). The event is
    /// eligible when some player in a world location meets them; the chance roll and every other
    /// requirement run once, as vanilla's. <see cref="HostLocationEventAnchorsPatch"/> then plays the
    /// scene only where its player meets them.
    /// </summary>
    [HarmonyPatch(typeof(RandomEvent), nameof(RandomEvent.requirementsMet))]
    public static class HostLocationEventPlayerRequirementsPatch
    {
        // Filled and emptied within one call.
        private static readonly List<KeyValuePair<Location, int>> _anchors = new List<KeyValuePair<Location, int>>(4); // process-scoped

        private static bool Prefix(RandomEvent __instance, ref bool __result)
        {
            RandomEvent e = __instance;
            if (e == null || e.type != RandomEvent.Type.locationEvent || !NightEventAnchor.HasPlayerRequirement(e))
                return true;
            if (!NetGuard.ConnectedHost(out _) || LanNetworkManager.IsApplyingRemoteState || !PlayerPositionManager.HasRemotePlayer)
                return true;

            // Vanilla requirementsMet, with the player requirements asked per player.
            __result = false;
            if (e.disabled)
                return false;
            if (Random.Range(0f, 1f) > e.chance)
                return false;
            for (int i = 0; i < e.requirements.Count; i++)
            {
                EventTriggerRequirement r = e.requirements[i];
                if (r != null && r.type != EventTriggerRequirement.Type.playerState && !r.requirementsMet())
                    return false;
            }
            NightEventAnchor.Collect(_anchors);
            try
            {
                for (int a = 0; a < _anchors.Count; a++)
                {
                    if (NightEventAnchor.AnchorPasses(e, _anchors[a].Value))
                    {
                        __result = true;
                        break;
                    }
                }
            }
            finally
            {
                _anchors.Clear();
            }
            return false;
        }
    }
}

using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Per-instance component lookups for <see cref="HostCanSeeEnemyPatch"/> (canSeeEnemy
    /// runs for every awake AI each think tick). Cleared on session reset.
    /// </summary>
    internal static class CanSeeComponentCache
    {
        private struct Entry
        {
            public Sniffer Sniffer;
            public Collider Collider;
        }

        private const int MaxEntries = 4096;
        private static readonly Dictionary<int, Entry> _byCharacter = new Dictionary<int, Entry>();
        private static readonly Dictionary<int, bool> _isProxyTarget = new Dictionary<int, bool>();
        private static int _hostPlayerId;
        private static CharBase _hostCharBase;

        internal static void Get(Character c, out Sniffer sniffer, out Collider collider)
        {
            int id = c.GetInstanceID();
            if (!_byCharacter.TryGetValue(id, out Entry e))
            {
                if (_byCharacter.Count >= MaxEntries)
                    _byCharacter.Clear();
                e = new Entry { Sniffer = c.GetComponent<Sniffer>(), Collider = c.GetComponent<Collider>() };
                _byCharacter[id] = e;
            }
            sniffer = e.Sniffer;
            collider = e.Collider;
        }

        internal static CharBase HostCharBase()
        {
            Player p = Player.Instance;
            if (p == null)
                return null;
            int id = p.GetInstanceID();
            if (id != _hostPlayerId || _hostCharBase == null)
            {
                _hostPlayerId = id;
                _hostCharBase = p.GetComponent<CharBase>();
            }
            return _hostCharBase;
        }

        internal static bool IsProxy(Transform target)
        {
            int id = target.GetInstanceID();
            if (!_isProxyTarget.TryGetValue(id, out bool isProxy))
            {
                if (_isProxyTarget.Count >= MaxEntries)
                    _isProxyTarget.Clear();
                isProxy = target.GetComponent<RemotePlayerProxy>() != null;
                _isProxyTarget[id] = isProxy;
            }
            return isProxy;
        }

        internal static void Reset()
        {
            _byCharacter.Clear();
            _isProxyTarget.Clear();
            _hostPlayerId = 0;
            _hostCharBase = null;
        }
    }

    /// <summary>
    /// Host with remote players: vanilla's sight check (<c>canSeeEnemy</c>, every 0.5-1 s) for more
    /// than one player body.
    ///
    /// Vanilla puts every character it sees in <c>charactersInSight</c> and then, for each one in
    /// list order (the physics overlap order, not distance), turns to listen to it and makes it the
    /// target, so the last one seen wins. With one player that is always the player. With the host
    /// and a client both in view the creature flipped between them on every check (and the mod's
    /// own sensing and closest-player rules flipped it again), turning on the spot each time.
    ///
    /// Now: (1) stand-ins vanilla's ray misses (it only counts their root collider) are sensed by
    /// the same sight test and given vanilla's per-sighting consequences; (2) which player body the
    /// creature ends up on is <see cref="PlayerTargetArbiter"/>'s choice (hold the current one,
    /// else the nearest seen); (3) vanilla's "turn and listen" fires once, toward that body, under
    /// the same conditions vanilla would apply to a single player (<see cref="HostCanSeeListenPatch"/>
    /// holds back the per-body listens of vanilla's loop). Other targets stay vanilla's.
    /// </summary>
    [HarmonyPatch(typeof(Character), "canSeeEnemy")]
    public static class HostCanSeeEnemyPatch
    {
        internal struct State
        {
            public bool On;
            public bool Scoped;
            public Transform Before;
        }

        /// <summary>The creature whose sight check is running (listens to player bodies are held back).</summary>
        internal static Character Scope; // process-scoped: call-scoped, cleared by the Finalizer

        /// <summary>Inside the scope, vanilla asked to listen to a player body (held back).</summary>
        internal static bool PlayerListenHeld; // process-scoped: call-scoped, cleared by the Prefix and Finalizer

        /// <summary>Inside the scope, a listen to a non-player went through after a held one (it was vanilla's last).</summary>
        internal static bool OtherListenAfter; // process-scoped: call-scoped, cleared by the Prefix and Finalizer

        private static void Prefix(Character __instance, ref State __state)
        {
            __state = default;
            if (__instance == null || !HostPlayerIdentity.HostWithRemotes())
                return;
            __state.On = true;
            __state.Before = __instance.target;
            PlayerTargetArbiter.CheckUnrouted(__instance);
            if (Scope == null)
            {
                Scope = __instance;
                PlayerListenHeld = false;
                OtherListenAfter = false;
                __state.Scoped = true;
            }
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Character __instance, State __state)
        {
            if (!__state.On || __instance == null)
                return;
            Character c = __instance;
            // Vanilla's own early return (blind / inactive) touched nothing.
            if (c.blind || !c.isActive || !c.alive)
            {
                PlayerTargetArbiter.Observe(c, __state.Before, c.target, "canSeeEnemy");
                return;
            }

            bool assigns = !c.onlyAttackPlayer && (!c.veryHungry || !c.eating);
            Transform before = __state.Before;
            // Vanilla's top clears a dead target before its loop.
            if (before != null && PlayerTargetArbiter.IsPlayerBody(before))
            {
                CharBase bcb = before.GetComponent<CharBase>();
                if (bcb == null || !bcb.alive)
                    before = null;
            }

            if (!c.dummy)
                SenseStandIns(c, assigns);

            Transform pick = PlayerTargetArbiter.Choose(c, before, switchWindow: false,
                out PlayerTargetReason reason, out bool pickSensed);
            Transform after = c.target;
            if (assigns && pick != null && after != pick && PlayerTargetArbiter.IsPlayerBody(after))
            {
                if (after != before)
                    PlayerTargetArbiter.TraceHold(c, pick, after, "canSeeEnemy", reason);
                c.target = pick;
            }
            // A listen to another character that vanilla made after the player bodies' turn was its
            // last word; leave it.
            if (pick != null && pickSensed && !OtherListenAfter)
                Listen(c, before, pick);

            PlayerTargetArbiter.Observe(c, __state.Before, c.target, "canSeeEnemy", reason);
            // A player target in sight is always the arbiter's pick (it holds a sensed target).
            if (pick != null && pickSensed && c.target == pick)
                PlayerTargetArbiter.NoteSensed(c, pick);
            TraceProxyChase(c);
        }

        private static void Finalizer(State __state)
        {
            if (!__state.Scoped)
                return;
            Scope = null;
            PlayerListenHeld = false;
            OtherListenAfter = false;
        }

        /// <summary>Vanilla <c>seesFaction</c> (private): the creature attacks or flees that faction.</summary>
        private static bool SeesFaction(Character c, Faction faction)
        {
            List<Character.EnemyType> types = c.enemyTypes;
            if (types == null)
                return false;
            for (int i = 0; i < types.Count; i++)
            {
                if (types[i] != null && types[i].faction == faction && (types[i].attacks || types[i].runsAwayFrom))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Stand-ins vanilla's ray missed, seen with vanilla's own range and field of view (halved
        /// while eating, all round while asleep or underwater), added to the sight list with what
        /// vanilla's loop does for each seen character: <c>canSeeEnemyFar</c>, the <c>lostEnemy</c>
        /// countdown stopped, the target (when vanilla assigns targets from sight), <c>canSeeEnemyNear</c>
        /// up close, the sleep-view wake-up and the <c>superTarget</c> follow. The listen is
        /// <see cref="Listen"/>'s. Before, an unseen stand-in also counted as seen when it was inside
        /// the creature's smell radius, which the host never did; smell goes through the sniffer for
        /// every player.
        /// </summary>
        private static void SenseStandIns(Character c, bool assigns)
        {
            if (ProxyDistanceHelper.ProxyIsFar(c))
                return;
            var net = ModRuntime.Network;
            if (net == null)
                return;
            CanSeeComponentCache.Get(c, out Sniffer _, out Collider own);
            float maxD = (float)c.farViewDistance * c.aniSightRangeModifier;
            float fov = c.fieldOfViewRange;
            if (c.eating)
            {
                maxD = c.farViewDistance / 2;
                fov = c.fieldOfViewRange / 2;
            }
            if (c.sleeping || c.isUnderwater)
                fov = 360f;
            float nearR = (float)c.nearViewDistance * c.aniSightRangeModifier;
            Vector3 from = c.transform.position;
            Vector3 up = c.transform.up;

            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy == null)
                    continue;
                CharBase pcb = proxy.CachedCharBase;
                if (pcb == null || !pcb.alive || pcb.invisible || pcb.ignoreMe)
                    continue;
                if (DeathStateTracker.IsRemoteNightDead(proxy.PlayerId))
                    continue;
                if (c.charactersInSight.Contains(pcb) || !SeesFaction(c, pcb.faction))
                    continue;
                Transform pt = proxy.transform;
                Vector3 to = pt.position - from;
                float d = to.magnitude;
                if (d > maxD || !(Vector3.Angle(to, up) < fov))
                    continue;
                if (!Physics.Raycast(from, to, out RaycastHit hit, d, 18909185)
                    || hit.collider == null
                    || hit.collider == own
                    || hit.collider.GetComponentInParent<RemotePlayerProxy>() != proxy)
                    continue;

                c.charactersInSight.Add(pcb);
                c.canSeeEnemyFar = true;
                c.stopRoutine("lostEnemy", true);
                if (assigns)
                    c.target = pt;
                float flat = Core.trueDistance(pt.position, from);
                if (flat < nearR)
                    c.canSeeEnemyNear = true;
                if (c.sleeping && c.sleepViewDistance > 0 && flat < (float)c.sleepViewDistance)
                    c.wakeup();
                if (c.superTarget == pt)
                {
                    if (c.AIpath != null)
                        c.AIpath.setTarget(pt);
                    c.lastKnownTargetPosition = pt.position;
                }
            }
        }

        /// <summary>
        /// Vanilla's "stop and listen" for one player, with the player bodies as that one player.
        /// Vanilla calls it for a seen character when the creature is not chasing, defending,
        /// following or escaping, sees nothing up close yet, is not neutral, is not heading for a
        /// lure while starving, and is not already targeting that character. Its loop runs in list
        /// order, so the conditions are taken where the first player body sits in the list: the
        /// target the loop had reached there (what the creature had before, or an earlier
        /// non-player it saw) and whether an earlier sighting was already up close.
        /// </summary>
        private static void Listen(Character c, Transform before, Transform pick)
        {
            if (c.aggressiveness == Aggressiveness.neutral
                || c.behaviour == Character.Behaviour.chasingTarget
                || c.behaviour == Character.Behaviour.defensive
                || c.behaviour == Character.Behaviour.following
                || c.behaviour == Character.Behaviour.escaping
                || (c.veryHungry && c.headingForLure))
                return;
            bool assigns = !c.onlyAttackPlayer && (!c.veryHungry || !c.eating);
            float nearR = (float)c.nearViewDistance * c.aniSightRangeModifier;
            Vector3 from = c.transform.position;
            Transform running = before;
            List<CharBase> list = c.charactersInSight;
            for (int i = 0; i < list.Count; i++)
            {
                CharBase cb = list[i];
                if (cb == null || cb.invisible || !cb.alive)
                    continue;
                if (PlayerTargetArbiter.IsPlayerBody(cb.transform))
                    break;
                if (Core.trueDistance(cb.transform.position, from) < nearR)
                    return;
                if (assigns)
                    running = cb.transform;
            }
            if (running == pick)
                return;
            Character scope = Scope;
            Scope = null;
            try
            {
                c.stopAndListenTo(pick.position);
            }
            finally
            {
                Scope = scope;
            }
        }

        /// <summary>How far behind the stand-in a chasing creature's chase point is, once a second.</summary>
        private static void TraceProxyChase(Character c)
        {
            Transform t = c.target;
            if (t == null || c.behaviour != Character.Behaviour.chasingTarget || !CanSeeComponentCache.IsProxy(t))
                return;
            ModLog.TraceRate(LogCat.AI, "aichase:" + c.GetInstanceID(), () =>
                $"[AIChase] {c.name} → stand-in seenFar={c.canSeeEnemyFar} near={c.canSeeEnemyNear} "
                + $"inSight={c.enemyInSight} lag={Core.trueDistance(c.lastKnownTargetPosition, t.position):F0} "
                + $"dist={Core.trueDistance(c.transform.position, t.position):F0}", 1f);
        }
    }

    /// <summary>
    /// Inside <see cref="HostCanSeeEnemyPatch"/>'s sight check, vanilla's loop turns the creature to
    /// listen to every player body it sees in turn (the last one wins). Those listens are held back;
    /// the sight patch then listens once, to the body the arbiter chose. Listens to anything else
    /// (other characters) and every listen outside the sight check stay vanilla's.
    /// </summary>
    [HarmonyPatch(typeof(Character), nameof(Character.stopAndListenTo))]
    public static class HostCanSeeListenPatch
    {
        private static bool Prefix(Character __instance, Vector3 _pos)
        {
            Character scope = HostCanSeeEnemyPatch.Scope;
            if (scope == null || scope != __instance)
                return true;
            if (AtPlayerBody(_pos))
            {
                HostCanSeeEnemyPatch.PlayerListenHeld = true;
                HostCanSeeEnemyPatch.OtherListenAfter = false;
                return false;
            }
            if (HostCanSeeEnemyPatch.PlayerListenHeld)
                HostCanSeeEnemyPatch.OtherListenAfter = true;
            return true;
        }

        /// <summary>Vanilla passes the seen character's own position.</summary>
        private static bool AtPlayerBody(Vector3 pos)
        {
            Player host = Player.Instance;
            if (host != null && host.transform.position == pos)
                return true;
            var net = ModRuntime.Network;
            if (net == null)
                return false;
            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy != null && proxy.transform.position == pos)
                    return true;
            }
            return false;
        }
    }
}

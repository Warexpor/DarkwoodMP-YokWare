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
    /// A walk-in trigger whose events lock a door (the wedding's "close the escape route", the
    /// wolf's room, a dream's exit) is built for one player: whoever walks in is on the right side
    /// of the door by definition. With several players the first one through locked the others
    /// out, or in a dead-end room, for good. On the host such a trigger waits until every living
    /// player in that location has walked into its volume, then fires once for the party.
    /// Clients never fire one-shots themselves, so the host's wait is everyone's.
    /// On a dream's pad "in that location" is "still playing the dream": the pad's ground does not
    /// always have one Location under every part of it, and a dreamer in the corridor outside the
    /// apartment dream's studio did not count, so a client walking in locked the host out for good.
    /// </summary>
    internal static class LockBehindPartyGate
    {
        /// <summary>Without a Location under the trigger (a dream pad): players this close count as there.</summary>
        private const float NoLocationRadius = 2500f;
        private const float TickInterval = 1f;

        private static readonly Dictionary<int, bool> _locksDoor = new Dictionary<int, bool>();              // reset-in: Reset
        private static readonly Dictionary<int, HashSet<int>> _passed = new Dictionary<int, HashSet<int>>(); // reset-in: Reset
        private static readonly Dictionary<int, EventTriggers> _waiting = new Dictionary<int, EventTriggers>(); // reset-in: Reset
        private static readonly List<int> _scratch = new List<int>();                                        // reset-in: Reset
        private static float _nextTick;                                                                     // reset-in: Reset

        public static void Reset()
        {
            _locksDoor.Clear();
            _passed.Clear();
            _waiting.Clear();
            _scratch.Clear();
            _nextTick = 0f;
        }

        /// <summary>An area trigger of this set still has to run events that lock a door or plug a doorway.</summary>
        private static bool Gated(EventTriggers et)
        {
            if (et == null || et.eventTriggers == null || !et.reactsToPlayer)
                return false;
            int key = et.GetInstanceID();
            if (!_locksDoor.TryGetValue(key, out bool locks))
            {
                locks = false;
                for (int i = 0; i < et.eventTriggers.Count && !locks; i++)
                {
                    EventTrigger t = et.eventTriggers[i];
                    if (t == null || t.type != EventTrigger.Type.area || t.gameEvents == null || t.gameEvents.events == null)
                        continue;
                    locks = LocksDoor(t.gameEvents) || SetsFlagThatLocks(et, t.gameEvents);
                }
                _locksDoor[key] = locks;
            }
            if (!locks)
                return false;
            for (int i = 0; i < et.eventTriggers.Count; i++)
            {
                EventTrigger t = et.eventTriggers[i];
                if (t == null || t.disabled || t.type != EventTrigger.Type.area || t.gameEvents == null)
                    continue;
                if (t.gameEvents.multipleFire || !t.gameEvents.fired)
                    return true;
            }
            return false;
        }

        private static bool LocksDoor(GameEvents ge)
        {
            if (ge == null || ge.events == null)
                return false;
            for (int j = 0; j < ge.events.Count; j++)
            {
                GameEvent e = ge.events[j];
                if (e == null || e.disabled)
                    continue;
                if (e.type == GameEvent.Type.modifyDoor && e.doorModifyType == GameEvent.DoorModify.lockMe)
                    return true;
                if (e.type == GameEvent.Type.gameObject && e.gameObjectModifyType == GameEvent.GameObjectModify.setActive
                    && e.activeModifier && PlugsWay(e.targetGameObjects))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Switching on a plug: a bare solid collider with nothing else on it and nothing under it,
        /// dropped into a doorway behind the player (the wedding's groom room, the acid dream's
        /// studio, the maw in the doctor's dream). It shuts the way as a locked door would.
        /// </summary>
        private static bool PlugsWay(List<GameObject> targets)
        {
            if (targets == null)
                return false;
            for (int i = 0; i < targets.Count; i++)
            {
                GameObject go = targets[i];
                if (go == null || go.transform.childCount != 0)
                    continue;
                Component[] comps = go.GetComponents<Component>();
                if (comps.Length != 2)
                    continue;
                Collider c = go.GetComponent<Collider>();
                if (c != null && !c.isTrigger)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The trigger sets a world flag that another trigger of its location waits for, and that
        /// one locks a door. The wedding's switch in the groom room: its flag lets the groom's beat
        /// fire the bride attack, which shuts and locks the yard door, and the switch itself plugs
        /// the doorway. The first player back in the groom room shut the others in the yard.
        /// </summary>
        private static bool SetsFlagThatLocks(EventTriggers et, GameEvents ge)
        {
            if (ge == null || ge.events == null)
                return false;
            HashSet<string> flags = null;
            for (int j = 0; j < ge.events.Count; j++)
            {
                GameEvent e = ge.events[j];
                if (e != null && !e.disabled && e.type == GameEvent.Type.worldFlag && !string.IsNullOrEmpty(e.Value))
                    (flags ?? (flags = new HashSet<string>())).Add(e.Value);
            }
            if (flags == null)
                return false;
            Location here = Location.getAtPos(et.transform.position);
            if (here == null)
                return false;
            EventTriggers[] all = WorldQueryHelper.GetCachedSceneComponents<EventTriggers>();
            for (int i = 0; i < all.Length; i++)
            {
                EventTriggers other = all[i];
                if (other == null || other == et || other.eventTriggers == null)
                    continue;
                if (Location.getAtPos(other.transform.position) != here)
                    continue;
                bool waitsForFlag = RequiresFlag(other.eventRequirements, flags);
                for (int k = 0; k < other.eventTriggers.Count; k++)
                {
                    EventTrigger t = other.eventTriggers[k];
                    if (t == null || t.disabled)
                        continue;
                    if ((waitsForFlag || RequiresFlag(t.eventRequirements, flags)) && LocksDoor(t.gameEvents))
                        return true;
                }
            }
            return false;
        }

        private static bool RequiresFlag(List<EventTriggerRequirement> reqs, HashSet<string> flags)
        {
            if (reqs == null)
                return false;
            for (int i = 0; i < reqs.Count; i++)
            {
                EventTriggerRequirement r = reqs[i];
                if (r != null && r.type == EventTriggerRequirement.Type.worldFlag
                    && !string.IsNullOrEmpty(r.keyType) && flags.Contains(r.keyType))
                    return true;
            }
            return false;
        }

        internal static void NotePassed(EventTriggers et, int playerId)
        {
            if (playerId <= 0 || !EventTriggersAuth.IsHost() || !Gated(et))
                return;
            int key = et.GetInstanceID();
            if (!_passed.TryGetValue(key, out HashSet<int> set))
                _passed[key] = set = new HashSet<int>();
            set.Add(playerId);
        }

        /// <summary>
        /// Every living player in the trigger's location has walked into it. <paramref name="inside"/>
        /// is one of them standing in the volume now (0 when nobody is).
        /// </summary>
        private static bool PartyThrough(EventTriggers et, out int inside, out int missing)
        {
            inside = 0;
            missing = 0;
            LanNetworkManager net = ModRuntime.Network;
            if (net == null)
                return true;
            _passed.TryGetValue(et.GetInstanceID(), out HashSet<int> passed);
            Vector3 at = et.transform.position;
            bool dream = DreamSyncManager.IsDreamActive && DreamSyncManager.IsOnDreamPad(et.transform);
            Location here = dream ? null : Location.getAtPos(at);
            int mask = 1 << et.gameObject.layer;

            Player local = Player.Instance;
            if (local != null && local.health > 0f && (!dream || FinalDreamsceneManager.LocalIsLiveDreamer()))
                Check(net.LocalPlayerId, local._transform.position, et, dream, here, at, mask, passed, ref inside, ref missing);
            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy == null || proxy.PlayerId <= 0 || proxy.RemoteHealthPct <= 0f
                    || proxy.RemoteInPrologue || proxy.RemoteInEpilogue)
                    continue;
                if (dream && !FinalDreamsceneManager.IsLiveRemoteDreamer(proxy.PlayerId))
                    continue;
                Check(proxy.PlayerId, proxy.transform.position, et, dream, here, at, mask, passed, ref inside, ref missing);
            }
            return missing == 0;
        }

        private static void Check(int playerId, Vector3 pos, EventTriggers et, bool dream, Location here, Vector3 at, int mask,
            HashSet<int> passed, ref int inside, ref int missing)
        {
            bool present = dream
                || (here != null
                    ? Location.getAtPos(pos) == here
                    : (pos - at).sqrMagnitude < NoLocationRadius * NoLocationRadius);
            if (!present)
                return;
            if (passed == null || !passed.Contains(playerId))
            {
                missing++;
                return;
            }
            if (inside == 0 && Helpers.isComponentAtPos(pos, mask, et))
                inside = playerId;
        }

        /// <summary>Host, before an area fire: false holds it back until the party is through.</summary>
        internal static bool AllowFire(EventTriggers et)
        {
            if (!EventTriggersAuth.IsHost() || !Gated(et))
                return true;
            int key = et.GetInstanceID();
            if (PartyThrough(et, out _, out int missing))
            {
                _waiting.Remove(key);
                return true;
            }
            if (!_waiting.ContainsKey(key))
                ModLog.Event(LogCat.World,
                    "[PartyGate] " + et.name + " locks a door: held until " + missing + " more player(s) here walk in");
            _waiting[key] = et;
            return false;
        }

        /// <summary>Host: a held trigger whose party is through now fires, for a player standing in it.</summary>
        internal static void TryRelease(EventTriggers et)
        {
            if (et == null || !_waiting.ContainsKey(et.GetInstanceID()))
                return;
            if (!EventTriggersAuth.IsHost() || !et.isActiveAndEnabled || !EventTriggersAuth.CanFireTriggers(et))
                return;
            if (!Gated(et))
            {
                _waiting.Remove(et.GetInstanceID());
                return;
            }
            if (!PartyThrough(et, out int inside, out _) || inside == 0)
                return;
            _waiting.Remove(et.GetInstanceID());
            ModLog.Event(LogCat.World, "[PartyGate] " + et.name + " released: the party is through (p" + inside + " stands in it)");
            LanNetworkManager net = ModRuntime.Network;
            if (net != null && inside != net.LocalPlayerId)
                DialogHostApplyGuard.RunHostWorldFanoutForPlayer(inside, () => et.fireEventTrigger(EventTrigger.Type.area));
            else
                et.fireEventTrigger(EventTrigger.Type.area);
        }

        /// <summary>Host: a held trigger also opens when the player it waited for died or left the location.</summary>
        internal static void TickHost()
        {
            if (_waiting.Count == 0 || Time.unscaledTime < _nextTick)
                return;
            _nextTick = Time.unscaledTime + TickInterval;
            _scratch.Clear();
            foreach (KeyValuePair<int, EventTriggers> kv in _waiting)
                _scratch.Add(kv.Key);
            for (int i = 0; i < _scratch.Count; i++)
            {
                if (!_waiting.TryGetValue(_scratch[i], out EventTriggers et))
                    continue;
                if (et == null)
                {
                    _waiting.Remove(_scratch[i]);
                    _passed.Remove(_scratch[i]);
                    continue;
                }
                TryRelease(et);
            }
        }
    }

    [HarmonyPatch(typeof(EventTriggers), "OnTriggerEnter", new[] { typeof(Collider) })]
    public static class LockBehindPartyGateEnterPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(EventTriggers __instance, Collider other)
        {
            if (__instance == null || other == null || !EventTriggersAuth.IsHost())
                return;
            LanNetworkManager net = ModRuntime.Network;
            if (Player.Instance != null && other.gameObject == Player.Instance.gameObject)
            {
                LockBehindPartyGate.NotePassed(__instance, net.LocalPlayerId);
                return;
            }
            // The stand-in's body only, as the player's own collider above (its child colliders
            // reach further than the body: EventTriggersProxyEnterPatch).
            RemotePlayerProxy proxy = other.GetComponent<RemotePlayerProxy>();
            if (proxy != null)
                LockBehindPartyGate.NotePassed(__instance, proxy.PlayerId);
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(EventTriggers __instance) => LockBehindPartyGate.TryRelease(__instance);
    }

    [HarmonyPatch(typeof(EventTriggers), nameof(EventTriggers.fireEventTrigger))]
    public static class LockBehindPartyGateFirePatch
    {
        private static bool Prefix(EventTriggers __instance, EventTrigger.Type TriggerType)
            => TriggerType != EventTrigger.Type.area || LockBehindPartyGate.AllowFire(__instance);
    }
}

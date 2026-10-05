using System.Collections;
using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Patches;
using HarmonyLib;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// One-shot scripted moves (a volume or a use that carries "the player" somewhere: the road
    /// home from the radio tower or the tree village, the border gate, the cottage, the elephants).
    /// Vanilla latches them after the first fire, which in single player is the only fire. In
    /// co-op the first player through was carried and the event latched: the next player walking
    /// in got nothing and could be stranded on the wrong side. An event made only of such moves
    /// (plus its screen, sound and message steps) now carries each player once. A plain hint (a
    /// message, maybe a sound) is likewise shown once to each player, not only to whoever walked
    /// past first. Rewards stay one-shot: items are one shared world.
    /// </summary>
    internal static class PerPlayerTransportOneShots
    {
        private static readonly Dictionary<int, HashSet<int>> _served = new Dictionary<int, HashSet<int>>(); // reset-in: Reset

        internal static void Reset() => _served.Clear();

        internal static bool Qualifies(GameEvents ges)
        {
            if (ges == null || ges.multipleFire || ges.events == null || ges.events.Count == 0)
                return false;
            bool moves = false, message = false, scene = false;
            for (int i = 0; i < ges.events.Count; i++)
            {
                GameEvent e = ges.events[i];
                if (e == null)
                    continue;
                if (IsMove(e))
                    moves = true;
                else if (e.type == GameEvent.Type.displayMessage)
                    message = true;
                else if (GameEventPersonalActorPatch.IsScenePresentation(e))
                    scene = true;
                else if (!IsPresentation(e))
                    return false;
            }
            // A move with its screen steps, or a plain hint (message and sound only).
            return moves || (message && !scene);
        }

        private static bool IsMove(GameEvent e)
        {
            switch (e.type)
            {
                case GameEvent.Type.transportPlayerToObject:
                    return !GameEventPersonalActorPatch.IsChapterJump(e);
                case GameEvent.Type.transportToOutsideLocation:
                case GameEvent.Type.returnToWorld:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsPresentation(GameEvent e)
        {
            if (GameEventPersonalActorPatch.IsScenePresentation(e))
                return true;
            switch (e.type)
            {
                case GameEvent.Type.sound:
                case GameEvent.Type.particle:
                case GameEvent.Type.displayMessage:
                case GameEvent.Type.debug:
                    return true;
                default:
                    return false;
            }
        }

        internal static int CurrentActor()
        {
            var net = ModRuntime.Network;
            return GeFireActorContext.PeekOr(net != null ? net.LocalPlayerId : 0);
        }

        internal static bool Served(int key, int actor)
            => _served.TryGetValue(key, out HashSet<int> set) && set.Contains(actor);

        internal static void Note(int key, int actor)
        {
            if (actor <= 0)
                return;
            if (!_served.TryGetValue(key, out HashSet<int> set))
            {
                set = new HashSet<int>();
                _served[key] = set;
            }
            set.Add(actor);
        }

        internal static bool IsHost()
            => ModRuntime.Network != null && ModRuntime.Network.IsConnected && ModRuntime.Network.Role == NetworkRole.Host;

        /// <summary>A latched move trigger in this volume that has not carried <paramref name="playerId"/> yet.</summary>
        internal static bool HasReopenable(EventTriggers triggers, int playerId)
        {
            if (triggers == null || triggers.eventTriggers == null || !IsHost())
                return false;
            for (int i = 0; i < triggers.eventTriggers.Count; i++)
            {
                EventTrigger t = triggers.eventTriggers[i];
                if (t != null && !t.disabled && !t.multipleFire && t.fired && Qualifies(t.gameEvents)
                    && !Served(t.gameEvents.GetInstanceID(), playerId))
                    return true;
            }
            return false;
        }
    }

    /// <summary>Host: the move's GameEvents run again for a player it has not carried.</summary>
    [HarmonyPatch(typeof(GameEvents), nameof(GameEvents.fire))]
    public static class PerPlayerTransportGameEventsPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(GameEvents __instance)
        {
            if (__instance == null || !__instance.fired || !PerPlayerTransportOneShots.IsHost())
                return;
            if (!PerPlayerTransportOneShots.Qualifies(__instance))
                return;
            if (!PerPlayerTransportOneShots.Served(__instance.GetInstanceID(), PerPlayerTransportOneShots.CurrentActor()))
                __instance.fired = false;
        }

        private static void Postfix(GameEvents __instance)
        {
            if (__instance != null && __instance.fired && PerPlayerTransportOneShots.IsHost()
                && PerPlayerTransportOneShots.Qualifies(__instance))
                PerPlayerTransportOneShots.Note(__instance.GetInstanceID(), PerPlayerTransportOneShots.CurrentActor());
        }
    }

    /// <summary>
    /// Host: the trigger in front of such a move also latches. For a player the move has not
    /// carried, the trigger's latch is lifted for its own check and put back if the fire does not
    /// go through (requirements, warmup).
    /// </summary>
    [HarmonyPatch(typeof(EventTrigger), nameof(EventTrigger.fire))]
    public static class PerPlayerTransportTriggerPatch
    {
        private static void Postfix(EventTrigger __instance, ref IEnumerator __result)
        {
            if (__instance == null || __result == null || !__instance.fired || __instance.multipleFire)
                return;
            if (!PerPlayerTransportOneShots.IsHost() || !PerPlayerTransportOneShots.Qualifies(__instance.gameEvents))
                return;
            int actor = PerPlayerTransportOneShots.CurrentActor();
            if (PerPlayerTransportOneShots.Served(__instance.gameEvents.GetInstanceID(), actor))
                return;
            __result = new Reopened(__result, __instance);
        }

        private sealed class Reopened : IEnumerator
        {
            private readonly IEnumerator _inner;
            private readonly EventTrigger _trigger;
            private int _steps;
            private bool _settled;

            internal Reopened(IEnumerator inner, EventTrigger trigger)
            {
                _inner = inner;
                _trigger = trigger;
            }

            public object Current => _inner.Current;

            public void Reset() => _inner.Reset();

            public bool MoveNext()
            {
                // The first step only waits out the trigger's delay; its latch check is in the second.
                if (!_settled && _steps == 1 && _trigger != null)
                    _trigger.fired = false;
                _steps++;
                bool more = _inner.MoveNext();
                if (!_settled && _steps >= 2 && _trigger != null)
                {
                    if (_trigger.fired)
                        _settled = true;          // went through: vanilla latched it again
                    else if (!more)
                    {
                        _trigger.fired = true;    // did not go through: keep the original latch
                        _settled = true;
                    }
                }
                return more;
            }
        }
    }
}

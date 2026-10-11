using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// The events that open and close a party dream are every dreamer's, not one player's. Vanilla
    /// runs a dream's opening (its pad's on-spawn and on-enter events) and its endings (the events with
    /// an end-dream step) on "the player"; in a party dream every dreamer is that player. Their steps
    /// aimed at the player body (dream_home's get-up-from-bed and its clothes change at the start and
    /// back at the end, the epilogue room's clothes, the bunker dream's "in the dream" flag) used to
    /// follow the one-player rule: the host's opening dressed only the host, and whoever used the
    /// exit was the only one changed back, so another dreamer woke in the dream's clothes.
    /// Seeds: events fired by an on-spawn, on-enter or on-exit trigger on the pad, and events with an
    /// end-dream step; plus every event those fire (fireEvent targets and world events, fireTrigger
    /// targets), as one scene.
    /// </summary>
    internal static class PartyDreamScene
    {
        private static int _padId; // process-scoped: instance id of the pad the set below was built for
        private static readonly HashSet<int> _scene = new HashSet<int>(); // process-scoped: rebuilt for each new pad

        /// <summary>The GameEvents on <paramref name="geObject"/> opens or closes the running party dream.</summary>
        internal static bool Owns(GameObject geObject)
        {
            if (geObject == null || !DreamSyncManager.IsDreamActive)
                return false;
            if (!DreamSyncManager.IsOnDreamPad(geObject.transform))
                return false;
            Transform pad = DreamSyncManager.GetLoadedDreamPad();
            GameEvents ge = geObject.GetComponent<GameEvents>();
            if (pad == null || ge == null)
                return false;
            if (_padId != pad.GetInstanceID())
                Build(pad);
            return _scene.Contains(ge.GetInstanceID());
        }

        private static int _ownSteps; // process-scoped: call-scoped, unwound by OwnStep.MoveNext's finally

        /// <summary>A step of the party dream's scene is running as this dreamer's own.</summary>
        internal static bool StepActive => _ownSteps > 0;

        /// <summary>Runs a scene step with <see cref="StepActive"/> set while its body executes.</summary>
        internal static IEnumerator AsOwnStep(IEnumerator inner)
            => inner != null ? new OwnStep(inner) : null;

        private sealed class OwnStep : IEnumerator
        {
            private readonly IEnumerator _inner;

            internal OwnStep(IEnumerator inner) => _inner = inner;

            public object Current => _inner.Current;

            public void Reset() => _inner.Reset();

            public bool MoveNext()
            {
                _ownSteps++;
                try { return _inner.MoveNext(); }
                finally { _ownSteps--; }
            }
        }

        private static void Build(Transform pad)
        {
            _padId = pad.GetInstanceID();
            _scene.Clear();
            GameEvents[] all = pad.GetComponentsInChildren<GameEvents>(true);
            var queue = new Queue<GameEvents>();

            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && HasEndDream(all[i]))
                    Add(all[i], queue);
            }
            EventTriggers[] triggers = pad.GetComponentsInChildren<EventTriggers>(true);
            for (int i = 0; i < triggers.Length; i++)
            {
                EventTriggers et = triggers[i];
                if (et == null || et.eventTriggers == null)
                    continue;
                for (int j = 0; j < et.eventTriggers.Count; j++)
                {
                    EventTrigger t = et.eventTriggers[j];
                    if (t != null && t.gameEvents != null && IsSceneTrigger(t.type))
                        Add(t.gameEvents, queue);
                }
            }

            while (queue.Count > 0)
            {
                GameEvents ge = queue.Dequeue();
                if (ge.events == null)
                    continue;
                for (int i = 0; i < ge.events.Count; i++)
                {
                    GameEvent step = ge.events[i];
                    if (step == null || step.disabled)
                        continue;
                    if (step.type == GameEvent.Type.fireEvent)
                    {
                        if (step.activeModifier)
                            AddWorldEvents(all, step.Value, queue);
                        else
                            AddTargets(step.targetGameObjects, queue);
                    }
                    else if (step.type == GameEvent.Type.fireTrigger)
                    {
                        AddTriggerTargets(step.targetGameObjects, queue);
                    }
                }
            }
        }

        private static bool IsSceneTrigger(EventTrigger.Type type)
            => type == EventTrigger.Type.onSpawned
               || type == EventTrigger.Type.onEnterLocation
               || type == EventTrigger.Type.onExitLocation;

        private static bool HasEndDream(GameEvents ge)
        {
            if (ge.events == null)
                return false;
            for (int i = 0; i < ge.events.Count; i++)
            {
                GameEvent step = ge.events[i];
                if (step != null && !step.disabled && step.type == GameEvent.Type.endDream)
                    return true;
            }
            return false;
        }

        private static void Add(GameEvents ge, Queue<GameEvents> queue)
        {
            if (ge != null && _scene.Add(ge.GetInstanceID()))
                queue.Enqueue(ge);
        }

        private static void AddWorldEvents(GameEvents[] all, string type, Queue<GameEvents> queue)
        {
            if (string.IsNullOrEmpty(type))
                return;
            for (int i = 0; i < all.Length; i++)
            {
                GameEvents ge = all[i];
                if (ge != null && ge.isWorldEvent && ge.worldEventType == type)
                    Add(ge, queue);
            }
        }

        private static void AddTargets(List<GameObject> targets, Queue<GameEvents> queue)
        {
            if (targets == null)
                return;
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] != null)
                    Add(targets[i].GetComponent<GameEvents>(), queue);
            }
        }

        private static void AddTriggerTargets(List<GameObject> targets, Queue<GameEvents> queue)
        {
            if (targets == null)
                return;
            for (int i = 0; i < targets.Count; i++)
            {
                EventTriggers et = targets[i] != null ? targets[i].GetComponent<EventTriggers>() : null;
                if (et == null || et.eventTriggers == null)
                    continue;
                for (int j = 0; j < et.eventTriggers.Count; j++)
                {
                    if (et.eventTriggers[j] != null)
                        Add(et.eventTriggers[j].gameEvents, queue);
                }
            }
        }
    }
}

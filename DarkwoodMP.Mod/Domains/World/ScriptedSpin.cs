using System.Collections.Generic;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// A repeating event that turns a free body a little at every fire (the bunker dream's
    /// carousel: one degree, then it fires itself again 0.07 s later). The wait ends on a frame, so
    /// the turn rate follows each machine's frame rate, and every machine ran its own loop: the
    /// angle drifted apart, and each side then sent the turning body to the other as a push of its
    /// own. The host's loop is the one that turns the body; a client leaves the step out and
    /// follows the host's poses, which the host sends while the loop runs, however far the
    /// players stand from the body.
    /// </summary>
    internal static class ScriptedSpin
    {
        /// <summary>How long after its last step a body is still sent from where it stands.</summary>
        private const float SentFor = 1f;

        private struct Driven
        {
            public Transform Body;
            public float LastStep;
        }

        private static readonly Dictionary<int, Driven> _driven = new Dictionary<int, Driven>(); // reset-in: Reset
        private static readonly List<int> _stale = new List<int>(4); // process-scoped: scratch

        internal static void Reset() => _driven.Clear();

        /// <summary>The step turns a free body from an event that fires again and again.</summary>
        internal static bool IsSpinStep(GameEvent step, GameObject eventObject)
        {
            if (step == null || eventObject == null
                || step.type != GameEvent.Type.gameObject
                || step.gameObjectModifyType != GameEvent.GameObjectModify.rotate
                || step.targetGameObjects == null)
                return false;
            GameEvents owner = eventObject.GetComponent<GameEvents>();
            if (owner == null || !owner.multipleFire)
                return false;
            for (int i = 0; i < step.targetGameObjects.Count; i++)
            {
                if (FreeBody(step.targetGameObjects[i]) != null)
                    return true;
            }
            return false;
        }

        /// <summary>Host: the step runs here; its bodies are sent to the others while it keeps running.</summary>
        internal static void NoteHostStep(GameEvent step)
        {
            for (int i = 0; i < step.targetGameObjects.Count; i++)
            {
                Rigidbody rb = FreeBody(step.targetGameObjects[i]);
                if (rb != null)
                    _driven[rb.gameObject.GetInstanceID()] = new Driven { Body = rb.transform, LastStep = Time.time };
            }
        }

        /// <summary>Host: where the bodies being turned stand, as extra places for the physics scan.</summary>
        internal static void AddScanCenters(List<Vector3> centers)
        {
            if (_driven.Count == 0)
                return;
            float now = Time.time;
            _stale.Clear();
            foreach (KeyValuePair<int, Driven> kv in _driven)
            {
                if (kv.Value.Body == null || now - kv.Value.LastStep > SentFor)
                    _stale.Add(kv.Key);
                else
                    centers.Add(kv.Value.Body.position);
            }
            for (int i = 0; i < _stale.Count; i++)
                _driven.Remove(_stale[i]);
        }

        private static Rigidbody FreeBody(GameObject target)
        {
            if (target == null)
                return null;
            Rigidbody rb = target.GetComponent<Rigidbody>();
            return rb != null && (!rb.isKinematic || WorldPhysicsSyncService.IsSyncLocked(rb)) ? rb : null;
        }
    }
}

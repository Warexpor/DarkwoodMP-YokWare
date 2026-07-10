using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// Force-stops vanilla <see cref="ItemSounds"/> scrape loops.
    /// Native path keeps playing while vel&gt;11 / angVel&gt;0.25 even after the
    /// player released — that residual is the bulk of the MP "stop delay".
    /// Stop is immediate (fade begins this frame); fade length matches vanilla 0.5s.
    /// </summary>
    public static class ItemMovingSoundHelper
    {
        /// <summary>
        /// Vanilla ItemSounds moving stop: <c>Stop(0.5f)</c>. Decision is immediate;
        /// only the soft tail lasts half a second.
        /// </summary>
        public const float IntentionalStopFade = 0.5f;

        /// <summary>
        /// After ForceStop, ignore scrape restarts for this long (late Unreliable
        /// body-push starts / residual DragSync / native ItemSounds re-arm).
        /// </summary>
        public const float PostStopSuppressSec = 0.45f;

        private static readonly Dictionary<string, float> _suppressUntil =
            new Dictionary<string, float>(StringComparer.Ordinal);

        /// <summary>True if scrape start should be ignored for this object name.</summary>
        public static bool IsScrapeSuppressed(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return false;
            if (!_suppressUntil.TryGetValue(objectName, out float until))
                return false;
            if (Time.unscaledTime >= until)
            {
                _suppressUntil.Remove(objectName);
                return false;
            }
            return true;
        }

        private static void ArmSuppress(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return;
            _suppressUntil[objectName] = Time.unscaledTime + PostStopSuppressSec;
        }

        public static void ResetSuppress()
        {
            _suppressUntil.Clear();
        }

        /// <summary>
        /// Stop native movingSoundAO *now* (with vanilla-length fade), clear the field,
        /// sleep the body so ItemSounds.Update will not re-arm on residual velocity.
        /// Also stops MOS + any AudioController objects for the same ids.
        /// </summary>
        public static void ForceStop(GameObject go, float fadeSec = IntentionalStopFade)
        {
            if (go == null) return;

            string objectName = go.name;
            ArmSuppress(objectName);

            ItemSounds sounds = go.GetComponent<ItemSounds>();
            string soundId = MovingObjectSoundService.ResolveMovingSoundId(sounds);

            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                // IsSleeping short-circuits ItemSounds.Update moving branch.
                if (!rb.isKinematic)
                    rb.Sleep();
            }

            if (sounds != null)
            {
                try
                {
                    var ao = Traverse.Create(sounds).Field("movingSoundAO").GetValue<AudioObject>();
                    if (ao != null)
                    {
                        ao.Stop(fadeSec);
                        Traverse.Create(sounds).Field("movingSoundAO").SetValue(null);
                    }
                }
                catch
                {
                    // Traverse failure — fall through to StopAllVariants
                }
            }

            MovingObjectSoundService.StopAllVariants(objectName, soundId, fadeSec);

            // Grass variant may differ from primary movingSound id.
            if (sounds != null
                && !string.IsNullOrEmpty(sounds.movingSound_grass)
                && !string.Equals(sounds.movingSound_grass, soundId, System.StringComparison.OrdinalIgnoreCase))
            {
                MovingObjectSoundService.StopAllVariants(objectName, sounds.movingSound_grass, fadeSec);
            }
        }

        public static void ForceStopByName(string objectName, float fadeSec = IntentionalStopFade)
        {
            if (string.IsNullOrEmpty(objectName)) return;
            ArmSuppress(objectName);
            GameObject go = GameObject.Find(objectName);
            if (go != null)
                ForceStop(go, fadeSec);
            else
                MovingObjectSoundService.StopAllVariants(objectName, null, fadeSec);
        }
    }
}

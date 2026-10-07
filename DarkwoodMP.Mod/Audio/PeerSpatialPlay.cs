using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// A peer's sound played here gets its own 3D falloff (it is often a 2D sound in the game,
    /// played for its owner only). The falloff has to be on the source before it starts:
    /// <c>AudioController.Play</c> starts the source, and the audio thread mixed its first
    /// moments with the prefab's own settings, at full 2D volume, before settings changed after
    /// the call reached it. Near the peer the sound itself covered that; toward the edge of its
    /// range, where the 3D sound is silent, only those first moments were heard (chopped,
    /// sharp bits of every footstep, shot and throw). The setup is handed to the play call and
    /// applied in <c>AudioObject</c>'s own start, after the game has set the source up.
    /// The pooled source gets the game's settings back when it returns to the pool: a later
    /// vanilla sound on it kept the peer falloff.
    /// </summary>
    internal static class PeerSpatialPlay
    {
        private static Action<AudioSource> _pending; // call-scoped: set around one Play call

        private struct Saved
        {
            public float SpatialBlend;
            public AnimationCurve SpatialCurve;
            public AudioRolloffMode Rolloff;
            public AnimationCurve RolloffCurve;
            public float Min;
            public float Max;
        }

        private static readonly Dictionary<AudioSource, Saved> _saved = new Dictionary<AudioSource, Saved>(); // process-scoped: pooled sources holding a peer falloff, until they return to the pool

        /// <summary>Runs <paramref name="play"/> with <paramref name="configure"/> applied to each source it starts.</summary>
        internal static AudioObject Play(Func<AudioObject> play, Action<AudioSource> configure)
        {
            Action<AudioSource> prev = _pending;
            _pending = configure;
            try { return play(); }
            finally { _pending = prev; }
        }

        internal static void BeforeStart(AudioObject ao)
        {
            if (_pending == null || ao == null)
                return;
            AudioSource src = ao.primaryAudioSource;
            if (src == null)
                return;
            if (!_saved.ContainsKey(src))
            {
                _saved[src] = new Saved
                {
                    SpatialBlend = src.spatialBlend,
                    SpatialCurve = src.GetCustomCurve(AudioSourceCurveType.SpatialBlend),
                    Rolloff = src.rolloffMode,
                    RolloffCurve = src.GetCustomCurve(AudioSourceCurveType.CustomRolloff),
                    Min = src.minDistance,
                    Max = src.maxDistance
                };
            }
            _pending(src);
        }

        /// <summary>Back to the game's settings, before the game restores its own item overrides.</summary>
        internal static void OnReturn(AudioObject ao)
        {
            AudioSource src = ao != null ? ao.primaryAudioSource : null;
            if (ReferenceEquals(src, null) || !_saved.TryGetValue(src, out Saved s))
                return;
            _saved.Remove(src);
            if (src == null)
                return;
            src.spatialBlend = s.SpatialBlend;
            if (s.SpatialCurve != null && s.SpatialCurve.length > 1)
                src.SetCustomCurve(AudioSourceCurveType.SpatialBlend, s.SpatialCurve);
            if (s.RolloffCurve != null && s.RolloffCurve.length > 0)
                src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, s.RolloffCurve);
            src.rolloffMode = s.Rolloff;
            src.minDistance = s.Min;
            src.maxDistance = s.Max;
        }

    }

    [HarmonyPatch(typeof(AudioObject), "_PlayDelayed")]
    public static class PeerSpatialPlayDelayedPatch
    {
        private static void Prefix(AudioObject __instance) => PeerSpatialPlay.BeforeStart(__instance);
    }

    [HarmonyPatch(typeof(AudioObject), "_PlayScheduled")]
    public static class PeerSpatialPlayScheduledPatch
    {
        private static void Prefix(AudioObject __instance) => PeerSpatialPlay.BeforeStart(__instance);
    }

    [HarmonyPatch(typeof(AudioObject), "OnDestroy")]
    public static class PeerSpatialReturnPatch
    {
        private static void Prefix(AudioObject __instance) => PeerSpatialPlay.OnReturn(__instance);
    }
}

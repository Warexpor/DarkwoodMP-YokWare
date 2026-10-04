using System;
using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// Scrape loop owner for networked drag/body-push. It is vanilla
    /// <see cref="ItemSounds"/>.Update's moving branch, driven by network motion instead of the
    /// local rigidbody:
    /// - start: <c>AudioController.Play(id, transform, volumeModifier)</c>, with the id chosen
    ///   by ground exactly like vanilla (<c>movingSound</c>, or <c>movingSound_grass</c> off a
    ///   Ground), so the clip pick, Sound volume slider, indoor reverb and wall occlusion are
    ///   the game's own;
    /// - surface change: stop the old loop with the vanilla 0.5 s fade and start the new id;
    /// - stop: <c>AudioObject.Stop(0.5f)</c> the same frame motion ends.
    /// MP lag came from *late stop decisions* (1s stale, multi-tick wait),
    /// not from the vanilla 0.5s fade itself.
    /// </summary>
    public static class MovingObjectSoundService
    {
        // Fire stop on first stationary tick once net says "not moving".
        private const int StationaryTicksBeforeFade = 1;
        /// <summary>Matches ItemSounds: movingSoundAO.Stop(0.5f). Fade starts immediately.</summary>
        public const float VanillaMovingStopFade = 0.5f;
        public const float IntentionalStopFade = ItemMovingSoundHelper.IntentionalStopFade;
        private const float DefaultFadeSeconds = VanillaMovingStopFade;

        private sealed class Entry
        {
            public AudioObject Ao;
            public GameObject Host;
            public string SoundId;
            public int StationaryTicks;
            public bool Fading;
        }

        private static readonly Dictionary<string, Entry> _byName =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly List<string> _reap = new List<string>(8); // process-scoped: scratch

        public static void Reset()
        {
            foreach (var kv in _byName)
            {
                if (Owns(kv.Value))
                    kv.Value.Ao.Stop();
            }
            _byName.Clear();
        }

        /// <summary>
        /// The loop vanilla ItemSounds.Update would play for this object where it stands:
        /// <c>movingSound</c> on a Ground, <c>movingSound_grass</c> off one; nothing without a
        /// <c>movingSound</c> (vanilla returns early).
        /// </summary>
        public static string ResolveMovingSoundId(ItemSounds sounds)
        {
            if (sounds == null || string.IsNullOrEmpty(sounds.movingSound))
                return null;
            if (!string.IsNullOrEmpty(sounds.movingSound_grass)
                && Ground.getGround(sounds.transform.position) == null)
                return sounds.movingSound_grass;
            return sounds.movingSound;
        }

        /// <summary>Object is moving; start or keep the loop and cancel any fade.
        /// MOS path always means remote ownership: suppress native ItemSounds.</summary>
        public static void NoteMoving(GameObject go, string objectName, ItemSounds sounds)
        {
            if (go == null || string.IsNullOrEmpty(objectName) || sounds == null)
                return;
            // After intentional stop, ignore late network/physics residual restarts (5.2).
            if (ItemMovingSoundHelper.IsScrapeSuppressed(objectName))
                return;
            // Local pusher/dragger owns native ItemSounds; never arm MOS on top.
            // Do NOT use proximity/"live RB" heuristics here: that silenced host→client
            // observer scrape when the client stood near a free-body host was pushing.
            if (ItemMovingSoundHelper.IsLocalPushOrDragOwner(objectName)
                || ItemMovingSoundHelper.HasRecentClientPhysicsSent(objectName)
                || ItemMovingSoundHelper.HasRecentPushAuthority(objectName))
            {
                if (IsPlaying(objectName) || IsFading(objectName))
                    StopImmediate(objectName);
                ItemMovingSoundHelper.ClearRemoteScrape(objectName);
                return;
            }

            string soundId = ResolveMovingSoundId(sounds);
            if (string.IsNullOrEmpty(soundId))
                return;

            ItemMovingSoundHelper.MarkRemoteScrape(objectName);
            EnsurePlaying(go, objectName, soundId, sounds.volumeModifier);
        }

        // IsLocalSimOwner was removed because the proximity/live-RB heuristic silenced host-to-client scrape.

        /// <summary>Object barely moved this tick; build hysteresis, then fade.</summary>
        public static void NoteStationary(string objectName)
        {
            if (string.IsNullOrEmpty(objectName))
                return;
            if (!_byName.TryGetValue(objectName, out Entry e) || !Owns(e))
                return;
            if (e.Fading)
                return;

            e.StationaryTicks++;
            if (e.StationaryTicks >= StationaryTicksBeforeFade)
                BeginFade(objectName, DefaultFadeSeconds);
        }

        /// <param name="volume">The AudioController play volume (vanilla passes <c>volumeModifier</c>).</param>
        public static void EnsurePlaying(GameObject go, string objectName, string soundId, float volume)
        {
            if (go == null || string.IsNullOrEmpty(objectName) || string.IsNullOrEmpty(soundId))
                return;
            if (ItemMovingSoundHelper.IsScrapeSuppressed(objectName))
                return;
            if (ItemMovingSoundHelper.IsLocalPushOrDragOwner(objectName)
                || ItemMovingSoundHelper.HasRecentClientPhysicsSent(objectName))
            {
                if (IsPlaying(objectName) || IsFading(objectName))
                    StopImmediate(objectName);
                ItemMovingSoundHelper.ClearRemoteScrape(objectName);
                return;
            }

            // MOS is the remote-owner path; keep native ItemSounds suppressed.
            ItemMovingSoundHelper.MarkRemoteScrape(objectName);

            if (_byName.TryGetValue(objectName, out Entry existing))
            {
                existing.StationaryTicks = 0;
                if (Owns(existing) && !existing.Fading
                    && existing.Host == go && existing.SoundId == soundId)
                    return;

                // Fading, surface changed or host replaced: vanilla lets the old loop finish its
                // 0.5 s fade and starts a fresh one.
                if (Owns(existing) && !existing.Fading)
                    existing.Ao.Stop(VanillaMovingStopFade);
                _byName.Remove(objectName);
            }

            AudioObject ao;
            // Our own replay of a remote scrape: the AudioController forward patches must not
            // send it back out.
            bool prevNet = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try { ao = AudioController.Play(soundId, go.transform, Mathf.Max(0f, volume)); }
            finally { TraverseHack.SetExplicitFlag(prevNet); }
            if (ao == null)
                return; // out of hearing range (AudioSuppression) or unknown id; next motion tick retries

            _byName[objectName] = new Entry
            {
                Ao = ao,
                Host = go,
                SoundId = soundId,
                StationaryTicks = 0,
                Fading = false
            };
        }

        public static void BeginFade(string objectName, float fadeSeconds = DefaultFadeSeconds)
        {
            if (string.IsNullOrEmpty(objectName))
                return;
            if (!_byName.TryGetValue(objectName, out Entry e) || !Owns(e))
                return;
            if (e.Fading)
                return;

            if (fadeSeconds <= 0f)
            {
                StopImmediate(objectName);
                return;
            }

            e.Ao.Stop(fadeSeconds);
            e.Fading = true;
            e.StationaryTicks = 0;
        }

        public static void StopImmediate(string objectName)
        {
            if (string.IsNullOrEmpty(objectName))
                return;
            if (_byName.TryGetValue(objectName, out Entry e))
            {
                if (Owns(e))
                    e.Ao.Stop();
                _byName.Remove(objectName);
            }
            ItemMovingSoundHelper.ClearRemoteScrape(objectName);
        }

        /// <summary>True if MOS currently owns a (possibly fading) loop for this name.</summary>
        public static bool IsPlaying(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return false;
            return _byName.TryGetValue(objectName, out Entry e) && Owns(e);
        }

        /// <summary>True while a scrape fade-out is in progress (not fully stopped).</summary>
        public static bool IsFading(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return false;
            return _byName.TryGetValue(objectName, out Entry e) && e.Fading && Owns(e);
        }

        /// <summary>
        /// Stop the MOS loop. With <paramref name="owner"/>, also stop every AudioController
        /// loop of <paramref name="soundId"/> playing on that object (a native ItemSounds AO whose
        /// field was already cleared); only for intentional local ForceStop (drag release).
        /// Never a scene-wide kill by id: other objects share the same scrape ids.
        /// </summary>
        public static void StopAllVariants(string objectName, string soundId, float fadeSec = DefaultFadeSeconds,
            Transform owner = null)
        {
            if (fadeSec <= 0f)
                StopImmediate(objectName);
            else
                BeginFade(objectName, fadeSec);

            if (owner == null || string.IsNullOrEmpty(soundId))
                return;

            try
            {
                var playing = AudioController.GetPlayingAudioObjects(soundId);
                if (playing == null) return;
                foreach (var ao in playing)
                {
                    if (ao != null && ao.transform.parent == owner && !ao.isFadingOut)
                        ao.Stop(fadeSec); // same as ItemSounds.Update stop path
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Audio, "StopAllVariants failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Network/remote intentional stop: decide *now*, vanilla 0.5s fade from that frame.
        /// MOS only. Do not globally kill AudioController clips because native ItemSounds shares IDs.
        /// </summary>
        public static void StopNetwork(string objectName, string soundId = null)
        {
            StopNetworkMosOnly(objectName, VanillaMovingStopFade);
        }

        /// <summary>Fade or stop only the MOS entry; never AudioController.GetPlayingAudioObjects.</summary>
        public static void StopNetworkMosOnly(string objectName, float fadeSec)
        {
            if (string.IsNullOrEmpty(objectName)) return;
            if (fadeSec <= 0f)
                StopImmediate(objectName);
            else
                BeginFade(objectName, fadeSec);
        }

        /// <summary>Call once per frame (from physics interp LateUpdate path): reap finished loops.</summary>
        public static void Tick()
        {
            if (_byName.Count == 0)
                return;

            _reap.Clear();
            foreach (var kv in _byName)
            {
                if (!Owns(kv.Value))
                    _reap.Add(kv.Key);
            }
            for (int i = 0; i < _reap.Count; i++)
            {
                ItemMovingSoundHelper.ClearRemoteScrape(_reap[i]);
                _byName.Remove(_reap[i]);
            }
        }

        /// <summary>
        /// AudioObjects are pooled: a held reference stays ours only while it still plays our id
        /// on our object. A finished or recycled one is never stopped or reused through it.
        /// </summary>
        private static bool Owns(Entry e)
        {
            if (e == null || e.Ao == null || e.Host == null)
                return false;
            AudioObject ao = e.Ao;
            return ao.audioID == e.SoundId && ao.transform.parent == e.Host.transform
                && (ao.IsPlaying() || ao.IsPaused());
        }
    }
}

using System;
using System.Collections.Generic;
using DWMPHorde.Logging;
using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// Scrape loop owner for networked drag/body-push.
    /// Mirrors vanilla <see cref="ItemSounds"/> moving path:
    /// - start when motion is reported
    /// - on stop: <c>AudioObject.Stop(0.5f)</c> the same frame motion ends
    ///   (ItemSounds.Update else-branch). Do not invent a shorter fade.
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
        private const int OcclusionLayerMask = GameplayConstants.DefaultOcclusionLayerMask;

        private sealed class Entry
        {
            public AudioSource Source;
            public GameObject Host;
            public string SoundId;
            public float BaseVolume;
            public int StationaryTicks;
            public bool Fading;
            public float FadeStartVol;
            public float FadeEndTime;
            public float FadeDuration;
        }

        private static readonly Dictionary<string, Entry> _byName =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        public static void Reset()
        {
            foreach (var kv in _byName)
            {
                if (kv.Value?.Source != null)
                    UnityEngine.Object.Destroy(kv.Value.Source);
            }
            _byName.Clear();
        }

        public static string ResolveMovingSoundId(ItemSounds sounds)
        {
            if (sounds == null) return null;
            if (!string.IsNullOrEmpty(sounds.movingSound))
                return sounds.movingSound;
            if (!string.IsNullOrEmpty(sounds.movingSound_grass))
                return sounds.movingSound_grass;
            return null;
        }

        /// <summary>Object is moving — start/keep loop; cancel any fade.
        /// MOS path always means remote ownership: suppress native ItemSounds.</summary>
        public static void NoteMoving(GameObject go, string objectName, ItemSounds sounds)
        {
            if (go == null || string.IsNullOrEmpty(objectName) || sounds == null)
                return;
            // After intentional stop, ignore late network/physics residual restarts (5.2).
            if (ItemMovingSoundHelper.IsScrapeSuppressed(objectName))
                return;

            string soundId = ResolveMovingSoundId(sounds);
            if (string.IsNullOrEmpty(soundId))
                return;

            ItemMovingSoundHelper.MarkRemoteScrape(objectName);
            float volume = Mathf.Clamp01(sounds.volumeModifier * LocalAudioService.GetItemVolumeScale(soundId));
            EnsurePlaying(go, objectName, soundId, volume);
        }

        /// <summary>Object barely moved this tick — build hysteresis then fade.</summary>
        public static void NoteStationary(string objectName)
        {
            if (string.IsNullOrEmpty(objectName))
                return;
            if (!_byName.TryGetValue(objectName, out Entry e) || e == null || e.Source == null)
                return;
            if (e.Fading)
                return;

            e.StationaryTicks++;
            if (e.StationaryTicks >= StationaryTicksBeforeFade)
                BeginFade(objectName, DefaultFadeSeconds);
        }

        public static void EnsurePlaying(GameObject go, string objectName, string soundId, float volume)
        {
            if (go == null || string.IsNullOrEmpty(objectName) || string.IsNullOrEmpty(soundId))
                return;
            if (ItemMovingSoundHelper.IsScrapeSuppressed(objectName))
                return;

            // MOS is the remote-owner path — keep native ItemSounds suppressed.
            ItemMovingSoundHelper.MarkRemoteScrape(objectName);

            if (_byName.TryGetValue(objectName, out Entry existing) && existing != null)
            {
                if (existing.Fading && existing.Source != null)
                {
                    existing.Source.volume = existing.BaseVolume > 0f ? existing.BaseVolume : volume;
                    existing.Fading = false;
                    ModLog.Info(LogCat.Audio, "[MOS] re-arm canceled fade for " + objectName);
                }

                existing.StationaryTicks = 0;

                if (existing.Source != null && existing.Source.isPlaying
                    && existing.Host == go && existing.SoundId == soundId)
                {
                    existing.BaseVolume = volume;
                    if (!existing.Fading)
                        existing.Source.volume = volume;
                    return;
                }

                // Host/source stale — rebuild
                if (existing.Source != null)
                    UnityEngine.Object.Destroy(existing.Source);
                _byName.Remove(objectName);
            }

            AudioClip clip = LocalAudioService.ResolveClip(soundId);
            if (clip == null)
                return;

            var src = go.AddComponent<AudioSource>();
            src.clip = clip;
            src.loop = true;
            src.volume = Mathf.Clamp01(volume);
            src.spatialBlend = 1f;
            src.minDistance = LocalAudioService.DefaultMinSpatialDistance;
            src.maxDistance = LocalAudioService.DefaultMaxSpatialDistance;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.playOnAwake = false;
            src.Play();

            _byName[objectName] = new Entry
            {
                Source = src,
                Host = go,
                SoundId = soundId,
                BaseVolume = src.volume,
                StationaryTicks = 0,
                Fading = false
            };
        }

        public static void BeginFade(string objectName, float fadeSeconds = DefaultFadeSeconds)
        {
            if (string.IsNullOrEmpty(objectName))
                return;
            if (!_byName.TryGetValue(objectName, out Entry e) || e == null || e.Source == null)
                return;
            if (e.Fading)
                return;

            if (fadeSeconds <= 0f)
            {
                StopImmediate(objectName);
                return;
            }

            e.Fading = true;
            e.FadeStartVol = e.Source.volume;
            e.FadeDuration = Mathf.Max(0.01f, fadeSeconds);
            e.FadeEndTime = Time.time + e.FadeDuration;
            e.StationaryTicks = 0;
        }

        public static void StopImmediate(string objectName)
        {
            if (string.IsNullOrEmpty(objectName))
                return;
            if (!_byName.TryGetValue(objectName, out Entry e))
            {
                ItemMovingSoundHelper.ClearRemoteScrape(objectName);
                return;
            }

            if (e?.Source != null)
            {
                e.Source.Stop();
                UnityEngine.Object.Destroy(e.Source);
            }
            _byName.Remove(objectName);
            ItemMovingSoundHelper.ClearRemoteScrape(objectName);
        }

        /// <summary>True if MOS currently owns a (possibly fading) source for this name.</summary>
        public static bool IsPlaying(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return false;
            return _byName.TryGetValue(objectName, out Entry e)
                && e != null && e.Source != null && e.Source.isPlaying;
        }

        /// <summary>True while a scrape fade-out is in progress (not fully stopped).</summary>
        public static bool IsFading(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return false;
            return _byName.TryGetValue(objectName, out Entry e)
                && e != null && e.Fading && e.Source != null;
        }

        /// <summary>
        /// Stop MOS source + any AudioController objects for the same clip id.
        /// Fade defaults to vanilla ItemSounds moving stop (0.5s).
        /// </summary>
        public static void StopAllVariants(string objectName, string soundId, float fadeSec = DefaultFadeSeconds)
        {
            if (fadeSec <= 0f)
                StopImmediate(objectName);
            else
                BeginFade(objectName, fadeSec);

            if (string.IsNullOrEmpty(soundId))
                return;

            try
            {
                var playing = AudioController.GetPlayingAudioObjects(soundId);
                if (playing == null) return;
                foreach (var ao in playing)
                {
                    if (ao != null)
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
        /// </summary>
        public static void StopNetwork(string objectName, string soundId = null)
        {
            StopAllVariants(objectName, soundId, VanillaMovingStopFade);
        }

        /// <summary>Call once per frame (from physics interp LateUpdate path).</summary>
        public static void Tick()
        {
            if (_byName.Count == 0)
                return;

            float now = Time.time;
            List<string> remove = null;

            foreach (var kv in _byName)
            {
                Entry e = kv.Value;
                if (e == null || e.Source == null)
                {
                    if (remove == null) remove = new List<string>();
                    remove.Add(kv.Key);
                    continue;
                }

                if (e.Fading)
                {
                    if (now >= e.FadeEndTime)
                    {
                        e.Source.Stop();
                        UnityEngine.Object.Destroy(e.Source);
                        if (remove == null) remove = new List<string>();
                        remove.Add(kv.Key);
                        continue;
                    }

                    float t = 1f - ((e.FadeEndTime - now) / e.FadeDuration);
                    e.Source.volume = Mathf.Lerp(e.FadeStartVol, 0f, Mathf.Clamp01(t));
                }
                else
                {
                    ApplyOcclusion(e.Source);
                }
            }

            if (remove != null)
            {
                for (int i = 0; i < remove.Count; i++)
                {
                    ItemMovingSoundHelper.ClearRemoteScrape(remove[i]);
                    _byName.Remove(remove[i]);
                }
            }
        }

        private static void ApplyOcclusion(AudioSource src)
        {
            if (src == null || !src.isPlaying)
                return;

            Vector3 listener = LocalAudioService.GetListenPosition();
            Vector3 srcPos = src.transform.position;
            Vector3 dir = listener - srcPos;
            float dist = dir.magnitude;
            if (dist < 0.1f)
                return;

            bool occluded = Physics.Raycast(srcPos, dir.normalized, dist, OcclusionLayerMask);
            var lpf = src.GetComponent<AudioLowPassFilter>();
            if (occluded)
            {
                if (lpf == null)
                {
                    lpf = src.gameObject.AddComponent<AudioLowPassFilter>();
                    lpf.cutoffFrequency = 1500f;
                }
            }
            else if (lpf != null)
            {
                UnityEngine.Object.Destroy(lpf);
            }
        }
    }
}

using DWMPHorde.Audio;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Sync
{
    internal static class DreamAudioPlayer
    {
        private static AudioSource[] _sources;
        private static int _nextSource;
        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized) return;
            var go = new GameObject("DreamAudioPlayer");
            Object.DontDestroyOnLoad(go);
            _sources = new AudioSource[12];
            for (int i = 0; i < _sources.Length; i++)
            {
                _sources[i] = go.AddComponent<AudioSource>();
                _sources[i].playOnAwake = false;
                _sources[i].spatialBlend = 1f;
                _sources[i].rolloffMode = AudioRolloffMode.Linear;
                _sources[i].minDistance = LocalAudioService.DefaultMinSpatialDistance;
                _sources[i].maxDistance = LocalAudioService.DefaultMaxSpatialDistance;
            }
            _initialized = true;
            ModRuntime.LegacyInfo("[DreamAudioPlayer] Initialized with 12 sources");
        }

        public static void PlayForwardedAudio(DreamAudioMessage msg)
        {
            Initialize();

            if (string.IsNullOrEmpty(msg.AudioID)) return;

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            if (pos != Vector3.zero
                && !LocalAudioService.IsNearListener(pos, LocalAudioService.DefaultMaxAudioDistance))
                return;

            AudioClip clip = LocalAudioService.ResolveClip(msg.AudioID);
            if (clip == null)
            {
                ModRuntime.Log?.LogWarning("[DreamAudioPlayer] Could not resolve clip for: " + msg.AudioID);
                return;
            }

            var source = _sources[_nextSource % _sources.Length];
            _nextSource++;

            // Spatial one-shot: keep source.volume at 1 and scale via PlayOneShot so
            // msg.Volume is not applied twice (was volume * 1f on both paths).
            float vol = msg.Volume <= 0f ? 1f : Mathf.Clamp01(msg.Volume);
            float itemScale = LocalAudioService.GetItemVolumeScale(msg.AudioID);
            source.transform.position = pos;
            source.volume = 1f;
            source.pitch = msg.Pitch <= 0f ? 1f : msg.Pitch;
            source.spatialBlend = 1f;
            source.PlayOneShot(clip, Mathf.Clamp01(vol * itemScale));
        }

        public static void Cleanup()
        {
            if (!_initialized) return;
            if (_sources != null && _sources.Length > 0 && _sources[0] != null)
            {
                var go = _sources[0].gameObject;
                if (go != null)
                    Object.Destroy(go);
            }
            _sources = null;
            _initialized = false;
            _nextSource = 0;
            LocalAudioService.ClearClipCache();
        }
    }
}

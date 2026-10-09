using System;
using System.Collections.Generic;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using Steamworks;
using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// Steam Voice capture/playback over Horde wire (LAN or Steam SNS session).
    /// Requires Steam client logged on for codec; transport is independent.
    /// </summary>
    public static partial class VoiceChatService
    {
        /// <summary>
        /// A speaker that has had no voice data this long and an empty buffer is reaped (it is
        /// re-created on the next packet). Peer leave is handled sooner by <see cref="RemoveSpeaker"/>.
        /// </summary>
        private const float IdleReapSec = 30f;

        /// <summary>A whisper carries this share of <c>VoiceMaxDistance</c>; a shout all of it.</summary>
        private const float QuietRangeShare = 0.35f;
        /// <summary>Seconds for the loudness envelope to fall from a shout to silence (it rises at once).</summary>
        private const float LevelReleaseSec = 1.5f;
        /// <summary>Vanilla's sound-blocking layers (<c>Character.heardSound</c>: walls and solid world).</summary>
        private const int OcclusionMask = 32769;
        /// <summary>Close by, a voice is mostly in the middle; it pans fully from this far out.</summary>
        private const float FullPanDistance = 300f;

        private static Speaker EnsureSpeaker(int id)
        {
            if (_speakers.TryGetValue(id, out Speaker existing) && existing.Go != null)
                return existing;

            if (_root == null)
            {
                _root = new GameObject("YokWare_Voice");
                UnityEngine.Object.DontDestroyOnLoad(_root);
            }

            var s = new Speaker
            {
                Id = id,
                Ring = new float[Math.Max((int)_sampleRate * 4, 44100)]
            };
            s.Go = new GameObject("YokWare_Voice_" + id);
            s.Go.transform.SetParent(_root.transform, false);
            s.Src = s.Go.AddComponent<AudioSource>();
            s.Beh = s.Go.AddComponent<VoiceSpeakerBehaviour>();
            s.Beh.S = s;
            s.Beh.SrcRate = (int)_sampleRate;
            s.Muffle = s.Go.AddComponent<AudioLowPassFilter>();
            s.Muffle.cutoffFrequency = 22000f;
            s.Src.clip = CarrierClip();
            s.Src.loop = true;
            s.Src.playOnAwake = false;
            s.Src.volume = 1f;
            // Panned toward where the talker stands; the distance falloff is ours (by how loud they
            // spoke), so Unity's own rolloff is flat and doppler off (it would bend a moving voice).
            s.Src.spatialBlend = 0f;
            s.Src.rolloffMode = AudioRolloffMode.Custom;
            s.Src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, AnimationCurve.Constant(0f, 1f, 1f));
            s.Src.minDistance = 1f;
            s.Src.maxDistance = 100000f;
            s.Src.dopplerLevel = 0f;
            s.Src.spread = 60f;
            s.Src.Play();
            _speakers[id] = s;
            return s;
        }

        private static void UpdateSpeakers()
        {
            if (Time.unscaledTime >= _nextStatsLog)
            {
                _nextStatsLog = Time.unscaledTime + 10f;
                if (_txPackets > 0 || HasRecentRx())
                {
                    ModLog.Trace(LogCat.Audio, () =>
                    {
                        string line = "[Voice] 10s tx=" + _txPackets;
                        foreach (Speaker s in _speakers.Values)
                        {
                            int buffered, under;
                            lock (s.Lock) { buffered = s.Buffered; under = s.Underruns; s.PacketsIn = 0; s.Underruns = 0; }
                            line += " | p" + s.Id + " buf=" + buffered + " under=" + under
                                + " level=" + s.LevelEnv.ToString("0.00") + (s.RadioMode ? " radio" : "")
                                + (s.OccludedNow ? " walled" : "");
                        }
                        return line;
                    });
                }
                _txPackets = 0;
            }

            if (_speakers.Count == 0)
                return;

            float dt = Time.unscaledDeltaTime;
            float vol = ModConfig.VoiceVolume?.Value ?? 1f;
            float rangeFull = ModConfig.VoiceFullVolumeDistance?.Value ?? 150f;
            float rangeMax = ModConfig.VoiceMaxDistance?.Value ?? LocalAudioService.DefaultMaxAudioDistance;
            // Heard from where this player listens (the followed player while spectating).
            Vector3 listen = LocalAudioService.GetListenPosition();

            _reap.Clear();
            foreach (Speaker s in _speakers.Values)
            {
                if (s.Go == null || (Time.unscaledTime - s.LastData > IdleReapSec && s.Buffered == 0))
                {
                    _reap.Add(s.Id);
                    continue;
                }

                float since = Time.unscaledTime - s.LastData;
                lock (s.Lock)
                {
                    if (s.Priming && s.Buffered > 0 && since > PrimeFlushSec)
                        s.PrimeRelease = true;
                }

                if (s.RadioWasActive && since > 0.3f)
                {
                    s.RadioWasActive = false;
                    WriteSquelch(s, open: false);
                }

                // Loudness: up at once with the voice, down slowly between words.
                float level = since > 0.4f ? 0f : s.Level;
                s.LevelEnv = level > s.LevelEnv ? level : Mathf.MoveTowards(s.LevelEnv, level, dt / LevelReleaseSec);

                float proxVol = 0f;
                float cutoff = 22000f;
                float dist = float.MaxValue;
                var proxy = ModRuntime.Network?.GetProxy(s.Id);
                if (Player.Instance != null && proxy != null && proxy.transform != null)
                {
                    Vector3 b = proxy.transform.position;
                    s.Go.transform.position = b;
                    dist = Vector2.Distance(new Vector2(listen.x, listen.z), new Vector2(b.x, b.z));

                    // The louder they spoke, the farther it carries.
                    float loud = Mathf.Lerp(QuietRangeShare, 1f, s.LevelEnv);
                    float range = Mathf.Max(rangeMax * loud, 60f);
                    float full = Mathf.Min(rangeFull * loud, range * 0.5f);
                    float t = Mathf.InverseLerp(range, full, dist);
                    proxVol = t * vol;
                    cutoff = Mathf.Lerp(4500f, 22000f, t);

                    if (Time.unscaledTime >= s.NextOcclusionCheck)
                    {
                        s.NextOcclusionCheck = Time.unscaledTime + 0.15f;
                        s.OccludedNow = IsOccluded(listen, b);
                    }
                    s.Occlusion = Mathf.MoveTowards(s.Occlusion, s.OccludedNow ? 1f : 0f, dt * 5f);
                    proxVol *= Mathf.Lerp(1f, 0.55f, s.Occlusion);
                    cutoff = Mathf.Lerp(cutoff, Mathf.Min(cutoff, 900f), s.Occlusion);
                }

                float radioVol = (s.Walkie && _localWalkie) ? vol * 0.9f : 0f;
                bool radio = s.RadioMode
                    ? (radioVol > proxVol * 0.85f)
                    : (radioVol > proxVol * 1.15f);
                s.RadioMode = radio;
                // Static grows with the distance between the radios (and is at its worst when the
                // talker is not in this world: another location, a dream).
                s.RadioHiss = Mathf.Lerp(RadioHissNear, RadioHissFar,
                    dist == float.MaxValue ? 1f : Mathf.InverseLerp(500f, 4000f, dist));
                if (s.Beh != null)
                    s.Beh.Volume = Mathf.Clamp01(radio ? radioVol : proxVol);
                if (s.Muffle != null)
                {
                    float target = radio ? 22000f : cutoff;
                    s.SmoothCutoff = Mathf.Lerp(s.SmoothCutoff, target, Mathf.Clamp01(dt * 8f));
                    s.Muffle.cutoffFrequency = s.SmoothCutoff;
                }
                if (s.Src != null)
                {
                    // The radio is in this player's own hands: no pan. A voice in the room pans
                    // toward the talker, gently when they are close.
                    float blend = radio || dist == float.MaxValue ? 0f : Mathf.Lerp(0.35f, 1f, Mathf.Clamp01(dist / FullPanDistance));
                    s.Blend = Mathf.MoveTowards(s.Blend, blend, dt * 3f);
                    s.Src.spatialBlend = s.Blend;
                }
            }

            foreach (int id in _reap)
            {
                if (_speakers.TryGetValue(id, out Speaker dead) && dead.Go != null)
                    UnityEngine.Object.Destroy(dead.Go);
                _speakers.Remove(id);
            }
        }

        private static bool HasRecentRx()
        {
            foreach (Speaker s in _speakers.Values)
            {
                if (s.PacketsIn > 0)
                    return true;
            }
            return false;
        }

        private static void UpdateLocalWalkie()
        {
            if (Time.unscaledTime < _nextWalkieCheck)
                return;
            _nextWalkieCheck = Time.unscaledTime + 0.5f;
            _localWalkie = false;
            try
            {
                if (Player.Instance == null)
                    return;
                string name = ModConfig.WalkieItemName?.Value ?? "walkie_talkie";
                if (string.IsNullOrEmpty(name))
                    return;
                _localWalkie =
                    (Player.Instance.Inventory != null && Player.Instance.Inventory.getItemAmount(name) > 0)
                    || (Player.Instance.Hotbar != null && Player.Instance.Hotbar.getItemAmount(name) > 0);
            }
            catch { /* ignore */ }
        }

        /// <summary>Steam may initialise or log on after the mod loads: retry a negative result.</summary>
        private const float SteamRecheckSec = 5f;

        private static bool SteamAvailable()
        {
            // A positive result is cached; a negative one is re-probed every few seconds.
            if (_steamOk || Time.unscaledTime < _nextSteamCheck)
                return _steamOk;

            _nextSteamCheck = Time.unscaledTime + SteamRecheckSec;
            try
            {
                _steamOk = SteamManager.Initialized && SteamUser.BLoggedOn();
            }
            catch
            {
                _steamOk = false;
            }

            if (!_steamOk && !_steamWarned)
            {
                _steamWarned = true;
                ModLog.Event(LogCat.Audio, "Steam unavailable — voice chat disabled (will retry)");
            }
            else if (_steamOk && _steamWarned)
            {
                _steamWarned = false;
                ModLog.Event(LogCat.Audio, "Steam available — voice chat enabled");
            }
            return _steamOk;
        }

        /// <summary>
        /// A wall between listener and talker, by vanilla's own test for whether a creature hears a
        /// sound through something (<c>Character.heardSound</c>: one ray on its blocking layers).
        /// </summary>
        private static bool IsOccluded(Vector3 from, Vector3 to)
        {
            try
            {
                Vector3 delta = to - from;
                float mag = delta.magnitude;
                if (mag < 1f)
                    return false;
                return Physics.Raycast(from, delta / mag, mag, OcclusionMask, QueryTriggerInteraction.Ignore);
            }
            catch
            {
                return false;
            }
        }
    }
}

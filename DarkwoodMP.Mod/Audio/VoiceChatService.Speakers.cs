using System;
using System.Collections.Generic;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using LiteNetLib;
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
            s.Src.spatialBlend = 0f;
            s.Src.volume = 1f;
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
                            int buffered;
                            lock (s.Lock) { buffered = s.Buffered; s.PacketsIn = 0; }
                            line += " | p" + s.Id + " buf=" + buffered;
                        }
                        return line;
                    });
                }
                _txPackets = 0;
            }

            if (_speakers.Count == 0)
                return;

            float vol = ModConfig.VoiceVolume?.Value ?? 1f;
            float rangeFull = ModConfig.VoiceRangeFull?.Value ?? 8f;
            float rangeMax = ModConfig.VoiceRangeMax?.Value ?? 28f;

            _reap.Clear();
            foreach (Speaker s in _speakers.Values)
            {
                if (s.Go == null || (Time.unscaledTime - s.LastData > IdleReapSec && s.Buffered == 0))
                {
                    _reap.Add(s.Id);
                    continue;
                }

                if (s.RadioWasActive && Time.unscaledTime - s.LastData > 0.3f)
                {
                    s.RadioWasActive = false;
                    WriteStatic(s, 0.05f);
                }

                float proxVol = 0f;
                float cutoff = 22000f;
                if (Player.Instance != null)
                {
                    var proxy = ModRuntime.Network?.GetProxy(s.Id);
                    if (proxy != null && proxy.transform != null)
                    {
                        Vector3 a = Player.Instance.transform.position;
                        Vector3 b = proxy.transform.position;
                        float dist = Vector3.Distance(a, b);
                        float t = Mathf.InverseLerp(rangeMax, rangeFull, dist);
                        proxVol = Mathf.Sqrt(t) * vol;
                        cutoff = Mathf.Lerp(4500f, 22000f, t);
                        if (Time.unscaledTime >= s.NextOcclusionCheck)
                        {
                            s.NextOcclusionCheck = Time.unscaledTime + 0.2f;
                            s.Occluded = IsOccluded(a, b);
                        }
                        if (s.Occluded)
                        {
                            proxVol *= 0.65f;
                            cutoff = Mathf.Min(cutoff, 1000f);
                        }
                    }
                }

                float radioVol = (s.Walkie && _localWalkie) ? vol * 0.95f : 0f;
                bool radio = s.RadioMode
                    ? (radioVol > proxVol * 0.85f)
                    : (radioVol > proxVol * 1.15f);
                s.RadioMode = radio;
                if (s.Beh != null)
                    s.Beh.Volume = Mathf.Clamp01(radio ? radioVol : proxVol);
                if (s.Muffle != null)
                {
                    float target = radio ? 22000f : cutoff;
                    s.SmoothCutoff = Mathf.Lerp(s.SmoothCutoff, target, Time.deltaTime * 8f);
                    s.Muffle.cutoffFrequency = s.SmoothCutoff;
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

        private static void WriteStatic(Speaker speaker, float seconds)
        {
            int n = (int)(_sampleRate * seconds);
            lock (speaker.Lock)
            {
                for (int i = 0; i < n; i++)
                {
                    if (speaker.Buffered >= speaker.Ring.Length)
                        break;
                    float fade = 1f - (float)i / n;
                    speaker.Ring[speaker.WritePos] =
                        (UnityEngine.Random.value - 0.5f) * 0.16f * fade * fade;
                    speaker.WritePos = (speaker.WritePos + 1) % speaker.Ring.Length;
                    speaker.Buffered++;
                }
            }
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

        private static bool IsOccluded(Vector3 from, Vector3 to)
        {
            try
            {
                Vector3 delta = to - from;
                float mag = delta.magnitude;
                if (mag < 1f)
                    return false;
                RaycastHit[] hits = Physics.RaycastAll(from, delta / mag, mag);
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider col = hits[i].collider;
                    if (col == null || col.isTrigger)
                        continue;
                    if (col.GetComponentInParent<CharBase>() != null)
                        continue;
                    if (col.GetComponentInParent<Players.RemotePlayerProxy>() != null)
                        continue;
                    return true;
                }
            }
            catch { /* ignore */ }
            return false;
        }
    }
}

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
    public static class VoiceChatService
    {
        private sealed class Speaker
        {
            public int Id;
            public GameObject Go;
            public AudioSource Src;
            public AudioLowPassFilter Muffle;
            public VoiceSpeakerBehaviour Beh;
            public float[] Ring;
            public int ReadPos;
            public int WritePos;
            public int Buffered;
            public readonly object Lock = new object();
            public float LastData;
            public bool Walkie;
            public bool RadioMode;
            public bool RadioWasActive;
            public float RadioHp;
            public float RadioLp;
            public bool Priming = true;
            public int PacketsIn;
            public int Underruns;
            public bool Occluded;
            public float NextOcclusionCheck;
            public float SmoothCutoff = 22000f;
        }

        private sealed class VoiceSpeakerBehaviour : MonoBehaviour
        {
            internal Speaker S;
            public int SrcRate;
            public volatile float Volume;
            private double _step = 1.0;
            private double _acc;
            private float _cur;

            private void OnAudioFilterRead(float[] data, int channels)
            {
                Speaker s = S;
                if (s == null)
                {
                    Array.Clear(data, 0, data.Length);
                    return;
                }
                if (_step == 1.0 && SrcRate > 0)
                    _step = (double)SrcRate / AudioSettings.outputSampleRate;

                int frames = data.Length / channels;
                float volume = Volume;
                lock (s.Lock)
                {
                    int primeNeed = (int)(SrcRate * 0.25f);
                    if (s.Priming)
                    {
                        if (s.Buffered < primeNeed)
                        {
                            Array.Clear(data, 0, data.Length);
                            return;
                        }
                        s.Priming = false;
                    }

                    for (int i = 0; i < frames; i++)
                    {
                        _acc += _step;
                        while (_acc >= 1.0)
                        {
                            _acc -= 1.0;
                            if (s.Buffered > 0)
                            {
                                _cur = s.Ring[s.ReadPos];
                                s.ReadPos = (s.ReadPos + 1) % s.Ring.Length;
                                s.Buffered--;
                            }
                            else
                            {
                                s.Priming = true;
                                s.Underruns++;
                                for (int j = i; j < frames; j++)
                                {
                                    for (int c = 0; c < channels; c++)
                                        data[j * channels + c] = 0f;
                                }
                                return;
                            }
                        }
                        float sample = _cur * volume;
                        for (int c = 0; c < channels; c++)
                            data[i * channels + c] = sample;
                    }
                }
            }
        }

        private static bool _recording;
        private static float _stopLinger;
        private static ushort _seq;
        private static readonly byte[] _captureBuf = new byte[8192];
        private static KeyCode _pttKey = KeyCode.V;
        private static bool _keyParsed;
        private static AudioClip _carrier;
        private static readonly Dictionary<int, Speaker> _speakers = new Dictionary<int, Speaker>();
        private static readonly List<int> _reap = new List<int>();
        private static GameObject _root;
        private static byte[] _decompressBuf;
        private static uint _sampleRate;
        private static bool _localWalkie;
        private static float _nextWalkieCheck;
        private static bool _steamChecked;
        private static bool _steamOk;
        private static bool _steamWarned;
        private static bool _walkieTx;
        private static float _nextRearm;
        private static float _lastSent;
        private static int _txPackets;
        private static float _nextStatsLog;

        public static void Reset()
        {
            if (_recording)
                StopCapture();
            foreach (Speaker s in _speakers.Values)
            {
                if (s.Go != null)
                    UnityEngine.Object.Destroy(s.Go);
            }
            _speakers.Clear();
            _keyParsed = false;
        }

        public static void Tick()
        {
            if (ModConfig.VoiceEnabled == null || !ModConfig.VoiceEnabled.Value)
            {
                if (_recording)
                    StopCapture();
                return;
            }

            if (!SteamAvailable())
                return;

            UpdateLocalWalkie();
            UpdateSpeakers();

            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || Player.Instance == null || Core.loadingGame)
            {
                if (_recording)
                    StopCapture();
                return;
            }

            if (!_keyParsed)
            {
                _keyParsed = true;
                try
                {
                    _pttKey = (KeyCode)Enum.Parse(typeof(KeyCode),
                        ModConfig.VoicePttKey?.Value ?? "V", ignoreCase: true);
                }
                catch
                {
                    ModLog.Warn(LogCat.Audio, "Bad VoicePttKey — using V");
                    _pttKey = KeyCode.V;
                }
            }

            bool chatOpen = ChatHud.IsInputOpen;
            bool ptt = Input.GetKey(_pttKey) && !chatOpen;
            bool openMic = !string.Equals(ModConfig.VoiceMode?.Value ?? "ptt", "ptt",
                StringComparison.OrdinalIgnoreCase);
            _walkieTx = false;
            try
            {
                string walkie = ModConfig.WalkieItemName?.Value ?? "walkie_talkie";
                if (!string.IsNullOrEmpty(walkie))
                {
                    InvItemClass cur = Player.Instance.currentItem;
                    if (!InvItemClass.isNull(cur) && cur.type == walkie)
                        _walkieTx = Input.GetMouseButton(1);
                }
            }
            catch { /* ignore */ }

            if (openMic || ptt || _walkieTx)
            {
                if (!_recording)
                    StartCapture();
                _stopLinger = Time.unscaledTime + 0.25f;
                if (Time.unscaledTime >= _nextRearm)
                {
                    _nextRearm = Time.unscaledTime + 1f;
                    try { SteamUser.StartVoiceRecording(); } catch { /* ignore */ }
                }
            }
            else if (_recording && Time.unscaledTime > _stopLinger)
            {
                StopCapture();
            }

            if (_recording || _stopLinger > Time.unscaledTime)
                PumpCapture(net);
        }

        public static void OnVoiceData(VoiceDataMessage msg)
        {
            if (ModConfig.VoiceEnabled == null || !ModConfig.VoiceEnabled.Value)
                return;
            if (!SteamAvailable())
                return;
            var net = ModRuntime.Network;
            if (net != null && msg.PlayerId == net.LocalPlayerId)
                return;
            if (msg.Data == null || msg.Data.Length == 0)
                return;
            Decompress(msg);
        }

        private static void StartCapture()
        {
            try
            {
                SteamUser.StartVoiceRecording();
                _recording = true;
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Audio, "StartVoiceRecording: " + ex.Message);
            }
        }

        private static void StopCapture()
        {
            try
            {
                SteamUser.StopVoiceRecording();
                uint avail = 0;
                uint got = 0;
                for (int i = 0; i < 16; i++)
                {
                    if (SteamUser.GetAvailableVoice(out avail) != EVoiceResult.k_EVoiceResultOK)
                        break;
                    if (avail == 0)
                        break;
                    SteamUser.GetVoice(true, _captureBuf, (uint)_captureBuf.Length, out got);
                }
            }
            catch { /* ignore */ }
            _recording = false;
        }

        private static void PumpCapture(LanNetworkManager net)
        {
            try
            {
                uint avail = 0;
                if (SteamUser.GetAvailableVoice(out avail) != EVoiceResult.k_EVoiceResultOK || avail == 0)
                    return;
                uint got = 0;
                if (SteamUser.GetVoice(true, _captureBuf, (uint)_captureBuf.Length, out got)
                    != EVoiceResult.k_EVoiceResultOK || got == 0)
                    return;

                byte[] data = new byte[got];
                Buffer.BlockCopy(_captureBuf, 0, data, 0, (int)got);
                var msg = new VoiceDataMessage
                {
                    PlayerId = Math.Max(net.LocalPlayerId, 0),
                    Seq = _seq++,
                    Flags = (byte)(_walkieTx ? VoiceDataMessage.FlagWalkie : 0),
                    Data = data
                };
                net.Broadcast(NetMessageType.VoiceData, w => msg.Serialize(w), DeliveryMethod.Unreliable);
                _txPackets++;
                _lastSent = Time.unscaledTime;
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Audio, "Voice capture: " + ex.Message);
            }
        }

        private static void Decompress(VoiceDataMessage p)
        {
            try
            {
                if (_sampleRate == 0)
                {
                    _sampleRate = SteamUser.GetVoiceOptimalSampleRate();
                    if (_sampleRate == 0)
                        _sampleRate = 11025u;
                    _decompressBuf = new byte[131072];
                    ModLog.Event(LogCat.Audio, "Voice decoding at " + _sampleRate + "Hz");
                }

                uint bytesOut = 0;
                EVoiceResult result = SteamUser.DecompressVoice(
                    p.Data, (uint)p.Data.Length, _decompressBuf, (uint)_decompressBuf.Length,
                    out bytesOut, _sampleRate);
                if (result != EVoiceResult.k_EVoiceResultOK || bytesOut < 2)
                    return;

                Speaker speaker = EnsureSpeaker(p.PlayerId);
                speaker.Walkie = (p.Flags & VoiceDataMessage.FlagWalkie) != 0;
                speaker.PacketsIn++;
                speaker.LastData = Time.unscaledTime;
                float gain = ModConfig.VoiceGain?.Value ?? 1.4f;
                bool radioMode = speaker.RadioMode;
                if (radioMode)
                    speaker.RadioWasActive = true;

                float hpCoeff = 1f - Mathf.Exp((float)Math.PI * -600f / _sampleRate);
                float lpCoeff = 1f - Mathf.Exp((float)Math.PI * -6800f / _sampleRate);
                int samples = (int)bytesOut / 2;
                lock (speaker.Lock)
                {
                    for (int i = 0; i < samples; i++)
                    {
                        if (speaker.Buffered >= speaker.Ring.Length)
                            break;
                        short pcm = (short)(_decompressBuf[i * 2] | (_decompressBuf[i * 2 + 1] << 8));
                        float sample = pcm / 32768f * gain;
                        if (radioMode)
                        {
                            speaker.RadioHp += hpCoeff * (sample - speaker.RadioHp);
                            sample -= speaker.RadioHp;
                            speaker.RadioLp += lpCoeff * (sample - speaker.RadioLp);
                            sample = speaker.RadioLp;
                            sample *= 2f;
                            sample /= 1f + 0.5f * Mathf.Abs(sample);
                            sample += (UnityEngine.Random.value - 0.5f) * 0.012f;
                        }
                        speaker.Ring[speaker.WritePos] = Mathf.Clamp(sample, -1f, 1f);
                        speaker.WritePos = (speaker.WritePos + 1) % speaker.Ring.Length;
                        speaker.Buffered++;
                    }

                    int maxBuf = (int)(_sampleRate * 0.9f);
                    if (speaker.Buffered > maxBuf)
                    {
                        int drop = speaker.Buffered - (int)(_sampleRate * 0.35f);
                        speaker.ReadPos = (speaker.ReadPos + drop) % speaker.Ring.Length;
                        speaker.Buffered -= drop;
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Audio, "Voice decompress: " + ex.Message);
            }
        }

        private static AudioClip CarrierClip()
        {
            if (_carrier != null)
                return _carrier;
            _carrier = AudioClip.Create("yokware_voice_carrier", 4800, 1, 48000, false);
            _carrier.SetData(new float[4800], 0);
            return _carrier;
        }

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
                if (s.Go == null || (Time.unscaledTime - s.LastData > 300f && s.Buffered == 0))
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

        private static bool SteamAvailable()
        {
            if (!_steamChecked)
            {
                try
                {
                    _steamOk = SteamManager.Initialized && SteamUser.BLoggedOn();
                }
                catch
                {
                    _steamOk = false;
                }
                _steamChecked = true;
                if (!_steamOk && !_steamWarned)
                {
                    _steamWarned = true;
                    ModLog.Event(LogCat.Audio, "Steam unavailable — voice chat disabled");
                }
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

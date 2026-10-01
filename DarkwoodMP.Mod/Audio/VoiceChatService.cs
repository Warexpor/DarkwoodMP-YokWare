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
        private static float _nextSteamCheck;
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

        /// <summary>
        /// Drop one peer's playback speaker right away (peer left). Without this the speaker and
        /// its looping AudioSource linger until the idle reap.
        /// </summary>
        public static void RemoveSpeaker(int playerId)
        {
            if (!_speakers.TryGetValue(playerId, out Speaker s))
                return;
            if (s.Go != null)
                UnityEngine.Object.Destroy(s.Go);
            _speakers.Remove(playerId);
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

            // Typing in chat / F2 / F3 / the slot picker must not key the mic.
            bool ptt = Input.GetKey(_pttKey) && !UiInputLock.IsHeld;
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
                        _walkieTx = Input.GetMouseButton(1) && WalkieTxAllowed();
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

        /// <summary>
        /// RMB is also vanilla aim / context click, so it only keys the radio while the player is
        /// actually playing: no inventory, container, dialogue, map, journal or other menu; no
        /// pause menu; not dead; and no overlay of ours holding input (chat, F2, F3).
        /// </summary>
        private static bool WalkieTxAllowed()
        {
            if (UiInputLock.IsHeld || Core.mainMenu || Core.loadingGame || Core.forbidInputs)
                return false;

            Player p = Player.Instance;
            if (p == null || !p.alive || p.dying)
                return false;
            if (p.Inventory != null && p.Inventory.open)
                return false;
            if (p.openedItemInventory != null || p.openedItemInventory2 != null)
                return false;
            // dialogue, item menu, construction, leveling, map, journal, skills, padlock, controller menu
            if (p.inMenu())
                return false;

            MainMenu pause = Singleton<MainMenu>.Instance;
            return pause == null || !pause.gameObject.activeInHierarchy;
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

                int playerId = Math.Max(net.LocalPlayerId, 0);
                ushort seq = _seq++;
                byte flags = (byte)(_walkieTx ? VoiceDataMessage.FlagWalkie : 0);
                int len = (int)got;
                net.Broadcast(NetMessageType.VoiceData, w =>
                {
                    w.Put(playerId);
                    w.Put((short)seq);
                    w.Put(flags);
                    w.Put(_captureBuf, 0, len);
                }, DeliveryMethod.Unreliable);
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

    }
}

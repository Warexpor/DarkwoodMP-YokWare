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
    /// A voice is heard from where the talker stands (3D, muffled through walls), and as far as
    /// they spoke loud: each packet carries the talker's loudness, measured where it was
    /// recorded. On the walkie it is heard through the radio, band-limited and with static that
    /// grows with distance. On the host, loud talk is heard by the creatures around the talker
    /// (<see cref="VoiceHearing"/>).
    /// </summary>
    public static partial class VoiceChatService
    {
        /// <summary>How this player hears a talker: their voice, this player's own radio, or a radio nearby.</summary>
        private enum HearMode { Direct, OwnRadio, NearRadio }

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
            /// <summary>The talker is keying their walkie right now (walkie packets still coming).</summary>
            public bool WalkieActive;
            /// <summary>The talker's walkie key click and release, heard around them (3D, a short way).</summary>
            public AudioSource Click;
            public AudioLowPassFilter ClickMuffle;
            /// <summary>Vanilla's indoor reverb (<c>AudioController</c>: a sound from inside a building gets an AudioReverbFilter).</summary>
            public AudioReverbFilter Reverb;
            public float NextInsideCheck;
            public bool RadioMode;
            public bool RadioWasActive;
            public Biquad RadioHp;
            public Biquad RadioLp;
            /// <summary>Radio hiss level for this talker (grows with the distance between the two radios).</summary>
            public float RadioHiss;
            public bool Priming = true;
            /// <summary>Set when a short spurt has ended below the prime level: play it out anyway.</summary>
            public bool PrimeRelease;
            public int PacketsIn;
            public int Underruns;
            public bool HasSeq;
            public ushort LastSeq;
            /// <summary>Loudness of the latest packet (0..1) and its envelope: quick to rise, slow to fall between words.</summary>
            public float Level;
            public float LevelEnv;
            public float Occlusion;
            public float NextOcclusionCheck;
            public bool OccludedNow;
            public float SmoothCutoff = 22000f;
            public float Blend;
            public HearMode Mode;
            /// <summary>The other player whose walkie this talker is heard from (NearRadio), or 0.</summary>
            public int NearRadioId;
            public float RadioOcclusion;
            public bool RadioOccludedNow;
            public float NextRadioOcclusionCheck;
        }

        private sealed class VoiceSpeakerBehaviour : MonoBehaviour
        {
            internal Speaker S;
            public int SrcRate;
            public volatile float Volume;
            private double _step;
            private double _frac;
            private float _prev;
            private float _cur;
            private float _vol;

            private void OnAudioFilterRead(float[] data, int channels)
            {
                Speaker s = S;
                if (s == null || SrcRate <= 0)
                {
                    Array.Clear(data, 0, data.Length);
                    return;
                }
                if (_step <= 0.0)
                    _step = (double)SrcRate / OutputRate;

                int frames = data.Length / channels;
                // Volume ramps across the buffer: a step from one frame to the next clicks.
                float from = _vol;
                float to = Volume;
                _vol = to;
                lock (s.Lock)
                {
                    if (s.Priming)
                    {
                        if (s.Buffered < (int)(SrcRate * PrimeSec) && !s.PrimeRelease)
                        {
                            Array.Clear(data, 0, data.Length);
                            return;
                        }
                        s.Priming = false;
                        s.PrimeRelease = false;
                    }

                    for (int i = 0; i < frames; i++)
                    {
                        _frac += _step;
                        while (_frac >= 1.0)
                        {
                            _frac -= 1.0;
                            if (s.Buffered > 0)
                            {
                                _prev = _cur;
                                _cur = s.Ring[s.ReadPos];
                                s.ReadPos = (s.ReadPos + 1) % s.Ring.Length;
                                s.Buffered--;
                            }
                            else
                            {
                                s.Priming = true;
                                s.Underruns++;
                                _prev = _cur = 0f;
                                for (int j = i * channels; j < data.Length; j++)
                                    data[j] = 0f;
                                return;
                            }
                        }
                        // Linear between the two decoded samples around this output sample.
                        float sample = (_prev + (_cur - _prev) * (float)_frac) * (from + (to - from) * i / frames);
                        // The carrier is a clip of ones: multiplying keeps whatever 3D pan the
                        // source put on it before this filter, and is the sample itself if after.
                        for (int c = 0; c < channels; c++)
                            data[i * channels + c] *= sample;
                    }
                }
            }
        }

        /// <summary>Seconds buffered before a talk spurt starts playing (absorbs network jitter).</summary>
        private const float PrimeSec = 0.15f;
        /// <summary>A spurt shorter than <see cref="PrimeSec"/> plays out once no more data has come for this long.</summary>
        private const float PrimeFlushSec = 0.12f;

        private static int OutputRate = 48000; // process-scoped: AudioSettings.outputSampleRate, read on the main thread

        private static bool _recording;
        private static float _stopLinger;
        private static ushort _seq; // process-scoped: wrapping packet counter
        private static readonly byte[] _captureBuf = new byte[8192];
        private static KeyCode _pttKey = KeyCode.V; // process-scoped: config cache, re-parsed when the setting text changes
        private static string _pttKeyText; // process-scoped: the setting text _pttKey was parsed from
        private static AudioClip _carrier; // process-scoped: asset
        private static readonly Dictionary<int, Speaker> _speakers = new Dictionary<int, Speaker>();
        private static readonly List<int> _reap = new List<int>(); // process-scoped: scratch
        private static GameObject _root; // process-scoped: DontDestroyOnLoad speaker parent
        private static byte[] _decompressBuf; // process-scoped: decoder setup
        private static uint _sampleRate; // process-scoped: decoder setup
        private static byte[] _levelBuf; // process-scoped: decoder setup (own loudness)
        private static bool _localWalkie; // process-scoped: polled every 0.5 s
        private static float _nextWalkieCheck; // process-scoped: polled every 0.5 s
        private static float _nextSteamCheck; // process-scoped: Steam availability
        private static bool _steamOk; // process-scoped: Steam availability
        private static bool _steamWarned; // process-scoped: Steam availability
        private static bool _walkieTx;
        private static bool _walkieTxWas;
        private static float _nextRearm; // process-scoped: rate limit
        private static float _lastSent; // process-scoped: stats
        private static int _txPackets; // process-scoped: stats
        private static float _nextStatsLog; // process-scoped: stats

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
            _stopLinger = 0f;
            _walkieTx = false;
            _walkieTxWas = false;
            _radioHolder = 0;
            _radioHolderAt = 0f;
            VoiceHearing.Reset();
        }

        /// <summary>
        /// Drop one peer's playback speaker right away (peer left). Without this the speaker and
        /// its looping AudioSource linger until the idle reap.
        /// </summary>
        public static void RemoveSpeaker(int playerId)
        {
            VoiceHearing.Forget(playerId);
            if (!_speakers.TryGetValue(playerId, out Speaker s))
                return;
            if (s.Go != null)
                UnityEngine.Object.Destroy(s.Go);
            _speakers.Remove(playerId);
        }

        public static void Tick()
        {
            // Creatures hear the clients' voices on the host whether or not this player has voice.
            VoiceHearing.Tick(ModRuntime.Network);

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
                _walkieTxWas = false;
                return;
            }

            // Multiplayer > Settings can change the key mid-game.
            string keyText = ModConfig.VoicePttKey?.Value ?? "V";
            if (!string.Equals(keyText, _pttKeyText, StringComparison.Ordinal))
            {
                _pttKeyText = keyText;
                try
                {
                    _pttKey = (KeyCode)Enum.Parse(typeof(KeyCode), keyText, ignoreCase: true);
                }
                catch
                {
                    ModLog.Warn(LogCat.Audio, "Bad VoicePttKey — using V");
                    _pttKey = KeyCode.V;
                }
            }

            // Typing in chat or a menu text field must not key the mic.
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

            // The radio's own click when its key goes down and the squelch tail when it comes up.
            if (_walkieTx != _walkieTxWas)
            {
                _walkieTxWas = _walkieTx;
                PlayLocalSquelch(_walkieTx);
            }

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
        /// pause menu; not dead; and no overlay of ours holding input (chat, a menu text field).
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
            var net = ModRuntime.Network;
            if (net != null && msg.PlayerId == net.LocalPlayerId)
                return;
            // The loudness travels with the packet, so the host needs no Steam to let creatures hear it.
            VoiceHearing.Heard(msg.PlayerId, msg.Level / 255f, (msg.Flags & VoiceDataMessage.FlagWalkie) != 0);
            if (ModConfig.VoiceEnabled == null || !ModConfig.VoiceEnabled.Value)
                return;
            if (!SteamAvailable())
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
                float level = MeasureOwnLevel((int)got);
                byte levelByte = (byte)Mathf.RoundToInt(level * 255f);
                if (net.Role == NetworkRole.Host)
                    VoiceHearing.Heard(net.LocalPlayerId, level, _walkieTx);
                int len = (int)got;
                net.Broadcast(NetMessageType.VoiceData,
                    w => VoiceDataMessage.WriteSlice(w, playerId, seq, flags, levelByte, _captureBuf, len),
                    DeliveryMethod.Unreliable);
                _txPackets++;
                _lastSent = Time.unscaledTime;
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Audio, "Voice capture: " + ex.Message);
            }
        }

        /// <summary>Loudness of the packet just recorded: it is decoded here once (low rate is plenty to measure).</summary>
        private static float MeasureOwnLevel(int length)
        {
            try
            {
                if (_levelBuf == null)
                    _levelBuf = new byte[65536];
                uint bytesOut;
                if (SteamUser.DecompressVoice(_captureBuf, (uint)length, _levelBuf, (uint)_levelBuf.Length,
                        out bytesOut, 11025u) != EVoiceResult.k_EVoiceResultOK)
                    return 0f;
                return LevelOf(_levelBuf, (int)bytesOut / 2);
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        /// 0 for silence, about 0.15 for a whisper, 0.65 for normal speech, 1 for shouting: the RMS of the
        /// 16-bit samples in dBFS, from -50 to -12.
        /// </summary>
        internal static float LevelOf(byte[] pcm16, int samples)
        {
            if (samples <= 0)
                return 0f;
            double sum = 0;
            for (int i = 0; i < samples; i++)
            {
                short v = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
                double f = v / 32768.0;
                sum += f * f;
            }
            double rms = Math.Sqrt(sum / samples);
            float db = 20f * (float)Math.Log10(rms + 1e-9);
            return Mathf.Clamp01(Mathf.InverseLerp(-45f, -10f, db));
        }

        private static void Decompress(VoiceDataMessage p)
        {
            try
            {
                if (_sampleRate == 0)
                {
                    // Decoded straight at the mixer's rate (Steam takes 11025..48000): no resampling
                    // left for the audio thread unless the mixer runs faster than that.
                    OutputRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
                    _sampleRate = (uint)Mathf.Clamp(OutputRate, 11025, 48000);
                    _decompressBuf = new byte[262144];
                    ModLog.Event(LogCat.Audio, "Voice decoding at " + _sampleRate + "Hz (mixer " + OutputRate + "Hz)");
                }

                Speaker speaker = EnsureSpeaker(p.PlayerId);
                // Unreliable packets can arrive late or twice: an older one would play out of order.
                if (speaker.HasSeq)
                {
                    short ahead = (short)(p.Seq - speaker.LastSeq);
                    if (ahead <= 0 && ahead > -1000)
                        return;
                }
                speaker.HasSeq = true;
                speaker.LastSeq = p.Seq;

                uint bytesOut = 0;
                EVoiceResult result = SteamUser.DecompressVoice(
                    p.Data, (uint)p.Data.Length, _decompressBuf, (uint)_decompressBuf.Length,
                    out bytesOut, _sampleRate);
                if (result != EVoiceResult.k_EVoiceResultOK || bytesOut < 2)
                    return;

                bool walkieNow = (p.Flags & VoiceDataMessage.FlagWalkie) != 0;
                if (walkieNow != speaker.WalkieActive)
                {
                    speaker.WalkieActive = walkieNow;
                    PlayTalkerClick(speaker, keyDown: walkieNow);
                }
                speaker.Walkie = walkieNow;
                if (walkieNow)
                    ClaimChannel(speaker.Id);
                speaker.Level = p.Level / 255f;
                speaker.PacketsIn++;
                speaker.LastData = Time.unscaledTime;
                float gain = ModConfig.VoiceGain?.Value ?? 1.4f;
                bool radioMode = speaker.RadioMode;
                bool radioStart = radioMode && !speaker.RadioWasActive;
                if (radioMode)
                    speaker.RadioWasActive = true;
                if (radioStart)
                {
                    speaker.RadioHp = Biquad.HighPass(RadioLowHz, _sampleRate);
                    speaker.RadioLp = Biquad.LowPass(RadioHighHz, _sampleRate);
                    WriteSquelch(speaker, open: true);
                }

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
                            sample = RadioSample(speaker, sample);
                        speaker.Ring[speaker.WritePos] = Mathf.Clamp(sample, -1f, 1f);
                        speaker.WritePos = (speaker.WritePos + 1) % speaker.Ring.Length;
                        speaker.Buffered++;
                    }

                    // Fell behind (a burst after a stall): skip ahead rather than lag for the rest of the talk.
                    int maxBuf = (int)(_sampleRate * 0.6f);
                    if (speaker.Buffered > maxBuf)
                    {
                        int drop = speaker.Buffered - (int)(_sampleRate * 0.2f);
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

        /// <summary>A looping clip of ones: the speaker's filter multiplies its voice into it.</summary>
        private static AudioClip CarrierClip()
        {
            if (_carrier != null)
                return _carrier;
            var ones = new float[4800];
            for (int i = 0; i < ones.Length; i++)
                ones[i] = 1f;
            _carrier = AudioClip.Create("yokware_voice_carrier", ones.Length, 1, 48000, false);
            _carrier.SetData(ones, 0);
            return _carrier;
        }
    }
}

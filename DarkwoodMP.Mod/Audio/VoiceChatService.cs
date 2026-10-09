using System;
using System.Collections.Generic;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// Voice chat over the Horde wire (LAN or Steam session alike): this player's microphone
    /// (<see cref="VoiceMic"/>) in the mod's own codec (<see cref="VoiceCodec"/>), no Steam needed.
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
            public float NextTalkerInsideCheck;
            public bool TalkerInside;
            /// <summary>Signal on the radio this talker is heard through, 1 clear .. 0 none (0 when heard directly).</summary>
            public float RadioQuality;
            /// <summary>Someone keying over this talker (0..1): the whistle of two carriers.</summary>
            public float Doubling;
            /// <summary>Feedback from a radio right by this talker (0..1).</summary>
            public float Howl;
            public double HowlPhase;
            public double HetPhase;
            public float BreakupLeft;
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

        /// <summary>Voice is carried at the codec's rate end to end.</summary>
        private static readonly uint _sampleRate = VoiceCodec.SampleRate;

        private static float _stopLinger;
        private static ushort _seq; // process-scoped: wrapping packet counter
        private static KeyCode _pttKey = KeyCode.V; // process-scoped: config cache, re-parsed when the setting text changes
        private static string _pttKeyText; // process-scoped: the setting text _pttKey was parsed from
        private static AudioClip _carrier; // process-scoped: asset
        private static readonly Dictionary<int, Speaker> _speakers = new Dictionary<int, Speaker>();
        private static readonly List<int> _reap = new List<int>(); // process-scoped: scratch
        private static GameObject _root; // process-scoped: DontDestroyOnLoad speaker parent
        private static readonly float[] _decodeBuf = new float[VoiceCodec.PacketSamples * 2]; // process-scoped: decode scratch
        private static readonly byte[] _encodeBuf = new byte[VoiceCodec.EncodedSize(VoiceCodec.PacketSamples)]; // process-scoped: encode scratch
        private static readonly List<float[]> _frames = new List<float[]>(8); // process-scoped: frames from the mic this tick
        private static float[] _preroll; // process-scoped: the frame before talk began (its first syllable)
        private static bool _localWalkie; // process-scoped: polled every 0.5 s
        private static float _nextWalkieCheck; // process-scoped: polled every 0.5 s
        private static bool _walkieTx;
        private static bool _walkieTxWas;
        private static bool _transmitting;
        private static int _txPackets; // process-scoped: stats
        private static float _nextStatsLog; // process-scoped: stats

        /// <summary>The Voice settings screen is showing: keep the mic on for its level meter.</summary>
        internal static float MeterWantedUntil; // process-scoped: menu flag, refreshed every frame the screen shows

        /// <summary>This player is sending voice right now (push to talk held, the walkie keyed, or the open mic's gate open).</summary>
        internal static bool Transmitting => _transmitting;

        public static void Reset()
        {
            foreach (Speaker s in _speakers.Values)
            {
                if (s.Go != null)
                    UnityEngine.Object.Destroy(s.Go);
            }
            _speakers.Clear();
            _stopLinger = 0f;
            _walkieTx = false;
            _walkieTxWas = false;
            _transmitting = false;
            _preroll = null;
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
            if (AudioSettings.outputSampleRate > 0)
                OutputRate = AudioSettings.outputSampleRate;

            var net = ModRuntime.Network;
            bool enabled = ModConfig.VoiceEnabled != null && ModConfig.VoiceEnabled.Value;
            bool inGame = net != null && net.IsConnected && Player.Instance != null && !Core.loadingGame;
            bool meter = Time.unscaledTime < MeterWantedUntil;

            _frames.Clear();
            VoiceMic.Tick(enabled && (inGame || meter), _frames);

            if (!enabled)
            {
                _transmitting = false;
                // The radio is a device: its knob and battery work with voice chat off too.
                if (inGame)
                {
                    UpdateLocalWalkie();
                    TickWalkie(talking: false, receiving: false);
                }
                return;
            }

            UpdateLocalWalkie();
            UpdateSpeakers();
            TickHowl(net, Time.unscaledDeltaTime);

            if (!inGame)
            {
                _transmitting = false;
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
                        _walkieTx = Input.GetKey(RadioTalkKey()) && WalkieTxAllowed() && LocalRadioLive;
                }
            }
            catch { /* ignore */ }

            // A test tone sent as radio talk keys the walkie like the mouse button would.
            if (_toneWalkie && Time.unscaledTime < _toneUntil)
                _walkieTx = true;
            PumpTestTone(net);

            TickWalkie(_walkieTx, ReceivingOnOwnRadio());

            // The radio's own click when its key goes down and the squelch tail when it comes up.
            if (_walkieTx != _walkieTxWas)
            {
                _walkieTxWas = _walkieTx;
                PlayLocalSquelch(_walkieTx);
            }

            // Push to talk and the walkie send while held (and a moment after, for the last
            // word); an always-on mic sends while its gate hears speech over the room.
            if (ptt || _walkieTx)
                _stopLinger = Time.unscaledTime + 0.25f;
            bool send = ptt || _walkieTx || Time.unscaledTime < _stopLinger || (openMic && VoiceMic.GateOpen);
            if (send && !_transmitting && _preroll != null)
                SendFrame(net, _preroll);
            _transmitting = send;
            for (int i = 0; i < _frames.Count; i++)
            {
                if (send)
                    SendFrame(net, _frames[i]);
                _preroll = _frames[i];
            }
        }

        private static float _toneUntil; // process-scoped: test pilot tone
        private static bool _toneWalkie; // process-scoped: test pilot tone
        private static double _tonePhase; // process-scoped: test pilot tone
        private static float _toneNext; // process-scoped: test pilot tone pacing

        /// <summary>Test pilot: send a 440 Hz tone for <paramref name="seconds"/>, at the mic's own pace, as if talking.</summary>
        internal static void SendTestTone(float seconds, bool walkie)
        {
            _toneUntil = Time.unscaledTime + seconds;
            _toneWalkie = walkie;
            _toneNext = Time.unscaledTime;
        }

        private static void PumpTestTone(LanNetworkManager net)
        {
            const float frameSec = VoiceCodec.PacketSamples / (float)VoiceCodec.SampleRate;
            while (Time.unscaledTime < _toneUntil && _toneNext <= Time.unscaledTime)
            {
                _toneNext += frameSec;
                var frame = new float[VoiceCodec.PacketSamples];
                for (int i = 0; i < frame.Length; i++)
                {
                    frame[i] = 0.1f * (float)Math.Sin(_tonePhase);
                    _tonePhase += 2.0 * Math.PI * 440.0 / VoiceCodec.SampleRate;
                }
                SendFrame(net, frame);
            }
        }

        private static bool ReceivingOnOwnRadio()
        {
            foreach (Speaker s in _speakers.Values)
            {
                if (s.Mode == HearMode.OwnRadio && Time.unscaledTime - s.LastData < 0.5f)
                    return true;
            }
            return false;
        }

        private static string WalkieStateText(byte st)
        {
            byte p = WalkieStates.Power(st);
            string t = p == WalkieStates.None ? "none" : p == WalkieStates.Off ? "off" : p == WalkieStates.Pocket ? "pocket" : "hand";
            return WalkieStates.IsUnderground(st) ? t + "+underground" : t;
        }

        /// <summary>Test pilot: switch this player's radio on or off as the knob would.</summary>
        internal static void SetRadioPower(bool on) => _radioOn = on;

        /// <summary>Test pilot: this player's mic and every talker heard here.</summary>
        internal static string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("mic=").Append(VoiceMic.Running ? "'" + VoiceMic.RunningDevice + "' level=" + VoiceMic.Level.ToString("0.00") : "off")
              .Append(" devices=").Append(VoiceMic.Devices.Length)
              .Append(" walkie=").Append(WalkieStateText(LocalWalkieState))
              .Append(" battery=").Append(Mathf.RoundToInt(Charge() * 100f)).Append('%')
              .Append(" howl=").Append(_howlLevel.ToString("0.00"))
              .Append(" sent=").Append(_seq);
            foreach (Speaker s in _speakers.Values)
            {
                int buffered, under;
                lock (s.Lock) { buffered = s.Buffered; under = s.Underruns; }
                sb.Append(" | p").Append(s.Id).Append(" seq=").Append(s.LastSeq).Append(" buf=").Append(buffered)
                  .Append(" under=").Append(under).Append(" level=").Append(s.LevelEnv.ToString("0.00"))
                  .Append(" vol=").Append(s.Beh != null ? s.Beh.Volume.ToString("0.00") : "-")
                  .Append(" mode=").Append(s.Mode).Append(s.WalkieActive ? " walkie" : "")
                  .Append(" q=").Append(s.RadioQuality.ToString("0.00"))
                  .Append(s.Doubling > 0f ? " doubling=" + s.Doubling.ToString("0.00") : "")
                  .Append(s.Howl > 0f ? " howl=" + s.Howl.ToString("0.00") : "")
                  .Append(" age=").Append((Time.unscaledTime - s.LastData).ToString("0.0")).Append("s");
            }
            return sb.ToString();
        }

        private static KeyCode _talkKey = KeyCode.Mouse1; // process-scoped: config cache
        private static string _talkKeyText; // process-scoped: the setting text _talkKey was parsed from

        /// <summary>The walkie's talk button (config <c>VoiceRadioTalkKey</c>, right mouse by default).</summary>
        private static KeyCode RadioTalkKey()
        {
            string text = ModConfig.VoiceRadioTalkKey?.Value ?? "Mouse1";
            if (!string.Equals(text, _talkKeyText, StringComparison.Ordinal))
            {
                _talkKeyText = text;
                try { _talkKey = (KeyCode)Enum.Parse(typeof(KeyCode), text, ignoreCase: true); }
                catch
                {
                    ModLog.Warn(LogCat.Audio, "Bad VoiceRadioTalkKey — using Mouse1");
                    _talkKey = KeyCode.Mouse1;
                }
            }
            return _talkKey;
        }

        /// <summary>
        /// The talk key (right mouse by default) is also vanilla aim / context click, so it only keys the radio while the player is
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
            // The loudness travels with the packet: creatures hear it even where voice is off.
            VoiceHearing.Heard(msg.PlayerId, msg.Level / 255f, (msg.Flags & VoiceDataMessage.FlagWalkie) != 0);
            if (ModConfig.VoiceEnabled == null || !ModConfig.VoiceEnabled.Value)
                return;
            if (msg.Data == null || msg.Data.Length <= VoiceCodec.HeaderBytes)
                return;
            Decode(msg);
        }

        private static void SendFrame(LanNetworkManager net, float[] frame)
        {
            try
            {
                int len = VoiceCodec.Encode(frame, frame.Length, _encodeBuf);
                float level = VoiceCodec.LevelOf(frame, frame.Length);
                int playerId = Math.Max(net.LocalPlayerId, 0);
                ushort seq = _seq++;
                byte flags = (byte)(_walkieTx ? VoiceDataMessage.FlagWalkie : 0);
                byte levelByte = (byte)Mathf.RoundToInt(level * 255f);
                if (net.Role == NetworkRole.Host)
                    VoiceHearing.Heard(net.LocalPlayerId, level, _walkieTx);
                net.Broadcast(NetMessageType.VoiceData,
                    w => VoiceDataMessage.WriteSlice(w, playerId, seq, flags, levelByte, _encodeBuf, len),
                    DeliveryMethod.Unreliable);
                _txPackets++;
            }
            catch (Exception ex)
            {
                if (NetLogThrottle.ShouldLog("voice-send", 10f, out _))
                    ModLog.Warn(LogCat.Audio, "Voice send: " + ex.Message);
            }
        }

        private static void Decode(VoiceDataMessage p)
        {
            try
            {
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

                int samples = VoiceCodec.Decode(p.Data, p.Data.Length, _decodeBuf);
                if (samples <= 0)
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

                if (radioMode)
                    RadioPacket(speaker, _decodeBuf, samples);

                lock (speaker.Lock)
                {
                    for (int i = 0; i < samples; i++)
                    {
                        if (speaker.Buffered >= speaker.Ring.Length)
                            break;
                        float sample = _decodeBuf[i] * gain;
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
                if (NetLogThrottle.ShouldLog("voice-decode", 10f, out _))
                    ModLog.Warn(LogCat.Audio, "Voice decode: " + ex.Message);
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

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
        /// <summary>
        /// Which radio plays a talker's transmission to this player: their own, or another
        /// player's nearby (Direct: none). The talker's voice itself is heard besides, from
        /// where they stand, on the talker's own speaker.
        /// </summary>
        private enum HearMode { Direct, OwnRadio, NearRadio }

        private sealed class Speaker
        {
            public int Id;
            /// <summary>
            /// A talker's second speaker: their transmissions as a radio plays them, beside
            /// the voice itself on this one. The radio fields below are used on that one.
            /// </summary>
            public Speaker Radio;
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
            public AudioReverbFilter ClickReverb;
            /// <summary>Where the two static recordings are read from, and how much of the weak-signal one is mixed in.</summary>
            public int StaticNearPos;
            public int StaticFarPos;
            public float FarStatic;
            /// <summary>Vanilla's indoor reverb (<c>AudioController</c>: a sound from inside a building gets an AudioReverbFilter).</summary>
            public AudioReverbFilter Reverb;
            public float NextInsideCheck;
            /// <summary>The last inside check's answer (the reverb fades toward it every frame).</summary>
            public bool ReverbInside;
            public float NextTalkerInsideCheck;
            public bool TalkerInside;
            /// <summary>Signal on the radio this talker is heard through, 1 clear .. 0 none (0 when heard directly).</summary>
            public float RadioQuality;
            /// <summary>Someone keying over this talker (0..1): the two bury each other in static.</summary>
            public float Doubling;
            /// <summary>Where in the caller's beep this transmission is (-1: not playing).</summary>
            public int BeepPos = -1;
            public bool RadioMode;
            public bool RadioWasActive;
            /// <summary>How much of the voice the signal lets through (1 clear .. 0 static only), as of the last packet.</summary>
            public float VoiceShare = 1f;
            /// <summary>
            /// Until then the radio this talker was heard on plays out what it has and its squelch
            /// tail, at the volume it had (<see cref="HeldVol"/>): the talker is off the air by
            /// then, and without the hold the tail played as a voice from where they stand.
            /// </summary>
            public float RadioHoldUntil;
            public float HeldVol;
            public float HeldCutoff = 22000f;
            public Biquad RadioHp;
            public Biquad RadioLp;
            public bool Priming = true;
            /// <summary>Holds this talker's peaks under full scale (<see cref="VoiceLimiter"/>).</summary>
            public readonly VoiceLimiter Limiter = new VoiceLimiter(VoiceCodec.SampleRate);
            /// <summary>Seconds of voice buffered before playback starts (the network's jitter; less for a local source).</summary>
            public float PrimeSec = VoiceChatService.PrimeSec;
            /// <summary>Set when a short spurt has ended below the prime level: play it out anyway.</summary>
            public bool PrimeRelease;
            public int PacketsIn;
            public int Underruns;
            public bool HasSeq;
            public ushort LastSeq;
            /// <summary>This talker's two streams: the voice, and what their radio picked up around them.</summary>
            public VoiceDecoder VoiceDec;
            public VoiceDecoder WorldDec;
            /// <summary>Packets that never came, filled in from the next one or by the decoder.</summary>
            public int Recovered;
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
            private readonly VoiceInterpolator _interp = new VoiceInterpolator();
            private float _vol;
            /// <summary>0..1 over the first samples after a start: a stream that begins at full level clicks.</summary>
            private float _rise;

            /// <summary>Samples (at the voice rate) a stream takes to come in, and to go out as its buffer runs dry.</summary>
            private const int EdgeSamples = 128;

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
                        if (s.Buffered < (int)(SrcRate * s.PrimeSec) && !s.PrimeRelease)
                        {
                            Array.Clear(data, 0, data.Length);
                            return;
                        }
                        s.Priming = false;
                        s.PrimeRelease = false;
                        _rise = 0f;
                    }

                    for (int i = 0; i < frames; i++)
                    {
                        _frac += _step;
                        while (_frac >= 1.0)
                        {
                            _frac -= 1.0;
                            if (s.Buffered > 0)
                            {
                                // In over the first samples, out over the last ones buffered: static or a
                                // voice cut off at full level is a click on every start and stop.
                                if (_rise < 1f)
                                    _rise = Mathf.Min(1f, _rise + 1f / EdgeSamples);
                                float edge = s.Buffered >= EdgeSamples ? _rise : _rise * s.Buffered / EdgeSamples;
                                _interp.Push(s.Ring[s.ReadPos] * edge);
                                s.ReadPos = (s.ReadPos + 1) % s.Ring.Length;
                                s.Buffered--;
                            }
                            else
                            {
                                s.Priming = true;
                                s.Underruns++;
                                _interp.Clear();
                                for (int j = i * channels; j < data.Length; j++)
                                    data[j] = 0f;
                                return;
                            }
                        }
                        // The voice at this output sample, rebuilt from the decoded ones around it.
                        float sample = _interp.At((float)_frac) * (from + (to - from) * i / frames);
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
        /// <summary>Lost packets in a row that are filled in; a longer break starts the stream afresh.</summary>
        private const int MaxFilledPackets = 3;
        /// <summary>A packet that follows the last one within this long is the same stretch of talk.</summary>
        private const float SameTalkSec = 0.3f;
        /// <summary>Bytes ahead of the voice block that give its length, when a second block follows it.</summary>
        private const int WorldPrefixBytes = 2;
        private static readonly float[] _decodeBuf = new float[VoiceCodec.PacketSamples * (MaxFilledPackets + 1)]; // process-scoped: decode scratch
        private static readonly byte[] _encodeBuf = new byte[WorldPrefixBytes + VoiceCodec.MaxBlockBytes * 2]; // process-scoped: encode scratch (voice block, then the picked-up sounds)
        private static VoiceEncoder _voiceEnc;
        private static VoiceEncoder _worldEnc;
        private static long _encodeTicks; // process-scoped: stats
        private static readonly float[] _worldTx = new float[VoiceCodec.PacketSamples]; // process-scoped: picked-up sounds of the frame being sent
        private static readonly float[] _worldRx = new float[VoiceCodec.PacketSamples]; // process-scoped: picked-up sounds of the packet being decoded
        private static readonly float[] _echoMix = new float[VoiceCodec.PacketSamples]; // process-scoped: scratch
        /// <summary>The sounds picked up by a talker's radio against their voice, on the radio that plays them.</summary>
        private const float WorldRxGain = 0.5f;
        private static readonly List<float[]> _frames = new List<float[]>(8); // process-scoped: frames from the mic this tick
        private static float[] _preroll; // process-scoped: the frame before talk began (its first syllable)
        private static bool _localWalkie; // process-scoped: polled every 0.5 s
        private static float _nextWalkieCheck; // process-scoped: polled every 0.5 s
        private static bool _walkieTx;
        private static float _walkieSendUntil; // process-scoped: compared against the clock
        /// <summary>The mic's delay (<see cref="VoiceMicChain.MaxDelaySec"/>), the frame its worker hands packets over in, and a packet more.</summary>
        private const float WalkieSendTailSec = VoiceMicChain.MaxDelaySec + 0.08f;
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
                DestroySpeaker(s);
            _speakers.Clear();
            ResetSelfEcho();
            _knobStates.Clear();
            _stopLinger = 0f;
            _walkieTx = false;
            _walkieTxWas = false;
            _transmitting = false;
            _preroll = null;
            _voiceEnc = null;
            _worldEnc = null;
            _radioHolder = 0;
            _radioHolderAt = 0f;
            WorldSoundPickup.Reset();
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
            DestroySpeaker(s);
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
                WorldSoundPickup.SetActive(false);
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
            UpdateSelfEcho(net, Time.unscaledDeltaTime);
            TickRemoteKnobs(net);

            if (!inGame)
            {
                _transmitting = false;
                _walkieTxWas = false;
                WorldSoundPickup.SetActive(false);
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
            bool ptt = (Input.GetKey(_pttKey) && !UiInputLock.IsHeld)
                || (PadAction(PadVoiceAction(), down: false) && WalkieTxAllowed());
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
                        _walkieTx = RadioTalkHeld() && WalkieTxAllowed() && LocalRadioLive;
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
            // The keyed radio hears the world around its holder too.
            WorldSoundPickup.SetActive(_walkieTx
                && ModConfig.VoiceRadioWorldSounds != null && ModConfig.VoiceRadioWorldSounds.Value);
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
                if (s.Radio != null && s.Radio.Mode == HearMode.OwnRadio && Time.unscaledTime - s.LastData < 0.5f)
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
              .Append(" sent=").Append(_seq);
            foreach (Speaker s in _speakers.Values)
            {
                int buffered, under;
                lock (s.Lock) { buffered = s.Buffered; under = s.Underruns; }
                Speaker r = s.Radio;
                sb.Append(" | p").Append(s.Id).Append(" seq=").Append(s.LastSeq).Append(" buf=").Append(buffered)
                  .Append(" under=").Append(under).Append(" level=").Append(s.LevelEnv.ToString("0.00"))
                  .Append(" vol=").Append(s.Beh != null ? s.Beh.Volume.ToString("0.00") : "-")
                  .Append(" radio=").Append(r != null ? r.Mode.ToString() : "-").Append(s.WalkieActive ? " walkie" : "")
                  .Append(" radioVol=").Append(r != null && r.Beh != null ? r.Beh.Volume.ToString("0.00") : "-")
                  .Append(" q=").Append(r != null ? r.RadioQuality.ToString("0.00") : "-")
                  .Append(r != null && r.Doubling > 0f ? " doubling=" + r.Doubling.ToString("0.00") : "")
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

        /// <summary>The game is played with a controller right now (vanilla's own switch).</summary>
        internal static bool PadMode
        {
            get
            {
                try
                {
                    Globals g = Singleton<Globals>.Instance;
                    return g != null && g.controllerMode;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// A vanilla input action (Rewired, by its name) on the controller. The radio's talk
        /// button is the game's Aim and its knob the game's Attack: with the radio in hand
        /// neither does anything else (it cannot be aimed, and it is no weapon).
        /// </summary>
        private static bool PadAction(string action, bool down)
        {
            if (action == null || !PadMode)
                return false;
            try
            {
                InputScript input = Singleton<InputScript>.Instance;
                Rewired.Player rp = input != null ? input.rewiredPlayer : null;
                return rp != null && (down ? rp.GetButtonDown(action) : rp.GetButton(action));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Push to talk on a controller. Every button of the controller has a vanilla action in
        /// play, so there is none to take: the setting picks which one voice shares, by the
        /// vanilla action it is bound to (<c>VoicePadTalkButton</c>; the names are the buttons
        /// of the game's default layout).
        /// </summary>
        internal static readonly string[] PadVoiceButtons = { "Off", "DpadLeft", "DpadRight", "LeftStick", "RightStick" };
        private static readonly string[] PadVoiceActions = { null, "Cycle", "Effects Cycle", "Reload", "Dodge" };

        internal static int PadVoiceIndex()
        {
            string v = ModConfig.VoicePadTalkButton?.Value ?? "DpadLeft";
            for (int i = 0; i < PadVoiceButtons.Length; i++)
            {
                if (string.Equals(PadVoiceButtons[i], v, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return 1;
        }

        private static string PadVoiceAction() => PadVoiceActions[PadVoiceIndex()];

        internal const string PadTalkAction = "Aim";
        internal const string PadKnobAction = "Attack";

        /// <summary>The walkie's talk button is held: its key, or Aim on a controller.</summary>
        private static bool RadioTalkHeld() => Input.GetKey(RadioTalkKey()) || PadAction(PadTalkAction, down: false);

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

        /// <summary>
        /// Built-in level of received voice, under the VoiceGain setting. Voice sat well below
        /// the game's own sounds at the old levels (1.0, then 2.0: a quiet microphone's speech
        /// peaked near 0.3 of full scale); the setting keeps its meaning (1.4 = normal).
        /// </summary>
        private const float VoiceBaseGain = 3.2f;

        /// <summary>
        /// What a speaker plays against what is in its buffer. The limiter holds a voice's peaks
        /// to <see cref="VoiceLimiter.Ceiling"/> (0.7); this brings them to 0.9 of full scale at
        /// the default volume, for a voice and a radio alike.
        /// </summary>
        private const float VoiceOutLevel = 1.3f;

        private const float LimitKnee = 0.7f;

        /// <summary>
        /// Peaks above the knee bend toward full scale instead of being cut flat: the higher
        /// level would otherwise crackle on every loud syllable.
        /// </summary>
        private static float SoftLimit(float x)
        {
            float a = x < 0f ? -x : x;
            if (a <= LimitKnee)
                return x;
            float over = (a - LimitKnee) / (1f - LimitKnee);
            float y = LimitKnee + (1f - LimitKnee) * (float)Math.Tanh(over);
            return x < 0f ? -y : y;
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
            if (msg.Data == null || msg.Data.Length == 0)
                return;
            Decode(msg);
        }

        private static void SendFrame(LanNetworkManager net, float[] frame)
        {
            try
            {
                // On the radio, a second block: what the radio picked up around this player.
                bool world = _walkieTx && frame.Length == VoiceCodec.PacketSamples
                    && WorldSoundPickup.Read(_worldTx, VoiceCodec.PacketSamples);
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                int at = world ? WorldPrefixBytes : 0;
                int voiceLen = (_voiceEnc ?? (_voiceEnc = new VoiceEncoder(speech: true))).Encode(frame, frame.Length, _encodeBuf, at);
                int len = at + voiceLen;
                if (world)
                {
                    _encodeBuf[0] = (byte)(voiceLen & 0xFF);
                    _encodeBuf[1] = (byte)(voiceLen >> 8);
                    len += (_worldEnc ?? (_worldEnc = new VoiceEncoder(speech: false))).Encode(_worldTx, VoiceCodec.PacketSamples, _encodeBuf, len);
                }
                _encodeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                // The level is the voice's alone: it says how far the voice carries and what creatures hear.
                float level = VoiceCodec.LevelOf(frame, frame.Length);
                int playerId = Math.Max(net.LocalPlayerId, 0);
                ushort seq = _seq++;
                // The mic's sound comes out of its cleaning a moment late: what was said as the
                // key came up is still on its way, and belongs to the transmission.
                if (_walkieTx)
                    _walkieSendUntil = Time.unscaledTime + WalkieSendTailSec;
                bool walkie = _walkieTx || Time.unscaledTime < _walkieSendUntil;
                byte flags = (byte)(walkie ? VoiceDataMessage.FlagWalkie : 0);
                if (world)
                    flags |= VoiceDataMessage.FlagWorld;
                byte levelByte = (byte)Mathf.RoundToInt(level * 255f);
                if (net.Role == NetworkRole.Host)
                    VoiceHearing.Heard(net.LocalPlayerId, level, walkie);
                net.Broadcast(NetMessageType.VoiceData,
                    w => VoiceDataMessage.WriteSlice(w, playerId, seq, flags, levelByte, _encodeBuf, len),
                    DeliveryMethod.Unreliable);
                _txPackets++;
                if (walkie)
                {
                    float[] heard = frame;
                    if (world)
                    {
                        heard = _echoMix;
                        for (int i = 0; i < VoiceCodec.PacketSamples; i++)
                            heard[i] = frame[i] + _worldTx[i] * WorldRxGain;
                    }
                    FeedSelfEcho(heard, level);
                }
            }
            catch (Exception ex)
            {
                if (NetLogThrottle.ShouldLog("voice-send", 10f, out _))
                    ModLog.Warn(LogCat.Audio, "Voice send: " + ex.Message);
            }
        }

        private static void Decode(VoiceDataMessage p)
        {
            Speaker speaker = null;
            try
            {
                speaker = EnsureSpeaker(p.PlayerId);
                // Unreliable packets can arrive late or twice: an older one would play out of order.
                int lost = 0;
                if (speaker.HasSeq)
                {
                    short ahead = (short)(p.Seq - speaker.LastSeq);
                    if (ahead <= 0 && ahead > -1000)
                        return;
                    // A count that jumped back is a talker who started over.
                    lost = ahead > 0 ? ahead - 1 : int.MaxValue;
                }

                // Two blocks when the talker's radio sent what it picked up: the voice's length, the voice, the rest.
                int voiceAt = 0;
                int voiceBytes = p.Data.Length;
                int worldBytes = 0;
                if ((p.Flags & VoiceDataMessage.FlagWorld) != 0)
                {
                    if (p.Data.Length <= WorldPrefixBytes)
                        return;
                    voiceAt = WorldPrefixBytes;
                    voiceBytes = p.Data[0] | (p.Data[1] << 8);
                    worldBytes = p.Data.Length - voiceAt - voiceBytes;
                    if (voiceBytes <= 0 || worldBytes <= 0)
                        return;
                }
                speaker.HasSeq = true;
                speaker.LastSeq = p.Seq;

                VoiceDecoder dec = speaker.VoiceDec ?? (speaker.VoiceDec = new VoiceDecoder());
                int samples = 0;
                if (lost > MaxFilledPackets)
                {
                    dec.Reset();
                    speaker.WorldDec?.Reset();
                }
                else if (lost > 0)
                {
                    // The packet just before this one comes back from the copy this one carries.
                    // Any before that are the decoder's guess, and only inside one stretch of
                    // talk: across a pause there was nothing said to carry on.
                    if (Time.unscaledTime - speaker.LastData < SameTalkSec)
                    {
                        for (int i = 1; i < lost; i++)
                            samples += dec.Conceal(_decodeBuf, samples);
                    }
                    samples += dec.Recover(p.Data, voiceAt, voiceBytes, _decodeBuf, samples);
                    speaker.Recovered += lost;
                }
                int voiceStart = samples;
                int voiceSamples = dec.Decode(p.Data, voiceAt, voiceBytes, _decodeBuf, samples);
                if (voiceSamples <= 0)
                    return;
                samples += voiceSamples;
                int worldSamples = 0;
                if (worldBytes > 0)
                {
                    VoiceDecoder wdec = speaker.WorldDec ?? (speaker.WorldDec = new VoiceDecoder());
                    worldSamples = wdec.Decode(p.Data, voiceAt + voiceBytes, worldBytes, _worldRx, 0);
                }
                bool walkieNow = (p.Flags & VoiceDataMessage.FlagWalkie) != 0;
                bool keyed = walkieNow && !speaker.WalkieActive;
                if (walkieNow != speaker.WalkieActive)
                {
                    speaker.WalkieActive = walkieNow;
                    PlayTalkerClick(speaker, keyDown: walkieNow);
                }
                speaker.Walkie = walkieNow;
                speaker.Level = p.Level / 255f;
                speaker.PacketsIn++;
                speaker.LastData = Time.unscaledTime;
                Speaker radio = speaker.Radio;
                if (walkieNow)
                {
                    ClaimChannel(speaker.Id);
                    radio = EnsureRadioSpeaker(speaker);
                    radio.WalkieActive = true;
                    radio.Level = speaker.Level;
                    radio.LastData = speaker.LastData;
                    radio.RadioHoldUntil = 0f;
                    // The radio this transmission is heard on is picked now, not on the next
                    // frame: its first packet would miss the radio and the beep start late.
                    if (keyed)
                        PlaceNow(speaker);
                }
                else if (radio != null)
                {
                    // The key came up (the mic sends a moment longer, for the last word of a
                    // voice heard directly): the radio playing this talker closes now, with its
                    // squelch tail, and what follows is not on the air.
                    radio.WalkieActive = false;
                    if (radio.RadioWasActive)
                    {
                        radio.RadioWasActive = false;
                        WriteSquelchTail(radio);
                    }
                }

                // The voice itself, always: heard from where the talker stands, as far as it carries.
                WriteVoice(speaker, _decodeBuf, samples);

                // And the transmission on the radio that plays it. Only a radio plays the second
                // block: heard directly, the talker is a voice in a world the listener hears for
                // themselves.
                if (walkieNow && radio.RadioMode)
                {
                    if (worldSamples > 0)
                    {
                        int n = Math.Min(voiceSamples, worldSamples);
                        for (int i = 0; i < n; i++)
                            _decodeBuf[voiceStart + i] += _worldRx[i] * WorldRxGain;
                    }
                    WriteVoice(radio, _decodeBuf, samples);
                }
            }
            catch (Exception ex)
            {
                // A block that would not decode leaves the stream's state unknown.
                speaker?.VoiceDec?.Reset();
                speaker?.WorldDec?.Reset();
                if (NetLogThrottle.ShouldLog("voice-decode", 10f, out _))
                    ModLog.Warn(LogCat.Audio, "Voice decode: " + ex.Message);
            }
        }

        /// <summary>
        /// One packet of voice into a speaker's buffer: through the radio (squelch, band, static)
        /// when it is heard on one. <paramref name="buf"/> is changed in place.
        /// </summary>
        private static void WriteVoice(Speaker speaker, float[] buf, int samples)
        {
            float gain = (ModConfig.VoiceGain?.Value ?? 1.4f) * VoiceBaseGain;
            // Only what the talker's radio sends goes through the listener's radio.
            bool radioMode = speaker.RadioMode && speaker.WalkieActive;
            bool radioStart = radioMode && !speaker.RadioWasActive;
            if (radioMode)
                speaker.RadioWasActive = true;
            if (radioStart)
            {
                speaker.RadioHp = Biquad.HighPass(RadioLowHz, _sampleRate);
                speaker.RadioLp = Biquad.LowPass(RadioHighHz, _sampleRate);
                StartStatic(speaker);
                speaker.BeepPos = 0;
                speaker.VoiceShare = VoiceShareOf(speaker.RadioQuality);
            }

            if (radioMode)
                RadioPacket(speaker, buf, samples);

            lock (speaker.Lock)
            {
                // Nothing buffered and not playing: a new stretch of talk.
                if (speaker.Buffered == 0 && speaker.Priming)
                    speaker.Limiter.Reset();
                for (int i = 0; i < samples; i++)
                {
                    if (speaker.Buffered >= speaker.Ring.Length)
                        break;
                    float sample = buf[i] * gain;
                    if (radioMode)
                        sample = RadioSample(speaker, sample);
                    // Turned down ahead of a peak; the soft limit only catches what is left.
                    speaker.Ring[speaker.WritePos] = SoftLimit(speaker.Limiter.Process(sample));
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

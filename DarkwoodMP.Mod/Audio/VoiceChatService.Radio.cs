using System;
using DWMPHorde.Networking;
using DWMPHorde.Config;
using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// How a voice sounds through the walkie: a narrow band like a handheld radio's speaker,
    /// driven a little into distortion, under a hiss that grows with the distance between the two
    /// radios, opened by a squelch click and closed by a burst of static. The talker hears their
    /// own radio click when keying and letting go.
    /// </summary>
    public static partial class VoiceChatService
    {
        private const float RadioLowHz = 380f;
        private const float RadioHighHz = 2900f;
        private const float RadioDrive = 2.4f;
        private const float RadioHissNear = 0.006f;
        private const float RadioHissFar = 0.045f;

        private static AudioSource _localRadio; // process-scoped: 2D source for this player's own radio clicks
        private static AudioClip _keyClip; // process-scoped: recorded asset (RadioSamples)
        private static AudioClip _releaseClip; // process-scoped: recorded asset (RadioSamples)

        /// <summary>RBJ cookbook second-order filter (Q 0.707), direct form I.</summary>
        internal struct Biquad
        {
            private float _b0, _b1, _b2, _a1, _a2;
            private float _x1, _x2, _y1, _y2;

            internal static Biquad LowPass(float hz, float rate) => Make(hz, rate, low: true);
            internal static Biquad HighPass(float hz, float rate) => Make(hz, rate, low: false);

            private static Biquad Make(float hz, float rate, bool low)
            {
                double w = 2.0 * Math.PI * Math.Min(hz, rate * 0.45f) / rate;
                double cos = Math.Cos(w);
                double alpha = Math.Sin(w) / (2.0 * 0.7071);
                double a0 = 1.0 + alpha;
                double b1 = low ? 1.0 - cos : -(1.0 + cos);
                double b0 = low ? b1 / 2.0 : (1.0 + cos) / 2.0;
                return new Biquad
                {
                    _b0 = (float)(b0 / a0),
                    _b1 = (float)(b1 / a0),
                    _b2 = (float)(b0 / a0),
                    _a1 = (float)(-2.0 * cos / a0),
                    _a2 = (float)((1.0 - alpha) / a0)
                };
            }

            internal float Run(float x)
            {
                float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
                _x2 = _x1; _x1 = x;
                _y2 = _y1; _y1 = y;
                return y;
            }
        }

        private static readonly System.Random _noise = new System.Random(); // process-scoped: main thread only (decode side)

        private static float Noise() => (float)(_noise.NextDouble() * 2.0 - 1.0);

        /// <summary>
        /// One packet of voice as the radio gets it (decode side, before the radio's band and
        /// drive): quieter and breaking up as the signal weakens, a whistle under a second
        /// talker keying over this one, and the howl of feedback.
        /// </summary>
        private static void RadioPacket(Speaker s, float[] x, int n)
        {
            float q = s.RadioQuality;
            float voice = Mathf.Lerp(0.55f, 1f, q);
            // A weak signal drops out in pieces of 10..40 ms, more often the weaker it gets.
            if (s.BreakupLeft <= 0f && q < 0.6f && _noise.NextDouble() < Math.Pow((0.6f - q) / 0.6f, 1.5) * 0.75)
                s.BreakupLeft = 160 + (float)_noise.NextDouble() * 480f;
            double hetStep = 2.0 * Math.PI / VoiceCodec.SampleRate;
            for (int i = 0; i < n; i++)
            {
                float v = x[i] * voice;
                if (s.BreakupLeft > 0f)
                {
                    v = BreakupStatic(s);
                    s.BreakupLeft--;
                }
                if (s.Doubling > 0f)
                {
                    // Two carriers a little apart beat into a wobbling whistle; the voice under it garbles.
                    double f = 950.0 + 320.0 * Math.Sin(s.HetPhase * 0.0021);
                    s.HetPhase += hetStep * f;
                    v = v * (1f - 0.35f * s.Doubling) + s.Doubling * (0.16f * (float)Math.Sin(s.HetPhase) + 0.05f * Noise());
                }
                if (s.Howl > 0f)
                    v += s.Howl * s.Howl * 0.5f * HowlWave(ref s.HowlPhase, VoiceCodec.SampleRate);
                x[i] = v;
            }
        }

        /// <summary>Feedback: a tone near 2 kHz that drifts and warbles, with its octave.</summary>
        internal static float HowlWave(ref double phase, int rate)
        {
            double t = phase / (2.0 * Math.PI * 2000.0);
            double f = 2050.0 + 260.0 * Math.Sin(t * 2.0 * Math.PI * 0.6) + 70.0 * Math.Sin(t * 2.0 * Math.PI * 5.5);
            phase += 2.0 * Math.PI * f / rate;
            return (float)(Math.Sin(phase) * 0.8 + Math.Sin(phase * 2.0) * 0.25);
        }

        /// <summary>
        /// The feedback heard in the room: one source at the radio that howls. Every frame the
        /// strongest howl offered (<see cref="OfferHowl"/>) wins; none offered and it fades out.
        /// </summary>
        private sealed class HowlEmitter : MonoBehaviour
        {
            public volatile float Amp;
            private double _phase;
            private float _amp;
            private int _rate = 48000;

            private void Awake() => _rate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;

            private void OnAudioFilterRead(float[] data, int channels)
            {
                float from = _amp;
                float to = Amp;
                _amp = to;
                int frames = data.Length / channels;
                for (int i = 0; i < frames; i++)
                {
                    float a = from + (to - from) * i / frames;
                    float v = a <= 0f ? 0f : HowlWave(ref _phase, _rate) * a;
                    for (int c = 0; c < channels; c++)
                        data[i * channels + c] *= v;
                }
            }
        }

        private static HowlEmitter _howl; // process-scoped: the one feedback source
        private static float _howlOffer; // process-scoped: strongest howl offered this frame
        private static Vector3 _howlAt; // process-scoped: where it is
        private static float _howlLevel; // process-scoped: the emitter's smoothed level
        private static int _howlFrame = -1; // process-scoped: frame the offers belong to

        /// <summary>The loudest feedback this frame and where (host: creatures hear it, <see cref="VoiceHearing"/>).</summary>
        internal static float HowlNow => _howlLevel;
        internal static Vector3 HowlAt => _howlAt;

        private static void OfferHowl(float amount, Vector3 at)
        {
            if (_howlFrame != Time.frameCount)
            {
                _howlFrame = Time.frameCount;
                _howlOffer = 0f;
            }
            if (amount > _howlOffer)
            {
                _howlOffer = amount;
                _howlAt = at;
            }
        }

        /// <summary>
        /// This player keying their walkie right by another player's live radio: it plays them
        /// back into their own mic and howls (the receivers' side is in <see cref="RadioPacket"/>).
        /// </summary>
        private static float _localHowl; // process-scoped: smoothed

        private static void TickHowl(LanNetworkManager net, float dt)
        {
            Vector3 at = Vector3.zero;
            bool feeding = false;
            Player p = Player.Instance;
            if (_walkieTx && p != null && net != null)
            {
                foreach (Players.RemotePlayerProxy o in net.EnumerateRemoteProxies())
                {
                    if (o == null || !o.isActiveAndEnabled)
                        continue;
                    if (!net.RemotePlayers.TryGetValue(o.PlayerId, out RemotePlayerState st) || st == null || !WalkieStates.Live(st.WalkieState))
                        continue;
                    if (DistXz(o.transform.position, p.transform.position) < FeedbackRange)
                    {
                        feeding = true;
                        at = o.transform.position;
                        break;
                    }
                }
            }
            _localHowl = Mathf.MoveTowards(_localHowl, feeding ? 1f : 0f, dt * (feeding ? 1.2f : 3f));
            if (_localHowl > 0f)
                OfferHowl(_localHowl, feeding ? at : _howlAt);

            float target = _howlFrame == Time.frameCount ? _howlOffer : 0f;
            _howlLevel = Mathf.MoveTowards(_howlLevel, target, dt * 4f);
            if (_howlLevel <= 0f && _howl == null)
                return;
            if (_howl == null)
            {
                if (_root == null)
                {
                    _root = new GameObject("YokWare_Voice");
                    UnityEngine.Object.DontDestroyOnLoad(_root);
                }
                var go = new GameObject("YokWare_RadioHowl");
                go.transform.SetParent(_root.transform, false);
                AudioSource src = go.AddComponent<AudioSource>();
                src.clip = CarrierClip();
                src.loop = true;
                src.playOnAwake = false;
                src.spatialBlend = 1f;
                src.rolloffMode = AudioRolloffMode.Linear;
                src.minDistance = NearRadioFull;
                src.maxDistance = NearRadioRange * 1.5f;
                src.dopplerLevel = 0f;
                _howl = go.AddComponent<HowlEmitter>();
                src.Play();
            }
            _howl.transform.position = _howlAt;
            float vol = Mathf.Clamp01(ModConfig.VoiceVolume?.Value ?? 1f);
            _howl.Amp = _howlLevel * _howlLevel * 0.35f * vol;
        }

        /// <summary>One voice sample through the radio (decode side, main thread).</summary>
        private static float RadioSample(Speaker s, float x)
        {
            x = s.RadioHp.Run(x);
            x = s.RadioLp.Run(x);
            x *= RadioDrive;
            x /= 1f + Mathf.Abs(x);
            return x * 1.3f + StaticSample(s);
        }

        /// <summary>The steady static under a clear transmission, at this level (the recording is at RMS 0.12).</summary>
        private const float StaticNearGain = 0.1f;
        /// <summary>The out-of-range static at no signal at all (the recording is at RMS 0.18).</summary>
        private const float StaticFarGain = 0.35f;

        private static float[] _staticNear; // process-scoped: recorded asset (RadioSamples), at the voice rate
        private static float[] _staticFar; // process-scoped: recorded asset (RadioSamples), at the voice rate
        private static bool _staticLoaded; // process-scoped: tried once

        private static void EnsureStatic()
        {
            if (_staticLoaded)
                return;
            _staticLoaded = true;
            _staticNear = RadioSamples.LoadSamples("static_near.wav", VoiceCodec.SampleRate);
            _staticFar = RadioSamples.LoadSamples("static_far.wav", VoiceCodec.SampleRate);
        }

        /// <summary>A transmission opens on this speaker: its static starts somewhere in the recordings.</summary>
        private static void StartStatic(Speaker s)
        {
            EnsureStatic();
            if (_staticNear != null)
                s.StaticNearPos = _noise.Next(_staticNear.Length);
            if (_staticFar != null)
                s.StaticFarPos = _noise.Next(_staticFar.Length);
        }

        /// <summary>How much of the out-of-range static a signal of this quality carries.</summary>
        private static float FarStaticGain(float quality)
        {
            float weak = 1f - Mathf.Clamp01(quality);
            return weak * Mathf.Sqrt(weak) * StaticFarGain;
        }

        /// <summary>
        /// The radio's static under a voice: a recorded handheld's steady static while the signal
        /// is clear, a recorded out-of-range static rising over it as the signal weakens. The
        /// generated hiss stands in if the recordings are missing.
        /// </summary>
        private static float StaticSample(Speaker s)
        {
            float[] near = _staticNear, far = _staticFar;
            if (near == null || far == null)
                return Noise() * s.RadioHiss;
            if (++s.StaticNearPos >= near.Length) s.StaticNearPos = 0;
            if (++s.StaticFarPos >= far.Length) s.StaticFarPos = 0;
            return near[s.StaticNearPos] * StaticNearGain + far[s.StaticFarPos] * s.FarStatic;
        }

        /// <summary>What a dropout sounds like: the out-of-range static alone (before the radio's band and drive).</summary>
        private static float BreakupStatic(Speaker s)
        {
            float[] far = _staticFar;
            if (far == null)
                return Noise() * s.RadioHiss * 2.5f;
            if (++s.StaticFarPos >= far.Length) s.StaticFarPos = 0;
            return far[s.StaticFarPos] * 0.3f;
        }

        /// <summary>
        /// The squelch: a short click as the far radio opens, a fading burst of static as it closes.
        /// Band-limited like the voice so it sounds from the same little speaker.
        /// </summary>
        private static void WriteSquelch(Speaker speaker, bool open)
        {
            if (_sampleRate == 0)
                return;
            if (!open && speaker.RadioQuality > RadioSquelchQuality)
                WriteRogerBeep(speaker);
            float seconds = open ? 0.035f : 0.16f;
            float amp = open ? 0.35f : 0.22f;
            int n = (int)(_sampleRate * seconds);
            Biquad hp = Biquad.HighPass(RadioLowHz, _sampleRate);
            Biquad lp = Biquad.LowPass(RadioHighHz * 1.2f, _sampleRate);
            lock (speaker.Lock)
            {
                for (int i = 0; i < n; i++)
                {
                    if (speaker.Buffered >= speaker.Ring.Length)
                        break;
                    float k = (float)i / n;
                    float env = open ? (1f - k) * (1f - k) * (1f - k) : (1f - k) * (1f - k);
                    float v = lp.Run(hp.Run(Noise())) * amp * env * 2.5f;
                    speaker.Ring[speaker.WritePos] = Mathf.Clamp(v, -1f, 1f);
                    speaker.WritePos = (speaker.WritePos + 1) % speaker.Ring.Length;
                    speaker.Buffered++;
                }
                // The tail plays even though no voice follows it to fill the prime buffer.
                if (!open)
                    speaker.PrimeRelease = true;
            }
        }

        private static void EnsureSquelchClips()
        {
            // The talk key: a real handheld's call beep and squelch tail; the generated ones only if missing.
            if (_keyClip == null)
                _keyClip = RadioSamples.Load("key.wav", "yokware_radio_key")
                    ?? SquelchClip("yokware_radio_key", 0.03f, 0.5f, cubic: true);
            if (_releaseClip == null)
                _releaseClip = RadioSamples.Load("release.wav", "yokware_radio_release")
                    ?? SquelchClip("yokware_radio_release", 0.14f, 0.3f, cubic: false);
        }

        /// <summary>A talker's own radio clicking as they key it and let go, heard by players near them.</summary>
        private static void PlayTalkerClick(Speaker s, bool keyDown)
        {
            try
            {
                if (s.Click == null || ModRuntime.Network?.GetProxy(s.Id) == null)
                    return;
                EnsureSquelchClips();
                float vol = Mathf.Clamp01((ModConfig.VoiceVolume?.Value ?? 1f) * 0.3f);
                s.Click.PlayOneShot(keyDown ? _keyClip : _releaseClip, vol);
            }
            catch { /* audio not ready */ }
        }

        /// <summary>The far radio's roger beep: two short tones as the talker lets go, before the squelch tail.</summary>
        private static void WriteRogerBeep(Speaker speaker)
        {
            int rate = (int)_sampleRate;
            int each = rate * 70 / 1000;
            double phase = 0;
            lock (speaker.Lock)
            {
                for (int i = 0; i < each * 2; i++)
                {
                    if (speaker.Buffered >= speaker.Ring.Length)
                        break;
                    double f = i < each ? 1250.0 : 900.0;
                    phase += 2.0 * Math.PI * f / rate;
                    int k = i % each;
                    float env = Mathf.Min(1f, Mathf.Min(k, each - k) / (rate * 0.004f));
                    float v = (float)Math.Sin(phase) * 0.22f * env + Noise() * speaker.RadioHiss;
                    speaker.Ring[speaker.WritePos] = Mathf.Clamp(v, -1f, 1f);
                    speaker.WritePos = (speaker.WritePos + 1) % speaker.Ring.Length;
                    speaker.Buffered++;
                }
            }
        }

        internal enum RadioSound { PowerOn, PowerOff, Dead, LowBattery }

        private static AudioClip _powerOnClip; // process-scoped: generated asset
        private static AudioClip _powerOffClip; // process-scoped: generated asset
        private static AudioClip _deadClip; // process-scoped: generated asset
        private static AudioClip _lowBatteryClip; // process-scoped: generated asset

        /// <summary>
        /// This player's own radio: the knob's click and the speaker coming alive (on), the click
        /// and a dying hiss (off), a click and nothing (a flat battery), the low-battery double chirp.
        /// </summary>
        private static void PlayLocalRadioSound(RadioSound which)
        {
            try
            {
                EnsureLocalRadioSource();
                if (_powerOnClip == null)
                {
                    _powerOnClip = KnobClip("yokware_radio_on", hiss: 0.18f, hissSec: 0.22f);
                    _powerOffClip = KnobClip("yokware_radio_off", hiss: 0.1f, hissSec: 0.08f);
                    _deadClip = KnobClip("yokware_radio_dead", hiss: 0f, hissSec: 0f);
                    _lowBatteryClip = ChirpClip("yokware_radio_lowbatt");
                }
                // The knob is a recording, one of four takes at random; on, off and a flat battery
                // differ in what the speaker does after it, not in the knob.
                AudioClip knob = which == RadioSound.LowBattery ? null : KnobTake();
                AudioClip clip = knob != null ? knob
                    : which == RadioSound.PowerOn ? _powerOnClip
                    : which == RadioSound.PowerOff ? _powerOffClip
                    : which == RadioSound.Dead ? _deadClip : _lowBatteryClip;
                float vol = Mathf.Clamp01((ModConfig.VoiceVolume?.Value ?? 1f) * (HoldingWalkie() ? 0.4f : 0.22f)
                    * (knob != null ? KnobLevel : 1f));
                PlayLocal(clip, vol);
            }
            catch { /* audio not ready */ }
        }

        /// <summary>The recorded knob takes are quieter-mastered than the generated click was: played this much up.</summary>
        private const float KnobLevel = 1.6f;
        private const int KnobTakes = 4;

        private static AudioClip[] _knobClips; // process-scoped: recorded assets (RadioSamples)
        private static int _lastKnob = -1; // process-scoped: the take played last

        /// <summary>One of the recorded knob takes, not the one played last; null if none loaded.</summary>
        private static AudioClip KnobTake()
        {
            if (_knobClips == null)
            {
                _knobClips = new AudioClip[KnobTakes];
                for (int i = 0; i < KnobTakes; i++)
                    _knobClips[i] = RadioSamples.Load("knob" + (i + 1) + ".wav", "yokware_radio_knob" + (i + 1));
            }
            for (int tries = 0; tries < 8; tries++)
            {
                int i = _noise.Next(KnobTakes);
                if (i == _lastKnob || _knobClips[i] == null)
                    continue;
                _lastKnob = i;
                return _knobClips[i];
            }
            return null;
        }

        private static AudioReverbFilter _localRadioReverb; // process-scoped: with _localRadio

        private static void EnsureLocalRadioSource()
        {
            if (_root == null)
            {
                _root = new GameObject("YokWare_Voice");
                UnityEngine.Object.DontDestroyOnLoad(_root);
            }
            if (_localRadio == null)
            {
                var go = new GameObject("YokWare_LocalRadio");
                go.transform.SetParent(_root.transform, false);
                _localRadio = go.AddComponent<AudioSource>();
                _localRadio.playOnAwake = false;
                _localRadio.spatialBlend = 0f;
                _localRadioReverb = go.AddComponent<AudioReverbFilter>();
                _localRadioReverb.enabled = false;
            }
        }

        /// <summary>
        /// A sound of this player's own radio. It is in their hands, so nothing stands between
        /// it and them; it gets the room's reverb when they stand inside, as vanilla gives a
        /// sound made indoors.
        /// </summary>
        private static void PlayLocal(AudioClip clip, float vol)
        {
            if (clip == null)
                return;
            EnsureLocalRadioSource();
            Player p = Player.Instance;
            bool inside = p != null && p.isInside;
            if (_localRadioReverb != null && _localRadioReverb.enabled != inside)
                _localRadioReverb.enabled = inside;
            _localRadio.PlayOneShot(clip, vol);
        }

        /// <summary>A knob's dry click, then (optionally) the speaker's hiss swelling and closing.</summary>
        private static AudioClip KnobClip(string name, float hiss, float hissSec)
        {
            const int rate = 44100;
            int click = rate * 6 / 1000;
            int n = click + (int)(rate * hissSec) + 1;
            var data = new float[n];
            Biquad hp = Biquad.HighPass(RadioLowHz, rate);
            Biquad lp = Biquad.LowPass(RadioHighHz * 1.2f, rate);
            Biquad clickLp = Biquad.LowPass(3500f, rate);
            for (int i = 0; i < n; i++)
            {
                float v = 0f;
                if (i < click)
                {
                    float k = 1f - (float)i / click;
                    v = clickLp.Run(Noise()) * k * k * 1.4f;
                }
                else if (hiss > 0f)
                {
                    float k = (float)(i - click) / (n - click);
                    float env = Mathf.Sin(k * Mathf.PI);
                    v = lp.Run(hp.Run(Noise())) * hiss * env * 2.5f;
                }
                data[i] = Mathf.Clamp(v, -1f, 1f);
            }
            AudioClip clip = AudioClip.Create(name, n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>Two short high chirps, as a radio warns of a low battery.</summary>
        private static AudioClip ChirpClip(string name)
        {
            const int rate = 44100;
            int each = rate * 50 / 1000;
            int gap = rate * 70 / 1000;
            int n = each * 2 + gap;
            var data = new float[n];
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                bool on = i < each || i >= each + gap;
                int k = i < each ? i : i - each - gap;
                phase += 2.0 * Math.PI * 2400.0 / rate;
                float env = on ? Mathf.Min(1f, Mathf.Min(k, each - k) / (rate * 0.003f)) : 0f;
                data[i] = (float)Math.Sin(phase) * 0.3f * env;
            }
            AudioClip clip = AudioClip.Create(name, n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>This player's own radio: the call beep when the key goes down, the squelch tail when it comes up.</summary>
        private static void PlayLocalSquelch(bool keyDown)
        {
            try
            {
                EnsureLocalRadioSource();
                EnsureSquelchClips();
                float vol = Mathf.Clamp01((ModConfig.VoiceVolume?.Value ?? 1f) * 0.35f);
                PlayLocal(keyDown ? _keyClip : _releaseClip, vol);
            }
            catch { /* audio not ready */ }
        }

        private static AudioClip SquelchClip(string name, float seconds, float amp, bool cubic)
        {
            const int rate = 44100;
            int n = (int)(rate * seconds);
            var data = new float[n];
            Biquad hp = Biquad.HighPass(RadioLowHz, rate);
            Biquad lp = Biquad.LowPass(RadioHighHz * 1.2f, rate);
            for (int i = 0; i < n; i++)
            {
                float k = (float)i / n;
                float env = cubic ? (1f - k) * (1f - k) * (1f - k) : (1f - k) * (1f - k);
                data[i] = Mathf.Clamp(lp.Run(hp.Run(Noise())) * amp * env * 2.5f, -1f, 1f);
            }
            AudioClip clip = AudioClip.Create(name, n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}

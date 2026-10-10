using System;
using DWMPHorde.Networking;
using DWMPHorde.Config;
using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// How a voice sounds through the walkie: a narrow band like a handheld radio's speaker,
    /// driven a little into distortion, under static that grows with the distance between the two
    /// radios, opened by the caller's beep and closed by a squelch tail. The talker hears their
    /// own radio beep when keying and its squelch when letting go. Every sound of the radio is a
    /// recording (<see cref="RadioSamples"/>); a missing one is silence, nothing is generated.
    /// </summary>
    public static partial class VoiceChatService
    {
        private const float RadioLowHz = 380f;
        private const float RadioHighHz = 2900f;
        private const float RadioDrive = 2.4f;

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

        /// <summary>
        /// One packet of voice as the radio gets it (decode side, before the radio's band and
        /// drive): quieter and breaking up as the signal weakens, and buried in static under a
        /// second talker keying over this one.
        /// </summary>
        private static void RadioPacket(Speaker s, float[] x, int n)
        {
            float q = s.RadioQuality;
            float voice = Mathf.Lerp(0.55f, 1f, q);
            // A weak signal drops out in pieces of 10..40 ms, more often the weaker it gets.
            if (s.BreakupLeft <= 0f && q < 0.6f && _noise.NextDouble() < Math.Pow((0.6f - q) / 0.6f, 1.5) * 0.75)
                s.BreakupLeft = 160 + (float)_noise.NextDouble() * 480f;
            for (int i = 0; i < n; i++)
            {
                float v = x[i] * voice;
                if (s.BreakupLeft > 0f)
                {
                    v = BreakupStatic(s);
                    s.BreakupLeft--;
                }
                // Two talkers on one channel: the voice garbles under the static of the clash.
                if (s.Doubling > 0f)
                    v = v * (1f - 0.35f * s.Doubling) + s.Doubling * 0.6f * BreakupStatic(s);
                x[i] = v;
            }
        }

        /// <summary>One voice sample through the radio (decode side, main thread).</summary>
        private static float RadioSample(Speaker s, float x)
        {
            // The caller's beep, as their radio sends it ahead of the voice.
            if (s.BeepPos >= 0)
            {
                float[] beep = _keyTone;
                if (beep != null && s.BeepPos < beep.Length)
                    x += beep[s.BeepPos++] * BeepGain;
                else
                    s.BeepPos = -1;
            }
            x = s.RadioHp.Run(x);
            x = s.RadioLp.Run(x);
            x *= RadioDrive;
            x /= 1f + Mathf.Abs(x);
            return x * 1.3f + StaticSample(s);
        }

        /// <summary>The caller's beep and the squelch tail inside a received transmission, against the voice.</summary>
        private const float BeepGain = 0.5f;
        private const float TailGain = 0.6f;
        /// <summary>The steady static under a clear transmission, at this level (the recording is at RMS 0.055, all of it in the radio's band).</summary>
        private const float StaticNearGain = 0.1f;
        /// <summary>The out-of-range static at no signal at all (the recording is at RMS 0.18).</summary>
        private const float StaticFarGain = 0.35f;

        private static float[] _staticNear; // process-scoped: recorded asset (RadioSamples), at the voice rate
        private static float[] _staticFar; // process-scoped: recorded asset (RadioSamples), at the voice rate
        private static float[] _keyTone; // process-scoped: recorded asset (RadioSamples), at the voice rate
        private static float[] _releaseTail; // process-scoped: recorded asset (RadioSamples), at the voice rate
        private static bool _staticLoaded; // process-scoped: tried once

        private static void EnsureStatic()
        {
            if (_staticLoaded)
                return;
            _staticLoaded = true;
            _staticNear = RadioSamples.LoadSamples("static_near.wav", VoiceCodec.SampleRate);
            _staticFar = RadioSamples.LoadSamples("static_far.wav", VoiceCodec.SampleRate);
            _keyTone = RadioSamples.LoadSamples("key16.wav", VoiceCodec.SampleRate);
            _releaseTail = RadioSamples.LoadSamples("release16.wav", VoiceCodec.SampleRate);
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
        /// is clear, a recorded out-of-range static rising over it as the signal weakens.
        /// </summary>
        private static float StaticSample(Speaker s)
        {
            float[] near = _staticNear, far = _staticFar;
            if (near == null || far == null)
                return 0f;
            if (++s.StaticNearPos >= near.Length) s.StaticNearPos = 0;
            if (++s.StaticFarPos >= far.Length) s.StaticFarPos = 0;
            return near[s.StaticNearPos] * StaticNearGain + far[s.StaticFarPos] * s.FarStatic;
        }

        /// <summary>What a dropout sounds like: the out-of-range static alone (before the radio's band and drive).</summary>
        private static float BreakupStatic(Speaker s)
        {
            float[] far = _staticFar;
            if (far == null)
                return 0f;
            if (++s.StaticFarPos >= far.Length) s.StaticFarPos = 0;
            return far[s.StaticFarPos] * 0.3f;
        }

        /// <summary>
        /// A transmission ends on this speaker: the far radio's squelch tail (a recording), after
        /// whatever voice is still buffered.
        /// </summary>
        private static void WriteSquelchTail(Speaker speaker)
        {
            EnsureStatic();
            speaker.BeepPos = -1;
            float[] tail = _releaseTail;
            lock (speaker.Lock)
            {
                for (int i = 0; tail != null && i < tail.Length; i++)
                {
                    if (speaker.Buffered >= speaker.Ring.Length)
                        break;
                    speaker.Ring[speaker.WritePos] = Mathf.Clamp(tail[i] * TailGain, -1f, 1f);
                    speaker.WritePos = (speaker.WritePos + 1) % speaker.Ring.Length;
                    speaker.Buffered++;
                }
                // What is buffered plays out even though no voice follows it to fill the prime buffer.
                speaker.PrimeRelease = true;
            }
        }

        private static void EnsureSquelchClips()
        {
            // The talk key: a real handheld's call beep and squelch tail.
            if (_keyClip == null)
                _keyClip = RadioSamples.Load("key.wav", "yokware_radio_key");
            if (_releaseClip == null)
                _releaseClip = RadioSamples.Load("release.wav", "yokware_radio_release");
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
                AudioClip clip = keyDown ? _keyClip : _releaseClip;
                if (clip != null)
                    s.Click.PlayOneShot(clip, vol);
            }
            catch { /* audio not ready */ }
        }

        internal enum RadioSound { PowerOn, PowerOff, Dead }

        /// <summary>
        /// This player's own radio: its knob, switched on, off, or on with a flat battery. One
        /// of four recorded takes at random; what differs between the three is whether the
        /// radio then works, not the knob.
        /// </summary>
        private static void PlayLocalRadioSound(RadioSound which)
        {
            try
            {
                AudioClip knob = KnobTake();
                if (knob == null)
                    return;
                float vol = Mathf.Clamp01((ModConfig.VoiceVolume?.Value ?? 1f) * (HoldingWalkie() ? 0.4f : 0.22f) * KnobLevel);
                PlayLocal(knob, vol);
            }
            catch { /* audio not ready */ }
        }

        /// <summary>The knob takes peak at 0.7 on the click itself (their rumble is cut): played this much down.</summary>
        private const float KnobLevel = 0.6f;
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
    }
}

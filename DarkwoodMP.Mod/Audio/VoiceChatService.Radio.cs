using System;
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
        private static AudioClip _keyClip; // process-scoped: generated asset
        private static AudioClip _releaseClip; // process-scoped: generated asset

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

        /// <summary>One voice sample through the radio (decode side, main thread).</summary>
        private static float RadioSample(Speaker s, float x)
        {
            x = s.RadioHp.Run(x);
            x = s.RadioLp.Run(x);
            x *= RadioDrive;
            x /= 1f + Mathf.Abs(x);
            return x * 1.3f + Noise() * s.RadioHiss;
        }

        /// <summary>
        /// The squelch: a short click as the far radio opens, a fading burst of static as it closes.
        /// Band-limited like the voice so it sounds from the same little speaker.
        /// </summary>
        private static void WriteSquelch(Speaker speaker, bool open)
        {
            if (_sampleRate == 0)
                return;
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

        /// <summary>This player's own radio: a click when the key goes down, a short hiss when it comes up.</summary>
        private static void PlayLocalSquelch(bool keyDown)
        {
            try
            {
                if (_root == null)
                {
                    _root = new GameObject("YokWare_Voice");
                    UnityEngine.Object.DontDestroyOnLoad(_root);
                }
                if (_localRadio == null)
                {
                    _localRadio = _root.AddComponent<AudioSource>();
                    _localRadio.playOnAwake = false;
                    _localRadio.spatialBlend = 0f;
                }
                if (_keyClip == null)
                    _keyClip = SquelchClip("yokware_radio_key", 0.03f, 0.5f, cubic: true);
                if (_releaseClip == null)
                    _releaseClip = SquelchClip("yokware_radio_release", 0.14f, 0.3f, cubic: false);
                float vol = Mathf.Clamp01((ModConfig.VoiceVolume?.Value ?? 1f) * 0.35f);
                _localRadio.PlayOneShot(keyDown ? _keyClip : _releaseClip, vol);
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

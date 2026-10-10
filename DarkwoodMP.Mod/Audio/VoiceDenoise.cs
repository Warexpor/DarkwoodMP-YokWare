using System;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// Noise suppression for the microphone, at the voice rate: steady noise (fans, hiss, hum, a
    /// room's tone) is taken out of each frequency band while speech passes.
    /// <para>
    /// Spectral: 32 ms windows a quarter apart. The noise in each band is followed by minimum
    /// tracking with a speech-presence probability (Cohen's MCRA), so it keeps adapting under
    /// speech and never needs a "silence first" calibration. The gain of each band is a Wiener
    /// gain from a decision-directed estimate of its signal-to-noise ratio (Ephraim and Malah),
    /// which is what keeps the leftover noise from turning into chirps ("musical noise"), with
    /// a floor so the room does not drop to dead silence between words.
    /// </para>
    /// Managed code only, like the codec: the same on every install. The output is the input
    /// 32 ms later.
    /// </summary>
    internal sealed class VoiceDenoise
    {
        private const int N = 512;
        private const int Hop = N / 4;
        private const int Bins = N / 2 + 1;

        /// <summary>The most a band is turned down (about -19 dB).</summary>
        private const float GainFloor = 0.11f;
        /// <summary>The noise estimate is taken this much higher than tracked: minimum tracking reads low.</summary>
        private const float NoiseBias = 1.4f;
        /// <summary>Weight of the previous frame's cleaned signal in the signal-to-noise estimate.</summary>
        private const float DecisionDirected = 0.98f;
        /// <summary>Frames in one minimum-search window (0.8 s); a noise that rises is followed within two of them.</summary>
        private const int MinWindowFrames = 100;
        /// <summary>A band this far over its minimum holds speech.</summary>
        private const float SpeechRatio = 5f;
        /// <summary>Below this the mic's rumble only (bins of 31.25 Hz).</summary>
        private const int FirstBin = 3;

        private static readonly float[] Window = MakeWindow();
        private static readonly float[] Cos = MakeTable(cos: true);
        private static readonly float[] Sin = MakeTable(cos: false);
        private static readonly int[] Reverse = MakeReverse();

        private readonly float[] _in = new float[N];
        private readonly float[] _ola = new float[N];
        private readonly float[] _pending = new float[Hop];
        private readonly float[] _ready = new float[Hop];
        private readonly float[] _re = new float[N];
        private readonly float[] _im = new float[N];
        private readonly float[] _power = new float[Bins];
        private readonly float[] _smooth = new float[Bins];
        private readonly float[] _min = new float[Bins];
        private readonly float[] _minNext = new float[Bins];
        private readonly float[] _presence = new float[Bins];
        private readonly float[] _noise = new float[Bins];
        private readonly float[] _cleanPrev = new float[Bins];
        private int _fill;
        private int _frames;

        private static float[] MakeWindow()
        {
            var w = new float[N];
            for (int i = 0; i < N; i++)
                w[i] = 0.5f - 0.5f * (float)Math.Cos(2.0 * Math.PI * i / N);
            return w;
        }

        private static float[] MakeTable(bool cos)
        {
            var t = new float[N / 2];
            for (int i = 0; i < t.Length; i++)
                t[i] = (float)(cos ? Math.Cos(2.0 * Math.PI * i / N) : Math.Sin(2.0 * Math.PI * i / N));
            return t;
        }

        private static int[] MakeReverse()
        {
            int bits = 0;
            while ((1 << bits) < N)
                bits++;
            var r = new int[N];
            for (int i = 0; i < N; i++)
            {
                int v = 0;
                for (int b = 0; b < bits; b++)
                {
                    if ((i & (1 << b)) != 0)
                        v |= 1 << (bits - 1 - b);
                }
                r[i] = v;
            }
            return r;
        }

        /// <summary>Clean <paramref name="count"/> samples in place (what comes out is 32 ms behind what goes in).</summary>
        internal void Process(float[] x, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _pending[_fill] = x[offset + i];
                x[offset + i] = _ready[_fill];
                if (++_fill == Hop)
                {
                    _fill = 0;
                    Frame();
                }
            }
        }

        private void Frame()
        {
            Array.Copy(_in, Hop, _in, 0, N - Hop);
            Array.Copy(_pending, 0, _in, N - Hop, Hop);
            for (int i = 0; i < N; i++)
            {
                _re[i] = _in[i] * Window[i];
                _im[i] = 0f;
            }
            Fft(_re, _im, inverse: false);

            for (int k = 0; k < Bins; k++)
                _power[k] = _re[k] * _re[k] + _im[k] * _im[k];

            bool first = _frames == 0;
            bool newWindow = _frames % MinWindowFrames == 0;
            _frames++;
            for (int k = 0; k < Bins; k++)
            {
                float p = _power[k];
                // Smoothed over neighbouring bands and over time: what the minimum is searched in.
                float across = 0.5f * p + 0.25f * _power[k > 0 ? k - 1 : k] + 0.25f * _power[k < Bins - 1 ? k + 1 : k];
                float s = first ? across : 0.7f * _smooth[k] + 0.3f * across;
                _smooth[k] = s;
                if (first)
                {
                    _min[k] = s;
                    _minNext[k] = s;
                    _noise[k] = p;
                }
                else if (newWindow)
                {
                    _min[k] = Math.Min(_minNext[k], s);
                    _minNext[k] = s;
                }
                else
                {
                    _min[k] = Math.Min(_min[k], s);
                    _minNext[k] = Math.Min(_minNext[k], s);
                }

                // Speech in this band: the noise estimate holds; none: it follows the band.
                float present = s > _min[k] * SpeechRatio ? 1f : 0f;
                float pr = 0.2f * _presence[k] + 0.8f * present;
                _presence[k] = pr;
                float hold = 0.95f + 0.05f * pr;
                float noise = hold * _noise[k] + (1f - hold) * p;
                _noise[k] = noise;

                float gain;
                if (k < FirstBin)
                    gain = 0f;
                else
                {
                    float n = noise * NoiseBias + 1e-12f;
                    float post = p / n;
                    float prio = DecisionDirected * _cleanPrev[k] + (1f - DecisionDirected) * Math.Max(post - 1f, 0f);
                    gain = prio / (1f + prio);
                    if (gain < GainFloor)
                        gain = GainFloor;
                    _cleanPrev[k] = gain * gain * post;
                }
                _re[k] *= gain;
                _im[k] *= gain;
                if (k > 0 && k < Bins - 1)
                {
                    _re[N - k] = _re[k];
                    _im[N - k] = -_im[k];
                }
            }

            Fft(_re, _im, inverse: true);
            // Hann in, Hann out, a quarter apart: the windows squared add up to 1.5.
            const float norm = 1f / 1.5f;
            for (int i = 0; i < N; i++)
                _ola[i] += _re[i] * Window[i] * norm;
            Array.Copy(_ola, 0, _ready, 0, Hop);
            Array.Copy(_ola, Hop, _ola, 0, N - Hop);
            Array.Clear(_ola, N - Hop, Hop);
        }

        private static void Fft(float[] re, float[] im, bool inverse)
        {
            for (int i = 0; i < N; i++)
            {
                int j = Reverse[i];
                if (j > i)
                {
                    float t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            for (int size = 2; size <= N; size <<= 1)
            {
                int half = size >> 1;
                int stride = N / size;
                for (int start = 0; start < N; start += size)
                {
                    for (int k = 0; k < half; k++)
                    {
                        float wr = Cos[k * stride];
                        float wi = inverse ? Sin[k * stride] : -Sin[k * stride];
                        int a = start + k, b = a + half;
                        float xr = re[b] * wr - im[b] * wi;
                        float xi = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr; im[a] += xi;
                    }
                }
            }
            if (inverse)
            {
                for (int i = 0; i < N; i++)
                {
                    re[i] /= N;
                    im[i] /= N;
                }
            }
        }
    }
}

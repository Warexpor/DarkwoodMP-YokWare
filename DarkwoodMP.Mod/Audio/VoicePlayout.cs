using System;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// Rebuilds a voice stream between its samples for playback at the sound card's rate. Each
    /// output sample is the stream at that instant through a low-pass (a Kaiser-windowed sinc)
    /// cut just under half the voice rate. A straight line between two samples, as before, left
    /// copies of the voice above that (16 kHz voice played at 48 kHz: a 3 kHz tone also at 13 and
    /// 19 kHz, only about 25 dB down), heard as a metallic edge on every "s", and dulled the top
    /// of the voice by 3 dB. The output is the stream <see cref="DelaySamples"/> samples later.
    /// </summary>
    internal sealed class VoiceInterpolator
    {
        private const int Half = 16;
        private const int Taps = 2 * Half;
        private const int Phases = 128;
        private const double Cutoff = 0.92;
        private const double KaiserBeta = 7.0;

        /// <summary>How late the output is, in samples of the stream.</summary>
        internal const int DelaySamples = Half - 1;

        private static readonly float[] Table = BuildTable();

        /// <summary>
        /// The only positions a voice at a third of the sound card's rate (16 kHz voice, 48 kHz
        /// out) is ever read at besides 0, with the two table rows around each already blended:
        /// one pass over the history instead of two. Any other position goes the long way.
        /// </summary>
        private static readonly float ThirdKey = (float)(1.0 / 3.0);
        private static readonly float TwoThirdsKey = (float)(2.0 / 3.0);
        private static readonly float[] ThirdKernel = Blend(ThirdKey);
        private static readonly float[] TwoThirdsKernel = Blend(TwoThirdsKey);

        /// <summary>
        /// The history twice over: each sample is written at its place and <see cref="Taps"/>
        /// further on, so the last <see cref="Taps"/> samples always lie in one run from
        /// <see cref="_oldest"/> and nothing is moved on a push.
        /// </summary>
        private readonly float[] _hist = new float[2 * Taps];
        private int _oldest;

        /// <summary>The next sample of the stream.</summary>
        internal void Push(float sample)
        {
            _hist[_oldest] = sample;
            _hist[_oldest + Taps] = sample;
            if (++_oldest == Taps)
                _oldest = 0;
        }

        /// <summary>The stream at <paramref name="frac"/> (0..1) between the two samples in the middle of what was pushed.</summary>
        internal float At(float frac)
        {
            float[] hist = _hist;
            int o = _oldest;
            if (frac == ThirdKey)
                return Dot(hist, o, ThirdKernel, 0);
            if (frac == TwoThirdsKey)
                return Dot(hist, o, TwoThirdsKernel, 0);
            if (frac == 0f)
                return Dot(hist, o, Table, 0);
            float fp = frac * Phases;
            int p = (int)fp;
            if (p >= Phases)
                p = Phases - 1;
            float a = fp - p;
            int r0 = p * Taps;
            int r1 = r0 + Taps;
            float s0 = 0f, s1 = 0f;
            for (int j = 0; j < Taps; j++)
            {
                float h = hist[o + j];
                s0 += h * Table[r0 + j];
                s1 += h * Table[r1 + j];
            }
            return s0 + (s1 - s0) * a;
        }

        private static float Dot(float[] hist, int o, float[] kernel, int k0)
        {
            float s = 0f;
            for (int j = 0; j < Taps; j++)
                s += hist[o + j] * kernel[k0 + j];
            return s;
        }

        internal void Clear()
        {
            Array.Clear(_hist, 0, _hist.Length);
            _oldest = 0;
        }

        /// <summary>The two table rows around <paramref name="frac"/>, blended as <see cref="At"/> blends their sums.</summary>
        private static float[] Blend(float frac)
        {
            float fp = frac * Phases;
            int p = (int)fp;
            float a = fp - p;
            int r0 = p * Taps;
            int r1 = r0 + Taps;
            var k = new float[Taps];
            for (int j = 0; j < Taps; j++)
                k[j] = Table[r0 + j] + (Table[r1 + j] - Table[r0 + j]) * a;
            return k;
        }

        private static float[] BuildTable()
        {
            var table = new float[(Phases + 1) * Taps];
            double i0Beta = BesselI0(KaiserBeta);
            for (int p = 0; p <= Phases; p++)
            {
                double f = (double)p / Phases;
                double sum = 0;
                int row = p * Taps;
                for (int j = 0; j < Taps; j++)
                {
                    double t = f + Half - 1 - j;
                    double u = t / Half;
                    double w = Math.Abs(u) >= 1.0 ? 0.0 : BesselI0(KaiserBeta * Math.Sqrt(1.0 - u * u)) / i0Beta;
                    double arg = Math.PI * Cutoff * t;
                    double h = Cutoff * (Math.Abs(arg) < 1e-9 ? 1.0 : Math.Sin(arg) / arg) * w;
                    table[row + j] = (float)h;
                    sum += h;
                }
                // Every position passes a steady level unchanged.
                for (int j = 0; j < Taps; j++)
                    table[row + j] = (float)(table[row + j] / sum);
            }
            return table;
        }

        private static double BesselI0(double x)
        {
            double sum = 1.0, term = 1.0, q = x * x / 4.0;
            for (int k = 1; k < 40; k++)
            {
                term *= q / (k * (double)k);
                sum += term;
                if (term < 1e-12 * sum)
                    break;
            }
            return sum;
        }
    }

    /// <summary>
    /// Keeps received voice under a ceiling by turning it down, not by bending its waveform.
    /// Voice is played well above the level it was recorded at; the peaks of normal and loud
    /// speech went over full scale and were bent back sample by sample, which adds overtones,
    /// and at the voice's sample rate those fold back as a rough, buzzing edge on loud syllables.
    /// Here the level is read <see cref="LookSamples"/> samples ahead and the volume is already
    /// down when the peak plays, then comes back up between words. The output is the input
    /// <see cref="LookSamples"/> samples later.
    /// </summary>
    internal sealed class VoiceLimiter
    {
        internal const int LookSamples = 64;
        /// <summary>The ceiling: peaks are held to it.</summary>
        internal const float Ceiling = 0.7f;
        private const float AttackSec = 0.001f;
        private const float ReleaseSec = 0.15f;

        private readonly float[] _delay = new float[LookSamples];
        private readonly float _attack;
        private readonly float _release;
        private int _pos;
        private int _hold;
        private float _peak;
        private float _gain = 1f;

        internal VoiceLimiter(int sampleRate)
        {
            _attack = 1f - (float)Math.Exp(-1.0 / (sampleRate * AttackSec));
            _release = 1f - (float)Math.Exp(-1.0 / (sampleRate * ReleaseSec));
        }

        internal float Process(float x)
        {
            float a = x < 0f ? -x : x;
            // The highest level among the samples still waiting to play.
            if (a >= _peak)
            {
                _peak = a;
                _hold = LookSamples;
            }
            else if (_hold > 0)
                _hold--;
            else
                _peak += (a - _peak) * _attack;

            float target = _peak > Ceiling ? Ceiling / _peak : 1f;
            _gain += (target - _gain) * (target < _gain ? _attack : _release);

            float y = _delay[_pos] * _gain;
            _delay[_pos] = x;
            _pos = (_pos + 1) % LookSamples;
            return y;
        }

        /// <summary>A new stretch of talk: nothing of the last one is left to play.</summary>
        internal void Reset()
        {
            Array.Clear(_delay, 0, LookSamples);
            _pos = 0;
            _hold = 0;
            _peak = 0f;
            _gain = 1f;
        }
    }
}

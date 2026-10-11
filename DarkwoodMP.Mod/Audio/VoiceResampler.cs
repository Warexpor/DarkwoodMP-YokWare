using System;
using System.Collections.Generic;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// Sample rate conversion for the microphone, a stream at a time: each output sample is
    /// the input rebuilt at that instant from its neighbours through a low-pass (a
    /// Kaiser-windowed sinc), cut at the lower of the two rates so nothing above what the
    /// output can hold folds back into the voice. Any ratio; 48 kHz to the voice rate is an
    /// exact third. The output is the input <see cref="DelaySec"/> later.
    /// </summary>
    internal sealed class VoiceResampler
    {
        /// <summary>The low-pass reaches this many of its zero crossings to each side.</summary>
        private const int Zeros = 16;
        /// <summary>Positions between two input samples the low-pass is tabled at (in between: a line).</summary>
        private const int Phases = 256;
        /// <summary>Where the low-pass turns, as a share of the lower rate's half.</summary>
        private const double Cutoff = 0.88;
        private const double KaiserBeta = 7.0;

        private readonly double _step;
        private readonly int _half;
        private readonly int _taps;
        private readonly float[] _table;
        private float[] _buf;
        private int _count;
        private double _pos;

        /// <summary>How late the output is.</summary>
        internal float DelaySec { get; }

        internal VoiceResampler(int inRate, int outRate)
        {
            _step = (double)inRate / outRate;
            double scale = Math.Min(1.0, (double)outRate / inRate);
            _half = (int)Math.Ceiling(Zeros / scale);
            _taps = 2 * _half;
            DelaySec = _half / (float)inRate;
            double c = scale * Cutoff;
            _table = new float[(Phases + 1) * _taps];
            double i0Beta = BesselI0(KaiserBeta);
            for (int p = 0; p <= Phases; p++)
            {
                double f = (double)p / Phases;
                double sum = 0;
                int row = p * _taps;
                for (int j = 0; j < _taps; j++)
                {
                    double t = f + _half - 1 - j;
                    double u = t / _half;
                    double w = Math.Abs(u) >= 1.0 ? 0.0 : BesselI0(KaiserBeta * Math.Sqrt(1.0 - u * u)) / i0Beta;
                    double arg = Math.PI * c * t;
                    double h = c * (Math.Abs(arg) < 1e-9 ? 1.0 : Math.Sin(arg) / arg) * w;
                    _table[row + j] = (float)h;
                    sum += h;
                }
                // Every position passes a steady level unchanged.
                for (int j = 0; j < _taps; j++)
                    _table[row + j] = (float)(_table[row + j] / sum);
            }
            _buf = new float[_taps + 4096];
            _count = _half - 1;
            _pos = _half - 1;
        }

        /// <summary>Take <paramref name="n"/> input samples and add what they complete to <paramref name="output"/>, times <paramref name="gain"/>.</summary>
        internal void Process(float[] input, int n, List<float> output, float gain)
        {
            if (_count + n > _buf.Length)
                Array.Resize(ref _buf, _count + n + 4096);
            Array.Copy(input, 0, _buf, _count, n);
            _count += n;
            while (true)
            {
                int i0 = (int)_pos;
                if (i0 + _half >= _count)
                    break;
                double fp = (_pos - i0) * Phases;
                int p = (int)fp;
                float a = (float)(fp - p);
                int r0 = p * _taps;
                int k0 = i0 - _half + 1;
                float s0 = 0f;
                for (int j = 0; j < _taps; j++)
                    s0 += _buf[k0 + j] * _table[r0 + j];
                if (a > 0f)
                {
                    int r1 = r0 + _taps;
                    float s1 = 0f;
                    for (int j = 0; j < _taps; j++)
                        s1 += _buf[k0 + j] * _table[r1 + j];
                    s0 += (s1 - s0) * a;
                }
                output.Add(s0 * gain);
                _pos += _step;
            }
            int drop = (int)_pos - _half + 1;
            if (drop > 0)
            {
                Array.Copy(_buf, drop, _buf, 0, _count - drop);
                _count -= drop;
                _pos -= drop;
            }
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
}

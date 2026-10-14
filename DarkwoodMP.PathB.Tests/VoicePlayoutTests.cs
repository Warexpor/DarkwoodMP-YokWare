using System;
using DWMPHorde.Audio;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>Voice playback: the stream rebuilt between its samples, and peaks turned down instead of bent.</summary>
public class VoicePlayoutTests
{
    private const int Rate = VoiceCodec.SampleRate;

    [Theory]
    [InlineData(300.0, 3)]
    [InlineData(3000.0, 3)]
    [InlineData(6000.0, 3)]
    [InlineData(3000.0, 2.75625)] // 44.1 kHz output
    public void Interpolator_rebuilds_a_tone_between_its_samples(double hz, double ratio)
    {
        var interp = new VoiceInterpolator();
        double step = 1.0 / ratio;
        double frac = 0;
        int n = -1;
        double sig = 0, err = 0;
        for (int o = 0; o < 20000; o++)
        {
            frac += step;
            while (frac >= 1.0)
            {
                frac -= 1.0;
                n++;
                interp.Push((float)(0.5 * Math.Sin(2 * Math.PI * hz * n / Rate)));
            }
            if (n < 200)
                continue;
            // After pushing sample n the two middle samples are n - Half and n - Half + 1.
            double t = n - (VoiceInterpolator.DelaySamples + 1) + frac;
            double want = 0.5 * Math.Sin(2 * Math.PI * hz * t / Rate);
            double got = interp.At((float)frac);
            sig += want * want;
            err += (want - got) * (want - got);
        }
        double snr = 10 * Math.Log10(sig / (err + 1e-15));
        Assert.True(snr > 45, $"{hz} Hz: {snr:F1} dB");
    }

    [Fact]
    public void Limiter_passes_quiet_voice_unchanged_a_moment_later()
    {
        var lim = new VoiceLimiter(Rate);
        var x = new float[4000];
        for (int i = 0; i < x.Length; i++)
            x[i] = (float)(0.4 * Math.Sin(2 * Math.PI * 440 * i / Rate));
        for (int i = 0; i < x.Length; i++)
        {
            float y = lim.Process(x[i]);
            if (i >= VoiceLimiter.LookSamples)
                Assert.Equal(x[i - VoiceLimiter.LookSamples], y, 5);
        }
    }

    [Fact]
    public void Limiter_holds_a_loud_voice_under_the_ceiling_without_bending_it()
    {
        var lim = new VoiceLimiter(Rate);
        const int n = 16000;
        var y = new float[n];
        float peak = 0f;
        for (int i = 0; i < n; i++)
        {
            // Quiet, then a sudden shout at twice full scale.
            double amp = i < 2000 ? 0.2 : 2.0;
            y[i] = lim.Process((float)(amp * Math.Sin(2 * Math.PI * 440 * i / Rate)));
            peak = Math.Max(peak, Math.Abs(y[i]));
        }
        Assert.True(peak <= VoiceLimiter.Ceiling * 1.05f, "peak " + peak);

        // Settled, the shout is still a clean tone: fit a sine at 440 Hz and measure what is left.
        double ss = 0, sc = 0, pw = 0;
        int from = 8000;
        for (int i = from; i < n; i++)
        {
            double ph = 2 * Math.PI * 440 * i / Rate;
            ss += y[i] * Math.Sin(ph);
            sc += y[i] * Math.Cos(ph);
            pw += y[i] * (double)y[i];
        }
        int m = n - from;
        double fit = 2.0 * (ss * ss + sc * sc) / m;
        double rest = pw - fit;
        double snr = 10 * Math.Log10(fit / (rest + 1e-15));
        Assert.True(snr > 40, $"{snr:F1} dB");
    }
    /// <summary>
    /// The interpolator as it was before its history became a double ring and the 48 kHz
    /// positions got blended kernels: the shipped one must play the same stream.
    /// </summary>
    private sealed class ReferenceInterpolator
    {
        private const int Half = 16, Taps = 32, Phases = 128;
        private static readonly float[] Table = BuildTable();
        private readonly float[] _hist = new float[Taps];

        internal void Push(float sample)
        {
            Array.Copy(_hist, 1, _hist, 0, Taps - 1);
            _hist[Taps - 1] = sample;
        }

        internal float At(float frac)
        {
            float fp = frac * Phases;
            int p = (int)fp;
            if (p >= Phases)
                p = Phases - 1;
            float a = fp - p;
            int r0 = p * Taps, r1 = r0 + Taps;
            float s0 = 0f, s1 = 0f;
            for (int j = 0; j < Taps; j++)
            {
                s0 += _hist[j] * Table[r0 + j];
                s1 += _hist[j] * Table[r1 + j];
            }
            return s0 + (s1 - s0) * a;
        }

        internal void Clear() => Array.Clear(_hist, 0, Taps);

        private static float[] BuildTable()
        {
            var table = new float[(Phases + 1) * Taps];
            double i0Beta = I0(7.0);
            for (int p = 0; p <= Phases; p++)
            {
                double f = (double)p / Phases, sum = 0;
                int row = p * Taps;
                for (int j = 0; j < Taps; j++)
                {
                    double t = f + Half - 1 - j, u = t / Half;
                    double w = Math.Abs(u) >= 1.0 ? 0.0 : I0(7.0 * Math.Sqrt(1.0 - u * u)) / i0Beta;
                    double arg = Math.PI * 0.92 * t;
                    double h = 0.92 * (Math.Abs(arg) < 1e-9 ? 1.0 : Math.Sin(arg) / arg) * w;
                    table[row + j] = (float)h;
                    sum += h;
                }
                for (int j = 0; j < Taps; j++)
                    table[row + j] = (float)(table[row + j] / sum);
            }
            return table;
        }

        private static double I0(double x)
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

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    [InlineData(96000)]
    public void Interpolator_plays_what_the_old_one_played(int outRate)
    {
        // The speaker's own stepping (VoiceSpeakerBehaviour), with an underrun now and then.
        var shipped = new VoiceInterpolator();
        var reference = new ReferenceInterpolator();
        var rnd = new Random(7);
        double step = (double)Rate / outRate, frac = 0;
        double worst = 0;
        for (int o = 0; o < 200000; o++)
        {
            frac += step;
            while (frac >= 1.0)
            {
                frac -= 1.0;
                float x = (float)(rnd.NextDouble() * 2 - 1);
                shipped.Push(x);
                reference.Push(x);
            }
            if (o % 50000 == 49999)
            {
                shipped.Clear();
                reference.Clear();
            }
            float a = shipped.At((float)frac), b = reference.At((float)frac);
            worst = Math.Max(worst, Math.Abs(a - b));
        }
        Assert.True(worst <= 1e-6, $"{outRate} Hz: off by {worst:E2}");
    }
}

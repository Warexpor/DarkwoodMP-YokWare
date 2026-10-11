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
}

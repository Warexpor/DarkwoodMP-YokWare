using System;
using DWMPHorde.Audio;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>The microphone's noise suppression: steady noise goes down, a voice comes through, nothing is added.</summary>
public class VoiceDenoiseTests
{
    private const int Rate = VoiceCodec.SampleRate;
    private const int Delay = VoiceDenoise.DelaySamples;

    private static float[] Noise(int n, float amp, int seed)
    {
        var r = new Random(seed);
        var x = new float[n];
        for (int i = 0; i < n; i++)
            x[i] = amp * (float)(r.NextDouble() * 2 - 1);
        return x;
    }

    /// <summary>A voice-like signal: a 150 Hz buzz with harmonics, in syllables of 0.25 s with gaps.</summary>
    private static float[] Voice(int n, float amp)
    {
        var x = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            bool on = (t % 0.5) < 0.25;
            if (!on)
                continue;
            double v = 0;
            for (int h = 1; h <= 12; h++)
                v += Math.Sin(2 * Math.PI * 150 * h * t) / h;
            x[i] = amp * (float)v * 0.5f;
        }
        return x;
    }

    private static double Rms(float[] x, int from, int to)
    {
        double s = 0;
        for (int i = from; i < to; i++)
            s += x[i] * (double)x[i];
        return Math.Sqrt(s / (to - from));
    }

    private static float[] Run(float[] x)
    {
        var y = (float[])x.Clone();
        var d = new VoiceDenoise();
        // In mic-sized pieces of uneven length, as the capture hands them over.
        int pos = 0, step = 371;
        while (pos < y.Length)
        {
            int n = Math.Min(step, y.Length - pos);
            d.Process(y, pos, n);
            pos += n;
            step = step == 371 ? 640 : 371;
        }
        return y;
    }

    [Fact]
    public void SteadyNoiseAloneIsTurnedDownByAtLeast14Db()
    {
        float[] x = Noise(Rate * 4, 0.05f, 1);
        float[] y = Run(x);
        double before = Rms(x, Rate * 2, Rate * 4);
        double after = Rms(y, Rate * 2, Rate * 4);
        Assert.True(20 * Math.Log10(after / before) < -14, "noise went down by " + 20 * Math.Log10(after / before) + " dB");
    }

    [Fact]
    public void CleanSignalComesOutAsItWentIn()
    {
        float[] x = Voice(Rate * 3, 0.3f);
        float[] y = Run(x);
        double err = 0, sig = 0;
        for (int i = Rate; i < x.Length - Delay; i++)
        {
            double d = y[i + Delay] - x[i];
            err += d * d;
            sig += x[i] * (double)x[i];
        }
        Assert.True(10 * Math.Log10(sig / (err + 1e-12)) > 20, "clean voice SNR " + 10 * Math.Log10(sig / (err + 1e-12)) + " dB");
    }

    [Fact]
    public void VoiceInNoiseKeepsItsLevelWhileTheGapsGoQuiet()
    {
        int n = Rate * 5;
        float[] voice = Voice(n, 0.3f);
        float[] noise = Noise(n, 0.03f, 2);
        var mix = new float[n];
        for (int i = 0; i < n; i++)
            mix[i] = voice[i] + noise[i];
        float[] y = Run(mix);

        // From 3 s on: one syllable (3.0..3.25 s) and the gap after it (3.25..3.5 s), shifted by the delay.
        int syl = Rate * 3 + Delay, gap = Rate * 3 + Rate / 4 + Delay;
        double sylIn = Rms(voice, Rate * 3 + 800, Rate * 3 + Rate / 4 - 800);
        double sylOut = Rms(y, syl + 800, syl + Rate / 4 - 800);
        double gapIn = Rms(noise, Rate * 3 + Rate / 4 + 1600, Rate * 3 + Rate / 2 - 400);
        double gapOut = Rms(y, gap + 1600, gap + Rate / 4 - 400);
        Assert.InRange(20 * Math.Log10(sylOut / sylIn), -2.0, 1.0);
        Assert.True(20 * Math.Log10(gapOut / gapIn) < -10, "gap noise went down by " + 20 * Math.Log10(gapOut / gapIn) + " dB");
    }

    [Fact]
    public void NoiseIsCleanedFromTheFirstSecondOn()
    {
        float[] x = Noise(Rate * 2, 0.05f, 3);
        float[] y = Run(x);
        double db = 20 * Math.Log10(Rms(y, Rate / 2, Rate * 3 / 2) / Rms(x, Rate / 2, Rate * 3 / 2));
        Assert.True(db < -25, "noise 0.5..1.5 s after the start went down by " + db + " dB");
    }

    [Fact]
    public void ALongPauseGoesFarQuieterThanAGapBetweenWords()
    {
        int n = Rate * 6;
        float[] voice = Voice(n, 0.3f);
        // The voice stops at 3 s.
        Array.Clear(voice, Rate * 3, n - Rate * 3);
        float[] noise = Noise(n, 0.03f, 4);
        var mix = new float[n];
        for (int i = 0; i < n; i++)
            mix[i] = voice[i] + noise[i];
        float[] y = Run(mix);
        double db = 20 * Math.Log10(Rms(y, Rate * 4 + Delay, Rate * 6) / Rms(noise, Rate * 4, Rate * 6 - Delay));
        Assert.True(db < -30, "noise a second into a pause went down by " + db + " dB");
    }

    [Fact]
    public void ClicksBetweenSentencesDoNotOpenTheGate()
    {
        int n = Rate * 6;
        float[] x = Noise(n, 0.002f, 5);
        var r = new Random(6);
        // A key every 0.4 s: 8 ms of noise at a fifth of full scale.
        for (int at = Rate; at < n - 200; at += Rate * 2 / 5)
        {
            for (int i = 0; i < 128; i++)
                x[at + i] += 0.2f * (float)(r.NextDouble() * 2 - 1) * (float)Math.Sin(Math.PI * i / 128);
        }
        float[] y = Run(x);
        double db = 20 * Math.Log10(Rms(y, Rate * 2 + Delay, n) / Rms(x, Rate * 2, n - Delay));
        Assert.True(db < -12, "clicks went down by " + db + " dB");
    }

    [Fact]
    public void SilenceStaysSilence()
    {
        float[] y = Run(new float[Rate]);
        Assert.Equal(0.0, Rms(y, 0, y.Length), 6);
    }
}

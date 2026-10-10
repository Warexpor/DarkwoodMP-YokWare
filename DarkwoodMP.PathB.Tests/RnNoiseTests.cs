using System;
using System.Collections.Generic;
using DWMPHorde.Audio;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// The microphone's noise suppression (RNNoise in managed code) and the chain around it:
/// the port answers as the reference C code does, noise goes down, a voice-band tone
/// passes, and the rate changes neither colour the voice nor fold what is above it back in.
/// </summary>
public class RnNoiseTests
{
    private static double Rms(IList<float> x, int from, int to)
    {
        double s = 0;
        for (int i = from; i < to; i++)
            s += x[i] * (double)x[i];
        return Math.Sqrt(s / (to - from));
    }

    private static double Db(double ratio) => 20 * Math.Log10(ratio + 1e-12);

    /// <summary>
    /// Six seconds made of whole numbers only (so the reference build got the very same
    /// input): hiss throughout, a 120 Hz pulse train every other half second. The expected
    /// loudness of each half second is what the reference C code put out for it.
    /// </summary>
    [Fact]
    public void PortMatchesTheReferenceCode()
    {
        const int n = 48000 * 6;
        var x = new float[n];
        uint st = 12345;
        for (int i = 0; i < n; i++)
        {
            st = (st * 1103515245u + 12345u) & 0x7fffffffu;
            int v = (int)Math.Floor(((int)((st >> 16) & 0x7fff) - 16384) / 16.0);
            if ((i / 24000) % 2 == 1)
            {
                int ph = i % 400;
                if (ph < 40)
                    v += 8000 - 200 * ph;
            }
            x[i] = v;
        }
        var rn = new RnNoise();
        for (int f = 0; f < n / RnNoise.FrameSize; f++)
            rn.Process(x, f * RnNoise.FrameSize);

        float[] expected =
        {
            417.53f, 1409.79f, 187.08f, 1403.57f, 178.49f, 1395.44f,
            181.64f, 1392.25f, 190.64f, 1395.30f, 189.62f, 1391.72f
        };
        for (int k = 0; k < expected.Length; k++)
        {
            double got = Rms(x, k * 24000, (k + 1) * 24000);
            Assert.True(Math.Abs(got - expected[k]) < expected[k] * 0.02 + 1.0,
                "half second " + k + ": " + got.ToString("F2") + " against the reference's " + expected[k].ToString("F2"));
        }
    }

    /// <summary>A room's noise: a fan's rumble, falling off toward the highs, over a little hiss.</summary>
    private static float[] RoomNoise(int n, int rate, float amp, int seed)
    {
        var r = new Random(seed);
        var x = new float[n];
        double low = 0;
        double keep = Math.Exp(-2 * Math.PI * 80.0 / rate);
        for (int i = 0; i < n; i++)
        {
            double w = r.NextDouble() * 2 - 1;
            low = keep * low + (1 - keep) * w;
            x[i] = amp * (float)(low * 12 + w * 0.02);
        }
        return x;
    }

    private static List<float> RunChain(float[] x, int rate, bool suppress, float gain = 1f)
    {
        var chain = new VoiceMicChain(rate);
        var frames = new List<float[]>();
        var chunk = new float[rate / 60];
        for (int at = 0; at + chunk.Length <= x.Length; at += chunk.Length)
        {
            Array.Copy(x, at, chunk, 0, chunk.Length);
            chain.Process(chunk, chunk.Length, gain, suppress, frames);
        }
        Assert.Null(chain.SuppressionError);
        var y = new List<float>();
        foreach (float[] f in frames)
        {
            Assert.Equal(VoiceCodec.PacketSamples, f.Length);
            y.AddRange(f);
        }
        return y;
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    [InlineData(16000)]
    public void RoomNoiseGoesDown(int rate)
    {
        float[] x = RoomNoise(rate * 4, rate, 0.02f, 7);
        List<float> on = RunChain(x, rate, true);
        List<float> off = RunChain(x, rate, false);
        int a = VoiceCodec.SampleRate * 2, b = VoiceCodec.SampleRate * 4 - 2000;
        double cut = Db(Rms(on, a, b) / Rms(off, a, b));
        Assert.True(cut < -20, "noise only, after two seconds: " + cut.ToString("F1") + " dB");
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    [InlineData(16000)]
    [InlineData(96000)]
    public void WithoutSuppressionTheVoiceBandPassesAsRecorded(int rate)
    {
        var x = new float[rate * 2];
        for (int i = 0; i < x.Length; i++)
            x[i] = 0.25f * (float)Math.Sin(2 * Math.PI * 1000 * i / rate);
        List<float> y = RunChain(x, rate, false);
        Assert.InRange(y.Count, VoiceCodec.SampleRate * 2 - 2 * VoiceCodec.PacketSamples, VoiceCodec.SampleRate * 2);
        double level = Rms(y, 4000, y.Count - 1000) / (0.25 / Math.Sqrt(2));
        Assert.InRange(Db(level), -0.2, 0.2);
        List<float> loud = RunChain(x, rate, false, 2f);
        Assert.InRange(Db(Rms(loud, 4000, loud.Count - 1000) / Rms(y, 4000, y.Count - 1000)), 5.9, 6.1);
    }

    [Fact]
    public void WhatTheVoiceRateCannotHoldIsRemovedNotFoldedBack()
    {
        var x = new float[48000];
        for (int i = 0; i < x.Length; i++)
            x[i] = 0.25f * (float)Math.Sin(2 * Math.PI * 11000 * i / 48000.0);
        var down = new VoiceResampler(48000, VoiceCodec.SampleRate);
        var y = new List<float>();
        down.Process(x, x.Length, y, 1f);
        Assert.InRange(y.Count, VoiceCodec.SampleRate - 64, VoiceCodec.SampleRate);
        Assert.True(Db(Rms(y, 1000, y.Count - 1000) / (0.25 / Math.Sqrt(2))) < -55);
    }

    [Fact]
    public void ChainDelayIsWithinWhatTheRadioWaitsFor()
    {
        var x = new float[48000];
        x[24000] = 0.5f;
        List<float> y = RunChain(x, 48000, true);
        int peak = 0;
        for (int i = 1; i < y.Count; i++)
            if (Math.Abs(y[i]) > Math.Abs(y[peak]))
                peak = i;
        double delay = peak / (double)VoiceCodec.SampleRate - 0.5;
        Assert.InRange(delay, 0.0, VoiceMicChain.MaxDelaySec);
    }
}

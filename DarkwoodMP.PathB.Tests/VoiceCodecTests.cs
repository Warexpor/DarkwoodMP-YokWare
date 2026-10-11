using DWMPHorde.Audio;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>The voice codec: a stream decodes to what was said, small, and a lost packet is filled in.</summary>
public class VoiceCodecTests
{
    private const int N = VoiceCodec.PacketSamples;

    /// <summary>A voiced sound: a 140 Hz pitch with harmonics under two formants, swelling like syllables.</summary>
    private static float[] Speech(int packets)
    {
        var x = new float[packets * N];
        for (int i = 0; i < x.Length; i++)
        {
            double t = i / (double)VoiceCodec.SampleRate;
            double v = 0;
            for (int h = 1; h <= 24; h++)
            {
                double f = 140.0 * h;
                double g = 1.0 / (1.0 + System.Math.Pow((f - 700) / 250, 2)) + 0.5 / (1.0 + System.Math.Pow((f - 1900) / 350, 2));
                v += g * System.Math.Sin(2 * System.Math.PI * f * t + h);
            }
            double syllable = 0.55 + 0.45 * System.Math.Sin(2 * System.Math.PI * 3.0 * t);
            x[i] = (float)(0.12 * v * syllable);
        }
        return x;
    }

    private static byte[][] Encode(float[] x, VoiceEncoder enc)
    {
        int packets = x.Length / N;
        var outp = new byte[packets][];
        var frame = new float[N];
        var buf = new byte[VoiceCodec.MaxBlockBytes];
        for (int p = 0; p < packets; p++)
        {
            System.Array.Copy(x, p * N, frame, 0, N);
            int len = enc.Encode(frame, N, buf, 0);
            Assert.InRange(len, 1, VoiceCodec.MaxBlockBytes);
            outp[p] = new byte[len];
            System.Array.Copy(buf, outp[p], len);
        }
        return outp;
    }

    /// <summary>Best match of <paramref name="y"/> against <paramref name="x"/> over the codec's delay, as signal over error in dB.</summary>
    private static double SnrDb(float[] x, float[] y, int from, int to)
    {
        double best = double.NegativeInfinity;
        for (int delay = 0; delay <= 200; delay++)
        {
            double sig = 0, err = 0;
            for (int i = from; i < to; i++)
            {
                sig += x[i] * (double)x[i];
                double d = x[i] - y[i + delay];
                err += d * d;
            }
            best = System.Math.Max(best, 10 * System.Math.Log10(sig / (err + 1e-12)));
        }
        return best;
    }

    private static double Rms(float[] x, int from, int to)
    {
        double s = 0;
        for (int i = from; i < to; i++)
            s += x[i] * (double)x[i];
        return System.Math.Sqrt(s / (to - from));
    }

    [Fact]
    public void Stream_DecodesCloseToWhatWasSaid_AtAFractionOfTheSamples()
    {
        float[] x = Speech(50);
        byte[][] packets = Encode(x, new VoiceEncoder(speech: true));
        var dec = new VoiceDecoder();
        var y = new float[x.Length + N];
        int bytes = 0;
        for (int p = 0; p < packets.Length; p++)
        {
            Assert.Equal(N, dec.Decode(packets[p], 0, packets[p].Length, y, p * N));
            bytes += packets[p].Length;
        }
        // Two seconds: under 40 kbit/s (16-bit samples are 256).
        Assert.InRange(bytes * 8 / 2, 8000, 40000);
        // A speech coder keeps the sound, not the waveform: the level holds, and the shape roughly.
        double lx = Rms(x, 8 * N, 40 * N), ly = Rms(y, 8 * N, 40 * N);
        Assert.InRange(ly / lx, 0.7, 1.4);
        Assert.True(SnrDb(x, y, 8 * N, 40 * N) > 6, "SNR " + SnrDb(x, y, 8 * N, 40 * N));
    }

    [Fact]
    public void LostPacket_ComesBackFromTheNextOne()
    {
        float[] x = Speech(40);
        byte[][] packets = Encode(x, new VoiceEncoder(speech: true));
        const int lost = 20;

        var dec = new VoiceDecoder();
        var y = new float[x.Length + N];
        for (int p = 0; p < packets.Length; p++)
        {
            if (p == lost)
                continue;
            if (p == lost + 1)
                Assert.Equal(N, dec.Recover(packets[p], 0, packets[p].Length, y, lost * N));
            Assert.Equal(N, dec.Decode(packets[p], 0, packets[p].Length, y, p * N));
        }
        // The hole holds voice at the level around it, not silence.
        double around = Rms(y, (lost - 3) * N, lost * N);
        double hole = Rms(y, lost * N, (lost + 1) * N);
        Assert.InRange(hole / around, 0.4, 2.0);
        // And the talk after it is as it would have been.
        Assert.InRange(Rms(y, (lost + 3) * N, (lost + 8) * N) / Rms(x, (lost + 3) * N, (lost + 8) * N), 0.6, 1.5);
    }

    [Fact]
    public void LostPackets_WithNothingToRebuildFrom_AreConcealedAndFade()
    {
        float[] x = Speech(20);
        byte[][] packets = Encode(x, new VoiceEncoder(speech: true));
        var dec = new VoiceDecoder();
        var y = new float[N * 2];
        for (int p = 0; p < 15; p++)
            dec.Decode(packets[p], 0, packets[p].Length, y, 0);
        Assert.Equal(N, dec.Conceal(y, 0));
        Assert.True(Rms(y, 0, N) > 0.001, "a concealed packet carries the voice on");
        float peak = 0;
        for (int i = 0; i < N; i++)
            peak = System.Math.Max(peak, System.Math.Abs(y[i]));
        Assert.True(peak <= 1f);
    }

    [Fact]
    public void Decoder_SurvivesGarbageAndCarriesOnAfterAReset()
    {
        var dec = new VoiceDecoder();
        var y = new float[N];
        var junk = new byte[60];
        new System.Random(7).NextBytes(junk);
        try { dec.Decode(junk, 0, junk.Length, y, 0); } catch (System.Exception) { }
        dec.Reset();

        float[] x = Speech(12);
        byte[][] packets = Encode(x, new VoiceEncoder(speech: true));
        var all = new float[x.Length];
        for (int p = 0; p < packets.Length; p++)
            Assert.Equal(N, dec.Decode(packets[p], 0, packets[p].Length, all, p * N));
        Assert.InRange(Rms(all, 6 * N, 12 * N) / Rms(x, 6 * N, 12 * N), 0.6, 1.5);
        Assert.Equal(0, dec.Decode(junk, 50, 20, y, 0));
    }

    [Fact]
    public void WorldStream_CarriesSoundsThatAreNotSpeech()
    {
        // Rain-like noise with a low thump in it.
        var rng = new System.Random(3);
        var x = new float[30 * N];
        for (int i = 0; i < x.Length; i++)
            x[i] = (float)((rng.NextDouble() - 0.5) * 0.2 + 0.2 * System.Math.Sin(2 * System.Math.PI * 90 * i / VoiceCodec.SampleRate));
        byte[][] packets = Encode(x, new VoiceEncoder(speech: false));
        var dec = new VoiceDecoder();
        var y = new float[x.Length];
        for (int p = 0; p < packets.Length; p++)
            Assert.Equal(N, dec.Decode(packets[p], 0, packets[p].Length, y, p * N));
        Assert.InRange(Rms(y, 6 * N, 30 * N) / Rms(x, 6 * N, 30 * N), 0.6, 1.4);
    }

    [Fact]
    public void ShortFrame_IsPaddedToAPacket()
    {
        var enc = new VoiceEncoder(speech: true);
        var buf = new byte[VoiceCodec.MaxBlockBytes];
        int len = enc.Encode(Speech(1), 300, buf, 0);
        Assert.Equal(N, new VoiceDecoder().Decode(buf, 0, len, new float[N], 0));
    }

    [Fact]
    public void Level_SilenceIsZeroAndAShoutIsFull()
    {
        var shout = new float[640];
        var talk = new float[640];
        for (int i = 0; i < 640; i++)
        {
            double s = System.Math.Sin(2 * System.Math.PI * 300 * i / VoiceCodec.SampleRate);
            shout[i] = (float)(0.9 * s);
            talk[i] = (float)(0.1 * s); // about -23 dBFS
        }
        Assert.Equal(0f, VoiceCodec.LevelOf(new float[640], 640));
        Assert.Equal(1f, VoiceCodec.LevelOf(shout, 640));
        Assert.InRange(VoiceCodec.LevelOf(talk, 640), 0.5f, 0.75f);
    }
}

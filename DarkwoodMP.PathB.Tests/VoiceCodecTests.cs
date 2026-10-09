using DWMPHorde.Audio;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>The voice codec: a packet decodes to what was said, on its own, at the expected size.</summary>
public class VoiceCodecTests
{
    private static float[] Tone(float hz, float amp, int n, float phase = 0f)
    {
        var x = new float[n];
        for (int i = 0; i < n; i++)
            x[i] = amp * (float)System.Math.Sin(phase + 2 * System.Math.PI * hz * i / VoiceCodec.SampleRate);
        return x;
    }

    private static double SnrDb(float[] a, float[] b, int n)
    {
        double sig = 0, err = 0;
        for (int i = 0; i < n; i++)
        {
            sig += a[i] * (double)a[i];
            double d = a[i] - b[i];
            err += d * d;
        }
        return 10 * System.Math.Log10(sig / (err + 1e-12));
    }

    [Theory]
    [InlineData(220f, 0.3f)]
    [InlineData(1000f, 0.5f)]
    [InlineData(3000f, 0.1f)]
    public void Packet_DecodesCloseToTheInput(float hz, float amp)
    {
        float[] x = Tone(hz, amp, VoiceCodec.PacketSamples, 0.7f);
        var bytes = new byte[VoiceCodec.EncodedSize(x.Length)];
        int len = VoiceCodec.Encode(x, x.Length, bytes);
        Assert.Equal(VoiceCodec.HeaderBytes + VoiceCodec.PacketSamples / 2, len);

        var y = new float[VoiceCodec.PacketSamples];
        Assert.Equal(VoiceCodec.PacketSamples, VoiceCodec.Decode(bytes, len, y));
        Assert.True(SnrDb(x, y, x.Length) > 18, "SNR " + SnrDb(x, y, x.Length));
    }

    [Fact]
    public void Packets_DecodeIndependently()
    {
        // A second packet decodes the same whether or not the first one arrived.
        float[] a = Tone(500f, 0.4f, VoiceCodec.PacketSamples);
        float[] b = Tone(500f, 0.4f, VoiceCodec.PacketSamples, 1.3f);
        var pa = new byte[VoiceCodec.EncodedSize(a.Length)];
        var pb = new byte[VoiceCodec.EncodedSize(b.Length)];
        VoiceCodec.Encode(a, a.Length, pa);
        int lb = VoiceCodec.Encode(b, b.Length, pb);

        var y1 = new float[VoiceCodec.PacketSamples];
        var y2 = new float[VoiceCodec.PacketSamples];
        VoiceCodec.Decode(pa, pa.Length, y1);
        VoiceCodec.Decode(pb, lb, y1);
        VoiceCodec.Decode(pb, lb, y2);
        Assert.Equal(y1, y2);
    }

    [Fact]
    public void Level_SilenceIsZeroAndAShoutIsFull()
    {
        Assert.Equal(0f, VoiceCodec.LevelOf(new float[640], 640));
        Assert.Equal(1f, VoiceCodec.LevelOf(Tone(300f, 0.9f, 640), 640));
        float speech = VoiceCodec.LevelOf(Tone(300f, 0.1f, 640), 640); // about -23 dBFS
        Assert.InRange(speech, 0.5f, 0.75f);
    }
}

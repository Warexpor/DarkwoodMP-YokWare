using DWMPHorde.Networking;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>Round-trips shipped NetWriter/NetReader (LiteNetLib-backed).</summary>
public class NetWriterTests
{
    [Fact]
    public void RoundTrip_PrimitivesStringAndLengthPrefixedBytes()
    {
        var w = new NetWriter();
        w.Put((byte)7);
        w.Put((short)300);
        w.Put(42);
        w.Put(1.5f);
        w.Put(true);
        w.Put("hello");
        w.Put(new byte[] { 9, 8, 7 });

        var r = new NetReader(w.CopyData());
        Assert.Equal(7, r.GetByte());
        Assert.Equal(300, r.GetShort());
        Assert.Equal(42, r.GetInt());
        Assert.Equal(1.5f, r.GetFloat());
        Assert.True(r.GetBool());
        Assert.Equal("hello", r.GetString());
        Assert.Equal(new byte[] { 9, 8, 7 }, r.GetBytes());
        Assert.Equal(0, r.AvailableBytes);
    }

    [Fact]
    public void PutRaw_AppendsWithoutLengthPrefix()
    {
        // Host Forwardable Direct path must rebroadcast already-framed payloads raw.
        // Length-prefixing here corrupted 3+ peer fan-out.
        byte[] framed = { 1, 2, 3, 4, 5 };

        var lengthPrefixed = new NetWriter();
        lengthPrefixed.Put(framed);

        var raw = new NetWriter();
        raw.PutRaw(framed);

        Assert.True(lengthPrefixed.CopyData().Length > framed.Length,
            "Put(byte[]) should length-prefix");
        Assert.Equal(framed, raw.CopyData());
    }

    [Fact]
    public void PutRaw_NullOrEmpty_WritesNothing()
    {
        var w = new NetWriter();
        w.PutRaw(null!);
        w.PutRaw(Array.Empty<byte>());
        Assert.Empty(w.CopyData());
    }
}

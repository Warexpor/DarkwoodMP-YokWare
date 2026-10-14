using System;
using DWMPHorde.Players;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>Playback of another player's positions (shipped RemotePoseTimeline).</summary>
public class RemotePoseTimelineTests
{
    private const float Dt = 1f / 30f;
    private const float Speed = 300f; // units per second along X

    private static void Feed(RemotePoseTimeline tl, uint seq, float arrival)
        => tl.Add(seq, arrival, seq * Dt * Speed, 0f, 0f, Speed, 0f);

    [Fact]
    public void JitteryStream_PlaysForwardSmoothly()
    {
        var tl = new RemotePoseTimeline();
        var rng = new Random(7);
        float lastX = float.NegativeInfinity;
        float worstStep = 0f;
        uint seq = 1;
        float nextSend = 0f;
        for (float now = 0f; now < 5f; now += 0.01f)
        {
            while (nextSend <= now)
            {
                // Arrives up to 25 ms late.
                Feed(tl, seq++, nextSend + (float)rng.NextDouble() * 0.025f);
                nextSend += Dt;
            }
            if (!tl.Sample(now, out float x, out _, out _, out _, out _))
                continue;
            if (now > 1f)
            {
                Assert.True(x >= lastX - 1e-3f, $"went back at {now}: {x} < {lastX}");
                worstStep = Math.Max(worstStep, x - lastX);
            }
            lastX = x;
        }
        // 10 ms of 300 u/s is 3 u; a staircase would jump 10 u at once.
        Assert.True(worstStep < 6f, "step " + worstStep);
        Assert.InRange(tl.Interval, 0.03f, 0.037f);
    }

    [Fact]
    public void LatePacket_DoesNotStallTheBody()
    {
        var tl = new RemotePoseTimeline();
        for (uint s = 1; s <= 30; s++)
            Feed(tl, s, s * Dt);
        tl.Sample(30 * Dt, out float before, out _, out _, out _, out _);
        // Packet 31 is 60 ms late: the body keeps moving (interpolating, then coasting).
        tl.Sample(31 * Dt + 0.03f, out float during, out _, out _, out _, out _);
        Assert.True(during > before + 5f, $"{during} vs {before}");
    }

    [Fact]
    public void ReorderedPacket_IsDropped()
    {
        var tl = new RemotePoseTimeline();
        Feed(tl, 1, 0f);
        Feed(tl, 2, Dt);
        Feed(tl, 1, Dt + 0.001f);
        Assert.Equal(2, tl.Count);
    }

    [Fact]
    public void LongSilence_StartsANewStream()
    {
        var tl = new RemotePoseTimeline();
        for (uint s = 1; s <= 10; s++)
            Feed(tl, s, s * Dt);
        Feed(tl, 11, 10 * Dt + 1f);
        Assert.Equal(1, tl.Count);
        Assert.True(tl.Sample(10 * Dt + 1f, out float x, out _, out _, out _, out _));
        Assert.Equal(11 * Dt * Speed, x, 2);
    }

    [Fact]
    public void Clear_ForgetsEverything()
    {
        var tl = new RemotePoseTimeline();
        Feed(tl, 1, 0f);
        tl.Clear();
        Assert.Equal(0, tl.Count);
        Assert.False(tl.Sample(1f, out _, out _, out _, out _, out _));
    }
}

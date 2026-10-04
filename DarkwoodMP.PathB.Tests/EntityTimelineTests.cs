using DWMPHorde;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>Client entity interpolation on the host timeline (EntityTimeline / HostClockEstimator).</summary>
public class EntityTimelineTests
{
    private static TimelineSample S(float t, float x, float rot = 0f) => new TimelineSample { T = t, X = x, RotY = rot };

    [Fact]
    public void Sample_InterpolatesBetweenSamples()
    {
        var tl = new EntityTimeline();
        tl.Add(S(1.00f, 0f), 0.1f);
        tl.Add(S(1.05f, 10f), 0.1f);
        Assert.True(tl.Sample(1.025f, 0.05f, out TimelineSample p));
        Assert.Equal(5f, p.X, 3);
    }

    [Fact]
    public void Sample_BeforeOldest_HoldsOldest_AndPastNewest_CoastsThenHolds()
    {
        var tl = new EntityTimeline();
        tl.Add(S(1.00f, 0f), 0.1f);
        tl.Add(S(1.05f, 10f), 0.1f);
        tl.Sample(0.5f, 0.05f, out TimelineSample before);
        Assert.Equal(0f, before.X, 3);
        tl.Sample(1.075f, 0.05f, out TimelineSample coast);
        Assert.Equal(15f, coast.X, 3);
        tl.Sample(2.0f, 0.05f, out TimelineSample held);
        Assert.Equal(20f, held.X, 3); // capped at 0.05 s of coast
    }

    [Fact]
    public void Add_DropsOutOfOrder_AndHoldsAcrossQuietGap()
    {
        var tl = new EntityTimeline();
        Assert.True(tl.Add(S(1.0f, 0f), 0.1f));
        Assert.False(tl.Add(S(1.0f, 5f), 0.1f));
        Assert.False(tl.Add(S(0.9f, 5f), 0.1f));
        // Resting body went quiet for 2 s, then moved: it starts moving one interval before the new sample.
        Assert.True(tl.Add(S(3.0f, 10f), 0.1f));
        tl.Sample(2.5f, 0.05f, out TimelineSample resting);
        Assert.Equal(0f, resting.X, 3);
        tl.Sample(2.95f, 0.05f, out TimelineSample moving);
        Assert.Equal(5f, moving.X, 3);
    }

    [Fact]
    public void Add_KeepsTheNewestCapacitySamples()
    {
        var tl = new EntityTimeline();
        for (int i = 0; i < EntityTimeline.Capacity + 5; i++)
            tl.Add(S(i * 0.05f, i), 0.1f);
        Assert.Equal(EntityTimeline.Capacity, tl.Count);
        Assert.Equal(EntityTimeline.Capacity + 4, tl.Newest.X, 3);
    }

    [Fact]
    public void LerpAngle_TakesTheShortWay()
    {
        Assert.Equal(360f, EntityTimeline.LerpAngle(350f, 10f, 0.5f), 3);
        Assert.Equal(-5f, EntityTimeline.LerpAngle(5f, 345f, 0.5f), 3);
    }

    [Fact]
    public void HostClock_FollowsFastestPacket_DecaysSlowly_AndReseedsOnJump()
    {
        var clock = new HostClockEstimator();
        clock.AddSample(hostTime: 100f, localTime: 10f);   // offset 90
        clock.AddSample(hostTime: 100.2f, localTime: 10.1f); // faster packet: 90.1
        Assert.Equal(90.1f, clock.Offset, 3);
        clock.AddSample(hostTime: 100.3f, localTime: 10.4f); // late packet: 89.9, pulls only 2%
        Assert.Equal(90.1f - 0.2f * HostClockEstimator.DecayPerSample, clock.Offset, 3);
        clock.AddSample(hostTime: 5f, localTime: 11f);       // new host clock
        Assert.Equal(-6f, clock.Offset, 3);
        Assert.Equal(4f, clock.ToHost(10f), 3);
    }
}

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
        Assert.Equal(TimelinePoseKind.Interpolated, tl.Sample(1.025f, 0.05f, out TimelineSample p));
        Assert.Equal(5f, p.X, 3);
    }

    [Fact]
    public void Sample_BeforeOldest_HoldsOldest_AndPastNewest_CoastsSlowingThenHolds()
    {
        var tl = new EntityTimeline();
        tl.Add(S(1.00f, 0f), 0.1f);
        tl.Add(S(1.05f, 10f), 0.1f); // 200 units/s
        Assert.Equal(TimelinePoseKind.BeforeOldest, tl.Sample(0.5f, 0.05f, out TimelineSample before));
        Assert.Equal(0f, before.X, 3);
        // Halfway through a 0.05 s coast the body has slowed to half speed: 0.025 - 0.025²/0.1 = 0.01875 s of travel.
        Assert.Equal(TimelinePoseKind.Coasting, tl.Sample(1.075f, 0.05f, out TimelineSample coast));
        Assert.Equal(13.75f, coast.X, 3);
        // Coast over: it stopped half a full-speed coast past the newest sample.
        Assert.Equal(TimelinePoseKind.Holding, tl.Sample(2.0f, 0.05f, out TimelineSample held));
        Assert.Equal(15f, held.X, 3);
    }

    [Fact]
    public void Sample_DoesNotCoastOutOfAClientMadeHold()
    {
        var tl = new EntityTimeline();
        tl.Add(S(1.0f, 0f), 0.1f);
        // Resting gap: a client-made hold goes in at 2.9, the move (to 50) at 3.0.
        tl.Add(S(3.0f, 50f), 0.1f);
        Assert.Equal(TimelinePoseKind.Holding, tl.Sample(3.05f, 0.1f, out TimelineSample p));
        Assert.Equal(50f, p.X, 3);
    }

    [Fact]
    public void Sample_CoastSpeedIsBounded()
    {
        var tl = new EntityTimeline();
        tl.Add(S(1.00f, 0f), 0.1f);
        tl.Add(S(1.01f, 100f), 0.1f); // 10000 units/s: well past any creature
        tl.Sample(5f, 0.1f, out TimelineSample p);
        Assert.Equal(100f + EntityTimeline.MaxCoastSpeed * 0.05f, p.X, 2);
    }

    [Fact]
    public void Sample_TeleportHoldsTheOldSpotUntilItsMoment_ThenJumps_WithoutCoasting()
    {
        var tl = new EntityTimeline();
        tl.Add(S(1.00f, 0f), 0.05f);
        tl.Add(S(1.05f, 5f), 0.05f);
        var jump = S(1.10f, 500f);
        jump.Cut = true;
        tl.Add(jump, 0.05f);
        tl.Sample(1.09f, 0.05f, out TimelineSample beforeJump);
        Assert.Equal(5f, beforeJump.X, 3);
        Assert.Equal(TimelinePoseKind.Holding, tl.Sample(1.12f, 0.05f, out TimelineSample after));
        Assert.Equal(500f, after.X, 3);
        Assert.True(tl.HasCutIn(1.09f, 1.10f));
        Assert.False(tl.HasCutIn(1.10f, 1.2f));
        Assert.False(tl.HasCutIn(1.0f, 1.09f));
    }

    [Fact]
    public void Add_HoldsAcrossAGapOfThreeIntervals_NotOneLostPacket()
    {
        var tl = new EntityTimeline();
        tl.Add(S(1.0f, 0f), 0.1f);
        // One lost packet at 10 Hz: 0.2 s gap, still a straight move.
        tl.Add(S(1.2f, 20f), 0.1f);
        Assert.Equal(2, tl.Count);
        tl.Sample(1.1f, 0.1f, out TimelineSample mid);
        Assert.Equal(10f, mid.X, 3);
        // Four intervals: the body rested; a hold goes in one interval before the new sample.
        tl.Add(S(1.6f, 40f), 0.1f);
        Assert.Equal(4, tl.Count);
        tl.Sample(1.45f, 0.1f, out TimelineSample resting);
        Assert.Equal(20f, resting.X, 3);
    }

    [Fact]
    public void LatestAt_ReturnsTheSampleInForceAtTheRenderTime()
    {
        var tl = new EntityTimeline();
        var a = S(1.0f, 0f); a.Animating = true; a.Clip = "Walk";
        var b = S(1.05f, 1f); b.Animating = false; b.Clip = "Aim";
        tl.Add(a, 0.05f);
        tl.Add(b, 0.05f);
        Assert.True(tl.LatestAt(1.04f, out TimelineSample at));
        Assert.True(at.Animating);
        Assert.Equal("Walk", at.Clip);
        Assert.True(tl.LatestAt(1.2f, out TimelineSample later));
        Assert.Equal("Aim", later.Clip);
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

    private static TimelineSample C(float t, string clip) =>
        new TimelineSample { T = t, HasClip = true, Clip = clip };

    [Fact]
    public void TakeClip_ShowsEachClipWhenTheRenderTimeReachesIt_Once()
    {
        var tl = new EntityTimeline();
        tl.Add(C(1.00f, "Run"), 0.1f);
        tl.Add(C(1.05f, "RotateLeft_Start"), 0.1f);
        Assert.False(tl.TakeClip(0.99f, out _));
        Assert.True(tl.TakeClip(1.02f, out TimelineSample run));
        Assert.Equal("Run", run.Clip);
        Assert.False(tl.TakeClip(1.03f, out _));
        Assert.True(tl.TakeClip(1.06f, out TimelineSample rot));
        Assert.Equal("RotateLeft_Start", rot.Clip);
        Assert.False(tl.TakeClip(2f, out _));
    }

    [Fact]
    public void TakeClip_SkipsSamplesUpToAClipPlayedOnArrival_AndPoseHolds()
    {
        var tl = new EntityTimeline();
        tl.Add(C(1.00f, "Run"), 0.1f);
        tl.Add(C(1.05f, "Attack1"), 0.1f);
        tl.HoldClipsThrough(1.05f); // the attack played when it arrived
        Assert.False(tl.TakeClip(1.02f, out _));
        Assert.False(tl.TakeClip(1.06f, out _));
        // Quiet gap: the client-made pose hold does not replay the attack.
        tl.Add(C(2.00f, "Idle"), 0.1f);
        Assert.False(tl.TakeClip(1.95f, out _));
        Assert.True(tl.TakeClip(2.00f, out TimelineSample idle));
        Assert.Equal("Idle", idle.Clip);
    }

    [Fact]
    public void TakeClip_ShowsAPassThroughClipAtItsStart_ThenTheSamplesOwn()
    {
        var tl = new EntityTimeline();
        tl.Add(C(1.00f, "RotateLeft_Start"), 0.05f);
        var end = C(1.10f, "RotateLeft_End");
        end.PrevClip = "RotateLeft_Loop";
        end.PrevClipT = 1.06f;
        tl.Add(end, 0.05f);
        Assert.True(tl.TakeClip(1.01f, out TimelineSample start));
        Assert.Equal("RotateLeft_Start", start.Clip);
        Assert.False(tl.TakeClip(1.05f, out _));
        Assert.True(tl.TakeClip(1.07f, out TimelineSample loop));
        Assert.Equal("RotateLeft_Loop", loop.Clip);
        Assert.Equal(1.06f, loop.T, 4);
        Assert.Equal(0, loop.ClipFrame);
        Assert.True(tl.TakeClip(1.11f, out TimelineSample endClip));
        Assert.Equal("RotateLeft_End", endClip.Clip);
        Assert.False(tl.TakeClip(1.2f, out _));
    }

    [Fact]
    public void TakeClip_SkipsAPassThroughClipTheRenderTimeAlreadyPassed()
    {
        var tl = new EntityTimeline();
        tl.Add(C(1.00f, "Run"), 0.05f);
        var end = C(1.10f, "RotateLeft_End");
        end.PrevClip = "RotateLeft_Loop";
        end.PrevClipT = 1.06f;
        tl.Add(end, 0.05f);
        Assert.True(tl.TakeClip(1.01f, out _));
        // A long frame jumped past both starts: the newest one shows.
        Assert.True(tl.TakeClip(1.15f, out TimelineSample c));
        Assert.Equal("RotateLeft_End", c.Clip);
        Assert.False(tl.TakeClip(1.2f, out _));
    }

    [Fact]
    public void LerpAngle_TakesTheShortWay()
    {
        Assert.Equal(360f, EntityTimeline.LerpAngle(350f, 10f, 0.5f), 3);
        Assert.Equal(-5f, EntityTimeline.LerpAngle(5f, 345f, 0.5f), 3);
    }

    [Fact]
    public void HostClock_SlewsTowardTheFastestPacketOfTheWindow_AndReseedsOnJump()
    {
        var clock = new HostClockEstimator();
        clock.AddSample(hostTime: 100f, localTime: 10f);    // offset 90
        Assert.Equal(90f, clock.Offset, 4);
        clock.AddSample(hostTime: 100.2f, localTime: 10.1f); // faster packet: target 90.1
        Assert.Equal(90.1f, clock.Target, 4);
        // Slews: at most 5% of the 0.1 s since the last sample.
        Assert.Equal(90f + 0.1f * HostClockEstimator.SlewPerSec, clock.Offset, 4);
        clock.AddSample(hostTime: 100.3f, localTime: 10.4f); // late packet: the window keeps 90.1
        Assert.Equal(90.1f, clock.Target, 4);
        clock.AddSample(hostTime: 5f, localTime: 11f);        // new host clock
        Assert.Equal(-6f, clock.Offset, 4);
        Assert.Equal(4f, clock.ToHost(10f), 4);
    }

    [Fact]
    public void HostClock_ForgetsAFastPacketOnceTheWindowHasMovedOn()
    {
        var clock = new HostClockEstimator();
        clock.AddSample(10.05f, 0f);     // one lucky packet: offset 10.05
        for (int i = 1; i <= 100; i++)  // then 5 s of steady 30 ms later ones (offset 10.02)
            clock.AddSample(10.02f + i * 0.05f, i * 0.05f);
        Assert.Equal(10.02f, clock.Target, 3);
        Assert.Equal(10.02f, clock.Offset, 3);
    }

    [Fact]
    public void ArrivalJitter_MarginCoversTheSpread_AndIsBounded()
    {
        var j = new ArrivalJitter();
        for (int i = 0; i < 200; i++)
            j.Add(i % 2 == 0 ? 0.00f : 0.02f);
        Assert.InRange(j.Mean, 0.005f, 0.015f);
        Assert.InRange(j.Margin, 0.02f, 0.04f);
        for (int i = 0; i < 200; i++)
            j.Add(5f);
        Assert.Equal(ArrivalJitter.MaxMargin, j.Margin, 4);
    }

    [Fact]
    public void ClipStartLog_ReportsTheClipASendWouldMiss()
    {
        var log = new ClipStartLog();
        log.MarkSent(7, "RotateLeft_Start", 1.00f);
        log.Note(7, "RotateLeft_Loop", 1.02f);
        log.Note(7, "RotateLeft_End", 1.04f);
        Assert.True(log.TryIntermediate(7, "RotateLeft_End", out string clip, out float t));
        Assert.Equal("RotateLeft_Loop", clip);
        Assert.Equal(1.02f, t, 4);
        log.MarkSent(7, "RotateLeft_End", 1.05f);
        Assert.False(log.TryIntermediate(7, "RotateLeft_End", out _, out _));
    }

    [Fact]
    public void ClipStartLog_NothingMissedForAPlainChange_AndChosenButNotStartedClipCounts()
    {
        var log = new ClipStartLog();
        log.MarkSent(3, "Idle", 1.00f);
        log.Note(3, "Walk", 1.02f);
        Assert.False(log.TryIntermediate(3, "Walk", out _, out _));
        // Hit started, the AI already chose Idle again (played next frame): Hit is the pass-through.
        log.MarkSent(3, "Walk", 1.05f);
        log.Note(3, "Hit1", 1.07f);
        Assert.True(log.TryIntermediate(3, "Idle", out string clip, out _));
        Assert.Equal("Hit1", clip);
        // Another body under a recycled id knows nothing of it.
        Assert.False(log.TryIntermediate(4, "Idle", out _, out _));
    }
}

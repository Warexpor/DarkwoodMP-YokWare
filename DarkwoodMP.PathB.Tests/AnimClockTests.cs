using DWMPHorde;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// The shared animation clock (<c>Sync.AnimClock</c>): the client's estimate of the host clock,
/// and the clock-driven timing every machine computes the same way.
/// </summary>
public class AnimClockTests
{
    [Fact]
    public void ClockSync_TakesTheLeastQueuedSample()
    {
        var sync = new ClockSync();
        // Host is 100 s ahead; one-way delay 20 ms on the clean ping, a queued ping adds 200 ms.
        sync.AddSample(10.0, 110.02, 10.04);
        Assert.True(sync.HasEstimate);
        Assert.Equal(100.0, sync.Offset, 3);
        sync.AddSample(11.0, 111.22, 11.24); // queued on the way there: 220 ms out, 20 ms back
        Assert.Equal(100.0, sync.Target, 3); // the clean sample still wins
        Assert.Equal(112.0, sync.HostNow(12.0), 3);
    }

    [Fact]
    public void ClockSync_SlewsSmallChangesAndSnapsLargeOnes()
    {
        var sync = new ClockSync();
        sync.AddSample(0.0, 50.0, 0.0);
        Assert.Equal(50.0, sync.Offset, 6);
        for (int i = 1; i <= 8; i++)
            sync.AddSample(i, 50.05 + i, i); // the host clock is 50 ms further on
        Assert.True(sync.Offset < 50.05 && sync.Offset > 50.0, "a small step slews: " + sync.Offset);
        Assert.Equal(0, sync.Snaps);
        for (int i = 9; i <= 17; i++)
            sync.AddSample(i, 500.0 + i, i); // a new host after a migration
        Assert.Equal(500.0, sync.Offset, 6);
        Assert.True(sync.Snaps >= 1);
    }

    [Fact]
    public void ClockSync_IgnoresStalledRoundTrips()
    {
        var sync = new ClockSync();
        sync.AddSample(0.0, 50.0, ClockSync.MaxRttSec + 1);
        Assert.False(sync.HasEstimate);
    }

    [Fact]
    public void WrapError_TakesTheShortWayRound()
    {
        Assert.Equal(1.0, AnimTiming.WrapError(1.0, 8.0), 9);
        Assert.Equal(-1.0, AnimTiming.WrapError(7.0, 8.0), 9);
        Assert.Equal(-1.0, AnimTiming.WrapError(-9.0, 8.0), 9);
        Assert.Equal(4.0, AnimTiming.WrapError(4.0, 8.0), 9);
        Assert.Equal(3.5, AnimTiming.Mod(-4.5, 8.0), 9);
    }

    [Fact]
    public void Replays_KeepVanillaGapsAndAgreeFromAnyStartingPoint()
    {
        const int seed = 424242;
        const double min = 2.0, max = 8.0;
        double prev = AnimTiming.ReplayTime(seed, min, max, 0);
        for (long k = 1; k < 2000; k++)
        {
            double t = AnimTiming.ReplayTime(seed, min, max, k);
            double gap = t - prev;
            Assert.InRange(gap, min - 1e-9, max + 1e-9);
            prev = t;
        }
        // Two machines asking at different moments land on the same replay sequence.
        double a = AnimTiming.NextReplay(seed, min, max, 1000.0, out long ka);
        double b = AnimTiming.NextReplay(seed, min, max, 999.0, out long kb);
        Assert.True(a > 1000.0);
        Assert.Equal(AnimTiming.ReplayTime(seed, min, max, ka), a);
        Assert.True(kb <= ka);
        Assert.True(AnimTiming.ReplayTime(seed, min, max, ka - 1) <= 1000.0);
    }

    [Fact]
    public void Twitch_GoesOutAndBackThenRests()
    {
        const int seed = 77;
        const int frames = 10;
        const double fps = 10;
        bool sawTwitch = false, sawRest = false;
        int last = AnimTiming.TwitchFrame(seed, frames, fps, 0.0);
        for (int i = 0; i < 3000; i++)
        {
            double t = i * 0.02;
            int f = AnimTiming.TwitchFrame(seed, frames, fps, t);
            Assert.InRange(f, 0, frames - 1);
            Assert.True(System.Math.Abs(f - last) <= 2, "no jumps at t=" + t + ": " + last + "->" + f);
            if (f > 0) sawTwitch = true;
            if (f == 0 && sawTwitch) sawRest = true;
            last = f;
        }
        Assert.True(sawTwitch && sawRest);
        Assert.Equal(AnimTiming.TwitchFrame(seed, frames, fps, 12.34), AnimTiming.TwitchFrame(seed, frames, fps, 12.34));
    }
}

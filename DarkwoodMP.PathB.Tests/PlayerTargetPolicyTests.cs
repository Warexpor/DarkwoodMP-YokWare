using DWMPHorde;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// <see cref="PlayerTargetPolicy"/>: which player body a host creature targets when several are
/// around (every body alike, hold the current one, switch only when it is lost or, on the
/// closer-enemy check, when another is clearly nearer).
/// </summary>
public class PlayerTargetPolicyTests
{
    private static PlayerTargetCandidate Body(float distance, bool sensed = true, bool valid = true)
        => new PlayerTargetCandidate { Distance = distance, Sensed = sensed, Valid = valid };

    private static int Choose(PlayerTargetCandidate[] c, int current, bool held, out PlayerTargetReason reason,
        int pinned = -1, bool window = false, float sinceSwitch = 100f)
        => PlayerTargetPolicy.Choose(c, c.Length, current, held, pinned, window, sinceSwitch, out reason);

    [Fact]
    public void NoTarget_AcquiresNearestSensedBody()
    {
        var c = new[] { Body(300f), Body(120f), Body(200f) };
        Assert.Equal(1, Choose(c, -1, false, out var reason));
        Assert.Equal(PlayerTargetReason.Acquire, reason);
    }

    [Fact]
    public void NoTarget_IgnoresBodiesNotSensed()
    {
        var c = new[] { Body(300f), Body(50f, sensed: false) };
        Assert.Equal(0, Choose(c, -1, false, out _));
    }

    [Fact]
    public void NoTarget_NobodySensed_LeavesTargetAlone()
    {
        var c = new[] { Body(50f, sensed: false), Body(60f, sensed: false) };
        Assert.Equal(-1, Choose(c, -1, false, out var reason));
        Assert.Equal(PlayerTargetReason.None, reason);
    }

    [Fact]
    public void HostAndStandInAreAlike_NearestWinsWhicheverIndex()
    {
        // Index 0 is the host in the mod; the stand-in at index 1 is nearer and wins.
        Assert.Equal(1, Choose(new[] { Body(100f), Body(99f) }, -1, false, out _));
        Assert.Equal(0, Choose(new[] { Body(99f), Body(100f) }, -1, false, out _));
    }

    [Fact]
    public void Held_KeptWhenAnotherIsNearerOutsideTheSwitchWindow()
    {
        // The sight check (no switch window) never moves a held target, however near the other is.
        var c = new[] { Body(200f), Body(20f) };
        Assert.Equal(0, Choose(c, 0, true, out var reason));
        Assert.Equal(PlayerTargetReason.Keep, reason);
    }

    [Fact]
    public void Held_KeptOnCloserCheckWhenOtherIsOnlySlightlyNearer()
    {
        // 160 is not under 75% of 200 (150): side by side or crossing players keep the creature.
        var c = new[] { Body(200f), Body(160f) };
        Assert.Equal(0, Choose(c, 0, true, out var reason, window: true));
        Assert.Equal(PlayerTargetReason.Keep, reason);
    }

    [Fact]
    public void Held_SwitchesOnCloserCheckWhenOtherIsClearlyNearer()
    {
        var c = new[] { Body(200f), Body(140f) };
        Assert.Equal(1, Choose(c, 0, true, out var reason, window: true));
        Assert.Equal(PlayerTargetReason.ClearlyNearer, reason);
    }

    [Fact]
    public void Held_ExactRatioBoundaryDoesNotSwitch()
    {
        var c = new[] { Body(200f), Body(150f) };
        Assert.Equal(0, Choose(c, 0, true, out _, window: true));
    }

    [Fact]
    public void Held_NoSwitchSoonAfterTheLastOne()
    {
        var c = new[] { Body(200f), Body(40f) };
        Assert.Equal(0, Choose(c, 0, true, out var reason, window: true, sinceSwitch: 1f));
        Assert.Equal(PlayerTargetReason.Keep, reason);
        Assert.Equal(1, Choose(c, 0, true, out _, window: true,
            sinceSwitch: PlayerTargetPolicy.MinSecondsBetweenSwitches));
    }

    [Fact]
    public void Held_ClearlyNearerMustBeSensed()
    {
        var c = new[] { Body(200f), Body(10f, sensed: false) };
        Assert.Equal(0, Choose(c, 0, true, out _, window: true));
    }

    [Fact]
    public void Lost_GoesToNearestSensedRightAway()
    {
        // Not held (out of sight past the grace): vanilla retargets to what it sees at once.
        var c = new[] { Body(100f, sensed: false), Body(400f), Body(300f) };
        Assert.Equal(2, Choose(c, 0, false, out var reason));
        Assert.Equal(PlayerTargetReason.CurrentLost, reason);
    }

    [Fact]
    public void Lost_NobodyElseSensed_KeepsTargetForVanillaLostEnemy()
    {
        var c = new[] { Body(100f, sensed: false), Body(400f, sensed: false) };
        Assert.Equal(0, Choose(c, 0, false, out var reason));
        Assert.Equal(PlayerTargetReason.Keep, reason);
    }

    [Fact]
    public void InvalidCurrent_DeadOrInvisible_GivesWayEvenIfHeld()
    {
        var c = new[] { Body(50f, valid: false), Body(300f) };
        Assert.Equal(1, Choose(c, 0, true, out var reason));
        Assert.Equal(PlayerTargetReason.CurrentLost, reason);
    }

    [Fact]
    public void InvalidCurrent_NobodySensed_ReturnsNone()
    {
        var c = new[] { Body(50f, valid: false), Body(300f, sensed: false) };
        Assert.Equal(-1, Choose(c, 0, false, out _));
    }

    [Fact]
    public void InvalidBodiesAreNeverAcquired()
    {
        var c = new[] { Body(10f, valid: false), Body(300f) };
        Assert.Equal(1, Choose(c, -1, false, out _));
    }

    [Fact]
    public void Pinned_WinsOverEverything()
    {
        var c = new[] { Body(10f), Body(900f, sensed: false) };
        Assert.Equal(1, Choose(c, 0, true, out var reason, pinned: 1, window: true));
        Assert.Equal(PlayerTargetReason.Pinned, reason);
    }

    [Fact]
    public void Pinned_InvalidFallsBackToNormalRules()
    {
        var c = new[] { Body(10f), Body(900f, valid: false) };
        Assert.Equal(0, Choose(c, -1, false, out var reason, pinned: 1));
        Assert.Equal(PlayerTargetReason.Acquire, reason);
    }

    [Fact]
    public void ThreePlayers_HoldsThenSwitchesToTheClearlyNearestOne()
    {
        var c = new[] { Body(500f), Body(380f), Body(200f) };
        Assert.Equal(1, Choose(c, 1, true, out _));
        Assert.Equal(2, Choose(c, 1, true, out _, window: true));
    }

    [Fact]
    public void ApproachingTwoPlayers_DoesNotFlipBackAndForth()
    {
        // A creature walks toward two players 70 apart; distances shrink and cross by small amounts.
        // Held the whole way: it never switches, though the nearest one alternates.
        int current = 0;
        float[][] steps =
        {
            new[] { 300f, 310f }, new[] { 250f, 245f }, new[] { 200f, 205f },
            new[] { 150f, 140f }, new[] { 100f, 110f }, new[] { 60f, 55f },
        };
        foreach (float[] d in steps)
        {
            var c = new[] { Body(d[0]), Body(d[1]) };
            current = Choose(c, current, true, out _, window: true);
            Assert.Equal(0, current);
        }
    }

    [Theory]
    [InlineData(true, true, 100f, true)]
    [InlineData(true, false, 0.5f, true)]
    [InlineData(true, false, PlayerTargetPolicy.LostGraceSeconds, true)]
    [InlineData(true, false, 2.5f, false)]
    [InlineData(false, true, 0f, false)]
    public void CurrentHeld_SensedOrWithinGrace(bool valid, bool sensed, float since, bool expected)
        => Assert.Equal(expected, PlayerTargetPolicy.CurrentHeld(valid, sensed, since));

    [Fact]
    public void EmptyOrNull_ReturnsNone()
    {
        Assert.Equal(-1, PlayerTargetPolicy.Choose(null, 0, -1, false, -1, false, 0f, out _));
        Assert.Equal(-1, PlayerTargetPolicy.Choose(new PlayerTargetCandidate[0], 0, -1, false, -1, false, 0f, out _));
    }
}

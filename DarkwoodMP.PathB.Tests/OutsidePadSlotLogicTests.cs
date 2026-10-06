using DWMPHorde.Sync;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// The host-owned outside-location pad allocation, without the game: same name keeps its slot,
/// distinct names never share one, a reset forgets everything, and the host's "unavailable"
/// answer (slot -1) places the pad the vanilla way.
/// </summary>
public class OutsidePadSlotLogicTests
{
    private const int SlotCount = 4;
    private static readonly Func<string, bool> NoneAlive = _ => false;
    private static int _yaw;
    private static int NextYaw() => (_yaw += 90) % 360;

    private static OutsidePadSlotLogic.Entry Allocate(OutsidePadSlotLogic logic, string name,
        Func<string, bool> alive = null, int slotCount = SlotCount)
        => logic.Allocate(name, slotCount, alive ?? NoneAlive, NextYaw, out _);

    [Fact]
    public void SameName_GetsTheSameSlot_WhileItsAssignmentIsPending()
    {
        var logic = new OutsidePadSlotLogic();
        var first = logic.Allocate("bunker", SlotCount, NoneAlive, NextYaw, out bool firstIsNew);
        var again = logic.Allocate("bunker", SlotCount, NoneAlive, NextYaw, out bool againIsNew);

        Assert.True(firstIsNew);
        Assert.False(againIsNew);
        Assert.Same(first, again);
        Assert.Equal(first.Slot, again.Slot);
        Assert.Equal(first.Yaw, again.Yaw);
    }

    [Fact]
    public void ConsumedButAlivePad_KeepsItsSlot_ConsumedAndGonePad_MayBeReallocated()
    {
        var logic = new OutsidePadSlotLogic();
        var e = Allocate(logic, "cellar");
        e.Consumed = true;

        Assert.Same(e, Allocate(logic, "cellar", alive: n => n == "cellar"));

        var fresh = logic.Allocate("cellar", SlotCount, NoneAlive, NextYaw, out bool isNew);
        Assert.True(isNew);
        Assert.NotSame(e, fresh);
    }

    [Fact]
    public void DistinctNames_GetDistinctSlots_AndTheCounterOnlyMovesForward()
    {
        var logic = new OutsidePadSlotLogic();
        var a = Allocate(logic, "a");
        var b = Allocate(logic, "b");
        var c = Allocate(logic, "c");

        Assert.Equal(new[] { 0, 1, 2 }, new[] { a.Slot, b.Slot, c.Slot });
        Assert.Equal(3, logic.HighWater);
    }

    [Fact]
    public void FreedSlot_IsNotReused_UntilTheTableIsExhausted()
    {
        var logic = new OutsidePadSlotLogic();
        var a = Allocate(logic, "a");
        a.Consumed = true; // spawned, then its pad was destroyed (not alive)
        Allocate(logic, "b");
        Allocate(logic, "c");

        // Monotonic counter first: slot 3 is the next never-used one, not the freed slot 0.
        Assert.Equal(3, Allocate(logic, "d").Slot);
        // Table full: only now does the freed slot 0 come back.
        Assert.Equal(0, Allocate(logic, "e").Slot);
    }

    [Fact]
    public void ExhaustedTable_WithEverySlotHeld_ReturnsNull()
    {
        var logic = new OutsidePadSlotLogic();
        for (int i = 0; i < SlotCount; i++)
            Assert.NotNull(Allocate(logic, "n" + i));

        // Every assignment is still pending (unconsumed), so nothing is free.
        Assert.Null(Allocate(logic, "overflow"));
        Assert.Null(Allocate(new OutsidePadSlotLogic(), "x", slotCount: 0));
    }

    [Fact]
    public void Reset_ForgetsAssignments_InFlightAndTheCounter()
    {
        var logic = new OutsidePadSlotLogic();
        Allocate(logic, "a");
        Allocate(logic, "b");
        logic.BeginSpawn("a");

        logic.Reset();

        Assert.False(logic.TryGet("a", out _));
        Assert.False(logic.IsInFlight("a"));
        Assert.Equal(0, logic.HighWater);
        Assert.Equal(0, Allocate(logic, "z").Slot);
    }

    [Fact]
    public void HostUnavailable_MinusOne_IsConsumedAndFallsBackToVanilla()
    {
        var logic = new OutsidePadSlotLogic();
        Assert.True(logic.ApplyHostAssignment("pocket", -1, 0));
        Assert.True(logic.TryGet("pocket", out var entry));

        Assert.Null(OutsidePadSlotLogic.ConsumeIfUnavailable(entry));
        Assert.True(entry.Consumed);
        // A consumed "unavailable" never hands out a yaw.
        Assert.False(logic.TryConsumeYaw("pocket", out _));
        // A real assignment passes through untouched.
        var real = new OutsidePadSlotLogic.Entry { Slot = 2, Yaw = 90 };
        Assert.Same(real, OutsidePadSlotLogic.ConsumeIfUnavailable(real));
        Assert.False(real.Consumed);
        Assert.Null(OutsidePadSlotLogic.ConsumeIfUnavailable(null));
    }

    [Fact]
    public void HostAssignment_IsIgnoredWhileThatSpawnIsInFlight()
    {
        var logic = new OutsidePadSlotLogic();
        Assert.True(logic.ApplyHostAssignment("village", 1, 180));
        logic.BeginSpawn("village");

        Assert.False(logic.ApplyHostAssignment("village", 3, 0));
        Assert.True(logic.TryGet("village", out var e));
        Assert.Equal(1, e.Slot);

        logic.EndSpawn("village");
        Assert.True(logic.ApplyHostAssignment("village", 3, 0));
    }

    [Fact]
    public void Yaw_IsHandedOutOnce()
    {
        var logic = new OutsidePadSlotLogic();
        logic.ApplyHostAssignment("bunker", 0, 270);

        Assert.True(logic.TryConsumeYaw("bunker", out int yaw));
        Assert.Equal(270, yaw);
        Assert.False(logic.TryConsumeYaw("bunker", out _));
        Assert.False(logic.TryConsumeYaw("unknown", out _));
    }

    [Fact]
    public void ExistingPad_IsLearnedMatchedRebasedOrFlagged()
    {
        var host = new OutsidePadSlotLogic();
        Assert.Equal(OutsidePadSlotLogic.SeedResult.Learned, host.NotePadExists("a", 2, 90, authority: true));
        Assert.Equal(3, host.HighWater);
        Assert.True(host.TryGet("a", out var learned));
        Assert.True(learned.Consumed);

        Assert.Equal(OutsidePadSlotLogic.SeedResult.Matched, host.NotePadExists("a", 2, 90, authority: true));
        Assert.Equal(OutsidePadSlotLogic.SeedResult.Rebased, host.NotePadExists("a", 3, 180, authority: true));
        Assert.Equal(3, learned.Slot);
        Assert.Equal(180, learned.Yaw);

        var client = new OutsidePadSlotLogic();
        client.ApplyHostAssignment("a", 1, 0);
        Assert.Equal(OutsidePadSlotLogic.SeedResult.Mismatch, client.NotePadExists("a", 2, 90, authority: false));
        Assert.True(client.TryGet("a", out var kept));
        Assert.Equal(1, kept.Slot); // the host's assignment wins on a client
        Assert.True(kept.Consumed);
    }

    [Fact]
    public void Snapshot_SkipsUnavailableAndStaleEntries()
    {
        var logic = new OutsidePadSlotLogic();
        var live = new OutsidePadSlotLogic.Entry { Slot = 1, Consumed = true };
        var stale = new OutsidePadSlotLogic.Entry { Slot = 2, Consumed = true };
        var pending = new OutsidePadSlotLogic.Entry { Slot = 3, Consumed = false };
        var none = new OutsidePadSlotLogic.Entry { Slot = -1, Consumed = false };

        Assert.True(logic.ShouldSnapshot("live", live, n => n == "live"));
        Assert.False(logic.ShouldSnapshot("stale", stale, NoneAlive));
        Assert.True(logic.ShouldSnapshot("pending", pending, NoneAlive));
        Assert.False(logic.ShouldSnapshot("none", none, NoneAlive));

        logic.BeginSpawn("stale");
        Assert.True(logic.ShouldSnapshot("stale", stale, NoneAlive));
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(90.4f, 90)]
    [InlineData(359.6f, 0)]
    [InlineData(360f, 0)]
    [InlineData(-90f, 270)]
    public void NormalizeYaw_IsWholeDegreesInRange(float input, int expected)
        => Assert.Equal(expected, OutsidePadSlotLogic.NormalizeYaw(input));

    [Fact]
    public void DeriveSlot_MatchesWithinEpsilonOnly()
    {
        float[] xs = { 0f, 5000f, 10000f };
        float[] zs = { 0f, 0f, 5000f };

        int Find(float x, float z) => OutsidePadSlotLogic.DeriveSlot(3, i => xs[i], i => zs[i], x, z, 0.5f);

        Assert.Equal(1, Find(5000.2f, -0.3f));
        Assert.Equal(2, Find(10000f, 5000f));
        Assert.Equal(-1, Find(5001f, 0f));
        Assert.Equal(-1, Find(123f, 456f));
    }
}

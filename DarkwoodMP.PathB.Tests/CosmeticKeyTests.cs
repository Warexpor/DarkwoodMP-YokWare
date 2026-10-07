using DWMPHorde;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// The seed rules every machine has to agree on for cosmetic rolls (<c>Sync.CosmeticRolls</c>):
/// hashing must not depend on the runtime, cells must not split a placement that differs only by
/// float noise, and location roots keep their authored name.
/// </summary>
public class CosmeticKeyTests
{
    [Fact]
    public void Hashing_IsStableAcrossRuns()
    {
        // Fixed expectations: a runtime-dependent hash (string.GetHashCode) would change them.
        uint h = CosmeticKey.Add(CosmeticKey.Start(), "grass_01");
        h = CosmeticKey.Add(h, CosmeticKey.Cell(1234.4f, CosmeticKey.LocalCell));
        int a = CosmeticKey.Finish(h);
        uint h2 = CosmeticKey.Add(CosmeticKey.Start(), "grass_01");
        h2 = CosmeticKey.Add(h2, 1234);
        Assert.Equal(a, CosmeticKey.Finish(h2));
        Assert.NotEqual(a, CosmeticKey.Finish(CosmeticKey.Add(CosmeticKey.Add(CosmeticKey.Start(), "grass_02"), 1234)));
    }

    [Fact]
    public void Cell_AbsorbsFloatNoiseAwayFromItsEdge()
    {
        Assert.Equal(CosmeticKey.Cell(1234.0f, 1f), CosmeticKey.Cell(1234.0001f, 1f));
        Assert.Equal(CosmeticKey.Cell(1234.0f, 1f), CosmeticKey.Cell(1233.9999f, 1f));
        Assert.Equal(-75000 / 50, CosmeticKey.Cell(-75000.008f, CosmeticKey.RootCell));
        // World-grid roots (multiples of 300) sit mid-cell: noise never moves them to the next cell.
        Assert.Equal(CosmeticKey.Cell(9300f, CosmeticKey.RootCell), CosmeticKey.Cell(9300.02f, CosmeticKey.RootCell));
        Assert.Equal(CosmeticKey.Cell(9300f, CosmeticKey.RootCell), CosmeticKey.Cell(9299.98f, CosmeticKey.RootCell));
        Assert.Equal(int.MinValue, CosmeticKey.Cell(float.NaN, 1f));
    }

    [Fact]
    public void Mix_SeparatesRollsUnderOneAnchor()
    {
        int anchor = 987654;
        Assert.NotEqual(CosmeticKey.Mix(anchor, 1u), CosmeticKey.Mix(anchor, 2u));
        Assert.Equal(CosmeticKey.Mix(anchor, 7u), CosmeticKey.Mix(anchor, 7u));
        Assert.NotEqual(CosmeticKey.Salt(anchor, "AnimationPlay"), CosmeticKey.Salt(anchor, "VineSpawner"));
    }

    [Theory]
    [InlineData("outside_bunker_underground_02_done", "outside_bunker_underground_02")]
    [InlineData("gridObj_lakes_1_01", "gridObj_lakes_1_01")]
    [InlineData("x_done_done", "x")]
    [InlineData("", "")]
    public void RootName_DropsThePlacedSuffix(string name, string expected)
        => Assert.Equal(expected, CosmeticKey.RootName(name));
}

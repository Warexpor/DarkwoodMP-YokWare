using DWMPHorde.Sync;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>Desync check: entry text, hashing and the two-checks-in-a-row reporting rule.</summary>
public class DesyncLedgerTests
{
    private static List<KeyValuePair<string, string>> E(params (string k, string v)[] kv)
        => kv.Select(p => new KeyValuePair<string, string>(p.k, p.v)).ToList();

    [Fact]
    public void Compare_PairsAnObjectOnAGridLine_KeyedOneStepApart()
    {
        var host = new Dictionary<string, string> { ["Wardrobe@12056,11,11370"] = "items=a", ["Chest@1,2,3"] = "items=b" };
        var client = new Dictionary<string, string> { ["Wardrobe@12056,11,11371"] = "items=a", ["Chest@1,2,5"] = "items=b" };
        var diffs = DesyncEntries.Compare(host, client, DesyncEntries.Exact);
        // The wardrobe is one object; the chest two steps apart is not.
        Assert.Equal(2, diffs.Count);
        Assert.All(diffs, d => Assert.StartsWith("Chest@", d.Key));
        Assert.False(DesyncEntries.Neighbours("Wardrobe@1,2,3", "Chest@1,2,3"));
        Assert.True(DesyncEntries.Neighbours("Door@-639,11,23690", "Door@-638,11,23691"));
    }

    [Fact]
    public void Format_IsOrderIndependent_AndHashesAgree()
    {
        string a = DesyncEntries.Format(E(("b", "1"), ("a", "2")));
        string b = DesyncEntries.Format(E(("a", "2"), ("b", "1")));
        Assert.Equal(a, b);
        Assert.Equal(DesyncEntries.Hash(a), DesyncEntries.Hash(b));
        Assert.NotEqual(DesyncEntries.Hash(a), DesyncEntries.Hash(DesyncEntries.Format(E(("a", "2"), ("b", "0")))));
    }

    [Fact]
    public void Format_NumbersDuplicateKeys_AndParseRoundTrips()
    {
        string f = DesyncEntries.Format(E(("door", "open"), ("door", "closed"), ("x\ty", "a\nb")));
        var map = DesyncEntries.Parse(f);
        Assert.Equal(3, map.Count);
        Assert.Equal("closed", map["door"]);
        Assert.Equal("open", map["door#2"]);
        Assert.Equal("a b", map["x y"]);
        Assert.Equal(3, DesyncEntries.Count(f));
    }

    [Fact]
    public void Compare_FindsMissingExtraAndChanged()
    {
        var host = DesyncEntries.Parse(DesyncEntries.Format(E(("a", "1"), ("b", "1"))));
        var client = DesyncEntries.Parse(DesyncEntries.Format(E(("b", "2"), ("c", "1"))));
        var d = DesyncEntries.Compare(host, client, null);
        Assert.Equal(new[] { "a", "b", "c" }, d.Select(x => x.Key));
        Assert.Null(d[0].Client);
        Assert.Null(d[2].Host);
        Assert.Single(DesyncEntries.Compare(host, client, null, hostTruncated: true).Where(x => x.Key == "a"));
        Assert.DoesNotContain(DesyncEntries.Compare(host, client, null, hostTruncated: true), x => x.Key == "c");
    }

    private static List<DesyncEntries.Diff> D(params string[] keys)
        => keys.Select(k => new DesyncEntries.Diff { Key = k, Host = "h", Client = "c" }).ToList();

    [Fact]
    public void Ledger_ReportsOnlyAfterTwoChecksInARow_ThenResolves()
    {
        var l = new DesyncLedger();
        Assert.Empty(l.Evaluate(1, "Doors", D("d1")));               // first sighting: in flight
        var second = l.Evaluate(1, "Doors", D("d1"));
        Assert.Single(second);
        Assert.StartsWith("DESYNC Doors d1", second[0]);
        Assert.Empty(l.Evaluate(1, "Doors", D("d1")));               // reported once
        var gone = l.Evaluate(1, "Doors", D());
        Assert.Equal(new[] { "resolved Doors d1" }, gone);
    }

    [Fact]
    public void Ledger_GapResetsPending_AndSectionsAreIndependent()
    {
        var l = new DesyncLedger();
        l.Evaluate(1, "Doors", D("d1"));
        l.Evaluate(1, "Doors", D());                                  // went away
        Assert.Empty(l.Evaluate(1, "Doors", D("d1")));               // starts over
        l.Evaluate(2, "Flags", D("d1"));
        Assert.Single(l.Evaluate(2, "Flags", D("d1")));
        Assert.Single(l.Evaluate(1, "Doors", D("d1")));
    }

    [Fact]
    public void Ledger_CapsLinesPerCheck()
    {
        var l = new DesyncLedger();
        var many = D(Enumerable.Range(0, 40).Select(i => "k" + i.ToString("D2")).ToArray());
        l.Evaluate(3, "Items", many);
        var lines = l.Evaluate(3, "Items", many);
        Assert.Equal(DesyncLedger.MaxLinesPerCheck + 1, lines.Count);
        Assert.Equal("... and 15 more in Items", lines[^1]);
    }
}

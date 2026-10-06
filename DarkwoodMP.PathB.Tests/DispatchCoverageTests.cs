using System.Text.RegularExpressions;
using DWMPHorde.Networking;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// The wire enum and the inbound handler table must agree: an id nobody registers is silently
/// dropped; registering an id twice throws at startup.
/// </summary>
public class DispatchCoverageTests
{
    /// <summary>
    /// Every real message. <c>_Highest</c> is an alias of the last id, so it is excluded by NAME
    /// (comparing values would also drop the real message that owns that id).
    /// </summary>
    private static IEnumerable<NetMessageType> AllMessageTypes() =>
        Enum.GetNames<NetMessageType>()
            .Where(n => n != nameof(NetMessageType._Highest))
            .Select(n => Enum.Parse<NetMessageType>(n));

    [Fact]
    public void MessageIds_AreUnique_AndHighestIsTheMaximum()
    {
        var all = AllMessageTypes().ToList();
        var dupes = all.GroupBy(t => (byte)t).Where(g => g.Count() > 1)
            .Select(g => (int)g.Key + ": " + string.Join(", ", g)).ToList();
        Assert.True(dupes.Count == 0, "duplicate message ids: " + string.Join("; ", dupes));

        Assert.Equal((byte)NetMessageType._Highest, all.Max(t => (byte)t));
    }

    [Fact]
    public void EveryMessageType_IsDispatchedExactlyOnce()
    {
        string dir = Path.Combine(TestPaths.ModDir, "Networking", "Dispatch");
        var counts = new Dictionary<string, int>();
        foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\bOn(?:Raw)?\(\s*NetMessageType\.(\w+)\s*,"))
                counts[m.Groups[1].Value] = counts.GetValueOrDefault(m.Groups[1].Value) + 1;
        }

        var missing = AllMessageTypes().Where(t => !counts.ContainsKey(t.ToString()))
            .Select(t => t + "=" + (byte)t).ToList();
        var repeated = counts.Where(kv => kv.Value > 1).Select(kv => kv.Key + " x" + kv.Value).ToList();
        var unknown = counts.Keys.Where(n => !Enum.TryParse<NetMessageType>(n, out _)).ToList();

        Assert.True(missing.Count == 0, "message types with no inbound handler: " + string.Join(", ", missing));
        Assert.True(repeated.Count == 0, "message types registered more than once: " + string.Join(", ", repeated));
        Assert.True(unknown.Count == 0, "handlers for unknown message types: " + string.Join(", ", unknown));
    }

    [Fact]
    public void HostOnlyTypes_AreNeverRelayed()
    {
        // A [HostOnly] type is dropped when a client sends it, so marking it forwardable is a contradiction.
        foreach (var field in typeof(NetMessageType).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            bool hostOnly = Attribute.IsDefined(field, typeof(HostOnlyAttribute));
            bool relayed = Attribute.IsDefined(field, typeof(ForwardableAttribute))
                || Attribute.IsDefined(field, typeof(ForwardablePlayerAttribute));
            Assert.False(hostOnly && relayed, field.Name + " is both [HostOnly] and forwardable");
        }
    }

    [Fact]
    public void HandshakeIsNotHostOnly()
    {
        // Clients must be able to open the session.
        var field = typeof(NetMessageType).GetField(nameof(NetMessageType.Handshake))!;
        Assert.False(Attribute.IsDefined(field, typeof(HostOnlyAttribute)));
    }
}

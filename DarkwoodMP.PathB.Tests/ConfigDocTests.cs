using System.Text.RegularExpressions;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>docs/CONFIG.md must list every key ModConfig.Bind creates, in the right section.</summary>
public class ConfigDocTests
{
    private static List<(string Section, string Key)> BoundKeys()
    {
        string src = TestPaths.Read("DarkwoodMP.Mod", "Config", "ModConfig.cs");
        var keys = Regex.Matches(src, @"config\.Bind\(\s*""([^""]+)""\s*,\s*""([^""]+)""")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
        return keys;
    }

    [Fact]
    public void ModConfig_BindsAtLeastTheKnownSections()
    {
        var sections = BoundKeys().Select(k => k.Section).Distinct().ToList();
        foreach (string expected in new[] { "Network", "Saves", "Gameplay", "Voice", "Logging", "Debug" })
            Assert.Contains(expected, sections);
    }

    [Fact]
    public void ConfigDoc_ListsEveryBoundKeyUnderItsSection()
    {
        string doc = TestPaths.Read("DarkwoodMP.Mod", "docs", "CONFIG.md");

        // Split into "## Section" chunks.
        var chunks = Regex.Split(doc, @"^## ", RegexOptions.Multiline).Skip(1)
            .ToDictionary(c => c.Substring(0, c.IndexOf('\n')).Trim(), c => c);

        var missing = new List<string>();
        foreach (var (section, key) in BoundKeys())
        {
            if (!chunks.TryGetValue(section, out string chunk) || !chunk.Contains("| `" + key + "` |"))
                missing.Add(section + "." + key);
        }
        Assert.True(missing.Count == 0, "keys missing from docs/CONFIG.md: " + string.Join(", ", missing));
    }

    [Fact]
    public void ConfigDoc_HasNoKeysThatAreNotBound()
    {
        string doc = TestPaths.Read("DarkwoodMP.Mod", "docs", "CONFIG.md");
        var bound = BoundKeys().Select(k => k.Key).ToHashSet();
        var documented = Regex.Matches(doc, @"^\| `(\w+)` \|", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value);
        var stale = documented.Where(k => !bound.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "docs/CONFIG.md lists unbound keys: " + string.Join(", ", stale));
    }

    [Fact]
    public void FreshDefaults_MatchTheDocumentedPolicy()
    {
        string src = TestPaths.Read("DarkwoodMP.Mod", "Config", "ModConfig.cs");
        // Privacy-safe / quiet defaults: cursor free + verbose entity logs are opt-in, IP redaction opt-out.
        Assert.Matches(@"""FreeCursorForDualBox"",\s*false", src);
        Assert.Matches(@"""VerboseEntitySync"",\s*false", src);
        Assert.Matches(@"""LogRedactIPs"",\s*true", src);
        Assert.Matches(@"""ChatEnabled"",\s*true", src);
    }
}

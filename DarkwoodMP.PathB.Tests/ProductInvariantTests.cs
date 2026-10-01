using System.Text.RegularExpressions;
using DWMPHorde;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// Product-wide bans and identity checks. Behaviour is covered by the round-trip, policy and
/// patch-rule tests; this file only holds rules that apply to the whole source tree, so a rename
/// or a moved line never breaks it.
/// </summary>
public class ProductInvariantTests
{
    private static IEnumerable<string> ModSources()
        => Directory.EnumerateFiles(TestPaths.ModDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    /// <summary>Lines of code only (comments and doc comments dropped).</summary>
    private static IEnumerable<(string File, string Line)> CodeLines()
    {
        foreach (string f in ModSources())
        {
            foreach (string raw in File.ReadLines(f))
            {
                string t = raw.TrimStart();
                if (t.StartsWith("//") || t.StartsWith("*") || t.StartsWith("/*"))
                    continue;
                yield return (Path.GetRelativePath(TestPaths.ModDir, f), t);
            }
        }
    }

    [Fact]
    public void PluginInfo_IsYokWarePathB()
    {
        Assert.Equal("com.yokware.branch", PluginInfo.Guid);
        Assert.Equal("YokWare Branch", PluginInfo.Name);
        Assert.Contains("Horde", PluginInfo.Description);
    }

    [Fact]
    public void ShippedMod_HasNoYokyyActionEventCombatPath()
    {
        var hits = ModSources()
            .Where(f =>
            {
                string text = File.ReadAllText(f);
                return text.Contains("ActionEventPacket") || Regex.IsMatch(text, @"ActionName\s*=\s*\$?""pvp:");
            })
            .Select(f => Path.GetRelativePath(TestPaths.ModDir, f))
            .ToList();
        Assert.True(hits.Count == 0, "Yokyy ActionEvent combat remnants in shipped mod: " + string.Join(", ", hits));
    }

    [Fact]
    public void YokyyCore_RemovedFromShipTree_PathBEntryOnly()
    {
        Assert.False(Directory.Exists(Path.Combine(TestPaths.RepoRoot, "archive", "yokyy-merge-0.9")),
            "Frozen Path A tree must stay out of the public ship path.");
        Assert.False(File.Exists(Path.Combine(TestPaths.ModDir, "ModMain.cs")),
            "Yokyy ModMain.cs must not be the shipped entry under DarkwoodMP.Mod");
    }

    [Fact]
    public void ApplyGuards_AreClasses_NotStructs()
    {
        // `using (new SomeGuard())` on a struct with a parameterless ctor compiles to initobj on
        // older compilers/runtimes: the ctor never runs and the guard never engages.
        var hits = CodeLines()
            .Where(l => Regex.IsMatch(l.Line, @"\bstruct\s+\w*Guard\b") && l.Line.Contains("IDisposable"))
            .Select(l => l.File + ": " + l.Line)
            .ToList();
        Assert.True(hits.Count == 0, "disposable guard structs:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void HotPath_NoAllocatingOverlapSphere()
    {
        var hits = CodeLines()
            .Where(l => l.Line.Contains("Physics.OverlapSphere("))
            .Select(l => l.File + ": " + l.Line)
            .ToList();
        Assert.True(hits.Count == 0, "Allocating Physics.OverlapSphere still present (use NonAlloc):\n" + string.Join("\n", hits));
    }

    [Fact]
    public void CharacterTracker_HasNoAllocatingGetAll()
    {
        var hits = CodeLines()
            .Where(l => l.Line.Contains("CharacterTracker.GetAll(") || l.Line.Contains("public static Character[] GetAll()"))
            .Select(l => l.File + ": " + l.Line)
            .ToList();
        Assert.True(hits.Count == 0, "allocating CharacterTracker.GetAll:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void Domains_DoNotExtendLanNetworkManager()
    {
        // Domain code talks to the network manager; it does not add partial slices to it.
        var hits = Directory.GetFiles(Path.Combine(TestPaths.ModDir, "Domains"), "*.cs", SearchOption.AllDirectories)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\bpartial\s+class\s+LanNetworkManager\b"))
            .Select(f => Path.GetRelativePath(TestPaths.ModDir, f))
            .ToList();
        Assert.True(hits.Count == 0, "LanNetworkManager partials under Domains/: " + string.Join(", ", hits));
    }
}

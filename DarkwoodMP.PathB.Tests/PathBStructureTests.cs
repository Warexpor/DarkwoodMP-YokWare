using System.Text.RegularExpressions;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// Structural gates for Path B: shipped mod is Horde-based, not Yokyy ActionEvent combat.
/// Reads real source under the repo (shipped load path).
/// </summary>
public class PathBStructureTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "DarkwoodMP.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("Could not locate repo root (DarkwoodMP.sln).");
        }
    }

    private static string ModDir => Path.Combine(RepoRoot, "DarkwoodMP.Mod");

    [Fact]
    public void PluginInfo_IsYokWarePathB_WithHordeProtocol()
    {
        var text = File.ReadAllText(Path.Combine(ModDir, "PluginInfo.cs"));
        Assert.Contains("com.yokware.branch", text);
        Assert.Contains("YokWare Branch", text);
        Assert.Contains("0.9.1", text);
        Assert.Contains("ProtocolVersion = 19", text);
        Assert.Contains("Horde", text);
    }

    [Fact]
    public void ShippedMod_HasHordeHostClientCombatAuthority()
    {
        var required = new[]
        {
            "Patches/ClientHitscanDamageRedirectPatch.cs",
            "Patches/ClientCombatPatches.cs",
            "Patches/HostCombatPatches.cs",
            "Patches/ClientAIDisablePatches.cs",
            "Networking/EntityStateBroadcastService.cs",
            "Networking/ClientEntityInterpolationService.cs",
            "Networking/NetworkRole.cs",
            "Patches/AudioSuppressionPatch.cs",
        };
        foreach (var rel in required)
        {
            var path = Path.Combine(ModDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "Missing Horde authority surface: " + rel);
        }

        var redirect = File.ReadAllText(Path.Combine(ModDir, "Patches", "ClientHitscanDamageRedirectPatch.cs"));
        Assert.Contains("NetworkRole.Client", redirect);
        Assert.Contains("PlayerAttack", redirect);
        Assert.Contains("return false", redirect); // block local getHit on client
    }

    [Fact]
    public void ShippedMod_HasNoYokyyActionEventCombatPath()
    {
        var csFiles = Directory.GetFiles(ModDir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(csFiles);
        var hits = new List<string>();
        foreach (var f in csFiles)
        {
            // Skip archived docs under mod if any
            var text = File.ReadAllText(f);
            if (text.Contains("ActionEventPacket") || Regex.IsMatch(text, @"ActionName\s*=\s*\$?""pvp:"))
                hits.Add(Path.GetRelativePath(ModDir, f));
        }
        Assert.True(hits.Count == 0,
            "Yokyy ActionEvent combat remnants in shipped mod: " + string.Join(", ", hits));
    }

    [Fact]
    public void InventoryDoc_CoversMustKeepProductItems()
    {
        var inv = Path.Combine(RepoRoot, "docs", "PATH_B_FEATURE_INVENTORY.md");
        Assert.True(File.Exists(inv), "Missing docs/PATH_B_FEATURE_INVENTORY.md");
        var text = File.ReadAllText(inv);
        foreach (var must in new[]
                 {
                     "Dedicated server",
                     "Ironbark",
                     "MelonLoader",
                     "Chat",
                     "SyncCheck",
                     "ItemState",
                     "ClientStateBackup",
                     "GPLv3",
                     "present-in-horde",
                     "deferred",
                 })
        {
            Assert.Contains(must, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void YokyyCore_IsArchived_NotDefaultLoadPath()
    {
        var archReadme = Path.Combine(RepoRoot, "archive", "yokyy-merge-0.9", "README.md");
        Assert.True(File.Exists(archReadme));
        var text = File.ReadAllText(archReadme);
        Assert.Contains("Do not load", text, StringComparison.OrdinalIgnoreCase);

        // Shipped entry is Horde BaseUnityPlugin, not Yokyy ModMain-only tree as sole product
        var entry = Path.Combine(ModDir, "DWMPEntry.cs");
        Assert.True(File.Exists(entry));
        Assert.Contains("BepInPlugin", File.ReadAllText(entry));
        Assert.False(File.Exists(Path.Combine(ModDir, "ModMain.cs")),
            "Yokyy ModMain.cs must not be the shipped entry under DarkwoodMP.Mod");
    }
}

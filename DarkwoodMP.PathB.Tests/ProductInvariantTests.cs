using System.Text.RegularExpressions;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// Thin product gates for Path B. Prefer real unit tests for behavior;
/// these only lock high-signal ship invariants that must not quietly regress.
/// </summary>
public class ProductInvariantTests
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
    public void PluginInfo_IsYokWarePathB_Protocol24()
    {
        var text = File.ReadAllText(Path.Combine(ModDir, "PluginInfo.cs"));
        Assert.Contains("com.yokware.branch", text);
        Assert.Contains("YokWare Branch", text);
        Assert.Contains("ProtocolVersion = 24", text);
        Assert.Contains("Horde", text);

        var versionMatch = Regex.Match(text, @"Version\s*=\s*""(0\.7\.[^""]+)""");
        Assert.True(versionMatch.Success, "PluginInfo.Version must be 0.7.x");
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
        Assert.Contains("return false", redirect);
    }

    [Fact]
    public void ShippedMod_HasNoYokyyActionEventCombatPath()
    {
        var csFiles = Directory.GetFiles(ModDir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(csFiles);
        var hits = new List<string>();
        foreach (var f in csFiles)
        {
            var text = File.ReadAllText(f);
            if (text.Contains("ActionEventPacket") || Regex.IsMatch(text, @"ActionName\s*=\s*\$?""pvp:"))
                hits.Add(Path.GetRelativePath(ModDir, f));
        }
        Assert.True(hits.Count == 0,
            "Yokyy ActionEvent combat remnants in shipped mod: " + string.Join(", ", hits));
    }

    [Fact]
    public void YokyyCore_RemovedFromShipTree_PathBEntryOnly()
    {
        var archiveRoot = Path.Combine(RepoRoot, "archive", "yokyy-merge-0.9");
        Assert.False(Directory.Exists(archiveRoot),
            "Frozen Path A tree must stay out of the public ship path.");

        var entry = Path.Combine(ModDir, "DWMPEntry.cs");
        Assert.True(File.Exists(entry));
        Assert.Contains("BepInPlugin", File.ReadAllText(entry));
        Assert.False(File.Exists(Path.Combine(ModDir, "ModMain.cs")),
            "Yokyy ModMain.cs must not be the shipped entry under DarkwoodMP.Mod");
    }

    [Fact]
    public void NetworkApplyGuard_IsSealedClass_NotStruct()
    {
        // struct + `using (new NetworkApplyGuard())` compiled to initobj (ctor never ran).
        var guard = File.ReadAllText(Path.Combine(ModDir, "Networking", "NetworkApplyGuard.cs"));
        Assert.Contains("sealed class NetworkApplyGuard", guard);
        Assert.DoesNotContain("struct NetworkApplyGuard", guard);
    }

    [Fact]
    public void LanForward_UsesPutRaw_NotLengthPrefixedRewrap()
    {
        var lan = File.ReadAllText(Path.Combine(ModDir, "Networking", "LanNetworkManager.cs"));
        Assert.Contains("PutRaw(payload)", lan);
        Assert.DoesNotContain("w => w.Put(payload)", lan);
    }
}

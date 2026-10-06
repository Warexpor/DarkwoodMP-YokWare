using System.Text.RegularExpressions;
using DWMPHorde;
using DWMPHorde.Networking;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// One product version and one wire protocol, everywhere a human or a script reads them:
/// PluginInfo (the source of truth), csproj, AssemblyInfo, README and the CHANGELOG head.
/// </summary>
public class ReleaseConsistencyTests
{
    private static string Csproj => TestPaths.Read("DarkwoodMP.Mod", "DarkwoodMP.Mod.csproj");
    private static string AssemblyInfo => TestPaths.Read("DarkwoodMP.Mod", "Properties", "AssemblyInfo.cs");
    private static string Readme => TestPaths.Read("README.md");
    private static string Changelog => TestPaths.Read("CHANGELOG.md");

    [Fact]
    public void PluginInfo_IsWellFormed()
    {
        Assert.Matches(@"^0\.8\.\d+$", PluginInfo.Version);
        Assert.True(PluginInfo.ProtocolVersion > 0);
        // DisplayVersion already carries the product name; callers must not prefix it again.
        Assert.Equal("YokWare Branch " + PluginInfo.Version + " / Path B", PluginInfo.DisplayVersion);
    }

    [Fact]
    public void Csproj_DerivesVersionFromPluginInfo()
    {
        // The csproj must read the version from PluginInfo.cs, not carry its own copy.
        string version = Regex.Match(Csproj, @"<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.Contains("PluginInfo.cs", version);
        Assert.DoesNotMatch(@"\d+\.\d+\.\d+", version);
        Assert.Equal("$(Version)-path-b",
            Regex.Match(Csproj, @"<InformationalVersion>([^<]+)</InformationalVersion>").Groups[1].Value);

        // The regex the csproj uses must actually find the version in PluginInfo.cs.
        string pattern = Regex.Match(version, @"'(const string Version = [^']+)'").Groups[1].Value;
        string pluginInfo = TestPaths.Read("DarkwoodMP.Mod", "Bootstrap", "PluginInfo.cs");
        Assert.Equal(PluginInfo.Version, Regex.Match(pluginInfo, pattern).Groups[1].Value);
    }

    [Fact]
    public void AssemblyInfo_DerivesVersionFromPluginInfo()
    {
        Assert.Contains(@"AssemblyVersion(DWMPHorde.PluginInfo.Version + "".0"")", AssemblyInfo);
        Assert.Contains(@"AssemblyFileVersion(DWMPHorde.PluginInfo.Version + "".0"")", AssemblyInfo);
    }

    [Fact]
    public void Docs_DoNotRestateTheProductVersion()
    {
        // Only README (checked above) and CHANGELOG carry the number; other docs point to README.
        string[][] docs =
        {
            new[] { "CONTRIBUTORS.md" },
            new[] { "DarkwoodMP.Mod", "docs", "HOW_COOP_WORKS.md" },
            new[] { "DarkwoodMP.Mod", "docs", "CONFIG.md" },
            new[] { "DarkwoodMP.Mod", "docs", "COOP_COVERAGE.md" },
            new[] { "DarkwoodMP.Mod", "docs", "PLAYTEST.md" },
        };
        foreach (string[] doc in docs)
            Assert.DoesNotContain(PluginInfo.Version, TestPaths.Read(doc));
    }

    [Fact]
    public void Readme_VersionProtocolAndHighestMessageIdMatch()
    {
        string readme = Readme;

        Assert.Equal(PluginInfo.Version,
            Regex.Match(readme, @"\|\s*Product\s*\|\s*YokWare Branch \*\*([^*]+)\*\*").Groups[1].Value);
        Assert.Equal(PluginInfo.ProtocolVersion.ToString(),
            Regex.Match(readme, @"\|\s*Wire\s*\|\s*Horde protocol \*\*(\d+)\*\*").Groups[1].Value);

        var ship = Regex.Match(readme, @"Current ship: \*\*([^*]+)\*\*, protocol \*\*(\d+)\*\*");
        Assert.True(ship.Success, "README 'Current ship' line missing");
        Assert.Equal(PluginInfo.Version, ship.Groups[1].Value);
        Assert.Equal(PluginInfo.ProtocolVersion.ToString(), ship.Groups[2].Value);

        var highest = Regex.Match(readme, @"highest assigned message ID is (\d+) \(`(\w+)`\)");
        Assert.True(highest.Success, "README 'highest assigned message ID' sentence missing");
        Assert.Equal((int)(byte)NetMessageType._Highest, int.Parse(highest.Groups[1].Value));
        Assert.True(Enum.TryParse(highest.Groups[2].Value, out NetMessageType named),
            "README names a message that is not in NetMessageType: " + highest.Groups[2].Value);
        Assert.Equal((byte)NetMessageType._Highest, (byte)named);
    }

    [Fact]
    public void Changelog_TopEntryAndVersioningBlurbMatchPluginInfo()
    {
        string log = Changelog;

        var top = Regex.Match(log, @"^## (0\.8\.\d+)\b", RegexOptions.Multiline);
        Assert.True(top.Success, "CHANGELOG has no '## 0.8.x' entry");
        Assert.Equal(PluginInfo.Version, top.Groups[1].Value);

        var blurb = Regex.Match(log,
            @"display version are\s+\*\*([^*]+)\*\*\.\s+The current Horde wire protocol is \*\*(\d+)\*\*",
            RegexOptions.Singleline);
        Assert.True(blurb.Success, "CHANGELOG 'Versioning' blurb not found");
        Assert.Equal(PluginInfo.Version, blurb.Groups[1].Value);
        Assert.Equal(PluginInfo.ProtocolVersion.ToString(), blurb.Groups[2].Value);
    }

    [Fact]
    public void ScriptsDoNotHardCodeTheProductVersion()
    {
        foreach (string script in new[] { "pack-release.sh", "pack-release.ps1", "check-dualbox-perf.sh" })
        {
            string text = TestPaths.Read("scripts", script);
            Assert.DoesNotMatch(@"\b0\.[789]\.\d+\b", text);
        }
    }
}

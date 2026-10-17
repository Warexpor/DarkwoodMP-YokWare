using System.Text.RegularExpressions;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// A catch that carries on past any exception must leave a trace: it calls
/// <c>ModLog.Swallowed(ex)</c> or logs, rethrows, or catches a specific exception type.
/// Logging, Bootstrap and Config run before the log is up; files the tests compile have no
/// ModLog and are left out too.
/// </summary>
public class CatchRulesTests
{
    private static readonly Regex Catch = new Regex(
        @"\bcatch\s*(?:\(\s*(?<type>[\w.]+)(?:\s+\w+)?\s*\))?\s*\{", RegexOptions.Compiled);

    private static readonly Regex Traces = new Regex(
        @"Log|Warn|Error|Debug\.|Out\(|throw|Swallowed", RegexOptions.Compiled);

    private static HashSet<string> TestCompiledFiles()
    {
        string proj = TestPaths.Read("DarkwoodMP.PathB.Tests", "DarkwoodMP.PathB.Tests.csproj");
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(proj, @"Compile Include=""\.\.\\DarkwoodMP\.Mod\\([^""*]+\.cs)"""))
            set.Add(m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar));
        return set;
    }

    internal static List<string> FindSilentCatches()
    {
        HashSet<string> testCompiled = TestCompiledFiles();
        var offenders = new List<string>();
        foreach (string file in Directory.GetFiles(TestPaths.ModDir, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(TestPaths.ModDir, file);
            string[] parts = rel.Split(Path.DirectorySeparatorChar);
            if (parts.Any(p => p is "obj" or "bin" or "ThirdParty")
                || parts[0] is "Logging" or "Bootstrap" or "Config"
                || testCompiled.Contains(rel))
                continue;
            string src = File.ReadAllText(file);
            foreach (Match m in Catch.Matches(src))
            {
                int lineStart = src.LastIndexOf('\n', m.Index) + 1;
                if (src.Substring(lineStart, m.Index - lineStart).TrimStart().StartsWith("//", StringComparison.Ordinal))
                    continue;
                string type = m.Groups["type"].Value;
                if (type.Length > 0 && type != "Exception" && type != "System.Exception")
                    continue;
                int open = m.Index + m.Length - 1;
                int depth = 0, i = open;
                for (; i < src.Length; i++)
                {
                    if (src[i] == '{') depth++;
                    else if (src[i] == '}' && --depth == 0) break;
                }
                string body = src.Substring(open + 1, Math.Max(0, i - open - 1));
                body = Regex.Replace(body, @"/\*.*?\*/", "", RegexOptions.Singleline);
                body = Regex.Replace(body, @"//.*", "");
                if (Traces.IsMatch(body))
                    continue;
                int line = src.Take(m.Index).Count(c => c == '\n') + 1;
                offenders.Add(rel + ":" + line);
            }
        }
        return offenders;
    }

    [Fact]
    public void CatchAll_LeavesATrace()
    {
        List<string> offenders = FindSilentCatches();
        Assert.True(offenders.Count == 0,
            "catch blocks that swallow any exception without a trace (add ModLog.Swallowed(ex), log, "
            + "or catch a specific type):\n" + string.Join("\n", offenders));
    }
}

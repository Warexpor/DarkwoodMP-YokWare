using System.Text.RegularExpressions;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// Rules every Harmony patch class must follow, checked over the whole mod instead of per file.
/// </summary>
public class HarmonyPatchRulesTests
{
    private sealed record PatchClass(string File, string Name, string Body);

    private static IEnumerable<PatchClass> PatchClasses()
    {
        foreach (string file in Directory.EnumerateFiles(TestPaths.ModDir, "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            if (!text.Contains("[HarmonyPatch"))
                continue;
            foreach (Match m in Regex.Matches(text, @"\bclass\s+(\w+)[^{]*\{"))
            {
                int open = m.Index + m.Length - 1;
                int close = MatchBrace(text, open);
                if (close < 0)
                    continue;
                yield return new PatchClass(Path.GetRelativePath(TestPaths.ModDir, file), m.Groups[1].Value,
                    text.Substring(open, close - open + 1));
            }
        }
    }

    /// <summary>Bodies of the methods named <paramref name="name"/> or tagged [Harmony{name}].</summary>
    private static List<string> MethodBodies(string classBody, string name)
    {
        var bodies = new List<string>();
        var pattern = new Regex(@"(?:\[Harmony" + name + @"\][^{;]*?\b\w+|\b" + name + @")\s*\([^)]*\)\s*(\{|=>)");
        foreach (Match m in pattern.Matches(classBody))
        {
            int start = m.Groups[1].Index;
            if (m.Groups[1].Value == "=>")
            {
                int end = classBody.IndexOf(';', start);
                if (end > start) bodies.Add(classBody.Substring(start, end - start));
                continue;
            }
            int close = MatchBrace(classBody, start);
            if (close > start) bodies.Add(classBody.Substring(start, close - start + 1));
        }
        return bodies;
    }

    private static int MatchBrace(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    [Fact]
    public void ScanFindsThePatchLayer()
    {
        Assert.True(PatchClasses().Count() >= 100, "patch class scan found too little; did the layout change?");
    }

    [Fact]
    public void FlagSetInPrefixAndClearedInPostfix_IsAlsoClearedByAFinalizer()
    {
        // Harmony skips the Postfix when the original throws; a flag only the Postfix clears then
        // stays set for the rest of the session. The Finalizer always runs.
        var offenders = new List<string>();
        int checkedPairs = 0;
        foreach (PatchClass pc in PatchClasses())
        {
            string prefix = string.Join("\n", MethodBodies(pc.Body, "Prefix"));
            string postfix = string.Join("\n", MethodBodies(pc.Body, "Postfix"));
            if (prefix.Length == 0 || postfix.Length == 0)
                continue;
            var set = new HashSet<string>();
            foreach (Match m in Regex.Matches(prefix, @"([\w.]+)\s*=\s*true\s*;|([\w.]+)\s*\+\+"))
                set.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            var clearedOnlyInPostfix = new List<string>();
            foreach (Match m in Regex.Matches(postfix, @"([\w.]+)\s*=\s*false\s*;|([\w.]+)\s*--"))
            {
                string id = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (set.Contains(id))
                    clearedOnlyInPostfix.Add(id);
            }
            if (clearedOnlyInPostfix.Count == 0)
                continue;
            string finalizer = string.Join("\n", MethodBodies(pc.Body, "Finalizer"));
            foreach (string id in clearedOnlyInPostfix.Distinct())
            {
                checkedPairs++;
                string last = id.Substring(id.LastIndexOf('.') + 1);
                if (!finalizer.Contains(last))
                    offenders.Add(pc.File + " " + pc.Name + ": " + id);
            }
        }
        // The scan must actually see Prefix/Postfix flag pairs, or the rule checks nothing.
        Assert.True(checkedPairs >= 5, "only " + checkedPairs + " prefix/postfix flag pairs found");
        Assert.True(offenders.Count == 0,
            "flags set in Prefix and cleared only in Postfix (add a Finalizer):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void CoroutinePrefixThatSkipsTheOriginal_ReturnsAnEnumerator()
    {
        // `return false` on an IEnumerator target without assigning __result hands StartCoroutine
        // a null enumerator.
        var offenders = new List<string>();
        foreach (PatchClass pc in PatchClasses())
        {
            foreach (Match m in Regex.Matches(pc.Body, @"\bPrefix\s*\(([^)]*)\)\s*\{"))
            {
                if (!Regex.IsMatch(m.Groups[1].Value, @"ref\s+(System\.Collections\.)?IEnumerator\s+__result"))
                    continue;
                int open = m.Index + m.Length - 1;
                string body = pc.Body.Substring(open, MatchBrace(pc.Body, open) - open + 1);
                if (body.Contains("return false") && !body.Contains("__result ="))
                    offenders.Add(pc.File + " " + pc.Name);
            }
        }
        Assert.True(offenders.Count == 0,
            "coroutine prefixes returning false without __result:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void FinalizersDoNotSwallowExceptionsBroadly()
    {
        // A Finalizer that returns null swallows the original's exception. Only allowed with a
        // narrow, typed condition in the same method.
        var offenders = new List<string>();
        foreach (PatchClass pc in PatchClasses())
        {
            foreach (string body in MethodBodies(pc.Body, "Finalizer"))
            {
                if (!body.Contains("return null"))
                    continue;
                if (!Regex.IsMatch(body, @"is\s+\w*Exception|as\s+\w*Exception|GetType\(\)\s*==\s*typeof\(\w*Exception\)"))
                    offenders.Add(pc.File + " " + pc.Name);
            }
        }
        Assert.True(offenders.Count == 0,
            "finalizers swallowing every exception:\n" + string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("DialogueWindow", "displayNextBoard")]
    public void ConsolidatedTargets_HaveExactlyOnePatchClass(string type, string method)
    {
        // These hooks were merged into one patch class so their checks run in one fixed order;
        // a second independent patch on the same method would reintroduce order-dependent state.
        int hooks = 0;
        var pattern = new Regex(@"HarmonyPatch\(typeof\(" + type + @"\),\s*""" + method + @"""");
        foreach (string file in Directory.EnumerateFiles(TestPaths.ModDir, "*.cs", SearchOption.AllDirectories))
            hooks += pattern.Matches(File.ReadAllText(file)).Count;
        Assert.Equal(1, hooks);
    }
}

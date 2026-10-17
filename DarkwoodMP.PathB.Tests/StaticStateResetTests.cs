using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// Guard against session state leaking across reconnects. Every mutable static in patch and
/// networking code must either be cleared by a Reset/Clear/OnDisconnected/Stop method in the
/// same file (registered with NetworkResetRegistry or called from StopNetwork), or carry a
/// trailing <c>// process-scoped</c> marker on its declaration line (immutable reflection
/// caches, scratch buffers, call-scoped flags unwound by a Finalizer). State cleared from
/// another file (partial classes) names that method: <c>// reset-in: MethodName</c>, and the
/// named reset method must exist and touch the field.
/// </summary>
public class StaticStateResetTests
{
    private const string Marker = "// process-scoped";
    private static readonly Regex ResetInMarker = new Regex(@"// reset-in: (?:[\w.]+\.)?(\w+)", RegexOptions.Compiled);

    // static field (not const/event/property/method/class); group 1 = readonly, 2 = type, 3 = name.
    private static readonly Regex FieldDecl = new Regex(
        @"^\s*(?:\[[^\]]*\]\s*)*(?:(?:private|internal|public|protected)\s+)*static\s+(?!class\b)(?:volatile\s+)?(readonly\s+)?([\w<>\[\],\.\? ]+?)\s+(\w+)\s*(?:=(?!>)|;)",
        RegexOptions.Compiled);

    private static readonly Regex CollectionType = new Regex(
        @"^(?:System\.Collections\.Generic\.)?(?:Dictionary|HashSet|List|Queue)\b", RegexOptions.Compiled);

    // Method declaration whose name contains Reset/Clear/OnDisconnected/Stop; body is { … } or => …;
    private static readonly Regex ResetMethod = new Regex(
        @"\b[\w<>\[\],]+\s+(\w*(?:Reset|Clear|OnDisconnected|Stop)\w*)\s*\((?:[^()]|\([^()]*\))*\)\s*(\{|=>)",
        RegexOptions.Compiled);

    private static IEnumerable<string> ScannedFiles()
    {
        string mod = TestPaths.ModDir;
        char sep = Path.DirectorySeparatorChar;
        foreach (string f in Directory.GetFiles(mod, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(mod, f);
            string[] parts = rel.Split(sep);
            if (parts.Contains("obj") || parts.Contains("bin"))
                continue;
            // Every runtime folder that can hold session state. Bootstrap/Config/Logging/Harmony
            // hold process-wide setup only.
            if (parts[0] is "Networking" or "Domains" or "Core" or "Audio" or "UI" or "Items" or "Patches")
                yield return f;
        }
    }

    private static string MethodBody(string src, Match m)
    {
        int i = m.Index + m.Length;
        if (m.Groups[2].Value == "=>")
        {
            int end = src.IndexOf(';', i);
            return end > i ? src.Substring(i, end - i) : "";
        }
        int depth = 1;
        int start = i;
        while (depth > 0 && i < src.Length)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}') depth--;
            i++;
        }
        return src.Substring(start, i - start);
    }

    private static string ResetBodies(string src)
    {
        var sb = new StringBuilder();
        foreach (Match m in ResetMethod.Matches(src))
            sb.Append(MethodBody(src, m)).Append('\n');
        return sb.ToString();
    }

    private static string Normalize(string name) => name.TrimStart('_').ToLowerInvariant();

    /// <summary>Reset-named method name → lower-cased body text, across the whole mod.</summary>
    private static Dictionary<string, string> AllResetBodies()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string f in Directory.GetFiles(TestPaths.ModDir, "*.cs", SearchOption.AllDirectories))
        {
            string src = File.ReadAllText(f);
            foreach (Match m in ResetMethod.Matches(src))
            {
                map.TryGetValue(m.Groups[1].Value, out string prev);
                map[m.Groups[1].Value] = (prev ?? "") + "\n" + MethodBody(src, m).ToLowerInvariant();
            }
        }
        return map;
    }

    internal static List<string> FindUnresetStatics()
    {
        var offenders = new List<string>();
        Dictionary<string, string> resetBodies = AllResetBodies();
        foreach (string file in ScannedFiles())
        {
            string src = File.ReadAllText(file);
            string bodies = ResetBodies(src);
            string rel = Path.GetRelativePath(TestPaths.ModDir, file);
            foreach (string line in src.Split('\n'))
            {
                Match m = FieldDecl.Match(line);
                if (!m.Success)
                    continue;
                string beforeAssign = line.Split('=')[0];
                if (beforeAssign.Contains('(') || line.Contains(" const ") || line.Contains(" event "))
                    continue;
                bool isReadonly = m.Groups[1].Success && m.Groups[1].Length > 0;
                string type = m.Groups[2].Value.Trim();
                string name = m.Groups[3].Value;
                if (isReadonly && !CollectionType.IsMatch(type))
                    continue;
                if (line.Contains(Marker))
                    continue;
                Match resetIn = ResetInMarker.Match(line);
                if (resetIn.Success)
                {
                    string method = resetIn.Groups[1].Value;
                    if (resetBodies.TryGetValue(method, out string text) && text.Contains(Normalize(name)))
                        continue;
                    offenders.Add(rel + ": " + name + " (reset-in " + method + " missing or does not touch it)");
                    continue;
                }
                if (Regex.IsMatch(bodies, @"\b" + Regex.Escape(name) + @"\b"))
                    continue;
                offenders.Add(rel + ": " + name);
            }
        }
        return offenders;
    }

    [Fact]
    public void MutableStatics_AreResetPerSession_OrMarkedProcessScoped()
    {
        List<string> offenders = FindUnresetStatics();
        Assert.True(offenders.Count == 0,
            "Static state that is neither cleared in a Reset/Clear/OnDisconnected/Stop method of the same file "
            + "nor marked '" + Marker + "' / '// reset-in: Method':\n" + string.Join("\n", offenders));
    }

    private static readonly Regex StaticResetDecl = new Regex(
        @"\bstatic\s+[\w<>\[\],\.]+\s+(\w*(?:Reset|Clear|OnDisconnected|Stop)\w*)\s*\((?:[^()]|\([^()]*\))*\)\s*(?:\{|=>)",
        RegexOptions.Compiled);

    private static readonly Regex ClassDecl = new Regex(@"\bclass\s+(\w+)", RegexOptions.Compiled);

    /// <summary>Innermost class whose body holds <paramref name="index"/>.</summary>
    private static string OwningClass(string src, int index)
    {
        string owner = null;
        int ownerStart = -1;
        foreach (Match c in ClassDecl.Matches(src))
        {
            int open = src.IndexOf('{', c.Index);
            if (open < 0 || open > index || c.Index < ownerStart)
                continue;
            int depth = 1, i = open + 1;
            while (depth > 0 && i < src.Length)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}') depth--;
                i++;
            }
            if (index < i)
            {
                owner = c.Groups[1].Value;
                ownerStart = c.Index;
            }
        }
        return owner;
    }

    /// <summary>
    /// Static reset methods that nothing calls or registers. A field cleared only there passes
    /// the scan above but still leaks into the next session.
    /// </summary>
    internal static List<string> FindUncalledStaticResets()
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string f in Directory.GetFiles(TestPaths.ModDir, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(TestPaths.ModDir, f);
            if (rel.Split(Path.DirectorySeparatorChar).Any(p => p is "obj" or "bin" or "ThirdParty"))
                continue;
            all[f] = File.ReadAllText(f);
        }
        string everything = string.Join("\n", all.Values);
        var offenders = new List<string>();
        foreach (string file in ScannedFiles())
        {
            string src = all[file];
            foreach (Match m in StaticResetDecl.Matches(src))
            {
                string name = m.Groups[1].Value;
                string cls = OwningClass(src, m.Index);
                if (cls == null)
                    continue;
                if (Regex.IsMatch(everything, @"\b" + cls + @"\." + name + @"\b"))
                    continue;
                // A bare call (or method group) from the class itself, partial files included.
                var classDecl = new Regex(@"\bclass\s+" + cls + @"\b");
                string same = string.Join("\n", all.Values.Where(s => classDecl.IsMatch(s)));
                int uses = Regex.Matches(same, @"\b" + name + @"\b").Count;
                int decls = Regex.Matches(same, @"\s" + name + @"\s*\((?:[^()]|\([^()]*\))*\)\s*(?:\{|=>)").Count;
                if (uses > decls)
                    continue;
                offenders.Add(Path.GetRelativePath(TestPaths.ModDir, file) + ": " + cls + "." + name);
            }
        }
        return offenders;
    }

    [Fact]
    public void StaticResets_AreCalledOrRegistered()
    {
        List<string> offenders = FindUncalledStaticResets();
        Assert.True(offenders.Count == 0,
            "Static reset methods nothing calls (register them with NetworkResetRegistry, call them from a "
            + "registered reset, or delete them):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Scanner_FindsKnownPatterns()
    {
        // Sanity: the scan must actually see patch/networking statics (guards a broken regex/path).
        Assert.Contains(ScannedFiles(), f => f.EndsWith("FastProjectileAwakePatch.cs", StringComparison.Ordinal));
        string sample = "        private static readonly Dictionary<int, float> _x = new Dictionary<int, float>();";
        Assert.Matches(FieldDecl, sample);
        Assert.DoesNotMatch(FieldDecl, "        internal static int Pass => _pass;");
        Assert.DoesNotMatch(FieldDecl, "    internal static class Foo");
        string body = ResetBodies("void Reset() { _a.Clear(); }\nstatic void ClearAll() => _b = 0;\nint Other() { _c++; }");
        Assert.Contains("_a", body);
        Assert.Contains("_b", body);
        Assert.DoesNotContain("_c", body);
    }
}

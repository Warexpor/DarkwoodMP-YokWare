namespace DarkwoodMP.PathB.Tests;

/// <summary>Locates the repo from the test binary so tests can read shipped sources and docs.</summary>
internal static class TestPaths
{
    public static string RepoRoot
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

    public static string ModDir => Path.Combine(RepoRoot, "DarkwoodMP.Mod");

    public static string Read(params string[] relativeFromRepoRoot) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot }.Concat(relativeFromRepoRoot).ToArray()));
}

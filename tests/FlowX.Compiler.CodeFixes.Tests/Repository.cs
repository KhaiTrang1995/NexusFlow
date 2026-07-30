namespace FlowX.Compiler.CodeFixes.Tests;

/// <summary>
/// Locates repository files from the test output directory.
/// </summary>
/// <remarks>
/// Two tests here read files the build does not copy — the reference sample's source and
/// the diagnostic documentation pages — because a copy of either would be a copy that
/// stops matching the original without anything failing.
/// </remarks>
internal static class Repository
{
    /// <summary>The absolute path of a directory given relative to the repository root.</summary>
    public static string Directory(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);

            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate '{relativePath}' from {AppContext.BaseDirectory}.");
    }
}

namespace FlowX.ColdStart.Bench;

/// <summary>
/// Finds the repository, so the rig's paths mean the same thing wherever it is started from.
/// </summary>
/// <remarks>
/// Every path this rig uses — the sample project, the publish directory, the results file —
/// is relative to the repository root, and <c>dotnet run</c> leaves the working directory
/// wherever the shell was. Resolving it once here is what stops a run from a subdirectory
/// publishing nothing and reporting a missing sample.
/// </remarks>
internal static class Repository
{
    /// <summary>Makes the repository root the working directory.</summary>
    /// <exception cref="InvalidOperationException">No <c>FlowX.slnx</c> above either starting point.</exception>
    public static void SetCurrentDirectory()
    {
        var root = Find(Environment.CurrentDirectory) ?? Find(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException(
                "Could not find FlowX.slnx above the working directory or the rig's own " +
                $"location ({AppContext.BaseDirectory}). Run it from inside the repository.");

        Directory.SetCurrentDirectory(root.FullName);
    }

    private static DirectoryInfo? Find(string from)
    {
        var directory = new DirectoryInfo(from);

        while (directory is not null)
        {
            if (directory.GetFiles("FlowX.slnx").Length > 0)
            {
                return directory;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

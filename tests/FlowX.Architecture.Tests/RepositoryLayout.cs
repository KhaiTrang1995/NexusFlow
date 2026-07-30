using System.Xml.Linq;

namespace FlowX.Architecture.Tests;

/// <summary>
/// Locates the repository on disk so fitness functions can inspect project files
/// rather than only compiled output. Reading the .csproj is what lets
/// <c>AbstractionsHasNoDependencies</c> fail at the moment a reference is added,
/// instead of after it has already changed the package graph.
/// </summary>
internal static class RepositoryLayout
{
    private static readonly Lazy<DirectoryInfo> RootLazy = new(FindRoot);

    /// <summary>The repository root, found by walking up to the directory holding FlowX.slnx.</summary>
    public static DirectoryInfo Root => RootLazy.Value;

    /// <summary>All project files under <c>src/</c>.</summary>
    public static IReadOnlyList<FileInfo> SourceProjects =>
        Root.GetDirectories("src").SelectMany(static d => d.GetFiles("*.csproj", SearchOption.AllDirectories))
            .OrderBy(static f => f.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>Reads a project's <c>PackageReference</c> includes.</summary>
    public static IReadOnlyList<string> PackageReferences(FileInfo project) =>
        ItemIncludes(project, "PackageReference");

    /// <summary>Reads a project's <c>ProjectReference</c> includes, normalised to file names.</summary>
    public static IReadOnlyList<string> ProjectReferences(FileInfo project) =>
        ItemIncludes(project, "ProjectReference")
            .Select(static include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
            .ToList();

    private static List<string> ItemIncludes(FileInfo project, string itemName)
    {
        var document = XDocument.Load(project.FullName);

        return document.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, itemName, StringComparison.Ordinal))
            .Select(static e => e.Attribute("Include")?.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToList();
    }

    private static DirectoryInfo FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.GetFiles("FlowX.slnx").Length > 0)
            {
                return directory;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root (no FlowX.slnx found walking up from {AppContext.BaseDirectory}).");
    }
}

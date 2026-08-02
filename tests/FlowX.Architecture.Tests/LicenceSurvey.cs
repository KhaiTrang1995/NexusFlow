using System.Text.Json;
using System.Xml.Linq;

namespace FlowX.Architecture.Tests;

/// <summary>
/// Reads what this repository depends on, and what each dependency says its licence is.
/// </summary>
/// <remarks>
/// <para>
/// Two readings, because they answer different questions and fail at different moments.
/// <em>Declared</em> is every <c>PackageReference</c> written in a project file or in
/// <c>Directory.Build.props</c>; it is available with nothing built, in every tree, including
/// the two that are not in <c>FlowX.slnx</c> and are never restored by an ordinary build.
/// <em>Resolved</em> is every package NuGet actually put in a project's graph, read out of
/// <c>obj/project.assets.json</c>; it is the real transitive closure — the thing constraint
/// C6's implication ("vets every transitive dependency") is about — and it exists only for
/// projects that have been restored.
/// </para>
/// <para>
/// <strong>Neither reading needs the network.</strong> That is the whole reason this gate is a
/// fitness function rather than a CI job calling a licence API: the resolved graph is on disk
/// after <c>dotnet restore</c>, and so is every package's <c>.nuspec</c> — under the folder the
/// assets file itself names in <c>packageFolders</c>. A licence lookup service would tell us
/// what nuget.org believes; the <c>.nuspec</c> is what the package asserts about itself, which
/// is the document a redistributor is actually bound by.
/// </para>
/// </remarks>
internal static class LicenceSurvey
{
    /// <summary>
    /// Every tree that can introduce a dependency into something this repository publishes,
    /// builds or hands to a reader as an example.
    /// </summary>
    /// <remarks>
    /// <c>tests/</c> and <c>scripts/</c> are here even though nothing in them ships. A copyleft
    /// package in a test project is not redistributed, but it is linked into a binary that a
    /// contributor runs and that CI publishes coverage from, and answering "does this repository
    /// depend on GPL code" with "only in the parts we do not ship" is a conversation with a
    /// legal reviewer that this gate exists to avoid having. <c>templates/</c> is here because
    /// its <c>content/</c> is a .csproj that is published and then built by strangers.
    /// </remarks>
    public static readonly string[] DependencyTrees =
        ["src", "plugins", "tests", "samples", "templates", "scripts"];

    private static readonly Lazy<IReadOnlyList<DeclaredPackage>> DeclaredLazy = new(SurveyDeclared);
    private static readonly Lazy<Resolution> ResolvedLazy = new(SurveyResolved);

    /// <summary>Every <c>PackageReference</c> written down anywhere in the repository.</summary>
    public static IReadOnlyList<DeclaredPackage> Declared => DeclaredLazy.Value;

    /// <summary>Every package in the resolved graph of every project that has been restored.</summary>
    public static IReadOnlyList<ResolvedPackage> Resolved => ResolvedLazy.Value.Packages;

    /// <summary>
    /// The projects with no <c>obj/project.assets.json</c>, so no resolved closure to read.
    /// </summary>
    /// <remarks>
    /// This is the gate's blind spot, named rather than hidden. Such a project still has its
    /// declared references checked — a new dependency in it is still caught — but what that
    /// dependency drags in behind it is not, because nothing on this machine knows yet.
    /// </remarks>
    public static IReadOnlyList<string> ProjectsWithoutAssets => ResolvedLazy.Value.Unresolved;

    /// <summary>Every project file under <see cref="DependencyTrees"/>.</summary>
    public static IReadOnlyList<FileInfo> Projects =>
        RepositoryLayout.ProjectsIn(DependencyTrees);

    /// <summary>
    /// The SPDX expression a package's own <c>.nuspec</c> declares, when it declares one.
    /// </summary>
    /// <remarks>
    /// Returns <c>null</c> for the two shapes a machine cannot read: <c>&lt;license
    /// type="file"&gt;</c>, which names a text file full of prose, and the deprecated
    /// <c>&lt;licenseUrl&gt;</c>-only form used by packages published before SPDX expressions
    /// existed. Those are the rows a human has to read, and the register records that they did.
    /// </remarks>
    public static string? NuspecLicenceExpression(string id, string version)
    {
        var nuspec = PackageFile(id, version, $"{id.ToLowerInvariant()}.nuspec");

        if (nuspec is null)
        {
            return null;
        }

        var element = XDocument.Load(nuspec.FullName)
            .Descendants()
            .FirstOrDefault(static e => e.Name.LocalName == "license");

        return element?.Attribute("type")?.Value == "expression" ? element.Value.Trim() : null;
    }

    /// <summary>A file inside a restored package, or <c>null</c> when the package is not here.</summary>
    public static FileInfo? PackageFile(string id, string version, string relativePath)
    {
        foreach (var folder in ResolvedLazy.Value.Folders)
        {
            var file = new FileInfo(Path.Combine(
                folder, id.ToLowerInvariant(), version, relativePath.Replace('\\', '/')));

            if (file.Exists)
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>Whether any restored copy of this package is present on disk to be read.</summary>
    public static bool IsRestored(string id, string version) =>
        PackageFile(id, version, $"{id.ToLowerInvariant()}.nuspec") is not null;

    /// <summary>
    /// The licence every package built from this repository declares, read from
    /// <c>Directory.Build.props</c>.
    /// </summary>
    public static string? FirstPartyLicence()
    {
        var props = new FileInfo(Path.Combine(RepositoryLayout.Root.FullName, "Directory.Build.props"));

        return props.Exists
            ? XDocument.Load(props.FullName)
                .Descendants()
                .FirstOrDefault(static e => e.Name.LocalName == "PackageLicenseExpression")
                ?.Value.Trim()
            : null;
    }

    // ------------------------------------------------------------------ declared

    private static List<DeclaredPackage> SurveyDeclared()
    {
        var declared = new List<DeclaredPackage>();

        foreach (var file in Projects.Append(
            new FileInfo(Path.Combine(RepositoryLayout.Root.FullName, "Directory.Build.props"))))
        {
            if (!file.Exists)
            {
                continue;
            }

            foreach (var reference in XDocument.Load(file.FullName)
                .Descendants()
                .Where(static e => e.Name.LocalName == "PackageReference"))
            {
                if (reference.Attribute("Include")?.Value is { Length: > 0 } id)
                {
                    declared.Add(new DeclaredPackage(
                        id,
                        SourceSurvey.RelativePath(file),
                        Private: reference.Attribute("PrivateAssets")?.Value
                            .Equals("all", StringComparison.OrdinalIgnoreCase) == true));
                }
            }
        }

        return declared;
    }

    // ------------------------------------------------------------------ resolved

    private static Resolution SurveyResolved()
    {
        var packages = new List<ResolvedPackage>();
        var unresolved = new List<string>();
        var folders = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in Projects)
        {
            var assets = new FileInfo(Path.Combine(project.DirectoryName!, "obj", "project.assets.json"));

            if (!assets.Exists)
            {
                unresolved.Add(SourceSurvey.RelativePath(project));
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(assets.FullName));
            var root = document.RootElement;

            if (root.TryGetProperty("packageFolders", out var packageFolders))
            {
                foreach (var folder in packageFolders.EnumerateObject())
                {
                    folders.Add(folder.Name);
                }
            }

            packages.AddRange(ReadLibraries(root, SourceSurvey.RelativePath(project)));
            packages.AddRange(ReadDownloadDependencies(root, SourceSurvey.RelativePath(project)));
        }

        return new Resolution(packages, unresolved, [.. folders]);
    }

    /// <summary>
    /// Packages NuGet restored for a project without putting them in its library graph.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>PackageDownload</c> is how the SDK fetches a build-time pack — the NativeAOT
    /// compiler and its runtime, a crossgen2 pack, an apphost — and it lands in
    /// <c>downloadDependencies</c> rather than in <c>libraries</c>. Reading only
    /// <c>libraries</c> therefore missed them, and missing them is not a rounding error:
    /// <c>runtime.linux-x64.Microsoft.DotNet.ILCompiler</c> is where the native runtime that
    /// gets <em>statically linked into the published executable</em> comes from. Constraint
    /// C6 says every transitive dependency is vetted, and something linked into the binary
    /// this repository publishes is as transitive as a dependency gets.
    /// </para>
    /// <para>
    /// <strong>This is also what made the gate disagree with itself.</strong> Whether a pack
    /// is recorded as a download dependency or as an ordinary library is a decision of the
    /// SDK writing the assets file, and it is not the same decision in every feature band.
    /// Nothing pins one: there is no global.json, and ci.yml asks setup-dotnet for
    /// <c>10.0.x</c>, so CI floats to whatever is newest while a developer has whatever they
    /// installed. The result was a gate that failed in CI and passed locally on the same
    /// commit — the single most expensive shape a gate can take, because the person who can
    /// fix it is the person who cannot see it. Reading both places makes the verdict the
    /// same on both machines, which is the property that actually matters.
    /// </para>
    /// <para>
    /// Reported as contributing assemblies. It is the conservative reading and the same one
    /// <c>DependencyLicencesAreCompatible</c> already applies to an unrestored project: these
    /// packages have no <c>compile</c> or <c>runtime</c> entry to inspect, so "contributes
    /// nothing" cannot be shown, and for the AOT pack it would be false.
    /// </para>
    /// </remarks>
    private static IEnumerable<ResolvedPackage> ReadDownloadDependencies(JsonElement assets, string project)
    {
        if (!assets.TryGetProperty("project", out var projectSection) ||
            !projectSection.TryGetProperty("frameworks", out var frameworks))
        {
            yield break;
        }

        foreach (var framework in frameworks.EnumerateObject())
        {
            if (!framework.Value.TryGetProperty("downloadDependencies", out var downloads))
            {
                continue;
            }

            foreach (var download in downloads.EnumerateArray())
            {
                if (download.TryGetProperty("name", out var name) &&
                    download.TryGetProperty("version", out var version) &&
                    name.GetString() is { Length: > 0 } id &&
                    ExactVersion(version.GetString()) is { Length: > 0 } resolved)
                {
                    yield return new ResolvedPackage(id, resolved, project, ContributesAssemblies: true);
                }
            }
        }
    }

    /// <summary>
    /// The single version a download dependency's range pins.
    /// </summary>
    /// <remarks>
    /// NuGet writes these as a degenerate inclusive range — <c>[10.0.10, 10.0.10]</c> — because
    /// a package download is always for one exact version. The register's rows carry no
    /// version, but <see cref="IsRestored"/> and <see cref="NuspecLicenceExpression"/> both
    /// need one to find the <c>.nuspec</c> on disk, so the range is reduced to its lower bound.
    /// Anything that is not that shape is returned as written and simply fails to resolve to a
    /// file, which reports as an unreadable licence rather than as a silent pass.
    /// </remarks>
    private static string ExactVersion(string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return string.Empty;
        }

        var trimmed = range.Trim();

        if (trimmed.StartsWith('[') || trimmed.StartsWith('('))
        {
            var inner = trimmed.Trim('[', ']', '(', ')');
            var comma = inner.IndexOf(',', StringComparison.Ordinal);

            return (comma < 0 ? inner : inner[..comma]).Trim();
        }

        return trimmed;
    }

    private static IEnumerable<ResolvedPackage> ReadLibraries(JsonElement assets, string project)
    {
        if (!assets.TryGetProperty("libraries", out var libraries))
        {
            yield break;
        }

        foreach (var library in libraries.EnumerateObject())
        {
            if (library.Value.TryGetProperty("type", out var type) && type.GetString() != "package")
            {
                continue;
            }

            var separator = library.Name.IndexOf('/', StringComparison.Ordinal);

            if (separator < 0)
            {
                continue;
            }

            yield return new ResolvedPackage(
                library.Name[..separator],
                library.Name[(separator + 1)..],
                project,
                ContributesAssemblies(assets, library.Name));
        }
    }

    /// <summary>
    /// Whether the package puts a real assembly into this project's compile or runtime set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what separates a dependency from a build-time participant, and it is read off the
    /// resolved graph rather than asserted by a human. An analyzer contributes neither — it has
    /// no <c>compile</c> and no <c>runtime</c> entry at all. A legacy targeting-pack placeholder
    /// contributes neither either: NuGet records its asset as <c>lib/netstandard1.0/_._</c>,
    /// the empty-file marker, which is the package saying in its own words that it ships nothing.
    /// </para>
    /// <para>
    /// A package that contributes nothing cannot reach a consumer, because there is nothing of it
    /// to reach them with. That is the only ground on which a licence this project would
    /// otherwise refuse is tolerated at all — see the <c>restricted</c> verdict in
    /// docs/DEPENDENCIES.md.
    /// </para>
    /// </remarks>
    private static bool ContributesAssemblies(JsonElement assets, string library)
    {
        if (!assets.TryGetProperty("targets", out var targets))
        {
            return false;
        }

        foreach (var target in targets.EnumerateObject())
        {
            if (!target.Value.TryGetProperty(library, out var entry))
            {
                continue;
            }

            foreach (var kind in (string[])["compile", "runtime"])
            {
                if (entry.TryGetProperty(kind, out var assetGroup)
                    && assetGroup.EnumerateObject().Any(static a => !a.Name.EndsWith("/_._", StringComparison.Ordinal)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private sealed record Resolution(
        IReadOnlyList<ResolvedPackage> Packages,
        IReadOnlyList<string> Unresolved,
        IReadOnlyList<string> Folders);
}

/// <summary>One <c>PackageReference</c> as written in a project file.</summary>
/// <param name="Id">The package id.</param>
/// <param name="DeclaredIn">Repository-relative path of the file declaring it.</param>
/// <param name="Private">Whether it carries <c>PrivateAssets="all"</c>.</param>
internal sealed record DeclaredPackage(string Id, string DeclaredIn, bool Private);

/// <summary>One package NuGet put into a project's resolved graph.</summary>
/// <param name="Id">The package id.</param>
/// <param name="Version">The exact version restored.</param>
/// <param name="Project">Repository-relative path of the project whose graph contains it.</param>
/// <param name="ContributesAssemblies">
/// Whether it supplies a real compile-time or run-time assembly, as opposed to only an analyzer
/// or the <c>_._</c> empty-file placeholder.
/// </param>
internal sealed record ResolvedPackage(
    string Id,
    string Version,
    string Project,
    bool ContributesAssemblies);

using Mono.Cecil;

namespace FlowX.Architecture.Tests;

/// <summary>
/// Opens the assemblies this repository built, for the gates that have to read IL.
/// </summary>
/// <remarks>
/// <para>
/// Most architecture rules here are answered from source or from a <c>.csproj</c>, which is
/// cheaper and cannot be broken by an unrelated build error. Two of them cannot be: P4 and
/// P7 are stated in docs/03-Design-Principles.md as <em>IL scans</em>, and they have to be.
/// A source scan for the word <c>Reflection</c> misses <c>x.GetType().GetMethod(…)</c>
/// written without a using directive, misses it through an alias, and misses it entirely
/// when the call is emitted by a generator into <c>obj/</c>. What ships is the IL, so the
/// question is asked of the IL.
/// </para>
/// <para>
/// Reading rather than loading. <c>Assembly.Load</c> would run type initialisers, resolve
/// every transitive reference and report compiler-generated members as though a human had
/// written them; Cecil reads the file as data. It is already a dependency of this project.
/// </para>
/// <para>
/// A missing assembly is a failure, never a skip. Every gate below degrades into a
/// vacuous pass if it is handed nothing to inspect, and "the build output was not there"
/// is the most likely way that happens.
/// </para>
/// </remarks>
internal static class CompiledAssemblies
{
    /// <summary>
    /// The configuration this test run was built in, read off its own output path.
    /// </summary>
    /// <remarks>
    /// Hard-coding <c>Release</c> would make a Debug run silently inspect stale Release
    /// output — the gate would then be reporting on a build nobody made.
    /// </remarks>
    public static string Configuration { get; } = ConfigurationFrom(AppContext.BaseDirectory);

    /// <summary>
    /// The assemblies built from the trees that ship: the platform, the transports and the
    /// reference applications.
    /// </summary>
    /// <remarks>
    /// Derived from the project files rather than listed, for the reason
    /// <c>EverySourceProjectIsCoveredByTheLayeringRule</c> exists: a hand-maintained subject
    /// list means the next project added is not checked, and nothing says so.
    /// <c>&lt;AssemblyName&gt;</c> is honoured because <c>FlowX.Cli</c> builds to
    /// <c>flowx.dll</c>, and a rule that looked for <c>FlowX.Cli.dll</c> would quietly cover
    /// one project fewer than it claims.
    /// </remarks>
    public static IReadOnlyList<string> ShippingAssemblies => ShippingProjects.Keys.ToList();

    /// <summary>Shipping assembly name to the directory of the project that builds it.</summary>
    private static readonly SortedDictionary<string, DirectoryInfo> ShippingProjects = SurveyProjects();

    /// <summary>The directory of the project that builds an assembly, when it is a shipping one.</summary>
    public static DirectoryInfo? ProjectDirectory(string assemblyName) =>
        ShippingProjects.TryGetValue(assemblyName, out var directory) ? directory : null;

    /// <summary>Opens a project's own build output by project name.</summary>
    /// <param name="assemblyName">The assembly's simple name, e.g. <c>FlowX.Runtime</c>.</param>
    /// <exception cref="FileNotFoundException">The assembly has not been built.</exception>
    public static ModuleDefinition Read(string assemblyName)
    {
        var file = Locate(assemblyName)
            ?? throw new FileNotFoundException(
                $"{assemblyName}.dll was not found in any project's {Configuration} output. " +
                "Build the solution before running the architecture gates — a scan with " +
                "nothing to scan is not a passing gate.",
                assemblyName + ".dll");

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(file.DirectoryName);

        return ModuleDefinition.ReadModule(
            file.FullName,
            new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false });
    }

    /// <summary>
    /// The project's own copy of its output, not a copy some dependent project received.
    /// </summary>
    /// <remarks>
    /// <c>FlowX.Runtime.dll</c> exists in a dozen <c>bin</c> directories once everything that
    /// references it has been built, and reading a stale copy out of a consumer's folder is
    /// a gate reporting on a build nobody made. The search is therefore anchored on the
    /// directory of the project that declares the assembly — which is also why
    /// <c>&lt;AssemblyName&gt;</c> is read: <c>FlowX.Cli</c> builds <c>flowx.dll</c>, and
    /// matching output file name against project directory name would miss it.
    /// </remarks>
    private static FileInfo? Locate(string assemblyName)
    {
        var directory = ShippingProjects.TryGetValue(assemblyName, out var project)
            ? project
            : RepositoryLayout.Root.GetDirectories(assemblyName, SearchOption.AllDirectories).FirstOrDefault()
                ?? RepositoryLayout.Root;

        var output = new DirectoryInfo(Path.Combine(directory.FullName, "bin", Configuration));

        return output.Exists
            ? output.EnumerateFiles(assemblyName + ".dll", SearchOption.AllDirectories)
                .OrderBy(static file => file.FullName.Length)
                .FirstOrDefault()
            : null;
    }

    private static SortedDictionary<string, DirectoryInfo> SurveyProjects()
    {
        var found = new SortedDictionary<string, DirectoryInfo>(StringComparer.Ordinal);

        foreach (var tree in SourceSurvey.ShippingTrees)
        {
            var directory = new DirectoryInfo(Path.Combine(RepositoryLayout.Root.FullName, tree));

            if (!directory.Exists)
            {
                continue;
            }

            foreach (var project in directory.EnumerateFiles("*.csproj", SearchOption.AllDirectories))
            {
                found[AssemblyNameOf(project)] = project.Directory!;
            }
        }

        return found;
    }

    private static string AssemblyNameOf(FileInfo project) =>
        System.Xml.Linq.XDocument.Load(project.FullName)
            .Descendants()
            .Where(static e => e.Name.LocalName == "AssemblyName")
            .Select(static e => e.Value.Trim())
            .FirstOrDefault(static value => value.Length > 0)
        ?? Path.GetFileNameWithoutExtension(project.Name);

    private static string ConfigurationFrom(string baseDirectory)
    {
        var segments = baseDirectory.Replace('\\', '/').TrimEnd('/').Split('/');
        var bin = Array.LastIndexOf(segments, "bin");

        return bin >= 0 && bin + 1 < segments.Length ? segments[bin + 1] : "Release";
    }
}

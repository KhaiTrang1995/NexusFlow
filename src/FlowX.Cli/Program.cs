using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using FlowX.Cli.Diffing;
using FlowX.Cli.Manifest;
using FlowX.Cli.Rendering;
using FlowX.Cli.Verification;

namespace FlowX.Cli;

/// <summary>The <c>flowx</c> command-line tool.</summary>
/// <remarks>
/// Argument parsing is hand-written. The surface is four verbs and a handful of
/// options, and a command-line library would be a dependency this tool carries forever
/// to save about forty lines — see
/// <a href="../../docs/03-Design-Principles.md">principle P12</a>.
/// </remarks>
public static class Program
{
    private const int Ok = 0;

    /// <summary>
    /// A check said no: <c>diff</c> found a breaking change, or <c>verify</c> found
    /// something to report. Distinct from a usage error, because a CI job has to tell
    /// "the gate says no" apart from "the gate could not run" — the first blocks a merge,
    /// the second is a broken pipeline, and conflating them gets the gate disabled.
    /// </summary>
    private const int CheckFailed = 1;

    private const int UsageError = 2;
    private const int NotFound = 3;

    /// <summary>Entry point.</summary>
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0 || IsHelp(args[0]))
        {
            WriteUsage(Console.Out);
            return args.Length == 0 ? UsageError : Ok;
        }

        try
        {
            return args[0] switch
            {
                "graph" => Graph(args.AsSpan(1)),
                "manifest" => ManifestVerb(args.AsSpan(1)),
                "diff" => Diff(args.AsSpan(1)),
                "verify" => Verify(args.AsSpan(1)),
                _ => Fail($"Unknown command '{args[0]}'."),
            };
        }
        catch (FileNotFoundException error)
        {
            Console.Error.WriteLine($"flowx: {error.Message}");
            return NotFound;
        }
        catch (JsonException error)
        {
            // The manifest is machine-written, so malformed input almost always means
            // the wrong file was passed. Say which one.
            Console.Error.WriteLine($"flowx: the manifest is not valid JSON — {error.Message}");
            return UsageError;
        }
    }

    private static int Graph(ReadOnlySpan<string> args)
    {
        var options = Options.Parse(args);
        var manifest = Read(RequireManifest(options));
        var diagram = MermaidRenderer.Render(manifest, options.Value("--flow"));

        return Write(diagram, options.Value("--output"));
    }

    /// <summary>
    /// Reports flows that pay for an execution profile they do not use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One check, <c>--cost</c>, and it is required rather than defaulted. The verb was
    /// named in the documents with three — <c>--complete</c>, <c>--cost</c> and
    /// <c>--runtime</c> — so a bare <c>flowx verify</c> is far more likely to mean "I read
    /// about one of the other two" than "run whatever you have". Guessing would run a
    /// check the caller did not ask for and report a clean result for one they did.
    /// </para>
    /// <para>
    /// The verdict is the exit code, as it is for <c>diff</c>: 1 when something was found,
    /// so a pipeline that wants this gating gets it with no wrapper, and one that wants it
    /// advisory runs it in a step allowed to fail.
    /// </para>
    /// </remarks>
    private static int Verify(ReadOnlySpan<string> args)
    {
        var options = Options.Parse(args);

        // Named in 03-Design-Principles, 01-Vision and ADR-0005, and superseded before it
        // was ever built. Someone who read one of those and typed it deserves to be sent
        // to the check that does run rather than to a bare "unknown option".
        if (options.Has("--complete"))
        {
            return Fail(
                "flowx verify --complete does not exist. The ManifestIsComplete fitness " +
                "function does that job, and it runs in the build rather than after it.");
        }

        // Named in 05-Architecture as the mitigation for R7, manifest drift between build
        // and deploy. It compares a built manifest against a deployed one, and nothing
        // records what is deployed.
        if (options.Has("--runtime"))
        {
            return Fail(
                "flowx verify --runtime does not exist. Comparing the built manifest with " +
                "the deployed one needs a control plane recording the deployed hash, and " +
                "there is none.");
        }

        if (!options.Has("--cost"))
        {
            return Fail("flowx verify requires --cost. It is the only check implemented.");
        }

        if (!TryFormat(options, out var format))
        {
            return Fail($"Unknown --format '{format}'. Use 'text' or 'json'.");
        }

        var report = ProfileCostCheck.Run(Read(RequireManifest(options)));

        var rendered = format is "json"
            ? CostFormatter.ToJson(report)
            : CostFormatter.ToText(report);

        var written = Write(rendered, options.Value("--output"));

        return written is Ok && !report.Passed ? CheckFailed : written;
    }

    private static int ManifestVerb(ReadOnlySpan<string> args)
    {
        var options = Options.Parse(args);
        var assemblyPath = options.Value("--assembly");

        if (assemblyPath is null)
        {
            return Fail("flowx manifest requires --assembly <path>.");
        }

        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException($"No assembly at '{assemblyPath}'.", assemblyPath);
        }

#pragma warning disable IL2026, IL3002 // The verb exists to do this; ManifestExtractor
        var json = ManifestExtractor.Extract(assemblyPath); //  declares the constraint.
#pragma warning restore IL2026, IL3002

        if (json is null)
        {
            Console.Error.WriteLine(
                $"flowx: '{assemblyPath}' contains no FlowX manifest. It was either built " +
                "without FlowX.Compiler, or it declares no flows.");

            return NotFound;
        }

        return Write(json, options.Value("--output"));
    }

    /// <summary>
    /// Compares two manifests and reports whether the second can replace the first.
    /// </summary>
    /// <remarks>
    /// The verdict is the exit code, not the output. This runs in a pipeline step whose
    /// stdout nobody reads until it goes red, so the classification has to be legible to
    /// <c>$?</c> first and to a person second.
    /// </remarks>
    private static int Diff(ReadOnlySpan<string> args)
    {
        var options = Options.Parse(args);
        var baselinePath = options.Value("--old");
        var candidatePath = options.Value("--new");

        if (baselinePath is null || candidatePath is null)
        {
            return Fail("flowx diff requires --old <path> and --new <path>.");
        }

        if (!TryFormat(options, out var format))
        {
            return Fail($"Unknown --format '{format}'. Use 'text' or 'json'.");
        }

        RequireFile(baselinePath, "baseline");
        RequireFile(candidatePath, "candidate");

        var report = ManifestDiff.Compare(Read(baselinePath), Read(candidatePath));

        var rendered = format is "json"
            ? DiffFormatter.ToJson(report)
            : DiffFormatter.ToText(report);

        var written = Write(rendered, options.Value("--output"));

        return written is Ok && report.HasBreakingChange ? CheckFailed : written;
    }

    /// <summary>
    /// The manifest the caller means, defaulting to where the build puts it.
    /// </summary>
    /// <exception cref="FileNotFoundException">There is no manifest there.</exception>
    private static string RequireManifest(Options options)
    {
        var path = options.Value("--manifest") ?? "flowx.manifest.json";

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"No manifest at '{path}'. Build the application first, or pass --manifest.", path);
        }

        return path;
    }

    /// <summary>
    /// Whether the requested output format is one this tool can produce, reporting the
    /// requested value either way so the caller can name it in the error.
    /// </summary>
    /// <remarks>
    /// Rejected rather than defaulted. Silently printing text to a caller that asked for
    /// JSON produces a parse error somewhere downstream, a long way from the typo.
    /// </remarks>
    private static bool TryFormat(Options options, out string format)
    {
        format = options.Value("--format") ?? "text";

        return format is "text" or "json";
    }

    private static void RequireFile(string path, string role)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No {role} manifest at '{path}'.", path);
        }
    }

    private static ManifestDocument Read(string path)
    {
        using var stream = File.OpenRead(path);

        return JsonSerializer.Deserialize(stream, ManifestJsonContext.Default.ManifestDocument)
            ?? new ManifestDocument();
    }

    private static int Write(string content, string? output)
    {
        if (output is null)
        {
            Console.Out.Write(content);
            return Ok;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(output));

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(output, content);
        Console.Error.WriteLine($"flowx: wrote {output}");
        return Ok;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"flowx: {message}");
        WriteUsage(Console.Error);
        return UsageError;
    }

    private static bool IsHelp(string arg) => arg is "--help" or "-h" or "help";

    private static void WriteUsage(TextWriter writer) => writer.Write(
        """
        flowx — the FlowX command-line tool

        Usage:
          flowx graph    [--manifest <path>] [--flow <id>] [--output <path>]
          flowx manifest  --assembly <path>  [--output <path>]
          flowx diff      --old <path> --new <path>
                         [--format text|json] [--output <path>]
          flowx verify    --cost [--manifest <path>]
                         [--format text|json] [--output <path>]

        graph      Renders the manifest as a Mermaid flowchart. Defaults to
                   ./flowx.manifest.json, and writes to stdout unless --output is given.

        manifest   Extracts the manifest embedded in a built assembly. The compiler
                   emits it as a compiled-in constant rather than a file, because a
                   source generator must not do file IO.

        diff       Compares a released manifest with the one this build produced and
                   classifies every difference as breaking, additive or neutral. Exits
                   1 on a breaking change, so it works as a CI gate unmodified.

        verify     --cost reports flows that declare the Durable profile and use nothing
                   it provides — no compensation, no signal, no timer — which is what a
                   profile chosen by accident looks like. Exits 1 when it finds one.

        Exit codes: 0 success, 1 the check said no, 2 usage error, 3 not found.

        """);

    /// <summary>A minimal <c>--name value</c> and <c>--name</c> parser.</summary>
    /// <remarks>
    /// An option is a switch when what follows it is another option or nothing at all, and
    /// carries a value otherwise. That rule is what lets <c>--cost</c> and
    /// <c>--manifest &lt;path&gt;</c> appear in either order in the same command line
    /// without the switch swallowing the option after it.
    /// </remarks>
    private sealed class Options
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly HashSet<string> _switches = new(StringComparer.Ordinal);

        public static Options Parse(ReadOnlySpan<string> args)
        {
            var options = new Options();

            for (var i = 0; i < args.Length; i++)
            {
                if (!IsName(args[i]))
                {
                    continue;
                }

                if (i + 1 < args.Length && !IsName(args[i + 1]))
                {
                    options._values[args[i]] = args[i + 1];
                    i++;
                }
                else
                {
                    options._switches.Add(args[i]);
                }
            }

            return options;
        }

        public string? Value(string name) => _values.TryGetValue(name, out var value) ? value : null;

        /// <summary>Whether the option was given at all, with or without a value.</summary>
        /// <remarks>
        /// Both, because <c>--cost</c> is a switch and a caller who writes
        /// <c>--cost true</c> means the same thing by it. Reading only the switch set
        /// would reject that silently.
        /// </remarks>
        public bool Has(string name) => _switches.Contains(name) || _values.ContainsKey(name);

        private static bool IsName(string arg) => arg.StartsWith("--", StringComparison.Ordinal);
    }
}

/// <summary>Reads the manifest constant out of a built assembly.</summary>
/// <remarks>
/// Uses <see cref="MetadataLoadContext"/> rather than <c>Assembly.LoadFrom</c>. Loading
/// for execution would run module initialisers and static constructors from a file the
/// user pointed us at — more trust than a diagram-rendering tool has any business
/// asking for. Metadata-only loading reads the constant and executes nothing.
/// </remarks>
public static class ManifestExtractor
{
    private const string GeneratedTypeName = "FlowX.Generated.FlowXManifest";
    private const string ConstantName = "Json";

    /// <summary>Returns the manifest JSON, or <c>null</c> when the assembly has none.</summary>
    /// <remarks>
    /// Annotated rather than suppressed. The constraint is real — this method reads a
    /// type by name out of an assembly the caller names, which trimming cannot reason
    /// about — so it is declared, and it propagates to anyone who calls it. Silencing
    /// the analyzer instead would hide a genuine limitation from the next caller.
    /// <para>
    /// It is also not a limitation that matters here: the reflected type lives in the
    /// <em>target</em> assembly, which this tool never trims.
    /// </para>
    /// </remarks>
    [RequiresUnreferencedCode(
        "Reads a type by name out of an assembly supplied at run time; trimming cannot see it.")]
    [RequiresAssemblyFiles(
        "Needs the runtime's reference assemblies on disk, which a single-file bundle does not provide.")]
    public static string? Extract(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;

        var assemblies = Directory.GetFiles(directory, "*.dll")
            .Concat(Directory.GetFiles(
                Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll"))
            .Distinct(StringComparer.Ordinal);

        using var context = new MetadataLoadContext(new PathAssemblyResolver(assemblies));

        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        var type = assembly.GetType(GeneratedTypeName);

        return type?.GetField(ConstantName, BindingFlags.Public | BindingFlags.Static)
            ?.GetRawConstantValue() as string;
    }
}

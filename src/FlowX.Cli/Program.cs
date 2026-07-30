using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using FlowX.Cli.Manifest;
using FlowX.Cli.Rendering;

namespace FlowX.Cli;

/// <summary>The <c>flowx</c> command-line tool.</summary>
/// <remarks>
/// Argument parsing is hand-written. The surface is two verbs and four options, and a
/// command-line library would be a dependency this tool carries forever to save about
/// forty lines — see <a href="../../docs/03-Design-Principles.md">principle P12</a>.
/// </remarks>
public static class Program
{
    private const int Ok = 0;
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
        var path = options.Value("--manifest") ?? "flowx.manifest.json";

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"No manifest at '{path}'. Build the application first, or pass --manifest.", path);
        }

        var manifest = Read(path);
        var diagram = MermaidRenderer.Render(manifest, options.Value("--flow"));

        return Write(diagram, options.Value("--output"));
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

        graph      Renders the manifest as a Mermaid flowchart. Defaults to
                   ./flowx.manifest.json, and writes to stdout unless --output is given.

        manifest   Extracts the manifest embedded in a built assembly. The compiler
                   emits it as a compiled-in constant rather than a file, because a
                   source generator must not do file IO.

        Exit codes: 0 success, 2 usage error, 3 not found.

        """);

    /// <summary>A minimal <c>--name value</c> parser.</summary>
    private sealed class Options
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public static Options Parse(ReadOnlySpan<string> args)
        {
            var options = new Options();

            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    options._values[args[i]] = args[i + 1];
                    i++;
                }
            }

            return options;
        }

        public string? Value(string name) => _values.TryGetValue(name, out var value) ? value : null;
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

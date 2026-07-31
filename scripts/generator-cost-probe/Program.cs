// A deterministic price for one run of FlowPlanGenerator.
//
//   dotnet run --project scripts/generator-cost-probe -c Release -- \
//     --sources <dir> --refs <dir> --compiler <FlowX.Compiler.dll> --runs 5
//
// Driven by scripts/measure-generator-cost.py; see docs/benchmarks/generator-cost-gate.md
// for why this exists and what it is allowed to decide.
//
//
// Why bytes and not milliseconds
// ------------------------------
//
// docs/benchmarks/B12-scale.md measures build overhead in wall clock, because wall clock
// is what the +8 % budget means and what a developer waits for. That measurement needs
// fifteen sandwiched rounds, an A/A control and a documented refusal to answer, and after
// all of it the harness still reports a noise floor of 4-9 % on a quiet machine. It is
// the right instrument for the budget and the wrong one for a per-commit gate.
//
// The quantity below moves by about 0.1 % between runs on the same tree, because it is
// not a measurement of the machine at all: it is a count of the work the generator asked
// Roslyn to do, denominated in bytes. Nothing else in this process is running, the GC is
// non-concurrent, and the compilation is rebuilt from the same syntax trees every time.
//
// That follows the precedent scripts/check-benchmark-budgets.py already sets and argues
// at length: allocation counts are exact and reproducible on shared hardware, timings are
// not, so allocations are the blocking gate and timing is advisory. This is the same
// split applied to compile time instead of run time.
//
// Elapsed milliseconds are reported anyway, and they are advisory for exactly the reason
// that file gives. They are in the JSON so that a reader can see the two metrics agree in
// direction, and so that a regression which somehow costs time without costing
// allocations is visible to a human even though it would not fail the gate.
//
//
// What the number is, and what would move it other than a code change
// -------------------------------------------------------------------
//
// It is the delta of GC.GetTotalAllocatedBytes across one call to
// RunGeneratorsAndUpdateCompilation on a freshly created CSharpCompilation. It therefore
// includes everything the generator makes Roslyn do on its behalf — every SemanticModel
// query it issues binds something, and binding allocates.
//
// Three things other than FlowX.Compiler can move it, and all three are recorded in the
// output so that check-generator-cost.py can refuse a comparison across them rather than
// report the difference as a regression:
//
//   * the Roslyn version (pinned in the .csproj),
//   * the .NET runtime the probe runs on,
//   * the source text of the subject (hashed below).

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

const int ExitOk = 0;
const int ExitBroken = 3;

string? sources = null, references = null, compiler = null, json = null;
int runs = 5, warmup = 2;

try
{
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--sources": sources = args[++i]; break;
            case "--refs": references = args[++i]; break;
            case "--compiler": compiler = args[++i]; break;
            case "--json": json = args[++i]; break;
            case "--runs": runs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--warmup": warmup = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            default: return Fail($"Unknown argument '{args[i]}'.");
        }
    }
}
catch (Exception error) when (error is IndexOutOfRangeException or FormatException)
{
    return Fail("Malformed arguments.");
}

if (sources is null || references is null || compiler is null)
{
    return Fail("--sources, --refs and --compiler are all required.");
}

if (runs < 1)
{
    return Fail("--runs must be at least 1.");
}

// The first run of anything in a fresh process pays JIT, and the JIT allocates. Two
// discarded runs put that behind us: measured samples then differ from each other by
// roughly one part in ten thousand, and by roughly one part in three hundred if the first
// is kept. The warm-up is discarded rather than averaged in because it is a measurement of
// the probe starting up, not of the generator.
if (warmup < 1)
{
    return Fail("--warmup must be at least 1: the first run in a process measures the JIT.");
}

var files = Directory
    .EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories)
    .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
    .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
    .OrderBy(static path => path, StringComparer.Ordinal)
    .ToArray();

if (files.Length == 0)
{
    return Fail($"No .cs files under {sources}. Nothing to generate from.");
}

// The subject has to be pinned as tightly as the compiler under test, because the number
// is a property of the pair. Hashing the source text — in a fixed order, with the paths
// made relative so a temp directory name cannot enter it — is what lets the checker say
// "the project being measured changed, re-record the baseline" instead of reporting a
// change of subject as a regression in the generator.
var digest = SHA256.Create();
var trees = new SyntaxTree[files.Length];

for (var i = 0; i < files.Length; i++)
{
    var relative = Path.GetRelativePath(sources, files[i]).Replace('\\', '/');
    var source = File.ReadAllText(files[i]);

    var block = Encoding.UTF8.GetBytes(relative + "\0" + source + "\0");
    digest.TransformBlock(block, 0, block.Length, null, 0);

    trees[i] = CSharpSyntaxTree.ParseText(source, path: "/src/" + relative);
}

digest.TransformFinalBlock([], 0, 0);
var sourcesSha = Convert.ToHexString(digest.Hash!).ToLowerInvariant();

// The host's own Roslyn, not the compiler's. Filtering Microsoft.CodeAnalysis* out of the
// platform assemblies keeps a second copy of Roslyn from reaching the compilation, where
// it would make every generator type ambiguous.
var platform = ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty)
    .Split(Path.PathSeparator)
    .Where(static path => path.Length > 0)
    .Where(static path => !Path.GetFileName(path).StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal))
    .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path));

var flowx = Directory
    .EnumerateFiles(references, "FlowX.*.dll")
    .Where(static path => !Path.GetFileName(path).Equals("FlowX.Compiler.dll", StringComparison.Ordinal))
    .OrderBy(static path => path, StringComparer.Ordinal)
    .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
    .ToArray();

if (flowx.Length == 0)
{
    return Fail($"No FlowX.*.dll under {references}. The subject would not bind.");
}

var metadata = platform.Concat(flowx).ToArray();

Assembly assembly;
Type generatorType;

try
{
    assembly = Assembly.LoadFrom(Path.GetFullPath(compiler));

    // By name rather than by interface: FlowPlanGenerator is compiled against Roslyn
    // 4.8.0 and this host runs 4.14.0, and while the load resolves to the host's Roslyn
    // in practice, a type-identity check that fails would be a confusing way to find out.
    generatorType = assembly.GetTypes().Single(static type => type.Name == "FlowPlanGenerator");
}
catch (Exception error) when (error is IOException
                                       or BadImageFormatException
                                       or ReflectionTypeLoadException
                                       or InvalidOperationException
                                       or ArgumentException)
{
    return Fail($"Could not load FlowPlanGenerator from {compiler}: {error.Message}");
}

CSharpCompilation NewCompilation() => CSharpCompilation.Create(
    "ScaleSynthetic",
    trees,
    metadata,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

var samples = new List<Sample>(runs);
var generatedTrees = -1;
var generatedChars = -1L;
var diagnostics = -1;

for (var run = 0; run < warmup + runs; run++)
{
    // A fresh generator, a fresh driver and a fresh compilation every run. All three
    // cache, and CompilerBenchmarks records what reusing one costs: an earlier version of
    // that benchmark reported a 175x overhead that was entirely the harness measuring a
    // cache hit against a full bind.
    var generator = (IIncrementalGenerator)Activator.CreateInstance(generatorType)!;
    var driver = CSharpGeneratorDriver.Create(generator);
    var compilation = NewCompilation();

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var before = GC.GetTotalAllocatedBytes(precise: true);
    var clock = Stopwatch.StartNew();

    driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var produced);

    clock.Stop();
    var after = GC.GetTotalAllocatedBytes(precise: true);

    var added = updated.SyntaxTrees.Skip(compilation.SyntaxTrees.Length).ToArray();
    generatedTrees = added.Length;
    generatedChars = added.Sum(static tree => (long)tree.Length);
    diagnostics = produced.Length;

    if (run >= warmup)
    {
        samples.Add(new Sample(after - before, Math.Round(clock.Elapsed.TotalMilliseconds, 2)));
    }
}

var document = new Report(
    new Environment_(
        typeof(CSharpCompilation).Assembly.GetName().Version?.ToString() ?? "unknown",
        FileVersionInfo.GetVersionInfo(typeof(CSharpCompilation).Assembly.Location).ProductVersion ?? "unknown",
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        System.Runtime.GCSettings.IsServerGC),
    new Subject(files.Length, sourcesSha, generatedTrees, generatedChars, diagnostics),
    samples);

var report = JsonSerializer.Serialize(document, ReportContext.Default.Report);

if (json is not null)
{
    File.WriteAllText(json, report);
}

Console.WriteLine(report);
return ExitOk;

static int Fail(string message)
{
    Console.Error.WriteLine($"::error::{message}");
    return ExitBroken;
}

internal sealed record Sample(long AllocatedBytes, double ElapsedMs);

internal sealed record Environment_(string RoslynAssemblyVersion, string RoslynProductVersion, string Framework, bool ServerGc);

internal sealed record Subject(int SourceFiles, string SourcesSha256, int GeneratedTrees, long GeneratedChars, int Diagnostics);

internal sealed record Report(Environment_ Environment, Subject Subject, List<Sample> Samples);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.SnakeCaseLower)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Report))]
internal sealed partial class ReportContext : System.Text.Json.Serialization.JsonSerializerContext;

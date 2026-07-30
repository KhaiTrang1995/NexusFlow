using System.Text.Json;
using Shouldly;
using Xunit;

namespace FlowX.Cli.Tests;

/// <summary>
/// The command-line surface itself: verbs, options, and exit codes.
/// </summary>
/// <remarks>
/// Exit codes are a contract. A script that pipes <c>flowx graph</c> into a file and
/// checks <c>$?</c> is relying on 0 meaning success and on a missing manifest not
/// looking like a usage error — so those are asserted rather than assumed.
/// </remarks>
public sealed class ProgramTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "flowx-cli-tests-" + Guid.NewGuid().ToString("n"));

    public ProgramTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string WriteManifest(string json)
    {
        var path = Path.Combine(_directory, "flowx.manifest.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static (int ExitCode, string Out, string Error) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            return (Program.Main(args), stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    private const string MinimalManifest = """
        {
          "schemaVersion": "0.1.0",
          "application": { "name": "Sample.App", "version": "1.0.0" },
          "flows": [{
            "id": "order.place", "version": "1.0.0", "profile": "Ephemeral",
            "steps": [{ "id": 0, "kind": "Capability", "capability": "order.validate@1.0.0" }],
            "emits": []
          }],
          "capabilities": [
            { "id": "order.validate", "version": "1.0.0", "idempotent": true, "sideEffects": [] }
          ]
        }
        """;

    [Fact]
    public void GraphWritesTheDiagramToStdoutAndSucceeds()
    {
        var (exitCode, output, _) = Run("graph", "--manifest", WriteManifest(MinimalManifest));

        exitCode.ShouldBe(0);
        output.ShouldStartWith("flowchart TD");
        output.ShouldContain("order.place");
    }

    [Fact]
    public void GraphWritesToAFileWhenAskedTo()
    {
        var output = Path.Combine(_directory, "nested", "graph.mmd");

        var (exitCode, stdout, stderr) = Run(
            "graph", "--manifest", WriteManifest(MinimalManifest), "--output", output);

        exitCode.ShouldBe(0);
        File.ReadAllText(output).ShouldStartWith("flowchart TD");
        stdout.ShouldBeEmpty("With --output, stdout stays clean so the command can be piped.");
        stderr.ShouldContain("wrote");
        // The directory did not exist. Creating it beats making every caller mkdir first.
    }

    [Fact]
    public void GraphIsolatesASingleFlow()
    {
        var (exitCode, output, _) = Run(
            "graph", "--manifest", WriteManifest(MinimalManifest), "--flow", "order.place");

        exitCode.ShouldBe(0);
        output.ShouldContain("order.place");
    }

    [Fact]
    public void AMissingManifestIsNotFoundRatherThanAUsageError()
    {
        var (exitCode, _, stderr) = Run("graph", "--manifest", Path.Combine(_directory, "absent.json"));

        exitCode.ShouldBe(3,
            "A script distinguishes 'you called me wrong' from 'the file is not there'. " +
            "Collapsing them makes the second look like a bug in the caller.");
        stderr.ShouldContain("absent.json", Case.Sensitive);
        stderr.ShouldContain("Build the application first", Case.Sensitive);
    }

    [Fact]
    public void AMalformedManifestIsAUsageErrorAndSaysWhy()
    {
        var (exitCode, _, stderr) = Run("graph", "--manifest", WriteManifest("{ not json"));

        exitCode.ShouldBe(2);
        stderr.ShouldContain("not valid JSON", Case.Sensitive);
    }

    [Fact]
    public void AnUnknownVerbIsAUsageErrorAndPrintsHelp()
    {
        var (exitCode, _, stderr) = Run("teleport");

        exitCode.ShouldBe(2);
        stderr.ShouldContain("Unknown command 'teleport'", Case.Sensitive);
        stderr.ShouldContain("Usage:", Case.Sensitive,
            "Printing usage on an unknown verb saves the reader a second invocation.");
    }

    [Fact]
    public void NoArgumentsPrintsHelpAndFailsSoAScriptNotices()
    {
        var (exitCode, output, _) = Run();

        exitCode.ShouldBe(2);
        output.ShouldContain("Usage:", Case.Sensitive);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public void AskingForHelpSucceedsAndWritesToStdout(string flag)
    {
        var (exitCode, output, _) = Run(flag);

        exitCode.ShouldBe(0, "Asking for help is not an error.");
        output.ShouldContain("flowx graph", Case.Sensitive);
        output.ShouldContain("flowx manifest", Case.Sensitive);
        output.ShouldContain("flowx diff", Case.Sensitive);
        output.ShouldContain("1 breaking change", Case.Sensitive,
            "Exit codes are the contract a pipeline reads; the help has to state them.");
    }

    [Fact]
    public void ManifestWithoutAnAssemblyIsAUsageError()
    {
        var (exitCode, _, stderr) = Run("manifest");

        exitCode.ShouldBe(2);
        stderr.ShouldContain("requires --assembly", Case.Sensitive);
    }

    [Fact]
    public void ManifestWithAMissingAssemblyIsNotFound()
    {
        var (exitCode, _, stderr) = Run(
            "manifest", "--assembly", Path.Combine(_directory, "absent.dll"));

        exitCode.ShouldBe(3);
        stderr.ShouldContain("absent.dll", Case.Sensitive);
    }

    [Fact]
    public void ManifestExtractsNothingFromAnAssemblyWithoutOne()
    {
        // This test assembly was not built by FlowX.Compiler, so it carries no manifest.
        // Reporting that clearly beats an empty file or a stack trace.
        var (exitCode, _, stderr) = Run(
            "manifest", "--assembly", typeof(ProgramTests).Assembly.Location);

        exitCode.ShouldBe(3);
        stderr.ShouldContain("contains no FlowX manifest", Case.Sensitive);
        stderr.ShouldContain("declares no flows", Case.Sensitive);
    }

    [Fact]
    public void GraphDefaultsToTheConventionalManifestPath()
    {
        var previous = Directory.GetCurrentDirectory();

        try
        {
            WriteManifest(MinimalManifest);
            Directory.SetCurrentDirectory(_directory);

            var (exitCode, output, _) = Run("graph");

            exitCode.ShouldBe(0, "./flowx.manifest.json is where the build puts it.");
            output.ShouldStartWith("flowchart TD");
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    private string WriteManifest(string name, string json)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>The same application with a capability's authorisation opened up.</summary>
    private const string RelaxedManifest = """
        {
          "schemaVersion": "0.1.0",
          "application": { "name": "Sample.App", "version": "1.1.0" },
          "flows": [{
            "id": "order.place", "version": "1.0.0", "profile": "Ephemeral",
            "steps": [{ "id": 0, "kind": "Capability", "capability": "order.validate@1.0.0" }],
            "emits": []
          }],
          "capabilities": [
            { "id": "order.validate", "version": "1.0.0", "idempotent": true, "sideEffects": [],
              "authorization": { "mode": "Public" } }
          ]
        }
        """;

    /// <summary>The baseline for the diff tests: identical shape, restrictive stance.</summary>
    private const string StrictManifest = """
        {
          "schemaVersion": "0.1.0",
          "application": { "name": "Sample.App", "version": "1.0.0" },
          "flows": [{
            "id": "order.place", "version": "1.0.0", "profile": "Ephemeral",
            "steps": [{ "id": 0, "kind": "Capability", "capability": "order.validate@1.0.0" }],
            "emits": []
          }],
          "capabilities": [
            { "id": "order.validate", "version": "1.0.0", "idempotent": true, "sideEffects": [],
              "authorization": { "mode": "Authenticated" } }
          ]
        }
        """;

    [Fact]
    public void DiffExitsNonZeroOnABreakingChangeSoItGatesWithoutAWrapper()
    {
        var (exitCode, output, _) = Run(
            "diff",
            "--old", WriteManifest("old.json", StrictManifest),
            "--new", WriteManifest("new.json", RelaxedManifest));

        exitCode.ShouldBe(1,
            "A CI step runs this unmodified. If a breaking change exited 0, the gate would " +
            "have to be reimplemented by every pipeline that uses it.");

        output.ShouldContain("FLOWX-DIFF-014", Case.Sensitive);
        output.ShouldContain("INCOMPATIBLE", Case.Sensitive);
    }

    [Fact]
    public void DiffSucceedsWhenNothingIncompatibleChanged()
    {
        var (exitCode, output, _) = Run(
            "diff",
            "--old", WriteManifest("old.json", StrictManifest),
            "--new", WriteManifest("new.json", StrictManifest));

        exitCode.ShouldBe(0);
        output.ShouldContain("No contract changes.", Case.Sensitive);
    }

    [Fact]
    public void DiffKeepsTheBreakingVerdictWhenWritingToAFile()
    {
        var output = Path.Combine(_directory, "nested", "diff.json");

        var (exitCode, stdout, _) = Run(
            "diff",
            "--old", WriteManifest("old.json", StrictManifest),
            "--new", WriteManifest("new.json", RelaxedManifest),
            "--format", "json",
            "--output", output);

        exitCode.ShouldBe(1, "Redirecting the report must not change the verdict.");
        stdout.ShouldBeEmpty();

        using var document = JsonDocument.Parse(File.ReadAllText(output));

        document.RootElement.GetProperty("compatible").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void DiffEmitsParsableJsonWhenAskedTo()
    {
        var (exitCode, stdout, _) = Run(
            "diff",
            "--old", WriteManifest("old.json", StrictManifest),
            "--new", WriteManifest("new.json", RelaxedManifest),
            "--format", "json");

        exitCode.ShouldBe(1);

        using var document = JsonDocument.Parse(stdout);

        document.RootElement.GetProperty("findings").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("--old")]
    [InlineData("--new")]
    public void DiffWithOnlyOneManifestIsAUsageError(string given)
    {
        var (exitCode, _, stderr) = Run("diff", given, WriteManifest("only.json", StrictManifest));

        exitCode.ShouldBe(2);
        stderr.ShouldContain("requires --old", Case.Sensitive);
    }

    [Fact]
    public void DiffRejectsAFormatItCannotProduceRatherThanQuietlyPrintingText()
    {
        var (exitCode, _, stderr) = Run(
            "diff",
            "--old", WriteManifest("old.json", StrictManifest),
            "--new", WriteManifest("new.json", StrictManifest),
            "--format", "yaml");

        exitCode.ShouldBe(2, "A usage error, not a breaking change — the gate never ran.");
        stderr.ShouldContain("Unknown --format 'yaml'", Case.Sensitive);
        // Printing text to a caller that asked for JSON produces a parse failure a long
        // way downstream from the typo.
    }

    [Fact]
    public void DiffWithAMissingBaselineIsNotFoundAndSaysWhichSideIsMissing()
    {
        var (exitCode, _, stderr) = Run(
            "diff",
            "--old", Path.Combine(_directory, "absent.json"),
            "--new", WriteManifest("new.json", StrictManifest));

        exitCode.ShouldBe(3);
        stderr.ShouldContain("baseline", Case.Sensitive);
        stderr.ShouldContain("absent.json", Case.Sensitive);
    }

    [Fact]
    public void DiffWithAMissingCandidateIsNotFoundAndSaysWhichSideIsMissing()
    {
        var (exitCode, _, stderr) = Run(
            "diff",
            "--old", WriteManifest("old.json", StrictManifest),
            "--new", Path.Combine(_directory, "absent.json"));

        exitCode.ShouldBe(3);
        stderr.ShouldContain("candidate", Case.Sensitive);
    }

    [Fact]
    public void TheCommittedFixtureIsAValidManifest()
    {
        // Guards the fixture the CI mmdc step renders. If it drifts out of shape, this
        // fails here rather than as a confusing Mermaid parse error in another job.
        var fixture = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "fixtures", "sample.manifest.json");

        File.Exists(fixture).ShouldBeTrue($"Expected the CI fixture at {Path.GetFullPath(fixture)}.");

        using var document = JsonDocument.Parse(File.ReadAllText(fixture));

        document.RootElement.GetProperty("flows").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public void RejectsNullArguments()
        => Should.Throw<ArgumentNullException>(() => Program.Main(null!));
}

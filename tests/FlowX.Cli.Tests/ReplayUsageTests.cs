using Shouldly;
using Xunit;

namespace FlowX.Cli.Tests;

/// <summary>
/// The <c>replay</c> verb's command-line surface, and the store dependency
/// [ADR-0020](../../docs/adr/ADR-0020-cli-reads-the-journal-as-rows.md) introduced.
/// </summary>
/// <remarks>
/// Nothing here needs a database. That is the point of most of it: the interesting
/// assertions are about what happens when there is no store, or when the caller asked for
/// something the tool cannot do, and every one of those answers has to arrive without a
/// connection being attempted.
/// </remarks>
[Collection(CliConsoleGroup.Name)]
public sealed class ReplayUsageTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "flowx-replay-tests-" + Guid.NewGuid().ToString("n"));

    public ReplayUsageTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void ReplayWithoutAnInstanceIsAUsageError()
    {
        var (exitCode, _, stderr) = CliRunner.Run("replay", "--mode", "inspect");

        exitCode.ShouldBe(2);
        stderr.ShouldContain("requires --instance", Case.Sensitive);
    }

    [Fact]
    public void ReplayWithoutAModeIsAUsageErrorRatherThanAGuess()
    {
        var (exitCode, _, stderr) = CliRunner.Run("replay", "--instance", Guid.NewGuid().ToString());

        exitCode.ShouldBe(
            2,
            "The verb is documented with four modes and three of them change state. " +
            "Defaulting to the read-only one would be a kindness today and a disaster the " +
            "day a second mode ships, because the caller who meant --mode resume would get " +
            "silence and conclude nothing happened.");

        stderr.ShouldContain("requires --mode inspect", Case.Sensitive);
    }

    [Theory]
    [InlineData("simulate")]
    [InlineData("resume")]
    [InlineData("fork")]
    public void AModeThatNeedsAnEngineSaysSoRatherThanReportingAnUnknownOption(string mode)
    {
        var (exitCode, _, stderr) = CliRunner.Run(
            "replay", "--instance", Guid.NewGuid().ToString(), "--mode", mode);

        exitCode.ShouldBe(2);

        stderr.ShouldContain(mode, Case.Sensitive);
        stderr.ShouldContain(
            "ADR-0020",
            Case.Sensitive,
            "These three are not merely unbuilt — they are outside what the dependency " +
            "decision reaches, because each needs the engine the CLI is forbidden to link. " +
            "Someone who reads 12-Observability §5 and types one deserves to be sent to the " +
            "record that says why, not told the option is unknown.");
    }

    [Fact]
    public void ReplayRejectsAnInstanceIdThatIsNotOne()
    {
        var (exitCode, _, stderr) = CliRunner.Run(
            "replay", "--mode", "inspect", "--instance", "fi_01HV8");

        exitCode.ShouldBe(2, "A malformed id is a usage error; no lookup was possible.");
        stderr.ShouldContain("fi_01HV8", Case.Sensitive);
    }

    [Fact]
    public void ReplayWithNoConnectionStringNamesTheVariableRatherThanFailingToConnect()
    {
        var restore = Environment.GetEnvironmentVariable(CliPostgresDatabase.ConnectionVariable);

        try
        {
            Environment.SetEnvironmentVariable(CliPostgresDatabase.ConnectionVariable, null);

            var (exitCode, _, stderr) = CliRunner.Run(
                "replay", "--mode", "inspect", "--instance", Guid.NewGuid().ToString());

            exitCode.ShouldBe(2, "Nobody told the tool where the journal is. That is a usage error.");

            stderr.ShouldContain("--connection", Case.Sensitive);
            stderr.ShouldContain(CliPostgresDatabase.ConnectionVariable, Case.Sensitive);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CliPostgresDatabase.ConnectionVariable, restore);
        }
    }

    [Fact]
    public void AStoreThatCannotBeReachedIsNotReportedAsAMissingInstance()
    {
        var (exitCode, _, stderr) = CliRunner.Run(
            "replay", "--mode", "inspect",
            "--instance", Guid.NewGuid().ToString(),
            // Port 1 is reserved and nothing listens on it, so this fails to connect rather
            // than failing to authenticate — the case an operator hits when the bastion is
            // down, not when they typed the password wrong.
            "--connection", "Host=127.0.0.1;Port=1;Database=postgres;Username=postgres;Timeout=2");

        exitCode.ShouldBe(
            4,
            "Exit 3 would tell an operator mid-incident that the instance does not exist, " +
            "when the truth is that the tool could not look. That is the most dangerous of " +
            "the four wrong answers available, which is why this code exists rather than " +
            "being folded into one of them.");

        stderr.ShouldContain("could not be reached", Case.Sensitive);
    }

    [Fact]
    public void HelpNamesReplayNowThatItExists()
    {
        var (_, output, _) = CliRunner.Run("--help");

        output.ShouldContain("flowx replay", Case.Sensitive);
        output.ShouldContain(
            "4 the store could not be reached",
            Case.Sensitive,
            "Exit codes are the contract a pipeline reads, and the help is where they are " +
            "stated. A new code that appears only in a document is a code nobody handles.");
    }

    /// <summary>
    /// The four verbs that predate <c>replay</c> still run with no store, and none of them
    /// can reach one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the compensating control
    /// [ADR-0020](../../docs/adr/ADR-0020-cli-reads-the-journal-as-rows.md) §3 commits
    /// to.</strong> "The CLI is runnable against an artifact with no database" was one of
    /// three properties resting on <c>CliDependsOnNothingButTheManifest</c>, and the only one
    /// that rule never asserted. It was true because no verb had needed a store. The moment
    /// one does, it stops being accidental and has to be checked.
    /// </para>
    /// <para>
    /// The environment is pointed at a dead server for the duration, so a verb that had
    /// quietly grown a connection would fail rather than pass by not being asked.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryVerbButReplayRunsWithNoStore()
    {
        var manifest = Path.Combine(_directory, "flowx.manifest.json");

        File.WriteAllText(manifest, TestManifests.Minimal);

        var restore = Environment.GetEnvironmentVariable(CliPostgresDatabase.ConnectionVariable);

        try
        {
            Environment.SetEnvironmentVariable(
                CliPostgresDatabase.ConnectionVariable,
                "Host=127.0.0.1;Port=1;Database=nothing;Username=nobody;Timeout=2");

            CliRunner.Run("graph", "--manifest", manifest).ExitCode.ShouldBe(0);
            CliRunner.Run("verify", "--cost", "--manifest", manifest).ExitCode.ShouldBe(0);
            CliRunner.Run("diff", "--old", manifest, "--new", manifest).ExitCode.ShouldBe(0);

            CliRunner.Run("manifest", "--assembly", typeof(ReplayUsageTests).Assembly.Location)
                .ExitCode.ShouldBe(3, "This assembly carries no manifest — the same answer as ever.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CliPostgresDatabase.ConnectionVariable, restore);
        }
    }

    /// <summary>Only the replay reader knows what a database is.</summary>
    /// <remarks>
    /// <para>
    /// The structural half of the property above, and the durable one. The runtime test
    /// proves the four verbs do not reach a store <em>today</em>; this proves nothing outside
    /// <c>Replay/</c> <em>can</em>, so a fifth verb cannot acquire a store dependency by
    /// accident.
    /// </para>
    /// <para>
    /// Scanned as text rather than by reflecting over types, for the reason
    /// <c>DiffCodeDocumentationTests</c> gives: a list of exempt files would be a second place
    /// the rule is written down, and the test would then check that two of the three agree
    /// while the code drifted from both.
    /// </para>
    /// </remarks>
    [Fact]
    public void NothingOutsideTheReplayReaderKnowsWhatADatabaseIs()
    {
        var root = CliRunner.RepositoryPath("src/FlowX.Cli");

        var leaked = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Replace('\\', '/').Split('/').Any(static s => s is "obj" or "bin"))
            .Where(path => !Path.GetRelativePath(root, path).Replace('\\', '/').StartsWith("Replay/", StringComparison.Ordinal))
            .Where(static path => File.ReadAllText(path).Contains("Npgsql", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToList();

        leaked.ShouldBeEmpty(
            "The store dependency is confined to src/FlowX.Cli/Replay by ADR-0020. A file " +
            "outside it that names Npgsql has widened the CLI's inputs without the decision " +
            "that widening needs: " + string.Join(", ", leaked));
    }
}

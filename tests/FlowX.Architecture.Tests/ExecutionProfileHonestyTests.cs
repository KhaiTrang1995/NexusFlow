using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// The executable form of the statement `FLOWX1028` is built on: <c>FlowX.Runtime</c> does
/// not read <c>ExecutionProfile</c>.
/// </summary>
/// <remarks>
/// <para>
/// A flow declaring <c>Profile = ExecutionProfile.Durable</c> runs on the ephemeral engine:
/// no journal, no lease, no resumption, no replay. The profile reaches an
/// <c>ExecutionPlan</c> validation and the <c>profile</c> field of
/// <c>flowx.manifest.json</c>, and stops there. That is what
/// <c>docs/06-Execution-Engine.md §4</c> warns about, what makes risk R2 in
/// <c>docs/05-Architecture.md §11</c> unreachable rather than mitigated, and what
/// <c>FLOWX1028</c> exists to say out loud.
/// </para>
/// <para>
/// <strong>This test is a deletion reminder, and it is meant to fail.</strong> The
/// diagnostic is scaffolding for a phase that has not happened, and a scaffold nobody
/// removes when it stops being true is noise — noise is what teaches people to suppress a
/// catalogue. The day P2 makes the runtime read the profile, the assertion below goes red
/// and its message says exactly what to take down. Leaving the removal to somebody
/// remembering is how a rule outlives the gap it describes.
/// </para>
/// <para>
/// Source rather than IL, and deliberately: the claim is about what the runtime
/// <em>says</em>, so that a partially wired-up journal — a field read but not yet acted on
/// — trips it. That is the moment the honesty of the declaration changes, and it arrives
/// before any behaviour the IL could show.
/// </para>
/// </remarks>
public sealed class ExecutionProfileHonestyTests
{
    /// <summary>
    /// The trees that execute a flow. Nothing here may consult the profile while
    /// <c>FLOWX1028</c> claims the profile is not honoured.
    /// </summary>
    /// <remarks>
    /// <c>src/FlowX.Core</c> is deliberately absent: <c>ExecutionPlan</c> validates that a
    /// durable flow's steps are supported, which is a compile-time-shaped check on a
    /// descriptor and not the engine honouring the profile. <c>src/FlowX.Compiler</c> is
    /// absent for the same reason — reading the attribute is its job, and
    /// <c>ExecutionProfileAnalyzer</c> itself lives there.
    /// </remarks>
    private static readonly string[] ExecutingTrees = ["src/FlowX.Runtime", "src/FlowX.Hosting", "plugins"];

    private const string Reminder =
        "FLOWX1028 says a declared execution profile is not honoured, because the runtime " +
        "never reads one. If that has just changed, the rule has done its job and must now " +
        "be taken down rather than left to rot: narrow ExecutionProfileAnalyzer to the " +
        "profiles still unimplemented, or delete the analyzer, its descriptor in " +
        "FlowXDiagnostics, its row in AnalyzerReleases.Unshipped.md, its tests and " +
        "docs/diagnostics/FLOWX1028.md. Then correct docs/06-Execution-Engine.md §4, " +
        "ADR-0003 and risk R2 in docs/05-Architecture.md §11, which all record the gap. " +
        "A rule that outlives what it describes is noise.";

    [Fact]
    public void RuntimeDoesNotReadTheExecutionProfile()
    {
        var readers = SourceSurvey.SourceFiles(ExecutingTrees)
            .Where(static file => File.ReadAllText(file.FullName).Contains("ExecutionProfile", StringComparison.Ordinal))
            .Select(SourceSurvey.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();

        readers.ShouldBeEmpty(Reminder);
    }

    /// <summary>
    /// The trees the reminder watches must exist, or it passes by looking at nothing.
    /// </summary>
    /// <remarks>
    /// A gate whose subject has been renamed out from under it reports green forever. That
    /// is the failure mode this whole work package is about, so the reminder gets the guard
    /// it recommends for everything else.
    /// </remarks>
    [Fact]
    public void TheTreesTheReminderWatchesExist()
    {
        foreach (var tree in ExecutingTrees)
        {
            Directory.Exists(Path.Combine(RepositoryLayout.Root.FullName, tree)).ShouldBeTrue(
                $"{tree} is missing, so RuntimeDoesNotReadTheExecutionProfile would pass by " +
                "scanning nothing.");
        }

        SourceSurvey.SourceFiles(ExecutingTrees).ShouldNotBeEmpty(
            "The reminder found no source at all, which is a passing test that checks nothing.");
    }
}

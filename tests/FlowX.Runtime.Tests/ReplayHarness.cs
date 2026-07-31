using System.Globalization;
using System.Text;
using FlowX.Conformance.InMemory;
using FlowX.Runtime;
using Shouldly;

namespace FlowX.Runtime.Tests;

/// <summary>
/// One journaled instance as the comparison sees it: the flow it ran, and every row it
/// committed in commit order.
/// </summary>
internal sealed record InstanceHistory(FlowInstanceRecord Record, IReadOnlyList<JournalStep> Rows);

/// <summary>
/// One execution, as observed through both channels: what the flow did in process, and what
/// the journal was left holding.
/// </summary>
/// <remarks>
/// <strong>Two channels, because neither is sufficient.</strong> The journal is the contract —
/// the replay guarantee is stated about it — but it records what <em>ran</em> and not the
/// decisions that chose what ran: ADR-0015's first amendment removed the field for the branch
/// taken and made it derived, so a flow that took the other arm of a two-arm <c>Switch</c> in
/// which both arms invoke the same capability writes an identical row. It also records nothing
/// at all for a compensation's ambient reads. The trace catches both. Conversely the trace is
/// in-process and cannot see redaction, payload shape, scope spelling, attempt numbering or
/// commit order, which is where a store-visible divergence lives.
/// </remarks>
internal sealed record ObservedRun(
    IReadOnlyList<string> Trace,
    IReadOnlyList<InstanceHistory> Instances,
    FlowExecutionResult Result);

/// <summary>How strictly the in-process trace of two runs must line up.</summary>
internal enum TraceOrder
{
    /// <summary>
    /// Action for action, in the order they happened. What a replay must reproduce for every
    /// shape whose ordering the flow itself determines.
    /// </summary>
    AsRun,

    /// <summary>
    /// The same actions, in any order.
    /// </summary>
    /// <remarks>
    /// For a <c>Detached</c> sub-flow only, and it is a statement about the DSL rather than a
    /// weakening of the gate. Detachment is the author saying the child's steps are not
    /// ordered against the parent's remaining ones; requiring the replay to interleave them
    /// identically would be asserting an ordering the flow deliberately declined to declare,
    /// and the test would be measuring the thread pool. What must still match exactly is every
    /// action, every journal row, and every capture.
    /// </remarks>
    AsSet,
}

/// <summary>One difference between an original execution and its replay.</summary>
/// <param name="Channel">Which observation channel saw it.</param>
/// <param name="Where">The position in that channel, named the way a reader can find it.</param>
/// <param name="Original">What the original execution did.</param>
/// <param name="Replayed">What the replay did instead.</param>
/// <param name="Because">What that difference means, in the contract's own terms.</param>
internal sealed record ReplayDivergence(
    string Channel,
    string Where,
    string Original,
    string Replayed,
    string Because);

/// <summary>
/// The verdict on one replay, and the report a failure produces.
/// </summary>
/// <remarks>
/// A report rather than an exception, so that the tests which exist to prove the harness can
/// <em>detect</em> a divergence can read what it detected instead of matching on a message.
/// A determinism gate that cannot be shown to fail is worth nothing, and this repository has
/// removed several of exactly that kind.
/// </remarks>
internal sealed class ReplayReport(
    ObservedRun original, ObservedRun replayed, IReadOnlyList<ReplayDivergence> divergences)
{
    /// <summary>The original execution.</summary>
    public ObservedRun Original => original;

    /// <summary>The replay.</summary>
    public ObservedRun Replayed => replayed;

    /// <summary>Every difference found, in the order the channels were compared.</summary>
    public IReadOnlyList<ReplayDivergence> Divergences => divergences;

    /// <summary>Whether the replay reproduced the original execution.</summary>
    public bool IsIdentical => divergences.Count == 0;

    /// <summary>The divergence report, as a failing test prints it.</summary>
    public string Describe()
    {
        if (IsIdentical)
        {
            return $"The replay reproduced the original execution: " +
                $"{original.Trace.Count.ToString(CultureInfo.InvariantCulture)} traced actions and " +
                $"{Rows(original).ToString(CultureInfo.InvariantCulture)} committed rows, identical.";
        }

        var report = new StringBuilder()
            .Append("The replay diverged from the original execution in ")
            .Append(divergences.Count.ToString(CultureInfo.InvariantCulture))
            .AppendLine(" place(s).")
            .AppendLine();

        foreach (var divergence in divergences)
        {
            report
                .Append("  ").Append(divergence.Channel).Append(" · ").AppendLine(divergence.Where)
                .Append("    original : ").AppendLine(divergence.Original)
                .Append("    replay   : ").AppendLine(divergence.Replayed)
                .Append("    ").AppendLine(divergence.Because)
                .AppendLine();
        }

        return report.ToString();
    }

    /// <summary>Asserts that the replay reproduced the original, step for step.</summary>
    public void ShouldBeIdentical() => IsIdentical.ShouldBeTrue(Describe());

    /// <summary>Asserts that the harness caught a divergence, and that it named the right one.</summary>
    /// <param name="naming">
    /// Text the report must contain — the thing that diverged, not the fact that something did.
    /// </param>
    public void ShouldHaveCaught(params string[] naming)
    {
        IsIdentical.ShouldBeFalse(
            "The replay was expected to diverge and the harness reported nothing. A " +
            "determinism gate that cannot fail is worth nothing.");

        var report = Describe();

        foreach (var expected in naming)
        {
            report.ShouldContain(
                expected,
                Case.Sensitive,
                $"The divergence report must name what diverged. It says:{Environment.NewLine}{report}");
        }
    }

    private static int Rows(ObservedRun run) => run.Instances.Sum(instance => instance.Rows.Count);
}

/// <summary>
/// Runs a durable flow, then runs it again against its own journal, and compares.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The replay is a second execution from the first step, not a resumption.</strong>
/// Resumption skips what has committed — that is what it is for — so it re-runs only the
/// suffix and could never demonstrate that the committed prefix reproduces. What
/// <c>docs/06-Execution-Engine.md</c> §5 states is that replaying a completed instance
/// produces byte-identical step inputs and identical control flow, and the only way to see it is
/// to run the whole flow again with the journal, rather than the world, answering everything
/// it reads from outside itself.
/// </para>
/// <para>
/// <strong>The replay's clock is a hundred days from the original's.</strong> Deliberately: an
/// ambient value that is replayed comes back in the original's epoch, and one that leaked a
/// fresh read comes back in the replay's, so the two can never be confused and the comparison
/// cannot pass by accident. A shared fixed clock would have made every clock read agree
/// whether or not anything was replayed, which is the "passing by comparing nothing" the exit
/// criterion forbids.
/// </para>
/// </remarks>
internal static class ReplayHarness
{
    /// <summary>The original execution's epoch.</summary>
    public static DateTimeOffset Epoch { get; } = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The replay's epoch — far enough away that a fresh read is unmistakable.</summary>
    public static DateTimeOffset ReplayEpoch { get; } = Epoch.AddDays(100);

    private static FencingToken Token { get; } = new(1);

    /// <summary>
    /// Runs <paramref name="plan"/> twice — once against the world, once against the journal
    /// the first run wrote — and reports every difference.
    /// </summary>
    /// <param name="plan">The flow, which must declare <c>Durable</c>.</param>
    /// <param name="flow">
    /// Builds the corpus flow. Called twice, so the two runs share no state but the journal —
    /// a replay that reused the original's dispatcher would inherit its ledger and pass by
    /// remembering rather than by reproducing.
    /// </param>
    /// <param name="onReplay">Applied to the replay's flow tree only, to introduce an impurity.</param>
    /// <param name="order">How strictly the two traces must line up.</param>
    /// <param name="ct">Cancels both runs.</param>
    public static async Task<ReplayReport> RunAsync(
        ExecutionPlan plan,
        Func<CorpusFlow> flow,
        Action<CorpusFlow>? onReplay = null,
        TraceOrder order = TraceOrder.AsRun,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(flow);

        plan.Flow.Profile.ShouldBe(
            ExecutionProfile.Durable,
            "A flow that is not journaled has no capture to replay, and the engine refuses it.");

        var original = await ExecuteAsync(plan, flow(), captures: null, ct).ConfigureAwait(false);

        var replayFlow = flow();
        onReplay?.Invoke(replayFlow);

        var captures = original.Instances
            .Select(instance => new CaptureSource(instance.Rows))
            .ToList();

        var replayed = await ExecuteAsync(plan, replayFlow, captures, ct).ConfigureAwait(false);

        return new ReplayReport(original, replayed, Compare(original, replayed, order));
    }

    private static async Task<ObservedRun> ExecuteAsync(
        ExecutionPlan plan,
        CorpusFlow flow,
        IReadOnlyList<CaptureSource>? captures,
        CancellationToken ct)
    {
        var journal = new InMemoryFlowJournal();
        var clock = new DriftingClock(captures is null ? Epoch : ReplayEpoch);
        var trace = new List<string>();

        flow.Drive(trace, captures);

        var begun = await DurableExecution.BeginAsync(
            journal, plan, Plans.Invocation, Guid.NewGuid(), Token, cancellationToken: ct);

        begun.IsSuccess.ShouldBeTrue(begun.IsFailure ? begun.Error.ToString() : null);

        var engine = new FlowEngine(clock);
        var result = await engine.ExecuteAsync(plan, flow, Plans.Invocation, begun.Value, ct);

        // A detached child outlives the step that started it, so its rows are not all
        // committed when the parent returns. Waiting is what makes the comparison a
        // comparison of two finished histories rather than a race.
        _ = await engine.WaitForDetachedAsync(TimeSpan.FromSeconds(30), ct);

        var instances = new List<InstanceHistory>();

        foreach (var record in journal.Instances)
        {
            var frontier = await journal.ReadResumeFrontierAsync(record.InstanceId, ct);

            frontier.IsSuccess.ShouldBeTrue();

            instances.Add(new InstanceHistory(
                await Reread(journal, record.InstanceId, ct),
                [.. frontier.Value.Committed.OrderBy(row => row.Sequence)]));
        }

        return new ObservedRun(trace, instances, result);
    }

    private static async Task<FlowInstanceRecord> Reread(
        InMemoryFlowJournal journal, Guid instanceId, CancellationToken ct)
    {
        var record = await journal.ReadInstanceAsync(instanceId, ct);

        record.IsSuccess.ShouldBeTrue();

        return record.Value;
    }

    /// <summary>Compares the two runs through both channels.</summary>
    private static List<ReplayDivergence> Compare(
        ObservedRun original, ObservedRun replayed, TraceOrder order)
    {
        var found = new List<ReplayDivergence>();

        CompareTraces(original, replayed, order, found);
        CompareInstances(original, replayed, found);
        CompareOutcome(original, replayed, found);

        return found;
    }

    private static void CompareTraces(
        ObservedRun original, ObservedRun replayed, TraceOrder order, List<ReplayDivergence> found)
    {
        var before = Read(original, order);
        var after = Read(replayed, order);
        var longest = Math.Max(before.Count, after.Count);

        for (var i = 0; i < longest; i++)
        {
            var did = i < before.Count ? before[i] : "(the flow had finished)";
            var replayedDid = i < after.Count ? after[i] : "(the flow had finished)";

            if (did == replayedDid)
            {
                continue;
            }

            found.Add(new ReplayDivergence(
                "what the flow did",
                $"action {(i + 1).ToString(CultureInfo.InvariantCulture)} of " +
                    $"{longest.ToString(CultureInfo.InvariantCulture)}",
                did,
                replayedDid,
                Why(did, replayedDid)));

            // One report, not one per remaining action. Everything after the first divergence
            // is a consequence of it, and a page of consequences buries the cause.
            return;
        }
    }

    private static IReadOnlyList<string> Read(ObservedRun run, TraceOrder order) =>
        order == TraceOrder.AsRun ? run.Trace : [.. run.Trace.Order(StringComparer.Ordinal)];

    private static void CompareInstances(
        ObservedRun original, ObservedRun replayed, List<ReplayDivergence> found)
    {
        if (original.Instances.Count != replayed.Instances.Count)
        {
            found.Add(new ReplayDivergence(
                "what the journal holds",
                "instances started",
                original.Instances.Count.ToString(CultureInfo.InvariantCulture),
                replayed.Instances.Count.ToString(CultureInfo.InvariantCulture),
                "A replay that composed a different number of flows did not take the same path."));

            return;
        }

        for (var i = 0; i < original.Instances.Count; i++)
        {
            CompareInstance(i, original.Instances[i], replayed.Instances[i], found);
        }
    }

    private static void CompareInstance(
        int ordinal, InstanceHistory original, InstanceHistory replayed, List<ReplayDivergence> found)
    {
        var flow = original.Record.FlowId;

        if (original.Rows.Count != replayed.Rows.Count)
        {
            found.Add(new ReplayDivergence(
                "what the journal holds",
                $"{flow} committed rows",
                original.Rows.Count.ToString(CultureInfo.InvariantCulture),
                replayed.Rows.Count.ToString(CultureInfo.InvariantCulture),
                "A replay that committed a different number of rows ran a different set of steps."));
        }

        if (original.Record.State != replayed.Record.State)
        {
            found.Add(new ReplayDivergence(
                "what the journal holds",
                $"{flow} final state",
                original.Record.State.ToString(),
                replayed.Record.State.ToString(),
                "A replay that ended in a different state did not reproduce the execution."));
        }

        if (original.Record.StateBagJson != replayed.Record.StateBagJson)
        {
            found.Add(new ReplayDivergence(
                "what the journal holds",
                $"{flow} final state bag",
                original.Record.StateBagJson ?? "(none)",
                replayed.Record.StateBagJson ?? "(none)",
                "The state bag is what a resume rehydrates from, so a byte that differs here " +
                "is a byte the rest of the flow would have bound to."));
        }

        var shared = Math.Min(original.Rows.Count, replayed.Rows.Count);

        for (var i = 0; i < shared; i++)
        {
            CompareRow(ordinal, flow, i, original.Rows[i], replayed.Rows[i], found);
        }
    }

    private static void CompareRow(
        int ordinal,
        string flow,
        int position,
        JournalStep original,
        JournalStep replayed,
        List<ReplayDivergence> found)
    {
        var where = $"{flow} row {(position + 1).ToString(CultureInfo.InvariantCulture)} " +
            $"(instance {ordinal.ToString(CultureInfo.InvariantCulture)})";

        Differ(found, where, "step key", Key(original), Key(replayed),
            "A replay that committed a different step, scope or attempt did not follow the " +
            "same control flow.");

        Differ(found, where, "capability", $"{original.CapabilityId}@{original.CapabilityVersion}",
            $"{replayed.CapabilityId}@{replayed.CapabilityVersion}",
            "A replay that invoked a different capability is not a replay.");

        Differ(found, where, "outcome", original.Outcome.ToString(), replayed.Outcome.ToString(),
            "A step that failed once and succeeded on replay makes the journal a record of " +
            "neither run.");

        Differ(found, where, "result payload", original.ResultJson ?? "(none)",
            replayed.ResultJson ?? "(none)",
            "Step inputs and outputs must be byte-identical — that is the contract in 06 §5.");

        Differ(found, where, "ctx.UtcNow", Instant(original.Nondeterminism.UtcNow),
            Instant(replayed.Nondeterminism.UtcNow),
            "ctx.UtcNow must return the instant the journal captured. A value in the replay's " +
            "own epoch means the step read the clock instead of the capture.");

        Differ(found, where, "ctx.NewId()", Ids(original.Nondeterminism.NewIds),
            Ids(replayed.Nondeterminism.NewIds),
            "ctx.NewId() must return the ids the journal captured, in the order it captured " +
            "them. A fresh id, an extra id or a missing one are all divergence.");

        Differ(found, where, "ctx.Random seed",
            Seed(original.Nondeterminism.RandomSeed), Seed(replayed.Nondeterminism.RandomSeed),
            "ctx.Random must be rebuilt from the seed the journal captured, on the step that " +
            "first drew from it.");
    }

    private static void CompareOutcome(
        ObservedRun original, ObservedRun replayed, List<ReplayDivergence> found)
    {
        Differ(found, "the flow's outcome", "result",
            $"{(original.Result.IsSuccess ? "success" : original.Result.Error!.Code)} after " +
                $"{original.Result.CompletedSteps.ToString(CultureInfo.InvariantCulture)} steps",
            $"{(replayed.Result.IsSuccess ? "success" : replayed.Result.Error!.Code)} after " +
                $"{replayed.Result.CompletedSteps.ToString(CultureInfo.InvariantCulture)} steps",
            "A replay that ended differently reproduced nothing, whatever its rows say.");
    }

    private static void Differ(
        List<ReplayDivergence> found,
        string where,
        string what,
        string original,
        string replayed,
        string because)
    {
        if (original != replayed)
        {
            found.Add(new ReplayDivergence("what the journal holds", $"{where} · {what}", original, replayed, because));
        }
    }

    /// <summary>Names what a traced difference means, so the report explains rather than diffs.</summary>
    private static string Why(string original, string replayed)
    {
        if (original.Contains("ctx.UtcNow", StringComparison.Ordinal) &&
            replayed.Contains("ctx.UtcNow", StringComparison.Ordinal))
        {
            return "ctx.UtcNow diverged: the replay read the clock rather than the instant the " +
                "journal captured for this step.";
        }

        if (original.Contains("ctx.NewId()", StringComparison.Ordinal) &&
            replayed.Contains("ctx.NewId()", StringComparison.Ordinal))
        {
            return "ctx.NewId() diverged: the replay minted a fresh id rather than the one the " +
                "journal captured for this step.";
        }

        if (original.Contains("ctx.Random", StringComparison.Ordinal) &&
            replayed.Contains("ctx.Random", StringComparison.Ordinal))
        {
            return "ctx.Random diverged: the generator was not rebuilt from the captured seed, " +
                "so the flow drew a different number.";
        }

        if (original.Contains("static counter", StringComparison.Ordinal) ||
            replayed.Contains("static counter", StringComparison.Ordinal))
        {
            return "Mutable static state is outside the capture envelope entirely, so no " +
                "replay can reproduce it. This is what FLOWX1009 refuses.";
        }

        if (original.Contains("Guid.NewGuid()", StringComparison.Ordinal) ||
            replayed.Contains("Guid.NewGuid()", StringComparison.Ordinal))
        {
            return "An ambient identifier (Guid.NewGuid()) is outside the capture envelope " +
                "entirely, so no replay can reproduce it. This is what FLOWX1008 refuses.";
        }

        if (original.StartsWith("evaluate", StringComparison.Ordinal) ||
            original.Contains(" evaluate ", StringComparison.Ordinal) ||
            original.Contains(" select ", StringComparison.Ordinal))
        {
            return "Control flow diverged: the predicate or selector answered differently " +
                "against the restored state, which is the dependency ADR-0015's first " +
                "amendment created on FLOWX1011.";
        }

        return "The replay did something the original execution did not do at this point.";
    }

    private static string Key(JournalStep row) =>
        $"[{row.Key.Scope.Text}] step {row.Key.StepId.ToString(CultureInfo.InvariantCulture)} " +
        $"attempt {row.Key.Attempt.ToString(CultureInfo.InvariantCulture)}";

    private static string Instant(DateTimeOffset? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture) ?? "(never read)";

    private static string Ids(IReadOnlyList<Guid> ids) =>
        ids.Count == 0 ? "(none minted)" : string.Join(", ", ids);

    private static string Seed(int? seed) =>
        seed?.ToString(CultureInfo.InvariantCulture) ?? "(never drawn)";
}

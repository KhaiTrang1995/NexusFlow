using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using FlowX.Runtime;

namespace FlowX.Durability.Bench;

/// <summary>
/// B8 — what it costs to pick up a flow instance somebody else left behind.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The path measured is <see cref="DurableExecution.ResumeAsync"/>, not a copy of
/// it.</strong> That is the verb a recovering node calls, and it is two store round trips in
/// a required order: raise the fence, then read the history. A rig that timed only
/// <c>ReadResumeFrontierAsync</c> would report roughly half of what a recovery actually pays
/// and would miss the write entirely.
/// </para>
/// <para>
/// <strong>Sequential, because B8 states no rate.</strong> Unlike B7 this budget is a plain
/// per-operation latency, and issuing rehydrations concurrently would measure how well the
/// connection pool shares a server rather than what one takeover costs. Where the rate does
/// matter — a node recovering thousands of instances after a crash — the number that governs
/// is QR2's convergence time, which <c>tests/FlowX.Chaos</c> already measures end to end.
/// </para>
/// <para>
/// <strong>Every instance is rehydrated once.</strong> Reading the same instance repeatedly
/// would measure PostgreSQL's shared buffers after the first pass, which is a real effect but
/// not the one a recovery sweep sees: it walks instances it has never touched.
/// </para>
/// </remarks>
internal static class ResumeArm
{
    /// <summary>Runs the arm.</summary>
    /// <param name="journal">The store under measurement.</param>
    /// <param name="options">What the run was asked to do.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What it measured.</returns>
    public static async Task<ResumeResult> RunAsync(
        IFlowJournal journal,
        BenchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(options);

        var instances = await PrepareAsync(journal, options, cancellationToken).ConfigureAwait(false);

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"    {instances.Length} instances of {options.HistoryDepth} committed steps prepared; rehydrating each once."));

        var samples = new Samples("resume.total", instances.Length);
        var failures = 0;
        var steps = 0L;
        var clock = new Stopwatch();

        foreach (var instanceId in instances)
        {
            clock.Restart();

            var resumed = await DurableExecution
                .ResumeAsync(journal, instanceId, Tokens.Resumer, cancellationToken)
                .ConfigureAwait(false);

            clock.Stop();

            if (resumed.IsFailure)
            {
                failures++;

                continue;
            }

            // Read so that a store which returned an empty frontier cannot look fast. The
            // count is reported: a rehydration that carried no history is not the operation
            // this budget is about.
            steps += resumed.Value.Frontier?.Committed.Count ?? 0;

            samples.Add(clock.Elapsed);
        }

        samples.Freeze();

        return new ResumeResult(
            samples,
            options.HistoryDepth,
            StepsRead: steps,
            Failures: failures);
    }

    /// <summary>
    /// Builds instances that have got somewhere, outside the measured window.
    /// </summary>
    private static async Task<Guid[]> PrepareAsync(
        IFlowJournal journal,
        BenchOptions options,
        CancellationToken cancellationToken)
    {
        var instances = new Guid[options.Resumes];

        for (var i = 0; i < instances.Length; i++)
        {
            var instanceId = Guid.CreateVersion7();

            var started = await journal.StartAsync(
                new FlowInstanceStart
                {
                    InstanceId = instanceId,
                    FlowId = "bench.resume",
                    FlowVersion = "1.0.0",
                    Token = Tokens.Writer,
                    CorrelationId = "corr-" + instanceId.ToString("n")[..8],
                },
                cancellationToken).ConfigureAwait(false);

            if (started.IsFailure)
            {
                throw new InvalidOperationException(
                    $"The rig could not open instance {i}: {started.Error.Code}. Nothing was measured.");
            }

            for (var stepId = 0; stepId < options.HistoryDepth; stepId++)
            {
                var committed = await journal.CommitAsync(
                    new StepCommit
                    {
                        Key = StepKey.First(instanceId, stepId),
                        Token = Tokens.Writer,
                        CapabilityId = "bench.step",
                        CapabilityVersion = "1.0.0",
                        Outcome = JournalOutcome.Success,
                        Duration = TimeSpan.FromMilliseconds(1),
                        Result = JournalPayload.Of(
                            new BenchResult(
                                "ref-" + stepId.ToString(CultureInfo.InvariantCulture), 42.50m, "EUR", true),
                            BenchJson.Default),
                        StateBag = JournalPayload.Of(
                            new BenchState(
                                "step-" + stepId.ToString(CultureInfo.InvariantCulture), stepId, "corr"),
                            BenchJson.Default),
                    },
                    cancellationToken).ConfigureAwait(false);

                if (committed.IsFailure)
                {
                    throw new InvalidOperationException(
                        $"The rig could not build a history for instance {i}: {committed.Error.Code}.");
                }
            }

            instances[i] = instanceId;
        }

        return instances;
    }
}

/// <summary>What the B8 arm measured.</summary>
/// <param name="Total">Fence plus frontier read, per instance.</param>
/// <param name="HistoryDepth">Committed steps each instance carried.</param>
/// <param name="StepsRead">
/// How many step rows came back in total. Reported because a frontier read that returned
/// nothing is fast and is not a rehydration.
/// </param>
/// <param name="Failures">Instances the journal refused to resume. Any at all invalidate the run.</param>
internal sealed record ResumeResult(
    Samples Total,
    int HistoryDepth,
    long StepsRead,
    int Failures)
{
    /// <summary>The arm, as the results document records it.</summary>
    /// <returns>A JSON object.</returns>
    public JsonObject ToJson() => new()
    {
        ["budget"] = "B8",
        ["what"] = "Flow instance rehydration: DurableExecution.ResumeAsync (fence, then frontier read)",
        ["historyDepth"] = HistoryDepth,
        ["stepsRead"] = StepsRead,
        ["failures"] = Failures,
        ["total"] = Total.ToJson(),
    };
}

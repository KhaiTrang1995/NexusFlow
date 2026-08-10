using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace FlowX.Durability.Bench;

/// <summary>
/// B7 — what a durable step commit costs at the rate the budget names.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The budget is rate-qualified, so the rig offers the rate.</strong> "15 ms p99 @
/// 5 000 commits/s/node" is two claims, and measuring the first without the second is the
/// easiest way to pass it: a single writer issuing one commit at a time will report a fine
/// p99 at forty commits a second and prove nothing about a loaded node. Commits are therefore
/// issued against a schedule — operation <c>i</c> is due at <c>i / rate</c> — and the achieved
/// rate is reported beside the latency so a run that could not keep up is visible.
/// </para>
/// <para>
/// <strong>Two latencies, and the gated one is the pessimistic one.</strong> <em>Service</em>
/// is issue-to-complete: what the store took. <em>Response</em> is due-to-complete: what a
/// caller waiting on the schedule saw, including any time the operation spent queued because
/// every writer was busy. Reporting only service is the coordinated-omission mistake — when
/// the store slows down, fewer operations are issued, and the ones that are issued are timed
/// from the moment the store was ready for them, so the p99 <em>improves</em> under overload.
/// Response latency is what B7 is gated on for that reason.
/// </para>
/// </remarks>
internal static class CommitArm
{
    /// <summary>Committed steps per instance in the synthetic corpus.</summary>
    /// <remarks>
    /// A four-step flow with a retry apiece, which is <c>docs/14-Performance.md</c>'s own B1
    /// shape. Kept small on purpose: a node committing eighty boundaries into one instance
    /// would measure a history depth no flow in <c>samples/</c> has.
    /// </remarks>
    private const int Depth = 8;

    /// <summary>Runs the arm.</summary>
    /// <param name="journal">The store under measurement.</param>
    /// <param name="options">What the run was asked to do.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What it measured.</returns>
    public static async Task<CommitResult> RunAsync(
        IFlowJournal journal,
        BenchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(options);

        var total = options.Warmup + options.Commits;
        var instances = await OpenAsync(journal, (total / Depth) + 1, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"    {instances.Length} instances opened; offering {options.Commits} commits " +
                $"at {options.Rate}/s across {options.Writers} writers " +
                $"(after {options.Warmup} discarded)."));

        var next = -1;
        var refusals = new ConcurrentQueue<string>();
        var perWriter = new Writer[options.Writers];
        var clock = Stopwatch.StartNew();

        async Task WriteAsync(int writer)
        {
            var service = new Samples("commit.service", (options.Commits / options.Writers) + 1);
            var response = new Samples("commit.response", (options.Commits / options.Writers) + 1);
            var firstIssue = TimeSpan.MaxValue;
            var lastFinish = TimeSpan.MinValue;

            perWriter[writer] = new Writer(service, response, firstIssue, lastFinish);

            while (true)
            {
                var op = Interlocked.Increment(ref next);

                if (op >= total)
                {
                    return;
                }

                var due = TimeSpan.FromSeconds((double)op / options.Rate);

                await PaceAsync(clock, due, cancellationToken).ConfigureAwait(false);

                var issued = clock.Elapsed;

                var committed = await journal.CommitAsync(
                    Commit(instances[op % instances.Length], op / instances.Length),
                    cancellationToken).ConfigureAwait(false);

                var finished = clock.Elapsed;

                if (committed.IsFailure)
                {
                    refusals.Enqueue(committed.Error.Code);

                    continue;
                }

                if (op < options.Warmup)
                {
                    continue;
                }

                service.Add(issued > finished ? TimeSpan.Zero : finished - issued);
                response.Add(finished - due);

                // The achieved rate is the measured operations over the wall clock they
                // actually occupied -- first issue to last finish, per writer, merged. NOT
                // over their DUE times: a node that has fallen behind finishes long after
                // the schedule said it should, so dividing by the offered window would
                // report the rate the rig asked for rather than the one the store managed,
                // which is the single number the whole arm turns on.
                if (issued < firstIssue)
                {
                    firstIssue = issued;
                }

                lastFinish = finished;

                perWriter[writer] = new Writer(service, response, firstIssue, lastFinish);
            }
        }

        await Task.WhenAll(
                Enumerable.Range(0, options.Writers).Select(w => Task.Run(() => WriteAsync(w), cancellationToken)))
            .ConfigureAwait(false);

        var merged = Merge(perWriter);

        var window = perWriter.Where(static w => w.LastFinish > w.FirstIssue) is var active && active.Any()
            ? active.Max(static w => w.LastFinish) - active.Min(static w => w.FirstIssue)
            : TimeSpan.Zero;

        return new CommitResult(
            merged.Service,
            merged.Response,
            OfferedRate: options.Rate,
            AchievedRate: window > TimeSpan.Zero ? merged.Response.Count / window.TotalSeconds : 0,
            Writers: options.Writers,
            Refusals: [.. refusals]);
    }

    /// <summary>One writer's contribution: its samples and the window it covered.</summary>
    private sealed record Writer(
        Samples Service,
        Samples Response,
        TimeSpan FirstIssue,
        TimeSpan LastFinish);

    /// <summary>
    /// Waits until an operation is due, without pretending to a precision the platform has.
    /// </summary>
    /// <remarks>
    /// A timer on Linux resolves to roughly a millisecond, so anything closer than that is
    /// issued immediately and the slip lands in the response latency. That direction is the
    /// safe one: the pacer's own jitter can only make the gated number worse, never better.
    /// Spinning the last millisecond instead would burn a core per writer and change what the
    /// database is competing with, which is a worse trade on a four-core runner.
    /// </remarks>
    private static async Task PaceAsync(Stopwatch clock, TimeSpan due, CancellationToken cancellationToken)
    {
        var remaining = due - clock.Elapsed;

        if (remaining > TimeSpan.FromMilliseconds(1))
        {
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    private static (Samples Service, Samples Response) Merge(Writer[] perWriter)
    {
        var service = new Samples("commit.service", 0);
        var response = new Samples("commit.response", 0);

        foreach (var writer in perWriter)
        {
            if (writer is null)
            {
                continue;
            }

            service.AddRange(writer.Service);
            response.AddRange(writer.Response);
        }

        service.Freeze();
        response.Freeze();

        return (service, response);
    }

    /// <summary>Opens the instances the arm commits into, before any timing starts.</summary>
    /// <remarks>
    /// <c>StartAsync</c> is a different write with a different cost, and B7 is about the step
    /// boundary. Leaving instance creation inside the measured window would fold one into the
    /// other and neither number would be the budget's.
    /// </remarks>
    private static async Task<Guid[]> OpenAsync(
        IFlowJournal journal,
        int count,
        CancellationToken cancellationToken)
    {
        var instances = new Guid[count];

        for (var i = 0; i < count; i++)
        {
            var instanceId = Guid.CreateVersion7();

            var started = await journal.StartAsync(
                new FlowInstanceStart
                {
                    InstanceId = instanceId,
                    FlowId = "bench.commit",
                    FlowVersion = "1.0.0",
                    Token = Tokens.Writer,
                    CorrelationId = "corr-" + instanceId.ToString("n")[..8],
                },
                cancellationToken).ConfigureAwait(false);

            if (started.IsFailure)
            {
                throw new InvalidOperationException(
                    $"The rig could not open instance {i} of {count}: {started.Error.Code}. " +
                    "Nothing was measured.");
            }

            instances[i] = instanceId;
        }

        return instances;
    }

    private static StepCommit Commit(Guid instanceId, int stepId) => new()
    {
        Key = StepKey.First(instanceId, stepId),
        Token = Tokens.Writer,
        CapabilityId = "bench.step",
        CapabilityVersion = "1.0.0",
        Outcome = JournalOutcome.Success,
        Duration = TimeSpan.FromMilliseconds(1),
        Result = JournalPayload.Of(
            new BenchResult("ref-" + stepId.ToString(CultureInfo.InvariantCulture), 42.50m, "EUR", true),
            BenchJson.Default),
        StateBag = JournalPayload.Of(
            new BenchState("step-" + stepId.ToString(CultureInfo.InvariantCulture), stepId, "corr"),
            BenchJson.Default),
    };
}

/// <summary>What the B7 arm measured.</summary>
/// <param name="Service">Issue-to-complete, the store's own cost.</param>
/// <param name="Response">Due-to-complete, what a caller on the schedule saw. The gated one.</param>
/// <param name="OfferedRate">Commits per second the rig asked for.</param>
/// <param name="AchievedRate">Commits per second it managed.</param>
/// <param name="Writers">How many concurrent writers offered them.</param>
/// <param name="Refusals">
/// Error codes the journal answered. Any at all invalidate the run: a refused commit does
/// almost none of the work a committed one does, so a tail full of them is a fast p99 for a
/// store that was not writing anything.
/// </param>
internal sealed record CommitResult(
    Samples Service,
    Samples Response,
    int OfferedRate,
    double AchievedRate,
    int Writers,
    IReadOnlyList<string> Refusals)
{
    /// <summary>The arm, as the results document records it.</summary>
    /// <returns>A JSON object.</returns>
    public JsonObject ToJson() => new()
    {
        ["budget"] = "B7",
        ["what"] = "Durable step commit (PostgreSQL), one node, several writers in flight",
        ["offeredRatePerSecond"] = OfferedRate,
        ["achievedRatePerSecond"] = Math.Round(AchievedRate, 1, MidpointRounding.AwayFromZero),
        ["writers"] = Writers,
        ["refusals"] = Refusals.Count,
        ["refusalCodes"] = new JsonArray([.. Refusals.Distinct(StringComparer.Ordinal).Select(code => (JsonNode)code!)]),
        ["service"] = Service.ToJson(),
        ["response"] = Response.ToJson(),
    };
}

/// <summary>The fencing tokens the rig writes under.</summary>
/// <remarks>
/// Fixed rather than acquired from a lease store: the rig is one node and never contends, and
/// bringing a lease store in would price a second plugin inside a journal budget.
/// </remarks>
internal static class Tokens
{
    /// <summary>The token every commit in the B7 arm carries.</summary>
    public static FencingToken Writer { get; } = new(1);

    /// <summary>The token a rehydrating node fences with in the B8 arm.</summary>
    public static FencingToken Resumer { get; } = new(2);
}

using System.Diagnostics;
using System.Threading.Channels;
using FlowX.Observability;
using FlowX.Runtime;

namespace FlowX.Hosting;

/// <summary>
/// One pass over each stream subscription this node serves: read under a bound, window on event
/// time, start a flow per closed window, and checkpoint the prefix that is finished with.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the stream engine, and it is a driver rather than a second runtime.</strong>
/// It reaches a flow through
/// <see cref="FlowHost.RunAsync{TIn}(ExecutionPlan, IStepDispatcher, FlowInvocation, TIn, Guid, CancellationToken)"/>,
/// which is the same <c>FlowEngine.ExecuteAsync</c> an HTTP request reaches, with the minted
/// instance id replaced by the one the window derives. <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0003-execution-profiles.md">ADR-0003</a>
/// accepted a cost to avoid a second runtime path; nothing here forks one. What is new is
/// upstream of the flow — a bounded channel, a watermark and a checkpoint — and downstream of it
/// there is nothing new at all.
/// </para>
/// <para>
/// <strong>Backpressure is the subject, and it is enforced by not reading.</strong>
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/06-Execution-Engine.md">06 §10</a>
/// draws a bounded channel that pauses consumption at the source when it fills, and that is
/// literally what <see cref="ReadIntoAsync"/> does: it computes the room left in the channel and
/// issues no read at all when there is none. A record the source has not been asked for is a
/// record in Redis rather than in this process, which is the only sense in which memory is
/// bounded — a channel with <c>BoundedChannelFullMode.Wait</c> alone would still let a read that
/// was already issued materialise a whole batch.
/// </para>
/// <para>
/// <strong>Two bounds, and they are the whole memory story.</strong> The channel holds at most
/// <c>FlowXOptions.StreamChannelCapacity</c> records; open windows hold at most
/// <c>FlowXOptions.StreamMaxResidentRecords</c>. Nothing else here accumulates per record except
/// the checkpoint ledger, which is bounded by the sum of the two. Exceeding the resident bound is
/// a refusal (<see cref="StreamErrors.WindowOverflow"/>), not an eviction: a window emitted
/// without some of its records is an aggregate that is quietly wrong, and this engine would
/// rather stop.
/// </para>
/// <para>
/// <strong>State survives a pass and does not survive a process.</strong> Open windows live in the
/// <see cref="StreamWindowAssigner"/> this object keeps per subscription, because a window is
/// wider than a pass. They are not journaled: a restart reads the checkpoint, re-reads from it,
/// and rebuilds identical windows, because assignment is a pure function of event time
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0055-a-window-names-the-instance-it-starts.md">ADR-0055</a>).
/// A window whose flow had already committed derives the same id and is refused by the journal's
/// primary key. What a node death actually costs is in that record, and it is not nothing.
/// </para>
/// <para>
/// <strong>One reader per subscription, under a lease</strong>, for
/// <c>FlowChangeScan</c>'s reason and a stronger one: two readers would each hold half of every
/// window.
/// </para>
/// </remarks>
public sealed class FlowStreamScan
{
    private readonly FlowHost _host;
    private readonly FlowStreamCatalog _subscriptions;
    private readonly IStreamSource _source;
    private readonly IStreamCheckpointStore _checkpoints;
    private readonly IStreamSideOutput _sideOutput;
    private readonly FlowDurability _durability;
    private readonly FlowXOptions _options;
    private readonly LeasePolicy _policy;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, SubscriptionState> _state = [];

    /// <summary>Builds a pass over one node's registered stream subscriptions.</summary>
    /// <param name="host">Where a window is started, so it is counted and drained.</param>
    /// <param name="subscriptions">Which subscriptions this node serves.</param>
    /// <param name="source">The stream.</param>
    /// <param name="checkpoints">Where progress is kept.</param>
    /// <param name="sideOutput">Where a record too late for its window goes.</param>
    /// <param name="durability">The journal, whose primary key refuses a rebuilt window, and the lease store.</param>
    /// <param name="options">The validated host options.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public FlowStreamScan(
        FlowHost host,
        FlowStreamCatalog subscriptions,
        IStreamSource source,
        IStreamCheckpointStore checkpoints,
        IStreamSideOutput sideOutput,
        FlowDurability durability,
        FlowXOptions options)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(sideOutput);
        ArgumentNullException.ThrowIfNull(durability);
        ArgumentNullException.ThrowIfNull(options);

        _host = host;
        _subscriptions = subscriptions;
        _source = source;
        _checkpoints = checkpoints;
        _sideOutput = sideOutput;
        _durability = durability;
        _options = options;
        _policy = FlowDurability.PolicyFor(options);
    }

    /// <summary>Whether this host has anything to read.</summary>
    /// <remarks>
    /// <see cref="FlowChangeScan.IsEnabled"/>'s two reasons: nothing registered, or no journal —
    /// and without a journal the id a window derives is inert, so every rebuild after a restart
    /// would aggregate the same window a second time.
    /// </remarks>
    public bool IsEnabled => _subscriptions.Count > 0 && _host.IsDurabilityConfigured;

    /// <summary>Runs one pass over every subscription and returns what it did.</summary>
    /// <param name="ct">Cancels the pass, and every flow it started.</param>
    /// <returns>The counts. A pass that found nothing is the ordinary result.</returns>
    public async ValueTask<StreamScanReport> RunOnceAsync(CancellationToken ct = default)
    {
        using var span = FlowXTelemetry.Source.StartActivity("stream scan", ActivityKind.Internal);

        if (!IsEnabled || _host.IsDraining)
        {
            return Tagged(span, StreamScanReport.Nothing);
        }

        var report = StreamScanReport.Nothing;

        foreach (var registration in _subscriptions.Registrations)
        {
            report = report.Add(await ReadAsync(registration, ct).ConfigureAwait(false));
        }

        return Tagged(span, report);
    }

    /// <summary>One subscription's share of one pass, under a lease nobody else holds.</summary>
    private async Task<StreamScanReport> ReadAsync(StreamRegistration registration, CancellationToken ct)
    {
        var state = StateFor(registration);

        if (state.Stopped is { } stopped)
        {
            // A trimmed stream or an overflowed window is not a transient failure and the next
            // pass would fail the same way. The subscription stays stopped, visibly, until a
            // process restart or an operator does something about it.
            return StreamScanReport.Nothing with { Error = stopped };
        }

        var acquired = await DurableLease
            .AcquireAsync(_durability.Leases, registration.SubscriptionLease, _options.NodeName, _policy, ct)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {
            return StreamScanReport.Nothing with { Contended = 1 };
        }

        var lease = acquired.Value;

        try
        {
            return await PumpAsync(registration, state, ct).ConfigureAwait(false);
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The pass itself: a reader that stops when the channel is full, and a consumer that windows
    /// what it reads.
    /// </summary>
    /// <remarks>
    /// <strong>The two run concurrently, which is what makes the bound observable.</strong> A
    /// sequential read-then-process loop is bounded too — by the batch size — but it never
    /// exercises the case the design is about: a consumer slower than a producer. Running them
    /// together means the reader really does stall against a full channel, and
    /// <c>StreamBackpressureTests</c> asserts on how many records the source was asked for while
    /// it did.
    /// </remarks>
    private async Task<StreamScanReport> PumpAsync(
        StreamRegistration registration, SubscriptionState state, CancellationToken ct)
    {
        if (await ResumeAsync(registration, state, ct).ConfigureAwait(false) is { } refused)
        {
            return StreamScanReport.Nothing with { Error = Stop(state, refused) };
        }

        var channel = Channel.CreateBounded<StreamRecord>(new BoundedChannelOptions(
            _options.StreamChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        using var pass = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var reader = ReadIntoAsync(registration, state, channel, pass.Token);
        var report = await ConsumeAsync(registration, state, channel, pass.Token).ConfigureAwait(false);

        // The consumer stops on the first refusal it cannot make progress past; the reader is
        // told, so a stopped subscription does not leave a task pulling from Redis.
        await pass.CancelAsync().ConfigureAwait(false);

        var read = await reader.ConfigureAwait(false);

        return report with { Error = report.Error ?? read };
    }

    /// <summary>Reads the checkpoint once per process, and refuses a stream trimmed past it.</summary>
    private async ValueTask<Error?> ResumeAsync(
        StreamRegistration registration, SubscriptionState state, CancellationToken ct)
    {
        if (state.Resumed)
        {
            return null;
        }

        var checkpoint = await _checkpoints
            .ReadAsync(registration.Subscription, ct)
            .ConfigureAwait(false);

        if (checkpoint.IsFailure)
        {
            return checkpoint.Error;
        }

        state.Position = checkpoint.Value;
        state.Committed = checkpoint.Value;
        state.Resumed = true;

        return null;
    }

    /// <summary>
    /// The source half: read only into the room that exists, and never ask for more than that.
    /// </summary>
    private async Task<Error?> ReadIntoAsync(
        StreamRegistration registration,
        SubscriptionState state,
        Channel<StreamRecord> channel,
        CancellationToken ct)
    {
        var budget = _options.StreamReadBudget;

        try
        {
            while (!ct.IsCancellationRequested && budget > 0)
            {
                var room = _options.StreamChannelCapacity - channel.Reader.Count;

                if (room <= 0)
                {
                    // This is the pause. Nothing is buffered here and nothing is read: the
                    // backlog stays in the source, which is where 06 §10 says it belongs.
                    await channel.Writer.WaitToWriteAsync(ct).ConfigureAwait(false);

                    continue;
                }

                var take = Math.Min(room, budget);
                var read = await _source
                    .ReadAsync(registration.Subscription, state.Position, take, ct)
                    .ConfigureAwait(false);

                if (read.IsFailure)
                {
                    return read.Error;
                }

                if (read.Value.Count == 0)
                {
                    break;
                }

                foreach (var record in read.Value)
                {
                    await channel.Writer.WriteAsync(record, ct).ConfigureAwait(false);

                    state.Position = record.Position;
                }

                budget -= read.Value.Count;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            // The consumer stopped, or the host is shutting down. Nothing is lost: the checkpoint
            // is behind every record this pass read, so the next one re-reads them.
            return null;
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// The windowing half: admit, close, run, settle, checkpoint.
    /// </summary>
    private async Task<StreamScanReport> ConsumeAsync(
        StreamRegistration registration,
        SubscriptionState state,
        Channel<StreamRecord> channel,
        CancellationToken ct)
    {
        var report = StreamScanReport.Nothing;

        try
        {
            await foreach (var record in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                switch (state.Windows.Admit(record))
                {
                    case StreamAdmission.Windowed:
                        report = report with { Read = report.Read + 1 };
                        break;

                    case StreamAdmission.Late:
                        report = report with { Read = report.Read + 1, Late = report.Late + 1 };

                        var routed = await _sideOutput
                            .OnLateAsync(registration.Subscription, record, state.Windows.Watermark, ct)
                            .ConfigureAwait(false);

                        if (routed.IsFailure)
                        {
                            // The checkpoint stays behind this record, so it is offered again
                            // rather than dropped — which is the whole point of a side output.
                            return report with { Error = routed.Error };
                        }

                        state.Windows.SettleLate(record.Position);
                        break;

                    default:
                        return report with
                        {
                            Error = Stop(
                                state,
                                StreamErrors.WindowOverflow(
                                    state.Windows.Resident, _options.StreamMaxResidentRecords)),
                        };
                }

                report = report.Add(await CloseAsync(registration, state, ct).ConfigureAwait(false));

                if (report.Error is not null)
                {
                    return report;
                }
            }

            return report.Add(await CommitAsync(registration, state, force: true, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return report;
        }
    }

    /// <summary>Runs every window the watermark closed, up to the declared parallelism.</summary>
    private async Task<StreamScanReport> CloseAsync(
        StreamRegistration registration, SubscriptionState state, CancellationToken ct)
    {
        var closed = state.Windows.TakeClosed();

        if (closed.Count == 0)
        {
            return StreamScanReport.Nothing;
        }

        var report = StreamScanReport.Nothing;

        using var degree = new SemaphoreSlim(registration.Window.Parallelism);

        var running = closed
            .Select(window => StartAsync(registration, window, degree, ct))
            .ToArray();

        foreach (var (window, outcome) in closed.Zip(await Task.WhenAll(running).ConfigureAwait(false)))
        {
            report = report.Add(outcome.Report);

            if (outcome.Settled)
            {
                state.Windows.Settle(window.Start);
            }
        }

        return report.Add(await CommitAsync(registration, state, force: false, ct).ConfigureAwait(false));
    }

    /// <summary>One closed window, from its records to a disposition.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The tenant is the records', and a window that mixes tenants is refused.</strong> A
    /// window is one flow execution, and a flow executes as one tenant; aggregating two tenants'
    /// records into one instance would hand one of them the other's data through a path no policy
    /// sees. A deployment that isolates partitions the stream per tenant, or declares a
    /// subscription per tenant — it does not aggregate across them.
    /// </para>
    /// <para>
    /// <strong>A window whose flow already committed is deduplicated, not repeated.</strong>
    /// <c>journal.instance_exists</c> is the ordinary answer after any restart, and it is what
    /// makes the checkpoint safe to commit late.
    /// </para>
    /// </remarks>
    private async Task<WindowOutcome> StartAsync(
        StreamRegistration registration,
        ClosedWindow window,
        SemaphoreSlim degree,
        CancellationToken ct)
    {
        await degree.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var one = StreamScanReport.Nothing with { Windows = 1 };

            if (TenantOf(window) is not { } tenant)
            {
                return new WindowOutcome(
                    one with
                    {
                        Error = new Error(
                            "stream.mixed_tenants",
                            $"Window [{window.Start:O}, {window.End:O}) over " +
                            $"'{registration.Subscription.Source}' holds records from more than " +
                            "one tenant. One window is one flow execution and a flow executes as " +
                            "one tenant, so aggregating them would hand one tenant another's " +
                            "records. Partition the stream per tenant.",
                            ErrorCategory.Forbidden),
                    },
                    Settled: false);
            }

            var instanceId = registration.InstanceIdFor(window.Start, window.End);

            var result = await _host
                .RunAsync(
                    registration.Flow.Plan,
                    registration.Flow.Dispatcher,

                    // The correlation id is the window's derived instance, because a window has no
                    // inbound request to inherit one from and its records may have come from many
                    // producers. The tenant is attested: it was read out of the records the source
                    // served, which is provenance rather than a claim.
                    new FlowInvocation(
                        instanceId.ToString("d"),
                        instanceId.ToString(),
                        tenant.Value,
                        TenantAttested: tenant.Value is not null),
                    new StreamWindowBatch(
                        registration.Subscription.Source, window.Start, window.End, window.Records),
                    instanceId,
                    ct)
                .ConfigureAwait(false);

            return DispositionFor(result) switch
            {
                Disposition.Deduplicated => new WindowOutcome(
                    one with { Deduplicated = 1 }, Settled: true),

                Disposition.Held => new WindowOutcome(one with { Held = 1 }, Settled: false),

                _ => new WindowOutcome(one with { Started = 1 }, Settled: true),
            };
        }
        finally
        {
            degree.Release();
        }
    }

    /// <summary>Commits the settled prefix, at most as often as the declaration asked for.</summary>
    private async ValueTask<StreamScanReport> CommitAsync(
        StreamRegistration registration, SubscriptionState state, bool force, CancellationToken ct)
    {
        var elapsed = Stopwatch.GetElapsedTime(state.LastCommit);

        if (!force && elapsed < registration.Window.CheckpointInterval)
        {
            return StreamScanReport.Nothing;
        }

        if (state.Windows.TakeCheckpoint() is not { } position)
        {
            return StreamScanReport.Nothing;
        }

        var committed = await _checkpoints
            .CommitAsync(registration.Subscription, position, ct)
            .ConfigureAwait(false);

        if (committed.IsFailure)
        {
            return StreamScanReport.Nothing with { Error = committed.Error };
        }

        state.Committed = position;
        state.LastCommit = Stopwatch.GetTimestamp();

        return StreamScanReport.Nothing with { Checkpointed = 1 };
    }

    /// <summary>The tenant every record in a window agrees on, or null when they do not.</summary>
    private static Tenant? TenantOf(ClosedWindow window)
    {
        var tenant = window.Records[0].TenantId;

        foreach (var record in window.Records)
        {
            if (!string.Equals(record.TenantId, tenant, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return new Tenant(tenant);
    }

    private SubscriptionState StateFor(StreamRegistration registration)
    {
        lock (_gate)
        {
            if (!_state.TryGetValue(registration.SubscriptionLease, out var state))
            {
                state = new SubscriptionState(new StreamWindowAssigner(
                    registration.Window, _options.StreamMaxResidentRecords));

                _state[registration.SubscriptionLease] = state;
            }

            return state;
        }
    }

    private static Error Stop(SubscriptionState state, Error error)
    {
        state.Stopped = error;

        return error;
    }

    /// <summary>Whether the checkpoint may move past a window, given what the runtime did.</summary>
    /// <remarks>
    /// <c>FlowChangeScan.DispositionFor</c>'s classification unchanged, and for its reasons: a
    /// flow that ran and failed as a value has happened, and only a refusal that journaled
    /// nothing holds progress.
    /// </remarks>
    private static Disposition DispositionFor(FlowExecutionResult result)
    {
        if (result.IsSuccess || result.IsSuspended)
        {
            return Disposition.Started;
        }

        return result.Error!.Code switch
        {
            DurabilityErrors.InstanceExistsCode => Disposition.Deduplicated,

            DurabilityErrors.LeaseHeldCode
                or DurabilityErrors.LeaseLostCode
                or DurabilityErrors.FencedOutCode
                or HostDrainingCode
                or DurabilityNotConfiguredCode
                or TenantErrors.TenantRequiredCode
                or TenantErrors.CrossTenantDeniedCode
                or TenantErrors.RateLimitedCode
                or TenantErrors.QuotaExhaustedCode
                or TenantErrors.SaturatedCode
                or TenantErrors.FairnessUnavailableCode => Disposition.Held,

            _ => Disposition.Started,
        };
    }

    private const string HostDrainingCode = "host.draining";

    private const string DurabilityNotConfiguredCode = "flow.durability_not_configured";

    private static StreamScanReport Tagged(Activity? span, in StreamScanReport report)
    {
        if (span is not null)
        {
            span.SetTag("flowx.scan.read", report.Read);
            span.SetTag("flowx.scan.windows", report.Windows);
            span.SetTag("flowx.scan.started", report.Started);
            span.SetTag("flowx.scan.deduplicated", report.Deduplicated);
            span.SetTag("flowx.scan.late", report.Late);
        }

        return report;
    }

    private enum Disposition
    {
        Started,
        Deduplicated,
        Held,
    }

    /// <summary>A tenant that may legitimately be null, distinguished from "they disagree".</summary>
    private readonly record struct Tenant(string? Value);

    private sealed record WindowOutcome(StreamScanReport Report, bool Settled);

    /// <summary>
    /// What one subscription carries between passes: its windows, its position, and whether it has
    /// stopped.
    /// </summary>
    /// <remarks>
    /// <strong>Per process, and deliberately not journaled.</strong> Every field here is
    /// reconstructible from the checkpoint and the source — which is the claim ADR-0055 makes and
    /// the reason there is no window-state schema.
    /// </remarks>
    private sealed class SubscriptionState(StreamWindowAssigner windows)
    {
        public StreamWindowAssigner Windows { get; } = windows;

        public StreamPosition? Position { get; set; }

        public StreamPosition? Committed { get; set; }

        public bool Resumed { get; set; }

        public Error? Stopped { get; set; }

        public long LastCommit { get; set; } = Stopwatch.GetTimestamp();
    }
}

/// <summary>What one stream pass read and did.</summary>
/// <remarks>Counts rather than records, for <see cref="ChangeScanReport"/>'s reason.</remarks>
public sealed record StreamScanReport
{
    /// <summary>A pass that had nothing to do, and the base for one that did.</summary>
    public static StreamScanReport Nothing { get; } = new();

    /// <summary>How many records the pass admitted.</summary>
    public int Read { get; init; }

    /// <summary>How many windows the watermark closed.</summary>
    public int Windows { get; init; }

    /// <summary>How many windows started a flow.</summary>
    public int Started { get; init; }

    /// <summary>
    /// How many windows an instance already existed for — the expected number after a restart,
    /// and zero on a steady state.
    /// </summary>
    public int Deduplicated { get; init; }

    /// <summary>How many windows were left for the next pass because nothing recorded them.</summary>
    public int Held { get; init; }

    /// <summary>How many records arrived after their window had closed and went to the side output.</summary>
    public int Late { get; init; }

    /// <summary>How many times the checkpoint moved.</summary>
    public int Checkpointed { get; init; }

    /// <summary>How many subscriptions another node was already reading.</summary>
    public int Contended { get; init; }

    /// <summary>Why the pass itself failed, when it did.</summary>
    public Error? Error { get; init; }

    /// <summary>Sums two reports, keeping the first error seen.</summary>
    /// <param name="other">The report to add.</param>
    /// <returns>The sum.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public StreamScanReport Add(StreamScanReport other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new StreamScanReport
        {
            Read = Read + other.Read,
            Windows = Windows + other.Windows,
            Started = Started + other.Started,
            Deduplicated = Deduplicated + other.Deduplicated,
            Held = Held + other.Held,
            Late = Late + other.Late,
            Checkpointed = Checkpointed + other.Checkpointed,
            Contended = Contended + other.Contended,
            Error = Error ?? other.Error,
        };
    }
}

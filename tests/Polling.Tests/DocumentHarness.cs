using System.Globalization;
using System.Security.Claims;
using System.Text;
using FlowX;
using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;
using FlowX.Testing;

namespace Polling.Tests;

/// <summary>
/// A host for a flow that declares <see cref="ExecutionProfile.Durable"/> and polls.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists for the reason <c>tests/Workflow.Tests</c>' <c>OnboardingHarness</c>
/// exists</strong>, and the reason is asserted there rather than restated here:
/// <see cref="FlowTestHost"/> has no journal seam, every <c>RunAsync</c> on it reaches the
/// <c>FlowEngine.ExecuteAsync</c> overload that passes <c>durable: null</c>, and the engine
/// refuses a durable plan with no instance rather than running it ephemerally. A poll needs
/// more than a journal on top of that: it needs the timer sweep, because the gap between two
/// attempts is a parked row and something has to come back for it.
/// </para>
/// <para>
/// <strong>What is real here is everything except the two things a test has to own.</strong>
/// The engine, the compiled plan, the pooled context, the compensation stack, the deadline
/// check, the lease, the fencing token, the journal writes and <c>FlowTimerScan</c> itself are
/// the production ones — the journal, the lease store and the timer index are the conformance
/// suite's reference implementations, held to the same contract PostgreSQL is. This type
/// contributes an <see cref="IStepDispatcher"/> in front of the generated one and a trace.
/// </para>
/// <para>
/// <strong>The clock is virtual and nothing sleeps.</strong> A four-hour poll is driven by
/// moving <see cref="Clock"/> to the instant the instance itself recorded — read back off the
/// journal row, never computed here — and running one sweep. That is what makes "waiting costs
/// nothing" measurable rather than merely asserted: the elapsed wall-clock time of the whole
/// class is milliseconds, and the flow's own clock moves hours.
/// </para>
/// </remarks>
internal sealed class DocumentHarness
{
    private readonly Dictionary<string, CapabilityStandIn> _substitutions = new(StringComparer.Ordinal);

    private DocumentHarness(FlowXOptions options, TimeSpan perPage)
    {
        Options = options;
        Ocr = new InMemoryOcrService(new ClockProvider(Clock), perPage);
        Index = new InMemoryRecoveryIndex(Journal);
        Timers = new InMemoryTimerIndex(Journal);
        Durability = new FlowDurability(Journal, Leases, Index, Timers);
        Host = new FlowHost(new FlowEngine(Clock), options, Durability);
    }

    /// <summary>
    /// The caller every document in this file is processed as.
    /// </summary>
    /// <remarks>
    /// <c>document.process</c> names two permissions between its five capabilities, and the
    /// engine decides each stance against the invocation's principal before the step is
    /// dispatched. Listed rather than granted wholesale, so a capability added with a third
    /// fails here naming the grant it needs.
    /// </remarks>
    internal static ClaimsPrincipal Intake { get; } = TestPrincipal.Holding(
        "document.read",
        "document.write");

    /// <summary>The OCR provider the capabilities write to. Assert on this for effects.</summary>
    public InMemoryOcrService Ocr { get; }

    /// <summary>The clock the engine and the provider both read.</summary>
    public FlowTestClock Clock { get; } = new();

    /// <summary>The journal every step boundary commits to.</summary>
    public InMemoryFlowJournal Journal { get; } = new();

    /// <summary>Where exclusive ownership and fencing tokens come from.</summary>
    public InMemoryLeaseStore Leases { get; } = new();

    /// <summary>The one query a recovery scan needs, over the same journal.</summary>
    public InMemoryRecoveryIndex Index { get; }

    /// <summary>The one query a timer sweep needs, over the same journal.</summary>
    public InMemoryTimerIndex Timers { get; }

    /// <summary>The host every invocation goes through.</summary>
    public FlowHost Host { get; }

    /// <summary>The stores, as the host and the sweeps both see them.</summary>
    public FlowDurability Durability { get; }

    /// <summary>The validated host options.</summary>
    public FlowXOptions Options { get; }

    /// <summary>The instance row, as a store hands it back.</summary>
    public FlowInstanceRecord Instance => Journal.Instances[0];

    /// <summary>
    /// Starts a harness whose OCR provider takes <paramref name="perPage"/> for each page.
    /// </summary>
    /// <remarks>
    /// The provider's speed is the only knob a test turns. The poll's schedule and budget are
    /// the sample's own declared <c>Waits</c>, so what is being measured is the flow that
    /// ships rather than one arranged to pass.
    /// </remarks>
    public static DocumentHarness Create(TimeSpan perPage) => new(
        new FlowXOptions
        {
            ApplicationName = "Polling.Tests",
            NodeName = "test-node",
            ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
        },
        perPage);

    /// <summary>Makes a capability fail wherever it appears, so a test can drive the unwind.</summary>
    public DocumentHarness Substitute(string capabilityId, Error error)
    {
        _substitutions[capabilityId] = (_, _) => ValueTask.FromResult(StepOutcome.Failed(error));

        return this;
    }

    /// <summary>Runs <c>document.process</c> up to wherever it gets — normally its first park.</summary>
    public async ValueTask<DocumentRun> StartAsync(ProcessDocument document, CancellationToken ct)
    {
        var trace = new DurableTrace();

        var result = await Host
            .RunAsync(
                ProcessDocumentFlow.Plan,
                Wrap(trace),
                new FlowInvocation(
                    "corr-doc", document.DocumentId, TenantId: null, Deadline: null, Principal: Intake),
                document,
                ProcessDocumentFlow.Projection,
                ct)
            .ConfigureAwait(false);

        // TryGetValue rather than Value: a suspended flow's projection has not run, and reading
        // it throws — which is the behaviour, not something to work around.
        result.TryGetValue(out var projected);

        return new DocumentRun(result.Outcome, result.IsSuccess ? projected : null, trace);
    }

    /// <summary>
    /// Moves the clock to the instant the parked instance itself recorded, and runs one sweep.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The instant is read back rather than computed.</strong> The schedule this sample
    /// declares is jittered, so the gap before attempt <em>n</em> is a draw rather than a
    /// number a test could restate — and a test that restated it would be asserting against its
    /// own copy of the arithmetic instead of against what the instance asked for. Reading the
    /// row is also the only thing a timer sweep does, so this drives the flow exactly the way a
    /// deployment does.
    /// </para>
    /// <para>
    /// One tick past, not exactly on: <c>FlowTimerScan</c> asks for instances due <em>at or
    /// before</em> now, and the engine then compares the recorded instant against the same
    /// clock. Landing exactly on the instant is correct either way; landing past it removes any
    /// question about which comparison is which.
    /// </para>
    /// </remarks>
    public ValueTask<TimerRun> WakeWhenDueAsync(CancellationToken ct)
    {
        if (Instance.Wake is { } wake && wake.At > Clock.UtcNow)
        {
            Clock.Advance(wake.At - Clock.UtcNow + TimeSpan.FromTicks(1));
        }

        return WakeAsync(ct);
    }

    /// <summary>Runs one timer sweep over the same stores, and reports what it ran.</summary>
    /// <remarks>
    /// The trace comes back with the report because that is the only place a woken instance's
    /// steps are observable: the sweep resumes through <c>FlowHost.ResumeAsync</c> and hands its
    /// caller counts, exactly as it does in a deployment.
    /// </remarks>
    public async ValueTask<TimerRun> WakeAsync(CancellationToken ct)
    {
        var trace = new DurableTrace();

        var report = await new FlowTimerScan(Host, Catalogue(trace), Durability, Options, Clock)
            .RunOnceAsync(ct)
            .ConfigureAwait(false);

        return new TimerRun(report, trace);
    }

    /// <summary>Every committed row for one step, in commit order.</summary>
    /// <param name="stepId">The flat index, as the compiled plan numbers it.</param>
    public IReadOnlyList<JournalStep> RowsFor(int stepId) =>
        [.. Journal.Instances.Count == 0
            ? []
            : StepsOf().Where(step => step.Key.StepId == stepId)];

    /// <summary>Every committed row of the one instance, in commit order.</summary>
    public IReadOnlyList<JournalStep> StepsOf()
    {
        var frontier = Journal
            .ReadResumeFrontierAsync(Instance.InstanceId, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        return frontier.Value.Committed;
    }

    /// <summary>Whether a live lease is held on the instance.</summary>
    /// <remarks>
    /// The whole of the sample's claim, asked of the store: a parked document holds no lease,
    /// so nothing in the cluster is charged for it and any node may pick it up next.
    /// </remarks>
    public bool HoldsLease() =>
        Leases
            .ReadAsync(Instance.InstanceId, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult()
            .IsSuccess;

    /// <summary>What a sweep resolves a journal row's flow id and version back into.</summary>
    private FlowCatalog Catalogue(DurableTrace trace) =>
        new FlowCatalog().Add(ProcessDocumentFlow.Plan, Wrap(trace));

    private RecordingDispatcher Wrap(DurableTrace trace) => new(
        ProcessDocumentFlow.Plan,
        new ProcessDocumentFlow.Dispatcher(
            new CancelOcrJob(Ocr),
            new CheckOcrStatus(Ocr),
            new EscalateToManualReview(Ocr),
            new ExtractFields(Ocr),
            new UploadToOcr(Ocr)),
        trace,
        _substitutions);

    /// <summary>Lets the OCR stand-in read the same virtual clock the engine does.</summary>
    /// <remarks>
    /// The provider takes a <see cref="TimeProvider"/> so that a deployment hands it the system
    /// clock and a test hands it this. Without it the stand-in would finish jobs on wall-clock
    /// time while the flow moved through four virtual hours in a millisecond, and every test
    /// here would be measuring the test runner's speed.
    /// </remarks>
    private sealed class ClockProvider(IClock clock) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => clock.UtcNow;
    }
}

/// <summary>A capability's stand-in, with the same job the generated dispatcher's case does.</summary>
internal delegate ValueTask<StepOutcome> CapabilityStandIn(FlowContext ctx, CancellationToken ct);

/// <summary>What one invocation of <c>document.process</c> did.</summary>
internal sealed class DocumentRun
{
    internal DocumentRun(FlowExecutionResult result, DocumentResult? projected, DurableTrace trace)
    {
        Result = result;
        Projected = projected;
        Trace = trace;
    }

    /// <summary>The engine's own result.</summary>
    public FlowExecutionResult Result { get; }

    /// <summary>The projected output, or <c>null</c> when the flow did not finish.</summary>
    public DocumentResult? Projected { get; }

    /// <summary>What the flow did.</summary>
    public DurableTrace Trace { get; }

    /// <summary>True when the flow parked rather than finishing or failing.</summary>
    public bool IsSuspended => Result.IsSuspended;

    /// <summary>The business error, or <c>null</c>.</summary>
    public Error? Error => Result.Error;

    /// <inheritdoc />
    public override string ToString() =>
        (Result.IsSuspended ? "suspended" : Result.IsSuccess ? "completed" : "failed: " + Result.Error!.Code)
        + "\n" + Trace;
}

/// <summary>What one timer sweep found, and what the instance it woke then did.</summary>
internal sealed class TimerRun
{
    internal TimerRun(TimerScanReport report, DurableTrace trace)
    {
        Report = report;
        Trace = trace;
    }

    /// <summary>The sweep's own counts.</summary>
    public TimerScanReport Report { get; }

    /// <summary>What the woken instance ran.</summary>
    public DurableTrace Trace { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"examined {Report.Examined}, woke {Report.Woken}\n{Trace}";
}

/// <summary>What one invocation did, in the order it did it.</summary>
/// <remarks>
/// Entries hold strings and integers only. The engine's contexts are pooled and reset the
/// instant a flow returns, so a trace that captured one would read as empty at best and as the
/// next invocation's data at worst.
/// </remarks>
internal sealed class DurableTrace
{
    private readonly List<string> _entries = [];
    private readonly List<string> _steps = [];
    private readonly List<string> _compensations = [];
    private readonly Lock _sync = new();

    /// <summary>Everything the engine asked, in the order it asked.</summary>
    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_sync)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>The steps that ran, in order. Includes the one that failed.</summary>
    public IReadOnlyList<string> Executed
    {
        get
        {
            lock (_sync)
            {
                return [.. _steps];
            }
        }
    }

    /// <summary>The compensations that ran, in unwind order.</summary>
    public IReadOnlyList<string> Compensated
    {
        get
        {
            lock (_sync)
            {
                return [.. _compensations];
            }
        }
    }

    /// <summary>How many times a capability ran in this invocation.</summary>
    public int Count(string capabilityId)
    {
        lock (_sync)
        {
            var count = 0;

            foreach (var name in _steps)
            {
                if (string.Equals(name, capabilityId, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        lock (_sync)
        {
            if (_entries.Count == 0)
            {
                return "(nothing ran)";
            }

            var text = new StringBuilder();

            foreach (var entry in _entries)
            {
                text.Append(entry).Append('\n');
            }

            return text.ToString();
        }
    }

    internal void RecordStep(string flowId, int index, string name)
    {
        lock (_sync)
        {
            _steps.Add(name);
            _entries.Add(Line(flowId, index, "step", name));
        }
    }

    internal void RecordCompensation(string flowId, int index, string name)
    {
        lock (_sync)
        {
            _compensations.Add(name);
            _entries.Add(Line(flowId, index, "undo", name));
        }
    }

    internal void RecordControl(string flowId, int index, string kind, string name)
    {
        lock (_sync)
        {
            _entries.Add(Line(flowId, index, kind, name));
        }
    }

    private static string Line(string flowId, int index, string kind, string name) =>
        string.Create(CultureInfo.InvariantCulture, $"{flowId}[{index}] {kind}: {name}");
}

/// <summary>
/// A dispatcher that stands in front of the generated one, records what the engine asked, and
/// answers for the capabilities a test replaced.
/// </summary>
internal sealed class RecordingDispatcher : IStepDispatcher
{
    private readonly ExecutionPlan _plan;
    private readonly IStepDispatcher _inner;
    private readonly DurableTrace _trace;
    private readonly Dictionary<string, CapabilityStandIn> _substitutions;

    internal RecordingDispatcher(
        ExecutionPlan plan,
        IStepDispatcher inner,
        DurableTrace trace,
        Dictionary<string, CapabilityStandIn> substitutions)
    {
        _plan = plan;
        _inner = inner;
        _trace = trace;
        _substitutions = substitutions;
    }

    /// <inheritdoc />
    public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
    {
        var step = _plan.Graph[stepIndex];

        _trace.RecordStep(_plan.Flow.Id, stepIndex, StepName(step));

        return step.Capability is { } capability && _substitutions.TryGetValue(capability.Id, out var standIn)
            ? standIn(ctx, ct)
            : _inner.ExecuteAsync(stepIndex, ctx, ct);
    }

    /// <inheritdoc />
    public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
    {
        var compensation = _plan.Graph[stepIndex].Compensation;

        _trace.RecordCompensation(_plan.Flow.Id, stepIndex, compensation?.Id ?? "compensation");

        return compensation is not null && _substitutions.TryGetValue(compensation.Id, out var standIn)
            ? standIn(ctx, ct)
            : _inner.CompensateAsync(stepIndex, ctx, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Records the answer as well as forwarding it, because a poll's <c>until</c> reaches the
    /// dispatcher through this and the number of times it is asked is the difference between an
    /// engine that parks between attempts and one that loops.
    /// </remarks>
    public bool Evaluate(int stepIndex, FlowContext ctx)
    {
        var held = _inner.Evaluate(stepIndex, ctx);

        _trace.RecordControl(
            _plan.Flow.Id,
            stepIndex,
            _plan.Graph[stepIndex].Kind == StepKind.Poll ? "poll" : "branch",
            held ? "satisfied" : "again");

        return held;
    }

    /// <inheritdoc />
    public int Select(int stepIndex, FlowContext ctx) => _inner.Select(stepIndex, ctx);

    /// <inheritdoc />
    public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
        _inner.BeginIteration(stepIndex, ctx);

    /// <inheritdoc />
    public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
        _inner.EnterIteration(stepIndex, in source, iteration, ctx);

    /// <inheritdoc />
    public SubFlowSource BeginSubFlow(int stepIndex, FlowContext ctx) => _inner.BeginSubFlow(stepIndex, ctx);

    /// <inheritdoc />
    public void EnterSubFlow(int stepIndex, in SubFlowSource source, FlowContext child) =>
        _inner.EnterSubFlow(stepIndex, in source, child);

    /// <inheritdoc />
    public StepJournalEntry DescribeStep(int stepIndex, FlowContext ctx) => _inner.DescribeStep(stepIndex, ctx);

    /// <inheritdoc />
    public void RestoreState(FlowContext ctx, string stateBagJson) => _inner.RestoreState(ctx, stateBagJson);

    /// <inheritdoc />
    public JournalPayload DescribeInput(object? input) => _inner.DescribeInput(input);

    private static string StepName(StepNode step) => step.Kind switch
    {
        StepKind.Capability => step.Capability!.Id,
        StepKind.Emit => "emit:" + step.EventType,
        StepKind.Fail => "fail",
        _ => step.Kind.ToString().ToLowerInvariant(),
    };
}

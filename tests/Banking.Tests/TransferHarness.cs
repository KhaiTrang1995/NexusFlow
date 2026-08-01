using Banking;
using FlowX;
using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;

namespace Banking.Tests;

/// <summary>
/// One durable run of <c>transfer.execute</c>: a real journal, a real lease, the real
/// engine, and a trace of what the flow did.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists because <c>FlowTestHost</c> cannot run a <c>Durable</c> flow.</strong>
/// The shipped test host builds a <c>FlowEngine</c> and calls it with no
/// <c>DurableExecution</c>, and the engine refuses a durable plan without one — every test
/// in this project first came back <c>flow.durability_not_configured</c> and
/// <c>(nothing ran)</c>. <c>docs/23-Testing-Strategy.md §5</c> says the kit ships no journal
/// deliberately; what it does not say is that the consequence is a whole execution profile
/// the kit cannot exercise — and it is the profile <c>ADR-0003</c> names for "payments,
/// sagas and long-running processes". That gap is written up in the sample's README.
/// </para>
/// <para>
/// <strong>What stands in is closer to production than the test host, not further from
/// it.</strong> The composition below is <see cref="FlowHost"/> over a
/// <see cref="FlowDurability"/> — which is exactly what <c>MapFlow</c> resolves out of the
/// container and calls. The lease is acquired, the instance row is opened, every step
/// boundary is committed, and the outbox is staged by the step's own commit. The only
/// substitutions are the reference stores in place of PostgreSQL (ADR-0009 makes them the
/// semantic definition of a journal, and <c>plugins/FlowX.Postgres</c> passes the same
/// suite) and, per test, a capability made to fail.
/// </para>
/// <para>
/// The trace is this project's own, because <c>FlowX.Testing</c>'s is produced by an
/// internal dispatcher wrapper that cannot be reached from outside that assembly. It
/// records the same two things: the capability ids the engine dispatched, and the ones it
/// compensated, both in order.
/// </para>
/// </remarks>
internal sealed class TransferHarness
{
    private readonly Dictionary<string, Error> _substitutions = new(StringComparer.Ordinal);

    /// <summary>The ledger the capabilities are constructed over.</summary>
    public InMemoryLedger Ledger { get; } = new();

    /// <summary>The payments register the last step writes to.</summary>
    public InMemorySettlementRegister Register { get; } = new();

    /// <summary>The journal every step boundary is committed to.</summary>
    public InMemoryFlowJournal Journal { get; } = new();

    /// <summary>Where the instance's exclusive ownership comes from.</summary>
    public InMemoryLeaseStore Leases { get; } = new();

    /// <summary>Capability ids the engine dispatched, in order.</summary>
    public List<string> Executed { get; } = [];

    /// <summary>Capability ids the engine compensated, in order.</summary>
    public List<string> Compensated { get; } = [];

    /// <summary>Makes a capability fail wherever the flow reaches it.</summary>
    /// <param name="capabilityId">The id as the compiled plan carries it.</param>
    /// <param name="error">The business error the stand-in returns.</param>
    /// <returns>This harness.</returns>
    public TransferHarness Substitute(string capabilityId, Error error)
    {
        _substitutions[capabilityId] = error;

        return this;
    }

    /// <summary>Runs the flow and projects its declared output.</summary>
    /// <param name="input">What a trigger would have deserialised.</param>
    /// <param name="idempotencyKey">The caller's key, which becomes the transfer id.</param>
    /// <param name="ct">Cancels the run.</param>
    public ValueTask<FlowExecutionResult<TransferResult>> RunAsync(
        ExecuteTransfer input,
        string idempotencyKey,
        CancellationToken ct) =>
        Host().RunAsync(
            ExecuteTransferFlow.Plan,
            Dispatcher(),
            new FlowInvocation("corr-" + idempotencyKey, idempotencyKey, "tenant-1"),
            input,
            ExecuteTransferFlow.Projection,
            ct);

    /// <summary>Every instance this harness's journal holds.</summary>
    public IReadOnlyList<FlowInstanceRecord> Instances => Journal.Instances;

    /// <summary>The steps committed for an instance, in commit order.</summary>
    public async ValueTask<IReadOnlyList<JournalStep>> StepsAsync(Guid instanceId, CancellationToken ct)
    {
        var frontier = await Journal.ReadResumeFrontierAsync(instanceId, ct).ConfigureAwait(false);

        return frontier.Value.Committed;
    }

    /// <summary>The events staged for an instance.</summary>
    public async ValueTask<IReadOnlyList<OutboxRecord>> OutboxAsync(Guid instanceId, CancellationToken ct)
    {
        var outbox = await Journal.ReadOutboxAsync(instanceId, ct).ConfigureAwait(false);

        return outbox.Value;
    }

    private FlowHost Host() => new(
        new FlowEngine(SystemClock.Instance),
        new FlowXOptions { ApplicationName = "Banking", NodeName = "test-node" },
        new FlowDurability(Journal, Leases));

    private Recording Dispatcher() => new(
        new ExecuteTransferFlow.Dispatcher(
            postCredit: new PostCredit(Ledger),
            postDebit: new PostDebit(Ledger),
            recordSettlement: new RecordSettlement(Register),
            resolveCorrespondent: new ResolveCorrespondent(new InMemoryCorrespondentDirectory()),
            reverseCredit: new ReverseCredit(Ledger),
            reverseDebit: new ReverseDebit(Ledger),
            screenSanctions: new ScreenSanctions(new InMemorySanctionsScreening()),
            validateTransfer: new ValidateTransfer(Ledger)),
        this);

    /// <summary>
    /// The generated dispatcher, with a trace around it and a stand-in in front of it.
    /// </summary>
    /// <remarks>
    /// A decorator rather than a replacement: every call that is not substituted reaches the
    /// generated code, including <c>DescribeStep</c> — which is what stages the emitted event
    /// and is therefore the one member a hand-written double would have silently disabled.
    /// </remarks>
    private sealed class Recording : IStepDispatcher
    {
        private readonly ExecuteTransferFlow.Dispatcher _inner;
        private readonly TransferHarness _harness;

        public Recording(ExecuteTransferFlow.Dispatcher inner, TransferHarness harness)
        {
            _inner = inner;
            _harness = harness;
        }

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            var step = ExecuteTransferFlow.Plan.Graph.Steps[stepIndex];
            var id = Name(step);

            _harness.Executed.Add(id);

            return step.Capability is { } capability
                && _harness._substitutions.TryGetValue(capability.Id, out var error)
                ? ValueTask.FromResult(StepOutcome.Failed(error))
                : _inner.ExecuteAsync(stepIndex, ctx, ct);
        }

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            var step = ExecuteTransferFlow.Plan.Graph.Steps[stepIndex];

            _harness.Compensated.Add(step.Compensation?.Id ?? "?");

            return _inner.CompensateAsync(stepIndex, ctx, ct);
        }

        public bool Evaluate(int stepIndex, FlowContext ctx) => _inner.Evaluate(stepIndex, ctx);

        public int Select(int stepIndex, FlowContext ctx) => _inner.Select(stepIndex, ctx);

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            _inner.BeginIteration(stepIndex, ctx);

        public FlowContext EnterIteration(
            int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            _inner.EnterIteration(stepIndex, source, iteration, ctx);

        public SubFlowSource BeginSubFlow(int stepIndex, FlowContext ctx) =>
            _inner.BeginSubFlow(stepIndex, ctx);

        public void EnterSubFlow(int stepIndex, in SubFlowSource source, FlowContext child) =>
            _inner.EnterSubFlow(stepIndex, source, child);

        public StepJournalEntry DescribeStep(int stepIndex, FlowContext ctx) =>
            _inner.DescribeStep(stepIndex, ctx);

        // RestoreState is not forwarded: the generated dispatcher does not implement it,
        // because this flow's DescribeStep writes no state bag. The interface's default
        // throws, which is the correct answer — a bag that was never committed cannot be
        // asked for, and a resumed instance therefore re-enters with an empty one.

        /// <summary>
        /// The same name <c>FlowTestTrace</c> uses: the capability id verbatim, and a
        /// prefixed form for everything that is not one.
        /// </summary>
        private static string Name(StepNode step) => step.Kind switch
        {
            StepKind.Capability => step.Capability!.Id,
            StepKind.Emit => "emit:" + step.EventType,
            StepKind.Fail => "fail",
            _ => step.Kind.ToString().ToLowerInvariant(),
        };
    }
}

using System.Security.Claims;
using Banking;
using FlowX;
using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;
using FlowX.Testing;

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

    /// <summary>
    /// The budget <c>Policies.Admission</c>'s rate limit is counted against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A double, and it has to be one — but the reason is not the usual one.</strong>
    /// The sample's limit is real and is enforced against a shared store in a deployment; what
    /// this project cannot do is reach one, for the same reason it stands in a reference journal
    /// for PostgreSQL. What holds a real limiter to the contract is
    /// <c>RateLimiterConformance</c>, derived by Redis and by PostgreSQL, and what holds *this*
    /// to it is that the engine cannot tell the difference — it calls the same seam.
    /// </para>
    /// <para>
    /// Generous by default, so that a test about a transfer is not also a test about admission.
    /// <see cref="WithPermits"/> narrows it, and one test does exactly that.
    /// </para>
    /// </remarks>
    public FixedBudgetLimiter Limiter { get; private set; } = new(int.MaxValue);

    /// <summary>Narrows the admission budget to a number a test can exhaust.</summary>
    /// <param name="permits">How many transfers this harness admits before refusing.</param>
    /// <returns>This harness.</returns>
    public TransferHarness WithPermits(int permits)
    {
        Limiter = new FixedBudgetLimiter(permits);

        return this;
    }

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

    /// <summary>
    /// Makes a capability fail its first <paramref name="attempts"/> dispatches and succeed
    /// after that.
    /// </summary>
    /// <param name="capabilityId">The id as the compiled plan carries it.</param>
    /// <param name="attempts">How many dispatches fail before the real capability runs.</param>
    /// <param name="error">The business error the stand-in returns while it is failing.</param>
    /// <remarks>
    /// <see cref="Substitute"/> can only fail for ever, which cannot tell a retried step from
    /// an unretried one: both end with the flow failing. "The provider was down and then came
    /// back" is the ordinary transient case and the one <c>Policies.ExternalRead</c>'s
    /// <c>Retry(attempts: 3)</c> exists for, so it needs a double that can recover — the same
    /// gap <c>RecordingDispatcher.FailCompensationForAttempts</c> filled for the undo.
    /// </remarks>
    public TransferHarness SubstituteForAttempts(string capabilityId, int attempts, Error error)
    {
        _budgets[capabilityId] = attempts;
        _substitutions[capabilityId] = error;

        return this;
    }

    private readonly Dictionary<string, int> _budgets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);

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
            new FlowInvocation(
                "corr-" + idempotencyKey, idempotencyKey, "tenant-1", Deadline: null, Principal: Operator),
            input,
            ExecuteTransferFlow.Projection,
            ct);

    /// <summary>
    /// The caller every transfer in this file runs as.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ExecuteTransferFlow</c>'s capabilities declare four permissions between them, and
    /// the engine decides each stance against the invocation's principal before the step is
    /// dispatched. A harness that supplied none would refuse every transfer at
    /// <c>ScreenSanctions</c> and turn this whole file into one assertion about
    /// authorisation.
    /// </para>
    /// <para>
    /// The four are listed rather than granted wholesale, so that a capability added with a
    /// fifth permission fails here — naming the grant it needs — instead of being waved
    /// through by a caller who holds everything. There is deliberately no <c>TestPrincipal</c>
    /// that satisfies every stance, for exactly this reason.
    /// </para>
    /// </remarks>
    private static ClaimsPrincipal Operator { get; } = TestPrincipal.Holding(
        "compliance:screen",
        "correspondent:read",
        "ledger:post",
        "settlement:write");

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

    /// <summary>Every audit record this harness's transfers produced, in order.</summary>
    /// <remarks>
    /// The sample's own sink, not a double. Three of this flow's steps declare an
    /// <c>Audit</c> and the engine refuses an audited step it cannot record, so a harness that
    /// omitted this would fail every transfer at the debit — which is the behaviour, and is
    /// asserted separately in <c>TransferAuditTests</c>.
    /// </remarks>
    public InMemoryAuditTrail Audit { get; } = new();

    private FlowHost Host() => new(
        new FlowEngine(SystemClock.Instance, rateLimiter: Limiter, audit: Audit),
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

            if (step.Capability is not { } capability ||
                !_harness._substitutions.TryGetValue(capability.Id, out var error))
            {
                return _inner.ExecuteAsync(stepIndex, ctx, ct);
            }

            // A budget means the stand-in is transient: it fails that many dispatches and
            // then steps out of the way. No budget is the old behaviour — fail for ever.
            if (_harness._budgets.TryGetValue(capability.Id, out var budget))
            {
                var made = _harness._attempts.TryGetValue(capability.Id, out var seen) ? seen + 1 : 1;
                _harness._attempts[capability.Id] = made;

                if (made > budget)
                {
                    return _inner.ExecuteAsync(stepIndex, ctx, ct);
                }
            }

            return ValueTask.FromResult(StepOutcome.Failed(error));
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

        // Forwarded, and this line is the whole of what the host needs to record a request:
        // FlowHost asks the dispatcher for the input because it cannot name a JsonTypeInfo
        // for the flow's contract itself. A decorator that stopped here and inherited the
        // interface's default would silently put flow_instance.input back to NULL — which is
        // exactly the state WP-59 found it in — while every other test went on passing.
        public JournalPayload DescribeInput(object? input) => _inner.DescribeInput(input);

        public void RestoreState(FlowContext ctx, string stateBagJson) =>
            _inner.RestoreState(ctx, stateBagJson);

        // Forwarded for DescribeInput's reason, one policy later. The generated DescribeAudit
        // is what composes the request/result document and applies the flow's SensitiveMembers
        // together with the policy's redact list; a decorator that stopped here would inherit
        // the interface's default — JournalPayload.Empty — and every audit record in this bank
        // would carry nothing while still being written, which is the shape of failure the
        // whole policy was declined twice to avoid.
        public JournalPayload DescribeAudit(
            int stepIndex, FlowContext ctx, IReadOnlyList<string> redact) =>
            _inner.DescribeAudit(stepIndex, ctx, redact);

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

/// <summary>
/// A budget with no refill, so a test can say exactly how many callers get in.
/// </summary>
/// <remarks>
/// Deliberately not a token bucket: the real stores refill continuously and
/// <c>RateLimiterConformance</c> holds them to it, and a sample test that waited out a window
/// would be a test about the clock. What this shares with a real store is the only thing the
/// sample's tests are about — the decision and the consumption happen together, and a refusal is
/// a successful call reporting <c>false</c>.
/// </remarks>
internal sealed class FixedBudgetLimiter(int budget) : IRateLimiterStore
{
    private readonly Dictionary<string, int> _taken = new(StringComparer.Ordinal);

    /// <summary>Every key the engine built, in the order it built them.</summary>
    public List<string> Keys { get; } = [];

    /// <inheritdoc />
    public ValueTask<Result<RateLimitVerdict>> TryAcquireAsync(
        string key,
        int permits,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        Keys.Add(key);

        _taken.TryGetValue(key, out var used);
        _taken[key] = ++used;

        var verdict = used <= budget
            ? new RateLimitVerdict(true, budget - used, TimeSpan.Zero)
            : new RateLimitVerdict(false, 0, window);

        return new ValueTask<Result<RateLimitVerdict>>(Result.Ok(verdict));
    }
}

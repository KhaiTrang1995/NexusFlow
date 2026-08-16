using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// A capability-valued fallback against a real database: what the degraded row says, and what
/// a node that picks the instance up does about it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Here rather than only in the runtime suite, because the identity decision is about
/// a table.</strong>
/// <a href="../../docs/adr/ADR-0079-a-fallback-capability-is-a-dispatch-of-its-own.md">ADR-0079</a>
/// §2.2 answers ADR-0078 §3.2 — "two capabilities under one step index would either share a key
/// or need a fifth component, which is a schema change and a migration" — by using the column
/// the schema already has. That claim is only worth anything if the primary key really accepts
/// the rows, the <c>capability_id</c> really comes back, and no migration was needed; a
/// dictionary standing in for a store cannot disagree with any of it, which is the lesson
/// ADR-0015's own preamble records about being accepted on in-memory evidence.
/// </para>
/// <para>
/// The stores are the real ones and the engine is the real one. The only simulation is the node
/// death, which is a lease that is given up without the instance being finished.
/// </para>
/// </remarks>
public sealed class PostgresDegradedStepTests
{
    private const string DeadNode = "node-1";

    /// <summary>The step's own capability. A read, so a fallback over it is legal.</summary>
    private static readonly CapabilityDescriptor Rate =
        CapabilityDescriptor.Create("rating.primary", "1.0.0", isIdempotent: true);

    /// <summary>The second answer. No side effects, which FLOWX1053 requires of a fallback.</summary>
    private static readonly CapabilityDescriptor Secondary =
        CapabilityDescriptor.Create("rating.secondary", "1.0.0", isIdempotent: true);

    private static readonly Error Unavailable =
        new("rating.unavailable", "the rating service is not answering", ErrorCategory.Unavailable);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>The capability type a declared fallback names.</summary>
    private sealed class SecondaryRating;

    /// <summary>What a degraded step answers with.</summary>
    private sealed record Rating(string Score);

    private static ExecutionPlan Plan() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.rated", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
        StepGraph.Create([
            StepNode.ForCapability(
                0,
                Rate,
                policies: PolicyChain.ForStep(
                    PolicySet.Named("degradable").Fallback<SecondaryRating>(), Rate, Secondary)),
            StepNode.ForCapability(1, Rate),
        ]));

    /// <summary>
    /// PostgreSQL accepts both capabilities' rows under one step, and hands the ids back.
    /// </summary>
    /// <remarks>
    /// The primary key is <c>(instance_id, scope, step_id, attempt)</c> and stays that way: the
    /// attempt sequence keeps counting across the two capabilities, so the rows are distinct
    /// without a fifth component, and the <c>capability_id</c> column — already <c>NOT NULL</c>
    /// on every row since the initial schema — is what says which of them answered. Nothing in
    /// <c>Migrations/</c> changed for this, and this test is what makes that a fact rather than
    /// a claim.
    /// </remarks>
    [Fact]
    public async Task ADegradedStepsRowsSurviveTheRealPrimaryKeyAndNameBothCapabilities()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var instance = Guid.NewGuid();
        var plan = Plan();

        var begun = await DurableExecution.BeginAsync(
            schema.Journal, plan, new FlowInvocation("corr-1", instance.ToString(), "acme"),
            instance, new FencingToken(1), input: null, Cancellation);

        begun.IsSuccess.ShouldBeTrue(begun.IsFailure ? begun.Error.ToString() : string.Empty);

        var dispatcher = new DegradingDispatcher { FailPrimary = true, Answer = new Rating("secondary") };

        var result = await new FlowEngine(SystemClock.Instance).ExecuteAsync(
            plan, dispatcher, new FlowInvocation("corr-1", instance.ToString(), "acme"),
            begun.Value, Cancellation);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error!.ToString() : string.Empty);

        var frontier = await schema.Journal.ReadResumeFrontierAsync(instance, Cancellation);

        frontier.Value.Committed
            .Select(static row => (row.Key.StepId, row.Key.Attempt, row.CapabilityId, row.Outcome))
            .ShouldBe([
                (0, 1, "rating.primary", JournalOutcome.Failure),
                (0, 2, "rating.secondary", JournalOutcome.Success),
                (1, 1, "rating.primary", JournalOutcome.Success),
            ],
            "One step, two capabilities, two rows, one four-part key — and an operator " +
            "reading the table can see that the primary failed and the secondary answered " +
            "without joining anything.");
    }

    /// <summary>
    /// A node that takes over mid-degradation asks the fallback, not the primary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reason the identity has to be exact. The first node's rows are two failures, and
    /// without knowing that the second of them is the <em>fallback's</em> the new node would
    /// read "this step has not succeeded" and start again at the primary — spending the
    /// author's attempts on a dependency an earlier node already gave up on, and asking the two
    /// capabilities in the wrong order on every resume.
    /// </para>
    /// <para>
    /// The fence reaching 2 is the safety half, exactly as it is for an ordinary recovery: the
    /// dead node cannot come back and write over the degraded answer this one committed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ANodeThatTakesOverMidDegradationResumesTheFallback()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var instance = Guid.NewGuid();
        var plan = Plan();

        var lease = await DurableLease.AcquireAsync(
            schema.Leases,
            instance,
            DeadNode,
            new LeasePolicy { Ttl = TimeSpan.FromSeconds(30), RenewalInterval = TimeSpan.FromSeconds(10) },
            Cancellation);

        lease.IsSuccess.ShouldBeTrue(lease.IsFailure ? lease.Error.ToString() : string.Empty);

        var invocation = new FlowInvocation("corr-1", instance.ToString(), "acme");

        var begun = await lease.Value.BeginAsync(
            schema.Journal, plan, invocation, cancellationToken: Cancellation);

        begun.IsSuccess.ShouldBeTrue(begun.IsFailure ? begun.Error.ToString() : string.Empty);

        // The first node's primary failed and so did its fallback, and then it died before it
        // could seal anything. The two rows are committed here rather than run through the
        // engine for the reason AbandonAsync commits its one: a node that finishes a flow seals
        // the instance, and an instance that reached a terminal state is one the journal
        // correctly refuses further writes to — so playing the death out through the engine
        // would leave the wrong state. That the engine writes exactly these two rows is what
        // the test above asserts, against this same database.
        foreach (var (attempt, capability) in new[] { (1, "rating.primary"), (2, "rating.secondary") })
        {
            var committed = await schema.Journal.CommitAsync(
                new StepCommit
                {
                    Key = new StepKey(instance, StepScope.Root, 0, attempt),
                    Token = lease.Value.Token,
                    CapabilityId = capability,
                    CapabilityVersion = "1.0.0",
                    Outcome = JournalOutcome.Failure,
                    State = FlowInstanceState.Running,
                },
                Cancellation);

            committed.IsSuccess.ShouldBeTrue(
                committed.IsFailure ? committed.Error.ToString() : string.Empty);
        }

        // The lease goes; the instance does not. A crash reaches the same place a TTL later.
        await lease.Value.DisposeAsync();

        var resumed = await DurableExecution.ResumeAsync(
            schema.Journal, instance, new FencingToken(2), Cancellation);

        resumed.IsSuccess.ShouldBeTrue(resumed.IsFailure ? resumed.Error.ToString() : string.Empty);

        // This node's secondary is healthy — and so is its primary, which is what makes the
        // assertion below about the rule rather than about the double.
        var recovered = new DegradingDispatcher { Answer = new Rating("secondary") };

        var finished = await new FlowEngine(SystemClock.Instance).ExecuteAsync(
            plan, recovered, invocation, resumed.Value, Cancellation);

        finished.IsSuccess.ShouldBeTrue(finished.IsFailure ? finished.Error!.ToString() : string.Empty);

        recovered.FellBackAt.ShouldBe([0], "The degraded path was resumed where it was left.");

        recovered.Executed.ShouldBe(
            [1],
            "Step 0's own capability is never asked again. It had finished failing before this " +
            "node existed, and the fallback's committed row is what says so.");

        var frontier = await schema.Journal.ReadResumeFrontierAsync(instance, Cancellation);

        frontier.Value.Committed
            .Select(static row => (row.Key.StepId, row.Key.Attempt, row.CapabilityId, row.Outcome))
            .ShouldBe([
                (0, 1, "rating.primary", JournalOutcome.Failure),
                (0, 2, "rating.secondary", JournalOutcome.Failure),
                (0, 3, "rating.secondary", JournalOutcome.Success),
                (1, 1, "rating.primary", JournalOutcome.Success),
            ]);

        var record = await schema.Journal.ReadInstanceAsync(instance, Cancellation);

        record.Value.State.ShouldBe(FlowInstanceState.Completed);
        record.Value.Fence.Value.ShouldBe(2, "the second lease issued for this instance.");
    }

    /// <summary>
    /// A dispatcher whose step and whose fallback can each be made to fail.
    /// </summary>
    /// <remarks>
    /// Hand-written, and shaped exactly like what <c>FlowEmitter</c> emits: a switch for the
    /// step and a second one for <c>ExecuteFallbackAsync</c>, with the typed <c>ctx.Set</c> in
    /// the only code that knows the contract. Writing the seam by hand is what proves it is
    /// implementable at all, which is the same reason the forward switch was written by hand
    /// before a generator produced one.
    /// </remarks>
    private sealed class DegradingDispatcher : IStepDispatcher
    {
        private readonly Lock _gate = new();
        private readonly List<int> _executed = [];
        private readonly List<int> _fellBack = [];

        public bool FailPrimary { get; init; }

        public Rating? Answer { get; init; }

        public IReadOnlyList<int> Executed
        {
            get
            {
                lock (_gate)
                {
                    return [.. _executed];
                }
            }
        }

        public IReadOnlyList<int> FellBackAt
        {
            get
            {
                lock (_gate)
                {
                    return [.. _fellBack];
                }
            }
        }

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            lock (_gate)
            {
                _executed.Add(stepIndex);
            }

            return ValueTask.FromResult(
                FailPrimary && stepIndex == 0 ? StepOutcome.Failed(Unavailable) : StepOutcome.Success);
        }

        public ValueTask<StepOutcome> ExecuteFallbackAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(ctx);

            lock (_gate)
            {
                _fellBack.Add(stepIndex);
            }

            if (Answer is not null)
            {
                ctx.Set(Answer);
            }

            return ValueTask.FromResult(StepOutcome.Success);
        }

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no branch step.");

        public int Select(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no switch step.");

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to begin.");

        public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to enter.");
    }
}

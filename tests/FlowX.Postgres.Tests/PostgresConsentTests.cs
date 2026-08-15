using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// A declared <c>Consent</c> under <c>Durable</c>, against a real journal: what a refusal
/// leaves in the table, and what a node that picks the instance up decides.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Here rather than only in the runtime suite, because both facts are about a
/// table.</strong> A stage-2 refusal happens before the dispatch, so the claim is that the
/// journal carries <em>no row at all</em> for the refused step — which a dictionary standing in
/// for a store cannot disprove, and which is the difference between a step that was refused and
/// a step that ran and failed.
/// </para>
/// <para>
/// <strong>And the second fact is the one
/// <a href="../../docs/adr/ADR-0028-identity-arrives-on-the-invocation.md">ADR-0028</a> §2.2
/// makes true by omission.</strong> An instance row keeps no claims, deliberately, so it keeps
/// no purpose either — a resumed instance has nothing to compare. If the engine decided a
/// consent against that absence, every durable flow with a <c>.Delay(...)</c> or a wait before a
/// consent-gated step would resume into a <c>403</c>, and a node restart would become a refusal.
/// The runtime suite asserts that against an ephemeral plan and a flag; this asserts it against
/// a real committed history, which is where the absence actually lives.
/// </para>
/// </remarks>
public sealed class PostgresConsentTests
{
    private static readonly CapabilityDescriptor Register =
        CapabilityDescriptor.Create("patient.register", "1.0.0", isIdempotent: true, "patient-index");

    private static readonly CapabilityDescriptor Store =
        CapabilityDescriptor.Create("records.store", "1.0.0", isIdempotent: true, "record-store");

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// Step 0 runs for anybody; step 1 may only be reached for direct care.
    /// </summary>
    /// <remarks>
    /// Two steps rather than one, so the refusal has something committed behind it. A
    /// consent-gated first step would leave an empty history and could not distinguish "the
    /// gate refused before anything ran" from "nothing ran".
    /// </remarks>
    private static ExecutionPlan Plan() => ExecutionPlan.Create(
        FlowDescriptor.Create("patient.intake", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
        StepGraph.Create([
            StepNode.ForCapability(0, Register),
            StepNode.ForCapability(
                1,
                Store,
                policies: PolicyChain.ForStep(
                    PolicySet.Named("clinical").Consent("treatment"), Store)),
        ]));

    /// <summary>
    /// A refused step commits nothing, and the step before it is on the history.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The absence is the assertion.</strong> Stage 2 runs before the dispatch, so a
    /// refused step never reaches <c>CommitStepAsync</c> — and an operator reading the table
    /// sees an instance that stopped at step 0 rather than one whose step 1 was attempted and
    /// failed. Those are different facts and the repairs differ, which is exactly why the check
    /// is where <a href="../../docs/adr/ADR-0027-authorisation-runs-in-the-step-loop.md">ADR-0027</a>
    /// §2.3 puts the stance beside it.
    /// </para>
    /// <para>
    /// Committed rows are asserted rather than counted, so a change that started writing a
    /// refusal row would be visible as the row it is rather than as a number.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AConsentRefusalCommitsNoRowForTheStepItRefused()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var instance = Guid.NewGuid();
        var plan = Plan();

        // Started for research, and step 1 is declared for treatment.
        var invocation = new FlowInvocation(
            "corr-1", instance.ToString(), "clinic", Purpose: "research");

        var begun = await DurableExecution.BeginAsync(
            schema.Journal, plan, invocation, instance, new FencingToken(1), input: null, Cancellation);

        begun.IsSuccess.ShouldBeTrue(begun.IsFailure ? begun.Error.ToString() : string.Empty);

        var dispatcher = new CountingDispatcher();

        var result = await new FlowEngine(SystemClock.Instance)
            .ExecuteAsync(plan, dispatcher, invocation, begun.Value, Cancellation);

        result.IsFailure.ShouldBeTrue();

        result.Error!.Code.ShouldBe(
            FlowErrors.ConsentPurposeNotCoveredCode,
            "a consent to be treated is not a consent to be studied, and the journal is where " +
            "that decision has to be legible afterwards.");

        dispatcher.Executed.ShouldBe([0], "records.store was never entered.");

        var frontier = await schema.Journal.ReadResumeFrontierAsync(instance, Cancellation);

        frontier.Value.Committed
            .Select(static row => (row.Key.StepId, row.CapabilityId, row.Outcome))
            .ShouldBe([(0, "patient.register", JournalOutcome.Success)],
                "One row, for the step that ran. A refused step has no attempt to record — it " +
                "was never dispatched — and a row saying otherwise would tell an operator the " +
                "record store had been called.");
    }

    /// <summary>
    /// A resumed instance is not re-gated, because the row it was rebuilt from keeps no purpose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The rule that makes a consent-gated step composable with a wait.</strong> The
    /// instance row carries a correlation id, a tenant and a deadline, and no claims — ADR-0028
    /// §2.2, which is a security property rather than an omission: persisting credentials for
    /// the life of a durable instance would authorise a Friday payment with a Monday grant. A
    /// purpose came off the same credential and is not persisted either.
    /// </para>
    /// <para>
    /// So a sweep or a recovery scan has nothing to compare, and the engine does not ask. The
    /// alternative is worse in an obvious way and in a subtle one: every resumed instance would
    /// refuse, and a flow's behaviour would then depend on whether a node happened to survive
    /// long enough to finish it.
    /// </para>
    /// <para>
    /// The first execution here is the one that proves the gate is live at all — it runs for
    /// the declared purpose and commits both steps — so a continuation that also succeeds is
    /// evidence about the continuation rather than about a policy that never refuses. The
    /// refusing half is the test above, against the same plan and the same database.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AContinuationRebuiltFromTheJournalIsNotGatedByAPurposeTheRowCannotKeep()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var plan = Plan();
        var live = Guid.NewGuid();

        var caller = new FlowInvocation("corr-1", live.ToString(), "clinic", Purpose: "treatment");

        var begun = await DurableExecution.BeginAsync(
            schema.Journal, plan, caller, live, new FencingToken(1), input: null, Cancellation);

        begun.IsSuccess.ShouldBeTrue(begun.IsFailure ? begun.Error.ToString() : string.Empty);

        var dispatcher = new CountingDispatcher();

        var served = await new FlowEngine(SystemClock.Instance)
            .ExecuteAsync(plan, dispatcher, caller, begun.Value, Cancellation);

        served.IsSuccess.ShouldBeTrue(served.IsFailure ? served.Error!.ToString() : string.Empty);
        dispatcher.Executed.ShouldBe([0, 1], "the gate is live and this caller satisfied it.");

        // A second instance, resumed by the platform. Nobody is asking for anything, and the
        // row it is rebuilt from carries no purpose — which is what a sweep looks like.
        var swept = Guid.NewGuid();

        var sweepStart = new FlowInvocation("corr-2", swept.ToString(), "clinic");

        var reopened = await DurableExecution.BeginAsync(
            schema.Journal, plan, sweepStart, swept, new FencingToken(1), input: null, Cancellation);

        reopened.IsSuccess.ShouldBeTrue(reopened.IsFailure ? reopened.Error.ToString() : string.Empty);

        var sweeper = new CountingDispatcher();

        var resumed = await new FlowEngine(SystemClock.Instance).ExecuteAsync(
            plan,
            sweeper,
            sweepStart with { IsContinuation = true },
            reopened.Value,
            Cancellation);

        resumed.IsSuccess.ShouldBeTrue(
            resumed.IsFailure ? resumed.Error!.ToString() : string.Empty);

        sweeper.Executed.ShouldBe(
            [0, 1],
            "a timer sweep and a recovery scan carry no purpose because they carry no caller. " +
            "Refusing here would make a wait before a consent-gated step unwritable and would " +
            "turn a node restart into a 403.");

        var frontier = await schema.Journal.ReadResumeFrontierAsync(swept, Cancellation);

        frontier.Value.Committed
            .Select(static row => (row.Key.StepId, row.Outcome))
            .ShouldBe([(0, JournalOutcome.Success), (1, JournalOutcome.Success)]);
    }

    private sealed class CountingDispatcher : IStepDispatcher
    {
        private readonly Lock _gate = new();
        private readonly List<int> _executed = [];

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

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            lock (_gate)
            {
                _executed.Add(stepIndex);
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

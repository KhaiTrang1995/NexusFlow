using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// What the engine does with a declared policy chain: for eight of the nine kinds, nothing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The executable half of <c>docs/diagnostics/FLOWX1032.md</c>.</strong> That page
/// says a declared <c>Timeout</c> arms no clock, a <c>Retry</c> dispatches once, a
/// <c>Cache</c> is never consulted and a <c>RateLimit</c> counts nothing. Those are claims
/// about the engine, and a claim about the engine that only a paragraph makes is the exact
/// failure the diagnostic exists to end — so they are asserted here, against a real
/// <see cref="FlowEngine"/> running a real plan whose steps carry a real
/// <see cref="PolicyChain"/>.
/// </para>
/// <para>
/// <strong>It is written to go red on the day P4 lands</strong>, which is when FLOWX1032 is
/// deleted. Every assertion below is of the form "this did not happen"; the first policy the
/// engine actually applies breaks one of them, and the failure message points at the rule
/// that then has to be narrowed or removed.
/// </para>
/// <para>
/// <strong>The positive control is at the bottom and is not optional.</strong> A file of
/// nothing-happened assertions passes against an engine that does not run at all, which is
/// exactly the vacuous gate <c>docs/21-Quality-Gates.md §2.4</c> refuses. The last test
/// declares the one policy that <em>is</em> executed and asserts it executes.
/// </para>
/// </remarks>
public sealed class PolicyExecutionTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Error Unavailable =
        new("order.validate_failed", "the ledger is down", ErrorCategory.Unavailable);

    private static readonly Error BrokerDown =
        new("inventory.release_failed", "broker down", ErrorCategory.Unavailable);

    /// <summary>
    /// Every kind the DSL offers except the compensation retry, on one step.
    /// </summary>
    /// <remarks>
    /// A zero-length <c>Timeout</c> and a one-permit <c>RateLimit</c> deliberately: if either
    /// were armed, no step in this file would ever complete and no flow would ever run twice.
    /// Choosing values that make the failure loud is what stops these assertions passing
    /// because the numbers happened to be generous.
    /// </remarks>
    private static PolicyChain Everything { get; } = PolicyChain.ForStep(
        PolicySet.Named("everything")
            .RateLimit(permits: 1, TimeSpan.FromHours(1))
            .Idempotency(TimeSpan.FromHours(1))
            .Timeout(TimeSpan.Zero)
            .Retry(attempts: 3)
            .CircuitBreaker(failureRatio: 0.01, breakDuration: TimeSpan.FromHours(1))
            .Bulkhead(maxConcurrency: 1)
            .Cache(TimeSpan.FromHours(1))
            .Audit("financial"),
        Plans.Validate);

    /// <summary>
    /// <c>0 validate (every forward policy) · 1 reserve (undo: release) · 2 capture</c>.
    /// </summary>
    /// <remarks>
    /// Three steps rather than two, because a step that fails is not on its own unwind stack:
    /// the positive control at the bottom needs a <em>later</em> step to fail so that the
    /// reserve it follows is the thing being undone.
    /// </remarks>
    private static ExecutionPlan Plan(PolicyChain? forward = null, PolicyChain? undo = null) =>
        ExecutionPlan.Create(
            FlowDescriptor.Create("order.policy", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(0, Plans.Validate, policies: forward ?? Everything),
                StepNode.ForCapability(1, Plans.Reserve, Plans.Release, compensationPolicies: undo),
                StepNode.ForCapability(2, Plans.Capture),
            ]));

    // ------------------------------------------------------------------ nothing runs

    /// <summary>
    /// A step carrying all eight kinds is dispatched exactly once, and the flow succeeds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single assertion the diagnostic's page rests on. The <c>Timeout</c> is
    /// <see cref="TimeSpan.Zero"/> — an armed one would abort the step before it began — and
    /// the run completes; the <c>Bulkhead</c> admits one caller and there is one; the
    /// <c>Cache</c>, the <c>Idempotency</c> window and the <c>CircuitBreaker</c> are
    /// consulted by nothing.
    /// </para>
    /// <para>
    /// This is the test named on <c>docs/diagnostics/FLOWX1032.md</c> as the rule's take-down
    /// trigger.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task OnlyCompensationRetryIsExecutedAtRunTime()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(Plan(), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeTrue(
            "A zero-length Timeout on step 0 stops nothing, because nothing arms it. If this " +
            "fails, the policy engine has landed and FLOWX1032 is due for deletion — see " +
            "docs/diagnostics/FLOWX1032.md.");

        dispatcher.Executed.ShouldBe(
            [0, 1, 2],
            "One dispatch per step. A Bulkhead of one, a Cache and an Idempotency window are " +
            "declared on step 0 and read by no code.");
    }

    /// <summary>
    /// A declared <c>Retry(3)</c> over a failing step buys exactly one attempt.
    /// </summary>
    /// <remarks>
    /// The forward half of FLOWX1014's subject, from the other side. That rule refuses a
    /// <c>Retry</c> on a capability that is not idempotent, and has always been enforced;
    /// this asserts that the retry it was protecting does not happen. <c>order.validate</c>
    /// declares <c>Idempotent = true</c>, so the declaration is legal and the failure is
    /// <see cref="ErrorCategory.Unavailable"/>, which is in <c>Retry</c>'s own default
    /// retryable set — every precondition an executing retry would need is met, and it still
    /// runs once.
    /// </remarks>
    [Fact]
    public async Task AForwardRetryDoesNotRetry()
    {
        var dispatcher = new RecordingDispatcher().FailAt(0, Unavailable);

        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(
                Plan(PolicyChain.ForStep(PolicySet.Named("r").Retry(attempts: 3), Plans.Validate)),
                dispatcher,
                Plans.Invocation,
                Ct);

        result.IsSuccess.ShouldBeFalse();

        dispatcher.Executed.ShouldBe(
            [0],
            "Three attempts were declared against a retryable failure on an idempotent " +
            "capability, and one was made.");
    }

    /// <summary>
    /// A <c>Cache</c> and a <c>RateLimit</c> survive a second run of the same flow unchanged.
    /// </summary>
    /// <remarks>
    /// Two executions of one plan, which is the only shape that can tell a consulted cache
    /// from an unconsulted one: a cache hit would skip the second dispatch, and a rate limit
    /// of one permit an hour would refuse it.
    /// </remarks>
    [Fact]
    public async Task ACacheIsNotConsultedAndARateLimitCountsNothing()
    {
        var engine = new FlowEngine(new FakeClock(T0));
        var plan = Plan();

        var first = new RecordingDispatcher();
        var second = new RecordingDispatcher();

        (await engine.ExecuteAsync(plan, first, Plans.Invocation, Ct)).IsSuccess.ShouldBeTrue();
        (await engine.ExecuteAsync(plan, second, Plans.Invocation, Ct)).IsSuccess.ShouldBeTrue();

        first.Executed.ShouldBe([0, 1, 2]);

        second.Executed.ShouldBe(
            [0, 1, 2],
            "One permit an hour was declared and two runs went through, and a one-hour cache " +
            "was declared and the second run dispatched anyway.");
    }

    /// <summary>
    /// A stage-7 <c>Audit</c> on a step's own chain is not a compensation policy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The runtime statement of the correction FLOWX1032 exists to make. <c>Audit</c> and
    /// <c>CompensationRetry</c> share <see cref="PolicyStage.Consistency"/>, so a reader who
    /// cuts the gap by stage would expect stage 7 to be "the one that runs". It is not:
    /// <c>PolicyChain.ForStep</c> moves only the compensation retry onto the undo's chain, and
    /// <c>CompensationPolicy.From</c> reads only that kind.
    /// </para>
    /// <para>
    /// So a plan whose only stage-7 policy is an audit reports
    /// <c>HasCompensationPolicies == false</c> — the flag the engine reads before it does any
    /// retry bookkeeping at all — and the audit is executed by nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnAuditIsAStageSevenPolicyAndStillExecutesNowhere()
    {
        var plan = Plan(PolicyChain.ForStep(PolicySet.Named("a").Audit("financial"), Plans.Validate));

        plan.Graph.Steps[0].Policies.Ordered
            .Select(static p => p.Stage)
            .ShouldBe([PolicyStage.Consistency], "Audit is stage 7, the same stage as the retry that runs.");

        plan.HasCompensationPolicies.ShouldBeFalse(
            "Stage is not the cut. The cut is what a policy wraps, and an Audit wraps the " +
            "step — which nothing reads.");

        plan.Graph.Steps[0].CompensationRetry.IsRetrying.ShouldBeFalse();
    }

    // -------------------------------------------------------------- the positive control

    /// <summary>
    /// And the one policy that does execute, on the same plan, executes.
    /// </summary>
    /// <remarks>
    /// Without this the file passes against an engine that never dispatches anything. Step 1
    /// carries the same forward chain as every test above and a <c>CompensationRetry</c> on
    /// its undo; the undo fails once and is dispatched a second time, which is the whole of
    /// the policy engine P2 ships.
    /// </remarks>
    [Fact]
    public async Task TheCompensationRetryOnTheSamePlanDoesExecute()
    {
        var undo = PolicyChain.ForCompensation(
            PolicySet.Named("undo").CompensationRetry(
                attempts: 3,
                Backoff.Exponential(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2))),
            Plans.Release);

        var dispatcher = new RecordingDispatcher()
            .FailAt(2, Unavailable)
            .FailCompensationForAttempts(1, attempts: 1, BrokerDown);

        var plan = Plan(undo: undo);

        plan.HasCompensationPolicies.ShouldBeTrue();

        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(plan, dispatcher, Plans.Invocation, Ct);

        result.Compensation.ShouldBe(CompensationOutcome.Succeeded);

        dispatcher.Compensated.ShouldBe(
            [1, 1],
            "The undo failed once and was dispatched again. This is the assertion that keeps " +
            "every 'nothing happened' above from being a statement about a dead engine.");
    }
}

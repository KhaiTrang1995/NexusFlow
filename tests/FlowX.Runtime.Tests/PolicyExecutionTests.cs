using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// What the engine does with a declared stage-4 policy chain, and the one kind that reaches
/// the step through a different flag.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This file used to assert the opposite of every test above the fold.</strong> It
/// was the executable half of the deleted <c>FLOWX1032</c> while that rule covered eight
/// kinds, and each of its assertions was of the form "this did not happen" — a
/// zero-length <c>Timeout</c> that stopped nothing, a <c>Retry(3)</c> that dispatched once,
/// a <c>Bulkhead(1)</c> that counted nobody. Its own remarks said it was "written to go red
/// on the day P4 lands". It did, and the inversions are recorded on each test.
/// </para>
/// <para>
/// <strong>What runs now is <see cref="PolicyStage.Resilience"/>, whole — and so does
/// everything else.</strong> <c>Timeout</c>, <c>Retry</c>, <c>CircuitBreaker</c> and
/// <c>Bulkhead</c> are executed by <c>FlowEngine</c> over <c>StepNode.StepPolicy</c>, in the
/// fixed nesting
/// <a href="../../docs/adr/ADR-0024-stage-four-is-a-fixed-nesting.md">ADR-0024</a> settles.
/// The four kinds outside it have left this file for the suites that assert them:
/// <c>RateLimit</c> and <c>Idempotency</c> to <c>AdmissionAndIntegrityTests</c>,
/// <c>Cache</c> to <c>CachePolicyTests</c>, <c>Audit</c> to <c>AuditPolicyTests</c>. All four
/// are written to the same rule as this file — every "did not happen" beside a "did".
/// </para>
/// <para>
/// <strong>What is left here about a non-stage-4 kind is the flag, not the behaviour.</strong>
/// An <c>Audit</c> is resolved onto <c>StepNode.StepAudit</c> and counted by
/// <c>ExecutionPlan.HasAuditedSteps</c>, so an audit-only chain must leave
/// <c>HasStepPolicies</c> false — which is the budget-B2 property this file has asserted since
/// there was anything to assert it about, and the one that survives every kind executing.
/// </para>
/// <para>
/// <strong>The positive control is still at the bottom and still not optional.</strong> A
/// file mixing "this happened" and "this did not" passes against an engine that runs nothing
/// only if the second sort is all it contains. The compensation retry, which executed before
/// this package and still does, is asserted last.
/// </para>
/// </remarks>
public sealed class PolicyExecutionTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Error Unavailable =
        new("order.validate_failed", "the ledger is down", ErrorCategory.Unavailable);

    private static readonly Error Invalid =
        new("order.validate_rejected", "the iban is malformed", ErrorCategory.Validation);

    private static readonly Error BrokerDown =
        new("inventory.release_failed", "broker down", ErrorCategory.Unavailable);

    /// <summary>
    /// Every kind the DSL offers except the compensation retry, on one step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A zero-length <c>Timeout</c> deliberately: it stops the step before it begins, which is
    /// the loudest available difference. Choosing values that make the difference loud is what
    /// stops either half of this file passing because the numbers happened to be generous.
    /// </para>
    /// <para>
    /// <strong>The <c>RateLimit</c> and the <c>Idempotency</c> window left this set when stage 1
    /// and stage 3 landed.</strong> They are executed now, so a set carrying them would make
    /// every test below assert stage 4 through two store round trips — and the tests that give
    /// this engine no store would fail at stage 1 before reaching the timeout they are about.
    /// `AdmissionAndIntegrityTests` holds the two kinds that moved.
    /// </para>
    /// </remarks>
    private static PolicyChain Everything { get; } = PolicyChain.ForStep(
        PolicySet.Named("everything")
            .Timeout(TimeSpan.Zero)
            .Retry(attempts: 3)
            .CircuitBreaker(failureRatio: 0.01, breakDuration: TimeSpan.FromHours(1))
            .Bulkhead(maxConcurrency: 1)
            .Cache(TimeSpan.FromHours(1))
            .Audit("financial"),
        Plans.Validate);

    /// <summary>The one kind that leaves <c>HasStepPolicies</c> false.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Narrowed from four kinds to one, and it is no longer inert.</strong> It held a
    /// <c>RateLimit</c>, an <c>Idempotency</c> window and a <c>Cache</c> as well.
    /// <c>RateLimit</c> and <c>Idempotency</c> left when stages 1 and 3 landed —
    /// <c>AdmissionAndIntegrityTests</c> is where they are asserted — and <c>Cache</c> left
    /// when stage 5 did, to <c>CachePolicyTests</c>.
    /// </para>
    /// <para>
    /// What is left is the <c>Audit</c>, and the property this chain now stands for is not
    /// "nothing executes" but "nothing executes <em>on the step-policy path</em>": stage 7
    /// runs after the commit, off <c>ExecutionPlan.HasAuditedSteps</c> and
    /// <c>StepNode.StepAudit</c>, so a chain carrying only an audit must still leave
    /// <c>HasStepPolicies</c> false. That is the budget-B2 assertion this property was always
    /// feeding, and it is the one that survives every kind becoming executable.
    /// </para>
    /// </remarks>
    private static PolicyChain AuditOnly { get; } = PolicyChain.ForStep(
        PolicySet.Named("audit-only").Audit("financial"),
        Plans.Validate);

    /// <summary>
    /// <c>0 validate (the forward chain) · 1 reserve (undo: release) · 2 capture</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three steps rather than two, because a step that fails is not on its own unwind stack:
    /// the positive control at the bottom needs a <em>later</em> step to fail so that the
    /// reserve it follows is the thing being undone.
    /// </para>
    /// <para>
    /// <strong>Step 0's default chain is empty rather than <see cref="AuditOnly"/>.</strong> It
    /// used to default to the inert set, which was free because nothing read it. An audit is
    /// read now, and an audited step with no <c>IAuditSink</c> is refused — so defaulting to
    /// one would make every test below fail at step 0 for a reason none of them is about.
    /// </para>
    /// </remarks>
    private static ExecutionPlan Plan(PolicyChain? forward = null, PolicyChain? undo = null) =>
        ExecutionPlan.Create(
            FlowDescriptor.Create("order.policy", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(0, Plans.Validate, policies: forward),
                StepNode.ForCapability(1, Plans.Reserve, Plans.Release, compensationPolicies: undo),
                StepNode.ForCapability(2, Plans.Capture),
            ]));

    private static PolicyChain Forward(PolicySet set) => PolicyChain.ForStep(set, Plans.Validate);

    // ------------------------------------------------------------------- stage 4 executes

    /// <summary>
    /// A step carrying all eight forward kinds is stopped by the one of them that is armed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Inverted.</strong> This test was <c>OnlyCompensationRetryIsExecutedAtRunTime</c>,
    /// and it asserted <c>result.IsSuccess</c> with the message "a zero-length Timeout on step
    /// 0 stops nothing, because nothing arms it", and <c>dispatcher.Executed == [0, 1, 2]</c>
    /// — one dispatch per step, the whole flow through. It was named on the deleted FLOWX1032's page as
    /// that rule's take-down trigger.
    /// </para>
    /// <para>
    /// A zero-length timeout is degenerate on purpose: it has expired before the step begins,
    /// so nothing is dispatched at all and the loudest possible thing happens. The
    /// <c>Retry(3)</c> beside it is armed too, so the step is attempted three times and times
    /// out three times, and <c>order.validate</c> is never entered once.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AZeroLengthTimeoutStopsTheStepBeforeItBegins()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(Plan(Everything), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeFalse(
            "A zero-length Timeout has no budget left the moment the step is reached.");

        result.Error!.Code.ShouldBe(FlowErrors.StepTimedOutCode);

        dispatcher.Executed.ShouldBeEmpty(
            "The capability is never entered: the timeout is checked before the dispatch, so " +
            "a step with no budget costs the dependency nothing.");
    }

    /// <summary>A declared <c>Retry(3)</c> over a failing step buys three attempts.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Inverted.</strong> This test was <c>AForwardRetryDoesNotRetry</c> and asserted
    /// <c>dispatcher.Executed == [0]</c> with the message "three attempts were declared
    /// against a retryable failure on an idempotent capability, and one was made".
    /// </para>
    /// <para>
    /// Every precondition FLOWX1014 protects is met and now matters: <c>order.validate</c>
    /// declares <c>Idempotent = true</c>, so the declaration is legal, and the failure is
    /// <see cref="ErrorCategory.Unavailable"/>, which is in <c>Retry</c>'s own default
    /// retryable set.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AForwardRetryRetries()
    {
        var dispatcher = new RecordingDispatcher().FailAt(0, Unavailable);
        var clock = new FakeClock(T0);

        var result = await new FlowEngine(clock)
            .ExecuteAsync(
                Plan(Forward(PolicySet.Named("r").Retry(attempts: 3))),
                dispatcher,
                Plans.Invocation,
                Ct);

        result.IsSuccess.ShouldBeFalse();

        dispatcher.Executed.ShouldBe(
            [0, 0, 0],
            "Three attempts were declared against a retryable failure on an idempotent " +
            "capability, and three were made.");

        clock.Delays.Count.ShouldBe(2, "Two waits for three attempts — a backoff sits between them, never before the first.");
    }

    /// <summary>A retry that succeeds stops retrying, and the flow carries on.</summary>
    [Fact]
    public async Task ARetryStopsAtTheFirstAttemptThatSucceeds()
    {
        var dispatcher = new RecordingDispatcher().FailAtNthVisit(0, visit: 1, Unavailable);

        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(
                Plan(Forward(PolicySet.Named("r").Retry(attempts: 3))),
                dispatcher,
                Plans.Invocation,
                Ct);

        result.IsSuccess.ShouldBeTrue();

        dispatcher.Executed.ShouldBe(
            [0, 0, 1, 2],
            "The first attempt failed, the second succeeded, and the flow ran on rather than " +
            "spending the third attempt it was entitled to.");
    }

    /// <summary>
    /// A retry declines a category outside its <c>retryOn</c> set, however many attempts it has.
    /// </summary>
    /// <remarks>
    /// <c>docs/10-Policy-Framework.md §5</c>'s decision tree, second question:
    /// <em>Validation / NotFound / Forbidden — no retry, terminal</em>. The input will not
    /// become valid by being sent again.
    /// </remarks>
    [Fact]
    public async Task ARetryDeclinesANonRetryableCategory()
    {
        var dispatcher = new RecordingDispatcher().FailAt(0, Invalid);

        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(
                Plan(Forward(PolicySet.Named("r").Retry(attempts: 5))),
                dispatcher,
                Plans.Invocation,
                Ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Category.ShouldBe(ErrorCategory.Validation);

        dispatcher.Executed.ShouldBe(
            [0],
            "Five attempts were declared and the failure was a Validation error, which is " +
            "terminal — the input does not become valid by being sent again.");
    }

    /// <summary>A retry never outlives the flow deadline, backoff included.</summary>
    /// <remarks>
    /// <c>docs/10-Policy-Framework.md §5</c>'s second explicit guarantee. The plan's deadline
    /// is one second and the backoff is fixed at 800 ms, so the first wait fits and the second
    /// would not — the policy stops rather than arming an attempt whose sleep alone outlasts
    /// the budget.
    /// </remarks>
    [Fact]
    public async Task ARetryNeverOutlivesTheDeadline()
    {
        var dispatcher = new RecordingDispatcher().FailAt(0, Unavailable);
        var clock = new FakeClock(T0);

        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create("order.tight", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(1)),
            StepGraph.Create([
                StepNode.ForCapability(
                    0,
                    Plans.Validate,
                    policies: Forward(PolicySet.Named("r").Retry(
                        attempts: 10,
                        Backoff.Exponential(TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(800))))),
            ]));

        var result = await new FlowEngine(clock).ExecuteAsync(plan, dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeFalse();

        dispatcher.Executed.ShouldBe(
            [0, 0],
            "Ten attempts were declared inside a one-second deadline with an 800 ms backoff. " +
            "The second attempt fits; the third would have to sleep past the budget, so it is " +
            "refused and the last error is returned.");
    }

    /// <summary>Every attempt presents the same idempotency key.</summary>
    /// <remarks>
    /// <c>docs/10-Policy-Framework.md §5</c>'s first explicit guarantee, and the one that
    /// makes downstream deduplication work: attempt 2 is the same request as attempt 1, so a
    /// gateway that honours the key cannot double-charge for a retry FlowX asked for.
    /// </remarks>
    [Fact]
    public async Task EveryAttemptPresentsTheSameIdempotencyKey()
    {
        var dispatcher = new RecordingDispatcher().FailAt(0, Unavailable);
        dispatcher.Observe = ctx => ctx.IdempotencyKey;

        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(
                Plan(Forward(PolicySet.Named("r").Retry(attempts: 3))),
                dispatcher,
                Plans.Invocation,
                Ct);

        result.IsSuccess.ShouldBeFalse();

        var keys = dispatcher.Observed.Cast<string>().ToList();

        keys.Count.ShouldBe(3);
        keys.Distinct(StringComparer.Ordinal).Count().ShouldBe(
            1,
            "A retry never mints a fresh idempotency key. Attempt 2 presents attempt 1's, " +
            "which is what makes downstream deduplication work.");
    }

    /// <summary>A breaker opens after sustained failure and short-circuits the next call.</summary>
    /// <remarks>
    /// <para>
    /// <strong>New.</strong> Nothing asserted this before, because no breaker existed: the
    /// old file's single line on the subject was that the <c>CircuitBreaker</c> in
    /// <see cref="Everything"/> "is consulted by nothing".
    /// </para>
    /// <para>
    /// The breaker's sampling needs <see cref="StepPolicy.DefaultMinimumThroughput"/> calls
    /// before a ratio means anything — one failure out of one is not evidence — so the loop
    /// runs past it and then asserts that the capability stops being called at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ABreakerOpensAfterSustainedFailuresAndShortCircuitsTheNextCall()
    {
        var clock = new FakeClock(T0);
        var engine = new FlowEngine(clock);

        var plan = Plan(Forward(PolicySet.Named("b")
            .CircuitBreaker(failureRatio: 0.5, breakDuration: TimeSpan.FromSeconds(30))));

        var dispatchesBeforeTheBreakerOpened = 0;
        Error? lastError = null;

        for (var run = 0; run < StepPolicy.DefaultMinimumThroughput + 2; run++)
        {
            var dispatcher = new RecordingDispatcher().FailAt(0, Unavailable);
            var result = await engine.ExecuteAsync(plan, dispatcher, Plans.Invocation, Ct);

            dispatchesBeforeTheBreakerOpened += dispatcher.Executed.Count(static i => i == 0);
            lastError = result.Error;
        }

        lastError!.Code.ShouldBe(
            FlowErrors.CircuitOpenCode,
            "Every call failed and the breaker's failure ratio is 0.5, so once the sampling " +
            "window holds enough calls to mean something the breaker opens and the next call " +
            "is refused without reaching the dependency.");

        dispatchesBeforeTheBreakerOpened.ShouldBeLessThan(
            StepPolicy.DefaultMinimumThroughput + 2,
            "An open breaker stops calling. If every run still dispatched, nothing opened.");
    }

    /// <summary>
    /// With breakers keyed per tenant, one tenant's failing downstream does not refuse another
    /// tenant's calls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>docs/16 §4</c>'s fifth mechanism, in one sentence: "one tenant's bad downstream does
    /// not trip everyone". Keyed by capability alone — the default, and correct where the
    /// dependency is shared — the second tenant here is refused a call it would have completed,
    /// which is an outage it is not having.
    /// </para>
    /// <para>
    /// <strong>Both arrangements are asserted, because the widening is a trade and not a
    /// fix.</strong> A shared breaker is <em>faster</em> to protect a shared dependency: the
    /// first tenant's failures spare every other tenant the calls. A test that only showed the
    /// per-tenant behaviour would read as though the shared key were a defect.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATenantScopedBreakerDoesNotRefuseAnotherTenantsCalls()
    {
        var plan = Plan(Forward(PolicySet.Named("b")
            .CircuitBreaker(failureRatio: 0.5, breakDuration: TimeSpan.FromSeconds(30))));

        var shared = new FlowEngine(new FakeClock(T0));
        var perTenant = new FlowEngine(new FakeClock(T0), breakersPerTenant: true);

        for (var run = 0; run < StepPolicy.DefaultMinimumThroughput + 2; run++)
        {
            await shared.ExecuteAsync(plan, new RecordingDispatcher().FailAt(0, Unavailable), Acme, Ct);
            await perTenant.ExecuteAsync(plan, new RecordingDispatcher().FailAt(0, Unavailable), Acme, Ct);
        }

        var refusedForEveryone = await shared.ExecuteAsync(plan, new RecordingDispatcher(), Globex, Ct);

        refusedForEveryone.Error!.Code.ShouldBe(
            FlowErrors.CircuitOpenCode,
            "one breaker per capability: the tenant that broke nothing is refused too");

        var admitted = await perTenant.ExecuteAsync(plan, new RecordingDispatcher(), Globex, Ct);

        admitted.IsSuccess.ShouldBeTrue(
            "one breaker per capability and tenant: the failing tenant's breaker is open and " +
            "the quiet tenant's has seen nothing to open it.");
    }

    private static FlowInvocation Acme { get; } = new("corr-1", "idem-1", TenantId: "acme");

    private static FlowInvocation Globex { get; } = new("corr-2", "idem-2", TenantId: "globex");

    /// <summary>A breaker closes again once its break duration has passed.</summary>
    /// <remarks>
    /// The half that makes a breaker a breaker rather than a kill switch. Asserted on the
    /// fake clock, so the thirty seconds cost the suite nothing.
    /// </remarks>
    [Fact]
    public async Task ABreakerClosesAfterItsBreakDuration()
    {
        var clock = new FakeClock(T0);
        var engine = new FlowEngine(clock);

        var plan = Plan(Forward(PolicySet.Named("b")
            .CircuitBreaker(failureRatio: 0.5, breakDuration: TimeSpan.FromSeconds(30))));

        for (var run = 0; run < StepPolicy.DefaultMinimumThroughput + 2; run++)
        {
            await engine.ExecuteAsync(
                plan, new RecordingDispatcher().FailAt(0, Unavailable), Plans.Invocation, Ct);
        }

        clock.Advance(TimeSpan.FromSeconds(31));

        var recovered = new RecordingDispatcher();
        var result = await engine.ExecuteAsync(plan, recovered, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeTrue();

        recovered.Executed.ShouldBe(
            [0, 1, 2],
            "The break duration passed, so the breaker let a call through, it succeeded, and " +
            "the dependency is back in service.");
    }

    /// <summary>A bulkhead refuses a caller beyond its concurrency and its queue.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Inverted, in the half that could be.</strong> The old file asserted a
    /// <c>Bulkhead(1)</c> "admits one caller and there is one" — which stays true — and drew
    /// from it that nothing counted. Two callers is the shape that can tell a counted
    /// bulkhead from an uncounted one, and the old file never ran one.
    /// </para>
    /// <para>
    /// <c>queueDepth: 0</c> so the refusal is immediate rather than a wait: a bulkhead that
    /// queues indefinitely is a bulkhead that converts a concurrency problem into a latency
    /// one, which is the failure it exists to prevent.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ABulkheadRefusesACallerBeyondItsConcurrency()
    {
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();

        // Two dispatchers, because only the first caller is meant to be inside the step. One
        // shared double would hold the second caller too, and the test would then be waiting
        // on the very dispatch it is asserting never happens.
        var holding = new RecordingDispatcher().HoldAt(0, gate.Task, entered);
        var arriving = new RecordingDispatcher();

        var plan = Plan(Forward(PolicySet.Named("bh").Bulkhead(maxConcurrency: 1, queueDepth: 0)));
        var engine = new FlowEngine(new FakeClock(T0));

        var first = engine.ExecuteAsync(plan, holding, Plans.Invocation, Ct).AsTask();

        await entered.Task;

        var second = await engine.ExecuteAsync(plan, arriving, Plans.Invocation, Ct);

        gate.SetResult();
        (await first).IsSuccess.ShouldBeTrue();

        second.IsSuccess.ShouldBeFalse();

        arriving.Executed.ShouldBeEmpty("A refused caller never reaches the dependency.");

        second.Error!.Code.ShouldBe(
            FlowErrors.BulkheadRejectedCode,
            "One permit was declared, one caller held it, and the second was refused rather " +
            "than queued — a bulkhead with no queue depth fails fast on purpose.");
    }

    // ------------------------------------------------- the kind that runs off another flag

    /// <summary>
    /// A declared <c>Cache</c> with no store configured dispatches on every run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Kept, and re-aimed twice.</strong> It was
    /// <c>ACacheIsNotConsultedAndARateLimitCountsNothing</c>: two claims, both of the form "a
    /// declaration bought nothing". The rate-limit half left when stage 1 landed — a two-permit
    /// version of exactly this shape is
    /// <c>AdmissionAndIntegrityTests.ARateLimitRefusesPastItsPermitsWithoutDispatchingTheStep</c>,
    /// and it refuses. The cache half left when stage 5 did:
    /// <c>CachePolicyTests.TheSecondExecutionIsServedFromTheCache</c> asserts the opposite on
    /// this same two-run shape, given an <c>IResultCache</c>.
    /// </para>
    /// <para>
    /// What this shape still asserts is the one behaviour a configured cache cannot show: an
    /// engine constructed with <em>no</em> <c>IResultCache</c> dispatches every time, which is
    /// what stage 5 did before it existed. ADR-0025 §2.3 is the decision — an unconsulted cache
    /// is slower and never wrong — and it is the only seam of the four whose absence does not
    /// refuse the step. Two executions of one plan is the only shape that can tell a consulted
    /// cache from an unconsulted one: a hit would skip the second dispatch.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ACacheWithNoStoreConfiguredDispatchesEveryTime()
    {
        var engine = new FlowEngine(new FakeClock(T0));
        var plan = Plan(Forward(PolicySet.Named("cached").Cache(TimeSpan.FromHours(1))));

        var first = new RecordingDispatcher();
        var second = new RecordingDispatcher();

        (await engine.ExecuteAsync(plan, first, Plans.Invocation, Ct)).IsSuccess.ShouldBeTrue();
        (await engine.ExecuteAsync(plan, second, Plans.Invocation, Ct)).IsSuccess.ShouldBeTrue();

        first.Executed.ShouldBe([0, 1, 2]);

        second.Executed.ShouldBe(
            [0, 1, 2],
            "A one-hour cache was declared on step 0 and no IResultCache was registered, so " +
            "the second run must dispatch it rather than fail or invent a hit.");
    }

    /// <summary>
    /// A stage-7 <c>Audit</c> is not a compensation policy and is not a step policy either —
    /// it is a third thing, with a flag of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Inverted in one assertion and kept in the rest.</strong> This test was
    /// <c>AnAuditIsAStageSevenPolicyAndStillExecutesNowhere</c>, and the deleted FLOWX1032's page named it
    /// as the assertion that would go red on the day stage 7's audit ran. What went red is the
    /// claim that no flag counted it; what is unchanged is the reason there had to be a third
    /// flag rather than a wider reading of the two that existed.
    /// </para>
    /// <para>
    /// <c>Audit</c> and <c>CompensationRetry</c> share <see cref="PolicyStage.Consistency"/>,
    /// so a reader who cut the gap by stage would have expected one flag to cover both. Neither
    /// covers the other: <c>PolicyChain.ForStep</c> moves only the compensation retry onto the
    /// undo's chain, and an audit is not stage 4, so <c>StepPolicy.From</c> still reads past it
    /// and <c>HasStepPolicies</c> is still false for an audit-only chain.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnAuditIsAStageSevenPolicyWithAFlagOfItsOwn()
    {
        var plan = Plan(Forward(PolicySet.Named("a").Audit("financial")));

        plan.Graph.Steps[0].Policies.Ordered
            .Select(static p => p.Stage)
            .ShouldBe([PolicyStage.Consistency], "Audit is stage 7, the same stage as the retry.");

        plan.HasCompensationPolicies.ShouldBeFalse(
            "Stage is not the cut. The cut is what a policy wraps, and an Audit wraps the " +
            "step rather than its undo.");

        plan.HasStepPolicies.ShouldBeFalse(
            "Nor is it stage 4, so the engine's forward policy path is not entered for it — " +
            "which is why folding it into StepPolicy.IsActive would have made that flag mean " +
            "two different things.");

        plan.HasAuditedSteps.ShouldBeTrue(
            "It has its own flag, and that flag is what the step loop reads after the commit.");

        plan.Graph.Steps[0].StepAudit.Category.ShouldBe("financial");
        plan.Graph.Steps[0].CompensationRetry.IsRetrying.ShouldBeFalse();
    }

    /// <summary>An <c>Audit</c> with a blank category reaches no flag and writes nothing.</summary>
    /// <remarks>
    /// <c>PolicySet.Audit</c> validates no argument — no builder method does — so a record whose
    /// category is the empty string is expressible, and it is one no compliance query can
    /// select. Writing it would be worse than not auditing while looking like auditing, so
    /// <c>StepAudit.From</c> resolves it to <c>None</c> and the plan-level flag stays false:
    /// the bargain every other flag of this shape strikes, which is that it counts what the
    /// engine will do rather than what was declared.
    /// </remarks>
    [Fact]
    public void AnAuditWithNoCategoryCountsAsNoAudit()
    {
        Plan(Forward(PolicySet.Named("a").Audit("   "))).HasAuditedSteps.ShouldBeFalse();
    }

    /// <summary>
    /// A plan whose only declared kind runs off another flag costs the step loop nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The flag is what keeps budget B2 a hard zero for a flow that declares no in-line
    /// policy, and a flag that were true for every declaration would defeat its own purpose —
    /// see <a href="../../docs/adr/ADR-0023-policy-stages-hook-through-the-plan.md">ADR-0023</a>.
    /// </para>
    /// <para>
    /// <strong>This test read "the three inert kinds", then "Cache and Audit", then
    /// "RateLimit and Idempotency", and is now about the <c>Audit</c> alone.</strong> Every
    /// widening of <c>StepPolicy.IsActive</c> was an opportunity to make the flag count
    /// declarations rather than what runs, which is the one way widening it could have cost
    /// budget B2 — and stage 7 is the one stage that must still leave it false, because it
    /// runs after the commit off a flag of its own.
    /// </para>
    /// </remarks>
    [Fact]
    public void APlanDeclaringOnlyAnAuditReportsNoStepPolicies()
    {
        Plan(AuditOnly).HasStepPolicies.ShouldBeFalse(
            "An Audit is declared and it executes — after the commit, off HasAuditedSteps — " +
            "so the step loop must not take the in-line policy path for it. The flag counts " +
            "what the step-policy path will do, and three widenings of StepPolicy.IsActive " +
            "must not have quietly made it count declarations instead.");

        Plan(AuditOnly).HasAuditedSteps.ShouldBeTrue(
            "And the flag that does count it is true, so the assertion above is about which " +
            "path runs rather than about a declaration nothing reads.");

        Plan(Everything).HasStepPolicies.ShouldBeTrue(
            "The same chain plus stage 4 does take it.");
    }

    // -------------------------------------------------------------- the positive control

    /// <summary>
    /// And the one policy that executed before this package, on the same plan, still executes.
    /// </summary>
    /// <remarks>
    /// Step 1 carries a <c>CompensationRetry</c> on its undo; the undo fails once and is
    /// dispatched a second time. Kept unchanged from the file this replaced, because a
    /// package that made stage 4 run and quietly broke stage 7 would pass every other test
    /// here.
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

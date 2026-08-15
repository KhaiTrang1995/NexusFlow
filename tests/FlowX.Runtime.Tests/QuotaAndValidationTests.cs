using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// Stage 1's quota and stage 3's validation, against a real engine: what they refuse, where they
/// refuse it, and what a refusal tells the caller.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Written the way <c>AdmissionAndIntegrityTests</c> is, and for its reason.</strong>
/// Nearly every test here asserts that something did <em>not</em> happen, which is the shape
/// that passes against an engine that does nothing at all — so each one names the dispatch count
/// it expects, and each file has at least one positive control: a good input is admitted, and a
/// caller inside its budget gets through.
/// </para>
/// <para>
/// The stores are doubles that count. What holds a <em>real</em> quota store to the contract is
/// <c>QuotaStoreConformance</c>, derived by the in-memory reference and by PostgreSQL —
/// including the two assertions no double can make: that two clients share one budget, and that
/// an exhausted budget stays exhausted until its window turns over.
/// </para>
/// </remarks>
public sealed class QuotaAndValidationTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A step declaring two calls a day, per tenant.</summary>
    private static PolicyChain TwoADay { get; } = PolicyChain.ForStep(
        PolicySet.Named("plan").Quota(budget: 2, Day, QuotaScope.Tenant),
        Plans.Validate);

    private static ExecutionPlan Plan(PolicyChain forward) =>
        ExecutionPlan.Create(
            FlowDescriptor.Create("order.policy", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(0, Plans.Validate, policies: forward),
                StepNode.ForCapability(1, Plans.Reserve),
            ]));

    private static FlowInvocation For(string tenant) => new("corr-1", "idem-1", TenantId: tenant);

    // ------------------------------------------------------------------------ stage 1 · quota

    /// <summary>
    /// The third caller against a budget of two is refused, and the capability is never entered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The assertion the whole of the policy exists to make true</strong>, written
    /// against the dispatch count rather than against the result: a quota that refused the flow
    /// <em>after</em> calling the dependency would satisfy every assertion about the outcome and
    /// none of the ones that matter.
    /// </para>
    /// <para>
    /// Two rather than one, so the test distinguishes "the store counts" from "the store refuses
    /// everything" — a budget that always said no would pass a one-call version of this on its
    /// second call and would take the step out of service.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AQuotaRefusesPastItsBudgetWithoutDispatchingTheStep()
    {
        var quota = new CountingQuotaStore(allowed: 2);
        var engine = new FlowEngine(new FakeClock(T0), quota: quota);
        var plan = Plan(TwoADay);

        var first = new RecordingDispatcher();
        var second = new RecordingDispatcher();
        var third = new RecordingDispatcher();

        (await engine.ExecuteAsync(plan, first, For("acme"), Ct)).IsSuccess.ShouldBeTrue(
            "the first of two calls the plan grants.");

        (await engine.ExecuteAsync(plan, second, For("acme"), Ct)).IsSuccess.ShouldBeTrue(
            "the second.");

        var refused = await engine.ExecuteAsync(plan, third, For("acme"), Ct);

        refused.IsSuccess.ShouldBeFalse(
            "Two calls a day were declared and this is the third inside the day. A quota that " +
            "admits it is a plan limit that counts nothing.");

        refused.Error!.Code.ShouldBe(
            FlowErrors.QuotaExhaustedCode,
            "a caller branches on the code, and this one says 'the plan you bought is spent' " +
            "rather than 'slow down' — which is the whole reason it is not policy.rate_limited.");

        refused.Error.Category.ShouldBe(
            ErrorCategory.Forbidden,
            "nothing is down and nothing is temporarily busy: no amount of waiting inside this " +
            "period changes the answer, which is what keeps the error out of every retry set.");

        third.Executed.ShouldBeEmpty(
            "order.validate was never entered. Admission control that calls the dependency and " +
            "then reports a refusal has protected nothing.");

        quota.Calls.ShouldBe(3, "one decision per execution of the policed step, and no more.");
    }

    /// <summary>A refusal says when the budget comes back.</summary>
    /// <remarks>
    /// The one thing a refused caller can act on, and the reason the verdict carries it rather
    /// than the engine deriving it: only the store knows where the window boundary is, because
    /// only the store's clock is the one every node agrees about.
    /// </remarks>
    [Fact]
    public async Task ARefusedCallerIsToldWhenTheBudgetComesBack()
    {
        var quota = new CountingQuotaStore(allowed: 1);
        var engine = new FlowEngine(new FakeClock(T0), quota: quota);
        var plan = Plan(PolicyChain.ForStep(
            PolicySet.Named("plan").Quota(budget: 1, Day, QuotaScope.Global), Plans.Validate));

        await engine.ExecuteAsync(plan, new RecordingDispatcher(), For("acme"), Ct);

        var refused = await engine.ExecuteAsync(plan, new RecordingDispatcher(), For("acme"), Ct);

        refused.Error!.Data.ShouldNotBeNull();

        refused.Error.Data!["retryAfter"].ShouldBe(
            Day,
            "the remainder of the window, as the store reported it. A refusal with no wait is " +
            "one the caller can only respond to by asking again immediately.");
    }

    /// <summary>The budget is granted again when the window turns over.</summary>
    /// <remarks>
    /// The other half of the policy, and the half a token bucket gets wrong in the opposite
    /// direction: a store that never granted the budget again would take the step out of
    /// service permanently on the first busy day, and every refusal assertion above would still
    /// pass.
    /// </remarks>
    [Fact]
    public async Task TheBudgetIsGrantedAgainWhenTheWindowTurnsOver()
    {
        var quota = new CountingQuotaStore(allowed: 1);
        var engine = new FlowEngine(new FakeClock(T0), quota: quota);
        var plan = Plan(TwoADay);

        await engine.ExecuteAsync(plan, new RecordingDispatcher(), For("acme"), Ct);

        (await engine.ExecuteAsync(plan, new RecordingDispatcher(), For("acme"), Ct))
            .IsSuccess.ShouldBeFalse("the budget is spent inside this window.");

        quota.TurnTheWindowOver();

        var next = new RecordingDispatcher();

        (await engine.ExecuteAsync(plan, next, For("acme"), Ct)).IsSuccess.ShouldBeTrue(
            "the window turned over, so the plan grants its calls again.");

        next.Executed.ShouldBe([0, 1], "and the step actually ran.");
    }

    /// <summary>One tenant exhausting its plan does not refuse another.</summary>
    /// <remarks>
    /// <para>
    /// <strong>This is why the policy is in the catalogue at all.</strong> <c>docs/16 §4</c>'s
    /// fairness is not a property of the store — a counter counts whatever key it is handed —
    /// but of the key the engine builds, which carries the scope's identity. A key built from
    /// the capability alone would make a tenant-scoped declaration a shared ceiling the noisiest
    /// tenant spends first, and every other assertion in this file would still pass.
    /// </para>
    /// <para>
    /// The keys are checked too, and not only the outcome: two admissions could also mean the
    /// store counted nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task OneTenantsExhaustedBudgetDoesNotRefuseAnother()
    {
        var quota = new CountingQuotaStore(allowed: 1);
        var engine = new FlowEngine(new FakeClock(T0), quota: quota);
        var plan = Plan(TwoADay);

        await engine.ExecuteAsync(plan, new RecordingDispatcher(), For("acme"), Ct);

        (await engine.ExecuteAsync(plan, new RecordingDispatcher(), For("acme"), Ct))
            .IsSuccess.ShouldBeFalse("acme has spent its plan.");

        var other = new RecordingDispatcher();

        (await engine.ExecuteAsync(plan, other, For("globex"), Ct)).IsSuccess.ShouldBeTrue(
            "globex bought its own plan and has spent none of it. A quota that refuses here is " +
            "one tenant starving another, which is the failure docs/16 §4 exists to prevent.");

        other.Executed.ShouldBe([0, 1], "and globex's step actually ran.");

        quota.Keys.Distinct(StringComparer.Ordinal).Count().ShouldBe(
            2,
            "two tenants under a Tenant-scoped quota address two counters. One key for both " +
            "would be a global budget wearing a per-tenant declaration.");
    }

    /// <summary>A unit is spent once per step, not once per retry attempt.</summary>
    /// <remarks>
    /// The position assertion, and a commercial one rather than a tidy one: a budget spent per
    /// attempt would make a tenant's monthly plan shrink by a factor of three on the day one of
    /// its dependencies is unhealthy — a plan limit whose effective size is a function of
    /// somebody else's outage.
    /// </remarks>
    [Fact]
    public async Task ARetriedStepSpendsOneUnitAndNotOnePerAttempt()
    {
        var quota = new CountingQuotaStore(allowed: 10);

        var chain = PolicyChain.ForStep(
            PolicySet.Named("both")
                .Quota(budget: 10, Day, QuotaScope.Global)
                .Retry(attempts: 3, Backoff.Exponential(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2))),
            Plans.Validate);

        var dispatcher = new RecordingDispatcher()
            .FailAt(0, new Error("order.validate_failed", "down", ErrorCategory.Unavailable));

        var result = await new FlowEngine(new FakeClock(T0), quota: quota)
            .ExecuteAsync(Plan(chain), dispatcher, For("acme"), Ct);

        result.IsSuccess.ShouldBeFalse("the step failed all three times.");

        dispatcher.Executed.ShouldBe([0, 0, 0], "three attempts were declared and three were made.");

        quota.Calls.ShouldBe(
            1,
            "One unit for the step, not one per attempt. A quota inside the retry loop would " +
            "have spent three, so a tenant's plan would drain fastest exactly when the " +
            "dependency it is calling is already failing.");
    }

    /// <summary>A quota with no store registered refuses rather than admits.</summary>
    /// <remarks>
    /// ADR-0040 §2.2 asked of the second stage-1 kind. Admitting when the store is absent puts
    /// the declaration's meaning in a registration nobody can see from the flow, and the code is
    /// distinct from the exhaustion so an operator does not have to guess which repair to make.
    /// </remarks>
    [Fact]
    public async Task AQuotaWithNoStoreRefusesRatherThanAdmits()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(Plan(TwoADay), dispatcher, For("acme"), Ct);

        result.IsSuccess.ShouldBeFalse("no IQuotaStore was registered, so no budget was consulted.");

        result.Error!.Code.ShouldBe(
            FlowErrors.QuotaUnavailableCode,
            "distinct from the exhaustion, because the repairs are opposite: one is 'register a " +
            "store', the other is 'buy a bigger plan'.");

        dispatcher.Executed.ShouldBeEmpty(
            "and the step was refused rather than dispatched — a budget that is not wired up " +
            "must not read as a budget that had room.");
    }

    /// <summary>A store that cannot answer is a refusal, not an admission.</summary>
    [Fact]
    public async Task AStoreThatCannotAnswerRefuses()
    {
        var quota = new CountingQuotaStore(allowed: 10)
        {
            Unreachable = new Error("store.down", "no answer", ErrorCategory.Unavailable),
        };

        var dispatcher = new RecordingDispatcher();

        var result = await new FlowEngine(new FakeClock(T0), quota: quota)
            .ExecuteAsync(Plan(TwoADay), dispatcher, For("acme"), Ct);

        result.Error!.Code.ShouldBe(
            FlowErrors.QuotaUnavailableCode,
            "a counter that cannot reach its server does not know what this holder has spent, " +
            "and admitting on doubt turns an outage of the counter into an unmetered month.");

        dispatcher.Executed.ShouldBeEmpty("the step was not dispatched.");
    }

    // ------------------------------------------------------------------- stage 3 · validation

    /// <summary>A step declaring a validation and nothing else.</summary>
    private static PolicyChain Validated { get; } = PolicyChain.ForStep(
        PolicySet.Named("integrity").Validate(), Plans.Validate);

    private static ValidationOutcome TwoBadFields => ValidationOutcome.Invalid(
    [
        new FieldError("Quantity", "range", "'Quantity' must be between 1 and 100."),
        new FieldError("Reference", "required", "'Reference' is required."),
    ]);

    /// <summary>Bad input is refused with its field errors, and the step is never dispatched.</summary>
    /// <remarks>
    /// <c>docs/10 §2</c>'s sixth row: "validation after the side effect | corrupt data written,
    /// then rejected | prevented because Integrity (3) precedes Execution (6)". The dispatch
    /// count is what makes that a property rather than a sentence.
    /// </remarks>
    [Fact]
    public async Task ValidationRefusesBadInputWithFieldErrorsAndNoDispatch()
    {
        var dispatcher = new RecordingDispatcher().ValidatesAt(0, TwoBadFields);

        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(Plan(Validated), dispatcher, For("acme"), Ct);

        result.IsSuccess.ShouldBeFalse("the input broke two rules its contract declares.");

        result.Error!.Code.ShouldBe(FlowErrors.ValidationFailedCode);

        result.Error.Category.ShouldBe(
            ErrorCategory.Validation,
            "which is what makes it a 400 and keeps it out of every retry set: docs/10 §11's " +
            "first anti-pattern is retrying a validation error, because the input will never " +
            "become valid.");

        result.Error.Data.ShouldNotBeNull();
        result.Error.Data!.ShouldContainKey(FlowErrors.FieldErrorsDetail);

        ((IReadOnlyList<FieldError>)result.Error.Data[FlowErrors.FieldErrorsDetail]!)
            .Select(static f => f.Field)
            .ShouldBe(["Quantity", "Reference"],
                "the caller is told which members are wrong, which is the half of a refusal it " +
                "can act on — and the half a message alone cannot carry.");

        dispatcher.Executed.ShouldBeEmpty(
            "the capability was never entered. A validation that ran after the dispatch would " +
            "be a rejection of data that has already been written.");
    }

    /// <summary>Good input is admitted, and the step runs.</summary>
    /// <remarks>
    /// The positive control, and this file needs one more than most: every other assertion here
    /// would pass against a stage 3 that refused everything.
    /// </remarks>
    [Fact]
    public async Task ValidationAdmitsGoodInput()
    {
        var dispatcher = new RecordingDispatcher().ValidatesAt(0, ValidationOutcome.Valid);

        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(Plan(Validated), dispatcher, For("acme"), Ct);

        result.IsSuccess.ShouldBeTrue("the input satisfied every declared rule.");

        dispatcher.Executed.ShouldBe([0, 1], "and both steps ran.");

        dispatcher.Validated.ShouldBe([0], "the engine asked only about the step that declared one.");
    }

    /// <summary>A refused input claims no idempotency key.</summary>
    /// <remarks>
    /// <strong>The order inside stage 3, and it is a correctness property rather than a
    /// preference.</strong> <c>docs/10 §2</c> lists "validation, idempotency, dedupe" in that
    /// order. A window claimed before the input was judged would burn the caller's idempotency
    /// key on a call that never happened — so the repeat, with the payload corrected, would find
    /// its own key and replay the refusal instead of running the step.
    /// </remarks>
    [Fact]
    public async Task ARefusedInputClaimsNoIdempotencyKey()
    {
        var idempotency = new RecordingIdempotencyStore();

        var chain = PolicyChain.ForStep(
            PolicySet.Named("integrity")
                .Validate()
                .Idempotency(TimeSpan.FromHours(24), IdempotencyScope.Tenant),
            Plans.Validate);

        var dispatcher = new RecordingDispatcher().ValidatesAt(0, TwoBadFields);

        var result = await new FlowEngine(new FakeClock(T0), idempotency: idempotency)
            .ExecuteAsync(Plan(chain), dispatcher, For("acme"), Ct);

        result.Error!.Code.ShouldBe(FlowErrors.ValidationFailedCode, "the validation refused first.");

        idempotency.Keys.ShouldBeEmpty(
            "No key was claimed for an input that was never dispatched. A window taken first " +
            "would spend the caller's key on a call that did not happen, and the corrected " +
            "repeat would replay this refusal rather than run the step.");
    }

    /// <summary>A dispatcher with no generated checks refuses rather than admits.</summary>
    /// <remarks>
    /// <see cref="ValidationOutcome.Unavailable"/> is <c>default</c>, so this is the answer a
    /// dispatcher that does not implement the member gives without writing a line — and the
    /// engine reads it as a refusal. An outcome whose default was "valid" would make a declared
    /// <c>Validate</c> read as satisfied on exactly the builds nobody regenerated. A compiled
    /// flow cannot reach it: FLOWX1056 refuses the declaration at build time when the contract
    /// has no rule, and the generator emits a case for every step that passes.
    /// </remarks>
    [Fact]
    public async Task ADispatcherWithNoGeneratedChecksRefuses()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(Plan(Validated), dispatcher, For("acme"), Ct);

        result.Error!.Code.ShouldBe(
            FlowErrors.ValidationUnavailableCode,
            "nothing examined the input, and a validation nothing ran must not read as a " +
            "validation that passed.");

        dispatcher.Executed.ShouldBeEmpty("the step was refused rather than dispatched.");
    }
}

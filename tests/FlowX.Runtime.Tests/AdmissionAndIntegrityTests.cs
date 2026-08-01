using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// Stage 1 and stage 3, against a real engine: what they refuse, and where they refuse it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every test here asserts that something did <em>not</em> happen, which is the shape
/// that passes against an engine that does nothing at all.</strong> So each one names the
/// dispatch count it expects rather than merely that the flow failed, and
/// <c>PolicyExecutionTests</c>'s positive control still runs beside them. A rate limiter that
/// never refuses and an idempotency key that never replays are indistinguishable from the inert
/// versions they replace, and a file of failure assertions is exactly how that goes unnoticed.
/// </para>
/// <para>
/// The stores here are doubles that count. What holds a <em>real</em> store to the contract is
/// <c>RateLimiterConformance</c> and <c>IdempotencyStoreConformance</c>, derived by Redis and by
/// PostgreSQL — including the assertion no double can make, that two clients share one budget.
/// </para>
/// </remarks>
public sealed class AdmissionAndIntegrityTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A step declaring two permits an hour, and nothing else.</summary>
    private static PolicyChain TwoPerHour { get; } = PolicyChain.ForStep(
        PolicySet.Named("admission").RateLimit(permits: 2, TimeSpan.FromHours(1), RateLimitScope.Global),
        Plans.Validate);

    private static ExecutionPlan Plan(PolicyChain forward) =>
        ExecutionPlan.Create(
            FlowDescriptor.Create("order.policy", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(0, Plans.Validate, policies: forward),
                StepNode.ForCapability(1, Plans.Reserve),
            ]));

    /// <summary>
    /// The third caller of a two-permit step is refused, and its capability is never entered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the assertion the whole of stage 1 exists to make true.</strong> It is
    /// written against the dispatch count rather than against the result, because a limiter that
    /// refused the flow <em>after</em> calling the dependency would satisfy every assertion
    /// about the outcome and none of the ones that matter — the point of admission control is
    /// that the call does not happen.
    /// </para>
    /// <para>
    /// Two permits rather than one, so that the test distinguishes "the limiter counts" from
    /// "the limiter refuses everything" — a store that always said no would pass a one-permit
    /// version of this on its second call.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARateLimitRefusesPastItsPermitsWithoutDispatchingTheStep()
    {
        var limiter = new CountingRateLimiter(budget: 2);
        var engine = new FlowEngine(new FakeClock(T0), rateLimiter: limiter);
        var plan = Plan(TwoPerHour);

        var first = new RecordingDispatcher();
        var second = new RecordingDispatcher();
        var third = new RecordingDispatcher();

        (await engine.ExecuteAsync(plan, first, Plans.Invocation, Ct)).IsSuccess.ShouldBeTrue(
            "the first of two permits.");

        (await engine.ExecuteAsync(plan, second, Plans.Invocation, Ct)).IsSuccess.ShouldBeTrue(
            "the second of two permits.");

        var refused = await engine.ExecuteAsync(plan, third, Plans.Invocation, Ct);

        refused.IsSuccess.ShouldBeFalse(
            "Two permits an hour were declared and this is the third caller inside the hour. " +
            "A limiter that admits it is a limiter that counts nothing, which is what stage 1 " +
            "did before it executed.");

        refused.Error!.Code.ShouldBe(
            FlowErrors.RateLimitedCode,
            "a caller branches on the code, and docs/10 §3 makes this the row that becomes a " +
            "429 with a Retry-After.");

        third.Executed.ShouldBeEmpty(
            "order.validate was never entered. Admission control that calls the dependency and " +
            "then reports a refusal has protected nothing — the whole of stage 1 preceding " +
            "stage 6 is that the call does not happen.");

        limiter.Calls.ShouldBe(3, "one decision per execution of the policed step, and no more.");
    }

    /// <summary>A permit is taken once per step, not once per retry attempt.</summary>
    /// <remarks>
    /// <strong>The position assertion, and it is a correctness one rather than a tidiness
    /// one.</strong> A limiter consulted inside the retry loop would make a
    /// <c>RateLimit(20, PT1S)</c> beside a <c>Retry(3)</c> admit somewhere between seven and
    /// twenty callers a second depending on how healthy the dependency was — a limit whose
    /// effective value is a function of an outage. Three attempts, one permit, is what
    /// ADR-0011's stage order means when stage 1 is outside stage 4.
    /// </remarks>
    [Fact]
    public async Task ARetriedStepSpendsOnePermitAndNotOnePerAttempt()
    {
        var limiter = new CountingRateLimiter(budget: 10);

        var chain = PolicyChain.ForStep(
            PolicySet.Named("both")
                .RateLimit(permits: 10, TimeSpan.FromHours(1), RateLimitScope.Global)
                .Retry(attempts: 3, Backoff.Exponential(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2))),
            Plans.Validate);

        var dispatcher = new RecordingDispatcher()
            .FailAt(0, new Error("order.validate_failed", "down", ErrorCategory.Unavailable));

        var result = await new FlowEngine(new FakeClock(T0), rateLimiter: limiter)
            .ExecuteAsync(Plan(chain), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeFalse("the step failed all three times.");

        dispatcher.Executed.ShouldBe([0, 0, 0], "three attempts were declared and three were made.");

        limiter.Calls.ShouldBe(
            1,
            "One permit for the step, not one per attempt. A limiter inside the retry loop " +
            "would have spent three, so a declared budget would drain faster exactly when the " +
            "dependency it protects is already failing.");
    }

    /// <summary>The limiter is asked before the authorisation stance is decided.</summary>
    /// <remarks>
    /// <c>docs/10 §2</c>'s table: "rate limit after authentication → unauthenticated flood
    /// exhausts the token validator | prevented because Admission (1) precedes Identity (2)".
    /// The step below refuses every caller at stage 1, and the assertion is that the refusal is
    /// the limiter's rather than the stance's — so an unadmitted flood never reaches a claim
    /// lookup.
    /// </remarks>
    [Fact]
    public async Task AdmissionIsDecidedBeforeIdentity()
    {
        var limiter = new CountingRateLimiter(budget: 0);

        var permissioned = CapabilityDescriptor.Create(
            "order.validate", "1.0.0", isIdempotent: true, Authorization.Permission, "order.place");

        var chain = PolicyChain.ForStep(
            PolicySet.Named("admission").RateLimit(permits: 1, TimeSpan.FromHours(1), RateLimitScope.Global),
            permissioned);

        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create("order.policy", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([StepNode.ForCapability(0, permissioned, policies: chain)]));

        plan.HasAuthorizedSteps.ShouldBeTrue("the step can refuse a caller, so the stance is live.");

        var result = await new FlowEngine(new FakeClock(T0), rateLimiter: limiter)
            .ExecuteAsync(plan, new RecordingDispatcher(), Plans.Invocation, Ct);

        result.Error!.Code.ShouldBe(
            FlowErrors.RateLimitedCode,
            "The caller holds no 'order.place' permission either, so both stages would refuse " +
            "it — and the one that answers is stage 1. A deployment whose limiter ran second " +
            "would spend a token validator on every request of a flood it was declared to stop.");
    }

    /// <summary>A declared rate limit with no store registered refuses rather than admits.</summary>
    /// <remarks>
    /// <strong>Fail-closed, and this is the whole of what makes the policy honest.</strong>
    /// Admitting when the limiter is absent would put the declaration's meaning in a
    /// registration nobody can see from the flow — a limit that is enforced or not depending on
    /// a wiring decision, with the source reading identically either way. That is the
    /// half-executing policy ADR-0025 rejects, arriving through the configuration instead of
    /// through the algorithm.
    /// </remarks>
    [Fact]
    public async Task ARateLimitWithNoStoreRefusesRatherThanAdmits()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(Plan(TwoPerHour), dispatcher, Plans.Invocation, Ct);

        result.Error!.Code.ShouldBe(
            FlowErrors.RateLimiterUnavailableCode,
            "no IRateLimiterStore was registered, and a limiter that is not wired up must not " +
            "read as a limit that passed.");

        dispatcher.Executed.ShouldBeEmpty("and the dependency was not called.");
    }

    /// <summary>A limiter that cannot answer refuses too.</summary>
    /// <remarks>
    /// The case that separates this design from the plausible one. A store outage means the
    /// engine does not know whether this caller is inside the budget, and admitting on doubt
    /// turns an outage of the limiter into an unbounded flood of whatever it was bounding —
    /// which is the failure the limiter existed to prevent, arriving through the limiter.
    /// </remarks>
    [Fact]
    public async Task AnUnreachableLimiterRefusesRatherThanAdmits()
    {
        var limiter = new CountingRateLimiter(budget: 100)
        {
            Unreachable = new Error("redis.down", "no connection", ErrorCategory.Unavailable),
        };

        var dispatcher = new RecordingDispatcher();

        var result = await new FlowEngine(new FakeClock(T0), rateLimiter: limiter)
            .ExecuteAsync(Plan(TwoPerHour), dispatcher, Plans.Invocation, Ct);

        result.Error!.Code.ShouldBe(FlowErrors.RateLimiterUnavailableCode);

        result.Error.Message.ShouldContain(
            "no connection",
            Case.Sensitive,
            "the store's own reason travels, so an operator can tell 'not wired up' from " +
            "'wired up and down' without reading the flow.");

        dispatcher.Executed.ShouldBeEmpty();
    }

    /// <summary>The bucket a scope keys by is the scope's, not the capability's alone.</summary>
    /// <remarks>
    /// A <c>Tenant</c>-scoped limit on two tenants is two budgets. A limiter that ignored the
    /// scope would let one tenant starve the rest, which is <c>docs/10 §11</c>'s
    /// "rate limiting only globally" anti-pattern with the declaration saying otherwise.
    /// </remarks>
    [Fact]
    public async Task TenantScopedBudgetsAreSeparatePerTenant()
    {
        var limiter = new CountingRateLimiter(budget: 1);

        var chain = PolicyChain.ForStep(
            PolicySet.Named("per-tenant").RateLimit(permits: 1, TimeSpan.FromHours(1), RateLimitScope.Tenant),
            Plans.Validate);

        var plan = Plan(chain);
        var engine = new FlowEngine(new FakeClock(T0), rateLimiter: limiter);

        var acme = Plans.Invocation with { TenantId = "acme" };
        var globex = Plans.Invocation with { TenantId = "globex" };

        (await engine.ExecuteAsync(plan, new RecordingDispatcher(), acme, Ct)).IsSuccess.ShouldBeTrue();

        (await engine.ExecuteAsync(plan, new RecordingDispatcher(), globex, Ct)).IsSuccess.ShouldBeTrue(
            "globex has its own permit. One budget for both would be a global limit wearing a " +
            "per-tenant declaration.");

        (await engine.ExecuteAsync(plan, new RecordingDispatcher(), acme, Ct)).IsSuccess.ShouldBeFalse(
            "and acme's own permit is spent.");

        limiter.Keys.Distinct(StringComparer.Ordinal).Count().ShouldBe(
            2, "two tenants, two buckets.");
    }

    // ------------------------------------------------------------ stage 3 · Integrity

    /// <summary>What the policed step below contributes to the state bag.</summary>
    private static Validated Payment { get; } = new("GB33BUKB20201555555555", 42m);

    /// <summary>A step declaring a 24-hour window, and nothing else.</summary>
    private static PolicyChain OneDayWindow { get; } = PolicyChain.ForStep(
        PolicySet.Named("integrity").Idempotency(TimeSpan.FromHours(24), IdempotencyScope.Global),
        Plans.Validate);

    /// <summary>What a step contributes to its state bag, as the generator would describe it.</summary>
    /// <remarks>
    /// Built the way the generated <c>DescribeStep</c> builds it — a named member with its own
    /// generated metadata, composed inside <see cref="JournalPayload"/> so the document goes
    /// through the one redaction pass. A double that hand-assembled JSON would prove nothing
    /// about the exit the guard is on.
    /// </remarks>
    private static StepJournalEntry Describes(Validated value, params string[] sensitiveMembers) =>
        StepJournalEntry.Of(
            null,
            JournalPayload.OfState(
                [JournalMember.Of("validated", value, PolicyContracts.Default)],
                sensitiveMembers));

    /// <summary>
    /// A repeated key is answered from the record, and the capability is not called again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The assertion the whole of stage 3 exists to make true, and it is the dispatch
    /// count that carries it.</strong> An idempotency window that recorded diligently and
    /// dispatched anyway would satisfy every assertion about the result and none about the
    /// duplicate — and a key that never replays is indistinguishable from the inert version it
    /// replaces.
    /// </para>
    /// <para>
    /// The second execution also has to be <em>answered</em>, not merely skipped: the restored
    /// document is asserted, because a replay that skipped the dispatch and restored nothing
    /// would run the steps after the frontier against values no step produced — which is the
    /// half-executing policy ADR-0025 rejects and <c>RestoreState</c>'s own contract calls out.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARepeatedKeyIsReplayedRatherThanDispatched()
    {
        var store = new RecordingIdempotencyStore();
        var engine = new FlowEngine(new FakeClock(T0), idempotency: store);
        var plan = Plan(OneDayWindow);

        var first = new RecordingDispatcher
        {
            Describe = (index, _) => index == 0 ? Describes(Payment) : StepJournalEntry.Nothing,
        };

        (await engine.ExecuteAsync(plan, first, Plans.Invocation, Ct)).IsSuccess.ShouldBeTrue();

        first.Executed.ShouldBe([0, 1], "the first caller runs the whole flow.");

        store.Records.Count.ShouldBe(1, "and one record was written for the policed step.");

        var second = new RecordingDispatcher
        {
            Describe = (index, _) => index == 0 ? Describes(Payment) : StepJournalEntry.Nothing,
        };

        (await engine.ExecuteAsync(plan, second, Plans.Invocation, Ct)).IsSuccess.ShouldBeTrue();

        second.Executed.ShouldBe(
            [1],
            "Step 0 was answered from its record and never dispatched; step 1 declares no " +
            "window and ran. A window that dispatched anyway would have recorded diligently " +
            "and deduplicated nothing.");

        second.Restored.Count.ShouldBe(
            1,
            "and the replay restored what the first execution produced. A replay that skipped " +
            "the dispatch and restored nothing would leave every later step binding values no " +
            "step produced.");

        second.Restored[0].ShouldContain(
            "GB33BUKB20201555555555",
            Case.Sensitive,
            "the record is the document, not a marker that one exists.");
    }

    /// <summary>A key another caller is inside is refused, not run concurrently.</summary>
    /// <remarks>
    /// <c>docs/10 §7</c>: "the in-flight state matters: without it, two concurrent requests with
    /// the same key both execute. This is the most common bug in hand-rolled idempotency." The
    /// refusal carries the holder's remaining lease, which is §7's <c>Retry-After</c>.
    /// </remarks>
    [Fact]
    public async Task AKeyAnotherCallerHoldsIsRefusedRatherThanRunTwice()
    {
        var store = new RecordingIdempotencyStore { EverythingIsHeldBysomebodyElse = true };
        var dispatcher = new RecordingDispatcher();

        var result = await new FlowEngine(new FakeClock(T0), idempotency: store)
            .ExecuteAsync(Plan(OneDayWindow), dispatcher, Plans.Invocation, Ct);

        result.Error!.Code.ShouldBe(FlowErrors.IdempotencyInProgressCode);

        result.Error.Category.ShouldBe(
            ErrorCategory.Conflict,
            "nothing is down and nothing is over budget — two callers are contending for one " +
            "outcome, and the right answer is to come back and read it.");

        dispatcher.Executed.ShouldBeEmpty(
            "and the capability was not entered a second time, which is the whole point.");
    }

    /// <summary>A step that failed leaves its key free for the next caller.</summary>
    /// <remarks>
    /// <c>docs/10 §8</c>'s "negative caching: off — stale failures are worse than a retry", one
    /// stage earlier and sharper: a recorded failure would be replayed for the declared window,
    /// so one transient outage would make a key unusable for a day — and the caller's remedy,
    /// presenting it again, is exactly what would keep failing.
    /// </remarks>
    [Fact]
    public async Task AFailedStepRecordsNothingAndFreesItsKey()
    {
        var store = new RecordingIdempotencyStore();
        var engine = new FlowEngine(new FakeClock(T0), idempotency: store);
        var plan = Plan(OneDayWindow);

        var failing = new RecordingDispatcher()
            .FailAt(0, new Error("order.validate_failed", "the ledger is down", ErrorCategory.Unavailable));

        (await engine.ExecuteAsync(plan, failing, Plans.Invocation, Ct)).IsSuccess.ShouldBeFalse();

        store.Records.ShouldBeEmpty("a failure is not an outcome worth replaying.");
        store.Abandoned.ShouldBe(1, "and the claim was given back rather than left to lapse.");

        var retrying = new RecordingDispatcher
        {
            Describe = (index, _) => index == 0 ? Describes(Payment) : StepJournalEntry.Nothing,
        };

        (await engine.ExecuteAsync(plan, retrying, Plans.Invocation, Ct)).IsSuccess.ShouldBeTrue(
            "the same key runs again, because the first attempt recorded nothing. A window that " +
            "recorded the failure would have made this key unusable for twenty-four hours, and " +
            "presenting it again is the caller's only remedy.");

        retrying.Executed.ShouldBe([0, 1]);
    }

    /// <summary>
    /// A result the redaction pass had to change is not recorded, and the step says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>ADR-0038's guard, and the executable half of FLOWX1039.</strong> The flow below
    /// declares <c>DebtorIban</c> sensitive, so the document that would be recorded carries
    /// <c>[redacted]</c> where the value was. Replaying it would hand a later step the
    /// placeholder as if somebody had computed it — a fabricated answer, returned with a
    /// success, to a caller who has no way to tell.
    /// </para>
    /// <para>
    /// The step <em>fails</em>, which is a real cost: the capability has already been
    /// dispatched. That is the correct direction, and the alternative is the record that lies.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARedactedResultIsNeverRecorded()
    {
        var store = new RecordingIdempotencyStore();

        var dispatcher = new RecordingDispatcher
        {
            Describe = (index, _) => index == 0
                ? Describes(Payment, "DebtorIban")
                : StepJournalEntry.Nothing,
        };

        var result = await new FlowEngine(new FakeClock(T0), idempotency: store)
            .ExecuteAsync(Plan(OneDayWindow), dispatcher, Plans.Invocation, Ct);

        result.Error!.Code.ShouldBe(
            FlowErrors.IdempotencyNotReplayableCode,
            "The recorded result would carry [redacted] where an IBAN was, and replaying that " +
            "is worse than not replaying at all: the second caller gets a plausible wrong " +
            "answer with a success beside it.");

        store.Records.ShouldBeEmpty(
            "and nothing was written. A store that held the redacted document would hand it " +
            "back to the next caller whether or not this step reported anything.");

        store.Abandoned.ShouldBe(1, "the claim is released, so the key is not wedged.");

        dispatcher.Executed.ShouldBe(
            [0],
            "the capability did run — the guard is on the recording, not on the dispatch, so " +
            "the effect happened and the flow then refused to lie about it.");
    }

    /// <summary>A window whose result is clean is recorded, on the same shape.</summary>
    /// <remarks>
    /// The control for the test above. Without it, a guard that refused <em>every</em> recording
    /// would pass that one, and "nothing was recorded" would stop meaning anything.
    /// </remarks>
    [Fact]
    public async Task AResultWithNoMarkedMemberIsRecorded()
    {
        var store = new RecordingIdempotencyStore();

        var dispatcher = new RecordingDispatcher
        {
            Describe = (index, _) => index == 0
                ? Describes(Payment)
                : StepJournalEntry.Nothing,
        };

        var result = await new FlowEngine(new FakeClock(T0), idempotency: store)
            .ExecuteAsync(Plan(OneDayWindow), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeTrue("the same document, with no member declared sensitive.");

        store.Records.Count.ShouldBe(
            1,
            "so the guard is about the redaction and not about recording at all — the two " +
            "tests differ by one entry in SensitiveMembers.");
    }

    /// <summary>A dispatcher that describes no state bag cannot claim a window either.</summary>
    /// <remarks>
    /// The second cause behind one refusal. A record describing nothing would make the
    /// declaration look satisfied while every step after the frontier bound values no step
    /// produced — ADR-0025's "an idempotency in-flight guard with no replay", arriving through
    /// the dispatcher instead of through the design.
    /// </remarks>
    [Fact]
    public async Task ADispatcherThatDescribesNothingCannotBeReplayed()
    {
        var store = new RecordingIdempotencyStore();

        var result = await new FlowEngine(new FakeClock(T0), idempotency: store)
            .ExecuteAsync(Plan(OneDayWindow), new RecordingDispatcher(), Plans.Invocation, Ct);

        result.Error!.Code.ShouldBe(FlowErrors.IdempotencyNotReplayableCode);
        result.Error.Message.ShouldContain("describes no state bag", Case.Sensitive);
        store.Records.ShouldBeEmpty();
    }

    /// <summary>A declared window with no store registered refuses rather than dispatches.</summary>
    /// <remarks>
    /// <see cref="ARateLimitWithNoStoreRefusesRatherThanAdmits"/>'s argument, one stage down:
    /// dispatching on doubt is the duplicate the window was declared to prevent.
    /// </remarks>
    [Fact]
    public async Task AnIdempotencyWindowWithNoStoreRefusesRatherThanDispatches()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(Plan(OneDayWindow), dispatcher, Plans.Invocation, Ct);

        result.Error!.Code.ShouldBe(FlowErrors.IdempotencyUnavailableCode);
        dispatcher.Executed.ShouldBeEmpty();
    }

    /// <summary>The key is the invocation's own, narrowed by the capability.</summary>
    /// <remarks>
    /// Two things at once, and both are ADR-0037's. The key <em>contains</em>
    /// <c>ctx.IdempotencyKey</c>, rather than something minted beside it — the plan's
    /// instruction was to use the stable key that already exists. And it contains the capability
    /// id, so two policed steps of one flow under one invocation key do not share a record and
    /// replay each other.
    /// </remarks>
    [Fact]
    public async Task TheKeyIsTheInvocationsOwnNarrowedByCapability()
    {
        var store = new RecordingIdempotencyStore();

        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create("order.policy", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(0, Plans.Validate, policies: OneDayWindow),
                StepNode.ForCapability(
                    1,
                    Plans.Reserve,
                    policies: PolicyChain.ForStep(
                        PolicySet.Named("integrity").Idempotency(TimeSpan.FromHours(24), IdempotencyScope.Global),
                        Plans.Reserve)),
            ]));

        var dispatcher = new RecordingDispatcher
        {
            Describe = (_, _) => Describes(Payment),
        };

        (await new FlowEngine(new FakeClock(T0), idempotency: store)
            .ExecuteAsync(plan, dispatcher, Plans.Invocation, Ct)).IsSuccess.ShouldBeTrue();

        var keys = store.Keys.ToList();

        keys.Count.ShouldBe(2, "two policed steps, two claims.");

        keys.Distinct(StringComparer.Ordinal).Count().ShouldBe(
            2,
            "and two distinct keys. One key for both would make the second step replay the " +
            "first step's record — the flow would answer 'reserve' with what 'validate' produced.");

        foreach (var key in keys)
        {
            key.ShouldContain(
                Plans.Invocation.IdempotencyKey,
                Case.Sensitive,
                "the record is keyed by ctx.IdempotencyKey, which is stable across the flow and " +
                "across every attempt of a retried step, rather than by a second identity " +
                "minted here.");
        }

        keys[0].ShouldContain(Plans.Validate.Id, Case.Sensitive);
        keys[1].ShouldContain(Plans.Reserve.Id, Case.Sensitive);
    }
}

/// <summary>A step result carrying a member a flow might mark <c>[Sensitive]</c>.</summary>
internal sealed record Validated(string DebtorIban, decimal Amount);

/// <summary>The generated context <see cref="JournalMember.Of{T}(string, T, System.Text.Json.Serialization.JsonSerializerContext)"/> needs.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(Validated))]
internal sealed partial class PolicyContracts : System.Text.Json.Serialization.JsonSerializerContext;

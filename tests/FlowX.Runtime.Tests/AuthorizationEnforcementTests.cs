using System.Security.Claims;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// What the engine does with a declared authorisation stance: four of the five are decided
/// before the step is dispatched, and the fifth is refused at build time instead.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every test below asserted nothing before this package, because nothing existed to
/// assert against.</strong> <c>Authorization</c> was declared on the capability, published to
/// <c>flowx.manifest.json</c>, compared by <c>flowx diff</c>'s <c>FLOWX-DIFF-015</c> and
/// required by <c>FLOWX1010</c> and <c>FLOWX1030</c> — and a grep of <c>src/FlowX.Runtime</c>
/// and <c>src/FlowX.Hosting</c> for <c>Authorization</c>, <c>Permission</c> or
/// <c>Authorize</c> returned zero lines. A published contract, a breaking-change rule and two
/// build errors, over a step the engine dispatched without looking.
/// </para>
/// <para>
/// <strong>A control that has no test showing it refuse is decoration.</strong> So each
/// stance is asserted twice — once permitting what it should, once refusing what it should —
/// and the refusal is asserted to be a <see cref="Result{T}"/> failure carrying
/// <see cref="ErrorCategory.Forbidden"/> rather than an exception, which is
/// <a href="../../docs/adr/ADR-0007-result-over-exceptions.md">ADR-0007</a> and
/// <a href="../../docs/adr/ADR-0028-a-refusal-is-a-result-failure.md">ADR-0028</a>.
/// </para>
/// <para>
/// <strong><see cref="Authorization.Internal"/> has no refusing test and that is the finding,
/// not an omission.</strong> <see cref="AnInternalStancePermitsAStepBecauseNoTriggerCanAddressACapability"/>
/// carries the argument.
/// </para>
/// </remarks>
public sealed class AuthorizationEnforcementTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --------------------------------------------------------------- stanced descriptors

    private static CapabilityDescriptor Public { get; } = CapabilityDescriptor.Create(
        "catalogue.browse", "1.0.0", isIdempotent: true, Authorization.Public);

    private static CapabilityDescriptor Authenticated { get; } = CapabilityDescriptor.Create(
        "order.validate", "1.0.0", isIdempotent: true, Authorization.Authenticated);

    private static CapabilityDescriptor NeedsPaymentWrite { get; } = CapabilityDescriptor.Create(
        "payment.capture", "2.1.0", isIdempotent: false, Authorization.Permission, "payment.write");

    private static CapabilityDescriptor Internal { get; } = CapabilityDescriptor.Create(
        "inventory.release", "1.0.0", isIdempotent: true, Authorization.Internal);

    // ------------------------------------------------------------------------ principals

    /// <summary>Nobody. What an unauthenticated HTTP request produces.</summary>
    private static ClaimsPrincipal? Anonymous => null;

    /// <summary>
    /// A principal with an identity and no authentication type, which is what
    /// <c>new ClaimsPrincipal()</c> gives you.
    /// </summary>
    /// <remarks>
    /// Asserted separately from <see cref="Anonymous"/> because the two are different
    /// mistakes and only one of them is obvious. A non-null <see cref="ClaimsPrincipal"/>
    /// whose identity is unauthenticated is exactly what a null check would wave through,
    /// and it is what ASP.NET Core puts on <c>HttpContext.User</c> when no scheme
    /// authenticated the request.
    /// </remarks>
    private static ClaimsPrincipal Unauthenticated => new(new ClaimsIdentity());

    private static ClaimsPrincipal SignedIn =>
        new(new ClaimsIdentity(authenticationType: "Test"));

    private static ClaimsPrincipal Holding(string permission) => new(
        new ClaimsIdentity([new Claim("permission", permission)], authenticationType: "Test"));

    private static ClaimsPrincipal WithScopes(string scopes) => new(
        new ClaimsIdentity([new Claim("scope", scopes)], authenticationType: "Test"));

    // ----------------------------------------------------------------------------- plans

    private static ExecutionPlan OneStep(CapabilityDescriptor capability) =>
        ExecutionPlan.Create(
            FlowDescriptor.Create("order.place", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([StepNode.ForCapability(0, capability)]));

    private static FlowInvocation By(ClaimsPrincipal? principal) =>
        new("corr", "idem", TenantId: null, Deadline: null, Principal: principal);

    private static Task<FlowExecutionResult> Run(
        ExecutionPlan plan, ClaimsPrincipal? principal, RecordingDispatcher dispatcher) =>
        new FlowEngine(new FakeClock(T0)).ExecuteAsync(plan, dispatcher, By(principal), Ct).AsTask();

    // ------------------------------------------------------------------ Public: permits

    /// <summary>An anonymous caller reaches a <see cref="Authorization.Public"/> step.</summary>
    /// <remarks>
    /// The permit is the whole assertion. <c>Public</c> is the zero value of its enum, so a
    /// check that refused it would refuse every hand-built descriptor in the repository —
    /// and a check that never reached it would be indistinguishable from no check at all.
    /// </remarks>
    [Fact]
    public async Task APublicStancePermitsAnAnonymousCaller()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await Run(OneStep(Public), Anonymous, dispatcher);

        result.IsSuccess.ShouldBeTrue("Authorization.Public admits anyone, by declaration.");
        dispatcher.Executed.ShouldBe([0]);
    }

    // ------------------------------------------------- Authenticated: permits and refuses

    /// <summary>A signed-in caller reaches an <see cref="Authorization.Authenticated"/> step.</summary>
    [Fact]
    public async Task AnAuthenticatedStancePermitsASignedInCaller()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await Run(OneStep(Authenticated), SignedIn, dispatcher);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.Executed.ShouldBe([0]);
    }

    /// <summary>An anonymous caller does not.</summary>
    /// <remarks>
    /// <strong>The capability is never entered.</strong> Asserted as loudly as the refusal
    /// itself: an authorisation check that runs after the dispatch has authorised nothing,
    /// because the effect has already happened.
    /// </remarks>
    [Fact]
    public async Task AnAuthenticatedStanceRefusesAnAnonymousCaller()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await Run(OneStep(Authenticated), Anonymous, dispatcher);

        result.IsFailure.ShouldBeTrue("order.validate declares Authorization.Authenticated and nobody is signed in.");
        result.Error!.Category.ShouldBe(ErrorCategory.Forbidden);
        result.Error.Code.ShouldBe(AuthorizationErrors.NotAuthenticatedCode);

        dispatcher.Executed.ShouldBeEmpty(
            "The step is refused before it is dispatched. A check after the call has " +
            "authorised nothing — the effect has already happened.");
    }

    /// <summary>A non-null principal with an unauthenticated identity does not either.</summary>
    [Fact]
    public async Task AnAuthenticatedStanceRefusesANonNullButUnauthenticatedPrincipal()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await Run(OneStep(Authenticated), Unauthenticated, dispatcher);

        result.IsFailure.ShouldBeTrue(
            "ASP.NET Core puts a non-null ClaimsPrincipal on every request. A null check " +
            "would admit every anonymous caller.");
        result.Error!.Category.ShouldBe(ErrorCategory.Forbidden);
        dispatcher.Executed.ShouldBeEmpty();
    }

    // ----------------------------------------------------- Permission: permits and refuses

    /// <summary>A caller holding the named permission reaches the step.</summary>
    [Fact]
    public async Task APermissionStancePermitsAPrincipalHoldingIt()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await Run(OneStep(NeedsPaymentWrite), Holding("payment.write"), dispatcher);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.Executed.ShouldBe([0]);
    }

    /// <summary>A space-delimited <c>scope</c> claim carries permissions too.</summary>
    /// <remarks>
    /// The shape every OAuth 2.0 access token uses. Reading only a <c>permission</c> claim
    /// would mean the stance held for no real token this platform is ever handed.
    /// </remarks>
    [Fact]
    public async Task APermissionStanceReadsASpaceDelimitedScopeClaim()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await Run(
            OneStep(NeedsPaymentWrite), WithScopes("orders.read payment.write orders.write"), dispatcher);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.Executed.ShouldBe([0]);
    }

    /// <summary>A signed-in caller who does not hold it is refused.</summary>
    /// <remarks>
    /// <strong>Authenticated is not authorised.</strong> This is the test that separates the
    /// two stances: the caller has a valid identity, and that is not the question the
    /// capability asked.
    /// </remarks>
    [Fact]
    public async Task APermissionStanceRefusesASignedInCallerWithoutIt()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await Run(OneStep(NeedsPaymentWrite), Holding("orders.read"), dispatcher);

        result.IsFailure.ShouldBeTrue("payment.capture requires 'payment.write' and this caller holds 'orders.read'.");
        result.Error!.Category.ShouldBe(ErrorCategory.Forbidden);
        result.Error.Code.ShouldBe(AuthorizationErrors.PermissionDeniedCode);

        dispatcher.Executed.ShouldBeEmpty();
    }

    /// <summary>A scope that merely contains the permission as a substring is not a grant.</summary>
    /// <remarks>
    /// <c>payment.write.admin</c> and <c>not.payment.write</c> both contain
    /// <c>payment.write</c>. A contains-check would grant on either, which is how a
    /// permission model becomes a prefix model nobody chose.
    /// </remarks>
    [Fact]
    public async Task APermissionStanceRefusesAScopeThatMerelyContainsTheName()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await Run(
            OneStep(NeedsPaymentWrite), WithScopes("not.payment.write payment.written"), dispatcher);

        result.IsFailure.ShouldBeTrue("A permission is a whole token in the scope list, never a substring of one.");
        result.Error!.Category.ShouldBe(ErrorCategory.Forbidden);
        dispatcher.Executed.ShouldBeEmpty();
    }

    /// <summary>An anonymous caller is refused before the permission is even looked for.</summary>
    [Fact]
    public async Task APermissionStanceRefusesAnAnonymousCaller()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await Run(OneStep(NeedsPaymentWrite), Anonymous, dispatcher);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Category.ShouldBe(ErrorCategory.Forbidden);
        dispatcher.Executed.ShouldBeEmpty();
    }

    // -------------------------------------------------------------------------- Internal

    /// <summary>
    /// An <see cref="Authorization.Internal"/> step runs, whoever triggered the flow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This stance has no refusing test, and the reason is a property of the trigger
    /// model rather than a gap in this file.</strong>
    /// <c>docs/15-Security.md §4</c> asks "invoked from a flow, not a trigger?" and routes a
    /// "no" to a 403. In FlowX a trigger addresses a <em>flow</em> and never a capability —
    /// <c>[HttpTrigger]</c> is declared on a <c>Flow&lt;,&gt;</c>, which is
    /// <a href="../../docs/adr/ADR-0004-universal-trigger-model.md">ADR-0004</a> — so every
    /// capability invocation that exists is reached from a flow's step loop and the "no"
    /// branch is unreachable.
    /// </para>
    /// <para>
    /// <strong>The samples settle it beyond argument.</strong>
    /// <c>samples/workflow/OnboardEmployeeFlow</c> declares
    /// <c>[HttpTrigger("POST", "/api/v1/onboarding")]</c> and calls <c>hardware.order</c>,
    /// <c>equipment.assign</c> and <c>welcome.send</c> — all three
    /// <c>Authorization.Internal</c> — as ordinary forward steps. Any reading under which an
    /// externally triggered flow may not reach an <c>Internal</c> capability would refuse the
    /// repository's own reference application.
    /// </para>
    /// <para>
    /// So the stance is enforced by construction and not by this check, it is said out loud
    /// here rather than skipped in silence, and
    /// <see cref="EveryStanceIsEitherDecidedByTheEngineOrRefusedAtBuildTime"/> is what stops
    /// the silence returning.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnInternalStancePermitsAStepBecauseNoTriggerCanAddressACapability()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await Run(OneStep(Internal), Anonymous, dispatcher);

        result.IsSuccess.ShouldBeTrue(
            "A trigger addresses a flow, never a capability, so a capability reached at all " +
            "is reached from a flow — which is what Internal permits.");

        dispatcher.Executed.ShouldBe([0]);
    }

    // ------------------------------------------------------------------- the whole set

    /// <summary>
    /// Every member of <see cref="Authorization"/> is either decided by the engine or named
    /// by a diagnostic, and none is silently unhandled.
    /// </summary>
    /// <remarks>
    /// The gate against the defect this package fixes returning by a different door. A sixth
    /// stance added to the enum, or a fifth that quietly stops being decided, turns this red
    /// rather than passing because a <c>switch</c> fell through to a default that permits.
    /// </remarks>
    [Fact]
    public void EveryStanceIsEitherDecidedByTheEngineOrRefusedAtBuildTime()
    {
        var undecided = Enum.GetValues<Authorization>()
            .Where(static stance => !StepAuthorization.IsDecidedAtRunTime(stance)
                                 && !StepAuthorization.IsRefusedAtBuildTime(stance))
            .ToArray();

        undecided.ShouldBeEmpty(
            "A stance that is neither decided at run time nor refused at build time is a " +
            "published authorisation contract that nothing enforces and nothing reports — " +
            "the exact defect FLOWX1037 and this file exist to end.");
    }

    /// <summary>
    /// A plan whose every stance permits unconditionally leaves the gating flag false.
    /// </summary>
    /// <remarks>
    /// ADR-0023's bargain, applied to this stage: what is counted is what can <em>refuse</em>,
    /// never what was declared. A flow of <c>Public</c> and <c>Internal</c> steps takes the
    /// path it always took, so budget B2 is untouched for it.
    /// </remarks>
    [Fact]
    public void APlanWhoseStancesCannotRefuseReportsNoAuthorizedSteps()
    {
        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create("order.browse", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(0, Public),
                StepNode.ForCapability(1, Internal),
            ]));

        plan.HasAuthorizedSteps.ShouldBeFalse(
            "Public and Internal admit every caller, so nothing on this plan can be refused " +
            "and the step loop must not be sent looking.");
    }

    /// <summary>A plan carrying one refusable stance reports it.</summary>
    [Fact]
    public void APlanCarryingARefusableStanceReportsAuthorizedSteps()
    {
        OneStep(NeedsPaymentWrite).HasAuthorizedSteps.ShouldBeTrue();
    }

    // ----------------------------------------------------------- refusal is not an exception

    /// <summary>
    /// The refusal arrives as a value on the result, and nothing is thrown.
    /// </summary>
    /// <remarks>
    /// <a href="../../docs/adr/ADR-0007-result-over-exceptions.md">ADR-0007</a>: a refusal is
    /// a business outcome the caller was always going to have to handle, not a defect. If it
    /// threw, every trigger's consumer loop would need a catch for it and a bus consumer
    /// would dead-letter a message that was simply not allowed.
    /// </remarks>
    [Fact]
    public async Task ARefusalIsAResultFailureAndNotAnException()
    {
        var dispatcher = new RecordingDispatcher();

        Exception? thrown = null;
        FlowExecutionResult result = default;

        try
        {
            result = await Run(OneStep(NeedsPaymentWrite), Anonymous, dispatcher);
        }
#pragma warning disable CA1031 // The assertion is precisely that nothing of any type escapes.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            thrown = exception;
        }

        thrown.ShouldBeNull(
            "A refusal is a value on the result. Thrown, every trigger's consumer loop would " +
            "need a catch for it and a bus consumer would dead-letter a message that was " +
            "merely not permitted.");

        result.IsFailure.ShouldBeTrue();
        result.Error!.Category.ShouldBe(ErrorCategory.Forbidden);
        result.Error.Category.IsTerminal().ShouldBeTrue("A refusal is never retried, and never dead-lettered as a fault.");
        result.Error.Category.ToHttpStatusCode().ShouldBe(403);
    }

    /// <summary>A refused step is not retried, whatever the step's retry policy says.</summary>
    /// <remarks>
    /// The check sits outside the retry loop on purpose. Retrying a denial asks the same
    /// question of the same principal and gets the same answer, three times, while the
    /// backoff spends the flow's deadline — and it would turn one audit event into several.
    /// </remarks>
    [Fact]
    public async Task ARefusedStepIsNotRetried()
    {
        var idempotentAndPermissioned = CapabilityDescriptor.Create(
            "payment.refund", "2.1.0", isIdempotent: true, Authorization.Permission, "payment.write");

        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create("order.refund", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(
                    0,
                    idempotentAndPermissioned,
                    policies: PolicyChain.ForStep(
                        PolicySet.Named("retry-three").Retry(attempts: 3), idempotentAndPermissioned)),
            ]));

        var clock = new FakeClock(T0);
        var dispatcher = new RecordingDispatcher();

        var result = await new FlowEngine(clock)
            .ExecuteAsync(plan, dispatcher, By(SignedIn), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Category.ShouldBe(ErrorCategory.Forbidden);

        dispatcher.Executed.ShouldBeEmpty();
        clock.Delays.ShouldBeEmpty("A denial is not a transient fault, so no backoff is waited.");
    }

    // ------------------------------------------------------------------ identity plumbing

    /// <summary>
    /// The principal the invocation carries is the one the flow's context reports.
    /// </summary>
    /// <remarks>
    /// <c>FlowExecutionContext.Principal</c> was <c>=&gt; null</c>, unconditionally, while
    /// <c>FlowContext.Principal</c> had been declared since the first commit and
    /// <c>TriggerHeaders</c> had carried a <see cref="ClaimsPrincipal"/> all along. The
    /// abstraction existed and the wire was cut at the last inch.
    /// </remarks>
    [Fact]
    public async Task TheContextReportsThePrincipalTheInvocationCarried()
    {
        var caller = Holding("payment.write");

        var dispatcher = new RecordingDispatcher { Observe = static context => context.Principal };

        var result = await Run(OneStep(NeedsPaymentWrite), caller, dispatcher);

        result.IsSuccess.ShouldBeTrue();

        dispatcher.Observed.ShouldHaveSingleItem()
            .ShouldBeSameAs(caller, "The engine must hand the capability the caller the trigger validated.");
    }

    /// <summary>
    /// A descriptor built with no stance is not gated, and that is only reachable by hand.
    /// </summary>
    /// <remarks>
    /// <c>FLOWX1010</c> is an error, so no <em>compiled</em> capability can reach the runtime
    /// without a stance: every descriptor the generator emits carries one. A descriptor built
    /// by hand — in this file, in a benchmark — may not, and it takes the ungated path rather
    /// than a default. A default would be a stance nobody wrote, which is the thing
    /// <c>NoPermissiveDefaults</c> and <c>FLOWX1010</c> both exist to refuse.
    /// </remarks>
    [Fact]
    public async Task AHandBuiltDescriptorWithNoStanceIsNotGated()
    {
        var unstanced = CapabilityDescriptor.Create("order.validate", "1.0.0", isIdempotent: true);

        var plan = OneStep(unstanced);
        plan.HasAuthorizedSteps.ShouldBeFalse();

        var dispatcher = new RecordingDispatcher();
        var result = await Run(plan, Anonymous, dispatcher);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.Executed.ShouldBe([0]);
    }
}

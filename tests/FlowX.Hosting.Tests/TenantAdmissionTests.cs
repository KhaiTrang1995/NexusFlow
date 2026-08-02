using System.Security.Claims;
using FlowX.Conformance.InMemory;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Hosting.Tests;

/// <summary>
/// Admission, for a deployment that declares a tenant isolation level: what is refused, what
/// is admitted, and what a deployment that declares none goes on paying — which is nothing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>These are the refusals, and <c>TenantIsolationTests</c> is the isolation.</strong>
/// The two halves are deliberately tested apart because they fail apart: a runtime that
/// refused correctly and scoped nothing would leak to anyone holding an instance id, and one
/// that scoped correctly and refused nothing would admit untenanted calls that then write rows
/// no tenant can read back. Neither half is sufficient and this file owns the first.
/// </para>
/// <para>
/// <strong>Every refusal here is a <see cref="Result"/> failure with a category, never an
/// exception</strong>
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0007-result-over-exceptions.md">ADR-0007</a>).
/// A bus consumer that dead-lettered a message for naming no tenant would turn a
/// configuration mistake into data loss.
/// </para>
/// </remarks>
public sealed class TenantAdmissionTests
{
    private const string TenantA = "acme";
    private const string TenantB = "globex";

    private static readonly CapabilityDescriptor Validate =
        CapabilityDescriptor.Create("order.validate", "1.0.0", isIdempotent: true);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // -----------------------------------------------------------------------------------
    // A deployment that declares multi-tenancy refuses an untenanted call
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// A deployment declaring <see cref="TenantIsolation.Row"/> refuses a call that names no
    /// tenant, rather than defaulting it.
    /// </summary>
    /// <remarks>
    /// <strong>The alternative is the whole bug.</strong> A default tenant lets the call
    /// proceed, the reads succeed, and the wrong customer's data comes back — which is why
    /// <c>docs/16 §9</c> lists it as an anti-pattern and why the refusal is asserted here
    /// rather than left to be inferred from the resolver's unit tests.
    /// </remarks>
    [Fact]
    public async Task AMultiTenantDeploymentRefusesAnUntenantedCall()
    {
        var host = NewHost(TenantIsolation.Row);

        var outcome = await host.RunAsync(
            EphemeralPlan(), new NoopDispatcher(), Anonymous(), Cancellation);

        outcome.IsFailure.ShouldBeTrue(
            "a deployment that declares it isolates by tenant admitted a call carrying no " +
            "tenant. Everything that call went on to write is a row no tenant owns.");

        outcome.Error!.Code.ShouldBe(TenantErrors.TenantRequiredCode);
        outcome.Error.Category.ShouldBe(
            ErrorCategory.Forbidden,
            "the category is what a transport maps and what a retry policy reads. Forbidden " +
            "is terminal, which is correct: asking again with the same token gets the same " +
            "answer, and the repair is a token with a tenant claim.");
    }

    /// <summary>
    /// The refusal is an outcome, not an exception, and no step ran.
    /// </summary>
    /// <remarks>
    /// <c>CompletedSteps</c> is the assertion that matters. A refusal after the first step
    /// would mean the call was admitted and stopped, which for a capability with an effect is
    /// a payment taken from an untenanted caller.
    /// </remarks>
    [Fact]
    public async Task ARefusedCallRunsNoStepAtAll()
    {
        var host = NewHost(TenantIsolation.Row);
        var dispatcher = new NoopDispatcher();

        var outcome = await host.RunAsync(
            EphemeralPlan(), dispatcher, Anonymous(), Cancellation);

        outcome.CompletedSteps.ShouldBe(0);
        dispatcher.Executed.ShouldBe(
            0,
            "admission runs before the engine, so a refused call must not have reached a " +
            "capability. docs/16 §3 requires it: rejected 'before a flow instance exists'.");
    }

    // -----------------------------------------------------------------------------------
    // Refused is distinguishable from not configured
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// The same untenanted call a multi-tenant deployment refuses is admitted unchanged by a
    /// deployment that declares no isolation.
    /// </summary>
    /// <remarks>
    /// <strong>This is the "distinguishable from not configured" requirement, asserted as a
    /// pair rather than as a property.</strong> One call, two deployments, opposite outcomes —
    /// which is the only way to show that the refusal is caused by the declared level and not
    /// by the call being untenanted, and that a single-tenant deployment has not been broken
    /// by a feature it did not switch on.
    /// </remarks>
    [Fact]
    public async Task ASingleTenantDeploymentAdmitsTheCallAMultiTenantOneRefuses()
    {
        var refused = await NewHost(TenantIsolation.Row)
            .RunAsync(EphemeralPlan(), new NoopDispatcher(), Anonymous(), Cancellation);

        var admitted = await NewHost(TenantIsolation.None)
            .RunAsync(EphemeralPlan(), new NoopDispatcher(), Anonymous(), Cancellation);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(TenantErrors.TenantRequiredCode);

        admitted.IsSuccess.ShouldBeTrue(
            "a single-tenant deployment must be untouched by all of this. Every call it has " +
            "ever made carries no tenant, and refusing them would be the feature breaking " +
            "every deployment that did not ask for it.");
    }

    /// <summary>
    /// <see cref="TenantResolution.NotConfigured"/> and a refusal both carry no tenant and are
    /// not the same answer.
    /// </summary>
    /// <remarks>
    /// The distinction stated at the type rather than only through the host, because it is the
    /// one a caller of <see cref="ITenantResolver"/> has to get right: both have a null
    /// <c>TenantId</c>, and reading only that field collapses "this deployment does not
    /// isolate" into "this call may proceed without a tenant".
    /// </remarks>
    [Fact]
    public void ARefusalAndANotConfiguredAnswerAreNotTheSameAnswer()
    {
        var notConfigured = TenantResolution.NotConfigured;
        var refused = TenantResolution.Refuse("no tenant claim");

        notConfigured.TenantId.ShouldBeNull();
        refused.TenantId.ShouldBeNull();

        notConfigured.Refused.ShouldBeFalse();
        refused.Refused.ShouldBeTrue(
            "if these two were indistinguishable, the convenient reading of a null tenant — " +
            "carry on — would be the cross-tenant read this whole feature exists to refuse.");

        refused.Reason.ShouldNotBeEmpty();
        notConfigured.Reason.ShouldBeEmpty();
    }

    // -----------------------------------------------------------------------------------
    // A caller cannot name a tenant its claims do not support
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// A caller authenticated into one tenant cannot run a flow as another, in either
    /// direction.
    /// </summary>
    /// <remarks>
    /// <strong>This is the escalation the resolver exists for.</strong> Before it,
    /// <c>FlowInvocation.TenantId</c> was whatever the transport put there and nothing
    /// compared it with anything — so a caller able to influence that field owned every
    /// tenant. Both directions, because a check that happens to be written the right way round
    /// for one arrangement is not a check.
    /// </remarks>
    [Fact]
    public async Task ACallerCannotRunAFlowAsATenantItsClaimsDoNotSupport()
    {
        var host = NewHost(TenantIsolation.Row);

        var aAsB = await host.RunAsync(
            EphemeralPlan(), new NoopDispatcher(), AsTenant(TenantA, asserting: TenantB), Cancellation);

        var bAsA = await host.RunAsync(
            EphemeralPlan(), new NoopDispatcher(), AsTenant(TenantB, asserting: TenantA), Cancellation);

        aAsB.IsFailure.ShouldBeTrue(
            "a caller whose claims place it in tenant A ran a flow as tenant B.");

        bAsA.IsFailure.ShouldBeTrue("and the same the other way.");

        aAsB.Error!.Code.ShouldBe(TenantErrors.CrossTenantDeniedCode);
        bAsA.Error!.Code.ShouldBe(TenantErrors.CrossTenantDeniedCode);
    }

    /// <summary>
    /// A caller whose claims carry a tenant is admitted, and the tenant that travels onward is
    /// the one the claims supported.
    /// </summary>
    /// <remarks>
    /// <strong>Derived, not read.</strong> The invocation asserts nothing here, so the tenant
    /// on the context can only have come from the claims — which is the difference between a
    /// resolver and a pass-through, and the reason this asserts the value rather than merely
    /// that the call succeeded.
    /// </remarks>
    [Fact]
    public async Task TheTenantThatTravelsOnwardIsTheOneTheClaimsSupported()
    {
        var host = NewHost(TenantIsolation.Row);
        var dispatcher = new NoopDispatcher();

        var outcome = await host.RunAsync(
            EphemeralPlan(), dispatcher, AsTenant(TenantA, asserting: null), Cancellation);

        outcome.IsSuccess.ShouldBeTrue(
            outcome.IsFailure ? outcome.Error!.ToString() : string.Empty);

        dispatcher.TenantId.ShouldBe(
            TenantA,
            "the flow context carries the derived tenant. Had it carried the asserted one it " +
            "would be null here, and every row this flow wrote would belong to nobody.");
    }

    // -----------------------------------------------------------------------------------
    // A trigger with no caller attests its tenant
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// An attested tenant is admitted with no principal anywhere, and travels onward as the
    /// flow's own.
    /// </summary>
    /// <remarks>
    /// <strong>The gap this closes.</strong> Only HTTP carries a caller, so before this the
    /// three platform-initiated triggers reached the resolver with no claims and were refused —
    /// a change, a broker message and a cron occurrence could not start a flow in a tenanted
    /// deployment at all. What each of them supplies is not a claim and not an assertion either:
    /// no caller is involved, and the value came from the schema the change was read out of, the
    /// field the publisher wrote, or the directory the schedule was fanned out over.
    /// </remarks>
    [Fact]
    public async Task AnAttestedTenantIsAdmittedWithNoPrincipal()
    {
        var host = NewHost(TenantIsolation.Row);
        var dispatcher = new NoopDispatcher();

        var outcome = await host.RunAsync(
            EphemeralPlan(), dispatcher, Attesting(TenantA), Cancellation);

        outcome.IsSuccess.ShouldBeTrue(
            outcome.IsFailure ? outcome.Error!.ToString() : string.Empty);

        dispatcher.TenantId.ShouldBe(
            TenantA, "and it is the tenant the trigger named, not one the host defaulted to.");
    }

    /// <summary>
    /// An attestation naming no tenant is refused, not admitted untenanted.
    /// </summary>
    /// <remarks>
    /// <strong>This refusal is what makes a trigger hold its work.</strong> A change scan reads
    /// <c>tenant.required</c> and leaves its cursor where it was; a bus scan leaves the message
    /// unacknowledged. Admitting it instead — the answer a continuation gets, because a
    /// continuation's absent tenant means the deployment does not isolate — would write rows no
    /// tenant can read back and report success.
    /// </remarks>
    [Fact]
    public async Task AnAttestationThatNamesNoTenantIsRefused()
    {
        var host = NewHost(TenantIsolation.Row);

        var outcome = await host.RunAsync(
            EphemeralPlan(), new NoopDispatcher(), Attesting(tenantId: null), Cancellation);

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error!.Code.ShouldBe(TenantErrors.TenantRequiredCode);
    }

    /// <summary>
    /// An attested start still has every step's stance decided, and an authenticated step under
    /// one is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The reason attestation is a second flag rather than a reuse of
    /// <c>IsContinuation</c>.</strong> That flag would have carried the tenant with one field
    /// fewer and would also have suppressed authorisation — correct for a sweep resuming an
    /// instance the platform already admitted, and wrong for a start, where nothing has decided
    /// anything yet. A cron occurrence is not a caller, so a step that requires one is refused
    /// rather than run on its behalf.
    /// </para>
    /// <para>
    /// The refusal is the same <c>Forbidden</c> an anonymous HTTP call gets at the same step,
    /// which is the point: an attested start is a start.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnAttestedStartDoesNotSkipStepAuthorization()
    {
        var host = NewHost(TenantIsolation.Row);
        var dispatcher = new NoopDispatcher();

        var outcome = await host.RunAsync(
            AuthenticatedPlan(), dispatcher, Attesting(TenantA), Cancellation);

        outcome.IsFailure.ShouldBeTrue(
            "the step declares Authorization.Authenticated and an attested start carries no " +
            "principal, so the stance is decided against an absence and refuses.");

        outcome.Error!.Category.ShouldBe(ErrorCategory.Forbidden);

        dispatcher.Executed.ShouldBe(
            0, "and the capability never ran, which is what a bypass would have let happen.");
    }

    // -----------------------------------------------------------------------------------
    // Fixtures
    // -----------------------------------------------------------------------------------

    private static FlowHost NewHost(TenantIsolation isolation) =>
        new(
            new FlowEngine(new FixedClock()),
            new FlowXOptions
            {
                ApplicationName = "Sample.App",
                NodeName = "node-1",
                TenantIsolation = isolation,
            },
            durability: null);

    /// <summary>A one-step flow that journals nothing, so admission is all that is under test.</summary>
    private static ExecutionPlan EphemeralPlan() => ExecutionPlan.Create(
        FlowDescriptor.Create(
            "order.place", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromMinutes(5)),
        StepGraph.Create([StepNode.ForCapability(0, Validate)]));

    /// <summary>The same flow with a step no anonymous caller may reach.</summary>
    private static ExecutionPlan AuthenticatedPlan() => ExecutionPlan.Create(
        FlowDescriptor.Create(
            "order.place", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromMinutes(5)),
        StepGraph.Create([
            StepNode.ForCapability(
                0,
                CapabilityDescriptor.Create(
                    "order.validate", "1.0.0", isIdempotent: true, Authorization.Authenticated)),
        ]));

    /// <summary>An invocation from nobody, naming no tenant — what an anonymous call is.</summary>
    private static FlowInvocation Anonymous() => new("corr-1", "idem-1");

    /// <summary>
    /// What a change scan, a bus scan or a schedule sweep produces: a tenant and no caller.
    /// </summary>
    private static FlowInvocation Attesting(string? tenantId) =>
        new("corr-1", "idem-1", tenantId, TenantAttested: true);

    /// <summary>
    /// An invocation from a caller whose validated claims place it in one tenant, optionally
    /// asserting a different one.
    /// </summary>
    private static FlowInvocation AsTenant(string claimed, string? asserting) =>
        new("corr-1", "idem-1", asserting, Principal: PrincipalFor(claimed));

    /// <summary>A principal whose identity is authenticated and carries a tenant claim.</summary>
    /// <remarks>
    /// The authentication type is what makes <c>Identity.IsAuthenticated</c> true, and it has
    /// to be: an unauthenticated principal can carry any claim at all, so a resolver that read
    /// one would be reading a header with extra steps.
    /// </remarks>
    private static ClaimsPrincipal PrincipalFor(string tenantId) =>
        new(new ClaimsIdentity([new Claim("tid", tenantId)], "test"));

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    /// <summary>Records that it ran, and what tenant the context carried when it did.</summary>
    private sealed class NoopDispatcher : IStepDispatcher
    {
        public int Executed { get; private set; }

        public string? TenantId { get; private set; }

        public ValueTask<StepOutcome> ExecuteAsync(
            int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            Executed++;
            TenantId = ctx.TenantId;

            return ValueTask.FromResult(StepOutcome.Success);
        }

        public ValueTask<StepOutcome> CompensateAsync(
            int stepIndex, FlowContext ctx, CancellationToken ct) =>
            ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This double runs plans with no branch step.");

        public int Select(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This double runs plans with no switch step.");

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to begin.");

        public FlowContext EnterIteration(
            int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to enter.");
    }
}

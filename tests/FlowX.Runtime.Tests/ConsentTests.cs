using System.Security.Claims;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// Stage 2's declarable kind, against a real engine: what a declared purpose refuses, where it
/// refuses it, and what a refusal tells the caller.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Written the way <c>QuotaAndValidationTests</c> is, and for its reason.</strong>
/// Nearly every test here asserts that something did <em>not</em> happen, which is the shape
/// that passes against an engine doing nothing at all — so each one names the dispatch count it
/// expects, and the file opens with the positive control: a caller whose purpose matches gets
/// through and the capability is entered.
/// </para>
/// <para>
/// <strong>The refusing tests come in a pair, and the pair is the point.</strong> A purpose
/// limitation has two ways to fail — the caller asserted nothing, or asserted something else —
/// and only the first of them is the common case in production, because it is what every
/// credential issued before the policy existed does. They carry distinct codes for
/// <a href="../../docs/adr/ADR-0029-a-refusal-is-a-result-failure.md">ADR-0029</a> §2.1's
/// reason and one category, so a transport maps them identically and an operator can still tell
/// "start issuing the claim" from "this caller is doing something it was never granted".
/// </para>
/// <para>
/// <strong>Nothing here uses a store or a double for the decision.</strong> The comparison is
/// two strings the process already holds, which is what keeps a stage-2 policy synchronous and
/// keeps <a href="../../docs/adr/ADR-0030-policy-stance-is-refused-at-build-time.md">ADR-0030</a>
/// shut: an Identity-stage decision that needed I/O is the thing that record declined to build.
/// </para>
/// </remarks>
public sealed class ConsentTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A step that may be invoked for direct care and for nothing else.</summary>
    private static PolicyChain ForTreatment { get; } = PolicyChain.ForStep(
        PolicySet.Named("clinical").Consent("treatment"), Plans.Validate);

    private static ExecutionPlan Plan(PolicyChain forward) =>
        ExecutionPlan.Create(
            FlowDescriptor.Create(
                "patient.intake", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(0, Plans.Validate, policies: forward),
                StepNode.ForCapability(1, Plans.Reserve),
            ]));

    private static FlowInvocation For(string? purpose) =>
        new("corr-1", "idem-1", TenantId: "clinic", Purpose: purpose);

    private static Task<FlowExecutionResult> Run(FlowInvocation invocation, RecordingDispatcher dispatcher) =>
        new FlowEngine(new FakeClock(T0)).ExecuteAsync(Plan(ForTreatment), dispatcher, invocation, Ct).AsTask();

    // ---------------------------------------------------------------- the positive control

    /// <summary>An invocation made for the declared purpose reaches the step.</summary>
    /// <remarks>
    /// The control every refusing test below is measured against. A consent policy that refused
    /// everything would satisfy each of them and would take the step permanently out of
    /// service, which is a failure mode this file would otherwise be blind to.
    /// </remarks>
    [Fact]
    public async Task ADeclaredPurposeAdmitsAnInvocationMadeForIt()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await Run(For("treatment"), dispatcher);

        result.IsSuccess.ShouldBeTrue(
            "the invocation asserts the purpose the step declares, which is the whole of what " +
            "the policy asks.");

        dispatcher.Executed.ShouldBe([0, 1]);
    }

    // ----------------------------------------------------------------- absent, and refused

    /// <summary>
    /// An invocation that asserted no purpose is refused, and the capability is never entered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Deny by default, and this is the test that makes the policy worth
    /// declaring.</strong> Admitting an unstated purpose would leave the gate holding for
    /// callers who had thought about it and open for everybody else — the control failing open
    /// on exactly the population it exists for. Every credential issued before this policy
    /// existed lands here, which is why it is the common case rather than the corner one.
    /// </para>
    /// <para>
    /// Asserted against the dispatch count as well as the outcome: a purpose limitation that
    /// refused the flow <em>after</em> calling the step has limited nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnInvocationCarryingNoPurposeIsRefusedBeforeTheDispatch()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await Run(For(purpose: null), dispatcher);

        result.IsSuccess.ShouldBeFalse(
            "a step declaring a purpose is not satisfied by an invocation that named none. " +
            "Admitting here would open the gate for every caller who never heard of it.");

        result.Error!.Code.ShouldBe(
            FlowErrors.ConsentPurposeAbsentCode,
            "distinct from consent_purpose_not_covered because the repairs are opposite: this " +
            "one is a credential that has to start carrying a purpose claim.");

        result.Error.Category.ShouldBe(
            ErrorCategory.Forbidden,
            "the category every stage-2 refusal carries, which is what makes it a 403 and " +
            "keeps it out of every retry set.");

        dispatcher.Executed.ShouldBeEmpty(
            "order.validate was never entered. A check after the dispatch has limited nothing.");
    }

    /// <summary>A whitespace purpose is an absent one, not a wrong one.</summary>
    /// <remarks>
    /// The claim value <c>" "</c> would otherwise be compared, fail, and report "you were made
    /// for another purpose" — true and useless. Read as absent, the refusal names the repair.
    /// <c>InvocationPurpose.FromClaims</c> already drops it at the transport; this pins the
    /// engine's own floor, for the invocation a host constructs directly.
    /// </remarks>
    [Fact]
    public async Task AWhitespacePurposeIsReadAsNoneRatherThanAsAMismatch()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await Run(For("   "), dispatcher);

        result.Error!.Code.ShouldBe(FlowErrors.ConsentPurposeAbsentCode);
        dispatcher.Executed.ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ wrong, and refused

    /// <summary>A consent to be treated does not admit an invocation made for research.</summary>
    /// <remarks>
    /// <strong>The load-bearing test, and the one a widening would turn red.</strong> Purpose
    /// limitation is the whole of GDPR Article 5(1)(b): a person who agreed to be treated has
    /// not agreed to be studied. Anybody who decides the comparison should be a hierarchy —
    /// that treatment subsumes research, or that a prefix match is friendlier — turns this test
    /// red and nothing else in the repository.
    /// </remarks>
    [Fact]
    public async Task APurposeThatIsNotTheDeclaredOneIsRefused()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await Run(For("research"), dispatcher);

        result.Error!.Code.ShouldBe(
            FlowErrors.ConsentPurposeNotCoveredCode,
            "a purpose is granted for what it names and does not extend to a second.");

        result.Error.Category.ShouldBe(ErrorCategory.Forbidden);
        dispatcher.Executed.ShouldBeEmpty();
    }

    /// <summary>A purpose that merely contains the declared one is not a match.</summary>
    /// <remarks>
    /// <c>StepAuthorization.Grants</c> refuses a substring for a permission and this refuses one
    /// for a purpose, for the identical reason: a contains-check turns a limitation into a
    /// prefix model nobody chose, and <c>treatment-research</c> would then satisfy a step
    /// declared for <c>treatment</c>.
    /// </remarks>
    [Fact]
    public async Task APurposeThatMerelyContainsTheDeclaredOneIsRefused()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await Run(For("treatment-research"), dispatcher);

        result.Error!.Code.ShouldBe(FlowErrors.ConsentPurposeNotCoveredCode);
        dispatcher.Executed.ShouldBeEmpty();
    }

    /// <summary>The comparison is ordinal, so case is part of the identity.</summary>
    /// <remarks>
    /// Deliberate rather than incidental. A case fold is a normalisation the manifest does not
    /// publish, so an engine that applied one would be deciding against a value nobody can read
    /// off the contract — and whether <c>Treatment</c> and <c>treatment</c> are one purpose is a
    /// deployment's decision about its own claim vocabulary, not the platform's.
    /// </remarks>
    [Fact]
    public async Task ThePurposeComparisonIsOrdinalAndNotCaseFolded()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await Run(For("Treatment"), dispatcher);

        result.Error!.Code.ShouldBe(FlowErrors.ConsentPurposeNotCoveredCode);
        dispatcher.Executed.ShouldBeEmpty();
    }

    // ------------------------------------------------------------- what the refusal reveals

    /// <summary>A refusal names the purpose the step wants and never the one the caller sent.</summary>
    /// <remarks>
    /// <a href="../../docs/adr/ADR-0029-a-refusal-is-a-result-failure.md">ADR-0029</a> §2.2's
    /// rule, asked of the policy beside the stance. The declared purpose is already public — it
    /// is in <c>flowx.manifest.json</c> — so naming it tells the caller what to ask for. The
    /// asserted one came off the caller's credential, and putting a claim value into an
    /// RFC 7807 body is the information disclosure <c>docs/15 §3</c>'s Boundary 1 row refuses.
    /// </remarks>
    [Fact]
    public async Task ARefusalNamesTheDeclaredPurposeAndNotTheAssertedOne()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await Run(For("research"), dispatcher);

        var error = result.Error!;

        error.Message.ShouldContain("treatment", Case.Sensitive, "the caller is told what to ask for.");

        error.Message.ShouldNotContain(
            "research",
            Case.Sensitive,
            "the asserted purpose came off the caller's credential and is not echoed back.");

        error.Data!["purpose"].ShouldBe(
            "treatment",
            "the structured detail carries the declared purpose, for the reason the message does.");

        error.Data.Values.ShouldNotContain(
            "research",
            "and it carries nothing of the caller's — a detail is rendered into the problem " +
            "document's extensions exactly as the message is rendered into its detail.");
    }

    // --------------------------------------------------------------------- what it is not

    /// <summary>A step declaring no consent is not gated by a purpose the invocation lacks.</summary>
    /// <remarks>
    /// The other half of the positive control, and what keeps the policy a declaration rather
    /// than a mode. Step 1 of every plan above declares nothing and runs on an invocation with
    /// no purpose at all; if it did not, the gate would be a property of the flow rather than of
    /// the step that asked for it.
    /// </remarks>
    [Fact]
    public async Task AStepDeclaringNoConsentRunsWithoutAPurpose()
    {
        var dispatcher = new RecordingDispatcher();
        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create(
                "patient.intake", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(0, Plans.Validate),
                StepNode.ForCapability(1, Plans.Reserve),
            ]));

        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(plan, dispatcher, For(purpose: null), Ct);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.Executed.ShouldBe([0, 1]);
    }

    /// <summary>
    /// A platform continuation is not re-decided, so a sweep does not refuse a consented step.
    /// </summary>
    /// <remarks>
    /// <a href="../../docs/adr/ADR-0028-identity-arrives-on-the-invocation.md">ADR-0028</a>
    /// §2.3's argument, which applies to a purpose word for word: a timer sweep and a recovery
    /// scan carry no purpose because they carry no caller, and a journal row keeps no claims by
    /// design. Deciding against that absence would make <c>.Delay(...)</c> a construct no author
    /// could place before a consent-gated step, and would turn a node restart into a 403.
    /// </remarks>
    [Fact]
    public async Task APlatformContinuationIsNotGatedByAPurposeItCannotHave()
    {
        var dispatcher = new RecordingDispatcher();
        var sweep = new FlowInvocation("corr-1", "idem-1", TenantId: "clinic", IsContinuation: true);

        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(Plan(ForTreatment), dispatcher, sweep, Ct);

        result.IsSuccess.ShouldBeTrue(
            "the platform is continuing an instance it already admitted; nobody is asking for " +
            "anything, so there is no purpose to limit.");

        dispatcher.Executed.ShouldBe([0, 1]);
    }

    /// <summary>A blank declared purpose is no policy, rather than a policy nobody can pass.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The run-time floor under <c>FLOWX1057</c>, and it is deliberately
    /// permissive.</strong> A purpose nobody wrote is not a purpose nobody may satisfy: a step
    /// taken permanently out of service by a typo in a <c>static readonly PolicySet</c> is a
    /// worse failure than the build error that stops it shipping. The rule is the mechanism;
    /// this is the floor for the chain the compiler could not read.
    /// </para>
    /// <para>
    /// Which is exactly why the rule is an error rather than a warning — the pair only works
    /// with both halves, and this test is the reason the other one has to exist.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ABlankDeclaredPurposeLeavesTheStepUngated()
    {
        var dispatcher = new RecordingDispatcher();
        var chain = PolicyChain.ForStep(PolicySet.Named("clinical").Consent(" "), Plans.Validate);

        StepPolicy.From(chain).HasConsent.ShouldBeFalse(
            "read as undeclared, which is what FLOWX1057 exists to make unshippable.");

        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(Plan(chain), dispatcher, For(purpose: null), Ct);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.Executed.ShouldBe([0, 1]);
    }

    // ------------------------------------------------------------------ where a purpose is read

    /// <summary>A purpose is read from a validated claim, and an anonymous caller asserts none.</summary>
    /// <remarks>
    /// <strong>The half that would fail open if it were written the obvious way.</strong>
    /// ASP.NET Core hands every anonymous request a <see cref="ClaimsPrincipal"/> that can carry
    /// any claim at all, so a reader that checked only for the claim's presence would let an
    /// unauthenticated caller name whatever purpose opened the step — a purpose limitation
    /// whose input the limited party supplies. The question asked is the one
    /// <c>StepAuthorization</c> and <c>ClaimTenantResolver</c> both ask of the same object.
    /// </remarks>
    [Fact]
    public void APurposeIsReadOnlyFromAnAuthenticatedPrincipal()
    {
        var claim = new Claim("purpose", "treatment");

        InvocationPurpose.FromClaims(new ClaimsPrincipal(new ClaimsIdentity([claim], "Test")))
            .ShouldBe("treatment");

        InvocationPurpose.FromClaims(new ClaimsPrincipal(new ClaimsIdentity([claim])))
            .ShouldBeNull(
                "an unauthenticated identity's claims are a header with extra steps. Reading " +
                "them would let an anonymous caller assert its own purpose.");

        InvocationPurpose.FromClaims(null).ShouldBeNull();
    }

    /// <summary>A blank claim value is no purpose.</summary>
    [Fact]
    public void AWhitespaceClaimValueIsNotAPurpose()
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("purpose", "  ")], authenticationType: "Test"));

        InvocationPurpose.FromClaims(principal).ShouldBeNull();
    }

    /// <summary>
    /// A scope claim is not consulted, so a token granted three capabilities asserts no purpose.
    /// </summary>
    /// <remarks>
    /// The one deliberate difference from <c>StepAuthorization.PermissionClaimTypes</c>, which
    /// does read <c>scope</c> and <c>scp</c>. A scope is a list of things a credential may
    /// <em>do</em>; a purpose is the single reason this call is being made. Treating one entry
    /// of a space-delimited scope list as a purpose would mean a broad token asserted several
    /// purposes at once, which is the opposite of a limitation.
    /// </remarks>
    [Fact]
    public void AScopeClaimIsNotReadAsAPurpose()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("scope", "treatment records.write")], authenticationType: "Test"));

        InvocationPurpose.FromClaims(principal).ShouldBeNull(
            "a scope says what a credential may do, not what this call is for.");
    }
}

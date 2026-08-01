using Shouldly;
using Xunit;

namespace FlowX.Core.Tests;

/// <summary>
/// The policy chain is where <a href="../../docs/adr/ADR-0011-fixed-policy-stage-order.md">ADR-0011</a>
/// stops being a document and becomes a data structure. Every test here names the
/// production incident class the rule prevents — a rule whose reason is not written
/// down is a rule someone will "simplify" later.
/// </summary>
public sealed class PolicyChainTests
{
    [Fact]
    public void OrdersByStageRegardlessOfDeclarationOrder()
    {
        // Declared deliberately backwards: efficiency, then resilience, then admission.
        var declared = PolicySet.Named("backwards")
            .Cache(TimeSpan.FromMinutes(5))
            .Timeout(TimeSpan.FromSeconds(2))
            .RateLimit(100, TimeSpan.FromSeconds(1));

        var chain = PolicyChain.Create(declared, Fixtures.ValidateOrder);

        chain.Ordered.Select(static p => p.Stage).ShouldBe(
            [PolicyStage.Admission, PolicyStage.Resilience, PolicyStage.Efficiency],
            "Execution order is the platform's, not the author's. A team that writes " +
            "Cache before RateLimit must still get rate limiting first.");
    }

    [Fact]
    public void RetryOnANonIdempotentCapabilityIsRejected()
    {
        var withRetry = PolicySet.Named("risky").Retry(attempts: 3);

        var error = Should.Throw<InvalidFlowPlanException>(
            () => PolicyChain.Create(withRetry, Fixtures.CapturePayment));

        error.Message.ShouldContain("payment.capture");
        error.Message.ShouldContain("Idempotent");
        // Retrying a payment capture is a duplicate charge. This is FLOWX1014 at
        // compile time; enforcing it here too means a programmatically constructed
        // plan cannot bypass the compiler's guarantee.
    }

    [Fact]
    public void RetryOnAnIdempotentCapabilityIsAllowed()
    {
        var chain = PolicyChain.Create(
            PolicySet.Named("safe").Retry(attempts: 3),
            Fixtures.ReserveInventory);

        chain.Ordered.ShouldContain(static p => p.Kind == "Retry");
    }

    [Fact]
    public void CachingACapabilityWithSideEffectsIsRejected()
    {
        var error = Should.Throw<InvalidFlowPlanException>(
            () => PolicyChain.Create(
                PolicySet.Named("wrong").Cache(TimeSpan.FromMinutes(1)),
                Fixtures.ReserveInventory));

        error.Message.ShouldContain("inventory.reserve");
        // Caching a write returns a stale success without performing the write.
        // FLOWX1018.
    }

    [Fact]
    public void EmptyChainIsValidForEveryCapability()
    {
        PolicyChain.Create(PolicySet.Empty, Fixtures.CapturePayment).Ordered.ShouldBeEmpty();
        PolicyChain.Empty.Ordered.ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ the two-way split

    /// <summary>
    /// One declared set describes two different things, and the two are separated by kind.
    /// </summary>
    /// <remarks>
    /// <c>.WithPolicy(...)</c> names one set for a step that may carry a compensation, and the
    /// set legitimately says something about both: <c>Timeout</c> bounds the step,
    /// <c>CompensationRetry</c> bounds its undo. They cannot go into one chain, because the two
    /// wrap different capabilities and are checked against different idempotency declarations —
    /// which is the reason <see cref="StepNode.CompensationPolicies"/> is a separate property
    /// in the first place.
    /// </remarks>
    [Fact]
    public void ForStepKeepsEverythingExceptTheCompensationRetry()
    {
        var declared = PolicySet.Named("ledger-post")
            .Timeout(TimeSpan.FromSeconds(5))
            .Audit("financial")
            .CompensationRetry(attempts: 5);

        var chain = PolicyChain.ForStep(declared, Fixtures.ReserveInventory);

        chain.Ordered.Select(static p => p.Kind).ShouldBe(
            ["Timeout", "Audit"],
            "Audit is a forward-path Consistency policy and belongs to the step. Only the " +
            "compensation retry wraps the undo.");
    }

    [Fact]
    public void ForCompensationKeepsOnlyTheCompensationRetry()
    {
        var declared = PolicySet.Named("ledger-post")
            .Timeout(TimeSpan.FromSeconds(5))
            .Audit("financial")
            .CompensationRetry(attempts: 5);

        var chain = PolicyChain.ForCompensation(declared, Fixtures.ReleaseInventory);

        chain.Ordered.Select(static p => p.Kind).ShouldBe(
            [CompensationPolicy.CompensationRetryKind],
            "A Timeout on the step says nothing about its undo, and arming one there would " +
            "be the policy engine arriving early.");

        CompensationPolicy.From(chain).Attempts.ShouldBe(5);
    }

    /// <summary>
    /// The pairing the split exists for: a capture that must not be retried, carrying a refund
    /// that must be.
    /// </summary>
    /// <remarks>
    /// <c>PolicyChain.Create</c> validates every descriptor against the one capability it is
    /// handed, so handing it this set and <c>payment.capture</c> would refuse the
    /// <c>CompensationRetry</c> — and refuse it for the wrong capability's idempotency.
    /// Splitting first is what makes the legitimate case expressible.
    /// </remarks>
    [Fact]
    public void ACompensationRetryIsCheckedAgainstTheUndoAndNotAgainstTheStep()
    {
        var declared = PolicySet.Named("capture").CompensationRetry(attempts: 3);

        PolicyChain.ForStep(declared, Fixtures.CapturePayment).IsEmpty.ShouldBeTrue(
            "payment.capture is not idempotent, and nothing in this set wraps it.");

        PolicyChain.ForCompensation(declared, Fixtures.RefundPayment)
            .Ordered.ShouldContain(static p => p.Kind == CompensationPolicy.CompensationRetryKind);
    }

    /// <summary>FLOWX1014's rule, applied to the capability that would actually run twice.</summary>
    [Fact]
    public void ForCompensationRefusesANonIdempotentCompensatingCapability()
    {
        var error = Should.Throw<InvalidFlowPlanException>(
            () => PolicyChain.ForCompensation(
                PolicySet.Named("risky").CompensationRetry(attempts: 3),
                Fixtures.CapturePayment));

        error.Message.ShouldContain("payment.capture");
        error.Message.ShouldContain("CompensationRetry");
    }

    /// <summary>A forward Retry is still refused where it lands — on the step.</summary>
    [Fact]
    public void ForStepStillRefusesARetryOnANonIdempotentCapability()
    {
        var error = Should.Throw<InvalidFlowPlanException>(
            () => PolicyChain.ForStep(
                PolicySet.Named("risky").Retry(attempts: 3),
                Fixtures.CapturePayment));

        error.Message.ShouldContain("payment.capture");
    }

    [Fact]
    public void BothHalvesOfAnEmptySetAreEmpty()
    {
        PolicyChain.ForStep(PolicySet.Empty, Fixtures.CapturePayment).IsEmpty.ShouldBeTrue();
        PolicyChain.ForCompensation(PolicySet.Empty, Fixtures.CapturePayment).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void ForStepStillOrdersByStage()
    {
        var declared = PolicySet.Named("backwards")
            .Cache(TimeSpan.FromMinutes(5))
            .Timeout(TimeSpan.FromSeconds(2))
            .RateLimit(100, TimeSpan.FromSeconds(1));

        PolicyChain.ForStep(declared, Fixtures.ValidateOrder)
            .Ordered.Select(static p => p.Stage)
            .ShouldBe([PolicyStage.Admission, PolicyStage.Resilience, PolicyStage.Efficiency]);
    }

    [Fact]
    public void StagesWithEqualRankKeepDeclarationOrder()
    {
        var declared = PolicySet.Named("resilience")
            .Timeout(TimeSpan.FromSeconds(2))
            .CircuitBreaker(0.5, TimeSpan.FromSeconds(30))
            .Bulkhead(maxConcurrency: 8);

        var kinds = PolicyChain.Create(declared, Fixtures.ValidateOrder)
            .Ordered.Select(static p => p.Kind).ToArray();

        kinds.ShouldBe(["Timeout", "CircuitBreaker", "Bulkhead"],
            "Ordering must be stable. An unstable sort would make the emitted plan " +
            "non-deterministic, and a non-deterministic build breaks the manifest diff.");
    }
}

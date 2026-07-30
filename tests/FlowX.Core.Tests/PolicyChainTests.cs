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

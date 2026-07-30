using Shouldly;
using Xunit;

namespace FlowX.Abstractions.Tests;

/// <summary>
/// <see cref="PolicySet"/> is a declaration surface: it records intent, and
/// <c>PolicyChain</c> in FlowX.Core validates and orders it. These tests pin down
/// what the declaration preserves — chiefly that it is immutable, because a policy
/// set is meant to be a shared static constant.
/// </summary>
public sealed class PolicySetTests
{
    [Fact]
    public void EmptyCarriesNoPolicies()
    {
        PolicySet.Empty.Policies.ShouldBeEmpty();
        PolicySet.Empty.Name.ShouldBe("empty");
    }

    [Fact]
    public void NamedRejectsABlankName()
    {
        Should.Throw<ArgumentException>(() => PolicySet.Named(null!));
        Should.Throw<ArgumentException>(() => PolicySet.Named(""));
        Should.Throw<ArgumentException>(() => PolicySet.Named("   "));
    }

    [Fact]
    public void EachAdditionReturnsANewSetSoASharedConstantCannotBeMutated()
    {
        var baseSet = PolicySet.Named("gateway");
        var withTimeout = baseSet.Timeout(TimeSpan.FromSeconds(2));

        baseSet.Policies.ShouldBeEmpty(
            "A PolicySet is declared once as a static and applied by name. If adding " +
            "a policy mutated it, one flow's tweak would silently change every other " +
            "flow that shares the constant.");
        withTimeout.Policies.Length.ShouldBe(1);
        withTimeout.Name.ShouldBe("gateway");
    }

    [Fact]
    public void EveryPolicyLandsInItsFixedStage()
    {
        var set = PolicySet.Named("all")
            .RateLimit(100, TimeSpan.FromSeconds(1))
            .Idempotency(TimeSpan.FromHours(24))
            .Timeout(TimeSpan.FromSeconds(2))
            .Retry(3)
            .CircuitBreaker(0.5, TimeSpan.FromSeconds(30))
            .Bulkhead(8)
            .Cache(TimeSpan.FromMinutes(5))
            .Audit("payment");

        var stages = set.Policies.ToDictionary(static p => p.Kind, static p => p.Stage);

        stages["RateLimit"].ShouldBe(PolicyStage.Admission);
        stages["Idempotency"].ShouldBe(PolicyStage.Integrity);
        stages["Timeout"].ShouldBe(PolicyStage.Resilience);
        stages["Retry"].ShouldBe(PolicyStage.Resilience);
        stages["CircuitBreaker"].ShouldBe(PolicyStage.Resilience);
        stages["Bulkhead"].ShouldBe(PolicyStage.Resilience);
        stages["Cache"].ShouldBe(PolicyStage.Efficiency);
        stages["Audit"].ShouldBe(PolicyStage.Consistency);
    }

    [Fact]
    public void RetryDefaultsToRetryingOnlyTheRetryableCategories()
    {
        var retry = PolicySet.Named("r").Retry(3).Policies.Single();

        var categories = retry.Parameters["retryOn"].ShouldBeOfType<ErrorCategory[]>();

        categories.ShouldBe([ErrorCategory.Unavailable, ErrorCategory.Internal]);
        categories.ShouldNotContain(ErrorCategory.Validation,
            "Retrying invalid input just produces the same rejection more expensively.");
    }

    [Fact]
    public void RetryDefaultsToExponentialBackoffWithJitter()
    {
        var retry = PolicySet.Named("r").Retry(3).Policies.Single();

        var backoff = retry.Parameters["backoff"].ShouldBeOfType<Backoff>();

        backoff.Jitter.ShouldBeTrue(
            "Without jitter, every client retries a recovering dependency at the same " +
            "instant and knocks it over again.");
    }

    [Fact]
    public void ScopedPoliciesDefaultToTenantIsolation()
    {
        PolicySet.Named("c").Cache(TimeSpan.FromMinutes(1))
            .Policies.Single().Parameters["scope"].ShouldBe(CacheScope.Tenant);

        PolicySet.Named("r").RateLimit(10, TimeSpan.FromSeconds(1))
            .Policies.Single().Parameters["scope"].ShouldBe(RateLimitScope.Tenant);

        PolicySet.Named("i").Idempotency(TimeSpan.FromHours(1))
            .Policies.Single().Parameters["scope"].ShouldBe(IdempotencyScope.Tenant);
    }

    [Fact]
    public void PolicyParametersArePreservedVerbatimForTheManifest()
    {
        var timeout = PolicySet.Named("t").Timeout(TimeSpan.FromSeconds(3)).Policies.Single();

        timeout.Parameters["duration"].ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void AuditRecordsItsRedactionList()
    {
        var audit = PolicySet.Named("a").Audit("payment", "cardNumber", "cvv").Policies.Single();

        audit.Parameters["category"].ShouldBe("payment");
        audit.Parameters["redact"].ShouldBeOfType<string[]>().ShouldBe(["cardNumber", "cvv"]);
    }
}

/// <summary>Tests for the retry backoff shapes.</summary>
public sealed class BackoffTests
{
    [Fact]
    public void ExponentialJitterIsTheDefaultShape()
    {
        var backoff = Backoff.ExponentialJitter();

        backoff.Jitter.ShouldBeTrue();
        backoff.BaseDelay.ShouldBe(TimeSpan.FromMilliseconds(200));
        backoff.MaxDelay.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ExponentialWithoutJitterIsAvailableButDistinct()
    {
        Backoff.Exponential().Jitter.ShouldBeFalse();
        Backoff.Exponential().ShouldNotBe(Backoff.ExponentialJitter());
    }

    [Fact]
    public void DelaysAreOverridable()
    {
        var backoff = Backoff.ExponentialJitter(
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromSeconds(5));

        backoff.BaseDelay.ShouldBe(TimeSpan.FromMilliseconds(50));
        backoff.MaxDelay.ShouldBe(TimeSpan.FromSeconds(5));
    }
}

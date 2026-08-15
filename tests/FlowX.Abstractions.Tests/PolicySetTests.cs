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
            .Hedge(TimeSpan.FromMilliseconds(300))
            .Fallback(42)
            .Cache(TimeSpan.FromMinutes(5))
            .Audit("payment");

        var stages = set.Policies.ToDictionary(static p => p.Kind, static p => p.Stage);

        stages["RateLimit"].ShouldBe(PolicyStage.Admission);
        stages["Idempotency"].ShouldBe(PolicyStage.Integrity);
        stages["Timeout"].ShouldBe(PolicyStage.Resilience);
        stages["Retry"].ShouldBe(PolicyStage.Resilience);
        stages["CircuitBreaker"].ShouldBe(PolicyStage.Resilience);
        stages["Bulkhead"].ShouldBe(PolicyStage.Resilience);
        stages["Hedge"].ShouldBe(PolicyStage.Resilience);
        stages["Fallback"].ShouldBe(PolicyStage.Resilience);
        stages["Cache"].ShouldBe(PolicyStage.Efficiency);
        stages["Audit"].ShouldBe(PolicyStage.Consistency);
    }

    /// <summary>
    /// A fallback's constant is captured under its own static type, and files itself there.
    /// </summary>
    /// <remarks>
    /// The whole of why <c>FallbackValue</c> exists rather than a boxed <c>object</c> in the
    /// parameter dictionary: <c>FlowContext.Set&lt;T&gt;</c> is generic and the engine has no
    /// type argument, so the type has to be captured where it is known — at the declaration.
    /// <c>FLOWX1052</c> is the build-time half of the same fact, and
    /// <c>PolicyExecutionTests.AFallbackAnswersWithItsConstantWhenTheStepHasFailedForTheLastTime</c>
    /// is what asserts that a real engine puts it in a real bag under that type — this package
    /// has no <c>FlowContext</c> to write into.
    /// </remarks>
    [Fact]
    public void AFallbackCapturesItsConstantUnderItsOwnType()
    {
        var value = (FallbackValue)PolicySet.Named("f")
            .Fallback(new Reservation("none"))
            .Policies
            .Single()
            .Parameters["value"]!;

        value.Contract.ShouldBe(typeof(Reservation));
        value.Value.ShouldBe(new Reservation("none"));

        Should.Throw<ArgumentNullException>(() => value.ApplyTo(null!));
    }

    /// <summary>
    /// The capability-valued half of the same row declares a type and resolves nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A <c>Type</c> and not an id, and that is the design rather than a shortfall.</strong>
    /// A <c>PolicySet</c> is a <c>static readonly</c> field built with no step in sight, and a
    /// capability's id, version and side effects live on its <c>[Capability]</c> attribute —
    /// so reading them from here would mean reflecting at run time, which is what constraint
    /// C2 refuses. <c>PolicyChain.ForStep</c> binds the declaration to the descriptor the
    /// generated plan resolved, and <c>PolicyChainTests</c> is where that is asserted.
    /// </para>
    /// <para>
    /// Both overloads emit the one kind at the one stage, which is what makes
    /// <c>docs/10 §3</c>'s "capability or constant" one policy rather than two sharing a name.
    /// </para>
    /// </remarks>
    [Fact]
    public void AFallbackCapabilityDeclaresATypeAndLeavesItToBeBound()
    {
        var policy = PolicySet.Named("f").Fallback<Reservation>().Policies.Single();

        policy.Kind.ShouldBe("Fallback", "The same kind the constant overload emits.");
        policy.Stage.ShouldBe(PolicyStage.Resilience);

        policy.Parameters["capability"]
            .ShouldBeOfType<FallbackCapability>()
            .Capability
            .ShouldBe(typeof(Reservation));

        policy.Parameters.ContainsKey("value").ShouldBeFalse(
            "There is no constant. A degraded answer is one thing or the other, and a " +
            "descriptor carrying both would be a policy with two answers and no rule for " +
            "choosing.");
    }

    private sealed record Reservation(string Sku);

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

    /// <summary>The ISO-8601 overload is the same three numbers, written the way a flow does.</summary>
    /// <remarks>
    /// It exists because a poll's schedule is part of a flow's own declaration, beside
    /// <c>[FlowDeadline("PT6H")]</c>, rather than configuration beside an attempt count. Both
    /// bounds are required there: a base of 200 ms and a ceiling of 30 s are right for
    /// recovering from a fault and wrong for waiting on somebody else's four-hour job.
    /// </remarks>
    [Fact]
    public void TheIso8601OverloadIsTheSameShape()
    {
        var written = Backoff.Exponential("PT5S", "PT5M");

        written.BaseDelay.ShouldBe(TimeSpan.FromSeconds(5));
        written.MaxDelay.ShouldBe(TimeSpan.FromMinutes(5));
        written.Jitter.ShouldBeFalse();

        Backoff.ExponentialJitter("PT5S", "PT5M").Jitter.ShouldBeTrue();
    }

    /// <summary>And it refuses anything that is not a duration, naming the argument.</summary>
    [Fact]
    public void TheIso8601OverloadRefusesSomethingThatIsNotADuration() =>
        Should.Throw<ArgumentException>(() => Backoff.Exponential("5 seconds", "PT5M"))
            .ParamName.ShouldBe("from");

    /// <summary>The gap doubles with the attempt number and stops at the ceiling.</summary>
    /// <remarks>
    /// <para>
    /// <strong>This arithmetic has two callers, which is why it is on the type.</strong> A
    /// <c>CompensationRetry</c> asks it how long before the next undo; a <c>PollUntil</c> asks
    /// it how long before the next attempt. A second copy would have been the place the two
    /// came to disagree about what <c>Exponential(PT5S, PT5M)</c> means.
    /// </para>
    /// <para>
    /// A table rather than a statistical assertion, because the sample is a parameter: the
    /// function is pure and its cases are enumerable.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(6, 160)]

    // Five seconds doubled seven times is 320, which is past the five-minute ceiling.
    [InlineData(7, 300)]
    [InlineData(40, 300)]
    public void TheGapDoublesAndThenStopsAtTheCeiling(int attempt, int expectedSeconds) =>
        Backoff.Exponential("PT5S", "PT5M")
            .After(attempt, sample: 1d)
            .ShouldBe(TimeSpan.FromSeconds(expectedSeconds));

    /// <summary>Full jitter scales the gap by the draw, so nothing exceeds the undecorrelated one.</summary>
    /// <remarks>
    /// The property that matters at scale rather than the exact draw: a hundred thousand
    /// instances parked on one schedule must not wake in one second, and the way this shape
    /// prevents it is by spreading each gap over <c>[0, ceiling]</c>.
    /// </remarks>
    [Fact]
    public void FullJitterSpreadsTheGapOverTheWholeInterval()
    {
        var jittered = Backoff.ExponentialJitter("PT5S", "PT5M");

        jittered.After(3, sample: 0d).ShouldBe(TimeSpan.Zero);
        jittered.After(3, sample: 0.5d).ShouldBe(TimeSpan.FromSeconds(10));
        jittered.After(3, sample: 1d).ShouldBe(TimeSpan.FromSeconds(20));
    }

    /// <summary>An attempt number below one is not an attempt.</summary>
    [Fact]
    public void TheFirstAttemptIsNumberOne() =>
        Should.Throw<ArgumentOutOfRangeException>(
            () => Backoff.Exponential("PT5S", "PT5M").After(0, sample: 1d));
}

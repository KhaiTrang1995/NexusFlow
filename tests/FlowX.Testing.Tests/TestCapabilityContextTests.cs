using FlowX;
using FlowX.Testing;
using Shouldly;
using Xunit;

namespace FlowX.Testing.Tests;

/// <summary>
/// The test double's own behaviour.
/// </summary>
/// <remarks>
/// A test double needs tests for the same reason an assertion library does: everything
/// downstream trusts it. A context whose clock quietly advanced, or whose
/// <c>NewId</c> returned the same value twice, would make other people's tests pass for
/// the wrong reason — the worst failure a testing package can have.
/// </remarks>
public sealed class TestCapabilityContextTests
{
    [Fact]
    public void HasUsableDefaultsWithNoArguments()
    {
        var ctx = new TestCapabilityContext();

        ctx.IdempotencyKey.ShouldNotBeNullOrWhiteSpace();
        ctx.CorrelationId.ShouldNotBeNullOrWhiteSpace();
        ctx.CapabilityId.ShouldNotBeNullOrWhiteSpace();
        ctx.TenantId.ShouldBeNull();
        ctx.FlowInstanceId.ShouldBeNull();
    }

    [Fact]
    public void TheClockIsFixedAndNotTheWallClock()
    {
        // The whole reason the abstraction exists. A default of UtcNow would make a test
        // asserting on a timestamp pass today and fail tomorrow.
        var ctx = new TestCapabilityContext();

        ctx.UtcNow.ShouldBe(DateTimeOffset.UnixEpoch);
        ctx.UtcNow.ShouldBe(ctx.UtcNow);
    }

    [Fact]
    public void TheDeadlineIsTheClockPlusTheBudget()
    {
        var ctx = new TestCapabilityContext(
            utcNow: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            budget: TimeSpan.FromSeconds(30));

        ctx.Deadline.ShouldBe(new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero));
        ctx.TimeRemaining.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void AnExpiredBudgetReportsNoTimeRemaining()
    {
        var ctx = new TestCapabilityContext(budget: TimeSpan.Zero);

        ctx.TimeRemaining.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void NewIdIsDistinctPerCall()
    {
        // A constant would be reproducible and would hide a capability that used one id
        // where it needed two.
        var ctx = new TestCapabilityContext();

        var ids = new[] { ctx.NewId(), ctx.NewId(), ctx.NewId() };

        ids.Distinct().Count().ShouldBe(3);
        ctx.IdsIssued.ShouldBe(3);
    }

    [Fact]
    public void NewIdIsReproducibleAcrossContexts()
    {
        // Determinism is the contract. Two contexts built the same way must issue the
        // same sequence, or a test that captures an id cannot assert on it.
        var first = new TestCapabilityContext();
        var second = new TestCapabilityContext();

        new[] { first.NewId(), first.NewId() }
            .ShouldBe([second.NewId(), second.NewId()]);
    }

    [Fact]
    public void NewIdStartsFromTheSuppliedSeed()
    {
        var seed = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var ctx = new TestCapabilityContext(firstId: seed);

        ctx.NewId().ShouldBe(seed);
        ctx.NewId().ShouldNotBe(seed);
    }

    [Fact]
    public void IdsIssuedStartsAtZero()
        => new TestCapabilityContext().IdsIssued.ShouldBe(0);

    [Fact]
    public void RandomIsSeededAndReproducible()
    {
        var first = new TestCapabilityContext(randomSeed: 42);
        var second = new TestCapabilityContext(randomSeed: 42);

        first.Random.Next().ShouldBe(second.Random.Next());
    }

    [Fact]
    public void ADifferentSeedGivesADifferentSequence()
    {
        new TestCapabilityContext(randomSeed: 1).Random.Next()
            .ShouldNotBe(new TestCapabilityContext(randomSeed: 2).Random.Next());
    }

    [Fact]
    public void CarriesTheValuesItWasGiven()
    {
        var ctx = new TestCapabilityContext(
            idempotencyKey: "key-1",
            correlationId: "corr-1",
            tenantId: "acme",
            capabilityId: "payment.capture",
            flowInstanceId: "fi_1");

        ctx.IdempotencyKey.ShouldBe("key-1");
        ctx.CorrelationId.ShouldBe("corr-1");
        ctx.TenantId.ShouldBe("acme");
        ctx.CapabilityId.ShouldBe("payment.capture");
        ctx.FlowInstanceId.ShouldBe("fi_1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void RejectsABlankIdempotencyKey(string key)
    {
        // A blank key is worse than a missing one: it looks supplied, and every retry
        // deduplicates against the same empty string.
        Should.Throw<ArgumentException>(() => new TestCapabilityContext(idempotencyKey: key));
    }

    [Fact]
    public void RejectsABlankCorrelationIdAndCapabilityId()
    {
        Should.Throw<ArgumentException>(() => new TestCapabilityContext(correlationId: " "));
        Should.Throw<ArgumentException>(() => new TestCapabilityContext(capabilityId: " "));
    }

    [Fact]
    public void IsUsableAsTheAbstraction()
    {
        // The point of the type: a capability takes CapabilityContext, not this.
        CapabilityContext ctx = new TestCapabilityContext(idempotencyKey: "key-1");

        ctx.IdempotencyKey.ShouldBe("key-1");
    }
}

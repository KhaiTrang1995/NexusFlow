using System.Security.Claims;
using FlowX;
using FlowX.Testing;
using Shouldly;
using Xunit;

namespace FlowX.Testing.Tests;

/// <summary>
/// The flow context's typed bag — the part that makes generated code testable without
/// an engine.
/// </summary>
public sealed class TestFlowContextTests
{
    private sealed record Order(string Sku);

    private sealed record Reservation(string Id);

    [Fact]
    public void RoundTripsAValueByItsType()
    {
        var ctx = new TestFlowContext();
        ctx.Set(new Order("SKU-1"));

        ctx.Get<Order>().Sku.ShouldBe("SKU-1");
    }

    [Fact]
    public void WithSeedsInOneExpression()
    {
        var ctx = new TestFlowContext()
            .With(new Order("SKU-1"))
            .With(new Reservation("res-1"));

        ctx.Count.ShouldBe(2);
        ctx.Get<Order>().Sku.ShouldBe("SKU-1");
        ctx.Get<Reservation>().Id.ShouldBe("res-1");
    }

    [Fact]
    public void TheBagIsKeyedByTypeSoTheSecondValueWins()
    {
        // Exactly what the real context does. Keying by type is what makes a step bind
        // to the contract it declared rather than to a position in a list.
        var ctx = new TestFlowContext()
            .With(new Order("first"))
            .With(new Order("second"));

        ctx.Count.ShouldBe(1);
        ctx.Get<Order>().Sku.ShouldBe("second");
    }

    [Fact]
    public void GettingAnAbsentValueThrowsWithAUsefulMessage()
    {
        // Throws rather than returning default: a step reading a contract no earlier step
        // produced is a defect, and a silent null becomes a mystery two steps later.
        var error = Should.Throw<InvalidOperationException>(() => new TestFlowContext().Get<Order>());

        error.Message.ShouldContain("Order");
        error.Message.ShouldContain("Set<T>()");
    }

    [Fact]
    public void TryGetReportsAbsenceWithoutThrowing()
    {
        var ctx = new TestFlowContext();

        ctx.TryGet<Order>(out var missing).ShouldBeFalse();
        missing.ShouldBeNull();

        ctx.Set(new Order("SKU-1"));

        ctx.TryGet<Order>(out var found).ShouldBeTrue();
        found!.Sku.ShouldBe("SKU-1");
    }

    [Fact]
    public void RejectsANullValue()
        => Should.Throw<ArgumentNullException>(() => new TestFlowContext().Set<Order>(null!));

    [Fact]
    public void CarriesTheFlowIdentity()
    {
        var ctx = new TestFlowContext(flowId: "order.place", flowVersion: "2.1.0");

        ctx.FlowId.ShouldBe("order.place");
        ctx.FlowVersion.ShouldBe("2.1.0");
    }

    [Fact]
    public void RejectsABlankFlowIdentity()
    {
        Should.Throw<ArgumentException>(() => new TestFlowContext(flowId: " "));
        Should.Throw<ArgumentException>(() => new TestFlowContext(flowVersion: " "));
    }

    [Fact]
    public void DefaultsToAManualTrigger()
    {
        // Which is what a test invocation actually is. Defaulting to Http would make a
        // capability that branches on the trigger kind pass for the wrong reason.
        var ctx = new TestFlowContext(idempotencyKey: "key-1", correlationId: "corr-1");

        ctx.Trigger.Kind.ShouldBe(TriggerKind.Manual);
        ctx.Trigger.Headers.CorrelationId.ShouldBe("corr-1");
        ctx.Trigger.Headers.IdempotencyKey.ShouldBe("key-1");
        ctx.Trigger.OccurredAt.ShouldBe(ctx.UtcNow);
    }

    [Fact]
    public void AcceptsASuppliedTrigger()
    {
        var envelope = new TriggerEnvelope(
            TriggerKind.Bus,
            "kafka:orders.requested[3]",
            ReadOnlyMemory<byte>.Empty,
            new TriggerHeaders("corr-9"),
            DateTimeOffset.UnixEpoch);

        new TestFlowContext(trigger: envelope).Trigger.Source.ShouldBe("kafka:orders.requested[3]");
    }

    [Fact]
    public void CarriesAPrincipal()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u1")], "test"));

        new TestFlowContext(principal: principal).Principal.ShouldBeSameAs(principal);
    }

    [Fact]
    public void HasNoErrorUntilTheFlowFails()
    {
        var ctx = new TestFlowContext();
        ctx.Error.ShouldBeNull();

        var error = new Error("payment.declined", "no funds", ErrorCategory.Conflict);
        ctx.Fail(error);

        // A compensation runs after a failure and may read this to decide how much to
        // undo. Without Fail there was no way to reach that state.
        ctx.Error.ShouldBeSameAs(error);
    }

    [Fact]
    public void FailRejectsANullError()
        => Should.Throw<ArgumentNullException>(() => new TestFlowContext().Fail(null!));

    [Fact]
    public void InheritsTheCapabilityContextDefaults()
    {
        // Both contexts share one implementation of the defaults, so they cannot drift.
        var flow = new TestFlowContext();
        var capability = new TestCapabilityContext();

        flow.UtcNow.ShouldBe(capability.UtcNow);
        flow.Deadline.ShouldBe(capability.Deadline);
        flow.IdempotencyKey.ShouldBe(capability.IdempotencyKey);
        flow.NewId().ShouldBe(capability.NewId());
    }

    [Fact]
    public void IsUsableAsBothAbstractions()
    {
        // A generated dispatcher takes FlowContext; a capability takes CapabilityContext.
        // One object satisfies both, which is what lets a test drive a whole step.
        var ctx = new TestFlowContext();

        ((FlowContext)ctx).ShouldNotBeNull();
        ((CapabilityContext)ctx).IdempotencyKey.ShouldNotBeNullOrWhiteSpace();
    }
}

using Shouldly;
using Xunit;

namespace FlowX.Abstractions.Tests;

/// <summary>
/// Trigger attributes are metadata the compiler reads. The properties worth
/// asserting are the <em>defaults</em>: each one is a safety decision that would
/// otherwise be invisible until it caused an incident.
/// </summary>
public sealed class TriggerAttributeTests
{
    [Fact]
    public void HttpTriggerCarriesItsRouteAndIsNotIdempotentUnlessDeclared()
    {
        var trigger = new HttpTriggerAttribute("POST", "/api/v1/orders");

        trigger.Kind.ShouldBe(TriggerKind.Http);
        trigger.Method.ShouldBe("POST");
        trigger.Route.ShouldBe("/api/v1/orders");
        trigger.Idempotent.ShouldBeFalse("Idempotency is a claim the author must make explicitly.");
    }

    [Fact]
    public void KafkaTriggerBoundsInFlightRecordsByDefault()
    {
        var trigger = new KafkaTriggerAttribute("orders.requested") { Group = "order-placement" };

        trigger.Kind.ShouldBe(TriggerKind.Bus);
        trigger.MaxInFlight.ShouldBe(32);
        trigger.MaxInFlight.ShouldBeGreaterThan(0,
            "Unbounded in-flight records is not backpressure, it is a memory leak with extra steps.");
    }

    [Fact]
    public void CronTriggerDefaultsProtectAgainstTheTwoClassicSchedulerIncidents()
    {
        var trigger = new CronTriggerAttribute("0 2 * * *");

        trigger.Kind.ShouldBe(TriggerKind.Schedule);
        trigger.Overlap.ShouldBe(OverlapPolicy.Skip,
            "A run that outlives its interval must not stack on itself.");
        trigger.MissedFire.ShouldBe(MissedFirePolicy.RunOnce,
            "After a two-hour outage, RunAll would fire 120 minute-jobs at once.");
        trigger.TimeZone.ShouldBe("UTC");
    }

    [Fact]
    public void StreamTriggerDefaultsToNoLatenessAndSingleParallelism()
    {
        var trigger = new StreamTriggerAttribute("telemetry") { Window = "tumbling:1m" };

        trigger.Kind.ShouldBe(TriggerKind.Stream);
        trigger.Lateness.ShouldBe("PT0S");
        trigger.Parallelism.ShouldBe(1);
        trigger.Checkpoint.ShouldBe("PT5S");
    }

    [Fact]
    public void AgentTriggerRequiresConfirmationForSideEffectsByDefault()
    {
        var trigger = new AgentTriggerAttribute { Description = "Place an order" };

        trigger.Kind.ShouldBe(TriggerKind.Agent);
        trigger.Confirmation.ShouldBe(ConfirmationMode.RequiredForSideEffects,
            "An agent that can act without confirmation is excessive agency (OWASP LLM08).");
    }

    [Fact]
    public void ATriggerEnvelopeNormalisesEveryTransportToTheSameShape()
    {
        var headers = new TriggerHeaders("corr-1", TenantId: "acme", TraceParent: "00-abc-def-01");
        var envelope = new TriggerEnvelope(
            TriggerKind.Bus,
            "kafka:orders.requested[3]",
            new ReadOnlyMemory<byte>([1, 2, 3]),
            headers,
            DateTimeOffset.UnixEpoch);

        envelope.Kind.ShouldBe(TriggerKind.Bus);
        envelope.Headers.CorrelationId.ShouldBe("corr-1");
        envelope.Headers.TenantId.ShouldBe("acme");
        envelope.Headers.IdempotencyKey.ShouldBeNull();
        envelope.Body.Length.ShouldBe(3);
    }
}

/// <summary>
/// Tests the one piece of behaviour <see cref="CapabilityContext"/> implements
/// rather than declares.
/// </summary>
public sealed class CapabilityContextTests
{
    private sealed class FixedContext(DateTimeOffset now, DateTimeOffset deadline) : CapabilityContext
    {
        public override string CorrelationId => "corr-1";
        public override string? FlowInstanceId => null;
        public override string CapabilityId => "test.capability";
        public override string? TenantId => null;
        public override string IdempotencyKey => "key-1";
        public override DateTimeOffset Deadline { get; } = deadline;
        public override DateTimeOffset UtcNow { get; } = now;
        public override Guid NewId() => Guid.Empty;
        public override Random Random { get; } = new(0);
    }

    [Fact]
    public void TimeRemainingCountsDownToTheDeadline()
    {
        var now = DateTimeOffset.UnixEpoch;
        var context = new FixedContext(now, now.AddSeconds(30));

        context.TimeRemaining.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void TimeRemainingIsZeroRatherThanNegativeOnceTheDeadlinePasses()
    {
        var now = DateTimeOffset.UnixEpoch;
        var context = new FixedContext(now, now.AddSeconds(-5));

        context.TimeRemaining.ShouldBe(TimeSpan.Zero,
            "A negative remaining budget would be passed to a timeout as a negative " +
            "delay, which throws deep inside the runtime instead of failing the step.");
    }
}

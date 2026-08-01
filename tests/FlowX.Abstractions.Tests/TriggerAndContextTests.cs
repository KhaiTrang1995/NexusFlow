using System.Linq;
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

    // ----------------------------------------------------- the two sources of truth

    /// <summary>
    /// Every trigger attribute FlowX ships declares <c>[TriggerKind]</c>, and it agrees
    /// with the <c>Kind</c> property.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The kind is stated twice by design: <c>Kind =&gt;</c> is a property the runtime
    /// reads, and <c>[TriggerKind]</c> is the same fact as attribute data, which is the only
    /// form the compiler can read out of a referenced assembly. Two statements of one fact
    /// can disagree, and a disagreement here is silent and expensive — the flow would run on
    /// one transport family and be published in <c>flowx.manifest.json</c> as another, with
    /// nothing failing.
    /// </para>
    /// <para>
    /// <strong>What this fitness function can and cannot cover.</strong> It holds for the
    /// attributes FlowX ships, because they are in this assembly and their getters can be
    /// run. It cannot hold for a plugin's attribute, and nothing can: reading
    /// <c>Kind</c> means running a property getter, and a source generator does not run the
    /// code it compiles. A plugin that marks itself <c>Bus</c> and returns <c>Stream</c> is
    /// undetectable at compile time and is the documented cost of the design.
    /// </para>
    /// <para>
    /// Discovered by reflection rather than listed, so a sixth attribute is covered on the
    /// day it is added rather than on the day someone remembers to add it here.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryShippedTriggerAttributeDeclaresTheKindItsPropertyReturns()
    {
        var shipped = typeof(TriggerAttribute).Assembly.GetTypes()
            .Where(static t => t is { IsAbstract: false, IsPublic: true } && typeof(TriggerAttribute).IsAssignableFrom(t))
            .OrderBy(static t => t.FullName, StringComparer.Ordinal)
            .ToArray();

        shipped.ShouldNotBeEmpty("Reflection found no trigger attributes, so this asserts nothing.");

        foreach (var type in shipped)
        {
            var marker = (TriggerKindAttribute?)Attribute.GetCustomAttribute(type, typeof(TriggerKindAttribute));

            marker.ShouldNotBeNull(
                $"{type.Name} declares no [TriggerKind], so the compiler cannot read its " +
                "family and a flow declaring it would publish no trigger at all.");

            var instance = (TriggerAttribute)Activator.CreateInstance(type, DefaultArgumentsFor(type))!;

            marker!.Kind.ShouldBe(
                instance.Kind,
                $"{type.Name} declares [TriggerKind({marker.Kind})] but its Kind property " +
                $"returns {instance.Kind}. The runtime reads the property and the manifest " +
                "publishes the marker, so the flow would run as one family and be published " +
                "as another.");
        }
    }

    /// <summary>
    /// The smallest argument list that constructs the attribute, so its getter can be run.
    /// </summary>
    /// <remarks>
    /// The values are irrelevant — <c>Kind</c> is a constant expression on every one of
    /// these — so this passes <c>null</c> for a reference type and a zeroed value for
    /// anything else. Required members are a compile-time rule and reflection does not
    /// enforce them, which is what lets <c>AgentTriggerAttribute</c> be constructed here
    /// without a <c>Description</c>.
    /// </remarks>
    private static object?[] DefaultArgumentsFor(Type type)
    {
        var constructor = type.GetConstructors()
            .OrderBy(static c => c.GetParameters().Length)
            .First();

        return [.. constructor.GetParameters().Select(static p =>
            p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)];
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
        public override string? CompensatingFor => null;
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

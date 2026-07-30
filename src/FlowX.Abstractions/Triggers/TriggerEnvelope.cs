using System.Security.Claims;

namespace FlowX;

/// <summary>How a flow was activated, normalised across every transport (ADR-0004).</summary>
public enum TriggerKind
{
    /// <summary>Operator or test invocation. At-most-once.</summary>
    Manual = 0,

    /// <summary>REST, gRPC, GraphQL, webhook. At-most-once; the caller retries.</summary>
    Http = 1,

    /// <summary>Kafka, RabbitMQ, Service Bus, MQTT, SQS. At-least-once.</summary>
    Bus = 2,

    /// <summary>Cron, interval, one-shot. At-least-once.</summary>
    Schedule = 3,

    /// <summary>Continuous stream with checkpointed offsets. At-least-once.</summary>
    Stream = 4,

    /// <summary>Change data capture, outbox, file watcher. At-least-once.</summary>
    Change = 5,

    /// <summary>LLM tool call over MCP. At-most-once, subject to the same authorisation.</summary>
    Agent = 6,

    /// <summary><c>flowx run</c>. At-most-once.</summary>
    Cli = 7,
}

/// <summary>
/// Normalised trigger metadata. Header semantics are mapped once, in the platform,
/// instead of once per team per transport — a W3C <c>traceparent</c>, a Kafka header
/// and an MQTT user property all land in the same fields.
/// </summary>
/// <param name="CorrelationId">Created if absent; always propagated onward.</param>
/// <param name="TenantId">From validated claims only, never from the payload.</param>
/// <param name="Principal">The authenticated caller, if any.</param>
/// <param name="IdempotencyKey">Caller-supplied deduplication key.</param>
/// <param name="Deadline">Absolute budget; the runtime applies its own default when absent.</param>
/// <param name="TraceParent">W3C trace context, continued rather than restarted.</param>
public readonly record struct TriggerHeaders(
    string CorrelationId,
    string? TenantId = null,
    ClaimsPrincipal? Principal = null,
    string? IdempotencyKey = null,
    DateTimeOffset? Deadline = null,
    string? TraceParent = null);

/// <summary>
/// A normalised activation. Transport plugins produce it; the flow never sees which
/// transport produced it (principle P3).
/// </summary>
/// <param name="Kind">The transport family.</param>
/// <param name="Source">
/// Human-readable origin for diagnostics, e.g. <c>POST /api/v1/orders</c> or
/// <c>kafka:orders.requested[3]</c>.
/// </param>
/// <param name="Body">The raw payload, bound to the flow's input by generated code.</param>
/// <param name="Headers">Normalised metadata.</param>
/// <param name="OccurredAt">When the originating event happened, not when it was received.</param>
public readonly record struct TriggerEnvelope(
    TriggerKind Kind,
    string Source,
    ReadOnlyMemory<byte> Body,
    TriggerHeaders Headers,
    DateTimeOffset OccurredAt);

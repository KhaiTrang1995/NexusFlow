using System.Text.Json.Serialization;

namespace FlowX.Conformance;

/// <summary>
/// The contract the suite journals, carrying one member a flow would declare
/// <c>[Sensitive]</c>.
/// </summary>
/// <remarks>
/// A real contract shape rather than a string, because commitment 5 of ADR-0015 is that
/// payloads go through the generated <c>System.Text.Json</c> context — and the only way to
/// hold a store to that is to hand it something that can only be written through one.
/// </remarks>
public sealed record ConformanceOrder(string OrderId, string PaymentToken, int Quantity);

/// <summary>The event body the suite stages in the outbox.</summary>
public sealed record ConformanceEvent(string OrderId);

/// <summary>
/// The generated serialisation context for the contracts above.
/// </summary>
/// <remarks>
/// Source-generated, with no reflection fallback, so the suite exercises the same
/// AOT-compatible path a shipped flow does (ADR-0008, constraint C2).
/// </remarks>
[JsonSerializable(typeof(ConformanceOrder))]
[JsonSerializable(typeof(ConformanceEvent))]
public sealed partial class ConformanceJson : JsonSerializerContext;

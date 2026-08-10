using System.Text.Json.Serialization;

namespace FlowX.Durability.Bench;

/// <summary>
/// What a measured commit carries, so that the row written is the size a real one is.
/// </summary>
/// <remarks>
/// <strong>Not <see cref="JournalPayload.Empty"/>, and the difference is the measurement.</strong>
/// A commit with no payload writes two NULL columns; a real step boundary writes the step's
/// result and the state-bag snapshot beside it, which is more WAL, more TOAST decision-making
/// and a bigger row. Pricing the empty commit would produce a B7 number no deployment can
/// reproduce. These are deliberately modest — four short fields — so the number stays a
/// measurement of the commit path rather than of JSON serialisation.
/// </remarks>
internal sealed record BenchResult(string Reference, decimal Amount, string Currency, bool Settled);

/// <summary>The state a flow carries between steps in the rig's synthetic history.</summary>
internal sealed record BenchState(string Stage, int StepsDone, string CorrelationId);

/// <summary>
/// The generated metadata for the two payloads above.
/// </summary>
/// <remarks>
/// Source-generated rather than reflection-based because that is the only serialisation
/// FlowX's write path accepts (constraint C2, NativeAOT-safe). A rig that reached for
/// reflection here would be measuring a code path the product does not have.
/// </remarks>
[JsonSerializable(typeof(BenchResult))]
[JsonSerializable(typeof(BenchState))]
internal sealed partial class BenchJson : JsonSerializerContext;

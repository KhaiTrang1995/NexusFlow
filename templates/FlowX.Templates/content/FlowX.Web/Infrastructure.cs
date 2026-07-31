using System.Text.Json.Serialization;

namespace FlowXStarter;

/// <summary>
/// Source-generated serialisation for the two contracts on the wire, so the application
/// publishes with NativeAOT.
/// </summary>
/// <remarks>
/// The camelCase policy is not decoration. Without it the wire names are the C# ones, and
/// a client sending the conventional <c>"subject"</c> gets a <c>Subject</c> of
/// <c>null</c> rather than an error — a missing member deserialises to its default.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OpenTicket))]
[JsonSerializable(typeof(TicketOpened))]
internal sealed partial class AppJsonContext : JsonSerializerContext;

/// <summary>Tickets, in memory. Replace this class, and nothing else, with a real store.</summary>
internal sealed class InMemoryTicketStore : ITicketStore
{
    private readonly Dictionary<string, string> _tickets = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    public ValueTask SaveAsync(string ticketId, string subject, CancellationToken ct)
    {
        lock (_sync)
        {
            // Keyed on the id, so the same request replayed writes once. This is the
            // deduplication the capability's Idempotent = true promises.
            _tickets[ticketId] = subject;
        }

        return ValueTask.CompletedTask;
    }
}

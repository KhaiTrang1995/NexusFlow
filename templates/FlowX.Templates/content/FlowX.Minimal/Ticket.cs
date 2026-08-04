using System.Text.Json.Serialization;
using FlowX;

namespace FlowXMinimal;

/// <summary>What a caller sends.</summary>
public sealed record OpenTicket(string Subject);

/// <summary>What they get back.</summary>
public sealed record TicketOpened(string TicketId, string Subject);

/// <summary>
/// Source-generated serialisation for the two contracts on the wire.
/// </summary>
/// <remarks>
/// The camelCase policy is not decoration. Without it the wire names are the C# ones, and a
/// client sending the conventional <c>"subject"</c> gets a <c>Subject</c> of <c>null</c> rather
/// than an error — a missing member deserialises to its default.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OpenTicket))]
[JsonSerializable(typeof(TicketOpened))]
internal sealed partial class AppJsonContext : JsonSerializerContext;

/// <summary>
/// Opens a ticket. One unit of work, one authorisation stance, one contract version.
/// </summary>
/// <remarks>
/// <para>
/// <strong><c>Public</c> because this application registers no authentication</strong>, and a
/// stance that named a permission nobody could hold would refuse every call. The moment there is
/// a <c>ClaimsPrincipal</c> — one <c>AddAuthentication</c> line — change this to
/// <c>Authorization.Permission</c> and a name, and the rule holds over every transport at once
/// rather than on the route.
/// </para>
/// <para>
/// <strong>The id comes from the caller's key.</strong> A new <see cref="System.Guid"/> would
/// make a retried request a second ticket, which is what <c>Idempotent = true</c> promises it is
/// not — and the promise is what lets a retry policy be attached at all.
/// </para>
/// </remarks>
[Capability("ticket.open", Version = "1.0.0",
    Authorization = Authorization.Public,
    Idempotent = true)]
public sealed class OpenTicketCapability(TicketBook book) : ICapability<OpenTicket, TicketOpened>
{
    /// <inheritdoc />
    public ValueTask<Result<TicketOpened>> ExecuteAsync(
        OpenTicket input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        // An expected failure is a value, not an exception. Throwing here would be a defect
        // alert rather than a 400, and the compiler says so (FLOWX1016).
        if (string.IsNullOrWhiteSpace(input.Subject))
        {
            return ValueTask.FromResult(Result.Fail<TicketOpened>(
                new Error("ticket.subject_required", "A ticket needs a subject.", ErrorCategory.Validation)));
        }

        var opened = new TicketOpened(ctx.IdempotencyKey, input.Subject.Trim());

        book.Record(opened);

        return ValueTask.FromResult(Result.Ok(opened));
    }
}

/// <summary>Where the tickets go. In memory, so this application needs nothing installed.</summary>
public sealed class TicketBook
{
    private readonly Dictionary<string, TicketOpened> _tickets = [];

    /// <summary>Writes one, or leaves the one already there — the same key is the same ticket.</summary>
    /// <param name="ticket">What was opened.</param>
    public void Record(TicketOpened ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        lock (_tickets)
        {
            _tickets.TryAdd(ticket.TicketId, ticket);
        }
    }
}

/// <summary>
/// The flow: one step, and the route that reaches it.
/// </summary>
/// <remarks>
/// <c>Ephemeral</c>, so nothing is journalled and no database is needed. A flow that must survive
/// a process death — anything with compensation, a wait or an emitted event — declares
/// <c>Durable</c> and the host registers a journal.
/// </remarks>
[Flow("ticket.open", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral)]
[HttpTrigger("POST", "/api/v1/tickets", Idempotent = true)]
public sealed partial class OpenTicketFlow : Flow<OpenTicket, TicketOpened>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<OpenTicket, TicketOpened> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow.Step<OpenTicketCapability>().Return(ctx => ctx.Get<TicketOpened>());
    }
}

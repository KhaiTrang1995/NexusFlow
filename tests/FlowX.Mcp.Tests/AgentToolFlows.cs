using System.Text.Json.Serialization;
using FlowX;

namespace FlowX.Mcp.Tests;

/// <summary>What an agent hands a booking tool.</summary>
/// <param name="Room">The room to book.</param>
/// <param name="Nights">How many nights.</param>
/// <param name="CardNumber">The card to charge. Redacted everywhere it is written.</param>
public sealed record BookRoom(string Room, int Nights, [property: Sensitive] string CardNumber);

/// <summary>What the booking flow returns.</summary>
/// <param name="Reference">The booking reference.</param>
/// <param name="Total">What was charged.</param>
public sealed record Booking(string Reference, decimal Total);

/// <summary>What an agent hands the read-only tool.</summary>
/// <param name="Room">The room to price.</param>
public sealed record QuoteRequest(string Room);

/// <summary>What the quoting flow returns.</summary>
/// <param name="Room">The room quoted.</param>
/// <param name="NightlyRate">Its nightly rate.</param>
public sealed record Quote(string Room, decimal NightlyRate);

/// <summary>Every contract an agent can put on the wire in this assembly.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BookRoom))]
[JsonSerializable(typeof(Booking))]
[JsonSerializable(typeof(QuoteRequest))]
[JsonSerializable(typeof(Quote))]
public sealed partial class AgentJsonContext : JsonSerializerContext;

/// <summary>Charges a card and books a room — the side-effecting half of the surface.</summary>
/// <remarks>
/// <c>Authorization.Permission</c> and a declared side effect, which is what makes this the
/// capability the refusal tests are written against: an agent holding no grant meets the
/// step loop, and the descriptor an agent reads says both that a grant is needed and that a
/// consequence follows.
/// </remarks>
[Capability("booking.charge", Version = "1.0.0",
    Authorization = Authorization.Permission,
    Permission = "booking:write",
    SideEffects = ["payment-gateway"])]
public sealed class ChargeCard : ICapability<BookRoom, Booking>
{
    /// <summary>The nightly rate this sample charges.</summary>
    public const decimal NightlyRate = 120.00m;

    /// <summary>Charges the card and returns the booking.</summary>
    /// <param name="input">What to book.</param>
    /// <param name="ctx">Correlation, deadline and identity.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    public ValueTask<Result<Booking>> ExecuteAsync(
        BookRoom input, CapabilityContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        return ValueTask.FromResult(Result.Ok(
            new Booking(input.Room + "-1", input.Nights * NightlyRate)));
    }
}

/// <summary>Prices a room — the read-only half.</summary>
/// <remarks>
/// <c>Authorization.Public</c>, no side effect and idempotent, so its descriptor is the
/// negative of <see cref="ChargeCard"/>'s in every annotation. Two tools rather than one is
/// what stops every assertion about a descriptor passing for the wrong reason.
/// </remarks>
[Capability("booking.quote", Version = "1.0.0",
    Authorization = Authorization.Public,
    Idempotent = true)]
public sealed class QuoteRoom : ICapability<QuoteRequest, Quote>
{
    /// <summary>Quotes the room.</summary>
    /// <param name="input">What to price.</param>
    /// <param name="ctx">Correlation, deadline and identity.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    public ValueTask<Result<Quote>> ExecuteAsync(
        QuoteRequest input, CapabilityContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        return ValueTask.FromResult(Result.Ok(new Quote(input.Room, ChargeCard.NightlyRate)));
    }
}

/// <summary>
/// Books a room. Declared as both an HTTP endpoint and an agent tool, deliberately.
/// </summary>
/// <remarks>
/// <strong>Two triggers on one flow is what makes "the same stance the HTTP path enforces"
/// testable rather than asserted.</strong> The same plan, the same capability and the same
/// declared permission are reached down two transports in one process, so a refusal can be
/// compared against a refusal instead of against a remembered status code.
/// </remarks>
[Flow("booking.book", Version = "1.0.0", Owner = "bookings")]
[HttpTrigger("POST", "/api/v1/bookings")]
[AgentTrigger(Description = "Book a room for a number of nights and charge the card on file.")]
public sealed partial class BookRoomFlow : Flow<BookRoom, Booking>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<BookRoom, Booking> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ChargeCard>()
            .Return(ctx => ctx.Get<Booking>());
    }
}

/// <summary>Quotes a room. An agent tool and nothing else.</summary>
/// <remarks>
/// <c>Confirmation = ConfirmationMode.Never</c> is written rather than left to the default,
/// so the projection is exercised on a mode the attribute does not default to — and so a
/// descriptor that reported confirmation for every tool would be caught.
/// </remarks>
[Flow("booking.quote", Version = "1.0.0", Owner = "bookings")]
[AgentTrigger(
    Description = "Quote the nightly rate for a room.",
    Confirmation = ConfirmationMode.Never)]
public sealed partial class QuoteRoomFlow : Flow<QuoteRequest, Quote>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<QuoteRequest, Quote> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<QuoteRoom>()
            .Return(ctx => ctx.Get<Quote>());
    }
}

/// <summary>
/// A flow with no <c>[AgentTrigger]</c>, so no agent can reach it.
/// </summary>
/// <remarks>
/// The negative case, and it has to exist in the compilation rather than in a fixture: a
/// projection that published every flow would pass every assertion about the two above.
/// </remarks>
[Flow("booking.audit", Version = "1.0.0", Owner = "bookings")]
public sealed partial class AuditBookingFlow : Flow<QuoteRequest, Quote>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<QuoteRequest, Quote> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<QuoteRoom>()
            .Return(ctx => ctx.Get<Quote>());
    }
}

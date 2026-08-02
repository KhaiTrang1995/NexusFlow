using FlowX;

namespace EventDriven;

/// <summary>What issuing an invoice needs, whoever asked for it.</summary>
/// <param name="Reference">
/// The billing reference. It is the identity of the <em>work</em> rather than of the request, and
/// that is what makes this sample's claim checkable: four transports handed the same reference
/// produce one invoice id, so their outputs can be compared for equality rather than for shape.
/// </param>
/// <param name="Account">Who is billed.</param>
/// <param name="Net">The amount before tax, in major units.</param>
/// <param name="Currency">ISO 4217 code.</param>
public sealed record IssueInvoice(string Reference, string Account, decimal Net, string Currency);

/// <summary>A request that passed validation.</summary>
public sealed record ValidatedInvoice(string Reference, string Account, decimal Net, string Currency);

/// <summary>A validated request with tax applied.</summary>
public sealed record TaxedInvoice(
    string Reference, string Account, decimal Net, decimal Tax, string Currency);

/// <summary>The issued invoice — this chain's output, over every transport.</summary>
public sealed record Invoice(
    string InvoiceId, string Account, decimal Net, decimal Tax, decimal Gross, string Currency);

/// <summary>Published when an invoice is issued, whichever transport started the flow.</summary>
public sealed record InvoiceIssued(string InvoiceId, string Account, decimal Gross, string Currency);

/// <summary>
/// Published when an invoice is asked for, and consumed by two of the four transports.
/// </summary>
/// <remarks>
/// Its identity is <c>invoice.requested</c> — the type name converted by the compiler's own
/// convention — which is simultaneously the topic <see cref="IssueInvoiceOverBusFlow"/> subscribes
/// to and the source <see cref="IssueInvoiceOverChangeFlow"/> observes. Neither flow names the
/// emitter and the emitter names neither of them.
/// </remarks>
public sealed record InvoiceRequested(
    string Reference, string Account, decimal Net, string Currency);

/// <summary>What a caller gets back for asking. Not the event — see the remarks.</summary>
/// <remarks>
/// A contract belongs to exactly one generated <c>JsonSerializerContext</c>, so the request
/// flow's HTTP response cannot be the same type as the event it stages: one is on the wire in
/// camelCase and the other is an outbox body. Two types is the cheaper of the two costs.
/// </remarks>
public sealed record InvoiceRequestAccepted(string Reference, string Account);

/// <summary>Why issuing an invoice failed.</summary>
public static class InvoiceErrors
{
    /// <summary>The net amount is not positive.</summary>
    public static Error NotPositive(decimal net) =>
        new Error("invoice.not_positive", $"Net must be positive; got {net}.", ErrorCategory.Validation)
            .With("net", net);

    /// <summary>The currency is not one this deployment bills in.</summary>
    public static Error UnsupportedCurrency(string currency) =>
        new Error(
            "invoice.unsupported_currency",
            $"'{currency}' is not a billing currency.",
            ErrorCategory.Validation)
            .With("currency", currency);

    /// <summary>A delivered or observed event carried no body to issue from.</summary>
    /// <remarks>
    /// A <c>Result</c> failure and not an exception, which is what decides what the broker is
    /// told: a flow that ran and failed is acknowledged, because it happened. Throwing would
    /// leave the message pending and redeliver a body that will never be any different.
    /// </remarks>
    public static Error EventHasNoBody(Guid eventId) =>
        new Error("invoice.event_has_no_body", "The event carried no body.", ErrorCategory.Validation)
            .With("eventId", eventId.ToString("d"));

    /// <summary>No account is billed at this occurrence.</summary>
    public static Error NothingDue(DateTimeOffset occurrence) =>
        new Error(
            "invoice.nothing_due",
            "No billing run is scheduled for this occurrence.",
            ErrorCategory.NotFound)
            .With("occurrenceAt", occurrence.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
}

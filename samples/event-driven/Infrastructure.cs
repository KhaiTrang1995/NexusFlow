using System.Text.Json.Serialization;
using FlowX;

namespace EventDriven;

/// <summary>Serialisation for the two contracts that cross the HTTP wire.</summary>
/// <remarks>
/// The camelCase policy is not decoration: without it the wire names are the C# ones, and a
/// client sending the conventional <c>"reference"</c> gets a <c>Reference</c> of null — a missing
/// member deserialises to <c>default</c>. <c>invoice.validate</c> then rejects it, which is the
/// point of validating at the first step.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IssueInvoice))]
[JsonSerializable(typeof(Invoice))]
[JsonSerializable(typeof(InvoiceRequestAccepted))]
internal sealed partial class EventDrivenJsonContext : JsonSerializerContext;

/// <summary>Serialisation for everything the journal and the outbox hold.</summary>
/// <remarks>
/// Every flow here is <c>Durable</c>, so each step's result and the state bag after it are
/// journalled, and every emitted body is written through a source-generated context rather than
/// by reflection. <c>FLOWX1006</c> and <c>FLOWX1024</c> are what name a missing member, so this
/// list is compiler-maintained rather than hand-maintained. Public because
/// <c>tests/EventDriven.Tests</c> hands it to the hosts it builds against a real PostgreSQL.
/// </remarks>
[JsonSerializable(typeof(ValidatedInvoice))]
[JsonSerializable(typeof(TaxedInvoice))]
[JsonSerializable(typeof(InvoiceIssued))]
[JsonSerializable(typeof(InvoiceRequested))]
[JsonSerializable(typeof(BusMessage))]
[JsonSerializable(typeof(ScheduledFire))]
public sealed partial class EventDrivenJournalJsonContext : JsonSerializerContext;

/// <summary>Where issued invoices are written.</summary>
public interface IInvoiceLedger
{
    /// <summary>Writes an invoice, keyed on its id, replacing any earlier write of the same id.</summary>
    /// <param name="invoice">The invoice.</param>
    /// <param name="ct">Cancels the write.</param>
    ValueTask WriteAsync(Invoice invoice, CancellationToken ct);

    /// <summary>Removes an invoice, if it is there.</summary>
    /// <param name="invoiceId">Which one.</param>
    /// <param name="ct">Cancels the write.</param>
    ValueTask VoidAsync(string invoiceId, CancellationToken ct);

    /// <summary>Reads an invoice back, or null.</summary>
    /// <param name="invoiceId">Which one.</param>
    /// <param name="ct">Cancels the read.</param>
    ValueTask<Invoice?> ReadAsync(string invoiceId, CancellationToken ct);
}

/// <summary>What this deployment bills at a given occurrence.</summary>
public interface IBillingCalendar
{
    /// <summary>The request that falls due at an occurrence, or null when none does.</summary>
    /// <param name="occurrence">The instant the cron expression named.</param>
    /// <param name="ct">Cancels the read.</param>
    ValueTask<IssueInvoice?> DueAtAsync(DateTimeOffset occurrence, CancellationToken ct);
}

/// <summary>The ledger, in memory. The capabilities depend on the port, not on this.</summary>
public sealed class InMemoryInvoiceLedger : IInvoiceLedger
{
    private readonly Dictionary<string, Invoice> _invoices = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    /// <inheritdoc />
    public ValueTask WriteAsync(Invoice invoice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        lock (_sync)
        {
            _invoices[invoice.InvoiceId] = invoice;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask VoidAsync(string invoiceId, CancellationToken ct)
    {
        lock (_sync)
        {
            _invoices.Remove(invoiceId);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<Invoice?> ReadAsync(string invoiceId, CancellationToken ct)
    {
        lock (_sync)
        {
            return ValueTask.FromResult(
                _invoices.TryGetValue(invoiceId, out var invoice) ? invoice : null);
        }
    }
}

/// <summary>The billing calendar, in memory.</summary>
/// <remarks>
/// Entries are keyed on the occurrence the cron expression names, to the minute, because that is
/// the value <see cref="ScheduledFire.OccurrenceAt"/> carries and the value the instance id is
/// derived from — a lookup on "now" would answer differently on a late sweep.
/// </remarks>
public sealed class InMemoryBillingCalendar : IBillingCalendar
{
    private readonly Dictionary<DateTimeOffset, IssueInvoice> _due = [];
    private readonly Lock _sync = new();

    /// <summary>Puts a request in the calendar at an occurrence.</summary>
    /// <param name="occurrence">The instant a cron expression will name.</param>
    /// <param name="request">What to bill then.</param>
    public void Add(DateTimeOffset occurrence, IssueInvoice request)
    {
        lock (_sync)
        {
            _due[Normalise(occurrence)] = request;
        }
    }

    /// <inheritdoc />
    public ValueTask<IssueInvoice?> DueAtAsync(DateTimeOffset occurrence, CancellationToken ct)
    {
        lock (_sync)
        {
            return ValueTask.FromResult(
                _due.TryGetValue(Normalise(occurrence), out var request) ? request : null);
        }
    }

    private static DateTimeOffset Normalise(DateTimeOffset occurrence)
    {
        var utc = occurrence.ToUniversalTime();

        return new DateTimeOffset(
            utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
    }
}

using System.Text.Json;
using FlowX;

namespace EventDriven;

/// <summary>Refuses a request that could not become an invoice.</summary>
/// <remarks>
/// <para>
/// <strong>The stance is <see cref="Authorization.Internal"/> on all four business capabilities,
/// and that is the one authorisation decision transport portability actually constrains.</strong>
/// Two of the four transports here start a flow with no principal at all — a broker delivery and
/// a cron occurrence carry no caller — so <c>Authenticated</c> or <c>Permission</c> on a shared
/// step would refuse every message and every firing while passing over HTTP. A chain that has to
/// run under all four therefore states the truth: at this step there is no external caller to
/// refuse. What guards the chain is the trigger each flow carries.
/// </para>
/// </remarks>
[Capability("invoice.validate", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true)]
public sealed class ValidateInvoice : ICapability<IssueInvoice, ValidatedInvoice>
{
    private static readonly string[] Billable = ["GBP", "EUR", "USD"];

    /// <inheritdoc />
    public ValueTask<Result<ValidatedInvoice>> ExecuteAsync(
        IssueInvoice input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Net <= 0m)
        {
            return ValueTask.FromResult(
                Result.Fail<ValidatedInvoice>(InvoiceErrors.NotPositive(input.Net)));
        }

        return ValueTask.FromResult(
            Array.IndexOf(Billable, input.Currency) < 0
                ? Result.Fail<ValidatedInvoice>(InvoiceErrors.UnsupportedCurrency(input.Currency))
                : Result.Ok(new ValidatedInvoice(
                    input.Reference, input.Account, input.Net, input.Currency)));
    }
}

/// <summary>Applies the tax rate. A pure function of its input, which is why replay is safe.</summary>
[Capability("invoice.tax", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true)]
public sealed class CalculateTax : ICapability<ValidatedInvoice, TaxedInvoice>
{
    private const decimal Rate = 0.20m;

    /// <inheritdoc />
    public ValueTask<Result<TaxedInvoice>> ExecuteAsync(
        ValidatedInvoice input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        return ValueTask.FromResult(Result.Ok(new TaxedInvoice(
            input.Reference,
            input.Account,
            input.Net,
            decimal.Round(input.Net * Rate, 2, MidpointRounding.ToEven),
            input.Currency)));
    }
}

/// <summary>Writes the invoice to the ledger.</summary>
/// <remarks>
/// <strong>Keyed on the billing reference and not on the instance</strong>, so the same reference
/// issued over four transports is one row rather than four. That is what an idempotent write
/// means here, and it is what lets the portability assertion compare outputs for equality — an
/// invoice id derived from the instance id would differ per transport by construction and would
/// prove nothing.
/// </remarks>
[Capability("invoice.persist", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["invoice-ledger"])]
public sealed class PersistInvoice : ICapability<TaxedInvoice, Invoice>
{
    private readonly IInvoiceLedger _ledger;

    /// <summary>Creates the capability.</summary>
    /// <param name="ledger">Where invoices are written.</param>
    public PersistInvoice(IInvoiceLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        _ledger = ledger;
    }

    /// <inheritdoc />
    public async ValueTask<Result<Invoice>> ExecuteAsync(
        TaxedInvoice input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var invoice = new Invoice(
            "INV-" + input.Reference,
            input.Account,
            input.Net,
            input.Tax,
            input.Net + input.Tax,
            input.Currency);

        await _ledger.WriteAsync(invoice, ct).ConfigureAwait(false);

        return invoice;
    }
}

/// <summary>Voids the invoice. The business inverse of <see cref="PersistInvoice"/>.</summary>
[Capability("invoice.void", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["invoice-ledger"])]
public sealed class VoidInvoice : ICapability<TaxedInvoice, Invoice>
{
    private readonly IInvoiceLedger _ledger;

    /// <summary>Creates the capability.</summary>
    /// <param name="ledger">Where invoices are written.</param>
    public VoidInvoice(IInvoiceLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        _ledger = ledger;
    }

    /// <inheritdoc />
    public async ValueTask<Result<Invoice>> ExecuteAsync(
        TaxedInvoice input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        await _ledger.VoidAsync("INV-" + input.Reference, ct).ConfigureAwait(false);

        return new Invoice(
            "INV-" + input.Reference, input.Account, input.Net, input.Tax, 0m, input.Currency);
    }
}

/// <summary>
/// Turns a delivered or observed <c>invoice.requested</c> event into the chain's own input.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The whole of what a bus transport costs this chain, and one capability serves two of
/// them.</strong> <see cref="IssueInvoiceOverBusFlow"/> is started by a broker and
/// <see cref="IssueInvoiceOverChangeFlow"/> by the outbox itself; both hand the flow a
/// <see cref="BusMessage"/> whose body the host deliberately did not deserialise, because doing
/// so needs a <c>JsonTypeInfo</c> only generated code can name. A capability is where a
/// serialiser context is in scope and where a malformed body is a <c>Result</c> rather than an
/// exception out of a background service.
/// </para>
/// </remarks>
[Capability("invoice.read_request", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true)]
public sealed class ReadInvoiceRequest : ICapability<BusMessage, IssueInvoice>
{
    /// <inheritdoc />
    public ValueTask<Result<IssueInvoice>> ExecuteAsync(
        BusMessage input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Payload is not { Length: > 0 } body)
        {
            return ValueTask.FromResult(
                Result.Fail<IssueInvoice>(InvoiceErrors.EventHasNoBody(input.EventId)));
        }

        var requested = JsonSerializer.Deserialize(
            body, EventDrivenJournalJsonContext.Default.InvoiceRequested);

        return ValueTask.FromResult(requested is null
            ? Result.Fail<IssueInvoice>(InvoiceErrors.EventHasNoBody(input.EventId))
            : Result.Ok(new IssueInvoice(
                requested.Reference, requested.Account, requested.Net, requested.Currency)));
    }
}

/// <summary>Turns a cron occurrence into the chain's own input.</summary>
/// <remarks>
/// <strong>The occurrence is read from the input and never from a clock.</strong> A run at 03:41
/// is still billing the run that fell due at 03:00, and <c>FLOWX1007</c> forbids a durable flow
/// reading an ambient clock at all — so the instant arrives as <see cref="ScheduledFire"/> and is
/// journalled with the instance.
/// </remarks>
[Capability("invoice.due", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true)]
public sealed class DueInvoice : ICapability<ScheduledFire, IssueInvoice>
{
    private readonly IBillingCalendar _calendar;

    /// <summary>Creates the capability.</summary>
    /// <param name="calendar">What is billed at an occurrence.</param>
    public DueInvoice(IBillingCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        _calendar = calendar;
    }

    /// <inheritdoc />
    public async ValueTask<Result<IssueInvoice>> ExecuteAsync(
        ScheduledFire input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var due = await _calendar.DueAtAsync(input.OccurrenceAt, ct).ConfigureAwait(false);

        return due is null
            ? Result.Fail<IssueInvoice>(InvoiceErrors.NothingDue(input.OccurrenceAt))
            : Result.Ok(due);
    }
}

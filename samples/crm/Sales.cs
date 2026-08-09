using System.Security.Claims;
using Npgsql;
using NpgsqlTypes;

namespace Crm;

// ------------------------------------------------------------------------- what a caller asks

/// <summary>Asks for a quote against an opportunity.</summary>
/// <param name="OpportunityId">What is being quoted for.</param>
/// <param name="Lines">What is being sold.</param>
/// <param name="Discount">What is being taken off, in the lines' currency.</param>
/// <param name="ValidForDays">How long the price holds.</param>
public sealed record IssueQuote(
    Guid OpportunityId,
    IReadOnlyList<QuoteRequestLine> Lines,
    decimal Discount,
    int ValidForDays);

/// <summary>What the quote came to, and whether anybody has to approve it.</summary>
/// <param name="QuoteId">The quote.</param>
/// <param name="Total">What is being asked for.</param>
/// <param name="Status">Where it is: <c>Issued</c>, or <c>Draft</c> pending an approval.</param>
/// <param name="NeedsApproval">Whether the discount is over the threshold.</param>
public sealed record QuoteIssued(Guid QuoteId, Money Total, QuoteStatus Status, bool NeedsApproval);

/// <summary>Re-prices a quote by replacing it with a revised one.</summary>
/// <param name="QuoteId">The quote being replaced. Its opportunity is the revision's.</param>
/// <param name="Lines">What is being sold now. The whole set, not a change to it.</param>
/// <param name="Discount">What is being taken off, in the lines' currency.</param>
/// <param name="ValidForDays">How long the new price holds, counted from now.</param>
/// <remarks>
/// <strong>The lines are the revision's whole set and not a patch.</strong> A partial line edit
/// needs a rule about what an omitted line means, and every such rule is wrong for somebody — the
/// seller who deleted a line and the seller who did not mention it send the same request.
/// </remarks>
public sealed record RepriceQuote(
    Guid QuoteId,
    IReadOnlyList<QuoteRequestLine> Lines,
    decimal Discount,
    int ValidForDays);

/// <summary>The revision, and the quote it retired.</summary>
/// <param name="QuoteId">The new quote, which carries the revised lines.</param>
/// <param name="Supersedes">The one it replaced, now <see cref="QuoteStatus.Superseded"/>.</param>
/// <param name="Total">What the revision comes to.</param>
/// <param name="Status">Where the revision is: <c>Issued</c>, or <c>Draft</c> pending approval.</param>
/// <param name="NeedsApproval">Whether the revision's discount is over the threshold.</param>
/// <remarks>
/// <strong>Both ids, because a caller holding only the new one cannot tell a re-price from an
/// ordinary issue.</strong> The screen that asked for it has the old quote open and has to say
/// what happened to it.
/// </remarks>
public sealed record QuoteSuperseded(
    Guid QuoteId,
    Guid Supersedes,
    Money Total,
    QuoteStatus Status,
    bool NeedsApproval);

/// <summary>Asks for a discount to be approved.</summary>
/// <param name="QuoteId">The quote whose discount is over the threshold.</param>
public sealed record ApproveDiscount(Guid QuoteId);

/// <summary>The approval, and who made it.</summary>
/// <param name="QuoteId">The quote.</param>
/// <param name="ApprovedBy">The approver, derived from the caller's claims.</param>
/// <param name="At">When.</param>
public sealed record DiscountApproved(Guid QuoteId, Guid ApprovedBy, DateTimeOffset At);

/// <summary>What the approving capability is given, once the flow has read the caller.</summary>
/// <param name="QuoteId">The quote.</param>
/// <param name="ApprovedBy">The approver, from the caller's claims and never from the body.</param>
public sealed record ApproveQuoteDiscount(Guid QuoteId, Guid ApprovedBy);

/// <summary>Turns an issued quote into an order.</summary>
/// <param name="QuoteId">The quote being accepted.</param>
public sealed record PlaceOrder(Guid QuoteId);

/// <summary>The order.</summary>
/// <param name="OrderId">The order, derived from the quote.</param>
/// <param name="QuoteId">What was accepted.</param>
/// <param name="Total">What was ordered.</param>
public sealed record OrderPlaced(Guid OrderId, Guid QuoteId, Money Total);

/// <summary>Applies a trigger to an opportunity, for the configured process to answer.</summary>
/// <param name="OpportunityId">The opportunity.</param>
/// <param name="Trigger">What happened, in the administrator's vocabulary.</param>
public sealed record AdvanceOpportunity(Guid OpportunityId, string Trigger);

/// <summary>That the trigger was accepted and announced.</summary>
/// <param name="ApplicationId">
/// The handle for this application, and the only thing here a caller can act on.
/// <strong>Nothing in this answer says where the deal went, because at the moment it is written
/// nobody knows.</strong> The transition is decided afterwards, off the change feed; this id is
/// what <see cref="ReadTriggerOutcome"/> takes, and reading it is how a caller learns which of
/// <see cref="TriggerOutcome"/>'s three things happened.
/// </param>
/// <param name="OpportunityId">The opportunity.</param>
/// <param name="Trigger">What was applied.</param>
public sealed record OpportunityAdvanced(Guid ApplicationId, Guid OpportunityId, string Trigger);

/// <summary>A stored quote, as much of it as the sales rules need.</summary>
/// <param name="Id">The quote.</param>
/// <param name="Opportunity">What it was quoted against.</param>
/// <param name="Account">Whose it is, followed through the opportunity.</param>
/// <param name="Status">Where it has got to.</param>
/// <param name="Subtotal">The sum of its lines.</param>
/// <param name="Discount">What was taken off.</param>
/// <param name="Total">What is being asked for.</param>
/// <param name="Currency">What all three are in.</param>
/// <param name="ValidUntil">After which it has expired.</param>
/// <param name="ApprovedBy">The approver, or null.</param>
public sealed record QuoteRow(
    Guid Id,
    Guid Opportunity,
    Guid Account,
    QuoteStatus Status,
    decimal Subtotal,
    decimal Discount,
    decimal Total,
    string Currency,
    DateTimeOffset ValidUntil,
    Guid? ApprovedBy);

// -------------------------------------------------------------------------------- who approved

/// <summary>
/// Turns the caller a token attested into the id an approval is stored under.
/// </summary>
/// <remarks>
/// <para>
/// <strong>From the claims, never from the body.</strong> An <c>approvedBy</c> field on the
/// request would let a representative name a manager and be believed. The subject claim is what
/// the token validator attested and it is the only thing here that says who is calling —
/// ADR-0046 makes the same argument about the tenant.
/// </para>
/// <para>
/// <strong>Derived rather than parsed, because a subject is not a uuid.</strong> An OIDC
/// <c>sub</c> is an opaque string and <c>quote.approved_by</c> is a <c>uuid</c>. Hashing the
/// subject into a version 8 uuid gives the same id for the same caller on every call and on a
/// replay, without a users table this sample does not have.
/// </para>
/// </remarks>
public static class Approvers
{
    /// <summary>The approver id for a caller.</summary>
    /// <param name="principal">Whoever the trigger authenticated, or null.</param>
    /// <returns>The derived id, or <see cref="Guid.Empty"/> when nobody is calling.</returns>
    public static Guid Of(ClaimsPrincipal? principal)
    {
        var subject =
            principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
            principal?.FindFirst("sub")?.Value;

        if (string.IsNullOrEmpty(subject))
        {
            // Unreachable from the HTTP trigger, which refuses an anonymous caller before the
            // flow starts. Empty rather than a throw: a capability is not where an
            // authentication failure is discovered.
            return Guid.Empty;
        }

        Span<byte> hash = stackalloc byte[32];

        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(subject), hash);

        var id = hash[..16];

        id[6] = (byte)((id[6] & 0x0F) | 0x80);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);

        return new Guid(id);
    }
}

/// <summary>Derives the ids the sales rows are written under.</summary>
/// <remarks>
/// <strong>From the quote, because §6 draws at most one order per quote.</strong> The schema
/// says so with <c>UNIQUE (quote_id)</c>; deriving the id means a redelivered accept writes the
/// same row rather than colliding with the constraint and failing a caller who did nothing
/// wrong. The lines are derived for the same reason: a re-run of the insert must not leave a
/// quote with its lines counted twice.
/// </remarks>
public static class SalesIds
{
    /// <summary>The order a quote is accepted into.</summary>
    /// <param name="quoteId">The quote.</param>
    /// <returns>The derived id.</returns>
    public static Guid Order(Guid quoteId) => Derive(quoteId, "order", 0);

    /// <summary>The row one of a quote's lines is written as.</summary>
    /// <param name="quoteId">The quote.</param>
    /// <param name="index">The line's position in the request.</param>
    /// <returns>The derived id.</returns>
    public static Guid Line(Guid quoteId, int index) => Derive(quoteId, "line", index);

    private static Guid Derive(Guid quoteId, string role, int index)
    {
        Span<byte> seed = stackalloc byte[16 + 8 + 4];

        quoteId.TryWriteBytes(seed);
        System.Text.Encoding.UTF8.GetBytes(role, seed[16..]);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(seed[24..], index);

        Span<byte> hash = stackalloc byte[32];

        System.Security.Cryptography.SHA256.HashData(seed, hash);

        var id = hash[..16];

        id[6] = (byte)((id[6] & 0x0F) | 0x80);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);

        return new Guid(id);
    }
}

// -------------------------------------------------------------------------------------- the store

/// <summary>
/// Quotes, their lines and the orders they are accepted into.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A quote and its lines commit together.</strong> A quote whose lines are missing has a
/// subtotal nothing adds up to, and a caller reading it would have no way to tell. The insert is
/// one transaction for that reason and for no other.
/// </para>
/// <para>
/// <strong>The arithmetic is not here.</strong> <see cref="Pricing"/> decides what a quote comes
/// to and <see cref="DiscountPolicy"/> decides who may sign it; this writes the answer down.
/// </para>
/// </remarks>
public sealed class SalesStore
{
    private const string SelectOpportunityAccount = """
        SELECT account_id FROM opportunity WHERE opportunity_id = @opportunity
        """;

    private const string InsertQuote = """
        INSERT INTO quote (
            quote_id, tenant_id, opportunity_id, status,
            subtotal, discount, total, currency, valid_until, approved_by, supersedes)
        VALUES (@id, @tenant, @opportunity, @status, @subtotal, @discount, @total, @currency,
            @valid, NULL, @supersedes)
        """;

    private const string InsertQuoteLine = """
        INSERT INTO quote_line (quote_line_id, quote_id, sku, quantity, unit_price, currency)
        VALUES (@id, @quote, @sku, @quantity, @price, @currency)
        """;

    private const string SelectQuote = """
        SELECT q.quote_id, q.opportunity_id, o.account_id, q.status,
               q.subtotal, q.discount, q.total, q.currency, q.valid_until, q.approved_by
        FROM quote q
        JOIN opportunity o ON o.opportunity_id = q.opportunity_id
        WHERE q.quote_id = @quote
        """;

    private const string ApproveQuote = """
        UPDATE quote SET approved_by = @approver, status = 'Issued'
        WHERE quote_id = @quote AND status = 'Draft'
        """;

    private const string AcceptQuote = """
        UPDATE quote SET status = 'Accepted' WHERE quote_id = @quote AND status = 'Issued'
        """;

    // The states a quote can be re-priced out of, restated as a WHERE rather than trusted from
    // the read above it. Two sellers revising the same quote at once is a race, and the loser has
    // to be told rather than being allowed to retire a quote somebody has already replaced or
    // ordered against.
    private const string SupersedeQuote = """
        UPDATE quote SET status = 'Superseded'
        WHERE quote_id = @quote AND status IN ('Draft', 'Issued')
        """;

    private const string InsertOrder = """
        INSERT INTO sales_order (order_id, tenant_id, quote_id, account_id, status, total, currency, placed_at)
        VALUES (@id, @tenant, @quote, @account, 'Placed', @total, @currency, @placed)
        ON CONFLICT (quote_id) DO NOTHING
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public SalesStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>The account an opportunity belongs to.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="opportunityId">The opportunity.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The account, or null when this tenant cannot see the opportunity.</returns>
    public async ValueTask<Guid?> ReadAccountAsync(
        string? tenantId,
        Guid opportunityId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = SelectOpportunityAccount;
        command.Parameters.Add(new NpgsqlParameter("opportunity", NpgsqlDbType.Uuid) { Value = opportunityId });

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid account
            ? account
            : null;
    }

    /// <summary>Writes a quote and its lines, together.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="quoteId">The quote's id.</param>
    /// <param name="opportunityId">What it is quoted against.</param>
    /// <param name="priced">What <see cref="Pricing"/> made of the lines.</param>
    /// <param name="lines">The lines themselves.</param>
    /// <param name="validUntil">When the price stops holding.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The status the quote was written with.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="priced"/> or <paramref name="lines"/> is null.</exception>
    public async ValueTask<QuoteStatus> InsertQuoteAsync(
        string? tenantId,
        Guid quoteId,
        Guid opportunityId,
        PricedQuote priced,
        IReadOnlyList<QuoteRequestLine> lines,
        DateTimeOffset validUntil,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(priced);
        ArgumentNullException.ThrowIfNull(lines);

        // Draft until a manager signs it; Issued straight away when nobody has to.
        var status = priced.NeedsApproval ? QuoteStatus.Draft : QuoteStatus.Issued;

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var closingTransaction = transaction.ConfigureAwait(false);

        await WriteQuoteAsync(
            connection, tenantId, quoteId, opportunityId, status, priced, lines, validUntil,
            supersedes: null, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return status;
    }

    /// <summary>
    /// Writes a revised quote and retires the one it replaces, together.
    /// </summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="quoteId">The revision's id.</param>
    /// <param name="replaced">The quote being replaced, as it was read.</param>
    /// <param name="priced">What <see cref="Pricing"/> made of the revised lines.</param>
    /// <param name="lines">The revised lines themselves.</param>
    /// <param name="validUntil">When the new price stops holding.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The status the revision was written with, or null when the quote being replaced had
    /// already moved — in which case nothing was written at all.
    /// </returns>
    /// <remarks>
    /// <strong>One transaction, because either state on its own is a lie.</strong> A revision
    /// with the old quote still <c>Issued</c> is two live prices against one opportunity, and a
    /// retired quote with no revision behind it is a customer holding an offer this tenant has
    /// no record of having replaced.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="replaced"/>, <paramref name="priced"/> or <paramref name="lines"/> is null.
    /// </exception>
    public async ValueTask<QuoteStatus?> SupersedeAsync(
        string? tenantId,
        Guid quoteId,
        QuoteRow replaced,
        PricedQuote priced,
        IReadOnlyList<QuoteRequestLine> lines,
        DateTimeOffset validUntil,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replaced);
        ArgumentNullException.ThrowIfNull(priced);
        ArgumentNullException.ThrowIfNull(lines);

        // The revision is priced on its own merits: a discount the old quote's approval covered
        // is a discount nobody has approved on the new one.
        var status = priced.NeedsApproval ? QuoteStatus.Draft : QuoteStatus.Issued;

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var closingTransaction = transaction.ConfigureAwait(false);

        var retire = connection.CreateCommand();
        await using var closingRetire = retire.ConfigureAwait(false);

        retire.CommandText = SupersedeQuote;
        retire.Parameters.Add(new NpgsqlParameter("quote", NpgsqlDbType.Uuid) { Value = replaced.Id });

        if (await retire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            // Somebody ordered against it, or replaced it, between the read and here. Rolled
            // back rather than written as an orphan revision nothing points at.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return null;
        }

        await WriteQuoteAsync(
            connection, tenantId, quoteId, replaced.Opportunity, status, priced, lines, validUntil,
            replaced.Id, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return status;
    }

    /// <summary>Reads a quote.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="quoteId">The quote.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The quote, or null when this tenant cannot see it.</returns>
    public async ValueTask<QuoteRow?> ReadQuoteAsync(
        string? tenantId,
        Guid quoteId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = SelectQuote;
        command.Parameters.Add(new NpgsqlParameter("quote", NpgsqlDbType.Uuid) { Value = quoteId });

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var validUntil = await reader
            .GetFieldValueAsync<DateTimeOffset>(8, cancellationToken)
            .ConfigureAwait(false);

        var approved = await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false)
            ? (Guid?)null
            : reader.GetGuid(9);

        return new QuoteRow(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            Enum.Parse<QuoteStatus>(reader.GetString(3)),
            reader.GetDecimal(4),
            reader.GetDecimal(5),
            reader.GetDecimal(6),
            reader.GetString(7),
            validUntil,
            approved);
    }

    /// <summary>Stamps an approver on a draft quote and issues it.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="quoteId">The quote.</param>
    /// <param name="approver">Who approved, derived from their claims.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when a row moved; false when it was not a draft any more.</returns>
    /// <remarks>
    /// <strong><c>AND status = 'Draft'</c> is what makes a second approval a no-op.</strong> Two
    /// managers pressing approve at once must not produce two approvers, and the row the first
    /// one moved is no longer a draft for the second.
    /// </remarks>
    public async ValueTask<bool> ApproveAsync(
        string? tenantId,
        Guid quoteId,
        Guid approver,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = ApproveQuote;
        command.Parameters.Add(new NpgsqlParameter("quote", NpgsqlDbType.Uuid) { Value = quoteId });
        command.Parameters.Add(new NpgsqlParameter("approver", NpgsqlDbType.Uuid) { Value = approver });

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>Accepts a quote and writes the order it becomes, together.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="quote">The quote being accepted.</param>
    /// <param name="orderId">The order's id, derived from the quote.</param>
    /// <param name="placedAt">The engine's clock.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// <strong>One transaction, and the <c>ON CONFLICT</c> is the redelivery.</strong> An
    /// accepted quote with no order, or an order against a quote still open for a second one,
    /// are both states a caller could read and be misled by.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="quote"/> is null.</exception>
    public async ValueTask PlaceOrderAsync(
        string? tenantId,
        QuoteRow quote,
        Guid orderId,
        DateTimeOffset placedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quote);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var closingTransaction = transaction.ConfigureAwait(false);

        var accept = connection.CreateCommand();
        await using var closingAccept = accept.ConfigureAwait(false);

        accept.CommandText = AcceptQuote;
        accept.Parameters.Add(new NpgsqlParameter("quote", NpgsqlDbType.Uuid) { Value = quote.Id });

        await accept.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var order = connection.CreateCommand();
        await using var closingOrder = order.ConfigureAwait(false);

        order.CommandText = InsertOrder;
        order.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = orderId });
        order.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Text) { Value = tenantId ?? string.Empty });
        order.Parameters.Add(new NpgsqlParameter("quote", NpgsqlDbType.Uuid) { Value = quote.Id });
        order.Parameters.Add(new NpgsqlParameter("account", NpgsqlDbType.Uuid) { Value = quote.Account });
        order.Parameters.Add(new NpgsqlParameter("total", NpgsqlDbType.Numeric) { Value = quote.Total });
        order.Parameters.Add(new NpgsqlParameter("currency", NpgsqlDbType.Text) { Value = quote.Currency });
        order.Parameters.Add(new NpgsqlParameter("placed", NpgsqlDbType.TimestampTz) { Value = placedAt });

        await order.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes one quote and its lines on a connection already in a transaction.</summary>
    /// <remarks>
    /// Shared by the first offer and by every revision of it, because they differ in one column.
    /// A second copy of this insert is a second place the lines could be forgotten, and a quote
    /// whose lines are missing has a subtotal nothing adds up to.
    /// </remarks>
    private static async ValueTask WriteQuoteAsync(
        NpgsqlConnection connection,
        string? tenantId,
        Guid quoteId,
        Guid opportunityId,
        QuoteStatus status,
        PricedQuote priced,
        IReadOnlyList<QuoteRequestLine> lines,
        DateTimeOffset validUntil,
        Guid? supersedes,
        CancellationToken cancellationToken)
    {
        var quote = connection.CreateCommand();
        await using var closingQuote = quote.ConfigureAwait(false);

        quote.CommandText = InsertQuote;
        quote.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = quoteId });
        quote.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Text) { Value = tenantId ?? string.Empty });
        quote.Parameters.Add(new NpgsqlParameter("opportunity", NpgsqlDbType.Uuid) { Value = opportunityId });
        quote.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = status.ToString() });
        quote.Parameters.Add(new NpgsqlParameter("subtotal", NpgsqlDbType.Numeric) { Value = priced.Subtotal.Amount });
        quote.Parameters.Add(new NpgsqlParameter("discount", NpgsqlDbType.Numeric) { Value = priced.Discount.Amount });
        quote.Parameters.Add(new NpgsqlParameter("total", NpgsqlDbType.Numeric) { Value = priced.Total.Amount });
        quote.Parameters.Add(new NpgsqlParameter("currency", NpgsqlDbType.Text) { Value = priced.Total.Currency });
        quote.Parameters.Add(new NpgsqlParameter("valid", NpgsqlDbType.TimestampTz) { Value = validUntil });
        quote.Parameters.Add(new NpgsqlParameter("supersedes", NpgsqlDbType.Uuid)
        {
            Value = (object?)supersedes ?? DBNull.Value,
        });

        await quote.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];

            var command = connection.CreateCommand();
            await using var closingLine = command.ConfigureAwait(false);

            command.CommandText = InsertQuoteLine;
            command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid)
            {
                Value = SalesIds.Line(quoteId, index),
            });
            command.Parameters.Add(new NpgsqlParameter("quote", NpgsqlDbType.Uuid) { Value = quoteId });
            command.Parameters.Add(new NpgsqlParameter("sku", NpgsqlDbType.Text) { Value = line.Sku });
            command.Parameters.Add(new NpgsqlParameter("quantity", NpgsqlDbType.Integer) { Value = line.Quantity });
            command.Parameters.Add(new NpgsqlParameter("price", NpgsqlDbType.Numeric) { Value = line.UnitPrice.Amount });
            command.Parameters.Add(new NpgsqlParameter("currency", NpgsqlDbType.Text) { Value = line.UnitPrice.Currency });

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<NpgsqlConnection> OpenAsync(string? tenantId, CancellationToken cancellationToken)
    {
        var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await CrmTenantScope.ApplyAsync(connection, tenantId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return connection;
    }
}

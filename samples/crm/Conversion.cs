using FlowX;
using Npgsql;
using NpgsqlTypes;

namespace Crm;

// --------------------------------------------------------------------------- the contracts

/// <summary>What a representative asks for when a lead becomes real business.</summary>
/// <remarks>
/// <para>
/// <strong>It carries no tenant, and it carries no identifiers for what it creates.</strong>
/// The tenant is resolved from validated claims and read off
/// <see cref="CapabilityContext.TenantId"/>; the three ids are <em>derived</em> from the lead by
/// <see cref="ConversionIds"/>, which is what lets a compensation name the row its step wrote
/// without having seen that step's result.
/// </para>
/// <para>
/// The company and the contact's name are not here either: the lead already holds them, and
/// asking the caller to repeat them would let the two disagree.
/// </para>
/// </remarks>
/// <param name="LeadId">The lead being converted.</param>
/// <param name="Industry">What the new account does.</param>
/// <param name="Region">Where it is.</param>
/// <param name="Owner">The representative who will hold the account and the opportunity.</param>
/// <param name="OpportunityName">What the new piece of business is called.</param>
/// <param name="Amount">What it is thought to be worth.</param>
/// <param name="Currency">The currency of <paramref name="Amount"/>. Never implied — §5.2.</param>
/// <param name="Stage">The stage of the process version the opportunity starts pinned to.</param>
/// <param name="ExpectedClose">When the representative thinks it closes.</param>
public sealed record ConvertLead(
    Guid LeadId,
    string Industry,
    string Region,
    Guid Owner,
    string OpportunityName,
    decimal Amount,
    string Currency,
    Guid Stage,
    DateOnly ExpectedClose);

/// <summary>The three rows a conversion left behind.</summary>
/// <param name="Account">The account created.</param>
/// <param name="Contact">The contact created.</param>
/// <param name="Opportunity">The opportunity created.</param>
public sealed record ConversionResult(Guid Account, Guid Contact, Guid Opportunity);

/// <summary>The lead, read once and carried through the saga.</summary>
/// <remarks>
/// <see cref="Email"/> is <c>[Sensitive]</c> here for the same reason it is on
/// <see cref="Lead"/>: this record reaches the journal as a step result, and the marker is what
/// keeps the address out of the row, the outbox and the problem document.
/// </remarks>
/// <param name="LeadId">The lead.</param>
/// <param name="Company">Its company, which becomes the account's name.</param>
/// <param name="ContactName">Its contact, who becomes the contact.</param>
/// <param name="Email">Their address. <strong>Sensitive.</strong></param>
public sealed record LeadUnderConversion(
    Guid LeadId,
    string Company,
    string ContactName,
    [property: Sensitive] string? Email);

/// <summary>Create the account for a conversion.</summary>
/// <remarks>
/// <strong><see cref="AccountId"/> is on the request, not minted inside the capability, and
/// that is what makes the undo possible.</strong> A compensation in FlowX receives the
/// <em>input</em> of the step it undoes — <c>ReverseDebit</c> in <c>samples/banking</c> takes
/// the same <c>DebitInstruction</c> its step took. An id minted inside
/// <see cref="CreateAccount"/> would live only in that step's result, which the undo never
/// sees. Deriving it from the lead instead means both halves read the same value from the same
/// place, and a node that dies between the write and the commit still undoes the right row.
/// </remarks>
/// <param name="AccountId">Derived from the lead by <see cref="ConversionIds"/>.</param>
/// <param name="Name">The account's name, which is the lead's company.</param>
/// <param name="Industry">What it does.</param>
/// <param name="Region">Where it is.</param>
/// <param name="Owner">Who holds it.</param>
public sealed record CreateAccountRequest(
    Guid AccountId,
    string Name,
    string Industry,
    string Region,
    Guid Owner);

/// <summary>An account was created, or removed again.</summary>
/// <param name="AccountId">The row.</param>
public sealed record AccountWritten(Guid AccountId);

/// <summary>Create the contact for a conversion.</summary>
/// <param name="ContactId">Derived from the lead.</param>
/// <param name="AccountId">The account they work at.</param>
/// <param name="FullName">Their name, from the lead.</param>
/// <param name="Email">Their address, from the lead. <strong>Sensitive.</strong></param>
public sealed record CreateContactRequest(
    Guid ContactId,
    Guid AccountId,
    string FullName,
    [property: Sensitive] string? Email);

/// <summary>A contact was created, or removed again.</summary>
/// <param name="ContactId">The row.</param>
public sealed record ContactWritten(Guid ContactId);

/// <summary>Create the opportunity for a conversion.</summary>
/// <param name="OpportunityId">Derived from the lead.</param>
/// <param name="AccountId">Whose business it is.</param>
/// <param name="ContactId">Who is being sold to.</param>
/// <param name="Request">What the caller asked for.</param>
public sealed record CreateOpportunityRequest(
    Guid OpportunityId,
    Guid AccountId,
    Guid ContactId,
    ConvertLead Request);

/// <summary>An opportunity was created, or removed again.</summary>
/// <param name="OpportunityId">The row.</param>
public sealed record OpportunityWritten(Guid OpportunityId);

/// <summary>Mark the lead converted and name what it became.</summary>
/// <param name="LeadId">The lead.</param>
/// <param name="Result">The three rows.</param>
public sealed record MarkLeadConvertedRequest(Guid LeadId, ConversionResult Result);

/// <summary>The conversion is recorded.</summary>
/// <param name="LeadId">The lead.</param>
/// <param name="Result">What it became.</param>
public sealed record LeadConversionRecorded(Guid LeadId, ConversionResult Result);

/// <summary>Derives the three row ids from the lead, without a clock or a random source.</summary>
/// <remarks>
/// <para>
/// <strong>Pure, so a replay lands on the same three rows.</strong> The engine journals a
/// step's result, so a completed step is never re-run — but a step that died between its write
/// and its commit <em>is</em>, and its undo runs against whatever the input names. A random id
/// would make those two different rows.
/// </para>
/// <para>
/// The same derivation the platform uses for an instance a cron occurrence or an outbox change
/// starts: hash the parts, and keep the UUID version and variant bits so the value is a
/// well-formed identifier rather than sixteen bytes that happen to fit.
/// </para>
/// </remarks>
public static class ConversionIds
{
    /// <summary>The account a conversion of this lead creates.</summary>
    /// <param name="leadId">The lead.</param>
    /// <returns>The derived id.</returns>
    public static Guid Account(Guid leadId) => Derive(leadId, "account");

    /// <summary>The contact a conversion of this lead creates.</summary>
    /// <param name="leadId">The lead.</param>
    /// <returns>The derived id.</returns>
    public static Guid Contact(Guid leadId) => Derive(leadId, "contact");

    /// <summary>The opportunity a conversion of this lead creates.</summary>
    /// <param name="leadId">The lead.</param>
    /// <returns>The derived id.</returns>
    public static Guid Opportunity(Guid leadId) => Derive(leadId, "opportunity");

    private static Guid Derive(Guid leadId, string role)
    {
        Span<byte> seed = stackalloc byte[16 + 16];

        leadId.TryWriteBytes(seed);
        System.Text.Encoding.UTF8.GetBytes(role, seed[16..]);

        Span<byte> hash = stackalloc byte[32];

        System.Security.Cryptography.SHA256.HashData(seed, hash);

        var id = hash[..16];

        id[6] = (byte)((id[6] & 0x0F) | 0x80);   // version 8: a name-derived id
        id[8] = (byte)((id[8] & 0x3F) | 0x80);   // RFC 4122 variant

        return new Guid(id);
    }
}

// ----------------------------------------------------------------------------- the errors

/// <summary>Refusals the conversion can produce.</summary>
public static class ConversionErrors
{
    /// <summary>No such lead in the caller's tenant.</summary>
    /// <param name="leadId">What was asked for.</param>
    public static Error LeadNotFound(Guid leadId) =>
        new Error(
            "crm.lead_not_found",
            "That lead is not in this tenant.",
            ErrorCategory.NotFound)
            .With("leadId", leadId);

    /// <summary>The lead has already been converted.</summary>
    /// <remarks>
    /// <c>Conflict</c> rather than <c>Validation</c>: nothing about the request is malformed,
    /// and a second conversion is refused by the state of the row rather than by its shape.
    /// </remarks>
    /// <param name="leadId">The lead.</param>
    public static Error LeadAlreadyConverted(Guid leadId) =>
        new Error(
            "crm.lead_already_converted",
            "That lead has already been converted.",
            ErrorCategory.Conflict)
            .With("leadId", leadId);

    /// <summary>The lead was disqualified and cannot become business.</summary>
    /// <param name="leadId">The lead.</param>
    public static Error LeadDisqualified(Guid leadId) =>
        new Error(
            "crm.lead_disqualified",
            "A disqualified lead cannot be converted.",
            ErrorCategory.Conflict)
            .With("leadId", leadId);

    /// <summary>The named stage does not belong to an active process for this entity.</summary>
    /// <param name="stageId">What was asked for.</param>
    public static Error StageNotUsable(Guid stageId) =>
        new Error(
            "crm.stage_not_usable",
            "That stage does not belong to an active opportunity process in this tenant.",
            ErrorCategory.Validation)
            .With("stageId", stageId);

    /// <summary>PostgreSQL refused the write.</summary>
    public static Error ConversionUnreachable() =>
        new Error(
            "crm.conversion_unreachable",
            "The CRM tables did not answer.",
            ErrorCategory.Unavailable);
}

// ------------------------------------------------------------------------------ the store

/// <summary>Every write the conversion makes, on a connection narrowed to one tenant.</summary>
/// <remarks>
/// <para>
/// <strong>One class rather than one per capability</strong>, because the six statements below
/// are the same five columns read three ways, and splitting them would put the tenant binding
/// in six places instead of one.
/// </para>
/// <para>
/// Every statement is a <c>const</c>. <c>SqlFitnessTests</c> is a gate that fails on SQL text
/// built at run time, and the tenant never reaches a statement as text — it reaches
/// <see cref="CrmTenantScope"/> as a parameter, and the row-level security policies decide from
/// there.
/// </para>
/// </remarks>
public sealed class ConversionStore
{
    private const string SelectLead = """
        SELECT company, contact_name, email, status, converted_account_id
        FROM lead
        WHERE lead_id = @lead
        """;

    private const string StageIsUsable = """
        SELECT 1
        FROM process_stage s
        JOIN process_definition d ON d.process_id = s.process_id
        WHERE s.stage_id = @stage AND d.is_active AND d.applies_to = 'Opportunity'
        """;

    private const string InsertAccount = """
        INSERT INTO account (account_id, tenant_id, name, industry, lifecycle, region, owner_id)
        VALUES (@id, @tenant, @name, @industry, 'Prospect', @region, @owner)
        """;

    private const string DeleteAccount = "DELETE FROM account WHERE account_id = @id";

    private const string InsertContact = """
        INSERT INTO contact (contact_id, tenant_id, account_id, full_name, email, phone, is_primary)
        VALUES (@id, @tenant, @account, @name, @email, NULL, true)
        """;

    private const string DeleteContact = "DELETE FROM contact WHERE contact_id = @id";

    private const string InsertOpportunity = """
        INSERT INTO opportunity (
            opportunity_id, tenant_id, account_id, primary_contact_id, name,
            amount, currency, stage_id, probability, expected_close, owner_id,
            outcome, stage_entered_at)
        VALUES (@id, @tenant, @account, @contact, @name,
            @amount, @currency, @stage, 0, @close, @owner,
            NULL, @now)
        """;

    private const string DeleteOpportunity = "DELETE FROM opportunity WHERE opportunity_id = @id";

    private const string MarkConverted = """
        UPDATE lead
        SET status = 'Converted',
            converted_account_id = @account,
            converted_contact_id = @contact,
            converted_opportunity_id = @opportunity,
            converted_at = @now
        WHERE lead_id = @lead AND converted_at IS NULL
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public ConversionStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Reads the lead, or says why it cannot be converted.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="leadId">The lead.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The lead, or the refusal.</returns>
    public async ValueTask<Result<LeadUnderConversion>> ReadLeadAsync(
        string? tenantId,
        Guid leadId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = SelectLead;
        command.Parameters.Add(new NpgsqlParameter("lead", NpgsqlDbType.Uuid) { Value = leadId });

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return Result.Fail<LeadUnderConversion>(ConversionErrors.LeadNotFound(leadId));
        }

        var status = reader.GetString(3);

        var alreadyLinked = !await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false);

        if (alreadyLinked || string.Equals(status, "Converted", StringComparison.Ordinal))
        {
            return Result.Fail<LeadUnderConversion>(ConversionErrors.LeadAlreadyConverted(leadId));
        }

        if (string.Equals(status, "Disqualified", StringComparison.Ordinal))
        {
            return Result.Fail<LeadUnderConversion>(ConversionErrors.LeadDisqualified(leadId));
        }

        var email = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetString(2);

        return Result.Ok(new LeadUnderConversion(
            leadId,
            reader.GetString(0),
            reader.GetString(1),
            email));
    }

    /// <summary>Answers whether a stage belongs to an active opportunity process.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="stageId">The stage.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><c>true</c> when an opportunity may start there.</returns>
    public async ValueTask<bool> StageIsUsableAsync(
        string? tenantId,
        Guid stageId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = StageIsUsable;
        command.Parameters.Add(new NpgsqlParameter("stage", NpgsqlDbType.Uuid) { Value = stageId });

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>Creates the account.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">The id to mint it under.</param>
    /// <param name="name">Its name, which is the lead's company.</param>
    /// <param name="industry">What it does.</param>
    /// <param name="region">Where it is.</param>
    /// <param name="owner">Who holds it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask CreateAccountAsync(
        string? tenantId,
        Guid id,
        string name,
        string industry,
        string region,
        Guid owner,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertAccount;
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });
        command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Text) { Value = tenantId ?? string.Empty });
        command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = name });
        command.Parameters.Add(new NpgsqlParameter("industry", NpgsqlDbType.Text) { Value = industry });
        command.Parameters.Add(new NpgsqlParameter("region", NpgsqlDbType.Text) { Value = region });
        command.Parameters.Add(new NpgsqlParameter("owner", NpgsqlDbType.Uuid) { Value = owner });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates the contact.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">The id to mint it under.</param>
    /// <param name="accountId">The account it works at.</param>
    /// <param name="fullName">Their name.</param>
    /// <param name="email">Their address, or null.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask CreateContactAsync(
        string? tenantId,
        Guid id,
        Guid accountId,
        string fullName,
        string? email,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertContact;
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });
        command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Text) { Value = tenantId ?? string.Empty });
        command.Parameters.Add(new NpgsqlParameter("account", NpgsqlDbType.Uuid) { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = fullName });
        command.Parameters.Add(new NpgsqlParameter("email", NpgsqlDbType.Text)
        {
            Value = (object?)email ?? DBNull.Value,
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates the opportunity.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">The id to mint it under.</param>
    /// <param name="accountId">Whose business it is.</param>
    /// <param name="contactId">Who is being sold to.</param>
    /// <param name="request">What the caller asked for.</param>
    /// <param name="now">The engine's clock, never the database's.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask CreateOpportunityAsync(
        string? tenantId,
        Guid id,
        Guid accountId,
        Guid contactId,
        ConvertLead request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertOpportunity;
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });
        command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Text) { Value = tenantId ?? string.Empty });
        command.Parameters.Add(new NpgsqlParameter("account", NpgsqlDbType.Uuid) { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter("contact", NpgsqlDbType.Uuid) { Value = contactId });
        command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = request.OpportunityName });
        command.Parameters.Add(new NpgsqlParameter("amount", NpgsqlDbType.Numeric) { Value = request.Amount });
        command.Parameters.Add(new NpgsqlParameter("currency", NpgsqlDbType.Text) { Value = request.Currency });
        command.Parameters.Add(new NpgsqlParameter("stage", NpgsqlDbType.Uuid) { Value = request.Stage });
        command.Parameters.Add(new NpgsqlParameter("close", NpgsqlDbType.Date) { Value = request.ExpectedClose });
        command.Parameters.Add(new NpgsqlParameter("owner", NpgsqlDbType.Uuid) { Value = request.Owner });
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Marks the lead converted, naming all three rows at once.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="leadId">The lead.</param>
    /// <param name="result">What it became.</param>
    /// <param name="now">The engine's clock.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public async ValueTask MarkConvertedAsync(
        string? tenantId,
        Guid leadId,
        ConversionResult result,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = MarkConverted;
        command.Parameters.Add(new NpgsqlParameter("lead", NpgsqlDbType.Uuid) { Value = leadId });
        command.Parameters.Add(new NpgsqlParameter("account", NpgsqlDbType.Uuid) { Value = result.Account });
        command.Parameters.Add(new NpgsqlParameter("contact", NpgsqlDbType.Uuid) { Value = result.Contact });
        command.Parameters.Add(new NpgsqlParameter("opportunity", NpgsqlDbType.Uuid) { Value = result.Opportunity });
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Undoes a created row by id.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="what">Which of the three tables.</param>
    /// <param name="id">The row.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// <strong>A delete rather than a soft delete, and only because nothing outside the saga
    /// has seen these rows yet.</strong> The account, contact and opportunity a conversion
    /// creates are staged for one caller in one transaction's worth of work; no event has been
    /// published naming them, because <c>lead.converted</c> is staged by the last step and the
    /// last step is the one that cannot fail after them. A row another system had already been
    /// told about would need a tombstone instead.
    /// </remarks>
    public async ValueTask UndoAsync(
        string? tenantId,
        ConversionRow what,
        Guid id,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        // Assigned from a literal on each branch rather than from a switch expression.
        // SqlFitnessTests refuses `CommandText = <anything computed>`, and it is right to: the
        // rule is that every statement this adapter sends is fixed when the build is, and a
        // switch is the shape a run-time value would arrive in.
        if (what == ConversionRow.Account)
        {
            command.CommandText = DeleteAccount;
        }
        else if (what == ConversionRow.Contact)
        {
            command.CommandText = DeleteContact;
        }
        else
        {
            command.CommandText = DeleteOpportunity;
        }

        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

/// <summary>Which of the three tables an undo is against.</summary>
public enum ConversionRow
{
    /// <summary>The account.</summary>
    Account,

    /// <summary>The contact.</summary>
    Contact,

    /// <summary>The opportunity.</summary>
    Opportunity,
}

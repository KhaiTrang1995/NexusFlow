using System.Collections.Concurrent;
using FlowX;
using Npgsql;
using NpgsqlTypes;

namespace Crm;

// ------------------------------------------------------------------------------- contracts

/// <summary>What an outside provider said about a lead's company.</summary>
/// <param name="Industry">What the company does.</param>
/// <param name="Employees">Roughly how many people work there.</param>
/// <param name="Region">Where it is.</param>
public sealed record CompanyProfile(string Industry, int Employees, string Region);

/// <summary>The ticket a provider hands back, and what it is for.</summary>
/// <param name="LeadId">The lead being enriched.</param>
/// <param name="Ticket">What the provider calls the request.</param>
/// <param name="AlreadyAnswered">Whether the answer was there before the first attempt.</param>
public sealed record EnrichmentRequested(Guid LeadId, string Ticket, bool AlreadyAnswered);

/// <summary>One look at whether the provider has answered.</summary>
/// <param name="LeadId">The lead.</param>
/// <param name="Ticket">The ticket.</param>
/// <param name="Profile">What came back, or null while it has not.</param>
public sealed record EnrichmentAttempt(Guid LeadId, string Ticket, CompanyProfile? Profile)
{
    /// <summary>Whether the wait can end.</summary>
    public bool IsAnswered => Profile is not null;
}

/// <summary>The provider's webhook, which ends the same wait the poll is in.</summary>
/// <param name="Ticket">Which request it is answering.</param>
/// <param name="Profile">The answer.</param>
public sealed record EnrichmentWebhook(string Ticket, CompanyProfile Profile);

/// <summary>What was written onto the lead.</summary>
/// <param name="LeadId">The lead.</param>
/// <param name="Industry">What the company does.</param>
/// <param name="Employees">How many people work there.</param>
/// <param name="Region">Where it is.</param>
public sealed record LeadEnriched(Guid LeadId, string Industry, int Employees, string Region);

/// <summary>Refusals the enrichment wait can produce.</summary>
public static class EnrichmentErrors
{
    /// <summary>The lead is not in this tenant, or is gone.</summary>
    /// <param name="leadId">What was named.</param>
    public static Error LeadNotFound(Guid leadId) =>
        new Error(
            "crm.lead_not_found",
            "That lead is not in this tenant.",
            ErrorCategory.NotFound)
            .With("leadId", leadId);

    /// <summary>The provider did not answer inside the budget.</summary>
    /// <param name="leadId">The lead left unenriched.</param>
    /// <remarks>
    /// <strong><c>Unavailable</c>, because the lead is fine and the provider is not.</strong>
    /// The category is what decides whether the engine will retry, and a lead nobody could
    /// enrich this morning is a lead somebody can enrich this afternoon.
    /// </remarks>
    public static Error NotEnrichedInTime(Guid leadId) =>
        new Error(
            "crm.enrichment_timed_out",
            "The enrichment provider did not answer inside the budget for this lead.",
            ErrorCategory.Unavailable)
            .With("leadId", leadId);
}

/// <summary>Derives the ticket a lead's enrichment is asked under.</summary>
/// <remarks>
/// <strong>From the lead, so a redelivered <c>lead.created</c> asks about the same
/// request.</strong> A ticket minted per attempt would leave the provider holding one request
/// per redelivery, and the webhook would then answer a request nothing is waiting on.
/// </remarks>
public static class EnrichmentTickets
{
    /// <summary>The ticket for a lead.</summary>
    /// <param name="leadId">The lead.</param>
    /// <returns>The ticket, which is the lead's id in a form a provider would accept.</returns>
    public static string For(Guid leadId) => "enr-" + leadId.ToString("N");
}

// ---------------------------------------------------------------------------- the provider

/// <summary>
/// Stands where an outside enrichment provider stands.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It is a stand-in and not a client, exactly as <see cref="CrmTokenHandler"/> is a
/// stand-in for an OIDC handler.</strong> A real deployment deletes this and calls an HTTP API;
/// nothing else in §8.2 moves, because what the flow depends on is "ask, then wait" and not who
/// is on the other end. What it deliberately does not do is answer quickly enough to hide the
/// wait — the whole point of the package is the flow holding nothing while somebody else is
/// slow.
/// </para>
/// <para>
/// <strong>Its state is in memory and does not survive the process, which is honest.</strong>
/// The provider's records are the provider's. What the CRM keeps is the ticket and the answer,
/// and those are in <c>lead_enrichment</c> where a restart can still find them.
/// </para>
/// </remarks>
public sealed class EnrichmentProvider
{
    private readonly ConcurrentDictionary<string, CompanyProfile> _answers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Guid> _outstanding = new(StringComparer.Ordinal);

    /// <summary>Records that somebody asked, and hands back the ticket.</summary>
    /// <param name="leadId">The lead being asked about.</param>
    /// <returns>The ticket.</returns>
    public string Request(Guid leadId)
    {
        var ticket = EnrichmentTickets.For(leadId);

        _outstanding[ticket] = leadId;

        return ticket;
    }

    /// <summary>What the provider has for a ticket, if anything.</summary>
    /// <param name="ticket">The ticket.</param>
    /// <returns>The profile, or null while the provider is still working.</returns>
    /// <remarks>
    /// A ticket nobody asked under answers null rather than throwing: a provider does not owe an
    /// error to a caller quoting a reference it has never seen, and a flow that hit one would
    /// simply keep waiting until its budget ran out — which is the right ending for it.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="ticket"/> is null.</exception>
    public CompanyProfile? Poll(string ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        return _outstanding.ContainsKey(ticket) && _answers.TryGetValue(ticket, out var profile)
            ? profile
            : null;
    }

    /// <summary>Makes an answer available, as the real provider would when it finished.</summary>
    /// <param name="ticket">The ticket.</param>
    /// <param name="profile">What it found.</param>
    /// <exception cref="ArgumentNullException"><paramref name="ticket"/> or <paramref name="profile"/> is null.</exception>
    public void Answer(string ticket, CompanyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(profile);

        _answers[ticket] = profile;
    }
}

// ------------------------------------------------------------------------------- the store

/// <summary>The CRM's record of what it asked a provider and what came back.</summary>
public sealed class EnrichmentStore
{
    private const string InsertRequest = """
        INSERT INTO lead_enrichment (lead_id, tenant_id, ticket, status, requested_at)
        VALUES (@lead, @tenant, @ticket, 'Pending', @now)
        ON CONFLICT (lead_id) DO NOTHING
        """;

    private const string SelectLead = "SELECT 1 FROM lead WHERE lead_id = @lead";

    private const string SelectProfile = """
        SELECT status, industry, employees, region
        FROM lead_enrichment
        WHERE lead_id = @lead
        """;

    private const string Answer = """
        UPDATE lead_enrichment
        SET status = 'Answered', industry = @industry, employees = @employees,
            region = @region, answered_at = @now
        WHERE lead_id = @lead AND status = 'Pending'
        """;

    private const string Abandon = """
        UPDATE lead_enrichment SET status = 'Abandoned' WHERE lead_id = @lead AND status = 'Pending'
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public EnrichmentStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Whether the tenant has this lead at all.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="leadId">The lead.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when the lead is visible to this tenant.</returns>
    public async ValueTask<bool> LeadExistsAsync(
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

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>Records the request, if one is not already recorded.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="leadId">The lead.</param>
    /// <param name="ticket">The provider's ticket.</param>
    /// <param name="now">The engine's clock.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask RequestAsync(
        string? tenantId,
        Guid leadId,
        string ticket,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertRequest;
        command.Parameters.Add(new NpgsqlParameter("lead", NpgsqlDbType.Uuid) { Value = leadId });
        command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Text) { Value = tenantId ?? string.Empty });
        command.Parameters.Add(new NpgsqlParameter("ticket", NpgsqlDbType.Text) { Value = ticket });
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The answer already recorded for a lead, if there is one.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="leadId">The lead.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The profile, or null while the row is Pending or Abandoned.</returns>
    public async ValueTask<CompanyProfile?> ReadProfileAsync(
        string? tenantId,
        Guid leadId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = SelectProfile;
        command.Parameters.Add(new NpgsqlParameter("lead", NpgsqlDbType.Uuid) { Value = leadId });

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetString(0) != "Answered")
        {
            return null;
        }

        return new CompanyProfile(reader.GetString(1), reader.GetInt32(2), reader.GetString(3));
    }

    /// <summary>Writes what the provider said.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="leadId">The lead.</param>
    /// <param name="profile">The answer.</param>
    /// <param name="now">The engine's clock.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when a Pending row moved; false when it had already been answered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    public async ValueTask<bool> AnswerAsync(
        string? tenantId,
        Guid leadId,
        CompanyProfile profile,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = Answer;
        command.Parameters.Add(new NpgsqlParameter("lead", NpgsqlDbType.Uuid) { Value = leadId });
        command.Parameters.Add(new NpgsqlParameter("industry", NpgsqlDbType.Text) { Value = profile.Industry });
        command.Parameters.Add(new NpgsqlParameter("employees", NpgsqlDbType.Integer) { Value = profile.Employees });
        command.Parameters.Add(new NpgsqlParameter("region", NpgsqlDbType.Text) { Value = profile.Region });
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>Marks a request nobody answered in time.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="leadId">The lead.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask AbandonAsync(
        string? tenantId,
        Guid leadId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = Abandon;
        command.Parameters.Add(new NpgsqlParameter("lead", NpgsqlDbType.Uuid) { Value = leadId });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<NpgsqlConnection> OpenAsync(string? tenantId, CancellationToken cancellationToken) =>
        CrmTenantScope.OpenAsync(_source, tenantId, cancellationToken);
}

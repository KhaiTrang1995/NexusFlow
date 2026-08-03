using System.Text.Json;
using FlowX;
using Npgsql;
using NpgsqlTypes;

namespace Crm;

// --------------------------------------------------------------------------- the contracts

/// <summary>A lead arriving from a form, a list or a conversation.</summary>
/// <param name="Company">Who they work for.</param>
/// <param name="ContactName">Who they are.</param>
/// <param name="Email">How to reach them. <strong>Sensitive.</strong></param>
/// <param name="Source">Where they came from.</param>
public sealed record CaptureLead(
    string Company,
    string ContactName,
    [property: Sensitive] string? Email,
    LeadSource Source);

/// <summary>The lead that was written.</summary>
/// <param name="LeadId">Its id.</param>
public sealed record LeadCaptured(Guid LeadId);

/// <summary>A lead was captured, and three flows want to know.</summary>
/// <remarks>
/// <strong>Ids and the source, and no address.</strong> The scorer wants the company and the
/// source; the assigner wants the region; neither wants the contact's email, and putting it
/// here would put it in the outbox row and on the wire — which is what <c>[Sensitive]</c> on
/// <see cref="Lead.Email"/> exists to prevent. A consumer that needs it reads the row under its
/// own tenant.
/// </remarks>
/// <param name="LeadId">The lead.</param>
/// <param name="Company">Its company.</param>
/// <param name="Source">Where it came from.</param>
public sealed record LeadCreated(Guid LeadId, string Company, LeadSource Source);

/// <summary>A lead was scored.</summary>
/// <param name="LeadId">The lead.</param>
/// <param name="Score">0 to 100.</param>
public sealed record LeadScored(Guid LeadId, int Score);

/// <summary>A lead was given an owner.</summary>
/// <param name="LeadId">The lead.</param>
/// <param name="Owner">Who holds it now.</param>
public sealed record LeadAssigned(Guid LeadId, Guid Owner);

/// <summary>Turns a delivery's body into the event it carries.</summary>
/// <remarks>
/// <strong>Here rather than on one of the two capabilities that need it.</strong>
/// <c>CapabilitiesDoNotCallCapabilities</c> is an architecture gate, and it is right: a
/// capability reaching into another is a dependency the manifest does not describe and the
/// engine cannot authorise. Shared reading is a helper both compose, not a call between them.
/// </remarks>
public static class LeadDeliveries
{
    /// <summary>Reads the event, or null when the delivery carried no body.</summary>
    /// <param name="message">What the broker handed over.</param>
    /// <returns>The event, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public static LeadCreated? Read(BusMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message.Payload is { Length: > 0 } body
            ? JsonSerializer.Deserialize(body, CrmJsonContext.Default.LeadCreated)
            : null;
    }
}

/// <summary>Refusals the intake can produce.</summary>
public static class IntakeErrors
{
    /// <summary>The delivery carried no body, so there is nothing to act on.</summary>
    /// <param name="eventId">Which delivery.</param>
    public static Error EventHasNoBody(Guid eventId) =>
        new Error(
            "crm.event_has_no_body",
            "That delivery carried no body.",
            ErrorCategory.Validation)
            .With("eventId", eventId);
}

// ------------------------------------------------------------------------------ the store

/// <summary>The intake's three writes.</summary>
/// <remarks>
/// Separate from <see cref="ConversionStore"/> because they are a different lifecycle stage
/// with no statement in common — folding them together would produce one class that every
/// capability in the sample takes a dependency on.
/// </remarks>
public sealed class IntakeStore
{
    private const string InsertLead = """
        INSERT INTO lead (lead_id, tenant_id, company, contact_name, email, source, status, score, captured_at)
        VALUES (@id, @tenant, @company, @contact, @email, @source, 'New', 0, @now)
        """;

    private const string UpdateScore = "UPDATE lead SET score = @score WHERE lead_id = @id";

    private const string UpdateOwner = """
        UPDATE lead SET owner_id = @owner, status = 'Working'
        WHERE lead_id = @id AND owner_id IS NULL
        """;

    private const string SelectRegion = """
        SELECT count(*) FROM lead WHERE lead_id = @id AND owner_id IS NOT NULL
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public IntakeStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Writes the lead.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">The id to mint it under.</param>
    /// <param name="request">What arrived.</param>
    /// <param name="now">The engine's clock.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask CaptureAsync(
        string? tenantId,
        Guid id,
        CaptureLead request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertLead;
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });
        command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Text) { Value = tenantId ?? string.Empty });
        command.Parameters.Add(new NpgsqlParameter("company", NpgsqlDbType.Text) { Value = request.Company });
        command.Parameters.Add(new NpgsqlParameter("contact", NpgsqlDbType.Text) { Value = request.ContactName });
        command.Parameters.Add(new NpgsqlParameter("email", NpgsqlDbType.Text)
        {
            Value = (object?)request.Email ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("source", NpgsqlDbType.Text) { Value = request.Source.ToString() });
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the lead's score.</summary>
    /// <param name="tenantId">The tenant the delivery attested.</param>
    /// <param name="id">The lead.</param>
    /// <param name="score">0 to 100.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public ValueTask ScoreAsync(string? tenantId, Guid id, int score, CancellationToken cancellationToken) =>
        WriteAsync(tenantId, UpdateScore, id, ("score", NpgsqlDbType.Integer, score), cancellationToken);

    /// <summary>Gives the lead an owner, if it has none.</summary>
    /// <param name="tenantId">The tenant the delivery attested.</param>
    /// <param name="id">The lead.</param>
    /// <param name="owner">Who takes it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// <strong><c>AND owner_id IS NULL</c> is what makes a redelivery harmless.</strong> A
    /// broker may hand the same <c>lead.created</c> over twice; assigning twice would move a
    /// lead off the representative already working it. The scorer needs no such guard because
    /// the score it computes is a function of the lead and writing it twice writes the same
    /// number.
    /// </remarks>
    public ValueTask AssignAsync(string? tenantId, Guid id, Guid owner, CancellationToken cancellationToken) =>
        WriteAsync(tenantId, UpdateOwner, id, ("owner", NpgsqlDbType.Uuid, owner), cancellationToken);

    /// <summary>Answers whether the lead already has an owner.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="id">The lead.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><c>true</c> when somebody holds it.</returns>
    public async ValueTask<bool> IsAssignedAsync(string? tenantId, Guid id, CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = SelectRegion;
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long and > 0;
    }

    private async ValueTask WriteAsync(
        string? tenantId,
        string statement,
        Guid id,
        (string Name, NpgsqlDbType Type, object Value) parameter,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = statement;
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });
        command.Parameters.Add(new NpgsqlParameter(parameter.Name, parameter.Type) { Value = parameter.Value });

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

// ------------------------------------------------------------------------- the capabilities

/// <summary>Writes an arriving lead.</summary>
[Capability("crm.lead.capture", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    SideEffects = ["crm.lead.written"])]
public sealed class CaptureNewLead : ICapability<CaptureLead, LeadCaptured>
{
    private readonly IntakeStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">The intake writes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public CaptureNewLead(IntakeStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<LeadCaptured>> ExecuteAsync(
        CaptureLead input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        // The idempotency key rather than a fresh Guid: a caller that retries a capture gets
        // one lead. The key is on the invocation and the engine has already refused a second
        // run under it, so this is the belt to that brace.
        // ctx.NewId() rather than Guid.CreateVersion7(): FLOWX1008 refuses the second, because
        // the journal captures what the context mints and reproduces it on a replay, and a lead
        // that came back with a different id on resume would be a second lead.
        var id = ctx.IdempotencyKey is { Length: > 0 } key && Guid.TryParse(key, out var parsed)
            ? parsed
            : ctx.NewId();

        await _store.CaptureAsync(ctx.TenantId, id, input, ctx.UtcNow, ct).ConfigureAwait(false);

        return Result.Ok(new LeadCaptured(id));
    }
}

/// <summary>Scores a lead from what the event carries.</summary>
/// <remarks>
/// <strong>The score is a pure function of the event.</strong> It reads no clock and no other
/// row, so a redelivery writes the same number — which is why <see cref="IntakeStore.ScoreAsync"/>
/// needs no guard and this capability can honestly declare itself idempotent.
/// </remarks>
[Capability("crm.lead.score", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["crm.lead.written"])]
public sealed class ScoreLead : ICapability<BusMessage, LeadScored>
{
    private readonly IntakeStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">The intake writes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public ScoreLead(IntakeStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<LeadScored>> ExecuteAsync(
        BusMessage input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (LeadDeliveries.Read(input) is not { } created)
        {
            return Result.Fail<LeadScored>(IntakeErrors.EventHasNoBody(input.EventId));
        }

        var score = created.Source switch
        {
            LeadSource.Referral => 80,
            LeadSource.Partner => 70,
            LeadSource.Event => 55,
            LeadSource.Web => 40,
            _ => 25,
        };

        await _store.ScoreAsync(ctx.TenantId, created.LeadId, score, ct).ConfigureAwait(false);

        return Result.Ok(new LeadScored(created.LeadId, score));
    }
}

/// <summary>Gives an unowned lead to a representative.</summary>
[Capability("crm.lead.assign", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["crm.lead.written"])]
public sealed class AssignLead : ICapability<BusMessage, LeadAssigned>
{
    /// <summary>The round-robin this sample stands in for a real assignment rule.</summary>
    /// <remarks>
    /// Derived from the lead so it is stable across a redelivery and across a replay. A real
    /// deployment reads a territory table; what matters here is that it is a function of the
    /// event and not of a counter somewhere.
    /// </remarks>
    public static Guid OwnerFor(Guid leadId)
    {
        Span<byte> bytes = stackalloc byte[16];

        leadId.TryWriteBytes(bytes);

        return (bytes[15] & 1) == 0 ? FirstRep : SecondRep;
    }

    private static readonly Guid FirstRep = Guid.Parse("1a2b3c4d-0000-4000-8000-000000000001");
    private static readonly Guid SecondRep = Guid.Parse("1a2b3c4d-0000-4000-8000-000000000002");

    private readonly IntakeStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">The intake writes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public AssignLead(IntakeStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<LeadAssigned>> ExecuteAsync(
        BusMessage input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (LeadDeliveries.Read(input) is not { } created)
        {
            return Result.Fail<LeadAssigned>(IntakeErrors.EventHasNoBody(input.EventId));
        }

        var owner = OwnerFor(created.LeadId);

        await _store.AssignAsync(ctx.TenantId, created.LeadId, owner, ct).ConfigureAwait(false);

        return Result.Ok(new LeadAssigned(created.LeadId, owner));
    }
}

using FlowX;

namespace Crm;

/// <summary>
/// Registers a system this tenant sends to.
/// </summary>
/// <remarks>
/// <strong><c>crm.admin</c>, for the same reason declaring a field is.</strong> A connector is an
/// address this server will later make requests to, carrying this tenant's data, on the say-so of
/// whoever registered it. That is not a grant a representative should hold.
/// </remarks>
[Capability("crm.connector.define", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin",
    Idempotent = true)]
public sealed class DefineCrmConnector : ICapability<DefineConnector, ConnectorDefined>
{
    private readonly ConnectorStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Writes the registration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public DefineCrmConnector(ConnectorStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ConnectorDefined>> ExecuteAsync(
        DefineConnector input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (!CustomValues.IsUsableName(input.Name))
        {
            return Result.Fail<ConnectorDefined>(CustomSchemaErrors.NameIsNotUsable(input.Name));
        }

        if (!IsUsableEndpoint(input.Endpoint))
        {
            return Result.Fail<ConnectorDefined>(
                ConnectorErrors.EndpointIsNotUsable(input.Endpoint));
        }

        var id = await _store
            .RegisterAsync(ctx.TenantId, ctx.NewId(), input, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return id is null
            ? Result.Fail<ConnectorDefined>(ConnectorErrors.NameIsTaken(input.Name))
            : Result.Ok(new ConnectorDefined(id.Value, input.Name));
    }

    /// <summary>Whether this build will make a request to that address.</summary>
    /// <param name="endpoint">What was registered.</param>
    /// <returns>Whether it is usable.</returns>
    /// <remarks>
    /// Absolute and <c>https</c>. This server will later make a request to whatever is stored
    /// here, which is the shape of a server-side request forgery; the rule keeps
    /// <c>file://</c>, a relative path and a plain-text POST of a pipeline out of the table. It
    /// is not a complete answer — an allow-list of hosts is, and this sample does not have the
    /// deployment context to write one — and <see cref="ConnectorErrors.EndpointIsNotUsable"/>
    /// says so rather than implying otherwise.
    /// </remarks>
    public static bool IsUsableEndpoint(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps;
}

/// <summary>
/// Turns a connector on or off without losing what it has already sent.
/// </summary>
/// <remarks>
/// Disabling rather than deleting is how an integration is switched off: the deliveries recorded
/// against it stay readable, which is the whole reason anybody looks at them.
/// </remarks>
[Capability("crm.connector.set_enabled", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin",
    Idempotent = true)]
public sealed class SetCrmConnectorEnabled : ICapability<SetConnectorEnabled, ConnectorEnablementSet>
{
    private readonly ConnectorStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Writes the change.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public SetCrmConnectorEnabled(ConnectorStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ConnectorEnablementSet>> ExecuteAsync(
        SetConnectorEnabled input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var now = await _store
            .SetEnabledAsync(ctx.TenantId, input.ConnectorId, input.IsEnabled, ct)
            .ConfigureAwait(false);

        return now is null
            ? Result.Fail<ConnectorEnablementSet>(ConnectorErrors.NotFound(input.ConnectorId))
            : Result.Ok(new ConnectorEnablementSet(input.ConnectorId, now.Value));
    }
}

/// <summary>
/// Queues something for a connector to send.
/// </summary>
/// <remarks>
/// <strong><c>crm.write</c>, because sending this tenant's data to a system this tenant already
/// registered is data movement rather than configuration.</strong> Deciding <em>where</em> it may
/// go is <c>crm.admin</c>, one capability up.
/// </remarks>
[Capability("crm.connector.publish", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write",
    Idempotent = true)]
public sealed class PublishToCrmConnector : ICapability<PublishToConnector, DeliveryQueued>
{
    private readonly ConnectorStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Reads the connector and queues the delivery.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public PublishToCrmConnector(ConnectorStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<DeliveryQueued>> ExecuteAsync(
        PublishToConnector input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (await _store.ReadAsync(ctx.TenantId, input.ConnectorId, ct).ConfigureAwait(false)
            is not { } registered)
        {
            return Result.Fail<DeliveryQueued>(ConnectorErrors.NotFound(input.ConnectorId));
        }

        if (!registered.IsEnabled)
        {
            return Result.Fail<DeliveryQueued>(ConnectorErrors.IsDisabled(input.ConnectorId));
        }

        var id = ctx.NewId();

        await _store
            .QueueAsync(
                ctx.TenantId,
                id,
                input.ConnectorId,
                input.Subject,
                ConnectorPayload.ToJson(input.Payload),
                ctx.UtcNow,
                ct)
            .ConfigureAwait(false);

        return Result.Ok(new DeliveryQueued(id, DeliveryStatus.Pending));
    }
}

/// <summary>
/// Drains the outbound queue.
/// </summary>
/// <remarks>
/// <para>
/// <strong><c>PerTenant</c>, because the deliveries are a tenant's</strong> — one occurrence per
/// tenant gives the sweep a <c>TenantId</c> to narrow the connection with, and without it the
/// queue would need a connection that could see everybody's, which is the one thing the
/// row-level security exists to prevent.
/// </para>
/// <para>
/// <strong>The clock is the occurrence's.</strong> A schedule that fires late records the
/// delivery at the instant it should have fired, so a replay writes the same row.
/// </para>
/// </remarks>
[Capability("crm.connector.sweep", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true)]
public sealed class SweepConnectorDeliveries : ICapability<ScheduledFire, DeliveriesSwept>
{
    /// <summary>How many deliveries one sweep takes.</summary>
    /// <remarks>
    /// Small, because the claim is held under a transaction for the length of the batch — which
    /// is the cost of <c>SKIP LOCKED</c> being what stops two replicas double-sending. A large
    /// batch would hold one connector's slow gateway against every other tenant's queue.
    /// </remarks>
    public const int Batch = 20;

    private readonly ConnectorStore _store;
    private readonly IConnectorTransport _transport;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Claims the work and records the outcome.</param>
    /// <param name="transport">What reaches the far end.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public SweepConnectorDeliveries(ConnectorStore store, IConnectorTransport transport)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(transport);

        _store = store;
        _transport = transport;
    }

    /// <inheritdoc />
    public async ValueTask<Result<DeliveriesSwept>> ExecuteAsync(
        ScheduledFire input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var (delivered, failed) = await _store
            .SweepAsync(ctx.TenantId, _transport, Batch, input.OccurrenceAt, ct)
            .ConfigureAwait(false);

        return Result.Ok(new DeliveriesSwept(delivered, failed, input.OccurrenceAt));
    }
}

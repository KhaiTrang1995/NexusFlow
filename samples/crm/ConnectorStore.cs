using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// The connector registry and its outbound queue.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Queue, not call.</strong> Nothing here reaches a network. A transition that POSTed to
/// a connector inline would hold a database transaction open across somebody else's gateway,
/// fail the transition when they are slow and lose the notification when they are down. The row
/// is written in the transition's own transaction and drained afterwards — the argument the
/// platform's outbox makes, at the sample's level.
/// </para>
/// <para>
/// <strong>The sweep claims its work with <c>FOR UPDATE SKIP LOCKED</c>.</strong> Two replicas
/// sweeping at once must not both send the same delivery, and a lock the reader takes is what
/// makes that a property of the database rather than of how the schedule is configured.
/// </para>
/// </remarks>
public sealed class ConnectorStore
{
    private const string InsertConnector = """
        INSERT INTO connector (
            connector_id, tenant_id, name, kind, endpoint, secret_name, is_enabled, created_at)
        VALUES (@id, @tenant, @name, @kind, @endpoint, @secret, true, @now)
        ON CONFLICT (tenant_id, name) DO NOTHING
        RETURNING connector_id
        """;

    private const string SetEnabled = """
        UPDATE connector SET is_enabled = @enabled WHERE connector_id = @id
        RETURNING is_enabled
        """;

    private const string ReadConnector = """
        SELECT connector_id, name, kind, endpoint, secret_name, is_enabled
        FROM connector
        WHERE connector_id = @id
        """;

    private const string ReadConnectorByName = """
        SELECT connector_id, name, kind, endpoint, secret_name, is_enabled
        FROM connector
        WHERE name = @name
        """;

    private const string InsertDelivery = """
        INSERT INTO connector_delivery (
            delivery_id, tenant_id, connector_id, subject, payload, status, attempts, created_at)
        VALUES (@id, @tenant, @connector, @subject, @payload::jsonb, 'Pending', 0, @now)
        ON CONFLICT (delivery_id) DO NOTHING
        """;

    // FOR UPDATE SKIP LOCKED, so two replicas sweeping at once take disjoint work rather than
    // both sending the same notification. The join is what lets one statement answer "what is
    // owed, and where does it go" — a second read per delivery would let a connector be disabled
    // between the two.
    private const string ClaimPending = """
        SELECT d.delivery_id, d.subject, d.payload::text, d.attempts,
               c.connector_id, c.name, c.kind, c.endpoint, c.secret_name
        FROM connector_delivery d
        JOIN connector c ON c.connector_id = d.connector_id
        WHERE d.status = 'Pending' AND c.is_enabled
        ORDER BY d.created_at
        LIMIT @limit
        FOR UPDATE OF d SKIP LOCKED
        """;

    private const string MarkDelivered = """
        UPDATE connector_delivery
        SET status = 'Delivered', attempts = attempts + 1, delivered_at = @now, last_error = NULL
        WHERE delivery_id = @id
        """;

    private const string MarkFailed = """
        UPDATE connector_delivery
        SET status = 'Failed', attempts = attempts + 1, last_error = @error
        WHERE delivery_id = @id
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public ConnectorStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Registers a connector, or reports that the name is taken.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">The id to give it.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="now">The invocation's instant, not the wall clock.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id written, or null when the name was taken.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<Guid?> RegisterAsync(
        string? tenantId,
        Guid id,
        DefineConnector request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertConnector;
        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "name", NpgsqlDbType.Text, request.Name);
        Add(command, "kind", NpgsqlDbType.Text, request.Kind.ToString());
        Add(command, "endpoint", NpgsqlDbType.Text, request.Endpoint);
        Add(command, "secret", NpgsqlDbType.Text, (object?)request.SecretName ?? DBNull.Value);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }

    /// <summary>Turns a connector on or off.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="connectorId">Which connector.</param>
    /// <param name="isEnabled">What it becomes.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What it is now, or null when this tenant has no such connector.</returns>
    public async ValueTask<bool?> SetEnabledAsync(
        string? tenantId,
        Guid connectorId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = SetEnabled;
        Add(command, "id", NpgsqlDbType.Uuid, connectorId);
        Add(command, "enabled", NpgsqlDbType.Boolean, isEnabled);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as bool?;
    }

    /// <summary>Reads a connector, and whether it is switched on.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="connectorId">Which connector.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The connector and its enablement, or null when this tenant has no such one.</returns>
    public ValueTask<(ConnectorRow Connector, bool IsEnabled)?> ReadAsync(
        string? tenantId,
        Guid connectorId,
        CancellationToken cancellationToken) =>
        ReadOneAsync(tenantId, ReadConnector, "id", NpgsqlDbType.Uuid, connectorId, cancellationToken);

    private async ValueTask<(ConnectorRow Connector, bool IsEnabled)?> ReadOneAsync(
        string? tenantId,
        string sql,
        string parameter,
        NpgsqlDbType type,
        object value,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = sql;
        Add(command, parameter, type, value);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var secret = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetString(4);

        return (
            new ConnectorRow(
                reader.GetGuid(0),
                reader.GetString(1),
                Enum.Parse<ConnectorKind>(reader.GetString(2)),
                reader.GetString(3),
                secret),
            reader.GetBoolean(5));
    }

    /// <summary>Reads a connector by the name a configured action gave.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="name">The connector's name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The connector and its enablement, or null when this tenant has no such one.</returns>
    /// <remarks>
    /// By name because that is what an administrator writes into an action's parameters — an id
    /// would make a process definition unportable between a tenant's environments, which is the
    /// thing configuration exists to avoid.
    /// </remarks>
    public ValueTask<(ConnectorRow Connector, bool IsEnabled)?> ReadByNameAsync(
        string? tenantId,
        string name,
        CancellationToken cancellationToken) =>
        ReadOneAsync(tenantId, ReadConnectorByName, "name", NpgsqlDbType.Text, name, cancellationToken);

    /// <summary>Queues one delivery.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">The id to give it.</param>
    /// <param name="connectorId">Who it is for.</param>
    /// <param name="subject">What it is about.</param>
    /// <param name="payload">The document to send.</param>
    /// <param name="now">The invocation's instant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask QueueAsync(
        string? tenantId,
        Guid id,
        Guid connectorId,
        string subject,
        string payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertDelivery;
        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "connector", NpgsqlDbType.Uuid, connectorId);
        Add(command, "subject", NpgsqlDbType.Text, subject);
        Add(command, "payload", NpgsqlDbType.Text, payload);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Claims a batch of pending deliveries, sends each, and records what happened.
    /// </summary>
    /// <param name="tenantId">The tenant this occurrence fired for.</param>
    /// <param name="transport">What reaches the far end.</param>
    /// <param name="batch">How many to take.</param>
    /// <param name="now">The occurrence's instant, not the wall clock.</param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <returns>How many were delivered and how many failed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <strong>One transaction, held for the length of the batch.</strong> That is the cost of
    /// <c>SKIP LOCKED</c> being what stops two replicas double-sending: the claim is only worth
    /// anything while the lock is held, so the send happens inside it. It bounds how large a
    /// batch may sensibly be, which is why <paramref name="batch"/> is a parameter and small.
    /// </para>
    /// <para>
    /// <strong>A failure is recorded, not thrown.</strong> One connector's gateway being down
    /// must not leave the rest of the queue unattempted, so the reason lands in
    /// <c>last_error</c> and the sweep carries on.
    /// </para>
    /// </remarks>
    public async ValueTask<(int Delivered, int Failed)> SweepAsync(
        string? tenantId,
        IConnectorTransport transport,
        int batch,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closingTransaction = transaction.ConfigureAwait(false);

        var claimed = await ClaimAsync(connection, batch, cancellationToken).ConfigureAwait(false);
        var delivered = 0;
        var failed = 0;

        foreach (var delivery in claimed)
        {
            var reason = await transport.SendAsync(delivery, cancellationToken).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using var closingCommand = command.ConfigureAwait(false);

            if (reason is null)
            {
                command.CommandText = MarkDelivered;
                Add(command, "id", NpgsqlDbType.Uuid, delivery.Id);
                Add(command, "now", NpgsqlDbType.TimestampTz, now);
                delivered++;
            }
            else
            {
                command.CommandText = MarkFailed;
                Add(command, "id", NpgsqlDbType.Uuid, delivery.Id);
                Add(command, "error", NpgsqlDbType.Text, reason);
                failed++;
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return (delivered, failed);
    }

    private static async ValueTask<List<PendingDelivery>> ClaimAsync(
        NpgsqlConnection connection,
        int batch,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = ClaimPending;
        Add(command, "limit", NpgsqlDbType.Integer, batch);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var claimed = new List<PendingDelivery>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var secret = await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetString(8);

            claimed.Add(new PendingDelivery(
                reader.GetGuid(0),
                new ConnectorRow(
                    reader.GetGuid(4),
                    reader.GetString(5),
                    Enum.Parse<ConnectorKind>(reader.GetString(6)),
                    reader.GetString(7),
                    secret),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3)));
        }

        return claimed;
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

    private ValueTask<NpgsqlConnection> OpenAsync(string? tenantId, CancellationToken cancellationToken) =>
        CrmTenantScope.OpenAsync(_source, tenantId, cancellationToken);
}

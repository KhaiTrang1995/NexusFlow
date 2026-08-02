using System.Globalization;
using Npgsql;

namespace FlowX.Postgres;

/// <summary>
/// Reads <c>outbox_event</c> forward as a change feed, and keeps each subscription's cursor.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The second consumer of a table that already had a producer.</strong>
/// <see cref="PostgresOutboxPublisher"/> drains the outbox to a broker; this reads the same rows
/// without taking them, so a `[ChangeTrigger]` flow starts from another flow's `.Emit` with no
/// broker anywhere in the path
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0050-a-change-trigger-observes-the-outbox.md">ADR-0047</a>).
/// </para>
/// <para>
/// <strong>It writes <c>change_cursor</c> and nothing else.</strong> <c>published_at</c> belongs
/// to the publisher; a feed that marked it would take events away from the broker, and one that
/// filtered on it would race the publisher for them. The two paths therefore coexist over one
/// table with no coordination at all.
/// </para>
/// <para>
/// <strong>What makes the cursor safe is the snapshot barrier</strong>, and it is worth stating
/// here as well as in the SQL: a row is offered only once its staging transaction id is below
/// <c>pg_snapshot_xmin(pg_current_snapshot())</c>, at which point no transaction that could
/// insert an earlier row is still running. Without it, a cursor over the outbox skips rows that
/// commit late, silently and for ever — which is the hazard <c>0004</c>'s own comment describes
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0048-a-change-feed-advances-a-cursor.md">ADR-0048</a>).
/// </para>
/// <para>
/// <strong>A subscription with no cursor row starts at the beginning of what the outbox still
/// holds</strong>, rather than at the end. That is deliberate: "deployed today, so it observes
/// nothing that happened yesterday" would be an at-most-once feed whose behaviour depended on
/// deployment timing, and a new subscription that wants a clean start can have its cursor written
/// once by an operator. The bound on how far back that reaches is retention, not this class.
/// </para>
/// <para>
/// <strong>Two reads and one write, all unbatched and none in a transaction.</strong> The cursor
/// read and the change read are separate statements because the cursor is the only input to the
/// second, and the commit is a later call by contract — the host runs flows in between. Wrapping
/// them would hold a transaction open across the flows, which is the one thing a durable
/// execution engine must never do.
/// </para>
/// <para>
/// <strong>Every change names its tenant, which is what makes a change trigger usable under
/// isolation at all.</strong> The read joins <c>flow_instance</c> and takes the emitting
/// instance's <c>tenant_id</c> — the same foreign key 0008's policy decides visibility through —
/// so the host starts the observing flow in the tenant whose data the change came out of, with
/// no claim anywhere in the path and nothing a caller could have set. At
/// <see cref="TenantIsolation.Schema"/> the feed additionally fans out over the tenant registry;
/// see the second constructor for why a batch is always one tenant's.
/// </para>
/// </remarks>
public sealed class PostgresChangeFeed : IChangeFeed
{
    /// <summary>The feed family this serves, as <see cref="Feed"/> reports it.</summary>
    public const string FeedName = "postgres-outbox";

    /// <summary>The position a subscription that has never read starts from.</summary>
    /// <remarks>
    /// <c>0</c> is below every value <c>pg_current_xact_id()</c> can return, so the first read
    /// offers everything below the barrier. Rendered as the same string the feed's own positions
    /// are, so nothing downstream has two shapes to handle.
    /// </remarks>
    private static readonly Position Beginning = new(0, 0);

    private readonly NpgsqlDataSource? _dataSource;
    private readonly PostgresTenantStores? _stores;
    private int _rotation;

    /// <summary>Creates a feed over a data source.</summary>
    /// <param name="dataSource">
    /// The data source. Its connection string selects the schema, and the feed does not own it —
    /// a data source is pooled and shared with the journal.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is null.</exception>
    public PostgresChangeFeed(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <summary>Creates a feed that reads each tenant's schema in turn.</summary>
    /// <param name="stores">The per-tenant pools, and the registry that lists them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stores"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <strong>One pass reads one tenant, and that is what keeps the cursor a cursor.</strong>
    /// A read that merged several tenants' changes would hand the host one ordered list whose
    /// positions belong to different <c>change_cursor</c> rows, and the host's contract is to
    /// commit exactly one position — the last change it finished with. So the fan-out visits
    /// tenants in a rotating order and returns the first tenant that has anything, which makes
    /// every batch one tenant's and every commit that tenant's own row.
    /// </para>
    /// <para>
    /// <strong>The rotation is what stops a busy tenant starving the rest.</strong>
    /// <see cref="PostgresOutboxPublisher"/>'s reasoning exactly: without it the first tenant in
    /// the registry with a permanent backlog would be the only one ever read.
    /// </para>
    /// </remarks>
    public PostgresChangeFeed(PostgresTenantStores stores)
    {
        ArgumentNullException.ThrowIfNull(stores);

        _stores = stores;
    }

    /// <inheritdoc />
    public string Feed => FeedName;

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<ObservedChange>>> ReadAsync(
        ChangeSubscription subscription, int max, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);

        if (_stores is null)
        {
            return await ReadFromAsync(_dataSource!, subscription, max, tenantId: null, cancellationToken)
                .ConfigureAwait(false);
        }

        // Re-read every pass rather than cached, for KnownTenantsAsync's own stated reason: a
        // tenant another node provisioned five seconds ago has changes this node must observe.
        var tenants = await _stores.KnownTenantsAsync(cancellationToken).ConfigureAwait(false);

        if (tenants.Count == 0)
        {
            return Result.Ok<IReadOnlyList<ObservedChange>>([]);
        }

        var offset = (int)((uint)Interlocked.Increment(ref _rotation) % (uint)tenants.Count);

        for (var i = 0; i < tenants.Count; i++)
        {
            var tenant = tenants[(i + offset) % tenants.Count];
            var store = await _stores.ForAsync(tenant, cancellationToken).ConfigureAwait(false);

            var read = await ReadFromAsync(store, subscription, max, tenant, cancellationToken)
                .ConfigureAwait(false);

            if (read.IsFailure || read.Value.Count > 0)
            {
                return read;
            }
        }

        return Result.Ok<IReadOnlyList<ObservedChange>>([]);
    }

    /// <inheritdoc />
    public async ValueTask<Result<bool>> CommitAsync(
        ChangeSubscription subscription,
        ChangePosition position,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        if (Position.TryParse(position.Value) is not { } parsed)
        {
            // A position this feed did not produce. An exception rather than a Result: the
            // contract says the host hands back a position that came with a change, so this is a
            // defect in the caller and not a store that is unreachable (ADR-0007).
            throw new ArgumentException(
                $"'{position.Value}' is not a position {FeedName} issued. A change feed's " +
                "position is opaque above the plugin, and the only legitimate value is one that " +
                "arrived on an ObservedChange from this feed.",
                nameof(position));
        }

        // The tenant travels inside the opaque position rather than on the method, because the
        // host has no tenant to pass: IChangeFeed.CommitAsync takes a subscription and a
        // position, and adding a third parameter would put the fan-out into a contract every
        // other feed would then have to carry. A position is defined as this feed's to render
        // and this feed's to read back, so it is exactly the right place to keep it.
        var dataSource = parsed.TenantId is { Length: > 0 } tenant && _stores is not null
            ? await _stores.ForAsync(tenant, cancellationToken).ConfigureAwait(false)
            : _dataSource!;

        var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closing = connection.ConfigureAwait(false);

        using var command = connection.CreateCommand();

        command.CommandText = ChangeFeedSql.CommitCursor;
        command.Parameters.Add(Db.Uuid("subscription", SubscriptionIdFor(subscription)));
        command.Parameters.Add(Db.Text("position_xid", parsed.Xid));
        command.Parameters.Add(Db.Long("position_seq", parsed.Seq));

        var moved = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Zero rows is the monotonicity guard refusing to move the cursor backwards, which is
        // success: a node that raced a faster one has nothing to record.
        return Result.Ok(moved > 0);
    }

    /// <summary>One schema's next batch, cursor read and changes read.</summary>
    private static async ValueTask<Result<IReadOnlyList<ObservedChange>>> ReadFromAsync(
        NpgsqlDataSource dataSource,
        ChangeSubscription subscription,
        int max,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closing = connection.ConfigureAwait(false);

        var position = await CursorAsync(connection, subscription, tenantId, cancellationToken)
            .ConfigureAwait(false);

        using var command = connection.CreateCommand();

        command.CommandText = ChangeFeedSql.ReadFrom;
        command.Parameters.Add(Db.Text("type", subscription.Source));
        command.Parameters.Add(Db.Text("position_xid", position.Xid));
        command.Parameters.Add(Db.Long("position_seq", position.Seq));
        command.Parameters.Add(Db.Int("batch", max));

        var changes = new List<ObservedChange>();

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            changes.Add(Read(reader, subscription));
        }

        return Result.Ok<IReadOnlyList<ObservedChange>>(changes);
    }

    /// <summary>
    /// The id a subscription's cursor row is keyed on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Derived here rather than taken from the host, and derived the same way the
    /// instance id is</strong> — SHA-256 over the four terms NUL-separated, laid out as a UUID
    /// version 8. <c>FlowX.Runtime.ChangeIdentity</c> is where the host's derivation lives, and
    /// this package may not reference it: a store depends on <c>FlowX.Abstractions</c> and
    /// nothing else (ADR-0009), which <c>RuntimeDoesNotReferenceAnyPlugin</c> and the dependency
    /// gates hold it to.
    /// </para>
    /// <para>
    /// The two derivations are over different scopes and produce different ids on purpose: a
    /// cursor key and an instance id are two different subjects, and a collision between them
    /// would be a cursor row keyed on an instance.
    /// </para>
    /// </remarks>
    private static Guid SubscriptionIdFor(ChangeSubscription subscription)
    {
        var material = string.Join(
            '\0',
            "flowx\0change\0cursor",
            subscription.FlowId,
            subscription.FlowVersion,
            subscription.Source,
            subscription.Group);

        Span<byte> digest = stackalloc byte[32];

        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(material), digest);

        Span<byte> bytes = stackalloc byte[16];

        digest[..16].CopyTo(bytes);

        // RFC 9562 §4.2: version 8 is the slot for a derived id, so an operator reading the
        // table can tell a computed key from a minted one.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes, bigEndian: true);
    }

    private static async ValueTask<Position> CursorAsync(
        NpgsqlConnection connection,
        ChangeSubscription subscription,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = ChangeFeedSql.ReadCursor;
        command.Parameters.Add(Db.Uuid("subscription", SubscriptionIdFor(subscription)));

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closing = reader.ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new Position(
                ulong.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                reader.GetInt64(1),
                tenantId)
            : Beginning with { TenantId = tenantId };
    }

    /// <summary>One outbox row, as the flow it starts will receive it.</summary>
    /// <remarks>
    /// <c>Topic</c> is the subscription's declared source rather than a column, for the reason
    /// <c>RedisStreamBusConsumer</c> fills it from the subscription: the topic is the address a
    /// consumer declared, and the row knows only its own type. They are the same string here, and
    /// keeping the declared one is what makes them the same string when a future feed's address
    /// is not a type.
    /// </remarks>
    private static ObservedChange Read(NpgsqlDataReader reader, ChangeSubscription subscription)
    {
        var tenantId = Db.NullableString(reader, 7);

        var message = new BusMessage(
            reader.GetGuid(0),
            subscription.Source,
            reader.GetString(1),
            reader.GetString(2),
            Db.NullableString(reader, 3),
            Db.NullableString(reader, 4),
            tenantId);

        var position = new Position(
            ulong.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            reader.GetInt64(6),
            tenantId);

        return new ObservedChange(
            message, new ChangePosition(position.ToString()), tenantId);
    }

    /// <summary>
    /// A cursor position: the transaction that staged a change, and its position within that
    /// transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Both halves are needed, and neither alone is safe.</strong> The transaction id is
    /// what the visibility barrier is expressed in, so it is what the cursor has to be over; the
    /// staging sequence is what separates two events one transaction staged, which the id cannot.
    /// </para>
    /// <para>
    /// <c>xid8</c> is a 64-bit unsigned counter, so it is carried as a <see cref="ulong"/> and
    /// passed to the server as text with an explicit cast rather than through a driver mapping —
    /// which keeps the value exact at the top of the range and needs no <c>NpgsqlDbType</c> this
    /// package would have to keep in step with the driver.
    /// </para>
    /// </remarks>
    /// <param name="Transaction">The transaction that staged the change.</param>
    /// <param name="Seq">Its position within that transaction.</param>
    /// <param name="TenantId">
    /// Whose <c>change_cursor</c> row this position belongs to, or null in a single-schema
    /// deployment. Rendered as a third field so that <see cref="CommitAsync"/> can find the
    /// schema the cursor lives in without <see cref="IChangeFeed"/> growing a tenant parameter
    /// no other feed would have anything to put in.
    /// </param>
    private readonly record struct Position(ulong Transaction, long Seq, string? TenantId = null)
    {
        /// <summary>The transaction id, as the server reads and writes it.</summary>
        public string Xid => Transaction.ToString(CultureInfo.InvariantCulture);

        /// <summary>Parses a position this feed rendered, or null for anything else.</summary>
        /// <remarks>
        /// Split into three at most, so a tenant id containing a colon comes back whole. The
        /// first two fields cannot contain one, which is what makes the split unambiguous from
        /// the left.
        /// </remarks>
        public static Position? TryParse(string? value)
        {
            if (value?.Split(':', 3) is not { Length: >= 2 } parts ||
                !ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var transaction) ||
                !long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seq))
            {
                return null;
            }

            return new Position(
                transaction, seq, parts.Length == 3 && parts[2].Length > 0 ? parts[2] : null);
        }

        /// <inheritdoc />
        public override string ToString() => TenantId is { Length: > 0 } tenant
            ? string.Create(CultureInfo.InvariantCulture, $"{Transaction}:{Seq}:{tenant}")
            : string.Create(CultureInfo.InvariantCulture, $"{Transaction}:{Seq}");
    }
}

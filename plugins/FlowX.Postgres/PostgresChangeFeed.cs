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
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0047-a-change-trigger-observes-the-outbox.md">ADR-0047</a>).
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

    private readonly NpgsqlDataSource _dataSource;

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

    /// <inheritdoc />
    public string Feed => FeedName;

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<ObservedChange>>> ReadAsync(
        ChangeSubscription subscription, int max, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closing = connection.ConfigureAwait(false);

        var position = await CursorAsync(connection, subscription, cancellationToken)
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

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
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
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = ChangeFeedSql.ReadCursor;
        command.Parameters.Add(Db.Uuid("subscription", SubscriptionIdFor(subscription)));

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closing = reader.ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new Position(ulong.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.GetInt64(1))
            : Beginning;
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
        var message = new BusMessage(
            reader.GetGuid(0),
            subscription.Source,
            reader.GetString(1),
            reader.GetString(2),
            Db.NullableString(reader, 3),
            Db.NullableString(reader, 4));

        var position = new Position(
            ulong.Parse(reader.GetString(5), CultureInfo.InvariantCulture), reader.GetInt64(6));

        return new ObservedChange(message, new ChangePosition(position.ToString()));
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
    private readonly record struct Position(ulong Transaction, long Seq)
    {
        /// <summary>The transaction id, as the server reads and writes it.</summary>
        public string Xid => Transaction.ToString(CultureInfo.InvariantCulture);

        /// <summary>Parses a position this feed rendered, or null for anything else.</summary>
        public static Position? TryParse(string? value)
        {
            if (value?.Split(':') is not { Length: 2 } parts ||
                !ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var transaction) ||
                !long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seq))
            {
                return null;
            }

            return new Position(transaction, seq);
        }

        /// <inheritdoc />
        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"{Transaction}:{Seq}");
    }
}

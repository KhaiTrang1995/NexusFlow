using Npgsql;

namespace FlowX.Postgres;

/// <summary>
/// Drains <c>outbox_event</c> to an <see cref="IEventPublisher"/>, and marks what arrived.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this closes.</strong> <see cref="PostgresFlowJournal"/> already stages events
/// in the same transaction as the step that emitted them, which is the half of
/// <c>docs/11-Distributed-Runtime.md §5</c> that removes the dual-write problem. Nothing read
/// them back out. Constraint <c>C4</c> — "no 2-phase commit; consistency is saga-based,
/// outbox for atomic publish" — named the outbox as the mechanism that makes atomic publish
/// true, and until there was a publisher it named a table.
/// </para>
/// <para>
/// <strong>The ordering offered, exactly.</strong> Events with the same
/// <c>partition_key</c> reach the broker in the order they were staged, including when more
/// than one publisher is running — <see cref="OutboxSql.ClaimPending"/> holds an event back
/// when its key has an older pending sibling this claim did not take. <strong>Global
/// ordering is not offered.</strong> Two events with different keys can arrive in either
/// order, and no configuration changes that: a total order across keys serialises the whole
/// stream to one publisher, and per-key order is what a consumer that partitions by key can
/// actually use. An event with a null key asks for no order and is given none.
/// </para>
/// <para>
/// <strong>At-least-once, and never zero.</strong> A pass claims, publishes, marks and
/// commits in one transaction. A crash anywhere before the commit — including after the
/// broker acknowledged and before <c>published_at</c> was written — rolls the mark back and
/// leaves the rows pending, so the next pass republishes them. The event is delivered twice
/// rather than lost once, which is the composition <c>§4</c> calls effectively-once
/// processing and which requires consumers to be idempotent on
/// <see cref="OutboxRecord.EventId"/>.
/// </para>
/// <para>
/// <strong>The broker call happens inside the transaction, and that is the cost of the
/// guarantee.</strong> The rows stay locked while the publisher is on the network, so a slow
/// broker holds a claim open. That is what <c>FOR UPDATE SKIP LOCKED</c> is for: a second
/// publisher steps over the locked rows instead of waiting. Publishing outside the
/// transaction and marking after would either lose the mark on a crash — same guarantee, no
/// benefit — or need a claim column and a reaper for a publisher that died holding one.
/// </para>
/// <para>
/// <strong>One event nobody can publish stops the ones behind it.</strong> The batch is a
/// prefix contract: the caller marks the first <c>n</c> the publisher acknowledged and offers
/// the rest again, so an event a broker permanently refuses is retried forever and holds up
/// the outbox behind it. There is no dead-letter path here; <c>docs/17-Plugin-System.md §4</c>
/// lists DLQ as part of the <c>PublisherConformance</c> suite that has not been written, and
/// it is not part of this package.
/// </para>
/// </remarks>
public sealed class PostgresOutboxPublisher
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEventPublisher _publisher;
    private readonly PostgresOutboxOptions _options;

    /// <summary>Creates a publisher over a data source and a broker adapter.</summary>
    /// <param name="dataSource">
    /// The data source. Its connection string selects the schema, and the publisher does not
    /// own it — a data source is pooled and shared with the journal.
    /// </param>
    /// <param name="publisher">Where acknowledged events go.</param>
    /// <param name="options">Batch size and poll interval. Defaults to §5's numbers.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public PostgresOutboxPublisher(
        NpgsqlDataSource dataSource,
        IEventPublisher publisher,
        PostgresOutboxOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(publisher);

        _dataSource = dataSource;
        _publisher = publisher;
        _options = options ?? new PostgresOutboxOptions();
    }

    /// <summary>
    /// Claims one batch, publishes it and marks what the broker acknowledged.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What the pass claimed and what it published.</returns>
    /// <remarks>
    /// The whole pass is one transaction, and every early return leaves it to be rolled back
    /// by its disposal. That is the property the crash test exercises: there is no path
    /// through this method that marks an event published without the claim that produced it
    /// committing at the same instant, and no path that loses an event by marking one that
    /// did not arrive.
    /// </remarks>
    public async ValueTask<OutboxPass> PublishPendingAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closing = connection.ConfigureAwait(false);

        var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closingTransaction = transaction.ConfigureAwait(false);

        var claimed = await ClaimAsync(connection, cancellationToken).ConfigureAwait(false);

        if (claimed.Count == 0)
        {
            return OutboxPass.Empty;
        }

        var published = await _publisher.PublishAsync(claimed, cancellationToken)
            .ConfigureAwait(false);

        if (published.IsFailure)
        {
            // The broker refused the batch. Nothing is marked and the transaction rolls back,
            // so every claimed row is pending again the moment the lock is released. The
            // error is the publisher's to report — a caller that wants it can ask the
            // publisher, and a caller that wants throughput wants the next pass.
            return new OutboxPass(claimed.Count, 0, published.Error);
        }

        var acknowledged = Prefix(claimed, published.Value);

        if (acknowledged.Length == 0)
        {
            return new OutboxPass(claimed.Count, 0, null);
        }

        await MarkAsync(connection, acknowledged, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new OutboxPass(claimed.Count, acknowledged.Length, null);
    }

    /// <summary>
    /// Polls until cancelled, draining the outbox as fast as it fills.
    /// </summary>
    /// <param name="cancellationToken">Stops the loop.</param>
    /// <returns>A task that completes when the loop is cancelled.</returns>
    /// <remarks>
    /// <para>
    /// A plain loop rather than a hosted service. This package depends on
    /// <c>FlowX.Abstractions</c> and Npgsql and nothing else (ADR-0009), and a
    /// <c>BackgroundService</c> would add a hosting dependency to a plugin for the sake of
    /// one <c>while</c>. A host wires this the way it wires any long-running task.
    /// </para>
    /// <para>
    /// The loop keys its wait on what was <em>published</em>, not on what was claimed. A pass
    /// that drained a full batch is followed immediately by the next one, so the poll
    /// interval bounds idle latency without throttling a backlog; a pass that claimed a full
    /// batch and published none — a refusing broker, or a batch entirely held behind older
    /// siblings — waits, because retrying that at full speed is a hot loop against a
    /// condition that needs time to change.
    /// </para>
    /// <para>
    /// <strong>Cancellation ends the loop, it does not fault it.</strong> A shutdown that
    /// arrives while a pass is on the network cancels the database call, and the transaction
    /// that call was in rolls back — the claimed events stay pending and the next process to
    /// run publishes them, which is the same at-least-once outcome as a crash. Letting the
    /// <c>OperationCanceledException</c> escape would put a stack trace in every host's log at
    /// every deployment, which is how a component teaches operators to ignore its logs.
    /// </para>
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pass = await PublishPendingAsync(cancellationToken).ConfigureAwait(false);

                if (pass.Published >= _options.BatchSize)
                {
                    continue;
                }

                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Only when this token is the one that was cancelled. A cancellation from
            // anywhere else — a command timeout surfacing as one, say — is a fault and stays
            // one; swallowing every OperationCanceledException would hide a store that has
            // stopped answering behind a loop that looks like it shut down politely.
        }
    }

    /// <summary>Takes the leading events the publisher acknowledged.</summary>
    /// <remarks>
    /// A count above the batch is clamped rather than trusted. <see cref="IEventPublisher"/>
    /// is an extension point, so this number arrives from code this package did not write,
    /// and reading past the end of the claim would be the publisher's defect becoming this
    /// one.
    /// </remarks>
    private static Guid[] Prefix(IReadOnlyList<OutboxRecord> claimed, int published)
    {
        var count = Math.Clamp(published, 0, claimed.Count);
        var events = new Guid[count];

        for (var i = 0; i < count; i++)
        {
            events[i] = claimed[i].EventId;
        }

        return events;
    }

    private async ValueTask<IReadOnlyList<OutboxRecord>> ClaimAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = OutboxSql.ClaimPending;
        command.Parameters.Add(Db.Int("batch", _options.BatchSize));

        var claimed = new List<OutboxRecord>();

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            claimed.Add(JournalRows.PendingOutbox(reader));
        }

        return claimed;
    }

    private static async ValueTask MarkAsync(
        NpgsqlConnection connection,
        Guid[] events,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = OutboxSql.MarkPublished;
        command.Parameters.Add(Db.UuidArray("events", events));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>What one pass of the outbox publisher did.</summary>
/// <param name="Claimed">
/// How many pending events the pass took. Below the batch size means the outbox is drained,
/// or that the rest is held behind an older sibling or another publisher's lock.
/// </param>
/// <param name="Published">
/// How many of them the broker acknowledged and the pass marked. Always a prefix of
/// <paramref name="Claimed"/>, and always committed together with the mark.
/// </param>
/// <param name="Failure">
/// Why the broker took nothing, when it took nothing because it refused. Null when the
/// publisher answered — including when it answered with a count of zero, which is a
/// publisher declining rather than failing.
/// </param>
public readonly record struct OutboxPass(int Claimed, int Published, Error? Failure)
{
    /// <summary>A pass that found nothing pending.</summary>
    public static OutboxPass Empty => new(0, 0, null);
}

using System.Globalization;
using Npgsql;

namespace FlowX.Postgres;

/// <summary>
/// The right to erasure, on PostgreSQL: one indexed lookup and three updates, in one
/// transaction.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is possible at all is a decision taken three phases earlier.</strong>
/// <c>JournalPayload</c> has one exit and no accessor, so every value this store ever received
/// arrived as a document it could not reach into — which is what makes redaction structural.
/// The same shape is what would have made erasure impossible: a store that cannot read a
/// payload cannot search one either. <c>[Subject]</c> and the digest are the narrow answer to
/// that, and <c>flow_instance.subject_digest</c> is where it lands. See
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0061-a-subject-is-erased-by-digest-and-a-residency-is-a-refusal.md">ADR-0061</a>.
/// </para>
/// <para>
/// <strong>Scoped exactly as the journal is.</strong> An erasure opens the same kind of
/// connection every other statement opens — narrowed to <c>flowx_tenant</c>, with
/// <c>flowx.tenant_id</c> set — so the database refuses a cross-tenant erasure by the same
/// policy that refuses a cross-tenant read, and the predicate names the tenant as well. A
/// destructive statement is the last place to rely on one wall.
/// </para>
/// <para>
/// <strong>The dry run is the same transaction, not committed.</strong> Not a separate query
/// path and not a shorter one: it runs the same predicate <em>and the same three updates</em>,
/// reports the row counts they produced, and is disposed unrolled. An operator deciding whether
/// to destroy a patient's records is entitled to the numbers the confirmation will produce
/// rather than to an estimate of them, and two code paths that were meant to agree are two code
/// paths that will not.
/// </para>
/// </remarks>
public sealed class PostgresSubjectErasure : ISubjectErasure
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresTenantStores? _stores;
    private readonly TimeProvider _clock;
    private readonly bool _publisher;
    private readonly Guid[] _cursors;
    private readonly string[] _sources;

    /// <summary>Creates an erasure over a data source.</summary>
    /// <param name="dataSource">
    /// The data source. Not owned here, for <see cref="PostgresFlowJournal"/>'s reason: it is
    /// pooled and shared, and disposing it would close connections the journal is using.
    /// </param>
    /// <param name="stores">
    /// The per-tenant pools at <see cref="TenantIsolation.Schema"/>, or null at
    /// <see cref="TenantIsolation.Row"/>. At schema isolation a subject's rows live in that
    /// tenant's own schema, so an erasure that used the control pool would search an empty
    /// table and report a complete erasure of nothing.
    /// </param>
    /// <param name="consumers">
    /// What reads this deployment's outbox, and therefore which staged events are still owed to
    /// somebody. Null is <see cref="RetentionConsumers.Default"/> — one publisher and no change
    /// subscriptions, which is what a host that has not said otherwise safely behaves as.
    /// </param>
    /// <param name="clock">Stamps the receipt. Defaults to the system clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is null.</exception>
    /// <remarks>
    /// <strong>The consumer set is the same one <see cref="PostgresRetention"/> takes, and it is
    /// not optional in the sense of "not important".</strong> A deployment that stages events,
    /// wires no publisher and leaves this at its default owes every event to a publisher that
    /// does not exist — so every instance is withheld, no erasure ever completes, and the reason
    /// is truthful and useless. Declaring <c>Publisher = false</c> is how a host says that
    /// nothing reads its outbox, which is a fact about the deployment that nothing in the
    /// database can discover: an unpublished row looks identical whether the publisher is
    /// missing or merely down.
    /// </remarks>
    public PostgresSubjectErasure(
        NpgsqlDataSource dataSource,
        PostgresTenantStores? stores = null,
        RetentionConsumers? consumers = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        var declared = consumers ?? RetentionConsumers.Default;

        _dataSource = dataSource;
        _stores = stores;
        _clock = clock ?? TimeProvider.System;
        _publisher = declared.Publisher;
        _cursors = [.. declared.Subscriptions.Select(PostgresRetention.CursorIdFor)];
        _sources = [.. declared.Subscriptions.Select(static subscription => subscription.Source)];
    }

    /// <inheritdoc />
    public async ValueTask<Result<ErasureReceipt>> EraseAsync(
        ErasureRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.SubjectDigest))
        {
            throw new ArgumentException(
                "An erasure names a subject by its digest, and this request names none. A " +
                "blank digest would match every row whose flow identifies nobody.",
                nameof(request));
        }

        var session = await OpenAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = session.ConfigureAwait(false);
        var connection = session.Connection;

        // Opened by the acquire above so the tenant binding had something to be local to.
        var transaction = session.Transaction
            ?? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var closingTransaction = transaction.ConfigureAwait(false);

        var found = await FindAsync(connection, request, cancellationToken).ConfigureAwait(false);


        var erasable = found
            .Where(static candidate => candidate.Reason is null)
            .Select(static candidate => candidate.InstanceId)
            .ToArray();

        var withheld = found
            .Where(static candidate => candidate.Reason is not null)
            .Select(static candidate => new ErasureWithheld(candidate.InstanceId, candidate.Reason!))
            .ToList();

        var steps = 0;
        var events = 0;

        if (erasable.Length > 0)
        {
            // Executed in both modes and committed in one. A dry run that skipped the updates
            // could only report how many instances it matched, and an operator deciding whether
            // to destroy a patient's records is entitled to the row counts rather than to an
            // estimate — the numbers below are the ones the confirmation will produce, because
            // they are produced by the same statements. The transaction is disposed unrolled
            // when nothing commits it, so a dry run changes nothing and holds the rows only for
            // as long as it takes to count them.
            steps = await ExecuteAsync(connection, ErasureSql.ClearSteps, erasable, cancellationToken)
                .ConfigureAwait(false);

            events = await ExecuteAsync(connection, ErasureSql.ClearEvents, erasable, cancellationToken)
                .ConfigureAwait(false);

            await ExecuteAsync(connection, ErasureSql.ClearInstances, erasable, cancellationToken)
                .ConfigureAwait(false);

            if (request.Mode == ErasureMode.Confirm)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return Result.Ok(new ErasureReceipt
        {
            SubjectDigest = request.SubjectDigest,
            TenantId = request.TenantId,
            Mode = request.Mode,
            Matched = found.Count,
            Erased = erasable.Length,
            StepsCleared = steps,
            EventsCleared = events,
            Withheld = withheld,
            At = _clock.GetUtcNow(),
        });
    }

    /// <summary>
    /// Every instance carrying the handle, each already judged erasable or not.
    /// </summary>
    /// <remarks>
    /// The judgement is here rather than in the SQL because the reason has to reach the caller
    /// in words. An instance that is both running and holding an unpublished event is reported
    /// as running: that is the condition that has to clear first, and it is the one an operator
    /// can do nothing about except wait.
    /// </remarks>
    private async Task<List<Candidate>> FindAsync(
        NpgsqlConnection connection,
        ErasureRequest request,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = ErasureSql.FindSubject;
        command.Parameters.Add(Db.Text("digest", request.SubjectDigest));
        command.Parameters.Add(Db.Text("tenant", request.TenantId));

        // The consumer set this deployment declared, bound the way PostgresRetention binds it,
        // because it is the same question about the same rows.
        command.Parameters.Add(Db.Bool("publisher", _publisher));
        command.Parameters.Add(Db.UuidArray("cursors", _cursors));
        command.Parameters.Add(Db.TextArray("sources", _sources));

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var found = new List<Candidate>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var instanceId = reader.GetGuid(0);
            var state = StoredEnums.ToInstanceState(reader.GetString(1));
            var owed = reader.GetInt64(2);

            found.Add(new Candidate(instanceId, WhyNot(state, owed)));
        }

        return found;
    }

    /// <summary>What stands in the way of erasing this instance, or null when nothing does.</summary>
    /// <remarks>
    /// <para>
    /// <strong>A live instance is withheld rather than erased, and this is the whole of the
    /// reason.</strong> Clearing the state bag of a flow that is still running hands every step
    /// after the frontier the values of an execution nobody performed — which is the failure
    /// <c>IStepDispatcher.RestoreState</c> refuses to resume into, arriving from the one
    /// direction it cannot detect: the document would be well-formed and simply absent.
    /// </para>
    /// <para>
    /// <strong>An owed event is withheld for <c>PostgresRetention</c>'s reason.</strong> An
    /// event staged in the same transaction as the step that emitted it is a promise to a
    /// consumer. Emptying its body would keep the promise's shape and discard its content, so
    /// the consumer receives a message that does not carry what its type says it carries —
    /// which is worse than either erasing it or leaving it alone. Which consumers exist is a
    /// deployment fact rather than a column, so it is declared: see the constructor.
    /// </para>
    /// </remarks>
    private static string? WhyNot(FlowInstanceState state, long owed)
    {
        if (state is not (FlowInstanceState.Completed
            or FlowInstanceState.Failed
            or FlowInstanceState.TimedOut
            or FlowInstanceState.CompensationFailed))
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "the instance is {0} and has not finished. Erasing the state bag of a running " +
                "flow would resume it against values no step produced, so it is left alone. " +
                "Run the erasure again once it reaches a terminal state.",
                state);
        }

        return owed > 0
            ? string.Format(
                CultureInfo.InvariantCulture,
                "the instance still holds {0} outbox event(s) owed to a declared consumer. " +
                "Emptying a body the consumer has not taken would put a message on the broker " +
                "that does not carry what its type says it carries. Run the erasure again once " +
                "the consumer has caught up — or, if nothing reads this deployment's outbox, " +
                "say so with RetentionConsumers rather than leaving it at the default.",
                owed)
            : null;
    }

    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        Guid[] instances,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = sql;
        command.Parameters.Add(Db.UuidArray("instances", instances));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ScopedConnection> OpenAsync(
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var source = _stores is not null && tenantId is { Length: > 0 } tenant
            ? await _stores.ForAsync(tenant, cancellationToken).ConfigureAwait(false)
            : _dataSource;

        var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        NpgsqlTransaction? transaction = null;

        try
        {
            // The transaction is opened here rather than by the caller because the binding is
            // transaction-local and has to be inside one to survive to the next statement --
            // TenantScope records the cross-tenant read that made it so. The caller's own
            // atomic boundary then reuses this transaction rather than nesting a second.
            transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            await TenantScope.For(tenantId).ApplyAsync(transaction, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // An unbound connection must not escape a method whose next four statements are
            // destructive: it would run them as the privileged role with no tenant set, which
            // is the one state in which migration 0008's policies are inert.
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }

            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new ScopedConnection(connection, transaction);
    }

    private readonly record struct Candidate(Guid InstanceId, string? Reason);
}

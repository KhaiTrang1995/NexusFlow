using Npgsql;

namespace FlowX.Postgres;

/// <summary>
/// The append-only step history, on PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this adapter is for.</strong> ADR-0015 commits to storage — a primary key
/// an append-only table must reject a duplicate against, one transaction spanning the step
/// row, the instance update and the outbox rows, a fence checked on every write. Those are
/// claims a dictionary cannot disagree with. Here they are a <c>PRIMARY KEY</c>, a
/// <c>BEGIN</c>/<c>COMMIT</c>, and a <c>SELECT … FOR UPDATE</c> that every write passes
/// through.
/// </para>
/// <para>
/// <strong>Refusals are values; a broken store is an exception.</strong> A stale token, a
/// duplicate key and a finished instance are answers this class returns. A closed
/// connection or a missing table is not — it propagates, because a caller that cannot
/// reach its journal has a different problem from a caller that has been told no
/// (ADR-0007).
/// </para>
/// <para>
/// <strong>Serialisation is left to the row lock.</strong> Every write takes
/// <c>flow_instance</c>'s row lock before it decides anything, so the fence check, the
/// terminal check and the allocation of the commit sequence happen under one lock rather
/// than as three statements that can interleave. Contention is per instance, which is the
/// grain the lease already serialises at, so the lock costs nothing a correct caller was
/// not already paying.
/// </para>
/// <para>
/// <strong>On the shape of the <c>await using</c> below.</strong> A connection, a
/// transaction and a reader are all disposed asynchronously, and CA2007 requires the
/// continuation not to be scheduled back onto a caller's context — including the
/// continuation of a disposal. That is what the two-line acquire-then-guard pattern is:
/// the resource is named so it can be used, and its disposal is configured separately.
/// Commands are disposed synchronously because disposing one performs no I/O.
/// </para>
/// </remarks>
public sealed class PostgresFlowJournal : IFlowJournal, ITenantScopedJournal
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresTenantStores? _stores;
    private readonly TenantScope _scope;

    /// <summary>Creates a journal over a data source.</summary>
    /// <param name="dataSource">
    /// The data source. Its connection string selects the schema, and the adapter does not
    /// own it — a data source is pooled and shared, and disposing one from here would close
    /// connections the lease store is still using.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is null.</exception>
    /// <remarks>
    /// Unscoped. A journal built this way reaches every row in the schema, which is what a
    /// single-tenant deployment wants and what every caller got before
    /// <see cref="ForTenant"/> existed. A deployment isolating by tenant obtains a scoped one
    /// and never uses this instance to execute anything.
    /// </remarks>
    public PostgresFlowJournal(NpgsqlDataSource dataSource)
        : this(dataSource, stores: null, TenantScope.None)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
    }

    /// <summary>Creates a journal that gives each tenant a schema of its own.</summary>
    /// <param name="dataSource">
    /// The control schema's data source. Still what an unscoped call reaches — the migration
    /// ledger, the leases and the tenant registry live there — and still not owned here.
    /// </param>
    /// <param name="stores">The per-tenant pools, one schema each.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// <see cref="TenantIsolation.Schema"/>. <see cref="ForTenant"/> now selects a pool as well
    /// as a policy, and the two are applied together rather than one instead of the other:
    /// <see cref="PostgresTenantStores"/> says why both.
    /// </remarks>
    public PostgresFlowJournal(NpgsqlDataSource dataSource, PostgresTenantStores stores)
        : this(dataSource, stores, TenantScope.None)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(stores);
    }

    private PostgresFlowJournal(
        NpgsqlDataSource dataSource,
        PostgresTenantStores? stores,
        TenantScope scope)
    {
        _dataSource = dataSource;
        _stores = stores;
        _scope = scope;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="TenantIsolation.Schema"/> when this journal was given per-tenant pools and
    /// <see cref="TenantIsolation.Row"/> otherwise — a fact about how the adapter was wired,
    /// not about how a deployment configured itself. It is what lets a host refuse to start when
    /// it declares more separation than the store it was handed can deliver, instead of serving
    /// the weaker level under the stronger name.
    /// </remarks>
    public TenantIsolation Isolation =>
        _stores is null ? TenantIsolation.Row : TenantIsolation.Schema;

    /// <inheritdoc />
    public async ValueTask<Result<FlowInstanceRecord>> StartAsync(
        FlowInstanceStart start,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);

        var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var closing = session.ConfigureAwait(false);
        var connection = session.Connection;

        using var command = connection.CreateCommand();

        command.CommandText = JournalSql.StartInstance;
        command.Parameters.Add(Db.Uuid("instance", start.InstanceId));
        command.Parameters.Add(Db.Text("flow_id", start.FlowId));
        command.Parameters.Add(Db.Text("flow_version", start.FlowVersion));
        command.Parameters.Add(Db.Text("tenant_id", start.TenantId));
        command.Parameters.Add(Db.Text("state", StoredEnums.ToText(FlowInstanceState.Pending)));
        command.Parameters.Add(Db.Long("fence", start.Token.Value));
        command.Parameters.Add(Db.Json("input", start.Input.ToJson()));

        // The one handle this row can be erased by, and the only column written from something
        // the store is not allowed to see: the digest was computed inside JournalPayload,
        // before the redaction pass, from a member whose value arrives here as '[redacted]'.
        // Null for a flow that identifies nobody, which is most of them.
        command.Parameters.Add(Db.Text("subject_digest", start.SubjectDigest));
        command.Parameters.Add(Db.Text("correlation_id", start.CorrelationId));
        command.Parameters.Add(Db.Text("trace_id", start.TraceId));
        command.Parameters.Add(Db.Timestamp("deadline_at", start.DeadlineAt));
        command.Parameters.Add(Db.Uuid("parent_instance", start.ParentInstanceId));
        command.Parameters.Add(Db.Text("parent_scope", start.ParentScope.Text));
        command.Parameters.Add(Db.Int("parent_step", start.ParentStepId));

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException failure) when (failure.SqlState == JournalSql.UniqueViolation)
        {
            // The primary key refused it. Starting twice would replace a history rather
            // than extend it, which is the one thing an append-only table cannot do.
            return DurabilityErrors.InstanceExists(start.InstanceId);
        }
        catch (PostgresException failure)
            when (failure.SqlState == JournalSql.InsufficientPrivilege)
        {
            // The tenant policy's WITH CHECK refused it: this connection is scoped to one
            // tenant and the row names another. Unreachable when the host resolved the tenant
            // that opened the instance, which is why it is a refusal rather than a diagnostic
            // — but it is the database refusing to write a row nobody could read back, and
            // that is worth returning honestly instead of throwing.
            //
            // A null TenantId here is the mirror case: an untenanted instance opened through a
            // scoped journal. "(none)" rather than an empty string, because the message names
            // what the row asked for and an empty pair of quotes reads as a missing message.
            return TenantErrors.CrossTenantDenied(start.TenantId ?? "(none)");
        }

        // Before the read, and that order is load-bearing: ReadInstanceAsync opens its own
        // connection, so an insert still sitting in this session's transaction would be
        // invisible to it and the caller would be told the instance it just created does not
        // exist.
        await session.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await ReadInstanceAsync(start.InstanceId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<Result<FencingToken>> FenceAsync(
        Guid instanceId,
        FencingToken token,
        CancellationToken cancellationToken)
    {
        var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var closing = session.ConfigureAwait(false);
        var connection = session.Connection;

        using var command = connection.CreateCommand();

        command.CommandText = JournalSql.RaiseFence;
        command.Parameters.Add(Db.Uuid("instance", instanceId));
        command.Parameters.Add(Db.Long("token", token.Value));

        // Separate so the reader is closed before the commit: committing under an open reader
        // throws, and the answer has already been read by the time this returns.
        var fenced = await RaiseAsync(command, instanceId, token, cancellationToken)
            .ConfigureAwait(false);

        // A scoped session raised the fence inside its own transaction, so the raise is not
        // durable until this runs. An unscoped one has nothing to commit and this is a no-op.
        await session.CommitAsync(cancellationToken).ConfigureAwait(false);

        return fenced;
    }

    /// <summary>Runs the fence statement and reads its answer.</summary>
    /// <param name="command">The prepared fence statement.</param>
    /// <param name="instanceId">The instance being fenced, for the refusal it may return.</param>
    /// <param name="token">The token the caller is raising to.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The fence after the call, or the journal's refusal.</returns>
    private static async ValueTask<Result<FencingToken>> RaiseAsync(
        NpgsqlCommand command,
        Guid instanceId,
        FencingToken token,
        CancellationToken cancellationToken)
    {
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        return InterpretFence(reader, instanceId, token);
    }

    /// <inheritdoc />
    public async ValueTask<Result<JournalStep>> CommitAsync(
        StepCommit commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);

        var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var closing = session.ConfigureAwait(false);
        var connection = session.Connection;

        // A scoped session has already opened one, for the tenant binding to be local to,
        // and Npgsql does not nest: take that one, or open the atomic boundary here when
        // unscoped. Either way the step row, the instance update and the outbox rows land
        // together or not at all, which is what ADR-0015 commits to.
        var transaction = session.Transaction
            ?? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var closingTransaction = transaction.ConfigureAwait(false);

        var guard = await LockAsync(connection, commit.Key.InstanceId, cancellationToken)
            .ConfigureAwait(false);

        if (guard.IsFailure)
        {
            return Result.Fail<JournalStep>(guard.Error);
        }

        var (state, fence, sequence) = guard.Value;

        if (commit.Token < fence)
        {
            return DurabilityErrors.FencedOut(commit.Key.InstanceId, commit.Token, fence);
        }

        if (StoredEnums.IsTerminal(state))
        {
            return DurabilityErrors.InstanceTerminal(commit.Key.InstanceId, state);
        }

        DateTimeOffset committedAt;

        try
        {
            committedAt = await InsertStepAsync(connection, commit, sequence, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PostgresException failure) when (failure.SqlState == JournalSql.UniqueViolation)
        {
            // Returning without committing leaves the transaction to be rolled back by its
            // disposal, so the refused commit stages no outbox row and moves no state bag.
            // That is the half of atomicity ARefusedCommitWritesNothing exists to check.
            return DurabilityErrors.DuplicateStep(commit.Key);
        }

        await StageOutboxAsync(connection, commit, sequence, cancellationToken).ConfigureAwait(false);
        await AdvanceAsync(connection, commit, sequence, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new JournalStep
        {
            Key = commit.Key,
            Sequence = sequence,
            CapabilityId = commit.CapabilityId,
            CapabilityVersion = commit.CapabilityVersion,
            Outcome = commit.Outcome,
            ResultJson = commit.Result.ToJson(),
            Nondeterminism = commit.Nondeterminism,
            Duration = commit.Duration,
            CommittedAt = committedAt,
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// A terminal instance is not completed a second time into a <em>different</em> terminal
    /// state: that would rewrite the outcome, which is the same objection
    /// <c>AFinishedInstanceTakesNoFurtherSteps</c> makes about appending to one. Repeating
    /// the state it already reached is allowed, because a caller that lost the response to
    /// its first call must be able to ask again without inventing a new failure mode.
    /// </remarks>
    public async ValueTask<Result<FlowInstanceRecord>> CompleteAsync(
        Guid instanceId,
        FencingToken token,
        FlowInstanceState state,
        JournalPayload stateBag,
        FlowWake? wake,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stateBag);

        var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var closing = session.ConfigureAwait(false);
        var connection = session.Connection;

        // A scoped session has already opened one, for the tenant binding to be local to,
        // and Npgsql does not nest: take that one, or open the atomic boundary here when
        // unscoped. Either way the step row, the instance update and the outbox rows land
        // together or not at all, which is what ADR-0015 commits to.
        var transaction = session.Transaction
            ?? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var closingTransaction = transaction.ConfigureAwait(false);

        var guard = await LockAsync(connection, instanceId, cancellationToken).ConfigureAwait(false);

        if (guard.IsFailure)
        {
            return Result.Fail<FlowInstanceRecord>(guard.Error);
        }

        var (present, fence, _) = guard.Value;

        if (token < fence)
        {
            return DurabilityErrors.FencedOut(instanceId, token, fence);
        }

        if (StoredEnums.IsTerminal(present) && present != state)
        {
            return DurabilityErrors.InstanceTerminal(instanceId, present);
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = JournalSql.CompleteInstance;
            command.Parameters.Add(Db.Uuid("instance", instanceId));
            command.Parameters.Add(Db.Text("state", StoredEnums.ToText(state)));
            command.Parameters.Add(Db.Json("state_bag", stateBag.ToJson()));

            // All three, or all three null. flow_instance_wake_check refuses anything else,
            // which is what makes the read side able to test one column and trust the other
            // two — and what stops a partial write leaving an instant with no step to belong
            // to, or a step nothing is due to end.
            command.Parameters.Add(Db.Timestamp("wake_at", wake?.At));
            command.Parameters.Add(Db.Int("wake_step", wake?.StepId));
            command.Parameters.Add(Db.Text("wake_scope", wake?.Scope.Text));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await ReadInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<Result<FlowInstanceRecord>> ReadInstanceAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var closing = session.ConfigureAwait(false);
        var connection = session.Connection;

        using var command = connection.CreateCommand();

        command.CommandText = JournalSql.ReadInstance;
        command.Parameters.Add(Db.Uuid("instance", instanceId));

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return DurabilityErrors.InstanceNotFound(instanceId);
        }

        return JournalRows.Instance(reader);
    }

    /// <inheritdoc />
    public async ValueTask<Result<ResumeFrontier>> ReadResumeFrontierAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var instance = await ReadInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);

        if (instance.IsFailure)
        {
            return Result.Fail<ResumeFrontier>(instance.Error);
        }

        var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var closing = session.ConfigureAwait(false);
        var connection = session.Connection;

        using var command = connection.CreateCommand();

        command.CommandText = JournalSql.ReadFrontierSteps;
        command.Parameters.Add(Db.Uuid("instance", instanceId));

        var committed = new List<JournalStep>();

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            committed.Add(JournalRows.Step(reader, instanceId));
        }

        return new ResumeFrontier { Instance = instance.Value, Committed = committed };
    }

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<OutboxRecord>>> ReadOutboxAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var closing = session.ConfigureAwait(false);
        var connection = session.Connection;

        using var command = connection.CreateCommand();

        command.CommandText = JournalSql.ReadOutbox;
        command.Parameters.Add(Db.Uuid("instance", instanceId));

        var staged = new List<OutboxRecord>();

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            staged.Add(JournalRows.Outbox(reader));
        }

        return staged;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A new instance over the same data source, never a mutation of this one. The unscoped
    /// journal stays unscoped because the recovery scan, the timer sweep and the outbox
    /// publisher are node-wide and hold it; scoping it in place would silently narrow their
    /// sweeps to whichever tenant happened to execute last.
    /// </remarks>
    public IFlowJournal ForTenant(string? tenantId) =>
        new PostgresFlowJournal(_dataSource, _stores, TenantScope.For(tenantId));

    /// <summary>
    /// Opens a connection into this journal's tenant's schema and binds it to its tenant, if it
    /// has one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bind is inside the acquire so that no statement anywhere in this class can reach a
    /// connection that has not been through it — which is the property that makes the
    /// database, rather than this file's discipline, the thing enforcing isolation. An
    /// unscoped journal reaches <see cref="TenantScope.IsScoped"/> and returns, so a
    /// single-tenant deployment issues exactly the statements it always did.
    /// </para>
    /// <para>
    /// <strong>Choosing the pool is the first half and it is not optional either.</strong> Under
    /// schema isolation the connection has to come from that tenant's own data source, because
    /// the schema it resolves to is fixed when the socket is opened and cannot be corrected
    /// afterwards. Borrowing from the control pool and setting <c>search_path</c> here is the
    /// arrangement <see cref="PostgresTenantStores"/> exists to avoid.
    /// </para>
    /// </remarks>
    private async ValueTask<ScopedConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var source = _stores is not null && _scope.TenantId is { Length: > 0 } tenant
            ? await _stores.ForAsync(tenant, cancellationToken).ConfigureAwait(false)
            : _dataSource;

        var connection = await source.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!_scope.IsScoped)
        {
            return new ScopedConnection(connection, transaction: null);
        }

        NpgsqlTransaction? transaction = null;

        try
        {
            // The transaction exists so the binding has something to be local to. Opening it
            // here rather than at each call site is what keeps the guarantee impossible to
            // forget: there is one place a scoped connection is made, and it is bound before
            // it is handed out.
            transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            await _scope.ApplyAsync(transaction, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // An unbound connection must not escape: it would run the caller's next statement
            // as the privileged role with no tenant set, which is the one state where every
            // policy in migration 0006 is inert.
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }

            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new ScopedConnection(connection, transaction);
    }

    /// <summary>Reads what the fence statement reported, without deciding any I/O.</summary>
    private static Result<FencingToken> InterpretFence(
        NpgsqlDataReader reader,
        Guid instanceId,
        FencingToken token)
    {
        if (!reader.GetBoolean(2))
        {
            return DurabilityErrors.InstanceNotFound(instanceId);
        }

        var present = new FencingToken(reader.GetInt64(0));

        if (Db.IsNull(reader, 1))
        {
            // The UPDATE matched nothing while the row exists, so the only guard that can
            // have refused it is `fence <= @token`. Lowering the fence would re-admit every
            // writer it had already excluded.
            return DurabilityErrors.FencedOut(instanceId, token, present);
        }

        return new FencingToken(reader.GetInt64(1));
    }

    /// <summary>
    /// Takes the instance's row lock and reads the three facts every write is judged by.
    /// </summary>
    private static async ValueTask<Result<InstanceGuard>> LockAsync(
        NpgsqlConnection connection,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = JournalSql.LockInstance;
        command.Parameters.Add(Db.Uuid("instance", instanceId));

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return DurabilityErrors.InstanceNotFound(instanceId);
        }

        return ReadGuard(reader);
    }

    private static InstanceGuard ReadGuard(NpgsqlDataReader reader) => new(
        StoredEnums.ToInstanceState(reader.GetString(0)),
        new FencingToken(reader.GetInt64(1)),
        reader.GetInt64(2));

    private static async ValueTask<DateTimeOffset> InsertStepAsync(
        NpgsqlConnection connection,
        StepCommit commit,
        long sequence,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = JournalSql.InsertStep;
        command.Parameters.Add(Db.Uuid("instance", commit.Key.InstanceId));
        command.Parameters.Add(Db.Text("scope", commit.Key.Scope.Text));
        command.Parameters.Add(Db.Int("step", commit.Key.StepId));
        command.Parameters.Add(Db.Int("attempt", commit.Key.Attempt));
        command.Parameters.Add(Db.Long("sequence", sequence));
        command.Parameters.Add(Db.Text("capability_id", commit.CapabilityId));
        command.Parameters.Add(Db.Text("capability_version", commit.CapabilityVersion));
        command.Parameters.Add(Db.Text("outcome", StoredEnums.ToText(commit.Outcome)));
        command.Parameters.Add(Db.Json("result", commit.Result.ToJson()));
        command.Parameters.Add(Db.Json(
            "nondeterministic", NondeterminismJson.ToJson(commit.Nondeterminism)));
        command.Parameters.Add(Db.Long("duration_ms", (long)commit.Duration.TotalMilliseconds));

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        return Db.ReadTimestamp(reader, 0);
    }

    private static async ValueTask StageOutboxAsync(
        NpgsqlConnection connection,
        StepCommit commit,
        long sequence,
        CancellationToken cancellationToken)
    {
        for (var ordinal = 0; ordinal < commit.Outbox.Count; ordinal++)
        {
            var staged = commit.Outbox[ordinal];

            using var command = connection.CreateCommand();

            command.CommandText = JournalSql.InsertOutbox;
            command.Parameters.Add(Db.Uuid("event", Guid.NewGuid()));
            command.Parameters.Add(Db.Uuid("instance", commit.Key.InstanceId));
            command.Parameters.Add(Db.Long("sequence", sequence));
            command.Parameters.Add(Db.Int("ordinal", ordinal));
            command.Parameters.Add(Db.Text("type", staged.Type));
            command.Parameters.Add(Db.Text("schema_version", staged.SchemaVersion));
            command.Parameters.Add(Db.Text("partition_key", staged.PartitionKey));
            command.Parameters.Add(Db.Json("payload", staged.Payload.ToJson()));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask AdvanceAsync(
        NpgsqlConnection connection,
        StepCommit commit,
        long sequence,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = JournalSql.AdvanceInstance;
        command.Parameters.Add(Db.Uuid("instance", commit.Key.InstanceId));
        command.Parameters.Add(Db.Long("sequence", sequence));
        command.Parameters.Add(Db.Text(
            "state", commit.State is { } state ? StoredEnums.ToText(state) : null));
        command.Parameters.Add(Db.Json("state_bag", commit.StateBag.ToJson()));
        command.Parameters.Add(Db.Int("resume_hint", commit.ResumeHint));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What the row lock reports: the three facts a write is judged by.</summary>
    private readonly record struct InstanceGuard(
        FlowInstanceState State,
        FencingToken Fence,
        long Sequence);
}

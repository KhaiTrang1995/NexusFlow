using FlowX.Conformance;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// Runs the whole journal suite against PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// The suite is inherited from another assembly, unmodified. That is the deliverable of
/// this package as much as the adapter is: ADR-0015's commitments are about storage, and
/// until now the only thing holding them was a dictionary that has no transaction, no
/// unique constraint and no migration, so it could not disagree with any of them. Every
/// assertion below is now being answered by a <c>PRIMARY KEY</c>, a <c>COMMIT</c> and a
/// <c>SELECT … FOR UPDATE</c>.
/// </para>
/// <para>
/// Each test gets its own schema, and each schema is dropped when the class finishes.
/// </para>
/// </remarks>
public sealed class PostgresJournalConformanceTests : JournalConformance, IAsyncLifetime
{
    private readonly List<PostgresTestSchema> _schemas = [];

    /// <inheritdoc />
    protected override async ValueTask<IFlowJournal> CreateJournalAsync()
    {
        var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        _schemas.Add(schema);

        return schema.Journal;
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var schema in _schemas)
        {
            await schema.DisposeAsync();
        }

        _schemas.Clear();
    }
}

/// <summary>
/// Runs the whole recovery-index suite against PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// Inherited from another assembly, unmodified, exactly as the journal and lease suites above
/// are and exactly as <c>docs/17-Plugin-System.md §5</c> describes a third party claiming
/// conformance. Nothing in <c>tests/FlowX.Conformance.Tests</c> was shaped around this adapter.
/// </para>
/// <para>
/// <strong>This is what closes ADR-0016 decision 4's first gap.</strong> Which states count as
/// abandoned — and in particular that <c>Suspended</c> does not — was agreed between this
/// adapter and the reference index by reading, in two comments and no assertion. Both now
/// answer the same suite, so a disagreement is a red test rather than a difference nobody
/// notices until one deployment sweeps a parked instance and another does not.
/// </para>
/// <para>
/// The three-way skip behaviour is <see cref="PostgresTestSchema"/>'s and is inherited rather
/// than re-implemented: no connection string is a skip carrying a reason, a connection string
/// with no server behind it is a failure.
/// </para>
/// </remarks>
public sealed class PostgresRecoveryIndexConformanceTests : RecoveryIndexConformance, IAsyncLifetime
{
    private readonly List<PostgresTestSchema> _schemas = [];

    /// <inheritdoc />
    protected override async ValueTask<RecoveryStore> CreateStoreAsync()
    {
        var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        _schemas.Add(schema);

        return new PostgresRecoveryStore(schema);
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var schema in _schemas)
        {
            await schema.DisposeAsync();
        }

        _schemas.Clear();
    }

    /// <summary>The adapter under test, and the schema's own writes to arrange it.</summary>
    private sealed class PostgresRecoveryStore : RecoveryStore
    {
        private readonly PostgresTestSchema _schema;

        public PostgresRecoveryStore(PostgresTestSchema schema) => _schema = schema;

        /// <inheritdoc />
        public override IRecoveryIndex Index => _schema.RecoveryIndex;

        /// <inheritdoc />
        public override ValueTask<Guid> AbandonAsync(
            FlowInstanceState state,
            TimeSpan idleFor,
            string? tenantId,
            CancellationToken cancellationToken) =>
            _schema.AbandonAsync(state, idleFor, tenantId, cancellationToken);
    }
}

/// <summary>Runs the whole lease suite against PostgreSQL.</summary>
/// <remarks>
/// The expiry assertions wait out a real TTL against the database's own clock, which is the
/// only clock this store consults. A fake clock would have made them cheaper and would have
/// stopped them being about expiry.
/// </remarks>
public sealed class PostgresLeaseStoreConformanceTests : LeaseStoreConformance, IAsyncLifetime
{
    private readonly List<PostgresTestSchema> _schemas = [];

    /// <summary>
    /// A longer TTL than the in-memory suite's, because expiry here is a round trip.
    /// </summary>
    /// <remarks>
    /// The suite provides this hook for "a store with coarser granularity", and a store
    /// whose clock is <c>now()</c> on another process is one. 200 ms leaves the window
    /// between issuing a lease and waiting it out competing with the connection setup that
    /// precedes it; 750 ms does not. Nothing about the assertions changes — the suite still
    /// waits for the lease's own <c>ExpiresAt</c> and still refuses a lease that never
    /// expires.
    /// </remarks>
    protected override TimeSpan LeaseTtl => TimeSpan.FromMilliseconds(750);

    /// <inheritdoc />
    protected override async ValueTask<ILeaseStore> CreateStoreAsync()
    {
        var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        _schemas.Add(schema);

        return schema.Leases;
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var schema in _schemas)
        {
            await schema.DisposeAsync();
        }

        _schemas.Clear();
    }
}

/// <summary>
/// Runs the whole result-cache suite against PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// The second of the two implementations <c>ResultCacheConformance</c> is held to, and the
/// suite is inherited unmodified — the deliverable of a plugin package as much as the adapter
/// is, and the same claim <c>PostgresLeaseStoreConformanceTests</c> makes about
/// <c>ILeaseStore</c>. This one and <c>RedisResultCacheConformanceTests</c> disagree about
/// almost everything internally: Redis expires a key and this compares an instant against
/// <c>now()</c>. Passing the same assertions is what makes stage 5's seam a contract rather
/// than a description of whichever store was written first.
/// </para>
/// <para>
/// Each test gets its own schema, and each schema is dropped when the class finishes — so
/// "a fresh, empty cache, called once per test" is satisfied without a <c>TRUNCATE</c>
/// anywhere.
/// </para>
/// </remarks>
public sealed class PostgresResultCacheConformanceTests : ResultCacheConformance, IAsyncLifetime
{
    private readonly List<PostgresTestSchema> _schemas = [];

    /// <summary>
    /// A longer TTL than the suite's default, for <c>PostgresLeaseStoreConformanceTests</c>'s
    /// reason.
    /// </summary>
    /// <remarks>
    /// Expiry here is <c>now()</c> on another process reached over a connection this test has
    /// to open, so a 300 ms window competes with the round trip that precedes it. 750 ms does
    /// not. Nothing about the assertions changes: the suite still waits the TTL out and still
    /// refuses a store whose entries never lapse.
    /// </remarks>
    protected override TimeSpan ShortTtl => TimeSpan.FromMilliseconds(750);

    /// <inheritdoc />
    protected override async ValueTask<IResultCache> CreateCacheAsync()
    {
        var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        _schemas.Add(schema);

        return new PostgresResultCache(schema.DataSource);
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var schema in _schemas)
        {
            await schema.DisposeAsync();
        }

        _schemas.Clear();
    }
}

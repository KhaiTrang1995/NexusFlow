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

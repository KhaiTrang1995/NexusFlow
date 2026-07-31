using FlowX.Conformance;
using Xunit;

namespace FlowX.Redis.Tests;

/// <summary>Runs the whole lease suite against Redis.</summary>
/// <remarks>
/// <para>
/// <strong>This class is the package's deliverable as much as the adapter is.</strong>
/// <c>LeaseStoreConformance</c> is inherited from another assembly, unmodified, exactly as
/// <c>PostgresLeaseStoreConformanceTests</c> inherits it and exactly as
/// <c>docs/17-Plugin-System.md §5</c> describes a third party claiming conformance. Until this
/// project existed the suite had been derived once, which shows that deriving works and shows
/// nothing about whether the assertions are store-independent — a suite with one
/// implementation is a suite shaped like that implementation, and nobody can tell. ADR-0006's
/// claim is that exactly-one-writer is a property of two primitives rather than of any
/// particular database, and this is the first evidence either way.
/// </para>
/// <para>
/// Nothing in <c>tests/FlowX.Conformance.Tests</c> was changed to make this pass. That is the
/// result the package was commissioned to produce: had the suite needed an edit to accept
/// Redis, the suite would have been written against PostgreSQL and WP-51 would have failed.
/// </para>
/// <para>
/// The expiry assertions wait out a real TTL against Redis's own clock, which is the only
/// clock this store consults — every decision it makes is taken inside a Lua script against
/// <c>TIME</c> on the server. A fake clock would have made them cheaper and would have stopped
/// them being about expiry.
/// </para>
/// </remarks>
public sealed class RedisLeaseStoreConformanceTests : LeaseStoreConformance, IAsyncLifetime
{
    private readonly List<RedisTestKeySpace> _keySpaces = [];

    /// <inheritdoc />
    protected override async ValueTask<ILeaseStore> CreateStoreAsync()
    {
        var keySpace = await RedisTestKeySpace.CreateAsync(Cancellation);

        _keySpaces.Add(keySpace);

        return keySpace.Leases;
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var keySpace in _keySpaces)
        {
            await keySpace.DisposeAsync();
        }

        _keySpaces.Clear();
    }
}

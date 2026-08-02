using FlowX.Conformance;
using Npgsql;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>Runs the whole rate-limiter suite against PostgreSQL.</summary>
/// <remarks>
/// <para>
/// <strong>This is the second implementation the suite was written for.</strong> A suite with
/// one implementation is a suite shaped like that implementation and nobody can tell from
/// inside it — <c>LeaseStoreConformance</c>'s remarks make the argument, and this file and
/// <c>RedisRateLimiterConformanceTests</c> are the evidence for the limiter. The two stores
/// share no arithmetic: one refills in Lua against <c>TIME</c>, the other in SQL against
/// <c>now()</c>, and nothing in <c>tests/FlowX.Conformance.Tests</c> was changed to accept
/// either.
/// </para>
/// <para>
/// <strong>Two data sources, not two stores over one.</strong> The suite's shared-budget
/// assertion is about what two <em>nodes</em> see, so two clients that shared a connection pool
/// would make it pass vacuously.
/// </para>
/// </remarks>
public sealed class PostgresRateLimiterConformanceTests : RateLimiterConformance, IAsyncLifetime
{
    private readonly List<RateLimiterUnderTest> _harnesses = [];

    /// <inheritdoc />
    protected override async ValueTask<RateLimiterUnderTest> CreateAsync()
    {
        var harness = new PostgresRateLimiterHarness(await PostgresPolicySchema.CreateAsync(Cancellation));

        _harnesses.Add(harness);

        return harness;
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var harness in _harnesses)
        {
            await harness.DisposeAsync();
        }

        _harnesses.Clear();
    }
}

/// <summary>Runs the whole idempotency suite against PostgreSQL.</summary>
public sealed class PostgresIdempotencyConformanceTests : IdempotencyStoreConformance, IAsyncLifetime
{
    private readonly List<IdempotencyStoreUnderTest> _harnesses = [];

    /// <inheritdoc />
    protected override async ValueTask<IdempotencyStoreUnderTest> CreateAsync()
    {
        var harness = new PostgresIdempotencyHarness(await PostgresPolicySchema.CreateAsync(Cancellation));

        _harnesses.Add(harness);

        return harness;
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var harness in _harnesses)
        {
            await harness.DisposeAsync();
        }

        _harnesses.Clear();
    }
}

/// <summary>The rate limiter's two clients over one schema.</summary>
internal sealed class PostgresRateLimiterHarness(PostgresPolicySchema schema) : RateLimiterUnderTest
{
    /// <inheritdoc />
    public override IRateLimiterStore Limiter { get; } = new PostgresRateLimiterStore(schema.First);

    /// <inheritdoc />
    public override IRateLimiterStore SecondClient { get; } = new PostgresRateLimiterStore(schema.Second);

    /// <inheritdoc />
    public override ValueTask<IRateLimiterStore> UnreachableAsync(CancellationToken cancellationToken) =>
        new(new PostgresRateLimiterStore(schema.Unreachable));

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await schema.DisposeAsync();

        await base.DisposeAsync();
    }
}

/// <summary>The idempotency store's two clients over one schema.</summary>
internal sealed class PostgresIdempotencyHarness(PostgresPolicySchema schema) : IdempotencyStoreUnderTest
{
    /// <inheritdoc />
    public override IIdempotencyStore Store { get; } = new PostgresIdempotencyStore(schema.First);

    /// <inheritdoc />
    public override IIdempotencyStore SecondClient { get; } = new PostgresIdempotencyStore(schema.Second);

    /// <inheritdoc />
    public override ValueTask<IIdempotencyStore> UnreachableAsync(CancellationToken cancellationToken) =>
        new(new PostgresIdempotencyStore(schema.Unreachable));

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await schema.DisposeAsync();

        await base.DisposeAsync();
    }
}

/// <summary>
/// One private schema, migrated, with two independent data sources over it and a third pointed
/// at nothing.
/// </summary>
/// <remarks>
/// A schema per harness rather than a shared one, which is <c>PostgresTestSchema</c>'s decision
/// and its reason: the conformance suites require "a fresh, empty store, called once per test",
/// and a schema is the isolation PostgreSQL offers for free.
/// </remarks>
internal sealed class PostgresPolicySchema : IAsyncDisposable
{
    private readonly PostgresJournalOptions _options;
    private bool _disposed;

    private PostgresPolicySchema(
        PostgresJournalOptions options,
        NpgsqlDataSource first,
        NpgsqlDataSource second,
        NpgsqlDataSource unreachable)
    {
        _options = options;
        First = first;
        Second = second;
        Unreachable = unreachable;
    }

    /// <summary>The first client's data source.</summary>
    public NpgsqlDataSource First { get; }

    /// <summary>
    /// A second client's data source, sharing nothing with <see cref="First"/> but the server.
    /// </summary>
    /// <remarks>
    /// Its own pool, its own connections. Two stores over one <see cref="NpgsqlDataSource"/>
    /// would share a pool, which is close enough to one client that the suite's shared-budget
    /// assertion would stop meaning what it says.
    /// </remarks>
    public NpgsqlDataSource Second { get; }

    /// <summary>A data source pointed at a port nothing is listening on.</summary>
    public NpgsqlDataSource Unreachable { get; }

    /// <summary>Creates and migrates a schema, or refuses to pretend it did.</summary>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <returns>The prepared schema.</returns>
    /// <exception cref="InvalidOperationException">
    /// A database was promised by the environment and is not reachable.
    /// </exception>
    public static async ValueTask<PostgresPolicySchema> CreateAsync(CancellationToken cancellationToken)
    {
        if (!PostgresTestDatabase.IsAvailable)
        {
            if (PostgresTestDatabase.IsPromised)
            {
                throw PostgresTestDatabase.Unreachable();
            }

            Assert.Skip(PostgresTestDatabase.Reason);
        }

        var options = new PostgresJournalOptions { Schema = "flowx_t_" + Guid.NewGuid().ToString("n") };

        var first = ServiceCollectionExtensions.BuildDataSource(PostgresTestDatabase.ConnectionString!, options);
        var second = ServiceCollectionExtensions.BuildDataSource(PostgresTestDatabase.ConnectionString!, options);

        var unreachable = ServiceCollectionExtensions.BuildDataSource(
            "Host=127.0.0.1;Port=1;Database=postgres;Username=postgres;Timeout=1;Command Timeout=1",
            options);

        await new PostgresMigrator(first, options)
            .MigrateAsync(PostgresMigrator.TargetVersion, cancellationToken)
            .ConfigureAwait(false);

        return new PostgresPolicySchema(options, first, second, unreachable);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Idempotent, because it is called twice on purpose: each suite disposes the harness it was
    /// handed at the end of its own test, and the derivation disposes the whole list again when
    /// the class finishes — which is the belt-and-braces shape the Redis derivations use, and a
    /// pooled data source is less forgiving of a second close than a multiplexer is.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var connection = await First.OpenConnectionAsync().ConfigureAwait(false);

        await using (connection.ConfigureAwait(false))
        {
            using var command = connection.CreateCommand();

            command.CommandText = $"DROP SCHEMA IF EXISTS \"{_options.Schema}\" CASCADE";

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await First.DisposeAsync().ConfigureAwait(false);
        await Second.DisposeAsync().ConfigureAwait(false);
        await Unreachable.DisposeAsync().ConfigureAwait(false);
    }
}

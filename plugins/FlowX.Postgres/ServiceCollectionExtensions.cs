using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FlowX.Postgres;

/// <summary>
/// Registers the PostgreSQL journal and lease store.
/// </summary>
/// <remarks>
/// The registration deliberately does <em>not</em> migrate. Applying DDL as a side effect of
/// building a container means every replica of a rolling update races to migrate at the
/// moment it starts, and a failure surfaces as a container that will not start rather than
/// as a deployment step that did not pass. Call
/// <see cref="PostgresMigrator.MigrateAsync(CancellationToken)"/> from wherever your
/// deployment runs schema changes; it is safe to call from every replica,
/// but it should be a decision rather than a consequence.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IFlowJournal"/>, <see cref="ILeaseStore"/> and
    /// <see cref="IRecoveryIndex"/> over one data source.
    /// </summary>
    /// <param name="services">The container being built.</param>
    /// <param name="connectionString">How to reach PostgreSQL.</param>
    /// <param name="options">Where the tables live. Defaults to the <c>flowx</c> schema.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <remarks>
    /// <strong>The three registrations are three service types, not one store registered
    /// three times.</strong> <c>FlowXServiceCollectionExtensions</c> matches on the service
    /// type and resolves <see cref="IRecoveryIndex"/> as optional, so a store that is not
    /// registered under it produces a host that runs durable flows and never sweeps — which
    /// is a supported configuration and, before this, the only one PostgreSQL offered.
    /// <see cref="PostgresJournalOptions.RegisterRecoveryIndex"/> is where a deployment
    /// chooses that deliberately.
    /// </remarks>
    public static IServiceCollection AddFlowXPostgres(
        this IServiceCollection services,
        string connectionString,
        PostgresJournalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var settings = options ?? new PostgresJournalOptions();

        services.AddSingleton(settings);
        services.AddSingleton(_ => BuildDataSource(connectionString, settings));
        services.AddSingleton<IFlowJournal>(
            provider => new PostgresFlowJournal(provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<ILeaseStore>(
            provider => new PostgresLeaseStore(provider.GetRequiredService<NpgsqlDataSource>()));

        if (settings.RegisterRecoveryIndex)
        {
            services.AddSingleton<IRecoveryIndex>(
                provider => new PostgresRecoveryIndex(provider.GetRequiredService<NpgsqlDataSource>()));
        }

        if (settings.RegisterTimerIndex)
        {
            services.AddSingleton<ITimerIndex>(
                provider => new PostgresTimerIndex(provider.GetRequiredService<NpgsqlDataSource>()));
        }

        services.AddSingleton(
            provider => new PostgresRetention(provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton(provider => new PostgresMigrator(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<PostgresJournalOptions>()));

        return services;
    }

    /// <summary>
    /// Registers <see cref="IRateLimiterStore"/> and <see cref="IIdempotencyStore"/> over the
    /// data source <see cref="AddFlowXPostgres"/> built.
    /// </summary>
    /// <param name="services">The container being built.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <strong>Separate from <see cref="AddFlowXPostgres"/> for
    /// <see cref="AddFlowXPostgresOutbox"/>'s reason</strong>, and one more that is specific to
    /// these two: a deployment that keeps its journal in PostgreSQL very often wants its rate
    /// limits in Redis, because a bucket is a hot small write and a journal is not. Folding the
    /// registration into the journal's would make that arrangement need an override rather than
    /// a choice.
    /// </para>
    /// <para>
    /// <strong>Requires migration 6.</strong> Both stores read tables <c>0006_policy_stores.sql</c>
    /// creates, and a host that registers them against an unmigrated schema gets a refusal
    /// naming the migration rather than a silent admission — which is the direction
    /// <see cref="IRateLimiterStore"/>'s contract requires.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddFlowXPostgresPolicyStores(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IRateLimiterStore>(
            provider => new PostgresRateLimiterStore(provider.GetRequiredService<NpgsqlDataSource>()));

        services.AddSingleton<IIdempotencyStore>(
            provider => new PostgresIdempotencyStore(provider.GetRequiredService<NpgsqlDataSource>()));

        return services;
    }

    /// <summary>
    /// Registers <see cref="PostgresOutboxPublisher"/> over the data source
    /// <see cref="AddFlowXPostgres"/> built.
    /// </summary>
    /// <param name="services">The container being built.</param>
    /// <param name="options">Batch size and poll interval.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <strong>Separate from <see cref="AddFlowXPostgres"/> on purpose, and not defaulted
    /// on.</strong> The publisher needs an <see cref="IEventPublisher"/>, and this repository
    /// ships none — there is no broker plugin (<c>docs/17-Plugin-System.md §2</c>). Folding
    /// the registration into the journal's would make a host that wires PostgreSQL fail to
    /// resolve a service it never asked for, or — worse, and the mistake
    /// <see cref="PostgresJournalOptions.RegisterRecoveryIndex"/> exists because of — leave a
    /// deployment believing it publishes while nothing does.
    /// </para>
    /// <para>
    /// <strong>Registering it does not start it.</strong> This produces the publisher; a host
    /// runs <see cref="PostgresOutboxPublisher.RunAsync"/> on whatever it uses for
    /// long-running work. Starting a polling loop as a side effect of building a container is
    /// the same mistake as migrating from one, and the remarks on this class say why.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddFlowXPostgresOutbox(
        this IServiceCollection services,
        PostgresOutboxOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var settings = options ?? new PostgresOutboxOptions();

        services.AddSingleton(settings);
        services.AddSingleton(provider => new PostgresOutboxPublisher(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<IEventPublisher>(),
            provider.GetRequiredService<PostgresOutboxOptions>()));

        return services;
    }

    /// <summary>
    /// Registers <see cref="PostgresChangeFeed"/> over the data source
    /// <see cref="AddFlowXPostgres"/> built, so a <c>[ChangeTrigger]</c> flow is observed.
    /// </summary>
    /// <param name="services">The container being built.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <strong>Separate from <see cref="AddFlowXPostgres"/> for
    /// <see cref="AddFlowXPostgresOutbox"/>'s reason</strong>, inverted: this one needs nothing
    /// the journal does not already have, and registering it by default would still be wrong.
    /// <c>FlowChangeScan</c> resolves <see cref="IChangeFeed"/> optionally, so wiring one turns
    /// the change loop on for the whole host — and a deployment that runs PostgreSQL as a journal
    /// and observes nothing should not acquire a background loop by upgrading.
    /// </para>
    /// <para>
    /// <strong>It coexists with <see cref="AddFlowXPostgresOutbox"/> and does not replace
    /// it.</strong> The feed reads <c>outbox_event</c> without writing it, so a host can drain
    /// the outbox to a broker and observe the same rows from a change subscription at once
    /// (<c>docs/adr/ADR-0050-a-change-trigger-observes-the-outbox.md</c>).
    /// </para>
    /// </remarks>
    public static IServiceCollection AddFlowXPostgresChangeFeed(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IChangeFeed>(
            provider => new PostgresChangeFeed(provider.GetRequiredService<NpgsqlDataSource>()));

        return services;
    }

    /// <summary>
    /// Builds a data source whose connections already resolve to the configured schema.
    /// </summary>
    /// <param name="connectionString">How to reach PostgreSQL.</param>
    /// <param name="options">Where the tables live.</param>
    /// <returns>A data source. The caller owns it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <remarks>
    /// The schema is set on the connection rather than written into every statement, which
    /// is what lets <see cref="JournalSql"/> be a file of constants: there is no statement
    /// assembled from a configured value, so there is nothing for a value to be injected
    /// into.
    /// </remarks>
    public static NpgsqlDataSource BuildDataSource(
        string connectionString,
        PostgresJournalOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);

        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = options.Schema,
        };

        return new NpgsqlDataSourceBuilder(settings.ConnectionString).Build();
    }
}

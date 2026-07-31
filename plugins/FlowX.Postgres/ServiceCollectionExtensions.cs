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
    /// Registers <see cref="IFlowJournal"/> and <see cref="ILeaseStore"/> over one data source.
    /// </summary>
    /// <param name="services">The container being built.</param>
    /// <param name="connectionString">How to reach PostgreSQL.</param>
    /// <param name="options">Where the tables live. Defaults to the <c>flowx</c> schema.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
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
        services.AddSingleton(
            provider => new PostgresRetention(provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton(provider => new PostgresMigrator(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<PostgresJournalOptions>()));

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

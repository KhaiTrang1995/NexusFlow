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

        // Schema-per-tenant changes three registrations here and one in AddFlowXPostgresOutbox:
        // the journal gains the pools it hands out, and every loop that is node-wide rather than
        // per-call — both sweeps, and the publisher — becomes a fan-out. The lease store is not
        // among them — a lease is taken before the instance row exists and holds no tenant data
        // (ADR-0046 §2.6), so it stays in the control schema, node-wide and shared, which is
        // also the only place a lease on an instance whose tenant is not yet known could live.
        if (settings.TenantSchemas.IsEnabled)
        {
            services.AddSingleton(provider => new PostgresTenantStores(
                provider.GetRequiredService<NpgsqlDataSource>(), connectionString, settings));

            services.AddSingleton<IFlowJournal>(provider => new PostgresFlowJournal(
                provider.GetRequiredService<NpgsqlDataSource>(),
                provider.GetRequiredService<PostgresTenantStores>()));
        }
        else
        {
            services.AddSingleton<IFlowJournal>(
                provider => new PostgresFlowJournal(provider.GetRequiredService<NpgsqlDataSource>()));
        }

        services.AddSingleton<ILeaseStore>(
            provider => new PostgresLeaseStore(provider.GetRequiredService<NpgsqlDataSource>()));

        if (settings.RegisterRecoveryIndex)
        {
            services.AddSingleton<IRecoveryIndex>(provider => settings.TenantSchemas.IsEnabled
                ? new PostgresTenantRecoveryIndex(provider.GetRequiredService<PostgresTenantStores>())
                : new PostgresRecoveryIndex(provider.GetRequiredService<NpgsqlDataSource>()));
        }

        if (settings.RegisterTimerIndex)
        {
            services.AddSingleton<ITimerIndex>(provider => settings.TenantSchemas.IsEnabled
                ? new PostgresTenantTimerIndex(provider.GetRequiredService<PostgresTenantStores>())
                : new PostgresTimerIndex(provider.GetRequiredService<NpgsqlDataSource>()));
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
    /// <para>
    /// <strong>At <see cref="TenantIsolation.Schema"/> it drains every tenant's outbox rather
    /// than the control schema's empty one.</strong> The batch size becomes each tenant's own,
    /// and the rest of the guarantee is unchanged because a claim was always confined to one
    /// table — see <see cref="PostgresOutboxPublisher"/>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddFlowXPostgresOutbox(
        this IServiceCollection services,
        PostgresOutboxOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var settings = options ?? new PostgresOutboxOptions();

        services.AddSingleton(settings);

        // Decided in the factory rather than here, because this method may be called before
        // AddFlowXPostgres and the level lives on that call's options.
        services.AddSingleton(provider =>
        {
            var broker = provider.GetRequiredService<IEventPublisher>();
            var batch = provider.GetRequiredService<PostgresOutboxOptions>();

            return provider.GetRequiredService<PostgresJournalOptions>().TenantSchemas.IsEnabled
                ? new PostgresOutboxPublisher(
                    provider.GetRequiredService<PostgresTenantStores>(), broker, batch)
                : new PostgresOutboxPublisher(
                    provider.GetRequiredService<NpgsqlDataSource>(), broker, batch);
        });

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

        services.AddSingleton<IChangeFeed>(provider => new PostgresChangeFeed(
            RequiresOneSchema(provider, nameof(AddFlowXPostgresChangeFeed))));

        return services;
    }

    /// <summary>
    /// Returns the control data source, or refuses a feed that would observe an empty table.
    /// </summary>
    /// <param name="provider">Where the options and the data source come from.</param>
    /// <param name="registration">Which extension method is being refused.</param>
    /// <returns>The control schema's data source.</returns>
    /// <exception cref="InvalidOperationException">
    /// Each tenant has a schema of its own, so the control schema's table is empty.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <strong>The cursor is not what stops this fanning out.</strong> ADR-0051 §4 refused the
    /// feed and the publisher together, on the grounds that both claim rows and advance a
    /// position. The publisher now fans out — that reason did not survive contact with it — and
    /// the cursor would too: <c>change_cursor</c> is keyed by subscription, so one row per
    /// tenant schema is the same key in a different table, and the visibility barrier is over
    /// transaction ids, which are <em>cluster-wide</em> rather than per schema: the predicate
    /// therefore means the same thing in every tenant's schema and cannot skip a row. It is
    /// conservative in one direction only — one tenant holding a write transaction open holds
    /// every tenant's barrier down, which costs latency and never correctness, and which no
    /// fan-out could repair because the counter was never the schema's.
    /// </para>
    /// <para>
    /// <strong>What stops it is delivery, and it is one level up.</strong> A change read from
    /// tenant <em>A</em>'s schema has to start a flow <em>in</em> tenant A, and
    /// <c>FlowChangeScan</c> starts one with no principal — so <c>ClaimTenantResolver</c>
    /// refuses any invocation naming a tenant, at <see cref="TenantIsolation.Row"/> as much as
    /// here. The one path that carries a tenant without claims is
    /// <c>FlowInvocation.IsContinuation</c>, and that also skips step authorisation: correct for
    /// a sweep resuming an instance already admitted, wrong for a start. A fanned-out feed would
    /// therefore hand the host changes it refuses one at a time, and <c>FlowChangeScan</c>
    /// counts <c>tenant.required</c> as progress — so the cursor would advance past every change
    /// that never ran. Losing them quietly is worse than the empty table, which is why this is
    /// still a refusal and why the refusal now names the decision that is actually missing.
    /// </para>
    /// </remarks>
    private static NpgsqlDataSource RequiresOneSchema(
        IServiceProvider provider,
        string registration)
    {
        if (provider.GetRequiredService<PostgresJournalOptions>().TenantSchemas.IsEnabled)
        {
            throw new InvalidOperationException(
                $"{registration} reads outbox_event in the control schema, and this deployment " +
                $"gives every tenant a schema of its own ({nameof(PostgresJournalOptions)}." +
                $"{nameof(PostgresJournalOptions.TenantSchemas)}), so that table is empty and " +
                "always will be. It is not the cursor that stops this being fanned out — " +
                "change_cursor is keyed by subscription, and one per tenant schema is the same " +
                "key in a different table. It is delivery: a change observed in a tenant's " +
                "schema must start a flow in that tenant, a change scan carries no principal, " +
                "and the only invocation that carries a tenant without claims is a continuation " +
                "— which also skips step authorisation, so a start must not use it. A fanned-out " +
                "feed would offer changes the host refuses with tenant.required, which a change " +
                "scan counts as progress, and the cursor would move past every one of them. " +
                "AddFlowXPostgresOutbox does fan out at this level and is unaffected.");
        }

        return provider.GetRequiredService<NpgsqlDataSource>();
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

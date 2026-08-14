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

            // The tenant set a PerTenant schedule fans out over. Registered here because at this
            // level the deployment already has an authoritative list — tenant_schema — and
            // re-declaring it in FlowXOptions.Tenants would be a second copy that goes stale the
            // moment a tenant is provisioned at run time. AddFlowX registers the declared list
            // with TryAdd, so this one wins whichever order the two calls are made in.
            services.AddSingleton<ITenantDirectory>(provider => new PostgresTenantDirectory(
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

        // Erasure, registered beside retention because they are the same obligation asked two
        // ways: retention answers "how long may this be kept", erasure answers "this one, now".
        // It takes the per-tenant pools at Schema isolation for the reason the sweeps do — at
        // that level a subject's rows are in that tenant's schema, and an erasure that searched
        // the control schema would find nothing and report a complete erasure of it.
        services.AddSingleton<ISubjectErasure>(provider => new PostgresSubjectErasure(
            provider.GetRequiredService<NpgsqlDataSource>(),
            settings.TenantSchemas.IsEnabled
                ? provider.GetRequiredService<PostgresTenantStores>()
                : null,
            provider.GetService<RetentionConsumers>()));

        services.AddSingleton(provider => new PostgresRetention(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetService<RetentionConsumers>()));
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
    /// on.</strong> The publisher needs an <see cref="IEventPublisher"/>, which a host chooses
    /// and wires — <c>FlowX.Redis</c>'s <c>RedisStreamEventPublisher</c> is one, and a
    /// deployment on another broker supplies its own. Folding
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
    /// Tells <see cref="PostgresRetention"/> what reads this deployment's outbox.
    /// </summary>
    /// <param name="services">The container being built.</param>
    /// <param name="consumers">The publisher and the change subscriptions, if any.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// <para>
    /// <strong>Not inferred from the registrations above, because they are per node and this is
    /// per deployment.</strong> <see cref="AddFlowXPostgresOutbox"/> says this process publishes;
    /// it does not say no other process does, and a node that inferred "no publisher" from its
    /// own container would purge events another node's publisher owed a broker. The same is true
    /// of subscriptions, which is why the set is stated here rather than read from the host's
    /// change catalogue.
    /// </para>
    /// <para>
    /// <strong>Declining to call this is a decision with a default</strong> —
    /// <see cref="RetentionConsumers.Default"/>, one publisher and no subscriptions, which holds
    /// unpublished rows for ever rather than discarding them. A host with change subscriptions
    /// and no broker is exactly the deployment that has to call it.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddFlowXPostgresRetentionConsumers(
        this IServiceCollection services,
        RetentionConsumers consumers)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(consumers);

        services.AddSingleton(consumers);

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
    /// <para>
    /// <strong>It no longer refuses schema-per-tenant.</strong> ADR-0053 §2 refused it because
    /// the cursor was never the obstacle and delivery was: a change read from a tenant's schema
    /// had to start a flow in that tenant, and the only invocation carrying a tenant without
    /// claims was a continuation, which skips step authorisation. That gap is closed —
    /// <c>FlowInvocation.TenantAttested</c> carries the tenant a change was read <em>from</em>
    /// and grants no bypass — so the feed fans out over <c>PostgresTenantStores</c> here for the
    /// reason <see cref="AddFlowXPostgresOutbox"/> already does.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddFlowXPostgresChangeFeed(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IChangeFeed>(provider =>
            provider.GetRequiredService<PostgresJournalOptions>().TenantSchemas.IsEnabled
                ? new PostgresChangeFeed(provider.GetRequiredService<PostgresTenantStores>())
                : new PostgresChangeFeed(provider.GetRequiredService<NpgsqlDataSource>()));

        return services;
    }

    /// <summary>
    /// Registers <see cref="PostgresSweepSignal"/>, so this node's change and timer sweeps are
    /// woken by the database instead of waiting out their intervals.
    /// </summary>
    /// <param name="services">The container being built.</param>
    /// <param name="directConnectionString">
    /// How to reach PostgreSQL directly, bypassing any connection pooler.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="directConnectionString"/> is empty.</exception>
    /// <remarks>
    /// <para>
    /// <strong>It takes a second connection string, and that is the whole of the decision this
    /// method asks a deployment to make.</strong> <c>LISTEN</c> is a property of a session: the
    /// connection that issued it is the one notifications are delivered to, and it has to stay
    /// open. A transaction pooler cannot carry that — PgBouncer in transaction mode hands the
    /// server connection to the next client at the end of every transaction, so the subscription
    /// either travels to a session nobody is reading or is discarded, and neither failure says
    /// anything. It is the same shape as
    /// <see cref="PostgresJournalOptions.SetSearchPathOnConnection"/>'s: the pooled endpoint
    /// accepts everything and then does not do it.
    /// </para>
    /// <para>
    /// <strong>A deployment that has only a pooled endpoint does not call this</strong>, and that
    /// is a supported configuration rather than a degraded one — it is exactly what every release
    /// before this one did. The sweeps keep their intervals, the change feed keeps its cursor and
    /// the timer sweep keeps its query; what is lost is the acceleration, which is latency and
    /// never an event. Passing a pooled connection string here would be the mistake: the host
    /// would start, the listener would look connected, and the notifications would go nowhere.
    /// </para>
    /// <para>
    /// <strong>Requires migration 13</strong>, which is what announces on the two channels
    /// <see cref="PostgresSweepSignal"/> subscribes to. Registering it against an older schema is
    /// not an error and cannot be one — a listener with nothing announcing to it is a listener
    /// that hears nothing, which is the same state a dropped connection puts it in, and the
    /// intervals carry the deployment either way.
    /// </para>
    /// <para>
    /// <strong>At <see cref="TenantIsolation.Schema"/> it covers every tenant.</strong> Each
    /// tenant schema gets its own copy of the triggers when it is migrated, and they announce
    /// under their own schema name; the listener accepts anything under
    /// <see cref="TenantSchemaOptions.Prefix"/> as well as the control schema, because at that
    /// level both sweeps fan out over the tenants and a wake for any of them is a wake for the
    /// pass.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddFlowXPostgresSweepSignal(
        this IServiceCollection services,
        string directConnectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(directConnectionString);

        services.AddSingleton<ISweepSignal>(provider => new PostgresSweepSignal(
            directConnectionString,
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

        var settings = new NpgsqlConnectionStringBuilder(connectionString);

        // Left alone when the deployment says it supplies the schema itself. A startup
        // parameter is the shortest correct route on a direct connection and the one thing a
        // transaction pooler cannot carry -- PostgresJournalOptions.SetSearchPathOnConnection
        // records the two ways PgBouncer fails with it, both reproduced.
        if (options.SetSearchPathOnConnection)
        {
            settings.SearchPath = options.Schema;
        }

        return new NpgsqlDataSourceBuilder(settings.ConnectionString).Build();
    }
}

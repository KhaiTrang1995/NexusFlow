namespace FlowX.Postgres;

/// <summary>
/// Where the journal's tables live and how the adapter is allowed to reach them.
/// </summary>
/// <remarks>
/// The schema is a setting rather than a constant because a journal frequently shares a
/// database with the application it is journaling, and "which schema" is the only question
/// an operator has to answer to keep them apart. It is also what lets a test give every
/// case its own empty store without a second server.
/// </remarks>
public sealed record PostgresJournalOptions
{
    /// <summary>The default schema, used when nothing says otherwise.</summary>
    public const string DefaultSchema = "flowx";

    private readonly string _schema = DefaultSchema;

    /// <summary>
    /// The schema holding <c>flow_instance</c>, <c>flow_step</c>, <c>outbox_event</c>,
    /// <c>flow_lease</c> and <c>retention_policy</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The value is not a bare lower-case SQL identifier.
    /// </exception>
    /// <remarks>
    /// Validated on the way in rather than escaped on the way out. A schema name reaches
    /// SQL as an identifier, and an identifier cannot be a parameter — so the only two
    /// honest options are to reject anything that is not obviously safe, or to build SQL
    /// by concatenation and hope. This adapter does both halves of the first: the value is
    /// constrained here, and <see cref="PostgresMigrator"/> still passes it to the server
    /// as a parameter and lets <c>format('%I', …)</c> do the quoting.
    /// </remarks>
    public string Schema
    {
        get => _schema;
        init => _schema = Identifiers.RequireSchemaName(value);
    }

    /// <summary>
    /// Whether <see cref="PostgresMigrator"/> may create the schema if it is absent.
    /// </summary>
    /// <remarks>
    /// On by default so that a first run works, and switchable so that a deployment whose
    /// database roles forbid DDL at runtime can migrate out of band and still start.
    /// </remarks>
    public bool CreateSchemaIfMissing { get; init; } = true;

    /// <summary>
    /// Whether <see cref="PostgresRecoveryIndex"/> is registered, and with it whether a host
    /// on this store sweeps for instances a dead node left behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>On by default, because off is what a Postgres host used to be without
    /// choosing it.</strong> Until this adapter carried an <see cref="IRecoveryIndex"/>, a
    /// host wired to PostgreSQL fenced correctly, journaled correctly, and never picked up a
    /// dead node's work — silently, because <c>FlowXServiceCollectionExtensions</c> resolves
    /// the index as an optional service and a store that does not implement it is the
    /// supported way to opt out. Defaulting this to false would preserve that state and keep
    /// the surprise.
    /// </para>
    /// <para>
    /// <strong>And switchable, because the opt-out has to stay reachable.</strong>
    /// <c>FlowXOptions</c> says so in as many words — zero concurrent recoveries "is not
    /// 'recovery disabled' — leave the journal without an IRecoveryIndex for that" — so
    /// declining the registration is the documented switch, and a bundled extension method
    /// that always registered it would take the switch away from a single-node deployment
    /// that has reasonably decided not to sweep.
    /// </para>
    /// </remarks>
    public bool RegisterRecoveryIndex { get; init; } = true;

    /// <summary>
    /// Whether <see cref="PostgresTimerIndex"/> is registered as the host's
    /// <see cref="ITimerIndex"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>On by default, for the reason <see cref="RegisterRecoveryIndex"/> is.</strong>
    /// Off is the state a host is in when nothing registers one, and in that state a
    /// <c>.Delay(...)</c> never comes due and an <c>.OnTimeout(...)</c> never fires — the flow
    /// parks and waits for a signal or its own <c>[FlowDeadline]</c>. That is a defensible
    /// configuration to choose and a surprising one to inherit.
    /// </para>
    /// <para>
    /// Separate from <see cref="RegisterRecoveryIndex"/> rather than one switch over both,
    /// because they are two different sweeps over two disjoint sets of rows: a deployment may
    /// well want its parked instances woken on a node that does not take over other nodes'
    /// work, or the reverse.
    /// </para>
    /// </remarks>
    public bool RegisterTimerIndex { get; init; } = true;

    /// <summary>
    /// Whether each tenant's rows live in a schema of their own, and on what terms.
    /// </summary>
    /// <remarks>
    /// Off by default, which is <see cref="TenantIsolation.Row"/> or no tenancy at all. A
    /// deployment declaring <see cref="TenantIsolation.Schema"/> must turn this on, and a host
    /// refuses to start when it did not — <c>FlowDurability.IsolationEnforced</c> is what
    /// reports the mismatch, because a schema level served by a row store is precisely the
    /// silent downgrade the level exists to prevent.
    /// </remarks>
    public TenantSchemaOptions TenantSchemas { get; init; } = new();
}

/// <summary>
/// Where a tenant's own schema comes from, and what it is allowed to cost.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A separate record from <see cref="PostgresJournalOptions"/> because it is a
/// different decision with a different owner.</strong> Where the tables live is an operator's
/// answer to "which schema"; this is an answer to "how many", and it carries a pool bound and a
/// provisioning policy that only a deployment running schema-per-tenant has any opinion about.
/// </para>
/// </remarks>
public sealed record TenantSchemaOptions
{
    /// <summary>The default prefix every tenant schema starts with.</summary>
    public const string DefaultPrefix = "flowx_t_";

    private readonly string _prefix = DefaultPrefix;

    /// <summary>Whether a tenant's rows live in a schema of their own.</summary>
    public bool IsEnabled { get; init; }

    /// <summary>What every derived schema name begins with.</summary>
    /// <exception cref="ArgumentException">
    /// It is not a bare lower-case identifier, or it leaves no room for a fingerprint.
    /// </exception>
    /// <remarks>
    /// Configurable so that a journal can share a database with an application that has its own
    /// schemas and still be recognisable in <c>\dn</c>, and validated on the way in for
    /// <see cref="PostgresJournalOptions.Schema"/>'s reason: it reaches SQL as an identifier,
    /// and an identifier cannot be a parameter.
    /// </remarks>
    public string Prefix
    {
        get => _prefix;
        init => _prefix = TenantSchemaName.RequirePrefix(value);
    }

    /// <summary>
    /// How many connections one tenant may hold open, across this process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is <c>docs/16 §9</c>'s "connection pool shared across L2 tenants" answered
    /// rather than avoided.</strong> Each tenant gets its own pool, so a tenant that opens
    /// connections faster than it closes them exhausts a bound that belongs to it and leaves
    /// every other tenant's pool untouched — which is the entire reason the pools are separate
    /// rather than one pool with a per-tenant <c>search_path</c>.
    /// </para>
    /// <para>
    /// Ten rather than Npgsql's hundred, because the multiplier is the tenant count: a hundred
    /// per tenant is a thousand connections at ten tenants, and PostgreSQL's own default
    /// <c>max_connections</c> is a hundred for the whole server.
    /// </para>
    /// </remarks>
    public int MaxPoolSizePerTenant { get; init; } = 10;

    /// <summary>
    /// Whether a tenant seen for the first time has its schema created and migrated here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>On by default, because the alternative is a control plane this repository does
    /// not ship.</strong> A tenant arriving at run time is the ordinary case for a SaaS
    /// platform, and a runtime that refused every unprovisioned tenant would make
    /// <see cref="TenantIsolation.Schema"/> unusable without an external provisioner — which is
    /// exactly the "declared and inert" state the level is being built to leave.
    /// </para>
    /// <para>
    /// <strong>And switchable, because DDL in the request path is a real objection.</strong> A
    /// deployment whose database roles forbid runtime DDL, or that would rather pay the
    /// migration cost at provisioning time than on one unlucky caller's first request, sets this
    /// false and provisions out of band; a tenant whose schema is then absent is refused by name
    /// rather than met with a missing-relation error. <see cref="PostgresJournalOptions.CreateSchemaIfMissing"/>
    /// is the same switch for the control schema and is deliberately not reused: one of them is
    /// applied once at deployment and the other on every new customer.
    /// </para>
    /// </remarks>
    public bool ProvisionOnFirstUse { get; init; } = true;
}

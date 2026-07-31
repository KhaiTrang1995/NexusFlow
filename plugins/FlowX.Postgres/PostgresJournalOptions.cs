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
}

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
}

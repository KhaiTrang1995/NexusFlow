using System.Text.RegularExpressions;
using Npgsql;

namespace FlowX.Cli.Replay;

/// <summary>
/// Reads one instance's history out of a PostgreSQL journal.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the only file in the CLI that knows what a database is</strong>, and
/// <c>ReplayUsageTests.NothingOutsideTheReplayReaderKnowsWhatADatabaseIs</c> keeps it that
/// way. [ADR-0020](../../../docs/adr/ADR-0020-cli-reads-the-journal-as-rows.md)) confines the
/// store dependency here so that a fifth verb cannot acquire one by accident.
/// </para>
/// <para>
/// <strong>The column names below are a copy of a contract nothing checks.</strong> They are
/// defined by <c>plugins/FlowX.Postgres/Migrations/0001_initial_schema.sql</c> and no
/// compiler check connects the two files, so a rename there breaks this at run time. That is
/// the cost ADR-0020 accepts by name, and <c>ReplayInspectTests</c> — which runs against a
/// migrated schema and refuses to skip when a database was promised — is the whole of what
/// stands in for the check that cannot exist.
/// </para>
/// <para>
/// <strong>Synchronous by choice.</strong> A CLI reads one instance and exits; there is no
/// concurrency to overlap and no thread to free up. The asynchronous overload would have to
/// be blocked on at the entry point, which is the sync-over-async the threading analyzer
/// forbids and would rightly.
/// </para>
/// </remarks>
internal static partial class JournalReader
{
    /// <summary>The schema the journal lives in unless a caller says otherwise.</summary>
    /// <remarks>Matches <c>PostgresJournalOptions.DefaultSchema</c>, which is where the migrator puts it.</remarks>
    public const string DefaultSchema = "flowx";

    /// <summary>The environment variable a connection string can come from.</summary>
    /// <remarks>
    /// The same variable the PostgreSQL adapter's own tests read, so an operator who has one
    /// exported for a psql session does not have to learn a second name for it.
    /// </remarks>
    public const string ConnectionVariable = "FLOWX_POSTGRES_CONNECTION";

    /// <summary>
    /// Reads an instance and every step it committed, or reports that there is no such instance.
    /// </summary>
    /// <param name="connectionString">Where the journal is.</param>
    /// <param name="schema">The schema holding <c>flow_instance</c> and <c>flow_step</c>.</param>
    /// <param name="instanceId">The instance to read.</param>
    /// <returns>The history, or <c>null</c> when no row carries that id.</returns>
    /// <exception cref="JournalUnreadableException">
    /// The server did not answer, or answered that the journal's tables are not there.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is not a bare identifier.</exception>
    public static InstanceHistory? Read(string connectionString, string schema, Guid instanceId)
    {
        try
        {
            using var dataSource = NpgsqlDataSource.Create(SearchPath(connectionString, schema));
            using var connection = dataSource.OpenConnection();

            var instance = ReadInstance(connection, instanceId);

            return instance is null
                ? null
                : instance with { Steps = ReadSteps(connection, instanceId) };
        }
        catch (PostgresException failure) when (failure.SqlState == UndefinedTable)
        {
            throw new JournalUnreadableException(
                $"the schema '{schema}' has no FlowX journal in it — {failure.MessageText}. " +
                "Pass --schema, or run the migrations against this database.",
                failure);
        }
        catch (NpgsqlException failure)
        {
            throw new JournalUnreadableException(failure.Message, failure);
        }
    }

    /// <summary><c>42P01</c>: the table is not there.</summary>
    private const string UndefinedTable = "42P01";

    /// <summary>The instance row. Unqualified: <c>search_path</c> selects the schema.</summary>
    private const string SelectInstance =
        """
        SELECT flow_id, flow_version, tenant_id, state, correlation_id, trace_id,
               created_at, updated_at, input
        FROM   flow_instance
        WHERE  instance_id = @instance
        """;

    /// <summary>
    /// The history, in commit order.
    /// </summary>
    /// <remarks>
    /// Ordered by <c>sequence</c> rather than by <c>committed_at</c>. The sequence is handed
    /// out under the row lock the commit already takes, so it cannot skip or collide; two
    /// rows written in the same millisecond have a defined order under one and not the other.
    /// </remarks>
    private const string SelectSteps =
        """
        SELECT scope, step_id, attempt, sequence, capability_id, capability_version,
               outcome, result, nondeterministic, duration_ms, committed_at
        FROM   flow_step
        WHERE  instance_id = @instance
        ORDER BY sequence
        """;

    private static InstanceHistory? ReadInstance(NpgsqlConnection connection, Guid instanceId)
    {
        using var command = connection.CreateCommand();

        command.CommandText = SelectInstance;
        command.Parameters.AddWithValue("instance", instanceId);

        using var reader = command.ExecuteReader();

        if (!reader.Read())
        {
            return null;
        }

        return new InstanceHistory
        {
            InstanceId = instanceId,
            FlowId = reader.GetString(0),
            FlowVersion = reader.GetString(1),
            TenantId = reader.IsDBNull(2) ? null : reader.GetString(2),
            State = reader.GetString(3),
            CorrelationId = reader.IsDBNull(4) ? null : reader.GetString(4),
            TraceId = reader.IsDBNull(5) ? null : reader.GetString(5),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(6),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(7),

            // DBNull, not the JSON literal `null`, is what makes the input unknown. The two
            // are distinguishable here and the distinction is kept: a stored `null` document
            // is something a writer chose, and an absent one is something nobody recorded.
            Input = reader.IsDBNull(8) ? null : reader.GetString(8),
        };
    }

    private static List<HistoryStep> ReadSteps(NpgsqlConnection connection, Guid instanceId)
    {
        using var command = connection.CreateCommand();

        command.CommandText = SelectSteps;
        command.Parameters.AddWithValue("instance", instanceId);

        using var reader = command.ExecuteReader();

        var steps = new List<HistoryStep>();

        while (reader.Read())
        {
            steps.Add(new HistoryStep
            {
                Scope = reader.GetString(0),
                StepId = reader.GetInt32(1),
                Attempt = reader.GetInt32(2),
                Sequence = reader.GetInt64(3),
                CapabilityId = reader.GetString(4),
                CapabilityVersion = reader.GetString(5),
                Outcome = reader.GetString(6),
                Result = reader.IsDBNull(7) ? null : reader.GetString(7),
                Nondeterminism = reader.IsDBNull(8) ? null : reader.GetString(8),
                DurationMs = reader.GetInt64(9),
                CommittedAt = reader.GetFieldValue<DateTimeOffset>(10),
            });
        }

        return steps;
    }

    /// <summary>
    /// The connection string with the schema selected on the connection.
    /// </summary>
    /// <param name="connectionString">Where the journal is.</param>
    /// <param name="schema">The schema holding the journal's tables.</param>
    /// <returns>A connection string whose <c>search_path</c> is the schema.</returns>
    /// <exception cref="ArgumentException">
    /// The schema is not a bare identifier, or the connection string is not usable.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <strong>The schema is set on the connection rather than written into every
    /// statement</strong>, which is what lets the two queries above be constants: no
    /// statement is assembled from a configured value, so there is nothing for a value to be
    /// injected into. <c>FlowX.Postgres</c> does the same thing for the same reason, and a
    /// reader that reached the same store by a weaker route would be the wrong kind of
    /// independence.
    /// </para>
    /// <para>
    /// The name is validated anyway. It costs nothing, it is a second lock on the door, and
    /// the error it produces names the actual problem — which "relation flow_instance does
    /// not exist" would not.
    /// </para>
    /// </remarks>
    public static string SearchPath(string connectionString, string schema)
    {
        if (!SchemaName().IsMatch(schema))
        {
            throw new ArgumentException(
                $"'{schema}' is not a usable schema name. It has to be a bare lower-case " +
                "identifier — letters, digits and underscores, starting with a letter or an " +
                "underscore — which is all the migrator will ever have created.",
                nameof(schema));
        }

        return new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = schema,
        }.ConnectionString;
    }

    [GeneratedRegex(@"^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SchemaName();
}

/// <summary>The journal could not be read at all — as opposed to read and found empty.</summary>
/// <remarks>
/// <para>
/// <strong>The distinction this type exists to preserve is the dangerous one.</strong>
/// Telling an operator mid-incident that an instance does not exist, when the truth is that
/// the tool could not look, is the worst of the wrong answers available: it says the data is
/// gone. So "could not read" leaves here as its own type and becomes its own exit code, and
/// "read, and there is no such row" is a null return.
/// </para>
/// <para>
/// It also keeps the Npgsql types inside this folder. <c>Program</c> catches this and never
/// names a database library, which is the arrangement
/// <c>NothingOutsideTheReplayReaderKnowsWhatADatabaseIs</c> asserts.
/// </para>
/// </remarks>
public sealed class JournalUnreadableException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What the store said, in a sentence a reader can act on.</param>
    /// <param name="inner">The store's own failure.</param>
    public JournalUnreadableException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What the store said.</param>
    public JournalUnreadableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public JournalUnreadableException()
    {
    }
}

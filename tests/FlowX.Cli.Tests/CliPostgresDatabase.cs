using Npgsql;

namespace FlowX.Cli.Tests;

/// <summary>
/// Decides, once, whether there is a PostgreSQL server for the <c>replay</c> tests to run
/// against — and records why when there is not.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The same three-way decision <c>FlowX.Postgres.Tests</c> makes, and deliberately
/// not a reference to it.</strong> A test project referencing another test project couples
/// two suites that are allowed to move independently, and the thing being shared here is
/// twenty lines of policy rather than a fixture. What must not diverge is the *policy*, and
/// it is restated rather than imported: no connection string configured means every test
/// skips with a reason; a connection string configured but unreachable means every test
/// **fails**.
/// </para>
/// <para>
/// <strong>Why the second half matters more here than anywhere else.</strong>
/// [ADR-0020](../../docs/adr/ADR-0020-cli-reads-the-journal-as-rows.md) accepts one named
/// cost: the CLI now knows PostgreSQL column names, and nothing at compile time connects
/// <c>Replay/JournalReader.cs</c> to <c>0001_initial_schema.sql</c>. The only check that
/// catches a rename is a test that runs the verb against a migrated schema. If that test
/// were allowed to skip when a promised database did not answer, the accepted cost would
/// have no compensating control at all and the ADR would be describing a check that does
/// not run.
/// </para>
/// </remarks>
internal static class CliPostgresDatabase
{
    /// <summary>The variable that supplies a connection string.</summary>
    public const string ConnectionVariable = "FLOWX_POSTGRES_CONNECTION";

    private static readonly Lazy<Probe> Result = new(Run, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The configured connection string, or null when none was supplied.</summary>
    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionVariable) is { Length: > 0 } value
            ? value
            : null;

    /// <summary>Whether the environment promised a database, whether or not one answered.</summary>
    public static bool IsPromised => ConnectionString is not null;

    /// <summary>Whether a server answered.</summary>
    public static bool IsAvailable => Result.Value.Available;

    /// <summary>Why, in a sentence a reader can act on. Never empty.</summary>
    public static string Reason => Result.Value.Reason;

    /// <summary>The exception a test throws when a promised database is not there.</summary>
    /// <returns>The exception to throw.</returns>
    public static InvalidOperationException Unreachable() => new(
        $"{ConnectionVariable} is set, so this run promised a PostgreSQL server, and none " +
        $"answered. This is a failure rather than a skip on purpose: `flowx replay` reads " +
        $"column names that no compiler check ties to the migration that defines them, and " +
        $"these tests are the only thing that notices when the two drift apart. {Reason}");

    private static Probe Run()
    {
        var connectionString = ConnectionString;

        if (connectionString is null)
        {
            return new Probe(
                false,
                $"No PostgreSQL server is configured, so every `flowx replay` test is skipped " +
                $"and nothing about the verb's read path has been verified. Set " +
                $"{ConnectionVariable} to a connection string to run them — for example " +
                "'Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres'. " +
                "The account needs CREATE on the database, because each test is given its own schema.");
        }

        try
        {
            using var dataSource = NpgsqlDataSource.Create(connectionString);
            using var connection = dataSource.OpenConnection();
            using var command = connection.CreateCommand();

            command.CommandText = "SELECT version()";

            var version = command.ExecuteScalar() as string;

            return new Probe(true, $"Connected to {version}.");
        }
        catch (NpgsqlException failure)
        {
            return new Probe(
                false,
                $"{ConnectionVariable} is set but the server did not answer: {failure.Message}");
        }
        catch (ArgumentException failure)
        {
            return new Probe(
                false,
                $"{ConnectionVariable} is not a usable connection string: {failure.Message}");
        }
    }

    private readonly record struct Probe(bool Available, string Reason);
}

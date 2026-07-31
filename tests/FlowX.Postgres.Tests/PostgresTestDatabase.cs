using Npgsql;

namespace FlowX.Postgres.Tests;

/// <summary>
/// Decides, once, whether there is a PostgreSQL server to test against — and records why
/// when there is not.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A silently skipped test is indistinguishable from a passing one.</strong> This
/// repository has shipped tests that could not fail, and an integration suite that quietly
/// evaporates when a container is missing is the easiest way to ship another. So the
/// decision here is deliberately noisy in three directions at once.
/// </para>
/// <para>
/// <strong>No server configured: every test skips, and each skip carries this class's
/// reason.</strong> The runner prints one line per skipped test naming the environment
/// variable and how to supply one, so a run with no database reads as a run with no
/// database rather than as a green suite.
/// </para>
/// <para>
/// <strong>A server configured but unreachable: every test fails.</strong> That is the
/// case worth being loud about, because it is the one that looks like success in CI —
/// somebody wired a service container, the variable is set, the container did not come up,
/// and a skip would report thirty-three passes. <see cref="Unreachable"/> is thrown rather
/// than skipped for exactly that reason, and
/// <c>DatabaseAvailabilityTests.APromisedDatabaseMustBeReachable</c> fails first and says
/// so plainly.
/// </para>
/// <para>
/// <strong>The decision itself is asserted.</strong> <c>DatabaseAvailabilityTests</c> runs
/// whether or not a database exists and checks that this class explained itself, so the
/// skip path cannot quietly become empty.
/// </para>
/// </remarks>
internal static class PostgresTestDatabase
{
    /// <summary>The variable that supplies a connection string.</summary>
    public const string ConnectionVariable = "FLOWX_POSTGRES_CONNECTION";

    private static readonly Lazy<Probe> Result = new(Run, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The configured connection string, or null when none was supplied.</summary>
    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionVariable) is { Length: > 0 } value
            ? value
            : null;

    /// <summary>
    /// Whether the environment promised a database, whether or not one answered.
    /// </summary>
    /// <remarks>
    /// The distinction this whole class turns on. "Nobody asked for a database" is a
    /// reason to skip; "somebody asked for a database and there isn't one" is a defect in
    /// the run.
    /// </remarks>
    public static bool IsPromised => ConnectionString is not null;

    /// <summary>Whether a server answered.</summary>
    public static bool IsAvailable => Result.Value.Available;

    /// <summary>Why, in a sentence a reader can act on. Never empty.</summary>
    public static string Reason => Result.Value.Reason;

    /// <summary>
    /// The exception a test throws when a promised database is not there.
    /// </summary>
    /// <returns>The exception to throw.</returns>
    public static InvalidOperationException Unreachable() => new(
        $"{ConnectionVariable} is set, so this run promised a PostgreSQL server, and none " +
        $"answered. This is a failure rather than a skip on purpose: a skip here would " +
        $"report the whole conformance suite as green against a database that was never " +
        $"reached. {Reason}");

    private static Probe Run()
    {
        var connectionString = ConnectionString;

        if (connectionString is null)
        {
            return new Probe(
                false,
                $"No PostgreSQL server is configured, so every test that needs one is " +
                $"skipped and nothing about this adapter has been verified. Set " +
                $"{ConnectionVariable} to a connection string to run them — for example " +
                $"'Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres'. " +
                "The account needs CREATE on the database, because each test is given its " +
                "own schema.");
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

using Npgsql;

namespace Healthcare.Tests;

/// <summary>
/// Decides, once, whether there is a PostgreSQL server to test against — and records why when
/// there is not.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A silently skipped test is indistinguishable from a passing one</strong>, and this
/// project is the one place in the repository where that would be worst: what it asserts is
/// that one clinic cannot read another clinic's patients. So the decision is noisy in the same
/// three directions <c>FlowX.Postgres.Tests</c>'s own probe is, and the wording is deliberately
/// its wording.
/// </para>
/// <para>
/// No server configured: every test skips, and each skip carries this class's reason. A server
/// configured but unreachable: every test <em>fails</em>, because that is the case that looks
/// like success in CI — somebody wired a service container, the variable is set, the container
/// did not come up, and a skip would report a green isolation suite that never opened a
/// connection.
/// </para>
/// <para>
/// This is a second copy of a decision <c>FlowX.Postgres.Tests</c> already makes, and the
/// duplication is deliberate rather than overlooked: that one is <c>internal</c> to an assembly
/// whose <c>InternalsVisibleTo</c> is not this project's to widen, and widening it to share
/// twenty lines would put a test project's fixtures into another test project's public surface.
/// </para>
/// </remarks>
internal static class ClinicDatabase
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
        "answered. This is a failure rather than a skip on purpose: a skip here would report " +
        "the cross-tenant and erasure suites as green against a database that was never " +
        $"reached. {Reason}");

    private static Probe Run()
    {
        var connectionString = ConnectionString;

        if (connectionString is null)
        {
            return new Probe(
                false,
                "No PostgreSQL server is configured, so every test that needs one is skipped " +
                "and nothing about this sample's isolation or its erasure has been verified. " +
                $"Set {ConnectionVariable} to a connection string — for example " +
                "'Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres'. " +
                "The account needs CREATE on the database, because each test is given its own " +
                "schema.");
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

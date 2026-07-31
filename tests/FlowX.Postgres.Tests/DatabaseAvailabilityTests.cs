using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// Gates on the skip itself, so that "no database" can never look like "all green".
/// </summary>
/// <remarks>
/// <para>
/// Every test in this class runs whether or not a server is configured. That is the point:
/// the rest of this project skips without one, and something has to be awake to say so.
/// </para>
/// <para>
/// docs/21-Quality-Gates.md §2.4 refuses a check that passes vacuously, and a conditional
/// integration suite is the standard way to acquire one. The three assertions below are the
/// three ways this suite could go quiet — an unexplained skip, a promised database that
/// never answered, and a probe that reported availability without having connected.
/// </para>
/// </remarks>
public sealed class DatabaseAvailabilityTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Creates the fixture.</summary>
    /// <param name="output">Where the decision is written, so it is in the run log either way.</param>
    public DatabaseAvailabilityTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The probe always explains itself, and names the variable when it found nothing.
    /// </summary>
    /// <remarks>
    /// A skip reason is the only thing a reader of a green run has to tell them that a third
    /// of this project did not execute. An empty or vague one is how that reader concludes
    /// the adapter was tested.
    /// </remarks>
    [Fact]
    public void TheProbeExplainsItself()
    {
        _output.WriteLine($"PostgreSQL: {PostgresTestDatabase.Reason}");

        PostgresTestDatabase.Reason.ShouldNotBeNullOrWhiteSpace(
            "the reason is printed beside every skipped test, and a blank one turns a " +
            "suite that did not run into a suite that looks like it passed.");

        if (!PostgresTestDatabase.IsAvailable)
        {
            PostgresTestDatabase.Reason.ShouldContain(
                PostgresTestDatabase.ConnectionVariable,
                Case.Sensitive,
                "a skip has to say what to set to stop it skipping, or it is a dead end.");
        }
    }

    /// <summary>
    /// A database the environment promised must actually answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the assertion that makes the skip safe.</strong> The dangerous
    /// arrangement is not "no database, tests skipped" — it is CI wiring a service
    /// container, the container failing to start, and the suite reporting thirty-three
    /// passes because every test skipped politely. Setting the variable is a promise, and
    /// this is where the promise is checked.
    /// </para>
    /// <para>
    /// It is deliberately not itself skippable. When the variable is absent it asserts
    /// nothing and says so; when the variable is present it is the first thing to go red.
    /// </para>
    /// </remarks>
    [Fact]
    public void APromisedDatabaseMustBeReachable()
    {
        if (!PostgresTestDatabase.IsPromised)
        {
            _output.WriteLine(
                $"{PostgresTestDatabase.ConnectionVariable} is unset, so nothing was " +
                "promised and nothing is asserted here. The conformance suite did not run " +
                "against PostgreSQL in this session.");

            return;
        }

        PostgresTestDatabase.IsAvailable.ShouldBeTrue(
            $"{PostgresTestDatabase.ConnectionVariable} is set, so this run promised a " +
            $"server. {PostgresTestDatabase.Reason}");
    }

    /// <summary>
    /// Availability is claimed only after a real connection, and it names the server.
    /// </summary>
    /// <remarks>
    /// The probe could go wrong in the opposite direction — reporting a database it never
    /// reached — and everything downstream would then fail confusingly instead of skipping
    /// clearly. Checking that the reason carries the server's own version string is the
    /// cheapest proof that a connection was opened rather than assumed.
    /// </remarks>
    [Fact]
    public void AvailabilityIsClaimedOnlyAfterConnecting()
    {
        if (!PostgresTestDatabase.IsAvailable)
        {
            return;
        }

        PostgresTestDatabase.Reason.ShouldContain(
            "PostgreSQL",
            Case.Sensitive,
            "availability is reported from the server's own version string, so a probe " +
            "that never connected cannot claim it.");
    }
}

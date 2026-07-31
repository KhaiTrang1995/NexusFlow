using Shouldly;
using Xunit;

namespace FlowX.Redis.Tests;

/// <summary>
/// Gates on the skip itself, so that "no Redis" can never look like "all green".
/// </summary>
/// <remarks>
/// <para>
/// Every test in this class runs whether or not a server is configured. That is the point: the
/// rest of this project skips without one, and something has to be awake to say so.
/// </para>
/// <para>
/// <c>docs/21-Quality-Gates.md §2.4</c> refuses a check that passes vacuously, and a
/// conditional integration suite is the standard way to acquire one. This is
/// <c>DatabaseAvailabilityTests</c> for Redis, deliberately the same three assertions in the
/// same order — an unexplained skip, a promised server that never answered, and a probe that
/// reported availability without having connected. Two adapters that agree about when a suite
/// is allowed to go quiet is worth more than two adapters that each invented an answer.
/// </para>
/// </remarks>
public sealed class RedisAvailabilityTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Creates the fixture.</summary>
    /// <param name="output">Where the decision is written, so it is in the run log either way.</param>
    public RedisAvailabilityTests(ITestOutputHelper output) => _output = output;

    /// <summary>The probe always explains itself, and names the variable when it found nothing.</summary>
    /// <remarks>
    /// A skip reason is the only thing a reader of a green run has to tell them that this
    /// package did not execute. An empty or vague one is how that reader concludes the adapter
    /// was tested.
    /// </remarks>
    [Fact]
    public void TheProbeExplainsItself()
    {
        _output.WriteLine($"Redis: {RedisTestServer.Reason}");

        RedisTestServer.Reason.ShouldNotBeNullOrWhiteSpace(
            "the reason is printed beside every skipped test, and a blank one turns a suite " +
            "that did not run into a suite that looks like it passed.");

        if (!RedisTestServer.IsAvailable)
        {
            RedisTestServer.Reason.ShouldContain(
                RedisTestServer.ConnectionVariable,
                Case.Sensitive,
                "a skip has to say what to set to stop it skipping, or it is a dead end.");
        }
    }

    /// <summary>A server the environment promised must actually answer.</summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the assertion that makes the skip safe.</strong> The dangerous
    /// arrangement is not "no Redis, tests skipped" — it is CI wiring a service container, the
    /// container failing to start, and the suite reporting the lease conformance suite as
    /// green because every test skipped politely. Setting the variable is a promise, and this
    /// is where the promise is checked.
    /// </para>
    /// <para>
    /// It is deliberately not itself skippable. When the variable is absent it asserts nothing
    /// and says so; when the variable is present it is the first thing to go red.
    /// </para>
    /// </remarks>
    [Fact]
    public void APromisedServerMustBeReachable()
    {
        if (!RedisTestServer.IsPromised)
        {
            _output.WriteLine(
                $"{RedisTestServer.ConnectionVariable} is unset, so nothing was promised and " +
                "nothing is asserted here. The lease conformance suite did not run against " +
                "Redis in this session.");

            return;
        }

        RedisTestServer.IsAvailable.ShouldBeTrue(
            $"{RedisTestServer.ConnectionVariable} is set, so this run promised a server. " +
            RedisTestServer.Reason);
    }

    /// <summary>Availability is claimed only after a real connection, and it names the server.</summary>
    /// <remarks>
    /// The probe could go wrong in the opposite direction — reporting a server it never reached
    /// — and everything downstream would then fail confusingly instead of skipping clearly.
    /// Checking that the reason carries the server's own reported version is the cheapest proof
    /// that a connection was opened rather than assumed.
    /// </remarks>
    [Fact]
    public void AvailabilityIsClaimedOnlyAfterConnecting()
    {
        if (!RedisTestServer.IsAvailable)
        {
            return;
        }

        RedisTestServer.Reason.ShouldContain(
            "Redis",
            Case.Sensitive,
            "availability is reported from the server's own version, so a probe that never " +
            "connected cannot claim it.");
    }
}

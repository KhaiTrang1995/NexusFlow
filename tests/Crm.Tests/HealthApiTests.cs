using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The two probes, and the difference between them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One endpoint answering both questions is the defect these tests hold shut.</strong>
/// Liveness asks "is this process worth restarting"; readiness asks "should you send me work".
/// A single <c>/health</c> consulting the database answers the second and gets wired to the
/// first, and then a database failover restarts every replica — none of which can reach the
/// database either.
/// </para>
/// <para>
/// <strong>Nothing here goes over HTTP, deliberately.</strong> <see cref="CrmApplication"/>
/// composes its own endpoints by hand and maps no health checks, so a test asking it for
/// <c>/health/ready</c> would assert the harness rather than the application — the mistake
/// <see cref="BrokerlessCompositionTests"/> exists because of. The endpoints themselves are one
/// line each in <c>Program.cs</c>; what is worth asserting is the check they run and the exit
/// code a container reads.
/// </para>
/// </remarks>
public sealed class HealthApiTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A database that does not answer makes the node unready, and says so briefly.</summary>
    /// <remarks>
    /// <strong>Checked against the check rather than over HTTP</strong>, because taking the
    /// suite's database away for one test would take it away from every test sharing the server.
    /// </remarks>
    [Fact]
    public async Task AnUnreachableDatabaseIsUnready()
    {
        // A port nothing listens on. The refusal is immediate, so this does not wait a timeout.
        await using var nowhere = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=crm;Username=crm;Password=none;Timeout=1");

        var result = await new CrmSchemaHealthCheck(nowhere)
            .CheckHealthAsync(new HealthCheckContext(), Cancellation);

        result.Status.ShouldBe(HealthStatus.Unhealthy);

        // The message and not the exception: a health endpoint is the most reachable thing this
        // process serves, and a stack trace on it names hosts, ports and accounts.
        result.Description.ShouldNotBeNull().ShouldStartWith("The database did not answer");
        result.Exception.ShouldBeNull();
    }

    /// <summary>
    /// A database reachable but behind this build's schema is unready, not healthy.
    /// </summary>
    /// <remarks>
    /// <strong>The failure this check exists for.</strong> The process starts, every probe
    /// answers, and the first request touching a column the migration would have added fails —
    /// a deployment that looks healthy and is not.
    /// </remarks>
    [Fact]
    public async Task ASchemaBehindThisBuildIsUnready()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation, throughVersion: 1);

        var result = await new CrmSchemaHealthCheck(crm.DataSource)
            .CheckHealthAsync(new HealthCheckContext(), Cancellation);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldNotBeNull().ShouldContain(
            CrmMigrator.TargetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>A schema at this build's version is healthy, and names it.</summary>
    [Fact]
    public async Task ASchemaAtThisBuildsVersionIsHealthy()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var result = await new CrmSchemaHealthCheck(crm.DataSource)
            .CheckHealthAsync(new HealthCheckContext(), Cancellation);

        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Description.ShouldNotBeNull().ShouldContain(
            CrmMigrator.TargetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The probe a container runs reports failure when nothing is listening.</summary>
    /// <remarks>
    /// The half of <see cref="CrmProbe"/> a test can reach: an exit code of one is what makes a
    /// container unhealthy, and returning nought on a refused connection would make every
    /// container healthy for ever.
    /// </remarks>
    [Fact]
    public async Task TheContainerProbeFailsWhenNothingIsListening()
    {
        CrmProbe.WasAsked(["--healthcheck"]).ShouldBeTrue();
        CrmProbe.WasAsked(["--urls", "http://localhost:5000"]).ShouldBeFalse();

        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");

        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", "1");

            (await CrmProbe.RunAsync(Cancellation)).ShouldBe(1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", previous);
        }
    }
}

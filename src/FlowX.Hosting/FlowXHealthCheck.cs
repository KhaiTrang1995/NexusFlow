using System.Globalization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlowX.Hosting;

/// <summary>Readiness for the FlowX runtime.</summary>
/// <remarks>
/// <para>
/// Reports <see cref="HealthStatus.Unhealthy"/> while draining, deliberately. The
/// instinct is to report healthy — the node is, after all, still working — but a
/// healthy shutting-down pod keeps receiving traffic it is about to abandon. The probe
/// answers "should you send me work", not "am I alive".
/// </para>
/// <para>
/// This is a <em>readiness</em> check. A liveness probe wired to it would restart a pod
/// that is draining correctly, which is the opposite of what either signal is for.
/// </para>
/// </remarks>
public sealed class FlowXHealthCheck : IHealthCheck
{
    private readonly FlowHost _host;

    /// <summary>Creates the check.</summary>
    public FlowXHealthCheck(FlowHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var inFlight = _host.InFlight.ToString(CultureInfo.InvariantCulture);

        if (_host.IsDraining)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"The node is draining; {inFlight} flow(s) still in flight. " +
                "Stop sending work here."));
        }

        return Task.FromResult(_host.IsReady
            ? HealthCheckResult.Healthy($"Ready. {inFlight} flow(s) in flight.")
            : HealthCheckResult.Unhealthy("The runtime has not finished starting."));
    }
}

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FlowX.Observability;

/// <summary>
/// The single <see cref="System.Diagnostics.ActivitySource"/> and <see cref="System.Diagnostics.Metrics.Meter"/>
/// every FlowX assembly emits through, and the cardinality discipline
/// <a href="../../../docs/12-Observability.md">12-Observability</a> §3 requires the platform to
/// enforce rather than document.
/// </summary>
/// <remarks>
/// <para>
/// <strong>In <c>FlowX.Abstractions</c> because three layers emit and they must agree.</strong>
/// The step boundary is instrumented in <c>FlowX.Runtime</c>, the flow boundary and the sweeps
/// in <c>FlowX.Hosting</c>, and the outbox and instance gauges in <c>plugins/FlowX.Postgres</c>,
/// which references Abstractions and nothing else. One source name and one meter name is the
/// whole point — an operator adds <c>"FlowX"</c> to an exporter once, not once per package.
/// </para>
/// <para>
/// <strong>This adds no dependency, and that is checked.</strong>
/// <c>System.Diagnostics.DiagnosticSource</c> — which carries both
/// <see cref="System.Diagnostics.Activity"/> and <c>System.Diagnostics.Metrics</c> — is part of
/// the <c>Microsoft.NETCore.App</c> shared framework on <c>net10.0</c>, so
/// <c>AbstractionsHasNoDependencies</c> (ADR-0009) still holds with zero package references.
/// An <c>IMeterFactory</c> would have been the DI-idiomatic shape and would have cost
/// <c>Microsoft.Extensions.Diagnostics.Abstractions</c> here, inherited by every plugin and by
/// all user code.
/// </para>
/// <para>
/// <strong>Static rather than injected, because budget B6 is a hard zero.</strong> B6 is
/// "telemetry with no listener costs 0 ns and 0 B per step", and it is a correctness property
/// rather than a benchmark: <see cref="System.Diagnostics.ActivitySource.StartActivity(string, ActivityKind)"/>
/// returns <c>null</c> with no listener, and an <see cref="Instrument"/> is cheap only if the
/// tag list is not built first. Both facts are only usable from a call site that can read the
/// instrument without resolving anything. <c>TelemetryCostTests</c> asserts the zero.
/// </para>
/// </remarks>
public static class FlowXTelemetry
{
    /// <summary>
    /// The name an exporter subscribes to for FlowX spans, and the meter name for FlowX metrics.
    /// </summary>
    /// <remarks>
    /// One name for both. OpenTelemetry's <c>AddSource</c> and <c>AddMeter</c> take independent
    /// strings, and two names would mean an operator who wired traces silently getting no
    /// metrics — a misconfiguration indistinguishable from a healthy, idle system.
    /// </remarks>
    public const string SourceName = "FlowX";

    /// <summary>The label a tenant outside the allow-list is reported under.</summary>
    /// <remarks>
    /// §3: the <c>tenant</c> label "is capped by a configurable allow-list with an <c>other</c>
    /// bucket". The bucket is what makes the cap safe — dropping the label instead would make a
    /// tenant-labelled series and an unlabelled one two different series, and the sum over
    /// tenants would stop equalling the total.
    /// </remarks>
    public const string OtherTenant = "other";

    /// <summary>The version stamped on the source and the meter.</summary>
    private const string Version = "0.1.0";

    private static volatile HashSet<string>? _tenantAllowList;

    /// <summary>The source every FlowX span is created on.</summary>
    public static ActivitySource Source { get; } = new(SourceName, Version);

    /// <summary>The meter every FlowX instrument is created on.</summary>
    /// <remarks>
    /// Public because <c>plugins/FlowX.Postgres</c> registers observable gauges against it:
    /// <c>flowx_outbox_pending</c>, <c>flowx_outbox_lag_seconds</c> and <c>flowx_flow_active</c>
    /// are all questions only a store can answer, and an observable instrument is the shape for
    /// a value that is read on collection rather than written on an event.
    /// </remarks>
    public static Meter Meter { get; } = new(SourceName, Version);

    /// <summary>
    /// Caps the <c>tenant</c> metric label to a known set. Every other tenant is reported as
    /// <see cref="OtherTenant"/>.
    /// </summary>
    /// <param name="tenants">
    /// The tenants that have tenant-level SLOs, or <c>null</c> to report every tenant as
    /// <see cref="OtherTenant"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>The default is that nothing is allow-listed</strong>, so an application that
    /// never calls this emits <c>tenant=other</c> and one bounded series per flow. §3 says
    /// "<c>tenant</c> is a label only where tenant-level SLOs exist" and that "cardinality is a
    /// production incident waiting to happen" — a default that passed the tenant through would
    /// make the incident the default, and would make it arrive in production rather than in a
    /// review.
    /// </para>
    /// <para>
    /// Called once at start-up, before flows run. The set is replaced wholesale and read
    /// through a <c>volatile</c> field, so a reader sees either the old set or the new one and
    /// never a set being built.
    /// </para>
    /// </remarks>
    public static void ConfigureTenantLabels(IEnumerable<string>? tenants) =>
        _tenantAllowList = tenants is null ? null : new HashSet<string>(tenants, StringComparer.Ordinal);

    /// <summary>
    /// The value the <c>tenant</c> metric label takes for <paramref name="tenantId"/>.
    /// </summary>
    /// <param name="tenantId">The resolved tenant, or <c>null</c> for an untenanted flow.</param>
    /// <returns>
    /// The tenant itself when it is allow-listed, and <see cref="OtherTenant"/> otherwise.
    /// </returns>
    /// <remarks>
    /// Returns the caller's own string when it matches, rather than a copy, so a hot path pays
    /// a set lookup and no allocation.
    /// </remarks>
    public static string TenantLabel(string? tenantId)
    {
        if (tenantId is null)
        {
            return OtherTenant;
        }

        var allowed = _tenantAllowList;

        return allowed is not null && allowed.Contains(tenantId) ? tenantId : OtherTenant;
    }
}

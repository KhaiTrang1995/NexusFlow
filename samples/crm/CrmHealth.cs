using System.Globalization;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Crm;

/// <summary>
/// Whether this process can serve a request, which for a CRM means whether its tables are there.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Readiness, never liveness.</strong> A database that has gone away is a reason to stop
/// sending this node work and not a reason to restart it — the restart cannot reach the database
/// either, and a deployment that restarts every replica during a failover turns a recoverable
/// outage into a cold start. <see cref="Tag"/> is what keeps the two endpoints apart.
/// </para>
/// <para>
/// <strong>It checks the version, not just the connection.</strong> A reachable database at the
/// wrong schema version is the failure this exists for: the process starts, every probe answers,
/// and the first request touching a table the migration would have added fails with an error
/// about a missing column. That is a deployment that looks healthy and is not.
/// </para>
/// </remarks>
public sealed class CrmSchemaHealthCheck : IHealthCheck
{
    /// <summary>The tag the readiness endpoint selects on.</summary>
    public const string Tag = "ready";

    /// <summary>The probe's name, as it appears in the report.</summary>
    public const string Name = "crm-schema";

    // coalesce, so a schema whose ledger table exists and is empty reads as version 0 rather
    // than as null — which would be indistinguishable from "the query failed" downstream.
    private const string Version =
        "SELECT coalesce(max(version), 0) FROM crm_schema_migration";

    private readonly NpgsqlDataSource _source;

    /// <summary>Creates the check.</summary>
    /// <param name="source">The pool every CRM statement is issued on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public CrmSchemaHealthCheck(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var closing = connection.ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using var closingCommand = command.ConfigureAwait(false);

            command.CommandText = Version;

            var applied = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);

            var target = CrmMigrator.TargetVersion;

            if (applied < target)
            {
                return HealthCheckResult.Unhealthy(
                    $"The schema is at version {applied} and this build writes against {target}.");
            }

            // Ahead rather than behind: a rollback that left the newer schema in place. The
            // tables this build knows are all still there, so it serves — but somebody should
            // be told, and Degraded is the only status that says "works, and is not what you
            // think" without taking the node out of rotation.
            return applied > target
                ? HealthCheckResult.Degraded(
                    $"The schema is at version {applied} and this build writes against {target}. " +
                    "A newer deployment has migrated past this one.")
                : HealthCheckResult.Healthy($"Schema version {applied}.");
        }
        catch (NpgsqlException failure)
        {
            // The message and not the exception: a health endpoint is the most reachable thing a
            // process serves, and a stack trace on it names hosts, ports and user accounts.
            return HealthCheckResult.Unhealthy("The database did not answer: " + failure.Message);
        }
    }
}

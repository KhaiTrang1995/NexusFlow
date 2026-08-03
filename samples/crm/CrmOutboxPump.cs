using FlowX.Postgres;
using Microsoft.Extensions.Hosting;

namespace Crm;

/// <summary>
/// Drains staged events to the broker, for as long as the application is running.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A loop the host owns, not one the plugin registers.</strong> <c>FlowX.Postgres</c>
/// hands over a publisher and stops there — a plugin that registered a
/// <c>BackgroundService</c> would put a hosting dependency inside an adapter, and a deployment
/// that wanted the drain on a different node could not have it.
/// </para>
/// <para>
/// <strong>It runs whether or not a broker is configured.</strong> With none, the publisher the
/// drain resolves is the no-op one, the rows stay marked unpublished, and the change feed still
/// sees them — which is why <c>crm.process.transition</c> works with PostgreSQL alone.
/// </para>
/// </remarks>
/// <param name="publisher">The drain <c>AddFlowXPostgresOutbox</c> registered.</param>
public sealed class CrmOutboxPump(PostgresOutboxPublisher publisher) : BackgroundService
{
    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(publisher);

        return publisher.RunAsync(stoppingToken);
    }
}

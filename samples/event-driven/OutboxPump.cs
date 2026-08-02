using FlowX.Postgres;

namespace EventDriven;

/// <summary>
/// Runs the outbox publisher's loop, which registering it does not start.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Composition-root code, deliberately not a capability and not a flow.</strong>
/// <c>AddFlowXPostgresOutbox</c> produces the publisher and says in its own remarks that a host
/// runs it on whatever it uses for long-running work — starting a polling loop as a side effect
/// of building a container is the same mistake as migrating from one. This is that host.
/// </para>
/// <para>
/// Only the bus transport needs it. <see cref="IssueInvoiceOverChangeFlow"/> reads the same
/// staged rows with no broker in the path, which is why stopping this pump leaves one of the four
/// transports running and is worth trying.
/// </para>
/// </remarks>
/// <param name="publisher">The publisher to run.</param>
public sealed class OutboxPump(PostgresOutboxPublisher publisher) : BackgroundService
{
    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(publisher);

        return publisher.RunAsync(stoppingToken);
    }
}

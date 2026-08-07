using FlowX.Postgres;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
/// <para>
/// <strong>And it survives the database going away.</strong> A <c>BackgroundService</c> whose
/// <c>ExecuteAsync</c> throws stops the whole host by default, so a PostgreSQL restart — a
/// failover, a patch, `pg_ctl restart` on somebody's laptop — took the API down with it. It was
/// reproducible in one line and it made the readiness probe pointless: a process that exits when
/// the database does can never report "the database is gone", because there is nothing left to
/// ask. So the loop is restarted here instead, and the endpoint that says whether this node
/// should be sent work is left to say it.
/// </para>
/// </remarks>
public sealed partial class CrmOutboxPump : BackgroundService
{
    // Long enough that a failover is not a busy loop against a socket nothing is listening on,
    // short enough that a staged event is not sitting there for a minute after recovery.
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(5);

    private readonly PostgresOutboxPublisher _publisher;
    private readonly ILogger<CrmOutboxPump> _log;

    /// <summary>Creates the pump.</summary>
    /// <param name="publisher">The drain <c>AddFlowXPostgresOutbox</c> registered.</param>
    /// <param name="log">Where an interruption is reported.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public CrmOutboxPump(PostgresOutboxPublisher publisher, ILogger<CrmOutboxPump> log)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(log);

        _publisher = publisher;
        _log = log;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _publisher.RunAsync(stoppingToken).ConfigureAwait(false);

                // A clean return means the token was cancelled: the host is stopping, and
                // looping again would restart a drain nobody is going to read.
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // See the paragraph below the catch.
            catch (Exception failure)
            {
                Interrupted(_log, Backoff.TotalSeconds, failure);
            }
#pragma warning restore CA1031

            // WHY EVERY EXCEPTION AND NOT A LIST. The obvious narrower catch is NpgsqlException,
            // TimeoutException and IOException — and it is a guess. The drain reaches a
            // connection pool, a broker client and whatever an IEventPublisher implementation
            // does; enumerating how each of those fails mid-statement is a list that is wrong the
            // first time somebody swaps one out, and being wrong means the host stops. A drain
            // that retries something genuinely unrecoverable logs once every five seconds, which
            // is loud and harmless. The alternative failure is an API that is down.

            try
            {
                await Task.Delay(Backoff, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "The outbox drain stopped and will restart in {Seconds}s. Staged events wait; nothing is lost.")]
    private static partial void Interrupted(ILogger log, double seconds, Exception failure);
}

using System.Diagnostics.Metrics;
using FlowX.Observability;
using Npgsql;

namespace FlowX.Postgres;

/// <summary>
/// The three gauges in <a href="../../../docs/12-Observability.md">12-Observability</a> §3 whose
/// value lives in the store: <c>flowx_outbox_pending</c>, <c>flowx_outbox_lag_seconds</c> and
/// <c>flowx_flow_active</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Observable instruments over a cached snapshot, not over a query.</strong> A
/// <see cref="Meter"/> collects synchronously, on whichever thread the exporter uses, and a
/// callback that opened a connection and waited for a group-by would stall that thread for as
/// long as the database took — turning a slow store into a stalled metrics pipeline, which is
/// the one component that must keep answering while a store is slow. So
/// <see cref="RunAsync"/> refreshes a snapshot on its own schedule and each callback hands back
/// what it last saw.
/// </para>
/// <para>
/// <strong>A snapshot nobody has refreshed publishes nothing.</strong> Before the first pass
/// every callback yields an empty sequence, so a series appears when it has been measured
/// rather than reading zero because nothing has looked yet. §9's warning — that a metric nobody
/// emits "is indistinguishable from a healthy one" — applies with equal force to a metric that
/// reports a confident zero it has not checked.
/// </para>
/// <para>
/// <strong>A plain loop rather than a hosted service</strong>, for the reason
/// <see cref="PostgresOutboxPublisher.RunAsync"/> is one: this package depends on
/// <c>FlowX.Abstractions</c> and Npgsql (ADR-0009), and a <c>BackgroundService</c> would add a
/// hosting dependency to a plugin for the sake of one <c>while</c>.
/// </para>
/// </remarks>
public sealed class PostgresStoreMetrics : IDisposable
{
    private static readonly Lock Gate = new();
    private static readonly List<PostgresStoreMetrics> Sources = [];

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeSpan _interval;

    private volatile Measurement<long>[] _pending = [];
    private volatile Measurement<double>[] _lag = [];
    private volatile Measurement<long>[] _active = [];

    /// <summary>
    /// Registers the three gauges against the shared FlowX meter, once per process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Static, because an instrument name may be created once on a meter.</strong>
    /// Creating <c>flowx_outbox_pending</c> per instance would publish the same series from two
    /// instruments the moment a process opened a second data source — and an exporter handed a
    /// duplicate instrument either drops one or double-counts, neither of which is a reading.
    /// So the instruments belong to the type and the <em>snapshots</em> belong to the instances,
    /// which is also the shape that makes a second store contribute its rows rather than replace
    /// them.
    /// </para>
    /// <para>
    /// An <see cref="ObservableGauge{T}"/> cannot be disposed — instruments live as long as
    /// their meter — which is the other half of why they are created once here rather than
    /// unregistered in <see cref="Dispose"/>.
    /// </para>
    /// </remarks>
    static PostgresStoreMetrics()
    {
        FlowXTelemetry.Meter.CreateObservableGauge(
            TelemetryNames.OutboxPending,
            static () => Collect(static source => source._pending),
            unit: null,
            "Events staged in the outbox that no publisher has acknowledged.");

        FlowXTelemetry.Meter.CreateObservableGauge(
            TelemetryNames.OutboxLagSeconds,
            static () => Collect(static source => source._lag),
            "s",
            "Age of the oldest unpublished event of each type, from the step commit that staged it.");

        FlowXTelemetry.Meter.CreateObservableGauge(
            TelemetryNames.FlowActive,
            static () => Collect(static source => source._active),
            unit: null,
            "Instances that have not reached a terminal state, by flow and state.");
    }

    /// <summary>Starts contributing gauge readings for one data source.</summary>
    /// <param name="dataSource">
    /// The data source. Shared with the journal and the publisher, and not owned here.
    /// </param>
    /// <param name="refreshInterval">
    /// How often the snapshot is refreshed. Defaults to fifteen seconds, which is under half
    /// the thirty-second window §7 alerts on for outbox freshness — a gauge sampled more slowly
    /// than its own alert window cannot raise it in time.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is null.</exception>
    public PostgresStoreMetrics(NpgsqlDataSource dataSource, TimeSpan? refreshInterval = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
        _interval = refreshInterval ?? TimeSpan.FromSeconds(15);

        lock (Gate)
        {
            Sources.Add(this);
        }
    }

    /// <summary>Refreshes the snapshot once.</summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <remarks>
    /// Public so a test — and an operator's diagnostic endpoint — can take one reading without
    /// waiting out an interval. Three statements on one connection: they are independent
    /// questions and there is no transaction, because a gauge wants each answer as fresh as it
    /// can be rather than all three as of one instant.
    /// </remarks>
    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        _pending = await ReadCountsAsync(
            connection, TelemetrySql.PendingByType, TelemetryNames.TypeLabel, cancellationToken)
            .ConfigureAwait(false);

        _lag = await ReadLagAsync(connection, cancellationToken).ConfigureAwait(false);

        _active = await ReadActiveAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Refreshes the snapshot until cancelled.</summary>
    /// <param name="cancellationToken">Stops the loop.</param>
    /// <remarks>
    /// A pass that throws does not end the loop and does not replace the snapshot: a store that
    /// is briefly unreachable leaves the last known values standing, which is what a gauge
    /// should do — replacing them with zero would report an empty outbox at the moment the
    /// database stopped answering, and that is the reading an operator would most like not to
    /// be given. Cancellation ends the loop without faulting it, as the publisher's does.
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshAsync(cancellationToken).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // A gauge that cannot read is a stale gauge, not a dead process.
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Deliberately swallowed. See the remarks: the previous snapshot stands.
                }
#pragma warning restore CA1031

                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown. Ending quietly, for the reason PostgresOutboxPublisher.RunAsync does.
        }
    }

    /// <summary>Stops this store contributing readings.</summary>
    /// <remarks>
    /// The instruments stay — see the static constructor — and this instance stops being asked.
    /// A host that tears down one store and builds another therefore stops publishing the
    /// first one's numbers immediately, rather than publishing a snapshot nothing refreshes.
    /// </remarks>
    public void Dispose()
    {
        lock (Gate)
        {
            Sources.Remove(this);
        }
    }

    /// <summary>Concatenates one snapshot from every registered store.</summary>
    private static List<Measurement<T>> Collect<T>(
        Func<PostgresStoreMetrics, Measurement<T>[]> snapshot)
        where T : struct
    {
        PostgresStoreMetrics[] sources;

        lock (Gate)
        {
            if (Sources.Count == 0)
            {
                return [];
            }

            sources = [.. Sources];
        }

        // Materialised rather than lazily concatenated: the callback runs on the exporter's
        // thread and a deferred sequence would read the volatile snapshots after the lock is
        // gone, mid-refresh, which is the one moment they are being replaced.
        var measurements = new List<Measurement<T>>();

        foreach (var source in sources)
        {
            measurements.AddRange(snapshot(source));
        }

        return measurements;
    }

    private static async ValueTask<Measurement<long>[]> ReadCountsAsync(
        NpgsqlConnection connection,
        string sql,
        string label,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var measurements = new List<Measurement<long>>();

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closing = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            measurements.Add(new Measurement<long>(
                reader.GetInt64(1),
                new KeyValuePair<string, object?>(label, reader.GetString(0))));
        }

        return [.. measurements];
    }

    private static async ValueTask<Measurement<double>[]> ReadLagAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = TelemetrySql.LagSecondsByType;

        var measurements = new List<Measurement<double>>();

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closing = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // EXTRACT(EPOCH FROM interval) is numeric in PostgreSQL, so it arrives as a decimal
            // rather than a double. Read as the type it is and converted once, because asking
            // Npgsql for a double here throws InvalidCastException on the first pending event —
            // which would be a gauge that works until the moment it is needed.
            measurements.Add(new Measurement<double>(
                (double)reader.GetDecimal(1),
                new KeyValuePair<string, object?>(TelemetryNames.TypeLabel, reader.GetString(0))));
        }

        return [.. measurements];
    }

    private static async ValueTask<Measurement<long>[]> ReadActiveAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = TelemetrySql.ActiveByFlowAndState;

        var measurements = new List<Measurement<long>>();

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closing = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            measurements.Add(new Measurement<long>(
                reader.GetInt64(2),
                new KeyValuePair<string, object?>(TelemetryNames.FlowLabel, reader.GetString(0)),
                new KeyValuePair<string, object?>(TelemetryNames.StateLabel, reader.GetString(1))));
        }

        return [.. measurements];
    }
}

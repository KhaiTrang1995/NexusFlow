using System.Diagnostics;
using FlowX.Observability;

namespace FlowX.Hosting;

/// <summary>
/// Times every <see cref="IFlowJournal"/> call against
/// <see cref="TelemetryNames.JournalCommitSeconds"/>, whichever store is underneath.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A decorator, so both adapters are timed by one piece of code.</strong>
/// <a href="../../../docs/12-Observability.md">12-Observability</a> §3 says the subject of this
/// histogram is "<c>IFlowJournal</c>, both adapters", and §7 gives it an SLO — p99 commit under
/// 15 ms, alerting above 50 ms for five minutes. Instrumenting the Postgres adapter would have
/// left the in-memory one silent and would have put the same stopwatch into the next store
/// somebody writes; instrumenting the contract times a third-party journal on the day it is
/// registered.
/// </para>
/// <para>
/// <strong>Applied where the host resolves durability</strong>, which
/// <c>FlowXServiceCollectionExtensions</c> already documents as the place "a store wrapped in
/// decorators" belongs. An application that builds its own <see cref="FlowDurability"/> and
/// registers it wins, exactly as before, and is then responsible for its own wrapping — which
/// is the same bargain that registration already struck.
/// </para>
/// <para>
/// <strong>Refusals are timed and not counted separately.</strong> A fenced-out commit is a
/// working store answering quickly, and it is the store's latency this histogram is about; an
/// error rate on the journal would be a different metric, and §3 does not specify one.
/// </para>
/// </remarks>
public sealed class JournalTelemetry : IFlowJournal
{
    private readonly IFlowJournal _inner;

    /// <summary>Times every call made through <paramref name="inner"/>.</summary>
    /// <param name="inner">The store underneath.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
    public JournalTelemetry(IFlowJournal inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>
    /// Wraps <paramref name="journal"/>, or returns it unchanged when nothing is listening.
    /// </summary>
    /// <remarks>
    /// Decided once, at start-up, rather than per call: a journal call is a store round trip,
    /// so the wrapper's cost is not the thing to optimise — but a host that will never export a
    /// metric should not carry an extra virtual call and an extra frame in every stack trace it
    /// ever prints.
    /// </remarks>
    /// <param name="journal">The store to time.</param>
    /// <returns>A timing decorator, or <paramref name="journal"/> itself.</returns>
    public static IFlowJournal Wrap(IFlowJournal journal) =>
        FlowXMetrics.JournalCommit.Enabled ? new JournalTelemetry(journal) : journal;

    /// <inheritdoc />
    public ValueTask<Result<FlowInstanceRecord>> StartAsync(
        FlowInstanceStart start, CancellationToken cancellationToken) =>
        TimeAsync(nameof(StartAsync), _inner.StartAsync(start, cancellationToken));

    /// <inheritdoc />
    public ValueTask<Result<FencingToken>> FenceAsync(
        Guid instanceId, FencingToken token, CancellationToken cancellationToken) =>
        TimeAsync(nameof(FenceAsync), _inner.FenceAsync(instanceId, token, cancellationToken));

    /// <inheritdoc />
    public ValueTask<Result<JournalStep>> CommitAsync(
        StepCommit commit, CancellationToken cancellationToken) =>
        TimeAsync(nameof(CommitAsync), _inner.CommitAsync(commit, cancellationToken));

    /// <inheritdoc />
    public ValueTask<Result<FlowInstanceRecord>> CompleteAsync(
        Guid instanceId,
        FencingToken token,
        FlowInstanceState state,
        JournalPayload stateBag,
        FlowWake? wake,
        CancellationToken cancellationToken) =>
        TimeAsync(
            nameof(CompleteAsync),
            _inner.CompleteAsync(instanceId, token, state, stateBag, wake, cancellationToken));

    /// <inheritdoc />
    public ValueTask<Result<FlowInstanceRecord>> ReadInstanceAsync(
        Guid instanceId, CancellationToken cancellationToken) =>
        TimeAsync(nameof(ReadInstanceAsync), _inner.ReadInstanceAsync(instanceId, cancellationToken));

    /// <inheritdoc />
    public ValueTask<Result<ResumeFrontier>> ReadResumeFrontierAsync(
        Guid instanceId, CancellationToken cancellationToken) =>
        TimeAsync(
            nameof(ReadResumeFrontierAsync), _inner.ReadResumeFrontierAsync(instanceId, cancellationToken));

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<OutboxRecord>>> ReadOutboxAsync(
        Guid instanceId, CancellationToken cancellationToken) =>
        TimeAsync(nameof(ReadOutboxAsync), _inner.ReadOutboxAsync(instanceId, cancellationToken));

    /// <summary>
    /// Awaits <paramref name="call"/> and records how long it took under
    /// <paramref name="operation"/>.
    /// </summary>
    /// <remarks>
    /// The inner call is started by the caller and passed in already running, so this adds an
    /// await and no extra branch on the store's own path. A store that answers synchronously —
    /// a cached instance read — still completes synchronously through here.
    /// </remarks>
    private static async ValueTask<T> TimeAsync<T>(string operation, ValueTask<T> call)
    {
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            return await call.ConfigureAwait(false);
        }
        finally
        {
            FlowXMetrics.JournalCommit.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalSeconds,
                new KeyValuePair<string, object?>(TelemetryNames.OperationLabel, operation));
        }
    }
}

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
        FlowXMetrics.JournalCommit.Enabled || FlowXLog.IsEnabled
            ? new JournalTelemetry(journal)
            : journal;

    /// <inheritdoc />
    /// <remarks>
    /// The null check is new and is not defensive noise: this decorator now reads the instance
    /// off the write so a §4 record can name it, so a null argument would fault here rather than
    /// inside the store, one frame further from the caller that supplied it.
    /// </remarks>
    public ValueTask<Result<FlowInstanceRecord>> StartAsync(
        FlowInstanceStart start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);

        return ObserveAsync(nameof(StartAsync), start.InstanceId, _inner.StartAsync(start, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<Result<FencingToken>> FenceAsync(
        Guid instanceId, FencingToken token, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(FenceAsync), instanceId, _inner.FenceAsync(instanceId, token, cancellationToken));

    /// <inheritdoc />
    /// <remarks>Guarded for the reason <see cref="StartAsync"/> is.</remarks>
    public ValueTask<Result<JournalStep>> CommitAsync(
        StepCommit commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);

        return ObserveAsync(
            nameof(CommitAsync), commit.Key.InstanceId, _inner.CommitAsync(commit, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<Result<FlowInstanceRecord>> CompleteAsync(
        Guid instanceId,
        FencingToken token,
        FlowInstanceState state,
        JournalPayload stateBag,
        FlowWake? wake,
        CancellationToken cancellationToken) =>
        ObserveAsync(
            nameof(CompleteAsync),
            instanceId,
            _inner.CompleteAsync(instanceId, token, state, stateBag, wake, cancellationToken));

    /// <inheritdoc />
    public ValueTask<Result<FlowInstanceRecord>> ReadInstanceAsync(
        Guid instanceId, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(ReadInstanceAsync), instanceId, _inner.ReadInstanceAsync(instanceId, cancellationToken));

    /// <inheritdoc />
    public ValueTask<Result<ResumeFrontier>> ReadResumeFrontierAsync(
        Guid instanceId, CancellationToken cancellationToken) =>
        ObserveAsync(
            nameof(ReadResumeFrontierAsync),
            instanceId,
            _inner.ReadResumeFrontierAsync(instanceId, cancellationToken));

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<OutboxRecord>>> ReadOutboxAsync(
        Guid instanceId, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(ReadOutboxAsync), instanceId, _inner.ReadOutboxAsync(instanceId, cancellationToken));

    /// <summary>
    /// Awaits <paramref name="call"/>, records how long it took under
    /// <paramref name="operation"/>, and writes the store's answer as a §4 record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The inner call is started by the caller and passed in already running, so this adds an
    /// await and no extra branch on the store's own path. A store that answers synchronously —
    /// a cached instance read — still completes synchronously through here.
    /// </para>
    /// <para>
    /// <strong>Generic over the <see cref="Result{T}"/>'s value rather than over the whole
    /// answer</strong>, which is what lets one helper tell a refusal from an answer. §4 wants
    /// the refusals in particular: a fenced-out commit is how an operator learns a lease moved,
    /// and it is invisible in the histogram above — where a refusal is just a working store
    /// answering quickly, as this type's own remarks say.
    /// </para>
    /// <para>
    /// A thrown store — a dropped connection — is left to propagate untouched and unlogged. It
    /// is not a refusal the store expressed, the caller sees the exception, and inventing an
    /// error code for it here would put a code in the logs that no error catalogue contains.
    /// </para>
    /// </remarks>
    private static async ValueTask<Result<TValue>> ObserveAsync<TValue>(
        string operation, Guid instanceId, ValueTask<Result<TValue>> call)
    {
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            var result = await call.ConfigureAwait(false);

            FlowXLog.WriteJournalCall(
                operation,
                instanceId,
                result.IsFailure ? result.Error.Code : null,
                result.IsFailure ? result.Error.Category.ToString() : null);

            return result;
        }
        finally
        {
            if (FlowXMetrics.JournalCommit.Enabled)
            {
                FlowXMetrics.JournalCommit.Record(
                    Stopwatch.GetElapsedTime(startedAt).TotalSeconds,
                    new KeyValuePair<string, object?>(TelemetryNames.OperationLabel, operation));
            }
        }
    }
}

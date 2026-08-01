using System.Diagnostics.Metrics;
using FlowX.Observability;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// The rows of <c>docs/10-Policy-Framework.md §9</c> that have a subject: a retry that
/// attempts, a breaker that opens, a bulkhead that refuses and a timeout that fires.
/// </summary>
/// <remarks>
/// <para>
/// <strong>§9's warning box is what this file exists to delete.</strong> It read "No metric in
/// this table is emitted… a policy that now genuinely opens a breaker or spends two retries
/// does so silently." Every assertion below is of the form "an operator would have seen this",
/// and each names the event that would have paged them.
/// </para>
/// <para>
/// <strong>The assertions are on the measurement, not on the instrument.</strong> An
/// instrument that exists and is never written to publishes an empty series, which
/// <c>FlowXMetrics</c>'s own remarks call indistinguishable from a healthy one — so a test
/// that only checked an instrument had been created would pass against exactly the silence
/// being fixed.
/// </para>
/// </remarks>
public sealed class PolicyMetricsTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Error Unavailable =
        new("order.validate_failed", "the ledger is down", ErrorCategory.Unavailable);

    private static ExecutionPlan Plan(PolicyChain forward) =>
        ExecutionPlan.Create(
            FlowDescriptor.Create("order.policy", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(0, Plans.Validate, policies: forward),
            ]));

    private static PolicyChain Forward(PolicySet set) => PolicyChain.ForStep(set, Plans.Validate);

    /// <summary>
    /// A retry that spends a second attempt is counted, once per attempt beyond the first.
    /// </summary>
    /// <remarks>
    /// The denominator matters more than the count. §9 gives <c>flowx_retry_attempts_total</c>
    /// an <c>attempt</c> label precisely so a dashboard can tell "many steps retried once"
    /// from "one step retried ten times" — the second is a dependency that is down, the first
    /// is a dependency that is flaky, and they are different pages.
    /// </remarks>
    [Fact]
    public async Task ARetryThatAttemptsAgainIsCountedOncePerExtraAttempt()
    {
        using var recorder = new MetricRecorder();

        var dispatcher = new RecordingDispatcher().FailAt(0, Unavailable);

        await new FlowEngine(new FakeClock(T0)).ExecuteAsync(
            Plan(Forward(PolicySet.Named("r").Retry(attempts: 3))),
            dispatcher,
            Plans.Invocation,
            Ct);

        var attempts = recorder.Counter(TelemetryNames.RetryAttemptsTotal);

        attempts.Count.ShouldBe(
            2,
            "Three attempts were declared and all three failed, so two of them were retries. " +
            "The first dispatch is not a retry — counting it would make every step that ever " +
            "ran look like it had been retried.");

        attempts.ShouldAllBe(m => m.Tag(TelemetryNames.CapabilityLabel) == Plans.Validate.Id);

        attempts.ShouldAllBe(m => m.Tag(TelemetryNames.ErrorCodeLabel) == Unavailable.Code);

        attempts.Select(m => m.Tag(TelemetryNames.AttemptLabel))
            .ShouldBe(["2", "3"], "The attempt label is the attempt being armed.");
    }

    /// <summary>A breaker that opens publishes the state an operator is paged on.</summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the event named in the brief.</strong> A breaker opening is a
    /// dependency being taken out of service by FlowX rather than by a human, and until this
    /// assertion passed it happened with no external evidence of any kind.
    /// </para>
    /// <para>
    /// The gauge is asserted at 2 — <c>docs/10 §9</c>'s "gauge (0/1/2)" — and the closed
    /// readings before it are asserted too, because a gauge that only ever publishes on the
    /// way open cannot be alerted on with a <c>== 2</c> rule that recovers.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ABreakerThatOpensPublishesItsStateAsAGauge()
    {
        using var recorder = new MetricRecorder();

        var clock = new FakeClock(T0);
        var engine = new FlowEngine(clock);

        var plan = Plan(Forward(PolicySet.Named("b")
            .CircuitBreaker(failureRatio: 0.5, breakDuration: TimeSpan.FromSeconds(30))));

        for (var run = 0; run < StepPolicy.DefaultMinimumThroughput + 2; run++)
        {
            await engine.ExecuteAsync(
                plan, new RecordingDispatcher().FailAt(0, Unavailable), Plans.Invocation, Ct);
        }

        var states = recorder.Gauge(TelemetryNames.CircuitState);

        states.ShouldNotBeEmpty("A breaker that changes state and says nothing is the gap.");

        states.Select(m => m.Value).ShouldContain(
            PolicyMetrics.CircuitOpen,
            "Every call failed against a 0.5 ratio, so the breaker opened. 2 is the value " +
            "docs/10 §9 gives 'open'.");

        states.Select(m => m.Value).ShouldContain(
            PolicyMetrics.CircuitClosed,
            "and it was closed before it opened. A gauge whose only value is 2 cannot " +
            "distinguish a recovered breaker from an exporter that stopped scraping.");

        states.ShouldAllBe(m => m.Tag(TelemetryNames.CapabilityLabel) == Plans.Validate.Id);
    }

    /// <summary>A bulkhead that refuses a caller is counted as a refused invocation.</summary>
    /// <remarks>
    /// The refusal, not the permit. A bulkhead is invisible until it turns somebody away, and
    /// the turning away is the operator's signal that the pool is too small for the load —
    /// which is the same signal whether the dependency is healthy or not.
    /// </remarks>
    [Fact]
    public async Task ABulkheadThatRefusesACallerIsCountedWithARejectedOutcome()
    {
        using var recorder = new MetricRecorder();

        var engine = new FlowEngine(new FakeClock(T0));

        var plan = Plan(Forward(PolicySet.Named("h")
            .Bulkhead(maxConcurrency: 1, queueDepth: 0)));

        // One permit, no queue, and two callers held inside the capability at once: the second
        // has nowhere to go and is refused, which is the only state a bulkhead is visible in.
        using var admitted = new SemaphoreSlim(0, 1);
        using var release = new SemaphoreSlim(0, 1);

        var holder = new RecordingDispatcher();
        holder.Observe = _ =>
        {
            admitted.Release();
            release.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
            return null;
        };

        // On its own thread, and that is load-bearing rather than tidy. A RecordingDispatcher
        // that blocks completes synchronously, so the engine's ValueTask never yields and the
        // whole execution would run on this thread — the permit would be taken and given back
        // before the line below, and the second caller would find the pool empty.
        var held = Task.Run(
            async () => await engine.ExecuteAsync(plan, holder, Plans.Invocation, Ct).ConfigureAwait(false),
            Ct);

        await admitted.WaitAsync(Ct);

        var refused = await engine.ExecuteAsync(
            plan, new RecordingDispatcher(), Plans.Invocation, Ct);

        release.Release();
        await held;

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Code.ShouldBe(FlowErrors.BulkheadRejectedCode);

        var invocations = recorder.Counter(TelemetryNames.PolicyInvocationsTotal)
            .Where(m => m.Tag(TelemetryNames.PolicyLabel) == StepPolicy.BulkheadKind)
            .ToList();

        invocations.ShouldNotBeEmpty();

        invocations.ShouldContain(
            m => m.Tag(TelemetryNames.OutcomeLabel) == PolicyMetrics.RejectedOutcome,
            "The second caller was turned away. That is what the bulkhead did, and it is the " +
            "only thing about a bulkhead worth a metric.");

        invocations.ShouldAllBe(m => m.Tag(TelemetryNames.StageLabel) == nameof(PolicyStage.Resilience));
    }

    /// <summary>A timeout that fires is counted as a timed-out invocation.</summary>
    /// <remarks>
    /// A zero-length timeout is degenerate on purpose, exactly as
    /// <c>PolicyExecutionTests</c> uses one: it has expired before the step begins, so the
    /// refusal is unambiguous and the assertion cannot pass because a machine was slow.
    /// </remarks>
    [Fact]
    public async Task ATimeoutThatFiresIsCountedWithATimedOutOutcome()
    {
        using var recorder = new MetricRecorder();

        var result = await new FlowEngine(new FakeClock(T0)).ExecuteAsync(
            Plan(Forward(PolicySet.Named("t").Timeout(TimeSpan.Zero))),
            new RecordingDispatcher(),
            Plans.Invocation,
            Ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(FlowErrors.StepTimedOutCode);

        recorder.Counter(TelemetryNames.PolicyInvocationsTotal).ShouldContain(
            m => m.Tag(TelemetryNames.PolicyLabel) == StepPolicy.TimeoutKind
                 && m.Tag(TelemetryNames.OutcomeLabel) == PolicyMetrics.TimedOutOutcome
                 && m.Tag(TelemetryNames.CapabilityLabel) == Plans.Validate.Id,
            "docs/10 §9's flowx_policy_invocations_total is labelled by policy, stage, " +
            "capability and outcome, and a timeout firing is the outcome it exists to report.");
    }

    /// <summary>A policy that applied cleanly is counted too, with an ok outcome.</summary>
    /// <remarks>
    /// The denominator. A counter that only increments on refusal cannot answer "what fraction
    /// of calls did this breaker refuse", which is the question §9's <c>outcome</c> label is
    /// for — and a rate of refusals with no rate of admissions is a number with no scale.
    /// </remarks>
    [Fact]
    public async Task APolicyThatAppliedCleanlyIsCountedWithAnOkOutcome()
    {
        using var recorder = new MetricRecorder();

        var result = await new FlowEngine(new FakeClock(T0)).ExecuteAsync(
            Plan(Forward(PolicySet.Named("t").Timeout(TimeSpan.FromSeconds(10)))),
            new RecordingDispatcher(),
            Plans.Invocation,
            Ct);

        result.IsSuccess.ShouldBeTrue();

        recorder.Counter(TelemetryNames.PolicyInvocationsTotal).ShouldContain(
            m => m.Tag(TelemetryNames.PolicyLabel) == StepPolicy.TimeoutKind
                 && m.Tag(TelemetryNames.OutcomeLabel) == PolicyMetrics.OkOutcome,
            "A timeout that was armed and not reached still applied to the call.");
    }

    // ------------------------------------------------------------------ the recorder

    /// <summary>
    /// Reads what an exporter would have seen, for the instruments on FlowX's own meter.
    /// </summary>
    /// <remarks>
    /// <c>GaugeRecorder</c> in <c>FlowX.Postgres.Tests</c> is the same idea for observable
    /// instruments, which have to be pumped with
    /// <see cref="MeterListener.RecordObservableInstruments"/>. Everything here is written
    /// synchronously by the engine, so the callback fires as the measurement is taken and
    /// there is nothing to pump.
    /// </remarks>
    private sealed class MetricRecorder : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<Recorded> _taken = [];
        private readonly Lock _sync = new();

        public MetricRecorder()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = static (instrument, listener) =>
                {
                    if (instrument.Meter.Name == FlowXTelemetry.SourceName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };

            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                lock (_sync)
                {
                    _taken.Add(new Recorded(instrument.Name, value, tags.ToArray()));
                }
            });

            _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
            {
                lock (_sync)
                {
                    _taken.Add(new Recorded(instrument.Name, value, tags.ToArray()));
                }
            });

            _listener.Start();
        }

        public IReadOnlyList<Recorded> Counter(string instrument) => Read(instrument);

        public IReadOnlyList<Recorded> Gauge(string instrument) => Read(instrument);

        private IReadOnlyList<Recorded> Read(string instrument)
        {
            lock (_sync)
            {
                return [.. _taken.Where(m => string.Equals(m.Name, instrument, StringComparison.Ordinal))];
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>One measurement, with its tags flattened for assertion.</summary>
    private sealed record Recorded(string Name, long Value, KeyValuePair<string, object?>[] Tags)
    {
        public string? Tag(string name) =>
            Tags.FirstOrDefault(t => string.Equals(t.Key, name, StringComparison.Ordinal)).Value as string;
    }
}

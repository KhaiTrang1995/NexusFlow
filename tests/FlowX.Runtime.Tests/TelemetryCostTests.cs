using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using FlowX.Observability;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// Budget <strong>B6</strong> — <em>telemetry with no listener costs 0 ns and 0 B per step</em> —
/// asserted as a unit test, the way <c>EngineAllocationTests</c> asserts B2.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A correctness property, not a benchmark.</strong> B6 is a hard zero in
/// <a href="../../docs/14-Performance.md">14-Performance</a>, and a hard zero is the one
/// performance property that can be gated without a harness: allocation counts are
/// deterministic while nanoseconds on a shared runner are not.
/// <a href="../../docs/21-Quality-Gates.md">21-Quality-Gates</a> §7 lists B6 under "no harness",
/// and this does not build one — it asserts the half of B6 that is checkable exactly, on every
/// pull request, in milliseconds.
/// </para>
/// <para>
/// <strong>The zero has two halves and both are here.</strong> With nothing listening the host
/// installs no decorator at all — <see cref="TheDispatcherIsNotWrappedWhenNothingIsListening"/> —
/// so the ordinary answer is that there is no telemetry code on the step path to cost anything.
/// That alone would be a zero that depends on a decision made one layer up, so
/// <see cref="AnInstalledDecoratorStillCostsNothingWithNoListener"/> forces the decorator into
/// existence and measures a dispatch through it. Both must hold: the first is what happens, the
/// second is what stops a future change to the first from quietly costing every step.
/// </para>
/// <para>
/// <strong>Release only</strong>, skipped rather than failed in Debug, for the reason
/// <c>EngineAllocationTests</c> gives: the C# compiler emits an async state machine as a class in
/// Debug and as a struct in Release, so a Debug run measures Edit-and-Continue scaffolding and
/// reports it as this code's allocation.
/// </para>
/// </remarks>
public sealed class TelemetryCostTests
{
    private static void RequireOptimisedBuild()
    {
#if DEBUG
        Assert.Skip(
            "Allocation budgets are measured in Release only. In Debug the compiler emits " +
            "async state machines as classes, which shows up as a few hundred bytes per " +
            "dispatch that this code does not allocate. Run: dotnet test -c Release");
#endif
    }

    /// <summary>
    /// The positive control. Without it every zero below could be a broken measurement.
    /// </summary>
    [Fact]
    public void TheMeasurementCanDetectAnAllocationItShouldSee()
    {
        Measure(static () => _ = new object()).ShouldBeGreaterThan(
            0,
            "If this reports zero, the harness is broken and every other assertion in this " +
            "class is meaningless.");
    }

    /// <summary>
    /// With no listener, <see cref="StepTelemetry.Wrap"/> hands back the caller's own dispatcher.
    /// </summary>
    /// <remarks>
    /// Reference equality, because it is the only evidence of "no telemetry on this path" that
    /// costs nothing to produce. An unobserved flow therefore dispatches to exactly the object it
    /// would have dispatched to before any of this existed — no extra virtual call, no extra
    /// frame in a stack trace, and nothing per step to measure.
    /// </remarks>
    [Fact]
    public void TheDispatcherIsNotWrappedWhenNothingIsListening()
    {
        StepTelemetry.IsEnabled.ShouldBeFalse(
            "Another test in this assembly has left a listener attached, which makes every " +
            "measurement here meaningless.");

        var dispatcher = new NullDispatcher();

        StepTelemetry.Wrap(Plans.FourStepSaga(), Plans.Invocation, dispatcher)
            .ShouldBeSameAs(dispatcher);
    }

    /// <summary>Deciding not to wrap allocates nothing.</summary>
    /// <remarks>
    /// The decision runs once per flow, and a <c>HasListeners()</c> that boxed or a
    /// <c>TenantLabel</c> that allocated would put bytes on every unobserved execution — small,
    /// per flow rather than per step, and exactly the kind of cost that is never noticed because
    /// nobody thought to look at the code that does nothing.
    /// </remarks>
    [Fact]
    public void DecidingNotToInstrumentAllocatesNothing()
    {
        RequireOptimisedBuild();

        var plan = Plans.FourStepSaga();
        var dispatcher = new NullDispatcher();

        Measure(() =>
        {
            _ = StepTelemetry.IsEnabled;
            _ = StepTelemetry.Wrap(plan, Plans.Invocation, dispatcher);
            _ = FlowXTelemetry.TenantLabel("acme");
        })
        .ShouldBe(0, "B6: telemetry with no listener costs 0 B.");
    }

    /// <summary>
    /// Every emit site is guarded by a check that is false, and reading that check allocates
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the shape B6 is actually about.</strong> <c>Counter.Add(value, tags)</c>
    /// is cheap with no listener, but building the tags to hand it is not: a label value that is
    /// not already a <see cref="string"/> boxes on its way into a
    /// <see cref="KeyValuePair{TKey,TValue}"/>, so the tag list must not be built before the
    /// check. <see cref="Instrument.Enabled"/> is what makes "do not build it" expressible.
    /// </para>
    /// <para>
    /// The same applies to <see cref="ActivitySource.StartActivity(string, ActivityKind)"/>,
    /// which returns <c>null</c> with no listener — asserted here because every span in this
    /// package is built by calling it first and setting tags only on the non-null result.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryInstrumentReportsItselfDisabledAndReadingThatAllocatesNothing()
    {
        RequireOptimisedBuild();

        FlowXMetrics.FlowDuration.Enabled.ShouldBeFalse();
        FlowXMetrics.FlowTotal.Enabled.ShouldBeFalse();
        FlowXMetrics.StepDuration.Enabled.ShouldBeFalse();
        FlowXMetrics.CapabilityDuration.Enabled.ShouldBeFalse();
        FlowXMetrics.CapabilityUnhandled.Enabled.ShouldBeFalse();
        FlowXMetrics.JournalCommit.Enabled.ShouldBeFalse();
        FlowXMetrics.LeaseLost.Enabled.ShouldBeFalse();
        FlowXMetrics.CompensationFailed.Enabled.ShouldBeFalse();

        FlowXTelemetry.Source.HasListeners().ShouldBeFalse();

        Measure(static () =>
        {
            _ = FlowXMetrics.StepDuration.Enabled;
            _ = FlowXMetrics.CapabilityDuration.Enabled;
            _ = FlowXMetrics.CapabilityUnhandled.Enabled;
            _ = FlowXTelemetry.Source.HasListeners();
            _ = FlowXTelemetry.Source.StartActivity("step 0 order.validate", ActivityKind.Internal);
        })
        .ShouldBe(
            0,
            "StartActivity returns null with no listener, and Instrument.Enabled reads a " +
            "field. Building a tag list before either would be the shape that costs 0 ns and " +
            "several hundred bytes.");
    }

    /// <summary>
    /// The policy instruments are disabled too, and a policy that reports a decision to
    /// nobody allocates nothing to do it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>B6 for <c>docs/10 §9</c>, and it is the half that could most easily have been
    /// lost.</strong> Every one of these four helpers takes at least three labels, and a label
    /// reaches an instrument as a <see cref="KeyValuePair{TKey,TValue}"/> of
    /// <c>string</c> to <c>object?</c> — so a helper that built its tags before asking whether
    /// anybody was listening would box the <see cref="int"/> in
    /// <see cref="PolicyMetrics.CircuitChanged"/> and
    /// <see cref="PolicyMetrics.BulkheadQueued"/> on every breaker transition and every
    /// queued caller, while correctly reporting <c>Enabled = false</c>.
    /// </para>
    /// <para>
    /// Measured through the helpers rather than through a policed execution on purpose. A
    /// policed step allocates for reasons that have nothing to do with telemetry — a linked
    /// <see cref="CancellationTokenSource"/> for the timeout, an async state machine for the
    /// bulkhead's wait — so a measurement around the whole path could not tell a tag list from
    /// a token source, and would go green the day the metrics started costing something.
    /// </para>
    /// </remarks>
    [Fact]
    public void APolicyReportingADecisionToNobodyAllocatesNothing()
    {
        RequireOptimisedBuild();

        PolicyMetrics.Invocations.Enabled.ShouldBeFalse();
        PolicyMetrics.RetryAttempts.Enabled.ShouldBeFalse();
        PolicyMetrics.CircuitState.Enabled.ShouldBeFalse();
        PolicyMetrics.BulkheadQueueDepth.Enabled.ShouldBeFalse();

        PolicyMetrics.IsEnabled.ShouldBeFalse();

        Measure(static () =>
        {
            PolicyMetrics.Applied("Timeout", "Resilience", "order.validate", PolicyMetrics.OkOutcome);
            PolicyMetrics.Retried("order.validate", 2, "order.validate_failed");
            PolicyMetrics.CircuitChanged("order.validate", PolicyMetrics.CircuitOpen);
            PolicyMetrics.BulkheadQueued("order.validate", 3);
        })
        .ShouldBe(
            0,
            "A breaker opening, a retry attempting and a bulkhead queueing are the three " +
            "events docs/10 §9 exists to publish, and with no exporter attached all three " +
            "must cost exactly what they cost before anything published them.");
    }

    /// <summary>
    /// Every §4 log event reports itself disabled, and writing one to nobody allocates nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>B6 for the third pillar, and it is the half most likely to have been lost.</strong>
    /// <see cref="DiagnosticSource.Write"/> is cheap with no subscriber — but its
    /// <em>payload</em> is a <see cref="FlowLogRecord"/>, which is an allocation, and C#
    /// evaluates an argument before the call that would have discarded it. A helper written as
    /// <c>Write(name, new FlowLogRecord(…))</c> would allocate a record per step of every
    /// unobserved flow and hand it to a listener list that is empty, while correctly reporting
    /// <c>IsEnabled = false</c> to anyone who asked.
    /// </para>
    /// <para>
    /// <strong>That is the same defect this file already caught once.</strong>
    /// <c>StepTelemetry.StartSpan</c> was written <c>StartActivity($"step {i} {id}")</c>, which
    /// measured 68 B per step for a string that was thrown away, because the interpolation ran
    /// before the call returned null. The shape is identical here and one layer more expensive,
    /// so it is measured rather than argued.
    /// </para>
    /// <para>
    /// Measured through the emit helpers rather than through a flow, for the reason
    /// <see cref="APolicyReportingADecisionToNobodyAllocatesNothing"/> gives: a measurement
    /// around a whole execution cannot tell a discarded record from anything else the path
    /// allocates, and would go green the day the logs started costing something.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryLogEventReportsItselfDisabledAndWritingOneAllocatesNothing()
    {
        RequireOptimisedBuild();

        FlowXLog.IsEnabled.ShouldBeFalse(
            "Another test in this assembly has left a log subscriber attached, which makes " +
            "every measurement here meaningless.");

        FlowXLog.IsEnabledFor(FlowXLog.FlowStarted).ShouldBeFalse();
        FlowXLog.IsEnabledFor(FlowXLog.FlowCompleted).ShouldBeFalse();
        FlowXLog.IsEnabledFor(FlowXLog.StepCompleted).ShouldBeFalse();
        FlowXLog.IsEnabledFor(FlowXLog.StepCompensated).ShouldBeFalse();
        FlowXLog.IsEnabledFor(FlowXLog.JournalCalled).ShouldBeFalse();
        FlowXLog.IsEnabledFor(FlowXLog.JournalRefused).ShouldBeFalse();

        var instanceId = Guid.NewGuid();

        Measure(() =>
        {
            FlowXLog.WriteFlowStarted("order.place", "1.2.0", "Durable", "acme", "corr-1");

            FlowXLog.WriteFlowCompleted(
                "order.place", "1.2.0", "Durable", instanceId, "acme", "corr-1",
                "Failure", "payment.declined", "Conflict");

            FlowXLog.WriteStepCompleted(
                "order.place", 2, "payment.capture", "2.1.0", null, "acme",
                "Failure", "payment.declined", "Conflict", JournalPayload.Empty);

            FlowXLog.WriteStepCompensated(
                "order.place", 2, "payment.refund", null, "acme", "Success", null, null);

            FlowXLog.WriteJournalCall("CommitAsync", instanceId, null, null);
            FlowXLog.WriteJournalCall("CommitAsync", instanceId, "journal.fenced", "Conflict");
        })
        .ShouldBe(
            0,
            "B6: with no subscriber, a log event must cost exactly what it cost before logs " +
            "existed. A record built before the IsEnabled check — or a Guid formatted into an " +
            "instance id before it — allocates on every step of every unobserved flow and is " +
            "then dropped, which is the defect this file caught on an interpolated span name.");
    }

    /// <summary>
    /// A log subscriber that goes away leaves nothing behind on the step path.
    /// </summary>
    /// <remarks>
    /// The stronger half, and the mirror of
    /// <see cref="AnInstalledDecoratorStillCostsNothingWithNoListener"/> for §4. A subscriber is
    /// attached, the decorator is built while it is — so <see cref="StepTelemetry.Wrap"/> really
    /// does wrap, on the strength of the log listener alone and with no span or metric listener
    /// anywhere — and the subscription is then disposed and four steps are measured. What is
    /// left is the cost of being an observed-then-unobserved process, which is what a host that
    /// reconfigures logging at run time actually is.
    /// </remarks>
    [Fact]
    public void ALogSubscriberThatGoesAwayCostsNothingPerStep()
    {
        RequireOptimisedBuild();

        var plan = Plans.FourStepSaga();
        var inner = new NullDispatcher();

        IStepDispatcher decorated;

        using (FlowXLog.Subscribe(static (_, _) => { }))
        {
            StepTelemetry.IsEnabled.ShouldBeTrue(
                "a log subscriber alone must be reason enough to instrument the step boundary; " +
                "otherwise a host that bridges logs and exports nothing else gets no records.");

            decorated = StepTelemetry.Wrap(plan, Plans.Invocation, inner, Guid.NewGuid());

            decorated.ShouldNotBeSameAs(
                inner, "with a subscriber attached the dispatcher must be wrapped.");
        }

        StepTelemetry.IsEnabled.ShouldBeFalse("the subscription was disposed.");

        var engine = new FlowEngine(new UnixEpochClock());

        Measure(() => Complete(engine.ExecuteAsync(plan, decorated, Plans.Invocation)))
            .ShouldBe(
                0,
                "B6: four steps dispatched through an installed decorator with no log " +
                "subscriber must allocate exactly nothing — no record, no formatted instance " +
                "id, and no payload described for a listener that is not there.");
    }

    /// <summary>
    /// A decorator that <em>is</em> installed still costs nothing per step once the listener
    /// goes away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stronger half of B6, and the one that is not a consequence of a decision made
    /// elsewhere. The decorator is built while a listener is attached, the listener is then
    /// disposed, and a dispatch is measured: what is left is the code that runs on every step of
    /// an observed-then-unobserved process, which is also the code that would run on every step
    /// if <see cref="StepTelemetry.Wrap"/> ever stopped short-circuiting.
    /// </para>
    /// <para>
    /// It also pins that <see cref="StepTelemetry.ExecuteAsync"/> completes synchronously when
    /// the step does. An <c>async ValueTask</c> that suspends boxes its state machine, so a
    /// decorator that gratuitously went asynchronous would cost an allocation per step while
    /// reporting <c>Enabled = false</c> on every instrument.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnInstalledDecoratorStillCostsNothingWithNoListener()
    {
        RequireOptimisedBuild();

        var plan = Plans.FourStepSaga();
        var inner = new NullDispatcher();

        IStepDispatcher decorated;

        using (var listener = Listening())
        {
            decorated = StepTelemetry.Wrap(plan, Plans.Invocation, inner, Guid.NewGuid());

            decorated.ShouldNotBeSameAs(
                inner, "with a listener attached the dispatcher must be wrapped.");
        }

        StepTelemetry.IsEnabled.ShouldBeFalse("the listener was disposed.");

        // Through the real engine, which is what makes this the same measurement B2 is. A
        // four-step ephemeral saga is EngineAllocationTests' own subject, so a non-zero here
        // is either the decorator's or a regression B2 would have caught anyway — and either
        // way it is the number 14-Performance calls a hard zero.
        var engine = new FlowEngine(new UnixEpochClock());

        Measure(() => Complete(engine.ExecuteAsync(plan, decorated, Plans.Invocation)))
            .ShouldBe(
                0,
                "B6: four steps dispatched through an installed decorator with nothing " +
                "listening must allocate exactly nothing — no span, no tag list, and no boxed " +
                "state machine — and B2's hard zero on the ephemeral path must not move.");
    }

    /// <summary>An <see cref="ActivityListener"/> attached for the life of a <c>using</c>.</summary>
    private static ActivityListener Listening()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == FlowXTelemetry.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };

        ActivitySource.AddActivityListener(listener);

        return listener;
    }

    private static FlowExecutionResult Complete(ValueTask<FlowExecutionResult> execution)
    {
        execution.IsCompleted.ShouldBeTrue(
            "The decorator went asynchronous for a flow whose every step completed " +
            "synchronously. That boxes a state machine on the hot path.");

        return execution.GetAwaiter().GetResult();
    }

    /// <summary>Bytes allocated on this thread by one invocation, after warm-up.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static long Measure(Action operation)
    {
        for (var i = 0; i < 64; i++)
        {
            operation();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        operation();

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private sealed class UnixEpochClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    /// <summary>
    /// A dispatcher whose steps succeed and which allocates nothing doing it.
    /// </summary>
    /// <remarks>
    /// Its own double rather than <c>RecordingDispatcher</c>, and the difference is the whole
    /// measurement: a dispatcher that records what it was asked grows a list on every step, and
    /// sixty-five warm-up executions of a four-step flow through one measures the list rather
    /// than the engine. <c>EngineAllocationTests</c> keeps a private double for the same reason;
    /// this is that double, at the size this file needs.
    /// </remarks>
    private sealed class NullDispatcher : IStepDispatcher
    {
        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no branch step.");

        public int Select(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no switch step.");

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This dispatcher has no iteration to begin.");

        public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx)
            => throw new NotSupportedException("This dispatcher has no iteration to enter.");
    }
}

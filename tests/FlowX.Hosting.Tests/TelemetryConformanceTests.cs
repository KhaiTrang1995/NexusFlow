using System.Diagnostics;
using System.Diagnostics.Metrics;
using FlowX.Conformance.InMemory;
using FlowX.Observability;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Hosting.Tests;

/// <summary>
/// <c>TelemetryConformanceTest</c> — the gate
/// <a href="../../docs/12-Observability.md">12-Observability</a> §2 names as the thing that
/// will assert the frozen span schema "exactly", and which four documents described as an
/// existing gate while it existed in none of them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The names are a contract, so the assertions are literals.</strong> Every attribute
/// and metric name below is written out rather than read from the constant it is compared
/// against — a test that says <c>TelemetryNames.FlowId == TelemetryNames.FlowId</c> passes
/// whatever the constant is changed to, which is precisely the drift §3 says this gate exists
/// to stop. Dashboards, alerts and an estate's worth of queries depend on these strings; this
/// file is where changing one becomes a deliberate act.
/// </para>
/// <para>
/// <strong>What is asserted is emission, not existence.</strong> A constant that nothing writes
/// to a span is the failure mode this repository spends most of its effort removing, so each
/// name is checked on a span or a measurement produced by running a real flow through a real
/// <see cref="FlowHost"/> against a real <see cref="FlowEngine"/>.
/// </para>
/// <para>
/// <strong>Three of the thirteen attributes and two of the metric names have no producer,
/// and that is asserted too.</strong> "Not emitted" is a claim like any other:
/// <see cref="TwoOfTheThirteenAttributesAreNamedAndHaveNoProducer"/> and
/// <see cref="TheMetricsWithNoSubjectHaveNoInstrument"/> fail if somebody starts emitting one
/// without correcting §2 and §3, and fail if somebody deletes the name instead of the claim.
/// </para>
/// </remarks>
public sealed class TelemetryConformanceTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static FlowHost NewHost() =>
        new(new FlowEngine(new FixedTelemetryClock()), new FlowXOptions { ApplicationName = "Sample.App" });

    /// <summary>
    /// The first thing that has to be true: running a flow produces a span at all.
    /// </summary>
    [Fact]
    public async Task AFlowExecutionEmitsOneSpanNamedForTheFlow()
    {
        using var spans = new SpanRecorder();

        await NewHost().RunAsync(
            Plans.TwoStep(), new RecordingDispatcher(), Plans.Invocation, Cancellation);

        spans.Captured.Select(static a => a.OperationName).ShouldContain(
            "flow order.place",
            "docs/12-Observability.md §2 specifies one span per flow. Nothing under src/ " +
            "constructs an ActivitySource, so no exporter, sampler or dashboard in the " +
            "document's estate has ever had a span to attach to.");
    }

    /// <summary>
    /// The flow span carries every attribute §2's table puts on a flow and has a producer.
    /// </summary>
    [Fact]
    public async Task TheFlowSpanCarriesTheAttributesSection2PutsOnAFlow()
    {
        using var spans = new SpanRecorder();

        await NewHost().RunAsync(
            Plans.TwoStep(), new RecordingDispatcher(), Plans.Invocation, Cancellation);

        var flow = spans.Single("flow order.place");

        flow.GetTagItem("flowx.flow.id").ShouldBe("order.place");
        flow.GetTagItem("flowx.flow.version").ShouldBe("1.0.0");
        flow.GetTagItem("flowx.flow.profile").ShouldBe("Ephemeral");
        flow.GetTagItem("flowx.tenant.id").ShouldBe("acme");
    }

    /// <summary>The step span carries every attribute §2's table puts on a step.</summary>
    [Fact]
    public async Task TheStepSpanCarriesTheAttributesSection2PutsOnAStep()
    {
        using var spans = new SpanRecorder();

        await NewHost().RunAsync(
            Plans.TwoStep(), new RecordingDispatcher(), Plans.Invocation, Cancellation);

        var step = spans.Single("step 1 order.validate");

        step.GetTagItem("flowx.flow.id").ShouldBe("order.place");
        step.GetTagItem("flowx.step.id").ShouldBe(1);
        step.GetTagItem("flowx.capability.id").ShouldBe("order.validate");
        step.GetTagItem("flowx.capability.version").ShouldBe("1.0.0");
        step.GetTagItem("flowx.tenant.id").ShouldBe("acme");
    }

    /// <summary>
    /// A step span is a child of its flow's span, which is what makes a trace a causal chain
    /// rather than a bag of spans.
    /// </summary>
    [Fact]
    public async Task AStepSpanIsAChildOfItsFlowSpan()
    {
        using var spans = new SpanRecorder();

        await NewHost().RunAsync(
            Plans.TwoStep(), new RecordingDispatcher(), Plans.Invocation, Cancellation);

        var flow = spans.Single("flow order.place");
        var step = spans.Single("step 0 order.validate");

        step.ParentSpanId.ShouldBe(flow.SpanId);
        step.TraceId.ShouldBe(flow.TraceId);
    }

    /// <summary>A durable execution stamps the instance id on both spans.</summary>
    /// <remarks>
    /// §2 puts <c>flowx.flow.instance_id</c> on the flow and the step, and §3 forbids it as a
    /// metric label. Both halves are load-bearing and both are checked — here, and in
    /// <see cref="NoMetricIsLabelledWithAnythingUnbounded"/>.
    /// </remarks>
    [Fact]
    public async Task AJournaledExecutionStampsTheInstanceIdOnTheFlowAndStepSpans()
    {
        using var spans = new SpanRecorder();

        var host = new FlowHost(
            new FlowEngine(new FixedTelemetryClock()),
            new FlowXOptions { ApplicationName = "Sample.App" },
            new FlowDurability(new InMemoryFlowJournal(), new InMemoryLeaseStore()));

        var result = await host.RunAsync(
            Plans.Durable(), new RecordingDispatcher(), Plans.Invocation, Cancellation);

        var instanceId = result.InstanceId.ShouldNotBeNull().ToString();

        spans.Single("flow order.durable").GetTagItem("flowx.flow.instance_id").ShouldBe(instanceId);
        spans.Single("step 0 order.validate").GetTagItem("flowx.flow.instance_id").ShouldBe(instanceId);
    }

    /// <summary>An ephemeral flow's spans carry no instance id rather than an invented one.</summary>
    [Fact]
    public async Task AnEphemeralExecutionHasNoInstanceIdToStamp()
    {
        using var spans = new SpanRecorder();

        await NewHost().RunAsync(
            Plans.TwoStep(), new RecordingDispatcher(), Plans.Invocation, Cancellation);

        spans.Single("flow order.place").GetTagItem("flowx.flow.instance_id").ShouldBeNull();
        spans.Single("step 0 order.validate").GetTagItem("flowx.flow.instance_id").ShouldBeNull();
    }

    /// <summary>A failed step carries the error code and category, and so does its flow.</summary>
    [Fact]
    public async Task AFailureCarriesTheErrorCodeAndCategoryOnBothSpans()
    {
        using var spans = new SpanRecorder();

        var declined = new Error("payment.declined", "The card was declined.", ErrorCategory.Conflict);

        await NewHost().RunAsync(
            Plans.TwoStep(), new RecordingDispatcher(failAt: 1, declined), Plans.Invocation, Cancellation);

        var step = spans.Single("step 1 order.validate");

        step.GetTagItem("flowx.error.code").ShouldBe("payment.declined");
        step.GetTagItem("flowx.error.category").ShouldBe("Conflict");
        step.Status.ShouldBe(ActivityStatusCode.Error);

        var flow = spans.Single("flow order.place");

        flow.GetTagItem("flowx.error.code").ShouldBe("payment.declined");
        flow.GetTagItem("flowx.error.category").ShouldBe("Conflict");
    }

    /// <summary>
    /// Two of the thirteen attributes are named by the schema and produced by nothing, and a
    /// third is 1 by construction.
    /// </summary>
    [Fact]
    public async Task TwoOfTheThirteenAttributesAreNamedAndHaveNoProducer()
    {
        using var spans = new SpanRecorder();

        await NewHost().RunAsync(
            Plans.TwoStep(), new RecordingDispatcher(), Plans.Invocation, Cancellation);

        var flow = spans.Single("flow order.place");

        flow.GetTagItem("flowx.trigger.kind").ShouldBeNull(
            "FlowInvocation carries no trigger, and there is no Trigger Engine to learn one " +
            "from. docs/12 §2 records that on the row; if this starts carrying a value, the " +
            "row is now wrong.");

        flow.GetTagItem("flowx.trigger.source").ShouldBeNull();

        spans.Single("step 0 order.validate").GetTagItem("flowx.attempt").ShouldBeNull(
            "The attempt is derived from committed journal history inside FlowEngine and is " +
            "1 by construction while no forward-path policy executes. A literal 1 would read " +
            "as a retry count somebody had measured.");
    }

    /// <summary>The flow metrics carry the labels §3's table specifies.</summary>
    [Fact]
    public async Task TheFlowMetricsCarryTheLabelsSection3Specifies()
    {
        using var metrics = new MetricRecorder();

        await NewHost().RunAsync(
            Plans.TwoStep(), new RecordingDispatcher(), Plans.Invocation, Cancellation);

        var duration = metrics.Single("flowx_flow_duration_seconds");

        duration.Tags["flow"].ShouldBe("order.place");
        duration.Tags["profile"].ShouldBe("Ephemeral");
        duration.Tags["outcome"].ShouldBe("Success");
        duration.Tags["tenant"].ShouldBe("other");

        var total = metrics.Single("flowx_flow_total");

        total.Value.ShouldBe(1);
        total.Tags["flow"].ShouldBe("order.place");
        total.Tags["outcome"].ShouldBe("Success");
    }

    /// <summary>The step metrics carry the labels §3's table specifies.</summary>
    [Fact]
    public async Task TheStepMetricsCarryTheLabelsSection3Specifies()
    {
        using var metrics = new MetricRecorder();

        await NewHost().RunAsync(
            Plans.TwoStep(), new RecordingDispatcher(), Plans.Invocation, Cancellation);

        var step = metrics.Measurements("flowx_step_duration_seconds");

        step.Count.ShouldBe(2, "the plan has two steps.");
        step[0].Tags["flow"].ShouldBe("order.place");
        step[0].Tags["step"].ShouldBe("0");
        step[0].Tags["capability"].ShouldBe("order.validate");
        step[0].Tags["outcome"].ShouldBe("Success");
        step[1].Tags["step"].ShouldBe("1");

        var capability = metrics.Measurements("flowx_capability_duration_seconds");

        capability.Count.ShouldBe(2);
        capability[0].Tags["capability"].ShouldBe("order.validate");
        capability[0].Tags["outcome"].ShouldBe("Success");
        capability[0].Tags.ShouldNotContainKey(
            "flow",
            "§9 diagnoses a latency spike by capability, and a capability used by six flows " +
            "is one dependency with one p99.");
    }

    /// <summary>A capability that throws is counted, and still becomes the engine's error.</summary>
    [Fact]
    public async Task ACapabilityThatThrowsIsCountedAndStillBecomesAnUnhandledError()
    {
        using var metrics = new MetricRecorder();

        var result = await NewHost().RunAsync(
            Plans.TwoStep(), new RecordingDispatcher(throwAt: 0), Plans.Invocation, Cancellation);

        var unhandled = metrics.Single("flowx_capability_unhandled_total");

        unhandled.Value.ShouldBe(1);
        unhandled.Tags["capability"].ShouldBe("order.validate");

        result.Error!.Code.ShouldBe(
            "capability.unhandled",
            "Counting the defect must not change what happens to it: the decorator re-throws, " +
            "so the engine still converts it and still compensates.");
    }

    /// <summary>
    /// No metric is ever labelled with an instance id, a correlation id or an idempotency key.
    /// </summary>
    /// <remarks>
    /// §3's cardinality rule, as a gate. <c>flow.instance_id</c> is "never" a metric label — it
    /// belongs in traces and logs — and the other two are unbounded for the same reason. A
    /// metric tagged with one of these is a production incident rather than a detail, and it is
    /// the kind of mistake that is invisible in review and obvious in a bill.
    /// </remarks>
    [Fact]
    public async Task NoMetricIsLabelledWithAnythingUnbounded()
    {
        using var metrics = new MetricRecorder();

        var host = new FlowHost(
            new FlowEngine(new FixedTelemetryClock()),
            new FlowXOptions { ApplicationName = "Sample.App" },
            new FlowDurability(new InMemoryFlowJournal(), new InMemoryLeaseStore()));

        var result = await host.RunAsync(
            Plans.Durable(), new RecordingDispatcher(), Plans.Invocation, Cancellation);

        var instanceId = result.InstanceId.ShouldNotBeNull().ToString();

        metrics.All.ShouldNotBeEmpty("otherwise this gate is checking nothing.");

        foreach (var measurement in metrics.All)
        {
            foreach (var (key, value) in measurement.Tags)
            {
                key.ShouldNotBe("flowx.flow.instance_id");
                key.ShouldNotBe("instance_id");

                value.ShouldNotBe(instanceId, $"'{key}' on {measurement.Name} carries an instance id.");
                value.ShouldNotBe("corr-1", $"'{key}' on {measurement.Name} carries a correlation id.");
                value.ShouldNotBe("idem-1", $"'{key}' on {measurement.Name} carries an idempotency key.");
            }
        }
    }

    /// <summary>
    /// An allow-listed tenant is labelled; every other tenant is bucketed as <c>other</c>.
    /// </summary>
    [Fact]
    public async Task TheTenantLabelIsCappedByAnAllowListWithAnOtherBucket()
    {
        using var metrics = new MetricRecorder();

        try
        {
            FlowXTelemetry.ConfigureTenantLabels(["acme"]);

            var host = NewHost();

            await host.RunAsync(
                Plans.TwoStep(), new RecordingDispatcher(), Plans.Invocation, Cancellation);

            await host.RunAsync(
                Plans.TwoStep(),
                new RecordingDispatcher(),
                new FlowInvocation("corr-2", "idem-2", "not-listed"),
                Cancellation);

            var durations = metrics.Measurements("flowx_flow_duration_seconds");

            durations[0].Tags["tenant"].ShouldBe("acme", "an allow-listed tenant keeps its name.");
            durations[1].Tags["tenant"].ShouldBe(
                "other",
                "and every other tenant is bucketed, so one runaway caller cannot mint a " +
                "series per tenant id it invents.");
        }
        finally
        {
            // Static, because the meter is. Reset so no other test in this assembly depends on
            // the order it happened to run in.
            FlowXTelemetry.ConfigureTenantLabels(null);
        }
    }

    /// <summary>
    /// An exhausted compensation is counted under <c>flowx_flow_compensation_failed_total</c>.
    /// </summary>
    /// <remarks>
    /// The seam <c>ICompensationAlertSink</c>'s own documentation named this metric as "the
    /// thing that is not built". Raised through the sink rather than through a failing saga so
    /// that what is asserted is the counter and its labels, which is what §7 pages on.
    /// </remarks>
    [Fact]
    public void AnExhaustedCompensationIsCounted()
    {
        using var metrics = new MetricRecorder();

        ICompensationAlertSink sink = new CompensationFailureCounter();

        sink.CompensationExhausted(new CompensationAlert(
            "order.place",
            "1.0.0",
            Guid.NewGuid(),
            StepIndex: 2,
            "inventory.release",
            "corr-1",
            "acme",
            Attempts: 5,
            new Error("inventory.unavailable", "gone", ErrorCategory.Unavailable)));

        var counted = metrics.Single("flowx_flow_compensation_failed_total");

        counted.Value.ShouldBe(1);
        counted.Tags["flow"].ShouldBe("order.place");
        counted.Tags["step"].ShouldBe("2");
        counted.Tags.ShouldNotContainKey("tenant");
        counted.Tags.Count.ShouldBe(2, "§3 labels this one flow and step, and nothing else.");
    }

    /// <summary>Every journal call is timed under its own operation label.</summary>
    [Fact]
    public async Task JournalCallsAreTimedByOperation()
    {
        using var metrics = new MetricRecorder();

        var host = new FlowHost(
            new FlowEngine(new FixedTelemetryClock()),
            new FlowXOptions { ApplicationName = "Sample.App" },
            new FlowDurability(
                JournalTelemetry.Wrap(new InMemoryFlowJournal()), new InMemoryLeaseStore()));

        await host.RunAsync(Plans.Durable(), new RecordingDispatcher(), Plans.Invocation, Cancellation);

        var operations = metrics
            .Measurements("flowx_journal_commit_seconds")
            .Select(static m => (string?)m.Tags["operation"])
            .Distinct()
            .ToList();

        operations.ShouldContain("StartAsync");
        operations.ShouldContain("CommitAsync");
        operations.ShouldContain("CompleteAsync");
    }

    /// <summary>
    /// The frozen names, written out as literals.
    /// </summary>
    /// <remarks>
    /// <strong>Literals on both sides is the point.</strong> Comparing a constant to itself
    /// passes whatever it is changed to, and §2 freezes these names precisely because a rename
    /// silently breaks every dashboard and alert written against a previous release. This is
    /// the file where changing one is a deliberate act with a diff somebody has to justify.
    /// </remarks>
    [Fact]
    public void TheFrozenNamesAreTheOnesTheDocumentPublishes()
    {
        TelemetryNames.FlowId.ShouldBe("flowx.flow.id");
        TelemetryNames.FlowVersion.ShouldBe("flowx.flow.version");
        TelemetryNames.FlowInstanceId.ShouldBe("flowx.flow.instance_id");
        TelemetryNames.FlowProfile.ShouldBe("flowx.flow.profile");
        TelemetryNames.StepId.ShouldBe("flowx.step.id");
        TelemetryNames.CapabilityId.ShouldBe("flowx.capability.id");
        TelemetryNames.CapabilityVersion.ShouldBe("flowx.capability.version");
        TelemetryNames.TriggerKind.ShouldBe("flowx.trigger.kind");
        TelemetryNames.TriggerSource.ShouldBe("flowx.trigger.source");
        TelemetryNames.TenantId.ShouldBe("flowx.tenant.id");
        TelemetryNames.ErrorCode.ShouldBe("flowx.error.code");
        TelemetryNames.ErrorCategory.ShouldBe("flowx.error.category");
        TelemetryNames.Attempt.ShouldBe("flowx.attempt");

        TelemetryNames.FlowDurationSeconds.ShouldBe("flowx_flow_duration_seconds");
        TelemetryNames.FlowActive.ShouldBe("flowx_flow_active");
        TelemetryNames.FlowTotal.ShouldBe("flowx_flow_total");
        TelemetryNames.StepDurationSeconds.ShouldBe("flowx_step_duration_seconds");
        TelemetryNames.CapabilityDurationSeconds.ShouldBe("flowx_capability_duration_seconds");
        TelemetryNames.CapabilityUnhandledTotal.ShouldBe("flowx_capability_unhandled_total");
        TelemetryNames.TriggerAdmittedTotal.ShouldBe("flowx_trigger_admitted_total");
        TelemetryNames.TriggerRejectedTotal.ShouldBe("flowx_trigger_rejected_total");
        TelemetryNames.JournalCommitSeconds.ShouldBe("flowx_journal_commit_seconds");
        TelemetryNames.LeaseLostTotal.ShouldBe("flowx_lease_lost_total");
        TelemetryNames.OutboxPending.ShouldBe("flowx_outbox_pending");
        TelemetryNames.OutboxLagSeconds.ShouldBe("flowx_outbox_lag_seconds");
        TelemetryNames.StreamLagRecords.ShouldBe("flowx_stream_lag_records");
        TelemetryNames.FlowCompensationFailedTotal.ShouldBe("flowx_flow_compensation_failed_total");

        FlowXTelemetry.SourceName.ShouldBe("FlowX");
    }

    /// <summary>
    /// The metrics with no producer have no instrument either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An instrument created and never written to publishes an empty series, and §9's warning
    /// box is exactly that an operator who follows a row "finds no such series, which is
    /// indistinguishable from a healthy one". Not creating them is what keeps the difference
    /// between "zero" and "unmeasured" visible, and this is where that stays true.
    /// </para>
    /// <para>
    /// <strong>This test was <c>TheTwoMetricsWithNoSubjectHaveNoInstrument</c> and asserted that
    /// <c>flowx_trigger_admitted_total</c> had no instrument, "because a kind label presupposes
    /// the shared admission point P3 introduces".</strong> The seam exists —
    /// <c>FlowBusScan.AdmitAsync</c> — so the claim moved rather than being deleted: the counter
    /// is now asserted <em>emitted</em>, by
    /// <see cref="TheAdmissionSeamCountsWhatItLetIn"/>.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheMetricsWithNoSubjectHaveNoInstrument()
    {
        var created = Instruments();

        created.ShouldNotBeEmpty("otherwise this gate is checking nothing.");

        created.ShouldNotContain(
            TelemetryNames.StreamLagRecords,
            "Nothing streams, so this one arrives with P7 rather than with an emitter.");

        created.ShouldNotContain(
            TelemetryNames.TriggerRejectedTotal,
            "A requeue is not a refusal and a dead-letter is not the same event, so what this " +
            "counter would count is still a decision nobody has made.");

        created.ShouldContain(
            TelemetryNames.TriggerAdmittedTotal,
            "The admission seam is what this row waited for, and it exists.");
    }

    /// <summary>
    /// The admission seam counts what it let in, under the labels §3 froze.
    /// </summary>
    /// <remarks>
    /// <strong>One message through the seam, and the counter is the assertion.</strong> The
    /// labels are literals for this file's reason — a dashboard written against <c>kind</c> is
    /// broken by a rename and by nothing else — and <c>kind</c> is asserted to be the trigger
    /// family rather than the flow, because a counter labelled with the flow would be
    /// <c>flowx_flow_total</c> with extra steps.
    /// </remarks>
    [Fact]
    public async Task TheAdmissionSeamCountsWhatItLetIn()
    {
        using var metrics = new MetricRecorder();

        var admitted = await PushAdmissionTests.BusSeam.AdmitOneAsync(Cancellation);

        admitted.Disposition.ShouldBe(BusDisposition.Started);

        var counted = metrics.Single("flowx_trigger_admitted_total");

        counted.Value.ShouldBe(1);
        counted.Tags["kind"].ShouldBe("Bus");
        counted.Tags["reason"].ShouldBe("started");
        counted.Tags["tenant"].ShouldBe("other");
        counted.Tags.Count.ShouldBe(3, "§3 labels this one kind, reason and tenant.");
    }

    /// <summary>
    /// A second delivery of one message is counted as an admission, and says which it was.
    /// </summary>
    /// <remarks>
    /// The <c>reason</c> label's whole purpose. A redelivery the journal refused reached the
    /// runtime and did no work, and folding it into <c>started</c> would report a throughput
    /// that a broker's retry behaviour could inflate at will.
    /// </remarks>
    [Fact]
    public async Task ARedeliveryIsCountedAsDeduplicatedRatherThanStarted()
    {
        var seam = PushAdmissionTests.BusSeam.Create();
        var delivery = PushAdmissionTests.BusSeam.Delivery();

        await seam.Scan.AdmitAsync(seam.Registration, delivery, Cancellation);

        using var metrics = new MetricRecorder();

        var second = await seam.Scan.AdmitAsync(seam.Registration, delivery, Cancellation);

        second.Disposition.ShouldBe(BusDisposition.Deduplicated);

        metrics.Single("flowx_trigger_admitted_total").Tags["reason"].ShouldBe("deduplicated");
    }

    /// <summary>Every instrument <see cref="FlowXMetrics"/> creates, by name.</summary>
    private static IReadOnlyList<string> Instruments() =>
        [.. typeof(FlowXMetrics)
            .GetProperties()
            .Select(static p => p.GetValue(null))
            .OfType<Instrument>()
            .Select(static i => i.Name)];

    /// <summary>Captures every FlowX span, in order, for the duration of a test.</summary>
    private sealed class SpanRecorder : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<Activity> _captured = [];
        private readonly Lock _gate = new();

        public SpanRecorder()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == FlowXTelemetry.SourceName,

                // AllDataAndRecorded, not PropagationData: a sampler that drops the payload
                // makes SetTag a no-op, and this file exists to assert tags.
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,

                ActivityStopped = activity =>
                {
                    lock (_gate)
                    {
                        _captured.Add(activity);
                    }
                },
            };

            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> Captured
        {
            get
            {
                lock (_gate)
                {
                    return [.. _captured];
                }
            }
        }

        /// <summary>The one span with this name, or a failure naming what was captured.</summary>
        public Activity Single(string operationName)
        {
            var matches = Captured.Where(a => a.OperationName == operationName).ToList();

            matches.Count.ShouldBe(
                1,
                $"Expected exactly one '{operationName}'. Captured: " +
                $"[{string.Join(", ", Captured.Select(static a => a.OperationName))}].");

            return matches[0];
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>One recorded measurement: which instrument, what value, which tags.</summary>
    private sealed record RecordedMeasurement(
        string Name, double Value, IReadOnlyDictionary<string, object?> Tags);

    /// <summary>Captures every FlowX measurement for the duration of a test.</summary>
    /// <remarks>
    /// <see cref="MeterListener"/> is in the BCL, which is what makes a metric assertable
    /// without an exporter — and therefore what makes "this counter is emitted, with these
    /// labels" a test rather than a claim in a document.
    /// </remarks>
    private sealed class MetricRecorder : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<RecordedMeasurement> _measurements = [];
        private readonly Lock _gate = new();

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

            _listener.SetMeasurementEventCallback<long>(
                (instrument, value, tags, _) => Add(instrument.Name, value, tags));

            _listener.SetMeasurementEventCallback<double>(
                (instrument, value, tags, _) => Add(instrument.Name, value, tags));

            _listener.Start();
        }

        public IReadOnlyList<RecordedMeasurement> All
        {
            get
            {
                lock (_gate)
                {
                    return [.. _measurements];
                }
            }
        }

        public IReadOnlyList<RecordedMeasurement> Measurements(string instrument) =>
            [.. All.Where(m => m.Name == instrument)];

        public RecordedMeasurement Single(string instrument)
        {
            var matches = Measurements(instrument);

            matches.Count.ShouldBe(
                1,
                $"Expected exactly one measurement of '{instrument}'. Recorded: " +
                $"[{string.Join(", ", All.Select(static m => m.Name))}].");

            return matches[0];
        }

        public void Dispose() => _listener.Dispose();

        private void Add(string name, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copied = new Dictionary<string, object?>(tags.Length, StringComparer.Ordinal);

            foreach (var tag in tags)
            {
                copied[tag.Key] = tag.Value;
            }

            lock (_gate)
            {
                _measurements.Add(new RecordedMeasurement(name, value, copied));
            }
        }
    }

    private sealed class FixedTelemetryClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    /// <summary>A dispatcher whose steps succeed, fail or throw to order.</summary>
    private sealed class RecordingDispatcher : IStepDispatcher
    {
        private readonly int _failAt = -1;
        private readonly int _throwAt = -1;
        private readonly Error? _error;

        public RecordingDispatcher()
        {
        }

        public RecordingDispatcher(int failAt, Error error)
        {
            _failAt = failAt;
            _error = error;
        }

        public RecordingDispatcher(int throwAt) => _throwAt = throwAt;

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            if (stepIndex == _throwAt)
            {
                throw new InvalidOperationException("This capability threw instead of returning.");
            }

            return ValueTask.FromResult(
                stepIndex == _failAt ? StepOutcome.Failed(_error!) : StepOutcome.Success);
        }

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

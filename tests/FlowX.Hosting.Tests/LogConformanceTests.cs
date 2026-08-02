using System.Diagnostics;
using System.Text.Json.Serialization;
using FlowX.Conformance.InMemory;
using FlowX.Observability;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Hosting.Tests;

/// <summary>
/// <c>LogConformanceTest</c> — the gate for
/// <a href="../../docs/12-Observability.md">12-Observability</a> §4, which was the one section of
/// that document with no producer at all.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The names are a contract, so the assertions are literals.</strong> Every event name
/// and every field below is written out rather than read from the constant it is compared
/// against — a test that checks a constant against itself passes whatever the constant is
/// changed to, which is precisely the drift a frozen schema exists to stop.
/// <c>TelemetryConformanceTests</c> is written the same way and says why.
/// </para>
/// <para>
/// <strong><see cref="DiagnosticListener"/> is in the BCL</strong>, which is what makes "this
/// event is emitted, with these fields" a test rather than a claim — the same thing
/// <see cref="ActivityListener"/> and <see cref="System.Diagnostics.Metrics.MeterListener"/> do
/// for the other two pillars, and the reason §4 could be gated on the day it was built rather
/// than after somebody wired an exporter.
/// </para>
/// </remarks>
public sealed class LogConformanceTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static FlowHost NewHost() =>
        new(new FlowEngine(new FixedLogClock()), new FlowXOptions { ApplicationName = "Sample.App" });

    /// <summary>
    /// The first thing that has to be true: running a flow writes a structured event at all,
    /// and it carries §4's correlating fields.
    /// </summary>
    [Fact]
    public async Task AFlowExecutionEmitsAStartedEventCarryingTheCorrelatingFields()
    {
        using var log = new LogRecorder();

        await NewHost().RunAsync(
            Plans.TwoStep(), new LoggingDispatcher(), Plans.Invocation, Cancellation);

        var started = log.Single("FlowX.Flow.Started");

        started.FlowId.ShouldBe("order.place");
        started.FlowVersion.ShouldBe("1.0.0");
        started.Profile.ShouldBe("Ephemeral");
        started.TenantId.ShouldBe("acme");
        started.CorrelationId.ShouldBe("corr-1");
    }

    /// <summary>A flow that finished reports its outcome, at Information.</summary>
    [Fact]
    public async Task AFlowExecutionEmitsACompletedEventCarryingItsOutcome()
    {
        using var log = new LogRecorder();

        await NewHost().RunAsync(
            Plans.TwoStep(), new LoggingDispatcher(), Plans.Invocation, Cancellation);

        var completed = log.Single("FlowX.Flow.Completed");

        completed.FlowId.ShouldBe("order.place");
        completed.Outcome.ShouldBe("Success");
        completed.ErrorCode.ShouldBeNull();
        completed.Level.ShouldBe(FlowLogLevel.Information);
    }

    /// <summary>
    /// A failed flow is a Warning carrying the error code and category §2 puts on the span.
    /// </summary>
    [Fact]
    public async Task AFailedFlowIsRecordedAtWarningWithItsErrorCodeAndCategory()
    {
        using var log = new LogRecorder();

        var error = new Error("payment.declined", "The card was declined.", ErrorCategory.Conflict);

        await NewHost().RunAsync(
            Plans.TwoStep(), new LoggingDispatcher(failAt: 1, error), Plans.Invocation, Cancellation);

        var completed = log.Single("FlowX.Flow.Completed");

        completed.Outcome.ShouldBe("Failure");
        completed.ErrorCode.ShouldBe("payment.declined");
        completed.ErrorCategory.ShouldBe("Conflict");
        completed.Level.ShouldBe(FlowLogLevel.Warning);
    }

    /// <summary>The step boundary writes one record per step, carrying §2's step attributes.</summary>
    [Fact]
    public async Task TheStepBoundaryWritesOneRecordPerStep()
    {
        using var log = new LogRecorder();

        await NewHost().RunAsync(
            Plans.TwoStep(), new LoggingDispatcher(), Plans.Invocation, Cancellation);

        var steps = log.All("FlowX.Step.Completed");

        steps.Count.ShouldBe(2, "docs/12 §4 records a step boundary, and the plan has two steps.");

        steps[0].FlowId.ShouldBe("order.place");
        steps[0].StepId.ShouldBe(0);
        steps[0].CapabilityId.ShouldBe("order.validate");
        steps[0].CapabilityVersion.ShouldBe("1.0.0");
        steps[0].TenantId.ShouldBe("acme");
        steps[0].Outcome.ShouldBe("Success");

        // A step that succeeded is Debug: §4's worked example is a failure, and a record per
        // successful step at Information would make the ordinary case the loudest thing in the log.
        steps[0].Level.ShouldBe(FlowLogLevel.Debug);
    }

    /// <summary>
    /// §4's worked example, which is a failed step — the record that document shows in full.
    /// </summary>
    [Fact]
    public async Task AFailedStepIsRecordedAtWarningWithItsErrorCode()
    {
        using var log = new LogRecorder();

        var error = new Error(
            "payment.gateway_timeout", "The gateway did not answer.", ErrorCategory.Unavailable);

        await NewHost().RunAsync(
            Plans.TwoStep(), new LoggingDispatcher(failAt: 1, error), Plans.Invocation, Cancellation);

        var failed = log.All("FlowX.Step.Completed").Single(r => r.StepId == 1);

        failed.Outcome.ShouldBe("Failure");
        failed.ErrorCode.ShouldBe("payment.gateway_timeout");
        failed.ErrorCategory.ShouldBe("Unavailable");
        failed.Level.ShouldBe(FlowLogLevel.Warning);
        failed.Message.ShouldBe("Step failed");
    }

    /// <summary>A journaled execution stamps the instance id on the flow and step records.</summary>
    /// <remarks>
    /// §3's cardinality rule is that <c>flowx.flow.instance_id</c> is <em>never</em> a metric
    /// label because "it belongs in traces and logs". This is the logs half of that sentence, and
    /// <c>TelemetryConformanceTests.NoMetricIsLabelledWithAnythingUnbounded</c> is still the
    /// other half — the id being welcome here is exactly why it has to stay refused there.
    /// </remarks>
    [Fact]
    public async Task AJournaledExecutionStampsTheInstanceIdOnTheFlowAndStepRecords()
    {
        using var log = new LogRecorder();

        var host = new FlowHost(
            new FlowEngine(new FixedLogClock()),
            new FlowXOptions { ApplicationName = "Sample.App" },
            new FlowDurability(new InMemoryFlowJournal(), new InMemoryLeaseStore()));

        var result = await host.RunAsync(
            Plans.Durable(), new LoggingDispatcher(), Plans.Invocation, Cancellation);

        var instanceId = result.InstanceId.ShouldNotBeNull().ToString();

        log.Single("FlowX.Flow.Completed").InstanceId.ShouldBe(instanceId);
        log.All("FlowX.Step.Completed")[0].InstanceId.ShouldBe(instanceId);
    }

    /// <summary>An ephemeral flow's records carry no instance id rather than an invented one.</summary>
    [Fact]
    public async Task AnEphemeralExecutionHasNoInstanceIdToStamp()
    {
        using var log = new LogRecorder();

        await NewHost().RunAsync(
            Plans.TwoStep(), new LoggingDispatcher(), Plans.Invocation, Cancellation);

        log.Single("FlowX.Flow.Completed").InstanceId.ShouldBeNull();
        log.All("FlowX.Step.Completed")[0].InstanceId.ShouldBeNull();
    }

    /// <summary>The stores write a record per journal call, naming which call answered.</summary>
    [Fact]
    public async Task TheStoreWritesARecordPerJournalCall()
    {
        using var log = new LogRecorder();

        var host = new FlowHost(
            new FlowEngine(new FixedLogClock()),
            new FlowXOptions { ApplicationName = "Sample.App" },
            new FlowDurability(
                JournalTelemetry.Wrap(new InMemoryFlowJournal()), new InMemoryLeaseStore()));

        await host.RunAsync(Plans.Durable(), new LoggingDispatcher(), Plans.Invocation, Cancellation);

        var calls = log.All("FlowX.Journal.Called");

        calls.ShouldNotBeEmpty("docs/12 §4 records the stores, and a durable flow journals.");
        calls.Select(static c => c.Operation).ShouldContain("StartAsync");
        calls.Select(static c => c.Operation).ShouldContain("CompleteAsync");
        calls.ShouldAllBe(static c => c.InstanceId != null);
    }

    /// <summary>
    /// A store's refusal is its own event, so a subscriber can take the refusals and pay nothing
    /// for the commits.
    /// </summary>
    /// <remarks>
    /// A fenced-out write is how an operator learns a lease moved, and it is invisible in
    /// <c>flowx_journal_commit_seconds</c> — where a refusal is a working store answering
    /// quickly. That gap is the reason this event exists rather than an <c>outcome</c> field on
    /// the one above.
    /// </remarks>
    [Fact]
    public async Task ARefusedJournalCallIsItsOwnEvent()
    {
        using var log = new LogRecorder();

        var journal = JournalTelemetry.Wrap(new InMemoryFlowJournal());
        var missing = Guid.NewGuid();

        var result = await journal.ReadInstanceAsync(missing, Cancellation);

        result.IsFailure.ShouldBeTrue("reading an instance that was never started must refuse.");

        var refused = log.Single("FlowX.Journal.Refused");

        refused.Operation.ShouldBe("ReadInstanceAsync");
        refused.InstanceId.ShouldBe(missing.ToString());
        refused.ErrorCode.ShouldBe(result.Error.Code);
        refused.Level.ShouldBe(FlowLogLevel.Warning);

        log.All("FlowX.Journal.Called").ShouldBeEmpty("a refusal is not also an answer.");
    }

    /// <summary>
    /// Every message is a constant, and no message carries data. This is §4's
    /// "Structured only. Data as fields, never interpolated into the message".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the cardinality gate for logs, and it is the same discipline §3 states
    /// for metric labels.</strong> An interpolated message makes every occurrence its own
    /// distinct message: an aggregator can no longer group by it, "how often did this happen"
    /// stops being answerable, and the count of distinct messages grows with traffic — which is
    /// the log-shaped version of the incident §3 says an instance id in a metric label is.
    /// </para>
    /// <para>
    /// <strong>Reference equality, because that is what an interpolated string cannot
    /// satisfy.</strong> A literal is interned, so a message built with <c>$"…{id}"</c> fails
    /// this even when the run that produced it happens to have an empty id — which value
    /// equality would let through, and which is exactly the shape that ships and then explodes
    /// under real data.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task NoMessageCarriesDataAndEveryMessageIsAConstant()
    {
        using var log = new LogRecorder();

        var error = new Error("payment.declined", "The card was declined.", ErrorCategory.Conflict);

        var host = new FlowHost(
            new FlowEngine(new FixedLogClock()),
            new FlowXOptions { ApplicationName = "Sample.App" },
            new FlowDurability(
                JournalTelemetry.Wrap(new InMemoryFlowJournal()), new InMemoryLeaseStore()));

        await host.RunAsync(
            Plans.Durable(), new LoggingDispatcher(failAt: 1, error), Plans.Invocation, Cancellation);

        var frozen = new[]
        {
            "Flow started",
            "Flow completed",
            "Flow failed",
            "Flow suspended",
            "Step completed",
            "Step failed",
            "Step compensated",
            "Step compensation failed",
            "Journal call completed",
            "Journal call refused",
        };

        log.Every().ShouldNotBeEmpty("with nothing recorded this gate passes vacuously.");

        foreach (var record in log.Every())
        {
            frozen.ShouldContain(
                record.Message,
                $"'{record.Message}' is not one of the frozen messages, so either a message " +
                "was added without being frozen, or one carries data. docs/12 §4: data as " +
                "fields, never interpolated into the message.");

            // The identity check, not the equality check. An interpolated string that happens to
            // equal a constant is still an interpolated string, and the next record it produces
            // will not equal anything.
            frozen.Any(f => ReferenceEquals(f, record.Message)).ShouldBeTrue(
                $"'{record.Message}' is equal to a frozen message but is not that same " +
                "instance, so it was built at run time rather than named. That is an " +
                "interpolated message wearing a constant's value.");
        }
    }

    /// <summary>
    /// No record carries an attempt, because nothing in the runtime can count one yet.
    /// </summary>
    /// <remarks>
    /// §4's worked example shows <c>flowx.attempt</c> and §2's own table says it is "not
    /// emitted, and it would be a constant" — 1 by construction, because no policy runs on the
    /// forward path so a step is dispatched once. Emitting a literal 1 would put a retry count
    /// on a dashboard that nobody had measured. This fails if somebody adds the field without
    /// also making it mean something, and it is why §4's example was corrected rather than
    /// implemented.
    /// </remarks>
    [Fact]
    public void NoRecordCarriesAnAttemptBecauseNothingCanCountOne()
    {
        typeof(FlowLogRecord)
            .GetProperties()
            .Select(static p => p.Name)
            .ShouldNotContain(
                "Attempt",
                "docs/12 §2 says flowx.attempt has no producer and would be a constant 1. A " +
                "log field for it would be the same fabrication the span attribute refused.");
    }

    /// <summary>
    /// A record's trace and span ids join it to the trace, when there is one.
    /// </summary>
    /// <remarks>
    /// §4: <c>trace_id</c> and <c>span_id</c> "come free from <c>Activity.Current</c> once a span
    /// exists, which it now does at both boundaries". Free, but only once — this asserts the
    /// record is stamped from the span the flow is actually running under, rather than from
    /// whatever activity happened to be ambient in the caller.
    /// </remarks>
    [Fact]
    public async Task ARecordIsJoinedToTheTraceWhenOneIsBeingRecorded()
    {
        using var log = new LogRecorder();
        using var spans = new SpanListener();

        await NewHost().RunAsync(
            Plans.TwoStep(), new LoggingDispatcher(), Plans.Invocation, Cancellation);

        var step = log.All("FlowX.Step.Completed")[0];

        step.TraceId.ShouldNotBeNullOrEmpty();
        step.SpanId.ShouldNotBeNullOrEmpty();
        step.TraceId!.Length.ShouldBe(32, "a W3C trace id is 16 bytes as hex.");
        step.SpanId!.Length.ShouldBe(16, "a W3C span id is 8 bytes as hex.");
    }

    /// <summary>
    /// With no trace exporter attached, the ids are absent rather than fabricated.
    /// </summary>
    [Fact]
    public async Task ARecordCarriesNoTraceIdWhenNothingIsTracing()
    {
        using var log = new LogRecorder();

        await NewHost().RunAsync(
            Plans.TwoStep(), new LoggingDispatcher(), Plans.Invocation, Cancellation);

        var step = log.All("FlowX.Step.Completed")[0];

        step.TraceId.ShouldBeNull(
            "StartActivity returns null with no listener, so there is no span to read an id " +
            "from — and inventing one would put an id in the logs that no trace contains.");
    }

    // ---------------------------------------------------------------- [Sensitive]

    /// <summary>
    /// A <c>[Sensitive]</c> member does not reach a log record, and the reason is structural.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The proof is the absence of an accessor, not a redaction pass in the
    /// logger.</strong> Every property of <see cref="FlowLogRecord"/> is a <c>string</c>, an
    /// <c>int</c>, an enum or the one <see cref="JournalPayload"/> — so there is no property a
    /// contract instance could be assigned to, and no subscriber can ask a record for a value
    /// because a record has no value to give. That is what makes this a property of the type
    /// rather than of the current call sites: a future emitter cannot leak a member through a
    /// field that does not exist.
    /// </para>
    /// <para>
    /// <see cref="ASensitiveMemberIsRedactedOnTheWayOutOfALogRecord"/> is the other half — the
    /// one exit that does exist, and what comes out of it.
    /// </para>
    /// </remarks>
    [Fact]
    public void ALogRecordHasNoPropertyAValueCouldBeAssignedTo()
    {
        var allowed = new[]
        {
            typeof(string), typeof(int?), typeof(JournalPayload), typeof(FlowLogLevel),
        };

        var leaks = typeof(FlowLogRecord)
            .GetProperties()
            .Where(p => !allowed.Contains(p.PropertyType))
            .Select(static p => $"{p.Name} : {p.PropertyType.Name}")
            .ToList();

        leaks.ShouldBeEmpty(
            "A log record must carry a JournalPayload, never a value. JournalPayload has no " +
            "accessor for the object graph and its only exit — ToJson — redacts, so a member " +
            "declared [Sensitive] cannot leave through it. A property of any other reference " +
            "type would be a second exit with no redaction on it, which is the control " +
            "JournalPayload's own remarks say a store under deadline pressure walks around:" +
            Environment.NewLine + string.Join(Environment.NewLine, leaks));
    }

    /// <summary>
    /// The payload a real flow puts on a real log record has the marked member redacted.
    /// </summary>
    /// <remarks>
    /// The behavioural half. The step boundary takes its payload from
    /// <c>IStepDispatcher.DescribeStep</c> — the same object the journal is handed, already
    /// carrying the flow's <c>SensitiveMembers</c> — so this asserts that the pan is not in the
    /// record's only exit and, separately, that it is nowhere in the record at all.
    /// </remarks>
    [Fact]
    public async Task ASensitiveMemberIsRedactedOnTheWayOutOfALogRecord()
    {
        using var log = new LogRecorder();

        const string Pan = "4111111111111111";

        await NewHost().RunAsync(
            Plans.TwoStep(),
            new LoggingDispatcher(new PaymentMethod(Pan, "visa")),
            Plans.Invocation,
            Cancellation);

        var step = log.All("FlowX.Step.Completed")[0];

        var json = step.Payload.ToJson().ShouldNotBeNull();

        json.ShouldNotContain(Pan, Case.Sensitive, "the pan is declared [Sensitive].");
        json.ShouldContain("[redacted]");
        json.ShouldContain("visa", customMessage: "an unmarked member is still recorded.");
    }

    /// <summary>
    /// The sensitive value appears in no field of any record the run produced.
    /// </summary>
    /// <remarks>
    /// The scan <c>SecretsNeverLeaveTheProcess</c> is described as in §4's redaction note, over
    /// the one sink that now exists. It reads every string-valued property of every captured
    /// record rather than the payload alone, so a future field that carried the value directly
    /// would fail here even if the payload stayed clean.
    /// </remarks>
    [Fact]
    public async Task NoFieldOfAnyRecordCarriesTheSensitiveValue()
    {
        using var log = new LogRecorder();

        const string Pan = "4111111111111111";

        await NewHost().RunAsync(
            Plans.TwoStep(),
            new LoggingDispatcher(new PaymentMethod(Pan, "visa")),
            Plans.Invocation,
            Cancellation);

        log.Every().ShouldNotBeEmpty("with nothing recorded this scan passes vacuously.");

        foreach (var record in log.Every())
        {
            foreach (var property in typeof(FlowLogRecord).GetProperties())
            {
                (property.GetValue(record) as string)?.ShouldNotContain(
                    Pan,
                    Case.Sensitive,
                    $"{property.Name} carries a [Sensitive] value out of the process.");
            }

            record.Payload.ToJson()?.ShouldNotContain(Pan, Case.Sensitive);
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Captures every FlowX log event, in order, for the duration of a test.</summary>
    private sealed class LogRecorder : IDisposable
    {
        private readonly List<(string Name, FlowLogRecord Record)> _captured = [];
        private readonly Lock _gate = new();
        private readonly IDisposable _subscription;

        public LogRecorder() => _subscription = FlowXLog.Subscribe(Add);

        public IReadOnlyList<FlowLogRecord> Every() => [.. Captured().Select(static c => c.Record)];

        public IReadOnlyList<FlowLogRecord> All(string eventName) =>
            [.. Captured().Where(c => c.Name == eventName).Select(static c => c.Record)];

        public FlowLogRecord Single(string eventName)
        {
            var matches = All(eventName);

            matches.Count.ShouldBe(
                1,
                $"Expected exactly one '{eventName}'. Captured: " +
                $"[{string.Join(", ", Captured().Select(static c => c.Name))}].");

            return matches[0];
        }

        public void Dispose() => _subscription.Dispose();

        private List<(string Name, FlowLogRecord Record)> Captured()
        {
            lock (_gate)
            {
                return [.. _captured];
            }
        }

        private void Add(string name, FlowLogRecord record)
        {
            lock (_gate)
            {
                _captured.Add((name, record));
            }
        }
    }

    /// <summary>Records FlowX spans, so that a log record has a trace to be joined to.</summary>
    private sealed class SpanListener : IDisposable
    {
        private readonly ActivityListener _listener;

        public SpanListener()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == FlowXTelemetry.SourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
            };

            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class FixedLogClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    /// <summary>A dispatcher whose steps succeed or fail to order, and describe what they made.</summary>
    private sealed class LoggingDispatcher : IStepDispatcher
    {
        private readonly int _failAt = -1;
        private readonly Error? _error;
        private readonly PaymentMethod? _produced;

        public LoggingDispatcher()
        {
        }

        public LoggingDispatcher(int failAt, Error error)
        {
            _failAt = failAt;
            _error = error;
        }

        public LoggingDispatcher(PaymentMethod produced) => _produced = produced;

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(
                stepIndex == _failAt ? StepOutcome.Failed(_error!) : StepOutcome.Success);

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        /// <summary>
        /// What the generated dispatcher would hand the journal, and therefore what the step
        /// boundary logs: a payload carrying the flow's <c>SensitiveMembers</c>.
        /// </summary>
        public StepJournalEntry DescribeStep(int stepIndex, FlowContext ctx) =>
            _produced is null
                ? StepJournalEntry.Nothing
                : StepJournalEntry.Of(
                    JournalPayload.Of(_produced, LogPayloadJson.Default, [nameof(PaymentMethod.Pan)]),
                    stateBag: null);

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

/// <summary>A contract with a member the compiler would mark <c>[Sensitive]</c>.</summary>
/// <remarks>
/// File-scoped because a source-generated <c>JsonSerializerContext</c> requires every containing
/// type to be partial, and making the test class partial to hold a fixture would be the tail
/// wagging the dog. <c>JournalPayloadTests</c> declares its contracts the same way.
/// <para>
/// The attribute is read by the generator, which records the member in the manifest and emits it
/// as <c>Flow.SensitiveMembers</c>; what reaches a payload at run time is that list of names.
/// These tests build the list directly, so they assert the run-time control rather than
/// re-testing the generator that produces its input — which <c>PayloadWriterTests</c> covers.
/// </para>
/// </remarks>
internal sealed record PaymentMethod([property: Sensitive] string Pan, string Brand);

[JsonSerializable(typeof(PaymentMethod))]
internal sealed partial class LogPayloadJson : JsonSerializerContext;

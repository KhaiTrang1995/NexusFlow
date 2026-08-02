using System.Text.Json.Serialization;
using FlowX.Logging;
using FlowX.Observability;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace FlowX.Logging.Tests;

/// <summary>
/// The bridge from the events <c>FlowX.Abstractions</c> publishes to <see cref="ILogger"/> —
/// the half of <a href="../../docs/12-Observability.md">12-Observability</a> §4 that costs a
/// package reference, and the reason it lives in a project of its own.
/// </summary>
/// <remarks>
/// <strong>This assembly is the enforcement of the shape as well as a test of it.</strong> It
/// references <c>FlowX.Logging</c> and therefore <c>Microsoft.Extensions.Logging.Abstractions</c>,
/// and nothing else in the repository does. If the bridge ever moved down into
/// <c>FlowX.Abstractions</c>, <c>AbstractionsHasNoDependencies</c> would fail before this file
/// got a chance to.
/// </remarks>
public sealed class FlowXLogBridgeTests
{
    /// <summary>The first thing that has to be true: an event reaches an <see cref="ILogger"/>.</summary>
    [Fact]
    public void AnEventReachesTheLogger()
    {
        var sink = new RecordingLoggerFactory();

        using var bridge = FlowXLogBridge.Attach(sink);

        FlowXLog.WriteFlowStarted("order.place", "1.2.0", "Durable", "acme", "corr-1");

        var entry = sink.Single();

        entry.Level.ShouldBe(LogLevel.Information);
        entry.Message.ShouldBe("Flow started");
        entry.Category.ShouldBe("FlowX.Flow");
    }

    /// <summary>
    /// The fields arrive under §2's frozen attribute names, so one query finds a span attribute
    /// and a log field.
    /// </summary>
    /// <remarks>
    /// Literals on both sides. §4 reuses §2's vocabulary precisely so that an operator who
    /// filtered a trace by <c>flowx.flow.id</c> filters the logs the same way; a bridge that
    /// renamed them on the way out would silently break that and no test that compared a
    /// constant against itself would notice.
    /// </remarks>
    [Fact]
    public void TheFieldsArriveUnderTheFrozenAttributeNames()
    {
        var sink = new RecordingLoggerFactory();

        using var bridge = FlowXLogBridge.Attach(sink);

        FlowXLog.WriteStepCompleted(
            "order.place", 2, "payment.capture", "2.1.0", "fi_01HV8", "acme",
            "Failure", "payment.gateway_timeout", "Unavailable", JournalPayload.Empty);

        var fields = sink.Single().Fields;

        fields["flowx.flow.id"].ShouldBe("order.place");
        fields["flowx.step.id"].ShouldBe(2);
        fields["flowx.capability.id"].ShouldBe("payment.capture");
        fields["flowx.capability.version"].ShouldBe("2.1.0");
        fields["flowx.flow.instance_id"].ShouldBe("fi_01HV8");
        fields["flowx.tenant.id"].ShouldBe("acme");
        fields["flowx.error.code"].ShouldBe("payment.gateway_timeout");
        fields["flowx.error.category"].ShouldBe("Unavailable");
    }

    /// <summary>A field the record does not carry is absent, not present and null.</summary>
    /// <remarks>
    /// A flow event has no step and a store event has no flow. Writing the absent ones as nulls
    /// would put every key on every record, which a sink then indexes and an operator then has
    /// to filter out — the storage cost of a schema nobody chose.
    /// </remarks>
    [Fact]
    public void AFieldTheRecordDoesNotCarryIsAbsentRatherThanNull()
    {
        var sink = new RecordingLoggerFactory();

        using var bridge = FlowXLogBridge.Attach(sink);

        FlowXLog.WriteJournalCall("CommitAsync", Guid.Empty, null, null);

        var fields = sink.Single().Fields;

        fields.ShouldContainKey("flowx.journal.operation");
        fields.ShouldNotContainKey("flowx.flow.id");
        fields.ShouldNotContainKey("flowx.step.id");
        fields.ShouldNotContainKey("flowx.error.code");
    }

    /// <summary>
    /// The message reaches the sink as a constant, with the data beside it rather than in it.
    /// </summary>
    /// <remarks>
    /// §4's "Data as fields, never interpolated into the message", asserted at the far end of
    /// the bridge rather than at the emitter — which is where it would actually be lost, because
    /// a state object that rendered its fields in <c>ToString</c> would defeat the discipline one
    /// layer below where anybody would look for it.
    /// </remarks>
    [Fact]
    public void TheMessageReachesTheSinkAsAConstantWithTheDataBesideIt()
    {
        var sink = new RecordingLoggerFactory();

        using var bridge = FlowXLogBridge.Attach(sink);

        FlowXLog.WriteFlowCompleted(
            "order.place", "1.2.0", "Durable", Guid.NewGuid(), "acme", "corr-1",
            "Failure", "payment.declined", "Conflict");

        var entry = sink.Single();

        entry.Message.ShouldBe("Flow failed");
        entry.Message.ShouldNotContain("order.place");
        entry.Message.ShouldNotContain("payment.declined");
        entry.Fields["flowx.error.code"].ShouldBe("payment.declined");
    }

    /// <summary>
    /// A <c>[Sensitive]</c> member is redacted by the time the sink sees it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The far end of the guarantee. The bridge is handed a <see cref="JournalPayload"/> whose
    /// only exit is <see cref="JournalPayload.ToJson"/>, so the string it writes is the redacted
    /// one — there is no call it could have made instead. A bridge author who wanted the raw
    /// value would have had to change <c>FlowX.Abstractions</c> to get it, which is the point of
    /// the payload having no accessor.
    /// </para>
    /// <para>
    /// This is the sink end of what <c>LogConformanceTests</c> asserts at the emitter, and both
    /// halves are needed: the record could have been clean and the bridge could still have found
    /// a way to render something else.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASensitiveMemberIsRedactedByTheTimeTheSinkSeesIt()
    {
        const string Pan = "4111111111111111";

        var sink = new RecordingLoggerFactory();

        using var bridge = FlowXLogBridge.Attach(sink);

        var payload = JournalPayload.Of(
            new PaymentMethod(Pan, "visa"), BridgeJson.Default, [nameof(PaymentMethod.Pan)]);

        FlowXLog.WriteStepCompleted(
            "order.place", 0, "payment.capture", "2.1.0", null, "acme",
            "Success", null, null, payload);

        var entry = sink.Single();

        var rendered = entry.Fields["flowx.payload"].ShouldBeOfType<string>();

        rendered.ShouldNotContain(Pan, Case.Sensitive);
        rendered.ShouldContain("[redacted]");
        rendered.ShouldContain("visa", customMessage: "an unmarked member is still recorded.");

        // And nowhere else in the entry either — a field added later that happened to carry the
        // value would fail here even though the payload itself stayed clean.
        foreach (var field in entry.Fields.Values)
        {
            (field as string)?.ShouldNotContain(Pan, Case.Sensitive);
        }
    }

    /// <summary>
    /// <see cref="FlowLogLevel"/> and <see cref="LogLevel"/> agree member by member.
    /// </summary>
    /// <remarks>
    /// <see cref="FlowXLogBridge.ToLogLevel"/> is a cast, which is only correct because the two
    /// enums are declared with the same numeric values — and that is a fact about two files in
    /// two assemblies that nothing else would notice drifting. A record written at
    /// <c>Warning</c> arriving at a sink as <c>Error</c> would page somebody.
    /// </remarks>
    [Theory]
    [InlineData(FlowLogLevel.Trace, LogLevel.Trace)]
    [InlineData(FlowLogLevel.Debug, LogLevel.Debug)]
    [InlineData(FlowLogLevel.Information, LogLevel.Information)]
    [InlineData(FlowLogLevel.Warning, LogLevel.Warning)]
    [InlineData(FlowLogLevel.Error, LogLevel.Error)]
    [InlineData(FlowLogLevel.Critical, LogLevel.Critical)]
    public void TheTwoLevelEnumsAgree(FlowLogLevel flowX, LogLevel expected) =>
        FlowXLogBridge.ToLogLevel(flowX).ShouldBe(expected);

    /// <summary>Every event maps to one of three categories, and the set is bounded.</summary>
    /// <remarks>
    /// A category is what an operator raises and lowers a level on, and an unbounded set of them
    /// would be the logging equivalent of the cardinality problem §3 describes for metric labels
    /// — a per-instance category would make the filter configuration grow with traffic.
    /// </remarks>
    [Theory]
    [InlineData("FlowX.Flow.Started", "FlowX.Flow")]
    [InlineData("FlowX.Flow.Completed", "FlowX.Flow")]
    [InlineData("FlowX.Step.Completed", "FlowX.Step")]
    [InlineData("FlowX.Step.Compensated", "FlowX.Step")]
    [InlineData("FlowX.Journal.Called", "FlowX.Journal")]
    [InlineData("FlowX.Journal.Refused", "FlowX.Journal")]
    public void EveryEventMapsToOneOfThreeCategories(string eventName, string category) =>
        FlowXLogBridge.CategoryFor(eventName).ShouldBe(category);

    /// <summary>
    /// Disposing the bridge restores the zero cost the platform has with no subscriber.
    /// </summary>
    /// <remarks>
    /// The other end of budget B6. <c>TelemetryCostTests</c> measures that an unobserved emit
    /// allocates nothing; this asserts that detaching really does return the platform to
    /// unobserved, so that a host which reconfigures logging at run time does not keep paying
    /// for a subscriber it dropped.
    /// </remarks>
    [Fact]
    public void DisposingTheBridgeReturnsThePlatformToUnobserved()
    {
        var sink = new RecordingLoggerFactory();

        var bridge = FlowXLogBridge.Attach(sink);

        FlowXLog.IsEnabled.ShouldBeTrue();
        FlowXLog.IsEnabledFor("FlowX.Flow.Started").ShouldBeTrue();

        bridge.Dispose();

        FlowXLog.IsEnabled.ShouldBeFalse(
            "a disposed bridge must leave the platform costing exactly what it cost before " +
            "anything subscribed — the property TelemetryCostTests asserts.");

        FlowXLog.WriteFlowStarted("order.place", "1.2.0", "Durable", "acme", "corr-1");

        sink.Entries.ShouldBeEmpty("a disposed bridge must not still be writing.");
    }

    /// <summary>A null factory is refused rather than deferred to the first event.</summary>
    [Fact]
    public void AttachRefusesANullFactory() =>
        Should.Throw<ArgumentNullException>(() => FlowXLogBridge.Attach(null!));

    // ---------------------------------------------------------------- helpers

    private sealed record Entry(
        string Category, LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Fields);

    /// <summary>
    /// Captures what a sink would have written.
    /// </summary>
    /// <remarks>
    /// Its own <see cref="ILoggerFactory"/> rather than <c>LoggerFactory.Create</c>, and the
    /// difference is load-bearing: <c>LoggerFactory</c> is in
    /// <c>Microsoft.Extensions.Logging</c>, the implementation package, and taking it here would
    /// mean this assembly no longer demonstrates what the bridge actually needs. The bridge
    /// takes an <see cref="ILoggerFactory"/> and calls <see cref="ILoggerFactory.CreateLogger"/>,
    /// which is the whole of its surface — so a host that composes its own logging stack can
    /// wire it, and <c>FlowX.Logging</c> keeps its single Abstractions-only reference.
    /// </remarks>
    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly List<Entry> _entries = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<Entry> Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        public Entry Single()
        {
            var entries = Entries;

            entries.Count.ShouldBe(
                1,
                $"Expected exactly one entry. Captured: " +
                $"[{string.Join(", ", entries.Select(static e => e.Message))}].");

            return entries[0];
        }

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Add);

        public void AddProvider(ILoggerProvider provider) =>
            throw new NotSupportedException("This double is the sink; it hosts no providers.");

        public void Dispose()
        {
        }

        private void Add(Entry entry)
        {
            lock (_gate)
            {
                _entries.Add(entry);
            }
        }
    }

    private sealed class RecordingLogger(string category, Action<Entry> add) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);

            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    fields[pair.Key] = pair.Value;
                }
            }

            add(new Entry(category, logLevel, formatter(state, exception), fields));
        }
    }
}

/// <summary>A contract with a member the compiler would mark <c>[Sensitive]</c>.</summary>
internal sealed record PaymentMethod([property: Sensitive] string Pan, string Brand);

[JsonSerializable(typeof(PaymentMethod))]
internal sealed partial class BridgeJson : JsonSerializerContext;

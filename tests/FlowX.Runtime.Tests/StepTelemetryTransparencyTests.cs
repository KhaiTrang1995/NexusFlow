using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowX.Observability;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// <see cref="StepTelemetry"/> observes a step and changes nothing about it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Written for a defect that only an exporter could produce.</strong> The decorator
/// forwarded eleven of the interface's fourteen members and inherited the defaults for
/// <c>DescribeAudit</c>, <c>DescribeCacheKey</c> and <c>DescribeCacheEntry</c>. Since
/// <c>FlowHost</c> installs it whenever <see cref="StepTelemetry.IsEnabled"/> — a process-wide
/// condition — the consequence was that attaching an exporter emptied every audit record and
/// turned every cached step into an uncached one. Nothing failed; the features simply stopped.
/// </para>
/// <para>
/// <strong>The listener is the subject, not the setup.</strong> <see cref="StepTelemetry.Wrap"/>
/// hands back the caller's own dispatcher with nothing attached, so a test written without one
/// asserts about the undecorated path and would have passed throughout. That is why this class
/// joins <see cref="TelemetryCollection"/>: the listener is process-wide, which is the same fact
/// that made the defect reachable and the same fact <c>TelemetryCostTests</c> must not observe.
/// </para>
/// </remarks>
[Collection(TelemetryCollection.Name)]
public sealed partial class StepTelemetryTransparencyTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A step's result, with one member the flow marks sensitive.</summary>
    private sealed record Posted(string EntryId, string DebtorIban, decimal Amount);

    [JsonSerializable(typeof(Posted))]
    private sealed partial class PayloadJson : JsonSerializerContext;

    /// <summary>An audited step's record carries its payload with an exporter attached.</summary>
    /// <remarks>
    /// End to end rather than at the seam, because the seam is not where this was noticed: the
    /// record is written by the engine, and what an auditor holds is a document. A null one is
    /// still a record of what ran and on whose authority, so nothing on the path can tell it
    /// apart from a dispatcher that genuinely had nothing to describe.
    /// </remarks>
    [Fact]
    public async Task AnAuditedStepKeepsItsPayloadWhenTelemetryIsListening()
    {
        using var listener = Listening();

        var sink = new RecordingAuditSink();

        var dispatcher = new RecordingDispatcher
        {
            Audit = static (_, _, redact) => JournalPayload.OfState(
                [JournalMember.Of("result", new Posted("entry-1", "GB33BUKB20201555555555", 42m), PayloadJson.Default)],
                ["DebtorIban", .. redact]),
        };

        var plan = Plan();

        var wrapped = StepTelemetry.Wrap(plan, Plans.Invocation, dispatcher);

        wrapped.ShouldNotBeSameAs(
            dispatcher,
            "With a listener attached the decorator has to be installed, or this test asserts " +
            "about the undecorated path and can never fail.");

        var result = await new FlowEngine(new FakeClock(T0), audit: sink)
            .ExecuteAsync(plan, wrapped, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());

        var document = sink.Records.Single().Payload.ToJson();

        document.ShouldNotBeNull(
            "The decorator answered for the dispatcher it wraps. An empty payload makes the " +
            "policy's redact list name nothing, which is the objection stage 7 was declined " +
            "twice over.");

        using var parsed = JsonDocument.Parse(document);

        var posted = parsed.RootElement.GetProperty("result");

        posted.GetProperty("EntryId").GetString().ShouldBe(
            "entry-1",
            "and the record is the dispatcher's own document, not a shape that happens to parse.");

        posted.GetProperty("Amount").GetString().ShouldBe(
            JournalPayload.Redacted,
            "the redact list still reaches the one redaction pass — observing a step must not " +
            "make its record more revealing either.");
    }

    /// <summary>A cached step is still cached with an exporter attached.</summary>
    /// <remarks>
    /// The other member pair, and the failure is quieter than the audit's: <c>CacheKey</c> reads
    /// a null document as "this step cannot be cached" — the same answer a redacted key gives —
    /// and the engine carries on. Nothing anywhere reports that the policy stopped applying.
    /// </remarks>
    [Fact]
    public async Task ACachedStepIsStillCachedWhenTelemetryIsListening()
    {
        using var listener = Listening();

        var cache = new RecordingCache();
        var plan = Cached();

        var first = Caching();
        var second = Caching();

        var engine = new FlowEngine(new FakeClock(T0), cache: cache);

        (await engine.ExecuteAsync(plan, StepTelemetry.Wrap(plan, Plans.Invocation, first), Plans.Invocation, Ct))
            .IsSuccess.ShouldBeTrue();

        cache.Writes.Count.ShouldBe(
            1,
            "The step succeeded and declared a one-hour cache, so its result was held. A " +
            "decorator that inherited DescribeCacheEntry describes nothing, and WriteCacheAsync " +
            "returns before it reaches the store.");

        (await engine.ExecuteAsync(plan, StepTelemetry.Wrap(plan, Plans.Invocation, second), Plans.Invocation, Ct))
            .IsSuccess.ShouldBeTrue();

        second.Executed.ShouldBe(
            [1],
            "and the second execution was served out of the cache. With DescribeCacheKey " +
            "inherited the key is null, every execution misses, and the only symptom is a " +
            "dependency being called more often than it should be.");
    }

    private static ExecutionPlan Plan() => ExecutionPlan.Create(
        FlowDescriptor.Create("ledger.post", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(
                0,
                Plans.Validate,
                policies: PolicyChain.ForStep(
                    PolicySet.Named("ledger-post").Audit("financial", "Amount"), Plans.Validate)),
        ]));

    private static ExecutionPlan Cached() => ExecutionPlan.Create(
        FlowDescriptor.Create("market.quote", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(
                0,
                Plans.Validate,
                policies: PolicyChain.ForStep(
                    PolicySet.Named("external-read").Cache(TimeSpan.FromHours(1)), Plans.Validate)),
            StepNode.ForCapability(1, Plans.Capture),
        ]));

    /// <summary>A dispatcher whose step 0 describes a cache key and an entry.</summary>
    private static RecordingDispatcher Caching() => new()
    {
        CacheKey = static (index, _) => index == 0
            ? JournalPayload.Of(new Posted("entry-1", "iban", 42m), PayloadJson.Default, [])
            : JournalPayload.Empty,

        CacheEntry = static (index, _) => index == 0
            ? JournalPayload.OfState(
                [JournalMember.Of(nameof(Posted), new Posted("entry-1", "iban", 42m), PayloadJson.Default)],
                [])
            : JournalPayload.Empty,

        Restore = static (ctx, json) =>
        {
            using var document = JsonDocument.Parse(json);

            // By name, because a composed document also carries its schemaVersion — reading
            // that one as the contract is how this double first failed.
            foreach (var member in document.RootElement.EnumerateObject())
            {
                if (string.Equals(member.Name, nameof(Posted), StringComparison.Ordinal))
                {
                    ctx.Set(JournalState.Read<Posted>(member.Value, PayloadJson.Default));
                }
            }
        },
    };

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
}

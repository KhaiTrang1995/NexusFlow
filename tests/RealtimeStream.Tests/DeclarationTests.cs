using System.Text.Json;
using FlowX;
using FlowX.Generated;
using FlowX.Hosting;
using Shouldly;
using Xunit;

namespace RealtimeStream.Tests;

/// <summary>
/// What the sample declares, as the compiler read it — the manifest, the plan, and the
/// registration a host makes from them.
/// </summary>
/// <remarks>
/// These are the assertions that fail when somebody edits an attribute without meaning to change
/// behaviour. Everything else in this project drives the flow; this one checks that the flow
/// being driven is the one the README describes.
/// </remarks>
public sealed class DeclarationTests
{
    /// <summary>The flow declares the profile that makes its windows deduplicate.</summary>
    /// <remarks>
    /// Not decoration. The checkpoint is committed after a window's flow has run, so a crash in
    /// between rebuilds that window — and only a journaled instance has a primary key to refuse
    /// the second run. <c>FlowStreamCatalog.Add</c> throws on any other profile, which is what
    /// <see cref="TheDeclaredWindowIsOneTheEngineWillServe"/> exercises from the other side.
    /// </remarks>
    [Fact]
    public void TheFlowDeclaresStreamingAndTakesAWindowBatch()
    {
        AggregateTelemetryFlow.Plan.Flow.Profile.ShouldBe(ExecutionProfile.Streaming);
        AggregateTelemetryFlow.Plan.Flow.Id.ShouldBe("telemetry.aggregate");
    }

    /// <summary>
    /// The window, the lateness, the checkpoint interval and the parallelism the host is handed
    /// are the ones the attribute declares — and the registration is accepted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the test that would have caught <c>Lateness = "10s"</c>.</strong> The
    /// README wrote the lateness in the short form the <c>Window</c> argument takes;
    /// <c>StreamWindowSpec.Read</c> takes ISO-8601 for that argument and refuses anything else,
    /// so the sample would have thrown at startup with a message nobody sees until they run it.
    /// Registering here turns that into a failing test.
    /// </para>
    /// <para>
    /// It registers through <see cref="FlowStreamCatalog"/> rather than asserting on strings
    /// because the strings are not the claim: "a host can serve this declaration" is.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheDeclaredWindowIsOneTheEngineWillServe()
    {
        var catalogue = new FlowStreamCatalog();

        Should.NotThrow(() => catalogue.Add(
            new StreamSubscription(
                AggregateTelemetryFlow.Plan.Flow.Id,
                AggregateTelemetryFlow.Plan.Flow.Version,
                StreamHarness.Source,
                string.Empty),
            StreamHarness.DeclaredWindow,
            StreamHarness.DeclaredLateness,
            StreamHarness.DeclaredCheckpoint,
            StreamHarness.DeclaredParallelism,
            AggregateTelemetryFlow.Plan,
            new AggregateTelemetryFlow.Dispatcher(
                new DetectAnomalies(),
                new FoldReadings(),
                new PersistAggregate(new MeasuringAggregateStore(TimeSpan.Zero)))));

        var registration = catalogue.Registrations.ShouldHaveSingleItem();

        registration.Window.Size.ShouldBe(TimeSpan.FromMinutes(1));
        registration.Window.Lateness.ShouldBe(TimeSpan.FromSeconds(10));
        registration.Window.CheckpointInterval.ShouldBe(TimeSpan.FromSeconds(5));
        registration.Window.Parallelism.ShouldBe(8);
    }

    /// <summary>
    /// The manifest publishes the stream this flow reads, and publishes none of the tuning.
    /// </summary>
    /// <remarks>
    /// The split the stream emitter exists to make. The source is a contract — it is what this
    /// application promises to consume — so it is in <c>flowx.manifest.json</c> and
    /// <c>flowx diff</c> gates on it. The window, the lateness, the checkpoint interval and the
    /// parallelism configure how the platform runs the trigger rather than what it promises, so
    /// they travel with the generated registration and stay out of the published contract.
    /// </remarks>
    [Fact]
    public void TheManifestPublishesTheSourceAndNoneOfTheTuning()
    {
        var manifest = ManifestJson();

        var trigger = manifest.RootElement
            .GetProperty("flows")
            .EnumerateArray()
            .Single(flow => flow.GetProperty("id").GetString() == "telemetry.aggregate")
            .GetProperty("triggers")
            .EnumerateArray()
            .Single(candidate => candidate.GetProperty("kind").GetString() == "Stream");

        trigger.GetProperty("topic").GetString().ShouldBe(StreamHarness.Source);

        var raw = trigger.GetRawText();

        raw.ShouldNotContain("tumbling", Case.Insensitive);
        raw.ShouldNotContain("lateness", Case.Insensitive);
        raw.ShouldNotContain("checkpoint", Case.Insensitive);
        raw.ShouldNotContain("parallelism", Case.Insensitive);
    }

    /// <summary>
    /// Every capability this sample declares states an authorisation stance, and it is
    /// <c>Internal</c>.
    /// </summary>
    /// <remarks>
    /// A stream-triggered flow has no caller and therefore no principal: it is started by a
    /// window closing, on whichever node held the subscription's lease. <c>Internal</c> is the
    /// stance that says so. Anything requiring a principal would make every window fail with an
    /// authorisation error, and anything <c>Public</c> would be a capability reachable by a
    /// caller that does not exist — a permissive declaration in a sample people copy.
    /// </remarks>
    [Fact]
    public void EveryCapabilityIsInternalBecauseAWindowHasNoPrincipal()
    {
        var capabilities = ManifestJson()
            .RootElement
            .GetProperty("capabilities")
            .EnumerateArray()
            .ToList();

        capabilities.ShouldNotBeEmpty("a manifest with no capabilities would pass this vacuously.");

        foreach (var capability in capabilities)
        {
            capability.GetProperty("authorization").GetProperty("mode").GetString().ShouldBe(
                "Internal", capability.GetProperty("id").GetString());
        }
    }

    /// <summary>The manifest the sample's own build emitted.</summary>
    /// <remarks>
    /// The generated constant rather than a file on disk, for <c>Workflow.Tests.ManifestTests</c>'
    /// reason: the constant is what the compiler emits, and <c>flowx manifest</c> only copies it
    /// out. Asserting on the copy would leave these tests unable to tell a generator that stopped
    /// writing the trigger from a CLI that stopped copying the file.
    /// </remarks>
    private static JsonDocument ManifestJson() => JsonDocument.Parse(FlowXManifest.Json);
}

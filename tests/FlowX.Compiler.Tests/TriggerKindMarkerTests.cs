using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using FlowX.Compiler.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// <c>[TriggerKind]</c> across an assembly boundary — the claim the marker exists to make.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a compiled reference and not a second syntax tree.</strong> The whole
/// premise is that a trigger's <c>Kind</c> property cannot be read from metadata, and the
/// marker can. A test with the plugin's source in the same compilation proves neither:
/// Roslyn would have the property's syntax available and the case that matters — a
/// transport plugin consumed as a NuGet package — would be untested. So the plugin here is
/// compiled to an image and referenced as one, which is the shape of every real
/// consumption of it.
/// </para>
/// <para>
/// <strong>The alternative this rules out.</strong> Moving the kind to a base constructor
/// parameter — <c>protected TriggerAttribute(TriggerKind kind)</c>, called as
/// <c>base(TriggerKind.Bus)</c> — looks equivalent and is not: that call is IL in the
/// derived attribute's own constructor, not part of the applied attribute's
/// <c>ConstructorArguments</c>, so it reads only when the plugin's source is in the
/// compilation. <c>AMetadataOnlyAttributeExposesNoConstructorArguments</c> below measures
/// that directly, so the reason is recorded as an observation rather than as a claim in a
/// comment.
/// </para>
/// </remarks>
public sealed class TriggerKindMarkerTests
{
    private const string PluginSource = """
        using FlowX;

        namespace Plugin;

        /// <summary>What a transport plugin ships: its own attribute, declaring its family.</summary>
        [TriggerKind(TriggerKind.Bus)]
        [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
        public sealed class MqttTriggerAttribute(string topic) : TriggerAttribute
        {
            public override TriggerKind Kind => TriggerKind.Bus;

            public string Topic { get; } = topic;
        }

        /// <summary>The same attribute without the marker, as it looked before this work package.</summary>
        [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
        public sealed class SqsTriggerAttribute(string queue) : TriggerAttribute
        {
            public override TriggerKind Kind => TriggerKind.Bus;

            public string Queue { get; } = queue;
        }
        """;

    private const string Consumer = """
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;
        using Plugin;

        namespace Sample;

        public sealed record PlaceOrder(string Sku, int Quantity);
        public sealed record OrderResult(string Id);

        [Capability("inventory.reserve", Version = "1.0.0", Authorization = Authorization.Authenticated, Idempotent = true)]
        public sealed class ReserveInventory : ICapability<PlaceOrder, OrderResult>
        {
            public ValueTask<Result<OrderResult>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new OrderResult(input.Sku)));
        }
        """;

    /// <summary>The plugin, compiled to an image and referenced as one.</summary>
    private static readonly Lazy<PortableExecutableReference> Plugin = new(CompilePlugin);

    private static PortableExecutableReference CompilePlugin()
    {
        var compilation = GeneratorHarness.CompilationOf("Plugin", [], ("Plugin.cs", PluginSource));

        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);

        emitted.Success.ShouldBeTrue(string.Join(
            "\n",
            emitted.Diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error)));

        return MetadataReference.CreateFromImage(image.ToArray());
    }

    private static CSharpCompilation ConsumerWith(string attributes)
    {
        var compilation = GeneratorHarness.CompilationOf(
            "FlowX.GeneratorTests",
            [Plugin.Value],
            ("/src/Flows/Sample.cs", Consumer + "\n\n" + $$"""
            [Flow("order.place")]
            {{attributes}}
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .Select(static d => FormattableString.Invariant($"{d.Location.GetLineSpan()}: {d.Id} ") +
                                d.GetMessage(CultureInfo.InvariantCulture))
            .ShouldBeEmpty();

        return compilation;
    }

    // -------------------------------------------------------- the marker crosses

    /// <summary>
    /// A plugin's trigger, declared in a referenced assembly, reaches the manifest.
    /// </summary>
    /// <remarks>
    /// The unblocking claim. Kafka, RabbitMQ and Azure Service Bus are all P3 and each
    /// would otherwise face the choice ADR-0004 exists to remove: use a first-party
    /// attribute, or emit a manifest that cannot record its own trigger.
    /// </remarks>
    [Fact]
    public void APluginTriggerFromAReferencedAssemblyReachesTheManifest()
    {
        var run = GeneratorHarness.Run(ConsumerWith("""[MqttTrigger("orders/requested")]"""));

        run.ManifestJson.ShouldNotBeNull(run.Describe());

        using var manifest = JsonDocument.Parse(run.ManifestJson!);

        var trigger = manifest.RootElement
            .GetProperty("flows")[0]
            .GetProperty("triggers")
            .EnumerateArray()
            .ShouldHaveSingleItem();

        trigger.GetProperty("kind").GetString().ShouldBe("Bus");
    }

    /// <summary>
    /// The kind is all of it: a plugin trigger publishes what it declared and nothing more.
    /// </summary>
    /// <remarks>
    /// <c>topic</c> is deliberately absent. Nothing in metadata says that
    /// <c>MqttTriggerAttribute</c>'s first positional argument is a topic rather than a
    /// broker address or a subscription name, and ADR-0005 makes the manifest a document of
    /// declared facts. A partial record a consumer can see is honest; a guessed address is
    /// not.
    /// </remarks>
    [Fact]
    public void APluginTriggerPublishesItsKindAndNoInventedAddress()
    {
        var run = GeneratorHarness.Run(ConsumerWith("""[MqttTrigger("orders/requested")]"""));

        using var manifest = JsonDocument.Parse(run.ManifestJson!);

        var trigger = manifest.RootElement.GetProperty("flows")[0].GetProperty("triggers")[0];

        trigger.EnumerateObject().Select(static p => p.Name).ShouldBe(["kind"]);
        run.ManifestJson!.ShouldNotContain("orders/requested");
    }

    /// <summary>
    /// A plugin attribute without the marker is still skipped, and FLOWX1025 says so.
    /// </summary>
    /// <remarks>
    /// The other half, and the one that had to stay true: the marker is what makes a
    /// trigger readable, not the mere fact of being a <c>TriggerAttribute</c> subclass.
    /// </remarks>
    [Fact]
    public void APluginTriggerWithoutTheMarkerIsReportedAndSkipped()
    {
        var compilation = ConsumerWith("""[SqsTrigger("orders")]""");

        GeneratorHarness.Report(compilation, new TriggerDeclarationAnalyzer())
            .Select(static d => d.Id)
            .ShouldBe(["FLOWX1025"]);

        GeneratorHarness.Run(compilation).ManifestJson!.ShouldNotContain("\"triggers\"");
    }

    /// <summary>
    /// From a referenced assembly the rule stays a warning, because nobody here can fix it.
    /// </summary>
    /// <remarks>
    /// The consumer cannot add an attribute to a class in a package they do not own. An
    /// error would make referencing a plugin that has not shipped the marker yet fail their
    /// build over someone else's omission, which <c>17-Plugin-System.md §1</c> rules out —
    /// and it is the difference between this and the in-source case, which is an error
    /// because the fix is one line in a file the reader owns.
    /// </remarks>
    [Fact]
    public void APluginTriggerFromMetadataIsAWarningNotAnError()
    {
        GeneratorHarness
            .Report(ConsumerWith("""[SqsTrigger("orders")]"""), new TriggerDeclarationAnalyzer())
            .ShouldHaveSingleItem()
            .Severity.ShouldBe(DiagnosticSeverity.Warning);
    }

    // ------------------------------------------- what metadata does and does not carry

    /// <summary>
    /// The marker's enum argument is readable from metadata; a base-constructor call is not.
    /// </summary>
    /// <remarks>
    /// Measured rather than asserted in prose, because the difference between the two is
    /// the entire design and it is not obvious from either one's source. The applied
    /// attribute carries exactly the arguments written at the application site — for
    /// <c>[MqttTrigger("orders/requested")]</c> that is the topic, and nothing about a
    /// family — while the marker on the attribute's own class carries the enum. Anything
    /// passed to a base constructor lives in the derived constructor's IL and appears in
    /// neither.
    /// </remarks>
    [Fact]
    public void AMetadataOnlyAttributeCarriesItsMarkerButNotItsBaseConstructorCall()
    {
        var compilation = ConsumerWith("""[MqttTrigger("orders/requested")]""");
        var flow = compilation.GetTypeByMetadataName("Sample.PlaceOrderFlow");

        flow.ShouldNotBeNull();

        var applied = flow!.GetAttributes()
            .Single(static a => a.AttributeClass?.Name == "MqttTriggerAttribute");

        applied.AttributeClass!.Locations.ShouldAllBe(
            static location => !location.IsInSource,
            "The plugin must reach this compilation as metadata, or the test proves nothing.");

        applied.ConstructorArguments
            .Select(static argument => argument.Value)
            .ShouldBe(["orders/requested"], "The application site's arguments, and only those.");

        TriggerReader.KindOf(applied.AttributeClass).ShouldBe(
            "Bus",
            "The marker on the attribute's class is attribute data, and survives compilation.");
    }

    // ------------------------------------------------------------- kind names

    /// <summary>
    /// Every <c>TriggerKind</c> member has a manifest name, and the schema accepts each one.
    /// </summary>
    /// <remarks>
    /// <see cref="TriggerReader"/> maps the enum's underlying values to names by hand, and
    /// those names are also the schema's closed <c>kind</c> enum. Two ways for that to rot:
    /// a new <c>TriggerKind</c> member with no entry, which would make a properly declared
    /// trigger report FLOWX1025; and a name the schema does not list, which would emit a
    /// manifest failing its own validation. Neither is reachable from the generator — it
    /// targets netstandard2.0 and cannot reference the abstractions — so the check lives
    /// here.
    /// </remarks>
    [Fact]
    public void EveryTriggerKindHasAManifestNameTheSchemaAccepts()
    {
        var accepted = SchemaTriggerKinds();

        accepted.ShouldNotBeEmpty("The schema's trigger 'kind' enum could not be read.");

        foreach (var kind in Enum.GetValues<TriggerKind>())
        {
            var name = TriggerReader.ManifestKindName((int)kind);

            name.ShouldBe(
                kind.ToString(),
                FormattableString.Invariant($"TriggerKind.{kind} has no manifest name, so a ") +
                "trigger declaring it would be reported as declaring nothing.");

            accepted.ShouldContain(
                name!,
                FormattableString.Invariant($"The schema's trigger 'kind' enum does not list ") +
                FormattableString.Invariant($"'{name}', so a flow declaring it would emit a ") +
                "manifest that fails validation.");
        }
    }

    /// <summary>The <c>kind</c> values the committed manifest schema accepts.</summary>
    private static ImmutableArray<string> SchemaTriggerKinds()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "schemas", "flowx.manifest.schema.json");

            if (!File.Exists(candidate))
            {
                continue;
            }

            using var schema = JsonDocument.Parse(File.ReadAllText(candidate));

            return
            [
                .. schema.RootElement
                    .GetProperty("$defs")
                    .GetProperty("trigger")
                    .GetProperty("properties")
                    .GetProperty("kind")
                    .GetProperty("enum")
                    .EnumerateArray()
                    .Select(static value => value.GetString()!),
            ];
        }

        throw new FileNotFoundException("Could not locate schemas/flowx.manifest.schema.json.");
    }
}

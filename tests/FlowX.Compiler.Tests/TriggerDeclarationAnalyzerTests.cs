using System;
using System.Linq;
using FlowX.Compiler.Analysis;
using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// FLOWX1025 — a trigger declaration the compiler cannot read.
/// </summary>
/// <remarks>
/// <para>
/// Both directions, and the silent direction carries the weight. This rule fires on an
/// attribute the developer frequently does not own, so a false positive on a built-in
/// trigger would be downgraded in the first `.editorconfig` that hit it, and the rule
/// would then protect nothing. All five attributes <c>FlowX.Abstractions</c> ships are
/// pinned as cases that must stay silent, individually as well as together.
/// </para>
/// <para>
/// The positive direction pins the other half of the contract: what the analyzer reports
/// on is exactly what the manifest omits. Asserting the diagnostic without asserting the
/// omission would let the two drift apart, which is the state WP-22 left and this work
/// package exists to close.
/// </para>
/// </remarks>
public sealed class TriggerDeclarationAnalyzerTests
{
    private const string Preamble = """
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

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

    /// <summary>A trigger attribute a transport plugin would ship — outside the abstractions.</summary>
    private const string PluginTrigger = """
        [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
        public sealed class MqttTriggerAttribute(string topic) : TriggerAttribute
        {
            public override TriggerKind Kind => TriggerKind.Bus;

            public string Topic { get; } = topic;
        }
        """;

    private static string FlowWith(string attributes, string extraDeclarations = "") =>
        Preamble + "\n\n" + extraDeclarations + "\n\n" + $$"""
        [Flow("order.place")]
        {{attributes}}
        public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                .Step<ReserveInventory>()
                .Return(ctx => new OrderResult("id"));
        }
        """;

    private static string[] Analyze(string source)
    {
        GeneratorHarness.CompileErrorsIn(source).ShouldBeEmpty();

        return GeneratorHarness.Analyze(source, new TriggerDeclarationAnalyzer());
    }

    // ------------------------------------------------------------- it must fire

    [Fact]
    public void ATriggerAttributeTheCompilerCannotReadIsReported()
    {
        Analyze(FlowWith("""[MqttTrigger("orders/requested")]""", PluginTrigger))
            .ShouldBe(["FLOWX1025"]);
    }

    /// <summary>
    /// The reported flow still publishes no trigger — the diagnostic replaces the silence,
    /// not the decision.
    /// </summary>
    /// <remarks>
    /// WP-22 declined to invent a kind for an attribute it cannot read, and that stands:
    /// a guessed kind in the manifest would be a fact nobody declared, published in the
    /// document whose value is that it contains only declared facts. This package makes
    /// the refusal loud, and this test is what stops a later change from making it
    /// different instead.
    /// </remarks>
    [Fact]
    public void TheReportedTriggerIsStillAbsentFromTheManifest()
    {
        var source = FlowWith("""[MqttTrigger("orders/requested")]""", PluginTrigger);

        var run = GeneratorHarness.Run(source);

        run.ManifestJson.ShouldNotBeNull(run.Describe());
        run.ManifestJson!.ShouldNotContain("\"triggers\"");
    }

    /// <summary>
    /// A flow declaring both a readable and an unreadable trigger is reported once, and
    /// keeps the trigger that could be read.
    /// </summary>
    [Fact]
    public void OnlyTheUnreadableTriggerOfAMixedDeclarationIsReported()
    {
        var source = FlowWith(
            """
            [HttpTrigger("POST", "/api/v1/orders")]
            [MqttTrigger("orders/requested")]
            """,
            PluginTrigger);

        Analyze(source).ShouldBe(["FLOWX1025"]);

        GeneratorHarness.Run(source).ManifestJson!.ShouldContain("\"Http\"");
    }

    /// <summary>A subclass two levels down from <c>TriggerAttribute</c> is still a trigger.</summary>
    /// <remarks>
    /// The base-type walk is what makes this true, and it is worth a test: a plugin that
    /// factors shared members into its own intermediate base would otherwise slip past
    /// the rule entirely and be invisible again.
    /// </remarks>
    [Fact]
    public void AnIndirectSubclassIsReportedToo()
    {
        Analyze(FlowWith(
            "[Derived]",
            """
            public abstract class BusTriggerAttribute : TriggerAttribute
            {
                public override TriggerKind Kind => TriggerKind.Bus;
            }

            public sealed class DerivedAttribute : BusTriggerAttribute;
            """))
            .ShouldBe(["FLOWX1025"]);
    }

    // ---------------------------------------------------------- it must stay silent

    [Theory]
    [InlineData("""[HttpTrigger("POST", "/api/v1/orders", Idempotent = true)]""")]
    [InlineData("""[KafkaTrigger("orders.requested", Group = "order-placement")]""")]
    [InlineData("""[CronTrigger("0 2 * * *", TimeZone = "Europe/Berlin")]""")]
    [InlineData("""[StreamTrigger("orders.stream", Window = "tumbling:1m")]""")]
    [InlineData("""[AgentTrigger(Description = "Place a customer order")]""")]
    public void EveryTriggerTheAbstractionShipsIsSilent(string attribute)
    {
        Analyze(FlowWith(attribute)).ShouldBeEmpty(
            $"{attribute} is read into the manifest, so there is nothing to warn about. " +
            "A rule that fires on a built-in trigger would be downgraded everywhere and " +
            "then protect nothing.");
    }

    [Fact]
    public void AllFiveTogetherAreSilent()
    {
        Analyze(FlowWith(
            """
            [HttpTrigger("POST", "/api/v1/orders")]
            [KafkaTrigger("orders.requested", Group = "order-placement")]
            [CronTrigger("0 2 * * *", TimeZone = "Europe/Berlin")]
            [StreamTrigger("orders.stream", Window = "tumbling:1m")]
            [AgentTrigger(Description = "Place a customer order", Confirmation = ConfirmationMode.Always)]
            """))
            .ShouldBeEmpty();
    }

    [Fact]
    public void AFlowWithNoTriggerAtAllIsSilent()
    {
        Analyze(FlowWith(string.Empty)).ShouldBeEmpty(
            "A flow may be reached by a hand-written route. Absence of a trigger " +
            "attribute is not a declaration the compiler failed to read.");
    }

    /// <summary>
    /// An unreadable trigger on something that is not a flow says nothing.
    /// </summary>
    /// <remarks>
    /// The rule is about a manifest entry, and a type without <c>[Flow]</c> has no
    /// manifest entry to be missing from. Reporting there would be noise about a document
    /// the type was never going to appear in.
    /// </remarks>
    [Fact]
    public void ATriggerOnANonFlowTypeIsSilent()
    {
        Analyze(Preamble + "\n\n" + PluginTrigger + "\n\n" + """
            [MqttTrigger("orders/requested")]
            public sealed class NotAFlow;
            """)
            .ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ drift guard

    /// <summary>
    /// Every trigger attribute the abstractions ship must be one the reader recognises.
    /// </summary>
    /// <remarks>
    /// <see cref="TriggerReader"/> holds two lists that must agree — the switch that reads
    /// an attribute and the set <see cref="TriggerReader.IsRecognised"/> answers for — and
    /// a sixth built-in attribute added to only one of them would either vanish from the
    /// manifest without a diagnostic, or be reported as unreadable while being read. The
    /// per-attribute tests above cover the switch; this covers the day a new attribute is
    /// added and no test is written for it.
    /// </remarks>
    [Fact]
    public void EveryTriggerAttributeTheAbstractionShipsIsRecognised()
    {
        var shipped = typeof(TriggerAttribute).Assembly.GetTypes()
            .Where(static t => t is { IsAbstract: false, IsPublic: true } && typeof(TriggerAttribute).IsAssignableFrom(t))
            .Select(static t => t.FullName!)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        shipped.ShouldNotBeEmpty();

        shipped.ShouldBe(
            [.. TriggerReader.RecognisedAttributes.OrderBy(static name => name, StringComparer.Ordinal)],
            "A trigger attribute FlowX ships that the reader does not recognise would be " +
            "skipped by its own compiler and reported as a third party's.");
    }

    /// <summary>The analyzer declares the rule it raises. Roslyn silently drops it otherwise.</summary>
    [Fact]
    public void TheAnalyzerDeclaresTheRule()
    {
        new TriggerDeclarationAnalyzer().SupportedDiagnostics
            .Select(static d => d.Id)
            .ShouldBe(["FLOWX1025"]);
    }

    /// <summary>
    /// The rule is a warning, not an error, and that is a decision rather than an oversight.
    /// </summary>
    /// <remarks>
    /// The attribute usually belongs to a plugin package the consumer does not own, and
    /// <c>17-Plugin-System.md §1</c> commits to the opposite of a platform where using a
    /// third-party transport fails the build. FlowX's own <c>TreatWarningsAsErrors</c>
    /// still stops this repository's build; a consumer downgrades it in
    /// <c>.editorconfig</c>, which puts the decision in the repository that accepted it.
    /// </remarks>
    [Fact]
    public void TheRuleIsAWarningSoAPluginTransportDoesNotBreakAConsumersBuild()
    {
        FlowXDiagnostics.TriggerCannotBeRead.DefaultSeverity.ShouldBe(DiagnosticSeverity.Warning);

        FlowXDiagnostics.TriggerCannotBeRead.IsEnabledByDefault
            .ShouldBeTrue("A rule off by default reports nothing to the people who have not heard of it.");
    }
}

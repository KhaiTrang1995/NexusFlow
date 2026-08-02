using System;
using System.Linq;
using FlowX.Compiler.Analysis;
using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// FLOWX1025 — a trigger attribute that declares no <c>[TriggerKind]</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both directions, and the silent direction carries the weight. This rule fires on an
/// attribute the developer sometimes does not own, so a false positive on a built-in
/// trigger would be downgraded in the first `.editorconfig` that hit it, and the rule
/// would then protect nothing. All five attributes <c>FlowX.Abstractions</c> ships are
/// pinned as cases that must stay silent, individually as well as together, and so is a
/// plugin-style attribute that declares the marker — which is the whole point of the
/// marker existing.
/// </para>
/// <para>
/// The positive direction pins the other half of the contract: what the analyzer reports
/// on is exactly what the manifest omits. Asserting the diagnostic without asserting the
/// omission would let the two drift apart, which is the state WP-22 left and WP-25 closed.
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

    /// <summary>A trigger attribute a transport plugin would ship, before the marker was added.</summary>
    private const string UnmarkedPluginTrigger = """
        [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
        public sealed class MqttTriggerAttribute(string topic) : TriggerAttribute
        {
            public override TriggerKind Kind => TriggerKind.Bus;

            public string Topic { get; } = topic;
        }
        """;

    /// <summary>The same attribute, declaring its kind as data.</summary>
    private const string MarkedPluginTrigger = """
        [TriggerKind(TriggerKind.Bus)]
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
    public void ATriggerAttributeDeclaringNoKindIsReported()
    {
        Analyze(FlowWith("""[MqttTrigger("orders/requested")]""", UnmarkedPluginTrigger))
            .ShouldBe(["FLOWX1025"]);
    }

    /// <summary>
    /// The reported flow still publishes no trigger — the diagnostic replaces the silence,
    /// not the decision.
    /// </summary>
    /// <remarks>
    /// Declining to invent a kind for an attribute that declares none stands: a guessed
    /// kind in the manifest would be a fact nobody declared, published in the document
    /// whose value is that it contains only declared facts. What changed with the marker is
    /// that declaring one is now possible, not that an undeclared one is guessed at.
    /// </remarks>
    [Fact]
    public void TheReportedTriggerIsStillAbsentFromTheManifest()
    {
        var source = FlowWith("""[MqttTrigger("orders/requested")]""", UnmarkedPluginTrigger);

        var run = GeneratorHarness.Run(source);

        run.ManifestJson.ShouldNotBeNull(run.Describe());
        run.ManifestJson!.ShouldNotContain("\"triggers\"");
    }

    /// <summary>
    /// A flow declaring both a readable and an undeclared trigger is reported once, and
    /// keeps the trigger that could be read.
    /// </summary>
    [Fact]
    public void OnlyTheUndeclaredTriggerOfAMixedDeclarationIsReported()
    {
        var source = FlowWith(
            """
            [HttpTrigger("POST", "/api/v1/orders")]
            [MqttTrigger("orders/requested")]
            """,
            UnmarkedPluginTrigger);

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
    public void AnIndirectSubclassWithNoMarkerAnywhereInItsChainIsReportedToo()
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

    /// <summary>
    /// A marker carrying a value outside <c>TriggerKind</c> declares nothing readable.
    /// </summary>
    /// <remarks>
    /// A cast integer compiles — an attribute argument is not range-checked against the
    /// enum's members — and would otherwise reach the manifest as a <c>kind</c> the
    /// schema's closed enum rejects, discovered by whoever validated the document. Treating
    /// it as no declaration at all puts the failure at the keystroke that wrote it.
    /// </remarks>
    [Fact]
    public void AMarkerWithAValueOutsideTheEnumIsReportedAsNoDeclaration()
    {
        Analyze(FlowWith(
            """[Mqtt("orders/requested")]""",
            """
            [TriggerKind((TriggerKind)99)]
            public sealed class MqttAttribute(string topic) : TriggerAttribute
            {
                public override TriggerKind Kind => TriggerKind.Bus;

                public string Topic { get; } = topic;
            }
            """))
            .ShouldBe(["FLOWX1025"]);
    }

    // ---------------------------------------------------------- it must stay silent

    /// <summary>
    /// A plugin's own trigger attribute, declaring its kind, is not reported at all.
    /// </summary>
    /// <remarks>
    /// The case the whole work package exists for. Before the marker, this attribute was
    /// unreadable by construction and the only advice the rule could offer was "declare a
    /// built-in attribute instead" — which for the author of a transport plugin means
    /// "do not ship your attribute".
    /// </remarks>
    [Fact]
    public void APluginTriggerThatDeclaresItsKindIsSilent()
    {
        Analyze(FlowWith("""[MqttTrigger("orders/requested")]""", MarkedPluginTrigger))
            .ShouldBeEmpty();
    }

    /// <summary>The marker declared once on a plugin's intermediate base covers its subclasses.</summary>
    /// <remarks>
    /// <c>ISymbol.GetAttributes</c> returns only what is applied to that symbol — Roslyn
    /// does not apply the marker's <c>Inherited = true</c> to it — so this holds because
    /// the reader walks the base chain, and would silently stop holding if it stopped.
    /// Reflection inherits the attribute, so the runtime and the compiler would otherwise
    /// disagree about a declaration that is right there in the source.
    /// </remarks>
    [Fact]
    public void AMarkerOnAnIntermediateBaseCoversTheAttributesDerivedFromIt()
    {
        Analyze(FlowWith(
            "[Derived]",
            """
            [TriggerKind(TriggerKind.Bus)]
            public abstract class BusTriggerAttribute : TriggerAttribute
            {
                public override TriggerKind Kind => TriggerKind.Bus;
            }

            public sealed class DerivedAttribute : BusTriggerAttribute;
            """))
            .ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""[HttpTrigger("POST", "/api/v1/orders", Idempotent = true)]""")]
    [InlineData("""[KafkaTrigger("orders.requested", Group = "order-placement")]""")]
    [InlineData("""[CronTrigger("0 2 * * *", TimeZone = "Europe/Berlin")]""")]
    [InlineData("""[StreamTrigger("orders.stream", Window = "tumbling:1m")]""")]
    [InlineData("""[AgentTrigger(Description = "Place a customer order")]""")]
    public void EveryTriggerTheAbstractionShipsIsSilent(string attribute)
    {
        Analyze(FlowWith(attribute)).ShouldNotContain(
            "FLOWX1025",
            $"{attribute} is read into the manifest, so there is nothing to warn about. " +
            "A rule that fires on a built-in trigger would be downgraded everywhere and " +
            "then protect nothing.");
    }

    /// <summary>
    /// All five together raise nothing about a missing kind — and one thing about the cron.
    /// </summary>
    /// <remarks>
    /// <strong>This test asserted an empty list until the schedule trigger was bound, and the
    /// difference is a real limit rather than a rule being noisy.</strong> A schedule's flow must
    /// take <c>ScheduledFire</c>, because a firing has no body and the flow may not read a clock
    /// (<a href="../../docs/adr/ADR-0033-a-scheduled-flows-input-is-its-occurrence.md">ADR-0028</a>),
    /// and an HTTP endpoint binds a request body into whatever the flow declares. So one flow
    /// cannot serve both — <c>09 §3</c>'s "four transports, zero changes to the flow body" holds
    /// for the four whose payload the caller supplies, and stops at the ones whose payload the
    /// platform supplies. FLOWX1038 is what says so.
    /// <para>
    /// <strong>FLOWX1039 joined it when the bus trigger was bound, and it sharpens the same
    /// finding rather than repeating it.</strong> A bus flow must take <c>BusMessage</c>, because
    /// a delivery hands the body over undeserialised — the host has no <c>JsonTypeInfo</c> and
    /// constraint C2 forbids it reflecting for one. So there are now <em>two</em> kinds whose
    /// payload the platform supplies, and neither composes with the three whose payload a caller
    /// supplies. That is the honest shape of ADR-0004's claim, and the reason these two ids are
    /// separate: this flow is wrong for two different reasons, and an author fixing one still
    /// has the other.
    /// </para>
    /// </remarks>
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
            .ShouldBe(
                ["FLOWX1038", "FLOWX1039", "FLOWX1042"],
                "no trigger here fails to declare its kind; what is reported is that one flow " +
                "cannot bind both a caller's payload and the platform's — three times over now " +
                "that a stream is bound, because a closed window is a third payload the " +
                "platform supplies and it needs a profile of its own besides");
    }

    [Fact]
    public void AFlowWithNoTriggerAtAllIsSilent()
    {
        Analyze(FlowWith(string.Empty)).ShouldBeEmpty(
            "A flow may be reached by a hand-written route. Absence of a trigger " +
            "attribute is not a declaration the compiler failed to read.");
    }

    /// <summary>
    /// An undeclared trigger on something that is not a flow says nothing.
    /// </summary>
    /// <remarks>
    /// The rule is about a manifest entry, and a type without <c>[Flow]</c> has no
    /// manifest entry to be missing from. Reporting there would be noise about a document
    /// the type was never going to appear in.
    /// </remarks>
    [Fact]
    public void ATriggerOnANonFlowTypeIsSilent()
    {
        Analyze(Preamble + "\n\n" + UnmarkedPluginTrigger + "\n\n" + """
            [MqttTrigger("orders/requested")]
            public sealed class NotAFlow;
            """)
            .ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ the message

    /// <summary>
    /// The message names the missing declaration, not an impossibility.
    /// </summary>
    /// <remarks>
    /// The rule's old message said the trigger "is not one the compiler can read", whose
    /// only actionable reading was "stop using this attribute". What an author needs to be
    /// told is which attribute is missing which declaration, because that is the edit.
    /// </remarks>
    [Fact]
    public void TheMessageNamesTheAttributeAndTheMarkerItIsMissing()
    {
        var message = GeneratorHarness
            .AnalyzeWithMessages(
                FlowWith("""[MqttTrigger("orders/requested")]""", UnmarkedPluginTrigger),
                new TriggerDeclarationAnalyzer())
            .ShouldHaveSingleItem();

        message.ShouldContain("MqttTriggerAttribute");
        message.ShouldContain("PlaceOrderFlow");
        message.ShouldContain("[TriggerKind]");
        message.ShouldNotContain("cannot be read");
    }

    // ------------------------------------------------------------------ severity

    /// <summary>The analyzer declares the rules it raises. Roslyn silently drops one otherwise.</summary>
    [Fact]
    public void TheAnalyzerDeclaresTheRule()
    {
        new TriggerDeclarationAnalyzer().SupportedDiagnostics
            .Select(static d => d.Id)
            .ShouldBe([
                "FLOWX1025", "FLOWX1038", "FLOWX1039", "FLOWX1041", "FLOWX1042", "FLOWX1045",
            ]);
    }

    /// <summary>
    /// The rule's default is a warning, and that is a decision rather than an oversight.
    /// </summary>
    /// <remarks>
    /// The marker belongs on the attribute class, so a consumer of a plugin that has not
    /// added one cannot fix this in their own repository at all —
    /// <c>17-Plugin-System.md §1</c> commits to the opposite of a platform where using a
    /// third-party transport fails the build. The default severity is also what a consumer
    /// configures against in <c>.editorconfig</c> and what the release-tracking table
    /// records, so it must stay the lenient one even though the in-source case escalates.
    /// FlowX's own <c>TreatWarningsAsErrors</c> still stops this repository's build.
    /// </remarks>
    [Fact]
    public void TheDefaultSeverityIsAWarningSoAPluginTransportDoesNotBreakAConsumersBuild()
    {
        FlowXDiagnostics.TriggerDeclaresNoKind.DefaultSeverity.ShouldBe(DiagnosticSeverity.Warning);

        FlowXDiagnostics.TriggerDeclaresNoKind.IsEnabledByDefault
            .ShouldBeTrue("A rule off by default reports nothing to the people who have not heard of it.");
    }

    /// <summary>
    /// When the trigger attribute is declared here, the fix is here, and the rule is an error.
    /// </summary>
    /// <remarks>
    /// The escalation FLOWX1011 makes for a <c>Durable</c> flow, for the same reason: the
    /// severity follows what the author can do about it. Adding one attribute to a class in
    /// this compilation is not a trade-off worth a warning that will be scrolled past —
    /// and a manifest missing a trigger is a contract gate quietly losing its input.
    /// </remarks>
    [Fact]
    public void AnAttributeDeclaredInThisCompilationIsRaisedAsAnError()
    {
        GeneratorHarness
            .Report(
                FlowWith("""[MqttTrigger("orders/requested")]""", UnmarkedPluginTrigger),
                new TriggerDeclarationAnalyzer())
            .ShouldHaveSingleItem()
            .Severity.ShouldBe(DiagnosticSeverity.Error);
    }

    // ------------------------------------------------------------------ drift guard

    /// <summary>
    /// Every trigger attribute the abstractions ship is one whose arguments the reader
    /// projects.
    /// </summary>
    /// <remarks>
    /// A sixth built-in attribute added without being added to
    /// <see cref="TriggerReader.AttributesWithKnownShape"/> would still reach the manifest —
    /// it would carry the marker, so its kind publishes — but with none of its address:
    /// a <c>Bus</c> trigger with no topic and no group. That is a quiet loss in the document
    /// whose purpose is to record the address, and it is exactly the kind of omission no
    /// one writes a test for on the day they add the attribute.
    /// </remarks>
    [Fact]
    public void EveryTriggerAttributeTheAbstractionShipsHasAKnownShape()
    {
        var shipped = typeof(TriggerAttribute).Assembly.GetTypes()
            .Where(static t => t is { IsAbstract: false, IsPublic: true } && typeof(TriggerAttribute).IsAssignableFrom(t))
            .Select(static t => t.FullName!)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        shipped.ShouldNotBeEmpty();

        shipped.ShouldBe(
            [.. TriggerReader.AttributesWithKnownShape.OrderBy(static name => name, StringComparer.Ordinal)],
            "A trigger attribute FlowX ships whose arguments the reader does not project " +
            "would publish a kind and no address at all.");
    }
}

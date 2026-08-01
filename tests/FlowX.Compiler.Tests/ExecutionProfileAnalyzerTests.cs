using System.Linq;
using FlowX.Compiler.Analysis;
using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// FLOWX1028 — a flow declaring an execution profile the runtime does not implement.
/// </summary>
/// <remarks>
/// <para>
/// Both directions, and here they carry equal weight for once. The firing direction is the
/// point of the rule: <c>Streaming</c> has no engine at all, so a flow that declares it
/// compiles in silence and buys nothing. The silent direction is what keeps the rule usable —
/// <c>Ephemeral</c> and, since WP-52, <c>Durable</c> are the profiles the runtime implements,
/// and between them they are very nearly every flow ever written.
/// </para>
/// <para>
/// <strong>Half of this file used to be about <c>Durable</c>.</strong> WP-52 made the engine
/// journal a durable flow's step boundaries and refuse to run one with no journal, so the
/// rule was narrowed rather than deleted — deleting it would have handed <c>Streaming</c>
/// exactly the silence <c>Durable</c> had just been rescued from. The tests that asserted the
/// old behaviour are inverted here rather than removed, because "this no longer reports" is
/// the claim that stops the half coming back.
/// </para>
/// <para>
/// The severity is pinned by a test of its own, because it is the decision this rule turns
/// on and the one a later reader is most likely to "tidy up" into an error. The reasoning
/// is on the descriptor and on <c>docs/diagnostics/FLOWX1028.md</c>.
/// </para>
/// </remarks>
public sealed class ExecutionProfileAnalyzerTests
{
    private const string Preamble = """
        using System;
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

    private static string FlowWith(string flowAttribute) => Preamble + "\n\n" + $$"""
        {{flowAttribute}}
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

        return GeneratorHarness.Analyze(source, new ExecutionProfileAnalyzer());
    }

    /// <summary>Every report, undeduplicated, for the tests that count them.</summary>
    private static string[] AnalyzeOnce(string source)
    {
        GeneratorHarness.CompileErrorsIn(source).ShouldBeEmpty();

        return GeneratorHarness.AnalyzeWithMessages(source, new ExecutionProfileAnalyzer());
    }

    // ------------------------------------------------------------- it must fire

    /// <summary>
    /// <c>Streaming</c> is the half that is left, and it is the worse hole of the two.
    /// </summary>
    /// <remarks>
    /// <c>06 §4</c> puts it plainly — "<c>Streaming</c> has no engine at all". Deleting the
    /// rule when the journal landed would have left the strictly worse gap open, and P7 would
    /// have had to write it a second time.
    /// </remarks>
    [Fact]
    public void AStreamingFlowIsReported() =>
        Analyze(FlowWith("""[Flow("order.place", Profile = ExecutionProfile.Streaming)]"""))
            .ShouldBe(["FLOWX1028"]);

    /// <summary>The message names the flow and the profile it declared.</summary>
    /// <remarks>
    /// Naming the profile is what makes the message true for both values rather than
    /// generically true for neither, and it is what lets a P2 reader grep the build log for
    /// the flows that were waiting on the journal.
    /// </remarks>
    [Fact]
    public void TheMessageNamesTheFlowAndTheDeclaredProfile()
    {
        var messages = GeneratorHarness.AnalyzeWithMessages(
            FlowWith("""[Flow("order.place", Profile = ExecutionProfile.Streaming)]"""),
            new ExecutionProfileAnalyzer());

        messages.ShouldHaveSingleItem();
        messages[0].ShouldContain("PlaceOrderFlow");
        messages[0].ShouldContain("ExecutionProfile.Streaming");
    }

    /// <summary>Reported once per flow, at the declaration, however many steps it has.</summary>
    /// <remarks>
    /// A saga with nine compensable steps reported nine times is a rule that gets
    /// suppressed wholesale. The declaration is the decision; the steps are consequences.
    /// Asserted through the message list rather than the id list, because the latter is
    /// de-duplicated and would pass against an analyzer reporting nine times.
    /// </remarks>
    [Fact]
    public void ACompensatingFlowIsStillReportedExactlyOnce() =>
        AnalyzeOnce(Preamble + "\n\n" + """
            [Capability("inventory.release", Version = "1.0.0", Authorization = Authorization.Authenticated, Idempotent = true)]
            public sealed class ReleaseInventory : ICapability<OrderResult, OrderResult>
            {
                public ValueTask<Result<OrderResult>> ExecuteAsync(OrderResult input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(input));
            }

            [Flow("order.place", Profile = ExecutionProfile.Streaming)]
            [FlowDeadline("P30D")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
                    .Return(ctx => new OrderResult("id"));
            }
            """)
            .ShouldHaveSingleItem()
            .ShouldStartWith("FLOWX1028");

    // ---------------------------------------------------------- it must stay silent

    [Fact]
    public void AnEphemeralFlowIsSilent() =>
        Analyze(FlowWith("""[Flow("order.place", Profile = ExecutionProfile.Ephemeral)]"""))
            .ShouldBeEmpty(
                "Ephemeral is a profile the runtime implements. There is no gap to report.");

    /// <summary>
    /// <c>Durable</c> is silent, and this is the assertion that keeps the narrowing narrowed.
    /// </summary>
    /// <remarks>
    /// WP-52 made <c>FlowX.Runtime</c> read <c>ExecutionProfile</c>: a durable flow's step
    /// boundaries are journaled on <c>(instance, scope, step, attempt)</c> under a fencing
    /// token, resumption re-enters the same step loop from a cursor derived by replaying that
    /// journal, and a durable flow started with no journal is refused rather than run
    /// ephemerally. The declaration is honoured, so warning about it would be false — and a
    /// catalogue that reports things that are not true is a catalogue people stop reading.
    /// </remarks>
    [Fact]
    public void ADurableFlowIsSilentBecauseTheRuntimeNowJournalsIt() =>
        Analyze(FlowWith("""[Flow("order.place", Profile = ExecutionProfile.Durable)]"""))
            .ShouldBeEmpty(
                "The runtime journals a Durable flow's step boundaries since WP-52, so this " +
                "rule has nothing left to say about that profile.");

    /// <summary>A flow naming no profile is silent, because the default is the honoured one.</summary>
    /// <remarks>
    /// The common case by a wide margin — ADR-0003 makes durability opt-in, so a flow that
    /// says nothing is <c>Ephemeral</c>. A rule that fired here would fire on almost every
    /// flow ever written and be downgraded project-wide the same day.
    /// </remarks>
    [Fact]
    public void AFlowThatNamesNoProfileIsSilent() =>
        Analyze(FlowWith("""[Flow("order.place")]""")).ShouldBeEmpty();

    [Fact]
    public void OtherFlowAttributeArgumentsAreSilent() =>
        Analyze(FlowWith("""[Flow("order.place", Version = "2.0.0", Owner = "orders")]""")).ShouldBeEmpty();

    /// <summary>A profile on something that is not a flow is silent.</summary>
    /// <remarks>
    /// Nothing else carries <c>[Flow]</c>, so this is really a guard on the attribute test
    /// itself: a rule keyed on the property name alone would report any type with a
    /// <c>Profile</c> member.
    /// </remarks>
    [Fact]
    public void ATypeThatIsNotAFlowIsSilent() =>
        Analyze(Preamble + "\n\n" + """
            public sealed class NotAFlow
            {
                public ExecutionProfile Profile { get; set; } = ExecutionProfile.Durable;
            }
            """)
            .ShouldBeEmpty();

    /// <summary>A cast outside the enum is silent.</summary>
    /// <remarks>
    /// C# permits <c>(ExecutionProfile)7</c>. It names no profile the runtime could
    /// implement or fail to implement, so a message about durability would be about the
    /// wrong problem and this rule has no standing to invent one.
    /// </remarks>
    [Fact]
    public void AValueOutsideTheEnumIsSilent() =>
        Analyze(FlowWith("""[Flow("order.place", Profile = (ExecutionProfile)7)]""")).ShouldBeEmpty();

    // ------------------------------------------------------------------ the decisions

    /// <summary>The analyzer declares the rule it raises. Roslyn silently drops it otherwise.</summary>
    [Fact]
    public void TheAnalyzerDeclaresTheRule() =>
        new ExecutionProfileAnalyzer().SupportedDiagnostics
            .Select(static d => d.Id)
            .ShouldBe(["FLOWX1028"]);

    /// <summary>
    /// A warning, and that is the decision the rule turns on rather than a lax default.
    /// </summary>
    /// <remarks>
    /// An error's only repair is <c>Profile = Ephemeral</c>, which deletes the author's
    /// design decision and leaves P2 with no inventory of the flows waiting for it. Info
    /// is what ADR-0003 already rejected for <c>FLOWX1011</c>, because it never reaches a
    /// build log — shipping a rule that does nothing is the defect this one exists to
    /// correct.
    /// </remarks>
    [Fact]
    public void TheRuleIsAWarningSoAGoodFaithDurableDeclarationSurvives()
    {
        FlowXDiagnostics.ProfileIsNotHonouredByTheRuntime.DefaultSeverity
            .ShouldBe(DiagnosticSeverity.Warning);

        FlowXDiagnostics.ProfileIsNotHonouredByTheRuntime.IsEnabledByDefault
            .ShouldBeTrue("A rule off by default reports nothing to the people who have not heard of it.");
    }

    /// <summary>
    /// The fix for <c>FLOWX1017</c> produces a flow this rule is silent about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AwaitSignalRequiresDurableCodeFixProvider</c> exists to write
    /// <c>Profile = ExecutionProfile.Durable</c> onto a flow that suspends. While this rule
    /// covered <c>Durable</c>, that quick action cleared an error and raised a warning — and
    /// the two rules jointly left a suspending flow with no profile it could declare
    /// cleanly, which was the argument that pinned this rule to Warning rather than Error.
    /// </para>
    /// <para>
    /// Narrowing to <c>Streaming</c> retired the conflict outright rather than balancing it,
    /// so the claim worth asserting is now the stronger one: <em>this</em> rule is silent
    /// about the fix's output. The severity stays a Warning on its own merits, which the
    /// test above states.
    /// </para>
    /// <para>
    /// <strong>"The fix's output is clean" was untrue for two phases and is true again.</strong>
    /// This test runs one analyzer, so it only ever spoke for <c>FLOWX1028</c> — and while
    /// <c>FLOWX1031</c> was an error on <c>AwaitSignal</c> under every profile, the quick
    /// action really did land on a different diagnostic. WP-63 made the flow suspend and
    /// narrowed <c>FLOWX1031</c> off <c>AwaitSignal</c>, so
    /// <c>AwaitSignalRequiresDurableCodeFixTests.TheFixLandsOnAFlowThatCompilesAndWaits</c>
    /// now asserts the whole claim rather than the shortfall.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheFixForTheSuspensionRuleProducesAFlowThisRuleIsSilentAbout() =>
        Analyze(Preamble + "\n\n" + """
            public sealed record PaymentConfirmed(string Id);

            [Flow("order.place", Profile = ExecutionProfile.Durable)]
            [FlowDeadline("P30D")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .AwaitSignal<PaymentConfirmed>(TimeSpan.FromHours(1))
                    .Return(ctx => new OrderResult("id"));
            }
            """)
            .ShouldBeEmpty(
                "The quick action for FLOWX1017 writes exactly this profile. A fix whose " +
                "result is a different diagnostic is a fix that is broken.");

    /// <summary>The descriptor says when it is deleted, because it is scaffolding.</summary>
    /// <remarks>
    /// This rule reports a missing phase rather than a mistake, so it has an end date in a
    /// way the rest of the catalogue does not. A scaffold nobody removes when it stops
    /// being true becomes noise, and noise is what teaches people to suppress a catalogue.
    /// </remarks>
    [Fact]
    public void TheDescriptorSaysItIsDeletedRatherThanFixed() =>
        FlowXDiagnostics.ProfileIsNotHonouredByTheRuntime.Description.ToString(
            System.Globalization.CultureInfo.InvariantCulture)
            .ShouldContain("deleted");
}

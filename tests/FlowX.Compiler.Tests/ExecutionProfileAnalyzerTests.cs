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
/// whole point of the work package: before this rule, <c>Profile = Durable</c> compiled in
/// silence and bought nothing. The silent direction is what keeps the rule usable —
/// <c>Ephemeral</c> is the profile the runtime implements and the one the overwhelming
/// majority of flows declare, so a false positive there would fire on essentially every
/// flow in existence and be downgraded project-wide within a day.
/// </para>
/// <para>
/// The severity is pinned by a test of its own, because it is the decision this rule turns
/// on and the one a later reader is most likely to "tidy up" into an error. The reasoning
/// is on the descriptor and on <c>docs/diagnostics/FLOWX1028.md</c>; the deadlock with
/// <c>FLOWX1017</c> is asserted here rather than only argued, since it is the argument that
/// is checkable.
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

    [Fact]
    public void ADurableFlowIsReported() =>
        Analyze(FlowWith("""[Flow("order.place", Profile = ExecutionProfile.Durable)]"""))
            .ShouldBe(["FLOWX1028"]);

    /// <summary>
    /// <c>Streaming</c> too: it has even less behind it than <c>Durable</c>.
    /// </summary>
    /// <remarks>
    /// <c>06 §4</c> puts it plainly — "<c>Streaming</c> has no engine at all". A rule that
    /// covered only <c>Durable</c> would leave the strictly worse hole open, and would have
    /// to be written a second time in P7.
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
            FlowWith("""[Flow("order.place", Profile = ExecutionProfile.Durable)]"""),
            new ExecutionProfileAnalyzer());

        messages.ShouldHaveSingleItem();
        messages[0].ShouldContain("PlaceOrderFlow");
        messages[0].ShouldContain("ExecutionProfile.Durable");
    }

    /// <summary>Reported once per flow, at the declaration, however many steps it has.</summary>
    /// <remarks>
    /// A saga with nine compensable steps reported nine times is a rule that gets
    /// suppressed wholesale. The declaration is the decision; the steps are consequences.
    /// Asserted through the message list rather than the id list, because the latter is
    /// de-duplicated and would pass against an analyzer reporting nine times.
    /// </remarks>
    [Fact]
    public void ASuspendingCompensatingFlowIsStillReportedExactlyOnce() =>
        AnalyzeOnce(Preamble + "\n\n" + """
            public sealed record PaymentConfirmed(string Id);

            [Capability("inventory.release", Version = "1.0.0", Authorization = Authorization.Authenticated, Idempotent = true)]
            public sealed class ReleaseInventory : ICapability<OrderResult, OrderResult>
            {
                public ValueTask<Result<OrderResult>> ExecuteAsync(OrderResult input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(input));
            }

            [Flow("order.place", Profile = ExecutionProfile.Durable)]
            [FlowDeadline("P30D")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
                    .AwaitSignal<PaymentConfirmed>(TimeSpan.FromHours(1))
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
                "Ephemeral is the profile the runtime implements. There is no gap to report.");

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
    /// FLOWX1017 and FLOWX1028 must not both be errors, or a suspending flow has no legal
    /// profile.
    /// </summary>
    /// <remarks>
    /// <c>FLOWX1017</c> is an error on <c>AwaitSignal</c> without <c>Durable</c>; this rule
    /// reports <c>Durable</c>. Were both errors, <c>Ephemeral</c> would fail 1017 and
    /// <c>Durable</c> would fail 1028, making a documented construct unbuildable — and
    /// <c>AwaitSignalRequiresDurableCodeFixProvider</c>, whose entire job is to write
    /// <c>Profile = ExecutionProfile.Durable</c>, would be a quick action that produces a
    /// different error. This is the argument for the severity above, asserted rather than
    /// only written down.
    /// </remarks>
    [Fact]
    public void TheSuspensionRuleAndThisOneCannotBothBeErrors()
    {
        var both = new[]
        {
            FlowXDiagnostics.AwaitSignalRequiresDurable,
            FlowXDiagnostics.ProfileIsNotHonouredByTheRuntime,
        };

        both.Count(static d => d.DefaultSeverity == DiagnosticSeverity.Error).ShouldBeLessThan(
            2,
            "A flow using AwaitSignal would then have no profile it could legally declare: " +
            "Ephemeral fails FLOWX1017 and Durable fails FLOWX1028.");
    }

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

using System.Linq;
using FlowX.Compiler.Analysis;
using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// FLOWX1012 — compensation declared on a flow whose profile is not <c>Durable</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both directions, and the silent one is doing unusually heavy work here. This is the only
/// rule in the catalogue that reports on the <em>default</em> profile, so the flows it must
/// stay quiet about are not an edge case — they are every flow that declares no compensation
/// at all, which is most of them. A false positive on one of those would be a rule downgraded
/// project-wide the week it shipped.
/// </para>
/// <para>
/// <strong>The severity has three tests rather than one</strong>, because it is the decision
/// the package turned on and the one a later reader is most likely to "tidy up" by copying
/// WP-58's escalation across. It must not be copied across: this rule reports precisely
/// <em>because</em> the flow is not durable, so the determinism set's proof — the code is on
/// a durable flow's replay path — is never available where this rule fires. The sub-flow case
/// is pinned by <see cref="ADurableParentComposingThisFlowDoesNotEscalateIt"/>, because that
/// is the escalation somebody will propose and the engine does not honour it.
/// </para>
/// <para>
/// <strong>And the remedy is tested, not asserted.</strong>
/// <see cref="TheProfileTheMessageRecommendsSilencesTheRule"/> compiles the fix this rule
/// tells the reader to apply and checks that the rule then says nothing. The reservation on
/// this id outlived two phases because its fix changed nothing; a test that the fix is a fix
/// is the thing that would have caught that.
/// </para>
/// </remarks>
public sealed class CompensationDurabilityAnalyzerTests
{
    private const string Preamble = """
        using System;
        using System.Collections.Generic;
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

        [Capability("inventory.release", Version = "1.0.0", Authorization = Authorization.Authenticated, Idempotent = true)]
        public sealed class ReleaseInventory : ICapability<OrderResult, OrderResult>
        {
            public ValueTask<Result<OrderResult>> ExecuteAsync(OrderResult input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(input));
        }

        [Capability("payment.capture", Version = "1.0.0", Authorization = Authorization.Authenticated, Idempotent = false)]
        public sealed class CapturePayment : ICapability<OrderResult, OrderResult>
        {
            public ValueTask<Result<OrderResult>> ExecuteAsync(OrderResult input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(input));
        }

        [Capability("payment.refund", Version = "1.0.0", Authorization = Authorization.Authenticated, Idempotent = true)]
        public sealed class RefundPayment : ICapability<OrderResult, OrderResult>
        {
            public ValueTask<Result<OrderResult>> ExecuteAsync(OrderResult input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(input));
        }
        """;

    /// <summary>A compensable saga under whatever <c>[Flow]</c> attribute the test supplies.</summary>
    private static string SagaWith(string flowAttribute) => Preamble + "\n\n" + $$"""
        {{flowAttribute}}
        public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
                .Step<CapturePayment>()
                .Return(ctx => new OrderResult("id"));
        }
        """;

    /// <summary>The same flow with nothing to undo.</summary>
    private static string PlainWith(string flowAttribute) => Preamble + "\n\n" + $$"""
        {{flowAttribute}}
        public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                .Step<ReserveInventory>()
                .Step<CapturePayment>()
                .Return(ctx => new OrderResult("id"));
        }
        """;

    private static string[] Analyze(string source)
    {
        GeneratorHarness.CompileErrorsIn(source).ShouldBeEmpty();

        return GeneratorHarness.Analyze(source, new CompensationDurabilityAnalyzer());
    }

    /// <summary>Every report, undeduplicated, for the tests that count them.</summary>
    private static string[] AnalyzeOnce(string source)
    {
        GeneratorHarness.CompileErrorsIn(source).ShouldBeEmpty();

        return GeneratorHarness.AnalyzeWithMessages(source, new CompensationDurabilityAnalyzer());
    }

    // ------------------------------------------------------------- it must fire

    /// <summary>The declared-<c>Ephemeral</c> case, which is what the reference sample was.</summary>
    [Fact]
    public void ACompensableEphemeralFlowIsReported() =>
        Analyze(SagaWith("""[Flow("order.place", Profile = ExecutionProfile.Ephemeral)]"""))
            .ShouldBe(["FLOWX1012"]);

    /// <summary>
    /// And the case that matters more: a flow that names no profile at all.
    /// </summary>
    /// <remarks>
    /// ADR-0003 makes durability opt-in, so silence is <c>Ephemeral</c> — and a saga written
    /// without a thought about durability is far likelier than one that considered it and
    /// declined. Every other profile rule in the catalogue is silent on the default; this one
    /// exists for it. A version of this analyzer that only read an explicit argument would
    /// pass most of this file and miss the flows the rule was written for.
    /// </remarks>
    [Fact]
    public void ACompensableFlowThatNamesNoProfileIsReported() =>
        Analyze(SagaWith("""[Flow("order.place")]""")).ShouldBe(["FLOWX1012"]);

    /// <summary><c>Streaming</c> too, because the condition is "not Durable".</summary>
    /// <remarks>
    /// <c>Streaming</c> has no engine and runs on the ephemeral one, so it loses a pending
    /// compensation in exactly the same way. FLOWX1028 also reports such a flow; the two say
    /// different things — that the profile buys nothing, and that this is one of the things it
    /// costs — which is why neither defers to the other.
    /// </remarks>
    [Fact]
    public void ACompensableStreamingFlowIsReported() =>
        Analyze(SagaWith("""[Flow("order.place", Profile = ExecutionProfile.Streaming)]"""))
            .ShouldBe(["FLOWX1012"]);

    /// <summary>The message names the flow, the compensation it found, and the profile.</summary>
    /// <remarks>
    /// The compensation is quoted as the author's own text rather than as a type name, so the
    /// message points at something greppable in the file. The profile is named because
    /// "not durable" would be true of two different declarations and a helpful message about
    /// neither.
    /// </remarks>
    [Fact]
    public void TheMessageNamesTheFlowTheCompensationAndTheProfile()
    {
        var messages = GeneratorHarness.AnalyzeWithMessages(
            SagaWith("""[Flow("order.place", Profile = ExecutionProfile.Ephemeral)]"""),
            new CompensationDurabilityAnalyzer());

        messages.ShouldHaveSingleItem();
        messages[0].ShouldContain("PlaceOrderFlow");
        messages[0].ShouldContain("CompensateWith<ReleaseInventory>");
        messages[0].ShouldContain("Ephemeral");
    }

    /// <summary>Reported once per flow however many steps it compensates.</summary>
    /// <remarks>
    /// A saga with nine compensable steps reported nine times is a rule that gets suppressed
    /// wholesale — <c>ExecutionProfileAnalyzer</c>'s argument, and it applies unchanged. The
    /// profile is the decision; the compensations are its consequences, and they travel as
    /// additional locations instead. Asserted through the message list, because the id list is
    /// de-duplicated and would pass against an analyzer reporting nine times.
    /// </remarks>
    [Fact]
    public void ASagaWithTwoCompensationsIsReportedOnceAndSaysHowMany()
    {
        var messages = AnalyzeOnce(Preamble + "\n\n" + """
            [Flow("order.place", Profile = ExecutionProfile.Ephemeral)]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
                    .Step<CapturePayment>().CompensateWith<RefundPayment>()
                    .Return(ctx => new OrderResult("id"));
            }
            """);

        messages.ShouldHaveSingleItem();
        messages[0].ShouldContain("CompensateWith<ReleaseInventory> and 1 more");
    }

    /// <summary>A compensation nested inside a loop body is found.</summary>
    /// <remarks>
    /// A rule that only read the top level of the chain would be a rule that quietly stopped
    /// applying the day the DSL grew a nested builder — FLOWX1017 makes the same argument
    /// about a suspension hidden inside a <c>When</c>. An iteration's compensation is if
    /// anything more exposed: there are more of them and they complete further apart.
    /// </remarks>
    [Fact]
    public void ACompensationInsideAForEachBodyIsReported() =>
        Analyze(Preamble + "\n\n" + """
            [Flow("order.place", Profile = ExecutionProfile.Ephemeral)]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .ForEach(
                        ctx => new[] { ctx.Input.Sku },
                        line => line.Step<ReserveInventory>().CompensateWith<ReleaseInventory>(),
                        new ForEachOptions { MaxDegreeOfParallelism = 4 })
                    .Return(ctx => new OrderResult("id"));
            }
            """)
            .ShouldBe(["FLOWX1012"]);

    /// <summary>A profile the enum does not name is reported, and named as the reader wrote it.</summary>
    /// <remarks>
    /// C# permits <c>(ExecutionProfile)7</c>. <c>ExecutionProfileAnalyzer</c> is deliberately
    /// silent there because it asks which profile is unimplemented and an unnamed value is not
    /// an answer; this rule asks whether the flow is durable, and an unnamed value is a clear
    /// no. Whatever it is, nothing journals it and the unwind stack dies with the process.
    /// </remarks>
    [Fact]
    public void AProfileOutsideTheEnumIsReported()
    {
        var messages = GeneratorHarness.AnalyzeWithMessages(
            SagaWith("""[Flow("order.place", Profile = (ExecutionProfile)7)]"""),
            new CompensationDurabilityAnalyzer());

        messages.ShouldHaveSingleItem();
        messages[0].ShouldContain("(ExecutionProfile)7");
    }

    // ---------------------------------------------------------- it must stay silent

    /// <summary>
    /// A durable saga is silent, and this is the assertion the whole package rests on.
    /// </summary>
    /// <remarks>
    /// It is also the assertion that would have failed for two phases. Until WP-52 the
    /// runtime read <c>ExecutionProfile</c> nowhere, so this rule's remedy produced a flow
    /// that behaved identically — the reason the id stayed reserved. The engine now journals a
    /// durable flow's step boundaries, and a recovered instance puts each skipped compensable
    /// step back onto the unwind stack as it replays, which is exactly the guarantee the
    /// message sells.
    /// </remarks>
    [Fact]
    public void ACompensableDurableFlowIsSilent() =>
        Analyze(SagaWith("""[Flow("order.place", Profile = ExecutionProfile.Durable)]"""))
            .ShouldBeEmpty(
                "A durable flow journals its step boundaries and rebuilds the unwind stack " +
                "on resume. There is nothing left to warn about.");

    /// <summary>An ephemeral flow with nothing to undo is silent.</summary>
    /// <remarks>
    /// The single most important silence in the file. <c>Ephemeral</c> is the default profile,
    /// so a rule keyed on the profile alone would report on very nearly every flow ever
    /// written and be downgraded project-wide the same afternoon. Compensation is what makes
    /// the profile a finding rather than a fact.
    /// </remarks>
    [Fact]
    public void AnEphemeralFlowWithNoCompensationIsSilent() =>
        Analyze(PlainWith("""[Flow("order.place", Profile = ExecutionProfile.Ephemeral)]"""))
            .ShouldBeEmpty(
                "Ephemeral is the default profile. Without a compensation there is no work " +
                "for a crash to lose, and a rule that fired here would fire on everything.");

    /// <summary>And so is a flow that names no profile and compensates nothing.</summary>
    [Fact]
    public void AFlowThatNamesNoProfileAndCompensatesNothingIsSilent() =>
        Analyze(PlainWith("""[Flow("order.place")]""")).ShouldBeEmpty();

    /// <summary>A <c>CompensateWith</c> that is not FlowX's is silent.</summary>
    /// <remarks>
    /// The call is resolved semantically rather than matched on its name, for the reason
    /// <c>PredicatePurityAnalyzer</c> gives at length: other fluent libraries use these words,
    /// and a durability rule reasoning about somebody else's builder would be indefensible.
    /// The flow here is genuinely ephemeral and genuinely calls something spelled
    /// <c>CompensateWith</c> — everything except the one fact the rule is about.
    /// </remarks>
    [Fact]
    public void ACompensateWithOnSomethingThatIsNotAFlowXBuilderIsSilent() =>
        Analyze(Preamble + "\n\n" + """
            public interface INotOurBuilder
            {
                INotOurBuilder CompensateWith<T>();
            }

            [Flow("order.place", Profile = ExecutionProfile.Ephemeral)]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                private static INotOurBuilder? Elsewhere { get; }

                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow)
                {
                    Elsewhere?.CompensateWith<ReleaseInventory>();

                    flow.Step<ReserveInventory>().Return(ctx => new OrderResult("id"));
                }
            }
            """)
            .ShouldBeEmpty();

    /// <summary>A class that is not a flow is silent, whatever it declares.</summary>
    [Fact]
    public void ATypeThatIsNotAFlowIsSilent() =>
        Analyze(Preamble + "\n\n" + """
            public sealed class NotAFlow
            {
                public ExecutionProfile Profile { get; set; } = ExecutionProfile.Ephemeral;
            }
            """)
            .ShouldBeEmpty();

    // ------------------------------------------------------------------ the decisions

    /// <summary>The analyzer declares the rule it raises. Roslyn silently drops it otherwise.</summary>
    [Fact]
    public void TheAnalyzerDeclaresTheRule() =>
        new CompensationDurabilityAnalyzer().SupportedDiagnostics
            .Select(static d => d.Id)
            .ShouldBe(["FLOWX1012"]);

    /// <summary>A warning, and never anything else.</summary>
    /// <remarks>
    /// Three reasons, all on <c>docs/diagnostics/FLOWX1012.md</c>: the source is not wrong —
    /// a compensable ephemeral flow unwinds correctly on every failure that is not a crash,
    /// which is the trade ADR-0003 ratified; the remedy needs a journal registered on the
    /// host, which no analyzer can see, so an error would stop a build on a fact outside the
    /// compilation; and <c>Ephemeral</c> is the default, so an error would break every
    /// compensable flow in every codebase that has not already opted into durability. Info is
    /// what ADR-0003 got wrong twice — it never reaches a build log, and this rule reports
    /// only where nobody opted in.
    /// </remarks>
    [Fact]
    public void TheRuleIsAWarningAndTheReasonIsNotLeniency()
    {
        FlowXDiagnostics.CompensationIsNotDurable.DefaultSeverity
            .ShouldBe(DiagnosticSeverity.Warning);

        FlowXDiagnostics.CompensationIsNotDurable.IsEnabledByDefault
            .ShouldBeTrue("A rule off by default reports nothing to the people who have not heard of it.");
    }

    /// <summary>
    /// A <c>Durable</c> parent composing this flow does not escalate it to an error.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is WP-58's escalation, proposed for this rule, and refusing it is the decision
    /// worth pinning. The determinism set escalates where the compilation can prove the code
    /// is on a durable flow's replay path, transitively through sub-flows, because a sub-flow
    /// runs inside its parent's instance. Compensation is the one thing that reasoning does
    /// not carry: the parent records the composition as a single entry bound to the child's
    /// own context, and a resumed parent deliberately does <em>not</em> rebuild the skipped
    /// child's compensation stack — that context died with the node, and rebuilding it from
    /// the child's rows is WP-57.
    /// </para>
    /// <para>
    /// So escalating here would stop a build on a guarantee the engine does not deliver. The
    /// warning is not the lenient answer to that; it is the true one.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADurableParentComposingThisFlowDoesNotEscalateIt()
    {
        var reports = GeneratorHarness.Report(Preamble + "\n\n" + """
            [Flow("order.reserve", Profile = ExecutionProfile.Ephemeral)]
            public sealed partial class ReserveFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
                    .Return(ctx => new OrderResult("id"));
            }

            [Flow("order.place", Profile = ExecutionProfile.Durable)]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .SubFlow<ReserveFlow, PlaceOrder>(ctx => ctx.Input)
                    .Step<CapturePayment, OrderResult>(ctx => new OrderResult(ctx.Input.Sku))
                    .Return(ctx => new OrderResult("id"));
            }
            """, new CompensationDurabilityAnalyzer());

        reports.Select(static d => d.Id).ShouldBe(["FLOWX1012"]);

        reports.Single().Severity.ShouldBe(
            DiagnosticSeverity.Warning,
            "A durable parent does not make a composed child's compensations survive: the " +
            "resumed parent skips the child's entry and does not rebuild its unwind stack. " +
            "Escalating on the parent's profile would promise what WP-57 has not built.");
    }

    /// <summary>
    /// The profile the message recommends is one this rule is silent about.
    /// </summary>
    /// <remarks>
    /// The reservation on this id survived two phases because its remedy changed nothing, and
    /// a rule whose fix is a lie is worse than an unraised id. That failure was never
    /// detectable from the analyzer's own tests, which is exactly why this one compiles the
    /// recommended edit rather than asserting that it works.
    /// </remarks>
    [Fact]
    public void TheProfileTheMessageRecommendsSilencesTheRule() =>
        Analyze(SagaWith("""[Flow("order.place", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "orders")]"""))
            .ShouldBeEmpty();
}

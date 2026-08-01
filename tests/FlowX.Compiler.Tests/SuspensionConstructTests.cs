using System;
using System.IO;
using System.Linq;
using FlowX.Compiler.Diagnostics;
using FlowX.Compiler.Emit;
using FlowX.Compiler.Model;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// FLOWX1031 — the three constructs the compiler cannot honour, and what it says about them.
/// </summary>
/// <remarks>
/// <para>
/// <c>AwaitSignal</c>, <c>Delay</c> and <c>OnTimeout</c> are declared on
/// <c>IFlowBuilder</c>, documented in <c>docs/08-Flow-Definition.md §3.5</c>, and until
/// this rule they compiled with no diagnostic of any kind. Two of them produced no step;
/// the third produced one that completes immediately, carrying a timeout constant nobody
/// wrote.
/// </para>
/// <para>
/// Every test here goes through the real generator over a real compilation, because the
/// question is what a developer's build prints and a model-layer assertion cannot answer
/// that.
/// </para>
/// </remarks>
public sealed class SuspensionConstructTests
{
    private const string Signal = """
        public sealed record PaymentConfirmed(string Id);
        """;

    // ------------------------------------------------------------- it fires

    /// <summary>A durable flow that awaits a signal is told the wait does not happen.</summary>
    /// <remarks>
    /// The silent path, and the one the whole rule exists for: FLOWX1017 already refuses
    /// <c>AwaitSignal</c> under every other profile, so <c>Durable</c> is where an author
    /// who did everything right got nothing back.
    /// </remarks>
    [Fact]
    public void ReportsFLOWX1031WhenADurableFlowAwaitsASignal()
    {
        var run = GeneratorHarness.Run(Durable("""
                .Step<ReserveInventory>()
                .AwaitSignal<PaymentConfirmed>(TimeSpan.FromDays(7))
            """));

        run.Ids.ShouldContain("FLOWX1031", run.Describe());
    }

    /// <summary>The report names the construct and points at the call, not at the class.</summary>
    [Fact]
    public void TheAwaitSignalReportPointsAtTheCall()
    {
        var diagnostic = Only(Durable("""
                .Step<ReserveInventory>()
                .AwaitSignal<PaymentConfirmed>(TimeSpan.FromDays(7))
            """));

        diagnostic.Location.IsInSource.ShouldBeTrue(
            "A diagnostic with no source span shows up at the top of a build log rather " +
            "than on the line that has to change.");

        Message(diagnostic).ShouldContain("AwaitSignal");
    }

    /// <summary>A <c>Delay</c> is reported, under a durable profile or any other.</summary>
    /// <remarks>
    /// <c>Delay</c> has no FLOWX1017 of its own, so there is no profile under which
    /// anything already speaks for it. Reporting it only under <c>Durable</c> would leave
    /// the rule silent on the default profile, which is the one nearly every flow has.
    /// </remarks>
    [Fact]
    public void ReportsFLOWX1031ForADelayUnderEitherProfile()
    {
        Ids(Durable("""
                .Step<ReserveInventory>()
                .Delay(TimeSpan.FromDays(1))
            """)).ShouldContain("FLOWX1031");

        Ids(Ephemeral("""
                .Step<ReserveInventory>()
                .Delay(TimeSpan.FromDays(1))
            """)).ShouldContain("FLOWX1031");
    }

    /// <summary>An <c>OnTimeout</c> block is reported where it is written.</summary>
    /// <remarks>
    /// Its own report rather than a rider on the <c>AwaitSignal</c> above it, because it
    /// is a different loss: the steps inside the block reach no plan, no dispatcher and no
    /// manifest, so a reader auditing what the flow can do never sees them at all.
    /// </remarks>
    [Fact]
    public void ReportsFLOWX1031ForADiscardedOnTimeoutBlock()
    {
        var run = GeneratorHarness.Run(Durable("""
                .Step<ReserveInventory>()
                .AwaitSignal<PaymentConfirmed>(TimeSpan.FromDays(7))
                    .OnTimeout(f => f.Step<ReleaseInventory>())
            """));

        run.Diagnostics
            .Where(d => d.Id == "FLOWX1031")
            .Select(Message)
            .ShouldContain(m => m.Contains("OnTimeout", StringComparison.Ordinal), run.Describe());
    }

    /// <summary>A construct nested inside a conditional is reported too.</summary>
    /// <remarks>
    /// The trap FLOWX1017's own remarks describe: a rule that only reads the top level
    /// quietly stops applying the day someone wraps the call in a <c>When</c>.
    /// </remarks>
    [Fact]
    public void ReportsFLOWX1031ForADelayInsideAConditionalBlock() =>
        Ids(Durable("""
                .Step<ReserveInventory>()
                .When(ctx => ctx.Input.Sku.Length > 0, b => b.Delay(TimeSpan.FromDays(1)))
            """)).ShouldContain("FLOWX1031");

    // ------------------------------------------------- and stays silent otherwise

    /// <summary>A flow that declares none of the three is silent.</summary>
    /// <remarks>
    /// The half that matters more. A rule with false positives is suppressed everywhere
    /// and then protects nothing.
    /// </remarks>
    [Fact]
    public void IsSilentOnAFlowThatDeclaresNoSuspensionConstruct()
    {
        Ids(Durable("""
                .Step<ReserveInventory>()
                .Step<CapturePayment>()
            """)).ShouldNotContain("FLOWX1031");

        Ids(Ephemeral("""
                .Step<ReserveInventory>()
                .When(ctx => ctx.Input.Sku.Length > 0, b => b.Step<CapturePayment>())
                .Otherwise(b => b.Step<ReleaseInventory>())
            """)).ShouldNotContain("FLOWX1031");
    }

    /// <summary>The flow's own deadline is not a suspension construct.</summary>
    /// <remarks>
    /// <c>[FlowDeadline]</c> is the one timeout that works — an absolute budget checked at
    /// every step boundary — and a rule that reported it would be reporting the feature
    /// this one exists to distinguish itself from.
    /// </remarks>
    [Fact]
    public void IsSilentOnAFlowThatOnlyDeclaresADeadline() =>
        Ids(Durable("""
                .Step<ReserveInventory>()
            """)).ShouldNotContain("FLOWX1031");

    // ---------------------------------------------------------- the severities

    /// <summary>
    /// <c>AwaitSignal</c> is an error; <c>Delay</c> and <c>OnTimeout</c> are warnings.
    /// </summary>
    /// <remarks>
    /// The split is the line between omission and fabrication, and it is the whole of this
    /// rule's severity argument — <c>docs/diagnostics/FLOWX1031.md</c> makes it at length.
    /// A <c>Delay</c> that produces no step leaves a plan containing less than the author
    /// wrote and nothing untrue. An <c>AwaitSignal</c> reaches the plan carrying a timeout
    /// the compiler invented.
    /// </remarks>
    [Fact]
    public void AwaitSignalIsAnErrorAndTheOtherTwoAreWarnings()
    {
        Only(Durable("""
                .Step<ReserveInventory>()
                .AwaitSignal<PaymentConfirmed>(TimeSpan.FromDays(7))
            """)).Severity.ShouldBe(DiagnosticSeverity.Error);

        Only(Durable("""
                .Step<ReserveInventory>()
                .Delay(TimeSpan.FromDays(1))
            """)).Severity.ShouldBe(DiagnosticSeverity.Warning);
    }

    // ------------------------------------------------------ and nothing is emitted

    /// <summary>
    /// No plan is generated for a flow that awaits a signal, so no invented timeout is
    /// published.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the second defect, and it is separate from whether the construct works.
    /// <c>FlowEmitter</c> wrote <c>TimeSpan.FromHours(1)</c> for every <c>AwaitSignal</c>,
    /// whatever the author declared — a flow written to wait seven days produced a plan
    /// that said one hour. Carrying the declared duration through needs a field on the
    /// compiler's step model and a parameter through the emitter, which is WP-63's work.
    /// Between emitting a value nobody wrote and emitting nothing, the compiler now emits
    /// nothing.
    /// </para>
    /// <para>
    /// A <c>Delay</c> is not in this test because it never fabricated anything: it was
    /// dropped, and dropping it leaves the rest of the plan true.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoPlanIsEmittedForAFlowThatAwaitsASignal()
    {
        var run = GeneratorHarness.Run(Durable("""
                .Step<ReserveInventory>()
                .AwaitSignal<PaymentConfirmed>(TimeSpan.FromDays(7))
            """));

        run.Sources.ShouldNotContain(
            s => s.HintName.EndsWith(".Flow.g.cs", StringComparison.Ordinal),
            "A plan carrying a timeout the author did not write is worse than no plan.");
    }

    /// <summary>A flow that only delays still gets its plan, minus the delay.</summary>
    /// <remarks>
    /// The warning has to leave the build able to produce something, or it is an error
    /// wearing a warning's severity.
    /// </remarks>
    [Fact]
    public void APlanIsStillEmittedForAFlowThatOnlyDelays()
    {
        var run = GeneratorHarness.Run(Durable("""
                .Step<ReserveInventory>()
                .Delay(TimeSpan.FromDays(1))
                .Step<CapturePayment>()
            """));

        run.Plan.ShouldContainText("StepNode.ForCapability(1, Descriptors.Step1)",
            "The step after the absent delay takes the index the delay would have had.");
    }

    /// <summary>The emitter no longer contains a fabricated duration.</summary>
    /// <remarks>
    /// A source-level guard, deliberately, because the behavioural test above can only
    /// observe the constant's absence through a plan that is no longer emitted. If a later
    /// change starts emitting <c>AwaitSignal</c> plans again, this is what says the
    /// duration has to be the author's.
    /// </remarks>
    [Fact]
    public void TheEmitterFabricatesNoTimeout() =>
        SourceOf("src/FlowX.Compiler/Emit/FlowEmitter.cs")
            .ShouldNotContain(
                "TimeSpan.FromHours(1)",
                Case.Sensitive,
                "A value the author wrote must not be replaced by a constant.");

    /// <summary>And the emitter refuses rather than inventing one.</summary>
    /// <remarks>
    /// <para>
    /// The arm is unreachable through the generator — an <c>AwaitSignal</c> in a flow is an
    /// error, and <c>FlowPlanGenerator</c> emits nothing for a flow whose analysis failed —
    /// so this calls the emitter directly, which is the only way to ask what it would do.
    /// </para>
    /// <para>
    /// Asserting the refusal rather than leaving the arm as untested dead code is the point.
    /// If a later change starts emitting <c>AwaitSignal</c> plans again, the choice in front
    /// of whoever makes it should be "carry the author's duration" and not "put a constant
    /// back"; a loud failure is what puts that choice in front of them.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheEmitterRefusesAnAwaitSignalStepRatherThanInventingItsTimeout()
    {
        var suspending = new FlowModel(
            flowId: "offer.accept",
            version: "1.0.0",
            profile: "Durable",
            deadline: "P30D",
            containingNamespace: "Sample.Flows",
            typeName: "AcceptOfferFlow",
            inputTypeName: "Sample.Contracts.Offer",
            outputTypeName: "Sample.Contracts.Acceptance",
            steps: [StepModel.AwaitSignal(0, "payment.confirmed")]);

        Should.Throw<InvalidOperationException>(() => FlowEmitter.Emit(suspending))
            .Message.ShouldContain("FLOWX1031");
    }

    /// <summary>Reads a repository file, wherever the test binary happens to be built.</summary>
    private static string SourceOf(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate '{relativePath}'.");
    }

    // ------------------------------------------------------------- the descriptor

    /// <summary>The descriptor is on by default and names the work package that fixes it.</summary>
    [Fact]
    public void TheDescriptorIsEnabledAndNamesItsRemedy()
    {
        FlowXDiagnostics.SuspensionIsNotHonoured.IsEnabledByDefault.ShouldBeTrue(
            "A rule off by default reports nothing to the people who have not heard of it.");

        FlowXDiagnostics.SuspensionIsNotHonoured.Description
            .ToString(System.Globalization.CultureInfo.InvariantCulture)
            .ShouldContain("WP-63");
    }

    // ------------------------------------------------------------------ helpers

    private static string Durable(string steps) => Flow("Profile = ExecutionProfile.Durable", steps);

    private static string Ephemeral(string steps) => Flow(null, steps);

    private static string Flow(string? profile, string steps) =>
        FlowPlanGeneratorTests.WithFlow(Signal + "\n\n" +
            $$"""
            [Flow("order.place"{{(profile is null ? string.Empty : ", " + profile)}})]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
            {{steps}}
                    .Return(ctx => new OrderResult("id"));
            }
            """);

    private static string[] Ids(string source) => GeneratorHarness.Run(source).Ids;

    private static Diagnostic Only(string source)
    {
        var run = GeneratorHarness.Run(source);

        return run.Diagnostics.SingleOrDefault(d => d.Id == "FLOWX1031")
            ?? throw new InvalidOperationException(
                $"Expected exactly one FLOWX1031. Got: {run.Describe()}");
    }

    private static string Message(Diagnostic diagnostic) =>
        diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
}

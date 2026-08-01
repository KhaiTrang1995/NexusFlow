using System;
using System.IO;
using System.Linq;
using FlowX.Compiler.Emit;
using FlowX.Compiler.Model;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// What <c>.Delay(...)</c> and <c>.OnTimeout(...)</c> compile to.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The class that replaces <c>SuspensionConstructTests</c>, and it is a different
/// question.</strong> That one measured what <c>FLOWX1031</c> reported, because the two
/// constructs compiled to nothing: <c>FlowAnalyzer</c>'s switch reached its <c>default:</c>
/// arm and skipped the call, so a <c>.Delay</c> occupied no index and an <c>.OnTimeout</c>
/// block reached no plan, no dispatcher and no manifest. This one measures what they compile
/// to now, which is the reason that rule is deleted rather than fixed.
/// </para>
/// <para>
/// Through the real generator over a real compilation, for the reason the class it replaces
/// gave: the question is what a developer's build produces, and a model-layer assertion
/// cannot answer it.
/// </para>
/// </remarks>
public sealed class TimerConstructTests
{
    private const string Signal = """
        public sealed record PaymentConfirmed(string Id);
        """;

    // ---------------------------------------------------------------------- Delay

    /// <summary>A <c>.Delay</c> occupies an index of its own, carrying the author's duration.</summary>
    /// <remarks>
    /// <para>
    /// It used to occupy none at all: the step after it took the index it would have had, and
    /// the flow ran straight through with nothing anywhere saying that a wait had been
    /// dropped. The assertion that pinned <em>that</em> behaviour —
    /// <c>APlanIsStillEmittedForAFlowThatOnlyDelays</c> — is the one this replaces.
    /// </para>
    /// <para>
    /// The expression is copied verbatim, the treatment every other duration and options
    /// object in a plan already gets: the generator does not constant-fold, so a wait written
    /// as <c>Waits.Cooling</c> reaches the plan as that.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADelayIsAStepOfItsOwnCarryingTheAuthorsDuration()
    {
        var run = GeneratorHarness.Run(Durable("""
                .Step<ReserveInventory>()
                .Delay(TimeSpan.FromDays(1))
                .Step<CapturePayment>()
            """));

        run.Plan.ShouldContainText(
            "StepNode.ForDelay(1, TimeSpan.FromDays(1))",
            "one day is what the author wrote, at the index they wrote it at.");

        run.Plan.ShouldContainText(
            "StepNode.ForCapability(2, Descriptors.Step2)",
            "and the step after it moves down, because the delay is now really there.");
    }

    /// <summary>A <c>.Delay</c> inside a conditional block is laid out too.</summary>
    /// <remarks>
    /// The trap a rule that only reads the top level falls into, asserted from the other side
    /// now that there is a layout to check rather than a report.
    /// </remarks>
    [Fact]
    public void ADelayInsideAConditionalBlockIsLaidOut() =>
        GeneratorHarness.Run(Durable("""
                .Step<ReserveInventory>()
                .When(ctx => ctx.Input.Sku.Length > 0, b => b.Delay(TimeSpan.FromDays(1)))
            """))
            .Plan.ShouldContainText(
                "StepNode.ForDelay(2, TimeSpan.FromDays(1))",
                "index 0 is the reservation, 1 is the branch, and the block starts at 2.");

    // ------------------------------------------------------------------ OnTimeout

    /// <summary>
    /// An <c>.OnTimeout</c> block is laid out immediately after the wait, and the wait carries
    /// the index a delivered signal jumps to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mirror of a conditional's layout, and it has to be that way round. A
    /// <c>When</c> lays its <c>then</c> block contiguously and carries the <em>false</em>
    /// target, because only one of the two blocks can be contiguous. Here the block that can
    /// be is the escalation — "the steps after the wait" are the rest of the flow and have no
    /// end to jump over — so the timeout path is the ordinary next index and the target names
    /// where a delivered signal carries on.
    /// </para>
    /// <para>
    /// The two rejoin there, which is what makes <c>.Fail(...)</c> inside the block the way to
    /// say "and stop", exactly as it is inside an <c>.Otherwise(...)</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnOnTimeoutBlockIsLaidOutAfterTheWaitAndTheSignalPathSkipsIt()
    {
        var run = GeneratorHarness.Run(Durable("""
                .Step<ReserveInventory>()
                .AwaitSignal<PaymentConfirmed>(TimeSpan.FromDays(7))
                    .OnTimeout(f => f.Step<ReleaseInventory>())
                .Step<CapturePayment>()
            """));

        run.Plan.ShouldContainText(
            "StepNode.ForAwaitSignal(1, \"payment.confirmed\", TimeSpan.FromDays(7), signalTarget: 3)",
            "the wait is at 1, its escalation at 2, and a delivered signal carries on at 3.");

        run.Plan.ShouldContainText(
            "StepNode.ForCapability(2, Descriptors.Step2)",
            "the release is a real step at a real index — it used to be discarded entirely.");

        run.Plan.ShouldContainText(
            "StepNode.ForCapability(3, Descriptors.Step3)",
            "and the capture is where the signal path lands.");
    }

    /// <summary>A wait with no <c>.OnTimeout</c> carries no target, and that is not an omission.</summary>
    /// <remarks>
    /// A target would mean "go here when it expires", and there is nowhere to go: continuing at
    /// the next index would run the steps after the wait against a payload nothing delivered.
    /// So the node carries none and the engine ends the flow with
    /// <c>flow.signal_not_received</c> instead.
    /// </remarks>
    [Fact]
    public void AWaitWithNoOnTimeoutCarriesNoTarget() =>
        GeneratorHarness.Run(Durable("""
                .Step<ReserveInventory>()
                .AwaitSignal<PaymentConfirmed>(TimeSpan.FromDays(7))
                .Step<CapturePayment>()
            """))
            .Plan.ShouldContainText(
                "StepNode.ForAwaitSignal(1, \"payment.confirmed\", TimeSpan.FromDays(7))",
                "no block, no target — and the emitter writes neither rather than a target " +
                "equal to the next index, which would read as an escalation that runs nothing.");

    /// <summary>The steps inside the block reach the dispatcher, which is what a plan needs.</summary>
    /// <remarks>
    /// A node at index 2 that the generated dispatcher has no arm for is a plan the engine
    /// cannot run — so this is not a second way of asserting the layout, it is the half that
    /// makes the layout executable.
    /// </remarks>
    [Fact]
    public void TheBlocksStepsReachTheGeneratedDispatcher()
    {
        var run = GeneratorHarness.Run(Durable("""
                .Step<ReserveInventory>()
                .AwaitSignal<PaymentConfirmed>(TimeSpan.FromDays(7))
                    .OnTimeout(f => f.Step<ReleaseInventory>())
            """));

        run.Plan.ShouldContainText(
            "case 2:",
            "the generated dispatcher has an arm for the block's index — a node the " +
            "dispatcher cannot reach is a plan the engine cannot run.");

        run.Plan.ShouldContainText(
            "ReleaseInventory",
            "and it is the block's capability that arm invokes.");
    }

    // ----------------------------------------------------------------- the profile

    /// <summary>A <c>.Delay</c> below <c>Durable</c> is refused at build time.</summary>
    /// <remarks>
    /// <para>
    /// <c>FLOWX1017</c> covers both kinds of wait since the timer half landed. It could not
    /// cover <c>Delay</c> before, and not because anybody decided it should not: the call
    /// produced no step, so there was nothing for a rule that reads the step model to see.
    /// </para>
    /// <para>
    /// The message names the construct, so an author who wrote a <c>Delay</c> is not told
    /// about an <c>AwaitSignal</c> they did not write.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADelayBelowDurableIsRefusedAtBuildTime()
    {
        var run = GeneratorHarness.Run(FlowPlanGeneratorTests.WithFlow(Signal + "\n\n" +
            """
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Delay(TimeSpan.FromDays(1))
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldContain("FLOWX1017", run.Describe());

        run.Diagnostics
            .Single(d => d.Id == "FLOWX1017")
            .GetMessage(System.Globalization.CultureInfo.InvariantCulture)
            .ShouldContain("Delay");
    }

    /// <summary>
    /// And refused again when the plan is built, which is where a hand-built one meets it.
    /// </summary>
    /// <remarks>
    /// The run-time half of the same rule, and the same argument: there is nowhere outside a
    /// journal to record when a timer is due, so the only way to honour one in memory is to
    /// hold the process for the duration — which is a <c>Task.Delay</c> wearing a plan node.
    /// </remarks>
    [Fact]
    public void ADelayBelowDurableIsRefusedWhenThePlanIsBuilt() =>
        Should.Throw<InvalidFlowPlanException>(() => ExecutionPlan.Create(
                FlowDescriptor.Create(
                    "order.place", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
                StepGraph.Create([StepNode.ForDelay(0, TimeSpan.FromDays(1))])))
            .Message.ShouldContain("Durable profile");

    // ------------------------------------------------------ what is still refused

    /// <summary>The emitter still refuses an <c>AwaitSignal</c> it has no duration for.</summary>
    /// <remarks>
    /// Carried over from <c>SuspensionConstructTests</c> unchanged, because the invariant it
    /// pins is unchanged: this generator does not invent a duration. It never fires —
    /// <c>AwaitSignal&lt;TSignal&gt;(TimeSpan timeout)</c> has no overload without one — and
    /// it is asserted rather than left as untested dead code so that a change which loses the
    /// duration on the way to the emitter is faced with "carry the author's" rather than "put
    /// a constant back".
    /// </remarks>
    [Fact]
    public void TheEmitterStillRefusesAnAwaitSignalItHasNoDurationFor()
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
            steps: [StepModel.AwaitSignal(0, "payment.confirmed", timeoutExpression: null)]);

        Should.Throw<InvalidOperationException>(() => FlowEmitter.Emit(suspending))
            .Message.ShouldContain("the duration the author declared");
    }

    /// <summary>And it refuses a <c>Delay</c> it has no duration for, for the same reason.</summary>
    /// <remarks>
    /// A delay whose duration the compiler invented would be the exact defect
    /// <c>TimeSpan.FromHours(1)</c> was: a flow that waits for a length of time nobody wrote.
    /// The refusal is in place before anything can produce such a model, rather than after.
    /// </remarks>
    [Fact]
    public void TheEmitterRefusesADelayItHasNoDurationFor()
    {
        var delaying = new FlowModel(
            flowId: "offer.accept",
            version: "1.0.0",
            profile: "Durable",
            deadline: "P30D",
            containingNamespace: "Sample.Flows",
            typeName: "AcceptOfferFlow",
            inputTypeName: "Sample.Contracts.Offer",
            outputTypeName: "Sample.Contracts.Acceptance",
            steps: [StepModel.Delay(0, durationExpression: null)]);

        Should.Throw<InvalidOperationException>(() => FlowEmitter.Emit(delaying))
            .Message.ShouldContain("the duration the author declared");
    }

    /// <summary>The emitter contains no fabricated duration of any kind.</summary>
    /// <remarks>
    /// A source-level guard, carried over unchanged. The behavioural tests above can only
    /// observe a constant's absence through a plan; this observes it in the file that would
    /// have to hold it.
    /// </remarks>
    [Fact]
    public void TheEmitterFabricatesNoDuration() =>
        SourceOf("src/FlowX.Compiler/Emit/FlowEmitter.cs")
            .ShouldNotContain(
                "TimeSpan.FromHours(1)",
                Case.Sensitive,
                "A value the author wrote must not be replaced by a constant.");

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

    // ------------------------------------------------------------------ helpers

    private static string Durable(string steps) =>
        FlowPlanGeneratorTests.WithFlow(Signal + "\n\n" +
            $$"""
            [Flow("order.place", Profile = ExecutionProfile.Durable)]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
            {{steps}}
                    .Return(ctx => new OrderResult("id"));
            }
            """);
}

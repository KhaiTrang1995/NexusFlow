using System.Linq;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// The generated payload writer, and <c>FLOWX1006</c> — the rule that makes membership of a
/// source-generated JSON context a build-time fact rather than a convention.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is the only code that can write a journal payload.</strong>
/// <c>JournalPayload.Of</c> demands the generated <c>JsonTypeInfo&lt;T&gt;</c> and has no
/// overload that reflects over a type; the engine's step loop holds a
/// <c>Dictionary&lt;Type, object&gt;</c> and knows no contract types at all. So the writer is
/// generated beside the flow, which is the same division of labour <c>DescribeStep</c>'s event
/// half took in WP-56 — this is that mechanism generalised, not a second one beside it.
/// </para>
/// <para>
/// <strong>And why <c>FLOWX1006</c> could not exist before it.</strong> The requirement is
/// real only where something actually needs the metadata. Until a writer named
/// <c>JsonTypeInfo</c> for a state-bag contract, a rule checking membership would have been
/// checking a requirement nothing had.
/// </para>
/// </remarks>
public sealed class PayloadWriterTests
{
    /// <summary>A context declaring every contract a durable flow's journal touches.</summary>
    private const string CompleteContext = """

        [System.Text.Json.Serialization.JsonSerializable(typeof(PlaceOrder))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(Reservation))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(OrderResult))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(OrderPlaced))]
        public sealed partial class SampleJson : System.Text.Json.Serialization.JsonSerializerContext;
        """;

    /// <summary>The same context with the step's own result contract left out.</summary>
    private const string ContextMissingTheStepResult = """

        [System.Text.Json.Serialization.JsonSerializable(typeof(PlaceOrder))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(OrderResult))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(OrderPlaced))]
        public sealed partial class SampleJson : System.Text.Json.Serialization.JsonSerializerContext;
        """;

    private const string DurableFlow = """

        [Flow("order.place", Profile = ExecutionProfile.Durable)]
        public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                .Step<ReserveInventory>()
                .Return(ctx => new OrderResult("id"));
        }
        """;

    private const string EphemeralFlow = """

        [Flow("order.place")]
        public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                .Step<ReserveInventory>()
                .Return(ctx => new OrderResult("id"));
        }
        """;

    // ---------------------------------------------------------------------------------
    // FLOWX1006
    // ---------------------------------------------------------------------------------

    /// <summary>A state-bag member outside every generated context fails the build.</summary>
    /// <remarks>
    /// WP-59's exit criterion, in one assertion. The message has to name the member, because
    /// the fix is one <c>[JsonSerializable]</c> attribute and the only hard part of applying
    /// it is knowing which type to write down.
    /// </remarks>
    [Fact]
    public void AStateBagMemberOutsideTheGeneratedContextFailsTheBuildNamingIt()
    {
        var run = GeneratorHarness.Run(
            FlowPlanGeneratorTests.WithFlow(ContextMissingTheStepResult + DurableFlow));

        run.Ids.ShouldContain("FLOWX1006", run.Describe());

        var reported = run.Diagnostics.Single(d => d.Id == "FLOWX1006");

        reported.Severity.ShouldBe(Microsoft.CodeAnalysis.DiagnosticSeverity.Error,
            "the rule reports only on Durable flows, so its trigger is the determinism set's " +
            "escalation condition and there is no case left to warn about.");

        var message = reported.GetMessage(System.Globalization.CultureInfo.InvariantCulture);

        message.ShouldContain("Reservation", Case.Sensitive,
            "naming the flow but not the contract leaves the reader to work out which of " +
            "its step results is missing.");
        message.ShouldContain("order.place", Case.Sensitive);
        message.ShouldContain("JsonSerializable", Case.Sensitive);
    }

    /// <summary>A state-bag member inside the generated context is silent.</summary>
    [Fact]
    public void AStateBagMemberInsideTheGeneratedContextIsSilent()
    {
        var run = GeneratorHarness.Run(
            FlowPlanGeneratorTests.WithFlow(CompleteContext + DurableFlow));

        run.Ids.ShouldBeEmpty(run.Describe());
    }

    /// <summary>An ephemeral flow keeps no journal, so there is nothing to be missing.</summary>
    /// <remarks>
    /// The rule is not "contracts should be serialisable in general" — that would be a
    /// different, weaker rule under a number already spoken for. It is "this journal has to
    /// write this contract", and an ephemeral flow has no journal.
    /// </remarks>
    [Fact]
    public void AnEphemeralFlowIsNeverReported()
    {
        var run = GeneratorHarness.Run(
            FlowPlanGeneratorTests.WithFlow(ContextMissingTheStepResult + EphemeralFlow));

        run.Ids.ShouldBeEmpty(run.Describe());
    }

    /// <summary>The flow's own input is a state-bag member like any other.</summary>
    /// <remarks>
    /// The engine puts the input in the bag before the first step — that is how
    /// <c>ctx.Get&lt;PlaceOrder&gt;()</c> resolves in a generated dispatcher — so a resume
    /// that cannot rehydrate it re-runs every step that reads it against nothing.
    /// </remarks>
    [Fact]
    public void TheFlowInputIsCheckedTooBecauseTheEngineWritesItIntoTheBag()
    {
        var run = GeneratorHarness.Run(FlowPlanGeneratorTests.WithFlow("""

            [System.Text.Json.Serialization.JsonSerializable(typeof(Reservation))]
            [System.Text.Json.Serialization.JsonSerializable(typeof(OrderResult))]
            public sealed partial class SampleJson : System.Text.Json.Serialization.JsonSerializerContext;
            """ + DurableFlow));

        run.Diagnostics
            .Where(d => d.Id == "FLOWX1006")
            .Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .ShouldContain(m => m.Contains("PlaceOrder", System.StringComparison.Ordinal), run.Describe());
    }

    /// <summary>Two contexts declaring one contract is the same answer as none.</summary>
    /// <remarks>
    /// Picking the first of several would make the stored shape depend on file order, which
    /// is <c>EndpointEmitter</c>'s rule and <c>FLOWX1024</c>'s, applied to the journal.
    /// </remarks>
    [Fact]
    public void TwoContextsDeclaringTheSameContractIsTheSameAnswerAsNone()
    {
        var run = GeneratorHarness.Run(FlowPlanGeneratorTests.WithFlow(CompleteContext + """

            [System.Text.Json.Serialization.JsonSerializable(typeof(Reservation))]
            public sealed partial class SecondJson : System.Text.Json.Serialization.JsonSerializerContext;
            """ + DurableFlow));

        run.Diagnostics
            .Where(d => d.Id == "FLOWX1006")
            .Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .ShouldContain(m => m.Contains("Reservation", System.StringComparison.Ordinal), run.Describe());
    }

    // ---------------------------------------------------------------------------------
    // The writer
    // ---------------------------------------------------------------------------------

    /// <summary>A step's result reaches the journal as a redacted payload.</summary>
    [Fact]
    public void ADescribedStepCarriesItsResultThroughTheGeneratedContext()
    {
        var plan = GeneratorHarness
            .Run(FlowPlanGeneratorTests.WithFlow(CompleteContext + DurableFlow))
            .Plan;

        plan.ShouldContainText("public StepJournalEntry DescribeStep(int stepIndex, FlowContext ctx)",
            "nothing journals a payload until the dispatcher describes one.");
        plan.ShouldContainText("return StepJournalEntry.Of(", "as a result and a state bag,");
        plan.ShouldContainText("JournalPayload.Of(", "each wrapped in the type that redacts,");
        plan.ShouldContainText("ctx.TryGet<Sample.Reservation>(out var described)",
            "reading what the step produced — and journaling no result rather than failing a\n" +
            "            flow whose step succeeded without writing one,");
        plan.ShouldContainText("global::Sample.SampleJson.Default", "written by the declaring context,");
        plan.ShouldContainText("SensitiveMembers",
            "and carrying the flow's SensitiveMembers, which is ADR-0015's commitment 5 and " +
            "the obligation this package would otherwise forget.");
    }

    /// <summary>
    /// The state bag is composed inside <c>JournalPayload</c>, not assembled by the writer.
    /// </summary>
    /// <remarks>
    /// <strong>The assertion the whole package turns on.</strong> A generated writer is a
    /// second exit from the journal. It stays safe only because it hands named values to
    /// <c>JournalPayload.OfState</c> and never composes a document itself — so the state bag
    /// leaves through the one <c>ToJson</c> that redacts, exactly like the event body does.
    /// A writer that reached for a <c>Utf8JsonWriter</c> here would be re-implementing
    /// redaction beside the property rather than inheriting it.
    /// </remarks>
    [Fact]
    public void TheStateBagIsHandedToJournalPayloadRatherThanComposedByTheWriter()
    {
        var plan = GeneratorHarness
            .Run(FlowPlanGeneratorTests.WithFlow(CompleteContext + DurableFlow))
            .Plan;

        plan.ShouldContainText("JournalPayload.OfState(", "the bag is composed by the payload type,");
        plan.ShouldContainText("JournalMember.Of(", "out of named members,");
        plan.ShouldContainText("ctx.TryGet<Sample.PlaceOrder>(out var",
            "including only what the bag actually holds at this step.");

        plan.ShouldNotContainText("Utf8JsonWriter",
            "a writer that serialised the bag itself would be a second exit from " +
            "JournalPayload and would need its own copy of the redaction pass.");
        plan.ShouldNotContainText("JsonSerializer.Serialize",
            "and so would one that called the serialiser directly.");
    }

    /// <summary>What was written can be read back, by the only code that can name the types.</summary>
    [Fact]
    public void TheWriterEmitsTheRestoreThatMirrorsIt()
    {
        var plan = GeneratorHarness
            .Run(FlowPlanGeneratorTests.WithFlow(CompleteContext + DurableFlow))
            .Plan;

        plan.ShouldContainText("public void RestoreState(FlowContext ctx, string stateBagJson)",
            "a dispatcher that writes a state bag and cannot read it back makes a resumed " +
            "instance run the rest of the flow against values no step produced.");
        plan.ShouldContainText("ctx.Set(", "and rehydration is a ctx.Set of the stored value.");
    }

    /// <summary>The trigger input is describable, which is what lets the host journal it.</summary>
    /// <remarks>
    /// <c>FlowHost</c> passes <c>input: null</c> to <c>BeginAsync</c> because it cannot name a
    /// <c>JsonTypeInfo</c> for the flow's input contract. This is the call that can.
    /// </remarks>
    [Fact]
    public void TheWriterDescribesTheTriggerInput()
    {
        var plan = GeneratorHarness
            .Run(FlowPlanGeneratorTests.WithFlow(CompleteContext + DurableFlow))
            .Plan;

        plan.ShouldContainText("public JournalPayload DescribeInput(object? input)", "the host cannot name the contract; this can.");
        plan.ShouldContainText("input is Sample.PlaceOrder", "and it is the flow's own declared input.");
    }

    /// <summary>An ephemeral flow's dispatcher describes nothing, and pays for nothing.</summary>
    /// <remarks>
    /// Budget B2 is a hard zero on the ephemeral path. The engine never calls
    /// <c>DescribeStep</c> for an ephemeral flow, but emitting the writer would still put the
    /// candidate list, the <c>TryGet</c> chain and the member array into a type every
    /// ephemeral flow loads — so it is not emitted at all.
    /// </remarks>
    [Fact]
    public void AnEphemeralFlowGetsNoWriterAtAll()
    {
        var plan = GeneratorHarness
            .Run(FlowPlanGeneratorTests.WithFlow(CompleteContext + EphemeralFlow))
            .Plan;

        plan.ShouldNotContainText("JournalPayload.OfState(", "an ephemeral flow keeps no journal.");
        plan.ShouldNotContainText("public void RestoreState(", "so there is nothing to restore.");
        plan.ShouldNotContainText("public JournalPayload DescribeInput(", "and no instance row to record an input on.");
    }
}

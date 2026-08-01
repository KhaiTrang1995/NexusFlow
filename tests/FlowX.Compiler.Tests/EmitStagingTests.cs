using System.Linq;
using FlowX.Compiler.Emit;
using FlowX.Compiler.Model;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// The generated half of the outbox: an <c>.Emit</c> step describes an event for the engine
/// to stage.
/// </summary>
/// <remarks>
/// <para>
/// The engine can never build this itself. <c>JournalPayload.Of</c> demands the generated
/// metadata for the contract and the step loop holds a <c>Dictionary&lt;Type, object&gt;</c>,
/// so the only code that can name a <c>JsonTypeInfo&lt;OrderPlaced&gt;</c> is the code
/// generated beside the flow that emits one. That is why <c>DescribeStep</c> is here rather
/// than in <c>FlowEngine</c>, and why this file exists at all.
/// </para>
/// <para>
/// Three conditions have to hold before a row can be staged, and each one that does not is a
/// <c>FLOWX1024</c>. They are asserted separately, because a single "it works" test would go
/// on passing while two of the three quietly stopped mattering.
/// </para>
/// </remarks>
public sealed class EmitStagingTests
{
    private const string Contract = "Sample.Contracts.OrderPlaced";

    private static readonly JsonContextModel Declaring =
        new("Sample.SampleJson", [Contract, "Sample.Contracts.PlaceOrder"]);

    private static readonly JsonContextModel Unrelated =
        new("Sample.OtherJson", ["Sample.Contracts.PlaceOrder"]);

    /// <summary>The P0 flow, declared <c>Durable</c> and emitting a resolved contract.</summary>
    private static FlowModel Emitting(string profile = "Durable") => new(
        flowId: "order.place",
        version: "1.0.0",
        profile: profile,
        deadline: "PT30S",
        containingNamespace: "Sample.Flows",
        typeName: "PlaceOrderFlow",
        inputTypeName: "Sample.Contracts.PlaceOrder",
        outputTypeName: "Sample.Contracts.OrderPlacedResult",
        steps:
        [
            Models.Validate(0),
            StepModel.Emit(
                1,
                "order.placed",
                location: "/src/Flows/Place.cs:20",
                contractTypeName: Contract,
                factory: "ctx => new OrderPlaced(ctx.Input.Sku)",
                factoryLocation: "/src/Flows/Place.cs:21"),
        ],
        sensitiveInputMembers: ["PaymentToken"]);

    // ---------------------------------------------------------------------------------
    // What is emitted when everything holds
    // ---------------------------------------------------------------------------------

    [Fact]
    public void TheFactoryIsACachedStaticCarryingTheAuthorsOwnExpression()
    {
        var source = FlowEmitter.Emit(Emitting(), [Declaring]);

        source.ShouldContainText(
            "public static readonly Func<FlowContext<Sample.Contracts.PlaceOrder>, " +
            "Sample.Contracts.OrderPlaced> Step1 = ctx => new OrderPlaced(ctx.Input.Sku);",
            "The event body is built once at type initialisation, like every other copied " +
            "expression, so describing a step costs no delegate.");

        source.ShouldContainText(
            "#line 21 \"/src/Flows/Place.cs\"",
            "and it sits inside a #line pair, so a breakpoint on the event body lands on the " +
            "body the author wrote.");
    }

    [Fact]
    public void TheDispatcherDescribesTheEmitStepAsAnOutboxWrite()
    {
        var source = FlowEmitter.Emit(Emitting(), [Declaring]);

        source.ShouldContainText(
            "public StepJournalEntry DescribeStep(int stepIndex, FlowContext ctx)",
            "Nothing stages an event until the dispatcher describes one.");

        source.ShouldContainText("return StepJournalEntry.OfEvent(new OutboxWrite", "as an outbox write.");
        source.ShouldContainText("Type = \"order.placed\",", "carrying the identity the manifest publishes.");
        source.ShouldContainText(
            "SchemaVersion = \"" + ManifestWriter.EventSchemaVersion + "\",",
            "and the same schema version the manifest's events array stamps.");
    }

    /// <summary>
    /// The payload is a <c>JournalPayload</c> through the flow's own context and members.
    /// </summary>
    /// <remarks>
    /// This is the whole of the <c>[Sensitive]</c> guarantee for events, and it is structural
    /// rather than remembered: the emitted call has no overload that takes a string, so there
    /// is no shape of this generated code that puts a marked member on a broker in the clear.
    /// </remarks>
    [Fact]
    public void ThePayloadIsWrittenThroughTheGeneratedContextAndCarriesSensitiveMembers()
    {
        var source = FlowEmitter.Emit(Emitting(), [Declaring]);

        source.ShouldContainText("Payload = JournalPayload.Of(", "The body is a JournalPayload.");
        source.ShouldContainText("Events.Step1(Typed(ctx)),", "built from the author's factory,");
        source.ShouldContainText("global::Sample.SampleJson.Default,", "written by the declaring context,");
        source.ShouldContainText(
            "SensitiveMembers),",
            "and redacted by the flow's own array. An event body reaches every consumer a " +
            "broker has; it must not be a weaker sink than the journal row beside it.");
    }

    [Fact]
    public void ThePartitionKeyIsTheInstanceBecauseThatIsTheOnlyOrderingOffered()
    {
        FlowEmitter.Emit(Emitting(), [Declaring]).ShouldContainText(
            "PartitionKey = ctx.FlowInstanceId,",
            "ADR-0018 offers ordering per partition key and no global order, so an " +
            "instance's own events are the sequence a consumer can rely on.");
    }

    // ---------------------------------------------------------------------------------
    // What is emitted when one of the three conditions fails
    // ---------------------------------------------------------------------------------

    [Fact]
    public void AnEphemeralFlowDescribesNoEventBecauseThereIsNoTransactionToStageItIn()
    {
        var source = FlowEmitter.Emit(Emitting("Ephemeral"), [Declaring]);

        source.ShouldNotContainText(
            "DescribeStep",
            "An ephemeral execution keeps no journal, so there is no commit for the row to " +
            "join. Describing one anyway would be a promise the engine cannot keep.");
    }

    /// <summary>
    /// A contract no context declares stages no event — and, since WP-59, still journals.
    /// </summary>
    /// <remarks>
    /// The assertion moved from "there is no <c>DescribeStep</c>" to "there is no
    /// <c>OutboxWrite</c> in it", and the difference is the whole of the payload writer. A
    /// durable flow describes every step boundary now, so the dispatcher has a
    /// <c>DescribeStep</c> whether or not any event can be staged; what an undeclarable event
    /// contract costs is the outbox row, and only that.
    /// </remarks>
    [Fact]
    public void AContractNoContextDeclaresDescribesNoEvent()
    {
        var source = FlowEmitter.Emit(Emitting(), [Unrelated]);

        source.ShouldNotContainText(
            "OutboxWrite",
            "JournalPayload.Of takes a JsonTypeInfo and has no overload that reflects over a " +
            "type. Without a declaring context there is nothing that could write the body.");

        source.ShouldContainText(
            "StateBag(ctx)",
            "the step boundary is still journaled: the flow is Durable and the state bag's " +
            "own contracts are declared, so the row that cannot carry an event still carries " +
            "everything else.");
    }

    /// <summary>Two contexts declaring the contract is the same answer as none.</summary>
    /// <remarks>
    /// Picking the first would make the event's wire shape depend on file order, which is the
    /// reason <c>EndpointEmitter</c> declines to pick one for a request body either.
    /// </remarks>
    [Fact]
    public void TwoContextsDeclaringTheSameContractDescribeNoEvent()
    {
        var second = new JsonContextModel("Sample.SecondJson", [Contract]);

        FlowEmitter.Emit(Emitting(), [Declaring, second]).ShouldNotContainText(
            "OutboxWrite",
            "None and several are the same answer, and for the same reason.");
    }

    /// <summary>A flow with no emit step stages nothing, and journals its bag.</summary>
    /// <remarks>
    /// <c>Models.LinearQuery</c> is <c>Durable</c>, so before WP-59 it produced a dispatcher
    /// with no <c>DescribeStep</c> at all and inherited the interface's default — a journal of
    /// step boundaries with no payloads. It now describes its state bag. The event half is
    /// what this test is about, and that half is unchanged.
    /// </remarks>
    [Fact]
    public void AFlowWithNoEmitStepDescribesNothing()
    {
        var source = FlowEmitter.Emit(Models.LinearQuery(), [Declaring]);

        source.ShouldNotContainText(
            "OutboxWrite",
            "There is no emit step, so there is no event to stage.");

        source.ShouldContainText(
            "JournalPayload.OfState(",
            "and what it does describe is the bag, which is what makes it resumable.");
    }

    // ---------------------------------------------------------------------------------
    // FLOWX1024, re-scoped
    // ---------------------------------------------------------------------------------

    private const string DurableEmittingFlow = """
        [Flow("order.place", Profile = ExecutionProfile.Durable)]
        public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                .Step<ReserveInventory>()
                .Emit<OrderPlaced>(ctx => new OrderPlaced("sku"))
                .Return(ctx => new OrderResult("id"));
        }
        """;

    /// <summary>A context declaring the event body and every contract the journal writes.</summary>
    /// <remarks>
    /// The last two lines are WP-59's: the flow is <c>Durable</c>, so its input and its step's
    /// result are journal payloads and <c>FLOWX1006</c> requires the same metadata for them.
    /// Without them these tests would report two findings and be about neither.
    /// </remarks>
    private const string SerialiserContext = """

        [System.Text.Json.Serialization.JsonSerializable(typeof(OrderPlaced))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(PlaceOrder))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(Reservation))]
        public sealed partial class SampleJson : System.Text.Json.Serialization.JsonSerializerContext
        {
        }
        """;

    /// <summary>The same, minus the event contract. The one condition under test.</summary>
    private const string ContextWithoutTheEvent = """

        [System.Text.Json.Serialization.JsonSerializable(typeof(PlaceOrder))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(Reservation))]
        public sealed partial class SampleJson : System.Text.Json.Serialization.JsonSerializerContext
        {
        }
        """;

    [Fact]
    public void AStageableEmitRaisesNothing()
    {
        var run = GeneratorHarness.Run(FlowPlanGeneratorTests.WithFlow(DurableEmittingFlow + SerialiserContext));

        run.Ids.ShouldBeEmpty(run.Describe());
    }

    [Fact]
    public void AnEphemeralEmitIsReportedAndSaysWhichProfileWouldPublishIt()
    {
        var run = GeneratorHarness.Run(FlowPlanGeneratorTests.WithFlow(
            DurableEmittingFlow.Replace(", Profile = ExecutionProfile.Durable", string.Empty) +
            SerialiserContext));

        run.Ids.ShouldBe(["FLOWX1024"], run.Describe());
        run.Describe().ShouldContainText("Profile = Durable",
            "The fix is in user code now, so the message has to name it.");
    }

    [Fact]
    public void AnEmitWhoseContractNoContextDeclaresIsReportedAndSaysSo()
    {
        var run = GeneratorHarness.Run(
            FlowPlanGeneratorTests.WithFlow(DurableEmittingFlow + ContextWithoutTheEvent));

        run.Ids.ShouldBe(["FLOWX1024"], run.Describe());
        run.Describe().ShouldContainText("JsonSerializable",
            "and it names the attribute that would fix it.");
    }

    /// <summary>The generated dispatcher is not merely parseable; it binds.</summary>
    /// <remarks>
    /// <para>
    /// The harness does not run System.Text.Json's own generator, so the serialiser context
    /// here is hand-written: a real <c>JsonSerializerContext</c> with a <c>Default</c>
    /// property, which is what the emitted call site names. That is enough to prove the
    /// binding this test is about — <c>JournalPayload.Of(value, context, members)</c>
    /// resolving, <c>Events.Step1</c> being typed at the contract, and <c>DescribeStep</c>
    /// satisfying <c>IStepDispatcher</c>.
    /// </para>
    /// <para>
    /// The end-to-end proof that a real build's generated context works is in
    /// <c>FlowX.Postgres.Tests</c>, where both generators run over the same project.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheGeneratedDispatcherCompiles()
    {
        var errors = GeneratorHarness.GeneratedCompileErrorsIn(FlowPlanGeneratorTests.WithFlow(
            DurableEmittingFlow + HandWrittenContext));

        errors.ShouldBeEmpty(string.Join("\n", errors));
    }

    private const string HandWrittenContext = """

        [System.Text.Json.Serialization.JsonSerializable(typeof(OrderPlaced))]
        public sealed class SampleJson : System.Text.Json.Serialization.JsonSerializerContext
        {
            public SampleJson() : base(null) { }

            public static SampleJson Default { get; } = new SampleJson();

            protected override System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions => null;

            public override System.Text.Json.Serialization.Metadata.JsonTypeInfo? GetTypeInfo(System.Type type) => null;
        }
        """;

    /// <summary>Every emit step of a flow with several is described, in flat-index order.</summary>
    [Fact]
    public void EveryStageableEmitGetsItsOwnCase()
    {
        var flow = new FlowModel(
            flowId: "order.place",
            version: "1.0.0",
            profile: "Durable",
            deadline: null,
            containingNamespace: "Sample.Flows",
            typeName: "PlaceOrderFlow",
            inputTypeName: "Sample.Contracts.PlaceOrder",
            outputTypeName: "Sample.Contracts.OrderPlacedResult",
            steps:
            [
                StepModel.Emit(0, "order.placed", contractTypeName: Contract, factory: "ctx => new A()"),
                Models.Validate(1),
                StepModel.Emit(2, "order.paid", contractTypeName: Contract, factory: "ctx => new B()"),
            ]);

        var source = FlowEmitter.Emit(flow, [Declaring]);
        var describe = source[source.IndexOf("public StepJournalEntry DescribeStep", System.StringComparison.Ordinal)..];

        describe.IndexOf("case 0:", System.StringComparison.Ordinal)
            .ShouldBeLessThan(describe.IndexOf("case 2:", System.StringComparison.Ordinal));

        describe.Contains("case 1:", System.StringComparison.Ordinal).ShouldBeFalse(
            "A step that emits nothing describes nothing, and a case that returned Nothing " +
            "would be a line the reader has to check is harmless.");
    }
}

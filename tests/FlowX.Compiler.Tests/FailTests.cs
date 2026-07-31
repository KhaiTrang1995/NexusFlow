using System;
using System.Linq;
using FlowX.Compiler.Emit;
using FlowX.Compiler.Model;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// <c>.Fail(error)</c>: the terminal step.
/// </summary>
/// <remarks>
/// <para>
/// The builder has declared it since P0 and <c>08-Flow-Definition.md §4</c> has listed it
/// for as long, and until this work package the analyzer did not model it — so a block
/// whose only call was <c>.Fail(...)</c> compiled to an <em>empty</em> block. That is worse
/// than a no-op where it matters most: <c>08 §3.2</c>'s own worked example rejects an
/// unsupported channel with <c>.Default(b =&gt; b.Fail(OrderErrors.UnsupportedChannel))</c>,
/// and an empty default is the documented fall-through — so the arm written to reject the
/// request accepted it.
/// </para>
/// <para>
/// Two decisions are pinned here rather than left to be rediscovered. A <c>Fail</c> takes
/// the ordinary failure path, so the compensable steps that completed before it unwind;
/// and the <c>Error</c> reaches the generated dispatcher and never the manifest.
/// </para>
/// </remarks>
public sealed class FailTests
{
    private const string Preamble = """
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record PlaceOrder(string Sku, Channel Channel);
        public sealed record OrderResult(string Id);
        public sealed record Reservation(string Sku);

        public enum Channel { Retail, Wholesale, Partner }

        public static class OrderErrors
        {
            public static Error UnsupportedChannel { get; } =
                new Error("order.unsupported_channel", "That channel is not served.", ErrorCategory.Validation);
        }

        [Capability("inventory.reserve", Version = "1.2.0",
            Authorization = Authorization.Authenticated,
            Idempotent = true, SideEffects = new[] { "inventory-ledger" })]
        public sealed class ReserveInventory : ICapability<PlaceOrder, Reservation>
        {
            public ValueTask<Result<Reservation>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Reservation(input.Sku)));
        }

        [Capability("inventory.release", Version = "1.2.0",
            Authorization = Authorization.Internal, Idempotent = true)]
        public sealed class ReleaseInventory : ICapability<PlaceOrder, Reservation>
        {
            public ValueTask<Result<Reservation>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Reservation(input.Sku)));
        }

        [Capability("payment.capture", Version = "2.1.0",
            Authorization = Authorization.Permission, Permission = "payment.write",
            SideEffects = new[] { "payment-gateway" })]
        public sealed class CapturePayment : ICapability<Reservation, OrderResult>
        {
            public ValueTask<Result<OrderResult>> ExecuteAsync(Reservation input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new OrderResult(input.Sku)));
        }
        """;

    /// <summary>The §3.2 example, verbatim in shape: two priced channels and a rejected default.</summary>
    private const string UnsupportedChannelFlow = """
        [Flow("order.price")]
        public sealed partial class PriceOrderFlow : Flow<PlaceOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                .Switch(ctx => ctx.Input.Channel)
                .Case(Channel.Retail, b => b.Step<ReserveInventory>())
                .Case(Channel.Wholesale, b => b.Step<ReserveInventory>())
                .Default(b => b.Fail(OrderErrors.UnsupportedChannel))
                .Return(ctx => new OrderResult("id"));
        }
        """;

    private static string WithFlow(string flowDeclaration) => Preamble + "\n\n" + flowDeclaration;

    [Fact]
    public void TheDocumentedDefaultArmCompilesToAStepRatherThanToNothing()
    {
        var source = WithFlow(UnsupportedChannelFlow);

        GeneratorHarness.CompileErrorsIn(source).ShouldBeEmpty();
        GeneratorHarness.GeneratedCompileErrorsIn(source).ShouldBeEmpty();

        var run = GeneratorHarness.Run(source);

        run.Ids.ShouldBeEmpty(run.Describe());

        // 0 switch · 1 reserve · 2 jump · 3 reserve · 4 jump · 5 fail. The default target
        // is 5 and not the join, which is the whole of the defect: it used to be the join,
        // and a value nothing matched simply continued.
        run.Plan.ShouldContainText("defaultTarget: 5", run.Describe());
        run.Plan.ShouldContainText("StepNode.ForFail(5)", run.Describe());
    }

    [Fact]
    public void TheErrorReachesTheDispatcherAsAStaticFieldAndTheStepReturnsIt()
    {
        var run = GeneratorHarness.Run(WithFlow(UnsupportedChannelFlow));

        // A field, so rejecting a request allocates nothing at the moment the flow is
        // already about to unwind — and the author's own expression, so a factory that
        // decorates the error with structured detail compiles as written.
        run.Plan.ShouldContainText(
            "public static readonly Error Step5 = OrderErrors.UnsupportedChannel;",
            run.Describe());

        run.Plan.ShouldContainText(
            "return StepOutcome.Failed(Failures.Step5);",
            "A Fail is delivered on the same call a declined payment comes back on, so the " +
            "engine takes the identical failure path.");
    }

    [Fact]
    public void TheManifestPublishesThatTheArmFailsAndNeverWhatItFailsWith()
    {
        var json = GeneratorHarness.Run(WithFlow(UnsupportedChannelFlow)).ManifestJson;

        json.ShouldNotBeNull();
        json.ShouldContainText("\"kind\": \"Fail\"", "The shape of the flow is structure.");

        json.ShouldNotContainText(
            "OrderErrors",
            "The expression is business data, exactly as a predicate and a case value are.");
        json.ShouldNotContainText(
            "order.unsupported_channel",
            "And so is the code: the committed schema's step object is " +
            "additionalProperties:false and has no field for one, so publishing it would " +
            "be a schema change rather than a step kind.");
        json.ShouldNotContainText(
            "That channel is not served.",
            "A message is the one field of an Error that routinely interpolates business " +
            "values. It must never reach a published artifact.");
    }

    [Fact]
    public void AFailAtTheTopLevelEndsTheFlowAndStepsAfterItAreReported()
    {
        var source = WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Fail(OrderErrors.UnsupportedChannel)
                    .Step<CapturePayment>()
                    .Return(ctx => new OrderResult("id"));
            }
            """);

        GeneratorHarness.GeneratedCompileErrorsIn(source).ShouldBeEmpty();

        var run = GeneratorHarness.Run(source);

        run.Ids.ShouldBe(["FLOWX1027"], run.Describe());

        run.Plan.ShouldContainText("StepNode.ForFail(1)", run.Describe());
        run.Plan.ShouldNotContainText(
            "StepNode.ForCapability(2",
            "Control never leaves a Fail, so the step after it is not laid out — a plan, a " +
            "manifest and a diagram that listed it would all claim work the flow cannot do.");
    }

    [Fact]
    public void AReturnAfterAFailIsNotReportedAsUnreachable()
    {
        // `.Return(...)` declares no step: it is the output projection, it is read off the
        // chain rather than laid out, and the engine already does not run it when the flow
        // failed. It is also the only way to spell a flow that always rejects, so reporting
        // it would fire on the one shape that has to type-check.
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Fail(OrderErrors.UnsupportedChannel)
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldBeEmpty(run.Describe());
    }

    [Fact]
    public void AFailInsideOneBranchDoesNotEndTheEnclosingChain()
    {
        // The block a Fail terminates is its own. A `then` block that rejects says nothing
        // about what follows the conditional — that is the whole point of putting it there.
        var source = WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .When(ctx => ctx.Input.Channel == Channel.Partner, no => no
                        .Fail(OrderErrors.UnsupportedChannel))
                    .Step<ReserveInventory>()
                    .Step<CapturePayment>()
                    .Return(ctx => new OrderResult("id"));
            }
            """);

        GeneratorHarness.GeneratedCompileErrorsIn(source).ShouldBeEmpty();

        var run = GeneratorHarness.Run(source);

        run.Ids.ShouldBeEmpty(run.Describe());

        // 0 branch(else 2) · 1 fail · 2 reserve · 3 capture.
        run.Plan.ShouldContainText("StepNode.ForBranch(0, 2)", run.Describe());
        run.Plan.ShouldContainText("StepNode.ForFail(1)", run.Describe());
        run.Plan.ShouldContainText("StepNode.ForCapability(2, Descriptors.Step2)", run.Describe());
    }

    [Fact]
    public void TheModelCarriesTheExpressionAndOccupiesOneIndex()
    {
        var step = StepModel.Fail(3, "OrderErrors.UnsupportedChannel", "/src/Flows/Price.cs:9");

        step.Kind.ShouldBe(StepKindModel.Fail);
        step.FailureExpression.ShouldBe("OrderErrors.UnsupportedChannel");
        step.NextIndex.ShouldBe(4, "A Fail occupies exactly one index and carries no block.");
        step.SelfAndNested.Count().ShouldBe(1);
    }

    [Fact]
    public void TheEmittedErrorFieldCarriesALineDirectiveBackToTheAuthorsExpression()
    {
        // The one thing a developer wants when an unexpected rejection reaches production
        // is a breakpoint on the line that declared it.
        var source = FlowEmitter.Emit(new FlowModel(
            flowId: "order.price",
            version: "1.0.0",
            profile: "Ephemeral",
            deadline: null,
            containingNamespace: "Sample.Flows",
            typeName: "PriceOrderFlow",
            inputTypeName: "Sample.Contracts.PlaceOrder",
            outputTypeName: "Sample.Contracts.OrderResult",
            steps: [StepModel.Fail(0, "OrderErrors.UnsupportedChannel", "/src/Flows/Price.cs:9")]));

        source.ShouldContainText("#line 9 \"/src/Flows/Price.cs\"", "The error is the author's expression.");
        source.ShouldContainText("public static readonly Error Step0 = OrderErrors.UnsupportedChannel;", source);
    }

    [Fact]
    public void AFlowThatOnlyFailsEmitsNoFailuresClassItDoesNotNeed()
    {
        // The negative half of the same rule the conditions, selectors and iterations
        // follow: a flow that declares none of a shape gets none of its scaffolding.
        var source = FlowEmitter.Emit(Models.PlaceOrder());

        source.ShouldNotContainText("class Failures", "This flow declares no Fail.");
        source.ShouldNotContainText("StepNode.ForFail", "and no terminal node.");
    }
}

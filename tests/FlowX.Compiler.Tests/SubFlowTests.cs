using System.Linq;
using FlowX.Compiler.Analysis;
using FlowX.Compiler.Emit;
using FlowX.Compiler.Model;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// Composition through the compiler: the model layer, the emitted source, the manifest,
/// and the two rules that refuse a composition the runtime could not honour.
/// </summary>
/// <remarks>
/// <para>
/// The claim this shape rests on is that a sub-flow costs the parent <em>one index and no
/// layout</em>. Every other composite shape derives targets, jumps and a join from blocks
/// it was handed; this one has no blocks, because the child's steps belong to the child's
/// own model — compiled separately, possibly in another assembly. So the tests are about
/// what is <em>absent</em> as much as what is present.
/// </para>
/// <para>
/// The generator tests use <c>GeneratedCompileErrorsIn</c> rather than only reading the
/// text. Composition is the first shape whose emitted code names a type this flow does not
/// own — <c>FulfilOrderFlow.Plan</c>, <c>FulfilOrderFlow.Dispatcher</c> — so "the text looks
/// right" and "the text compiles" are further apart here than anywhere else in the DSL.
/// </para>
/// </remarks>
public sealed class SubFlowTests
{
    private const string Preamble = """
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record PlaceOrder(string Sku);
        public sealed record OrderResult(string Id);
        public sealed record FulfilOrder(string Sku);
        public sealed record Fulfilled(string Sku);
        public sealed record Reservation(string Sku);

        [Capability("inventory.reserve", Version = "1.2.0",
            Authorization = Authorization.Authenticated, Idempotent = true)]
        public sealed class ReserveInventory : ICapability<PlaceOrder, Reservation>
        {
            public ValueTask<Result<Reservation>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Reservation(input.Sku)));
        }

        [Capability("inventory.pick", Version = "1.0.0",
            Authorization = Authorization.Internal, Idempotent = true)]
        public sealed class PickStock : ICapability<FulfilOrder, Fulfilled>
        {
            public ValueTask<Result<Fulfilled>> ExecuteAsync(FulfilOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Fulfilled(input.Sku)));
        }

        [Flow("order.fulfil")]
        public sealed partial class FulfilOrderFlow : Flow<FulfilOrder, Fulfilled>
        {
            protected override void Define(IFlowBuilder<FulfilOrder, Fulfilled> flow) => flow
                .Step<PickStock>()
                .Return(ctx => ctx.Get<Fulfilled>());
        }
        """;

    private static string WithFlow(string flowDeclaration) => Preamble + "\n\n" + flowDeclaration;

    private const string Composing = """
        [Flow("order.place")]
        public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                .Step<ReserveInventory>()
                .SubFlow<FulfilOrderFlow, FulfilOrder>(ctx => new FulfilOrder(ctx.Get<PlaceOrder>().Sku))
                .Return(ctx => new OrderResult("id"));
        }
        """;

    [Fact]
    public void TheTestPreambleItselfCompiles()
        // Guards the harness. Without it, a typo in the shared source shows up as "the
        // generator found no flows" and the next hour goes into the wrong file.
        => GeneratorHarness.CompileErrorsIn(WithFlow(Composing)).ShouldBeEmpty();

    // ------------------------------------------------------------------------ the model

    [Fact]
    public void ACompositionOccupiesOneIndexAndCarriesNoLayout()
    {
        var composition = StepModel.SubFlow(
            1,
            "order.fulfil",
            "Sample.FulfilOrderFlow",
            "Sample.FulfilOrder",
            "ctx => new FulfilOrder(ctx.Get<PlaceOrder>().Sku)",
            "Inline");

        composition.Kind.ShouldBe(StepKindModel.SubFlow);
        composition.NextIndex.ShouldBe(2,
            "One index, like a capability step. Every other composite kind occupies " +
            "everything up to a join it derived from its blocks; this has no blocks.");

        composition.SelfAndNested.Count().ShouldBe(1,
            "The child's steps are in the child's own model. A parent that composes a " +
            "hundred-step flow still has one step here — which is what keeps the manifest " +
            "the size of the flow its author wrote.");

        composition.Then.ShouldBeEmpty();
        composition.Cases.ShouldBeEmpty();
        composition.Branches.ShouldBeEmpty();
        composition.Body.ShouldBeEmpty();
        composition.JumpIndex.ShouldBeNull();
    }

    // -------------------------------------------------------------------- the generator

    [Fact]
    public void TheGeneratedCodeCompiles()
        // The claim the emitter cannot make for itself. Composition is the first shape
        // whose emitted source names types this flow does not own, so a wrong name here is
        // a build break in the consumer's project rather than a wrong string in a test.
        => GeneratorHarness.GeneratedCompileErrorsIn(WithFlow(Composing)).ShouldBeEmpty();

    [Fact]
    public void ThePlanCarriesTheChildsIdentityAndTheMode()
    {
        var run = GeneratorHarness.Run(WithFlow(Composing));

        run.Ids.ShouldNotContain("FLOWX1026", run.Describe());

        var plan = run.Sources
            .Single(s => s.HintName == "Sample.PlaceOrderFlow.Flow.g.cs").Source;

        plan.ShouldContainText(
            "StepNode.ForSubFlow(1, \"order.fulfil\", SubFlowMode.Inline)",
            "The plan carries the child's identity and the mode, and nothing else about it.");
        plan.ShouldNotContainText("inventory.pick",
            "The child's capabilities belong to the child's plan. Splicing them in would " +
            "make this flow's manifest claim work it does not do.");
    }

    [Fact]
    public void TheChildsDispatcherIsInjectedRatherThanConstructed()
    {
        var plan = GeneratorHarness.Run(WithFlow(Composing)).Sources
            .Single(s => s.HintName == "Sample.PlaceOrderFlow.Flow.g.cs").Source;

        plan.ShouldContainText("Sample.FulfilOrderFlow.Dispatcher fulfilOrderFlowDispatcher",
            "Constructing one here would mean this flow had to know the child's " +
            "capabilities, which is the coupling composition exists to avoid.");
        plan.ShouldContainText(
            "new SubFlowSource(Sample.FulfilOrderFlow.Plan, _fulfilOrderFlowDispatcher",
            "The child's plan and its injected dispatcher, handed to the engine together.");
    }

    [Fact]
    public void TheInputMappingIsACachedStaticSeededIntoTheChildsOwnContext()
    {
        var plan = GeneratorHarness.Run(WithFlow(Composing)).Sources
            .Single(s => s.HintName == "Sample.PlaceOrderFlow.Flow.g.cs").Source;

        plan.ShouldContainText("private static class SubFlowInputs",
            "A field, not a lambda per execution: the same reason predicates and selectors " +
            "are cached statics.");
        plan.ShouldContainText("Func<FlowContext, Sample.FulfilOrder> Step1",
            "Typed at the child's declared input, so nothing is boxed and a wrong mapping " +
            "is a build error rather than the child's first ctx.Get<T>() throwing.");
        plan.ShouldContainText("child.Set((Sample.FulfilOrder)source.Input!);",
            "Into the *child's* context. The two contexts never meet, which is what makes " +
            "a detached child safe against a pooled instance being reused.");
    }

    [Fact]
    public void AFlowThatComposesNothingStillImplementsTheContractAndSaysSo()
    {
        var plan = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Return(ctx => new OrderResult("id"));
            }
            """)).Sources.Single(s => s.HintName == "Sample.PlaceOrderFlow.Flow.g.cs").Source;

        plan.ShouldContainText(
            "public SubFlowSource BeginSubFlow(int stepIndex, FlowContext ctx)",
            "The member is emitted whether or not the flow composes anything.");
        plan.ShouldContainText("This flow composes no sub-flow",
            "Emitted explicitly rather than left to the interface's default, so the file " +
            "the header promises is readable says what the method does.");
        plan.ShouldNotContainText("SubFlowInputs",
            "No mapping to cache, so no class for them.");
    }

    // -------------------------------------------------------------------- the manifest

    [Fact]
    public void TheManifestNamesTheChildAndDoesNotInlineIt()
    {
        var json = GeneratorHarness.Run(WithFlow(Composing)).ManifestJson.ShouldNotBeNull();

        json.ShouldContainText("\"kind\": \"SubFlow\"", "The step's kind is published.");
        json.ShouldContainText("\"flow\": \"order.fulfil\"",
            "The child's identity, which is what makes a set of flow documents a graph.");
        json.ShouldContainText("\"mode\": \"Inline\"",
            "Structure: whether the parent waits, and whether the child's failure is its own.");
    }

    [Fact]
    public void TheManifestCarriesNoMappingExpression()
    {
        // Structure only, never values. `ctx => new FulfilOrder(ctx.Get<PlaceOrder>().Sku)` names the
        // shape of somebody's data, exactly as a predicate or a case value does.
        var json = GeneratorHarness.Run(WithFlow(Composing)).ManifestJson.ShouldNotBeNull();

        json.ShouldNotContainText("ctx =>", "A mapping is an expression over somebody's data.");
        json.ShouldNotContainText("Get<PlaceOrder>", "And so are the members it names.");
    }

    [Fact]
    public void AChildsStepsAppearUnderTheChildRatherThanUnderTheParent()
    {
        var json = GeneratorHarness.Run(WithFlow(Composing)).ManifestJson.ShouldNotBeNull();

        // One occurrence: the child's own entry. If the parent inlined the child's block,
        // every edit to a shared flow would be a diff in every flow that composes it.
        json.Split("\"capability\": \"inventory.pick@1.0.0\"").Length.ShouldBe(
            2,
            "Once: under the child's own entry. A second occurrence would mean the parent " +
            "had inlined the child's block, which would make every edit to a shared flow a " +
            "diff in every flow that composes it.");
    }

    // ------------------------------------------------------------------------ FLOWX1026

    [Fact]
    public void ReportsFLOWX1026ForAwaitCompletion()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .SubFlow<FulfilOrderFlow, FulfilOrder>(
                        ctx => new FulfilOrder(ctx.Get<PlaceOrder>().Sku), SubFlowMode.AwaitCompletion)
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldContain("FLOWX1026",
            "It suspends the parent, and there is no journal to suspend into. Neither " +
            "degenerate form is honest, so the compiler refuses rather than guessing.");

        run.Describe().ShouldContainText("journal",
            "The message says why, not only that. 'AwaitCompletion is not supported' would " +
            "leave a reader guessing whether to wait for a release or change the design.");
    }

    [Fact]
    public void ReportsFLOWX1026WhenTheTargetCarriesNoFlowAttribute()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            public sealed partial class DraftFlow : Flow<FulfilOrder, Fulfilled>
            {
                protected override void Define(IFlowBuilder<FulfilOrder, Fulfilled> flow) => flow
                    .Step<PickStock>()
                    .Return(ctx => ctx.Get<Fulfilled>());
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .SubFlow<DraftFlow, FulfilOrder>(ctx => new FulfilOrder(ctx.Get<PlaceOrder>().Sku))
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldContain("FLOWX1026",
            "Nothing generates a plan for a type with no [Flow], so there is no compiled " +
            "flow to run — and dropping the step silently would ship a flow missing the " +
            "composition its author wrote.");
    }

    [Fact]
    public void ReportsFLOWX1026ForAModeItCannotRead()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                private const SubFlowMode Chosen = SubFlowMode.Detached;

                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .SubFlow<FulfilOrderFlow, FulfilOrder>(ctx => new FulfilOrder(ctx.Get<PlaceOrder>().Sku), Chosen)
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldContain("FLOWX1026",
            "Unlike a merge strategy or an iteration bound, a mode is not a number the " +
            "plan can copy verbatim: it decides whether the parent waits, whose deadline " +
            "applies and whether the child's failure is the parent's. A mode the compiler " +
            "cannot read is one this rule cannot check and the manifest cannot publish.");
    }

    [Fact]
    public void DetachedIsAccepted()
    {
        var source = WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .SubFlow<FulfilOrderFlow, FulfilOrder>(
                        ctx => new FulfilOrder(ctx.Get<PlaceOrder>().Sku), SubFlowMode.Detached)
                    .Return(ctx => new OrderResult("id"));
            }
            """);

        var run = GeneratorHarness.Run(source);

        run.Ids.ShouldNotContain("FLOWX1026", run.Describe());
        GeneratorHarness.GeneratedCompileErrorsIn(source).ShouldBeEmpty();

        run.Sources.Single(s => s.HintName == "Sample.PlaceOrderFlow.Flow.g.cs").Source
            .ShouldContainText("SubFlowMode.Detached", "The mode reaches the compiled plan.");
    }

    // ------------------------------------------------------------------------ FLOWX1021

    [Fact]
    public void ReportsFLOWX1021ForAFlowThatComposesItself()
    {
        var ids = GeneratorHarness.Analyze(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .SubFlow<PlaceOrderFlow, PlaceOrder>(ctx => ctx.Input)
                    .Return(ctx => new OrderResult("id"));
            }
            """), new SubFlowCycleAnalyzer());

        ids.ShouldContain("FLOWX1021");
    }

    [Fact]
    public void ReportsFLOWX1021ForACycleThroughAThirdFlow()
    {
        var ids = GeneratorHarness.Analyze(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .SubFlow<MiddleFlow, FulfilOrder>(ctx => new FulfilOrder(ctx.Get<PlaceOrder>().Sku))
                    .Return(ctx => new OrderResult("id"));
            }

            [Flow("order.middle")]
            public sealed partial class MiddleFlow : Flow<FulfilOrder, Fulfilled>
            {
                protected override void Define(IFlowBuilder<FulfilOrder, Fulfilled> flow) => flow
                    .SubFlow<PlaceOrderFlow, PlaceOrder>(ctx => new PlaceOrder(ctx.Get<FulfilOrder>().Sku))
                    .Return(ctx => ctx.Get<Fulfilled>());
            }
            """), new SubFlowCycleAnalyzer());

        ids.ShouldContain("FLOWX1021");
    }

    [Fact]
    public void ACycleIsReportedWithThePathRatherThanJustTheFlow()
    {
        var diagnostics = GeneratorHarness.AnalyzeWithMessages(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .SubFlow<MiddleFlow, FulfilOrder>(ctx => new FulfilOrder(ctx.Get<PlaceOrder>().Sku))
                    .Return(ctx => new OrderResult("id"));
            }

            [Flow("order.middle")]
            public sealed partial class MiddleFlow : Flow<FulfilOrder, Fulfilled>
            {
                protected override void Define(IFlowBuilder<FulfilOrder, Fulfilled> flow) => flow
                    .SubFlow<PlaceOrderFlow, PlaceOrder>(ctx => new PlaceOrder(ctx.Get<FulfilOrder>().Sku))
                    .Return(ctx => ctx.Get<Fulfilled>());
            }
            """), new SubFlowCycleAnalyzer());

        // The useful fact about a cycle is never that there is one — it is which edge to
        // cut, and in a longer cycle that edge is in a file the error is not reported on.
        diagnostics.ShouldContain(m =>
            m.Contains("order.place → order.middle → order.place", System.StringComparison.Ordinal));
    }

    [Fact]
    public void ACompositionNestedInsideAConditionIsStillACycle()
    {
        // A rule that only looked at the top level would quietly stop applying the day
        // somebody wrapped the call in a `When`.
        var ids = GeneratorHarness.Analyze(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .When(ctx => ctx.Get<PlaceOrder>().Sku.Length > 0, then => then
                        .SubFlow<PlaceOrderFlow, PlaceOrder>(ctx => ctx.Input))
                    .Return(ctx => new OrderResult("id"));
            }
            """), new SubFlowCycleAnalyzer());

        ids.ShouldContain("FLOWX1021");
    }

    [Fact]
    public void ADiamondIsNotACycle()
    {
        // Two flows composing the same third one reach it twice and never reach
        // themselves. A reachability rule that reported this would be suppressed on the
        // first correct application it met, and would then protect nothing.
        var ids = GeneratorHarness.Analyze(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .SubFlow<FulfilOrderFlow, FulfilOrder>(ctx => new FulfilOrder(ctx.Get<PlaceOrder>().Sku))
                    .SubFlow<MiddleFlow, FulfilOrder>(ctx => new FulfilOrder(ctx.Get<PlaceOrder>().Sku))
                    .Return(ctx => new OrderResult("id"));
            }

            [Flow("order.middle")]
            public sealed partial class MiddleFlow : Flow<FulfilOrder, Fulfilled>
            {
                protected override void Define(IFlowBuilder<FulfilOrder, Fulfilled> flow) => flow
                    .SubFlow<FulfilOrderFlow, FulfilOrder>(ctx => ctx.Input)
                    .Return(ctx => ctx.Get<Fulfilled>());
            }
            """), new SubFlowCycleAnalyzer());

        ids.ShouldNotContain("FLOWX1021");
    }

    [Fact]
    public void AnAcyclicCompositionIsSilent()
        => GeneratorHarness.Analyze(WithFlow(Composing), new SubFlowCycleAnalyzer())
            .ShouldNotContain("FLOWX1021");

    [Fact]
    public void AFlowThatMerelyReachesACycleIsNotBlamedForIt()
    {
        // Reporting it would send a reader to a file with nothing wrong in it.
        var diagnostics = GeneratorHarness.AnalyzeWithMessages(WithFlow("""
            [Flow("order.entry")]
            public sealed partial class EntryFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .SubFlow<PlaceOrderFlow, PlaceOrder>(ctx => ctx.Input)
                    .Return(ctx => new OrderResult("id"));
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .SubFlow<MiddleFlow, FulfilOrder>(ctx => new FulfilOrder(ctx.Get<PlaceOrder>().Sku))
                    .Return(ctx => new OrderResult("id"));
            }

            [Flow("order.middle")]
            public sealed partial class MiddleFlow : Flow<FulfilOrder, Fulfilled>
            {
                protected override void Define(IFlowBuilder<FulfilOrder, Fulfilled> flow) => flow
                    .SubFlow<PlaceOrderFlow, PlaceOrder>(ctx => new PlaceOrder(ctx.Get<FulfilOrder>().Sku))
                    .Return(ctx => ctx.Get<Fulfilled>());
            }
            """), new SubFlowCycleAnalyzer());

        diagnostics.Count(m => m.Contains("FLOWX1021", System.StringComparison.Ordinal))
            .ShouldBe(2, "The two flows on the cycle, and not the one that only reaches it.");

        diagnostics.ShouldNotContain(m =>
            m.Contains("'order.entry'", System.StringComparison.Ordinal));
    }

    // -------------------------------------------------------------------- FLOWX1011 still

    [Fact]
    public void AnImpureMappingIsStillReportedNowThatSubFlowIsReal()
    {
        // FLOWX1011 has covered the SubFlow mapping through a table since before anything
        // could declare one. This is the check that the table was right.
        var ids = GeneratorHarness.Analyze(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .SubFlow<FulfilOrderFlow, FulfilOrder>(
                        ctx => new FulfilOrder(System.DateTime.UtcNow.ToString()))
                    .Return(ctx => new OrderResult("id"));
            }
            """), new PredicatePurityAnalyzer());

        ids.ShouldContain("FLOWX1011");
    }
}

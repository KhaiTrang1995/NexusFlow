using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// <c>ctx.Input</c> inside the delegates the DSL declares it on.
/// </summary>
/// <remarks>
/// <para>
/// Every method on <c>IFlowBuilder&lt;TIn, TOut&gt;</c> that takes a delegate types it
/// <c>Func&lt;FlowContext&lt;TIn&gt;, …&gt;</c>, <c>08 §3.1</c> says a condition may read
/// <c>ctx.Input</c>, and two worked examples plus <c>FLOWX1011.md</c> do exactly that.
/// None of them compiled: the emitter copied each lambda into a field typed at the
/// <em>non-generic</em> <c>FlowContext</c>, on which <c>Input</c> is not declared, and the
/// author's own expression failed with CS1061 inside generated source they did not write.
/// </para>
/// <para>
/// <strong>Every test here goes through <c>GeneratedCompileErrorsIn</c>, which is the
/// point.</strong> The emitter's own suite parses the output, and a parse is happy with a
/// member that does not exist — this defect is precisely the kind only a real compilation
/// sees.
/// </para>
/// </remarks>
public sealed class FlowInputTests
{
    private const string Preamble = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record PlaceOrder(string Sku, int Quantity, Channel Channel, IReadOnlyList<string> Lines);
        public sealed record OrderResult(string Id);
        public sealed record Reservation(string Sku);
        public sealed record FulfilOrder(string Sku);

        public enum Channel { Retail, Wholesale, Partner }

        [Capability("inventory.reserve", Version = "1.2.0",
            Authorization = Authorization.Authenticated, Idempotent = true)]
        public sealed class ReserveInventory : ICapability<PlaceOrder, Reservation>
        {
            public ValueTask<Result<Reservation>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Reservation(input.Sku)));
        }

        [Capability("order.line_reserve", Version = "1.0.0",
            Authorization = Authorization.Internal, Idempotent = true)]
        public sealed class ReserveLine : ICapability<string, Reservation>
        {
            public ValueTask<Result<Reservation>> ExecuteAsync(string input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Reservation(input)));
        }

        [Capability("order.fulfil_line", Version = "1.0.0",
            Authorization = Authorization.Internal, Idempotent = true)]
        public sealed class FulfilLine : ICapability<FulfilOrder, Reservation>
        {
            public ValueTask<Result<Reservation>> ExecuteAsync(FulfilOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Reservation(input.Sku)));
        }
        """;

    private static string WithFlow(string flowDeclaration) => Preamble + "\n\n" + flowDeclaration;

    /// <summary>Asserts the author's source compiles, and so does everything generated from it.</summary>
    private static void ShouldCompileBothWays(string source)
    {
        GeneratorHarness.CompileErrorsIn(source).ShouldBeEmpty(
            "The flow as the author wrote it must compile before the generator is blamed.");

        GeneratorHarness.GeneratedCompileErrorsIn(source).ShouldBeEmpty(
            "The author's expression was copied into generated source. If the parameter " +
            "type there has lost a member, this is where CS1061 shows up.");
    }

    [Fact]
    public void AWhenPredicateMayReadTheInput() =>
        ShouldCompileBothWays(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .When(ctx => ctx.Input.Quantity > 10, bulk => bulk.Step<ReserveInventory>())
                    .Return(ctx => new OrderResult("id"));
            }
            """));

    [Fact]
    public void ASwitchSelectorMayReadTheInput() =>
        ShouldCompileBothWays(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Switch(ctx => ctx.Input.Channel)
                    .Case(Channel.Retail, b => b.Step<ReserveInventory>())
                    .Return(ctx => new OrderResult("id"));
            }
            """));

    [Fact]
    public void AForEachSelectorMayReadTheInput() =>
        ShouldCompileBothWays(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .ForEach(ctx => ctx.Input.Lines,
                            line => line.Step<ReserveLine>(),
                        options: new ForEachOptions { MaxDegreeOfParallelism = 2 })
                    .Return(ctx => new OrderResult("id"));
            }
            """));

    /// <summary>A child flow, declared only where a composition needs one.</summary>
    /// <remarks>
    /// Not in the preamble: a second flow in every source would mean every run produced two
    /// generated plans, and the harness's <c>Plan</c> would have nothing single to return.
    /// </remarks>
    private const string ChildFlow = """
        [Flow("order.fulfil")]
        public sealed partial class FulfilOrderFlow : Flow<FulfilOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<FulfilOrder, OrderResult> flow) => flow
                .Step<FulfilLine>()
                .Return(ctx => new OrderResult(ctx.Input.Sku));
        }
        """;

    [Fact]
    public void ASubFlowInputMappingMayReadTheInput() =>
        ShouldCompileBothWays(WithFlow(ChildFlow + """

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .SubFlow<FulfilOrderFlow, FulfilOrder>(ctx => new FulfilOrder(ctx.Input.Sku))
                    .Return(ctx => new OrderResult("id"));
            }
            """));

    [Fact]
    public void AReturnProjectionMayReadTheInput() =>
        ShouldCompileBothWays(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Return(ctx => new OrderResult(ctx.Input.Sku));
            }
            """));

    [Fact]
    public void AnInputReadInsideAForEachBodysPredicateStillResolves()
    {
        // The case a cast would have failed on. Inside a loop body the context the engine
        // hands the dispatcher is the *iteration's* scope, not the flow's own — so a
        // predicate there is evaluated against a different object, and the typed view has
        // to wrap whatever it is given rather than assume a shape.
        ShouldCompileBothWays(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .ForEach(ctx => ctx.Input.Lines,
                            line => line.When(ctx => ctx.Input.Quantity > 0, some => some.Step<ReserveLine>()),
                        options: new ForEachOptions { MaxDegreeOfParallelism = 1 })
                    .Return(ctx => new OrderResult("id"));
            }
            """));
    }

    [Fact]
    public void TheAmbientMembersTheDeterminismRulePermitsStillResolve()
    {
        // FLOWX1011 permits everything reachable from the delegate's own parameter —
        // ctx.UtcNow, ctx.NewId(), ctx.Random — because 06 §5 puts the clock, identifiers
        // and randomness in the journaled zone and the context is the seam that makes them
        // reproducible. The code fix for that rule rewrites `DateTime.UtcNow` into
        // `ctx.UtcNow`, so a typed view that had dropped the member would turn the
        // documented remedy into a compile error.
        ShouldCompileBothWays(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .When(ctx => ctx.UtcNow.Hour > 9 && ctx.TenantId != null && ctx.Random.Next() > 0,
                        open => open.Step<ReserveInventory>())
                    .Return(ctx => new OrderResult(
                        ctx.NewId().ToString() + ctx.CorrelationId + ctx.Get<Reservation>().Sku));
            }
            """));
    }

    [Fact]
    public void TheProjectionIsStillPublicAndStillTakesTheEnginesOwnContext()
    {
        // The engine and MapFlow both take `Func<FlowContext, TOut>`. Retyping it would
        // have pushed a generic parameter through two packages, so the author's expression
        // — which is written against the view — is adapted at the field instead, and the
        // field keeps the shape it always had.
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Return(ctx => new OrderResult(ctx.Input.Sku));
            }
            """));

        run.Plan.ShouldContainText(
            "public static readonly Func<FlowContext, Sample.OrderResult> Projection =",
            run.Describe());

        run.Plan.ShouldContainText(
            "Projection = FlowContext.Untyped<Sample.PlaceOrder, Sample.OrderResult>(ctx => " +
            "new OrderResult(ctx.Input.Sku));",
            "and the author's own expression is adapted from the view it was written " +
            "against, in one call, rather than retyped.");
    }

    [Fact]
    public void AFlowThatNeedsNoTypedViewDoesNotGetOne()
    {
        // The same rule the conditions, selectors and iterations follow: scaffolding for a
        // shape the flow does not declare is scaffolding nobody reads.
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Return(ctx => new OrderResult(ctx.Input.Sku));
            }
            """));

        run.Plan.ShouldNotContainText(
            "Typed(FlowContext ctx)",
            "A linear flow has one call site — the projection — and that one adapts itself.");
    }
}

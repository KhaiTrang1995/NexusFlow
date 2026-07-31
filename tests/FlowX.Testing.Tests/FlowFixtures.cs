using FlowX.Runtime;

namespace FlowX.Testing.Tests;

// The contracts the fixture flows pass between steps. Records, because a step binds to
// its input by type and nothing here needs behaviour.
internal sealed record Order(string Sku, int Quantity);

internal sealed record Reservation(string Id);

internal sealed record Payment(string Receipt);

internal sealed record Shipment(string Tracking);

internal sealed record Line(string Sku);

internal sealed record OrderReceipt(string ReservationId, string ReceiptId);

/// <summary>
/// A hand-written <see cref="IStepDispatcher"/> in the shape the generator emits: a
/// lookup by step index, with each step's typed work held here rather than in the engine.
/// </summary>
/// <remarks>
/// Hand-written on purpose. <c>FlowTestHost</c> must work against any dispatcher that
/// satisfies the contract, and building these tests on the generated one would tie them
/// to the sample — which is what <c>Ecommerce.Tests</c> covers instead, with the real
/// generated pair.
/// </remarks>
internal sealed class ScriptedDispatcher : IStepDispatcher
{
    private readonly Dictionary<int, Func<FlowContext, StepOutcome>> _steps = [];
    private readonly Dictionary<int, Func<FlowContext, StepOutcome>> _compensations = [];
    private readonly Dictionary<int, Func<FlowContext, bool>> _predicates = [];
    private readonly Dictionary<int, Func<FlowContext, int>> _selectors = [];
    private readonly Dictionary<int, Func<FlowContext, IterationSource>> _collections = [];
    private readonly Dictionary<int, Func<IterationSource, int, FlowContext, FlowContext>> _scopes = [];
    private readonly Dictionary<int, Func<FlowContext, SubFlowSource>> _children = [];
    private readonly Dictionary<int, Action<SubFlowSource, FlowContext>> _seeds = [];
    private readonly Lock _sync = new();
    private readonly List<string> _calls = [];

    /// <summary>Names of the real step bodies that ran, so a substitution can be shown to bypass one.</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_sync)
            {
                return [.. _calls];
            }
        }
    }

    public ScriptedDispatcher Step(int index, string name, Func<FlowContext, StepOutcome> body)
    {
        _steps[index] = ctx =>
        {
            lock (_sync)
            {
                _calls.Add(name);
            }

            return body(ctx);
        };

        return this;
    }

    /// <summary>A step that produces a value, exactly as a generated <c>ctx.Set(result.Value)</c> does.</summary>
    public ScriptedDispatcher Produces<T>(int index, string name, Func<FlowContext, T> value)
        where T : notnull =>
        Step(index, name, ctx =>
        {
            ctx.Set(value(ctx));
            return StepOutcome.Success;
        });

    public ScriptedDispatcher Fails(int index, string name, Error error) =>
        Step(index, name, _ => StepOutcome.Failed(error));

    public ScriptedDispatcher Compensates(int index, string name)
    {
        _compensations[index] = _ =>
        {
            lock (_sync)
            {
                _calls.Add(name);
            }

            return StepOutcome.Success;
        };

        return this;
    }

    public ScriptedDispatcher Predicate(int index, Func<FlowContext, bool> predicate)
    {
        _predicates[index] = predicate;
        return this;
    }

    public ScriptedDispatcher Selector(int index, Func<FlowContext, int> selector)
    {
        _selectors[index] = selector;
        return this;
    }

    public ScriptedDispatcher IterateOver<TItem>(int index, IReadOnlyList<TItem> items)
        where TItem : notnull
    {
        _collections[index] = _ => new IterationSource(items, items.Count);
        _scopes[index] = (source, iteration, ctx) =>
            IterationScope.For(ctx, ((IReadOnlyList<TItem>)source.Items!)[iteration]);

        return this;
    }

    public ScriptedDispatcher ComposeAt<TIn>(
        int index, ExecutionPlan plan, IStepDispatcher dispatcher, TIn input)
        where TIn : notnull
    {
        _children[index] = _ => new SubFlowSource(plan, dispatcher, input);
        _seeds[index] = (source, child) => child.Set((TIn)source.Input!);

        return this;
    }

    public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(_steps.TryGetValue(stepIndex, out var step) ? step(ctx) : StepOutcome.Success);

    public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(
            _compensations.TryGetValue(stepIndex, out var undo) ? undo(ctx) : StepOutcome.Success);

    public bool Evaluate(int stepIndex, FlowContext ctx) => _predicates[stepIndex](ctx);

    public int Select(int stepIndex, FlowContext ctx) => _selectors[stepIndex](ctx);

    public IterationSource BeginIteration(int stepIndex, FlowContext ctx) => _collections[stepIndex](ctx);

    public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
        _scopes[stepIndex](source, iteration, ctx);

    public SubFlowSource BeginSubFlow(int stepIndex, FlowContext ctx) => _children[stepIndex](ctx);

    public void EnterSubFlow(int stepIndex, in SubFlowSource source, FlowContext child) =>
        _seeds[stepIndex](source, child);
}

/// <summary>The compiled plans the host tests run, written out in the flat layout the compiler emits.</summary>
internal static class Fixtures
{
    public static CapabilityDescriptor Validate { get; } =
        CapabilityDescriptor.Create("order.validate", "1.0.0", isIdempotent: true);

    public static CapabilityDescriptor Reserve { get; } =
        CapabilityDescriptor.Create("inventory.reserve", "1.0.0", isIdempotent: true, "inventory-ledger");

    public static CapabilityDescriptor Release { get; } =
        CapabilityDescriptor.Create("inventory.release", "1.0.0", isIdempotent: true, "inventory-ledger");

    public static CapabilityDescriptor Capture { get; } =
        CapabilityDescriptor.Create("payment.capture", "2.1.0", isIdempotent: false, "payment-gateway");

    public static CapabilityDescriptor Refund { get; } =
        CapabilityDescriptor.Create("payment.refund", "2.1.0", isIdempotent: true, "payment-gateway");

    public static CapabilityDescriptor Dispatch { get; } =
        CapabilityDescriptor.Create("shipping.dispatch", "1.0.0", isIdempotent: false, "carrier");

    public static Error Declined { get; } =
        new("payment.declined", "The payment was declined.", ErrorCategory.Conflict);

    public static Error NoCarrier { get; } =
        new("shipping.no_carrier", "No carrier accepted the shipment.", ErrorCategory.Conflict);

    /// <summary>
    /// <c>0 reserve(+release) · 1 capture(+refund) · 2 dispatch · 3 emit</c>.
    /// </summary>
    /// <remarks>
    /// Two compensable steps before the one that can fail, which is what makes the unwind
    /// <em>order</em> observable at all — with one, forward and reverse look identical.
    /// </remarks>
    public static ExecutionPlan Saga(TimeSpan? deadline = null) => ExecutionPlan.Create(
        FlowDescriptor.Create(
            "order.place", "1.0.0", ExecutionProfile.Ephemeral, deadline ?? TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Reserve, Release),
            StepNode.ForCapability(1, Capture, Refund),
            StepNode.ForCapability(2, Dispatch),
            StepNode.ForEmit(3, "order.placed"),
        ]));

    public static ScriptedDispatcher SagaSteps() => new ScriptedDispatcher()
        .Produces(0, "reserve", ctx => new Reservation("res-" + ctx.Get<Order>().Sku))
        .Produces<Payment>(1, "capture", _ => new Payment("receipt-1"))
        .Produces<Shipment>(2, "dispatch", _ => new Shipment("track-1"))
        .Compensates(0, "release")
        .Compensates(1, "refund");

    /// <summary>
    /// <c>0 validate · 1 branch(else→5) · 2 reserve(+release) · 3 capture · 4 jump→6 ·
    /// 5 dispatch · 6 emit</c>.
    /// </summary>
    public static ExecutionPlan Conditional() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.review", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForBranch(1, falseTarget: 5),
            StepNode.ForCapability(2, Reserve, Release),
            StepNode.ForCapability(3, Capture),
            StepNode.ForJump(4, target: 6),
            StepNode.ForCapability(5, Dispatch),
            StepNode.ForEmit(6, "order.reviewed"),
        ]));

    /// <summary>
    /// <c>0 validate · 1 switch(→2,4 else 6) · 2 reserve · 3 jump→7 · 4 capture ·
    /// 5 jump→7 · 6 dispatch · 7 emit</c>.
    /// </summary>
    public static ExecutionPlan Switching() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.route", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForSwitch(1, [2, 4], defaultTarget: 6),
            StepNode.ForCapability(2, Reserve),
            StepNode.ForJump(3, target: 7),
            StepNode.ForCapability(4, Capture),
            StepNode.ForJump(5, target: 7),
            StepNode.ForCapability(6, Dispatch),
            StepNode.ForEmit(7, "order.routed"),
        ]));

    /// <summary><c>0 parallel(→1,2,3 join 4) · 1 reserve · 2 capture · 3 dispatch · 4 emit</c>.</summary>
    public static ExecutionPlan Fork(MergeStrategy merge) => ExecutionPlan.Create(
        FlowDescriptor.Create("order.enrich", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForParallel(0, [1, 2, 3], joinTarget: 4, merge),
            StepNode.ForCapability(1, Reserve),
            StepNode.ForCapability(2, Capture),
            StepNode.ForCapability(3, Dispatch),
            StepNode.ForEmit(4, "order.enriched"),
        ]));

    /// <summary><c>0 foreach(join 3) · 1 reserve(+release) · 2 capture · 3 emit</c>.</summary>
    public static ExecutionPlan Loop(int maxDegreeOfParallelism, bool continueOnError = false) =>
        ExecutionPlan.Create(
            FlowDescriptor.Create("order.pick", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForEach(
                    0,
                    joinTarget: 3,
                    new ForEachOptions
                    {
                        MaxDegreeOfParallelism = maxDegreeOfParallelism,
                        ContinueOnError = continueOnError,
                    }),
                StepNode.ForCapability(1, Reserve, Release),
                StepNode.ForCapability(2, Capture),
                StepNode.ForEmit(3, "order.picked"),
            ]));

    /// <summary><c>0 subflow(payment.settle) · 1 dispatch · 2 emit</c>.</summary>
    public static ExecutionPlan Composing(SubFlowMode mode) => ExecutionPlan.Create(
        FlowDescriptor.Create("order.fulfil", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForSubFlow(0, "payment.settle", mode),
            StepNode.ForCapability(1, Dispatch),
            StepNode.ForEmit(2, "order.fulfilled"),
        ]));

    /// <summary>The composed child: <c>0 capture(+refund) · 1 emit</c>.</summary>
    public static ExecutionPlan Child() => ExecutionPlan.Create(
        FlowDescriptor.Create("payment.settle", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Capture, Refund),
            StepNode.ForEmit(1, "payment.settled"),
        ]));

    /// <summary><c>0 reserve(+release) · 1 fail</c>: the rejection-after-effects shape.</summary>
    public static ExecutionPlan Rejecting() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.screen", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Reserve, Release),
            StepNode.ForFail(1),
        ]));
}

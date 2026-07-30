namespace FlowX;

/// <summary>How concurrent branches are joined.</summary>
public enum MergeStrategy
{
    /// <summary>Wait for all; the first failure cancels its siblings via a linked token.</summary>
    AllMustSucceed = 0,

    /// <summary>Wait for all and collect every outcome; branch errors are readable from the context.</summary>
    AllSettled = 1,

    /// <summary>First success wins; the remaining branches are cancelled.</summary>
    FirstSuccess = 2,
}

/// <summary>How a sub-flow relates to its parent.</summary>
public enum SubFlowMode
{
    /// <summary>Runs inline, sharing the parent's deadline and correlation. Child failure fails the parent.</summary>
    Inline = 0,

    /// <summary>Fire-and-forget with its own deadline and lifecycle.</summary>
    Detached = 1,

    /// <summary>The parent suspends until the child completes. Durable flows only.</summary>
    AwaitCompletion = 2,
}

/// <summary>Bounds an iteration. <see cref="MaxDegreeOfParallelism"/> is required by design — unbounded parallelism is never correct.</summary>
public sealed record ForEachOptions
{
    /// <summary>Concurrent iterations. Capped by the runtime.</summary>
    public required int MaxDegreeOfParallelism { get; init; }

    /// <summary>When false, the first failing item stops the iteration.</summary>
    public bool ContinueOnError { get; init; }
}

/// <summary>
/// Declares a flow's graph. Every method here maps to a node in
/// <c>flowx.manifest.json</c>, which is why there is deliberately no
/// <c>.Do(lambda)</c>: arbitrary inline code would be invisible to the manifest,
/// untestable in isolation and undetectable by determinism analysis. If it is worth
/// executing, it is worth naming — make it a capability.
/// </summary>
/// <typeparam name="TIn">Flow input contract.</typeparam>
/// <typeparam name="TOut">Flow output contract.</typeparam>
public interface IFlowBuilder<TIn, TOut>
{
    /// <summary>Invokes a capability, binding its input from the flow context.</summary>
    IStepBuilder<TIn, TOut> Step<TCapability>();

    /// <summary>Invokes a capability with an explicit input mapping, for when the shapes differ.</summary>
    IStepBuilder<TIn, TOut> Step<TCapability, TStepIn>(Func<FlowContext<TIn>, TStepIn> map);

    /// <summary>Branches on a predicate. The predicate may read only the context, the input and prior step results (FLOWX1011).</summary>
    IConditionalBuilder<TIn, TOut> When(
        Func<FlowContext<TIn>, bool> predicate,
        Action<IFlowBuilder<TIn, TOut>> then);

    /// <summary>
    /// Branches on a value rather than on a yes/no question. The selector obeys the same
    /// determinism rule as a <see cref="When"/> predicate: context, input and prior step
    /// results only.
    /// </summary>
    /// <typeparam name="TValue">
    /// What the selector produces and the cases are matched against. Inferred, so a
    /// <c>Case</c> whose value is of the wrong type is a C# compile error rather than an
    /// arm that never matches.
    /// </typeparam>
    /// <param name="selector">Reads the value to branch on. Evaluated exactly once.</param>
    ISwitchBuilder<TIn, TOut, TValue> Switch<TValue>(Func<FlowContext<TIn>, TValue> selector);

    /// <summary>Runs branches concurrently. Branches write to disjoint context slots, enforced at compile time (FLOWX1013).</summary>
    IFlowBuilder<TIn, TOut> Parallel(Action<IParallelBuilder<TIn, TOut>> branches, MergeStrategy merge);

    /// <summary>Iterates a collection with bounded concurrency. In a durable flow each iteration commits its own journal entry.</summary>
    IFlowBuilder<TIn, TOut> ForEach<TItem>(
        Func<FlowContext<TIn>, IReadOnlyList<TItem>> selector,
        Action<IFlowBuilder<TIn, TOut>> body,
        ForEachOptions options);

    /// <summary>Composes another flow. Sub-flow cycles are a build error (FLOWX1021) — the graph is always a DAG.</summary>
    IFlowBuilder<TIn, TOut> SubFlow<TFlow, TSubIn>(
        Func<FlowContext<TIn>, TSubIn> map,
        SubFlowMode mode = SubFlowMode.Inline);

    /// <summary>Publishes a domain event. Transactional-outbox based in durable flows: never lost, never published before the step is durable.</summary>
    IFlowBuilder<TIn, TOut> Emit<TEvent>(Func<FlowContext<TIn>, TEvent> map);

    /// <summary>Publishes an event on the failure path.</summary>
    IFlowBuilder<TIn, TOut> EmitOnFailure<TEvent>(Func<FlowContext<TIn>, TEvent> map);

    /// <summary>
    /// Suspends until an external signal arrives. Durable flows only (FLOWX1017) —
    /// an in-memory wait cannot survive a deployment. A suspended instance holds no
    /// thread, no memory and no lease; it costs one row.
    /// </summary>
    IAwaitBuilder<TIn, TOut> AwaitSignal<TSignal>(TimeSpan timeout);

    /// <summary>A durable timer. Holds no resources while waiting. Durable flows only.</summary>
    IFlowBuilder<TIn, TOut> Delay(TimeSpan duration);

    /// <summary>Terminates with a business error.</summary>
    IFlowBuilder<TIn, TOut> Fail(Error error);

    /// <summary>Produces the flow's output. Terminates the declaration.</summary>
    void Return(Func<FlowContext<TIn>, TOut> projection);
}

/// <summary>A flow builder positioned on a step, exposing step-scoped declarations.</summary>
public interface IStepBuilder<TIn, TOut> : IFlowBuilder<TIn, TOut>
{
    /// <summary>
    /// Registers the business inverse of this step. Compensation runs in strict reverse
    /// order of successfully completed steps, each under its own policy chain.
    /// </summary>
    IStepBuilder<TIn, TOut> CompensateWith<TCompensation>();

    /// <summary>Attaches a policy set to this step.</summary>
    IStepBuilder<TIn, TOut> WithPolicy(PolicySet policy);
}

/// <summary>A conditional awaiting its alternative branch.</summary>
public interface IConditionalBuilder<TIn, TOut> : IFlowBuilder<TIn, TOut>
{
    /// <summary>Declares the branch taken when the predicate is false.</summary>
    IFlowBuilder<TIn, TOut> Otherwise(Action<IFlowBuilder<TIn, TOut>> otherwise);
}

/// <summary>A value branch collecting its cases.</summary>
/// <typeparam name="TIn">Flow input contract.</typeparam>
/// <typeparam name="TOut">Flow output contract.</typeparam>
/// <typeparam name="TValue">What the selector produces.</typeparam>
/// <remarks>
/// <para>
/// Extends <see cref="IFlowBuilder{TIn, TOut}"/> so a switch can be followed by ordinary
/// steps without a <see cref="Default"/> — the same shape as
/// <see cref="IConditionalBuilder{TIn, TOut}"/>, and for the same reason: not every
/// branch has an alternative worth naming.
/// </para>
/// <para>
/// <strong>A value that matches no case, in a switch with no <see cref="Default"/>,
/// continues after the switch.</strong> It is not an error and it is not a
/// diagnostic. The alternative — requiring a <c>Default</c> — would force
/// <c>.Default(b =&gt; { })</c> onto every switch that legitimately special-cases two
/// channels out of five, and would still not make the switch exhaustive, because an
/// <c>enum</c> can hold a value no member declares. So the rule is the one
/// <c>When</c> already uses: a branch nobody took does nothing. State the miss
/// explicitly with <c>.Default(b =&gt; b.Fail(...))</c> when doing nothing is wrong.
/// </para>
/// </remarks>
public interface ISwitchBuilder<TIn, TOut, TValue> : IFlowBuilder<TIn, TOut>
{
    /// <summary>
    /// Declares the block taken when the selector's value equals <paramref name="value"/>.
    /// </summary>
    /// <param name="value">
    /// Compared with <c>EqualityComparer&lt;TValue&gt;.Default</c>, so an <c>enum</c>,
    /// an <c>int</c> and a <c>string</c> all mean what a reader expects and none of them
    /// is boxed.
    /// </param>
    /// <param name="body">The steps to run when it matches.</param>
    /// <remarks>
    /// Cases are tested in declaration order and the first match wins, so two cases with
    /// the same value are not an error — the second is simply unreachable, exactly as a
    /// duplicated <c>When</c> would be.
    /// </remarks>
    ISwitchBuilder<TIn, TOut, TValue> Case(TValue value, Action<IFlowBuilder<TIn, TOut>> body);

    /// <summary>
    /// Declares the block taken when no case matched. Returns the plain builder, so a
    /// switch has at most one <c>Default</c> and it is always last.
    /// </summary>
    IFlowBuilder<TIn, TOut> Default(Action<IFlowBuilder<TIn, TOut>> body);
}

/// <summary>A suspension point awaiting its timeout branch.</summary>
public interface IAwaitBuilder<TIn, TOut> : IFlowBuilder<TIn, TOut>
{
    /// <summary>Declares what happens when the signal never arrives.</summary>
    IFlowBuilder<TIn, TOut> OnTimeout(Action<IFlowBuilder<TIn, TOut>> onTimeout);
}

/// <summary>Collects concurrent branches.</summary>
public interface IParallelBuilder<TIn, TOut>
{
    /// <summary>Adds a branch invoking a single capability.</summary>
    IParallelBuilder<TIn, TOut> Branch<TCapability>();

    /// <summary>Adds a multi-step branch.</summary>
    IParallelBuilder<TIn, TOut> Branch(Action<IFlowBuilder<TIn, TOut>> branch);
}

namespace FlowX;

/// <summary>Which join rule a <see cref="MergeStrategy"/> names.</summary>
/// <remarks>
/// The discriminant, separated from the strategy itself because one of the four carries
/// a number and the other three do not. This is what reaches the compiled plan and the
/// manifest; <see cref="MergeStrategy"/> is what an author writes.
/// </remarks>
public enum MergeKind
{
    /// <summary>Wait for all; the first failure cancels its siblings via a linked token.</summary>
    AllMustSucceed = 0,

    /// <summary>Wait for all and collect every outcome; branch errors are readable from the context.</summary>
    AllSettled = 1,

    /// <summary>First success wins; the remaining branches are cancelled.</summary>
    FirstSuccess = 2,

    /// <summary>The first <em>n</em> successes win; the remaining branches are cancelled.</summary>
    Quorum = 3,
}

/// <summary>
/// How concurrent branches are joined.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A struct rather than an enum, because <c>Quorum(n)</c> carries a number.</strong>
/// <c>06-Execution-Engine.md</c> §9 has always listed four strategies and the fourth is
/// parameterised; an <c>enum</c> can name it but cannot hold its argument, so the
/// documented surface and the shipped type disagreed. The three parameterless strategies
/// are static properties, so <c>merge: MergeStrategy.AllMustSucceed</c> reads and compiles
/// exactly as it did — and as both documentation pages spell it.
/// </para>
/// <para>
/// <c>default(MergeStrategy)</c> is <see cref="AllMustSucceed"/>, which is the strictest
/// of the four. A default that silently tolerated a failed branch would be the wrong way
/// round.
/// </para>
/// </remarks>
public readonly struct MergeStrategy : IEquatable<MergeStrategy>
{
    private readonly int _successes;

    private MergeStrategy(MergeKind kind, int successes)
    {
        Kind = kind;
        _successes = successes;
    }

    /// <summary>Which join rule this is.</summary>
    public MergeKind Kind { get; }

    /// <summary>
    /// How many branches must succeed before the remainder are cancelled, or <c>0</c> when
    /// the strategy waits for all of them.
    /// </summary>
    /// <remarks>
    /// <see cref="FirstSuccess"/> reports <c>1</c> rather than <c>0</c>: it is the
    /// degenerate quorum, and reporting them alike lets the engine run one code path for
    /// both instead of two that must be kept in agreement.
    /// </remarks>
    public int RequiredSuccesses => Kind switch
    {
        MergeKind.Quorum => _successes,
        MergeKind.FirstSuccess => 1,
        _ => 0,
    };

    /// <summary>Wait for all; the first failure cancels its siblings via a linked token.</summary>
    public static MergeStrategy AllMustSucceed => new(MergeKind.AllMustSucceed, 0);

    /// <summary>Wait for all and collect every outcome; branch errors are readable from the context.</summary>
    public static MergeStrategy AllSettled => new(MergeKind.AllSettled, 0);

    /// <summary>First success wins; the remaining branches are cancelled.</summary>
    public static MergeStrategy FirstSuccess => new(MergeKind.FirstSuccess, 1);

    /// <summary>The first <paramref name="successes"/> successes win; the remainder are cancelled.</summary>
    /// <param name="successes">How many branches must succeed. Must be positive.</param>
    /// <remarks>
    /// Not validated against the branch count here — this type does not know it. A quorum
    /// larger than the number of branches can never be met, and the engine reports that as
    /// a failed merge rather than hanging.
    /// </remarks>
    public static MergeStrategy Quorum(int successes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(successes);
        return new MergeStrategy(MergeKind.Quorum, successes);
    }

    /// <inheritdoc />
    public bool Equals(MergeStrategy other) => Kind == other.Kind && _successes == other._successes;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MergeStrategy other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, _successes);

    /// <summary>Compares two strategies.</summary>
    public static bool operator ==(MergeStrategy left, MergeStrategy right) => left.Equals(right);

    /// <summary>Compares two strategies.</summary>
    public static bool operator !=(MergeStrategy left, MergeStrategy right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        Kind == MergeKind.Quorum ? $"Quorum({_successes})" : Kind.ToString();
}

/// <summary>How a sub-flow relates to its parent.</summary>
/// <remarks>
/// <para>
/// <strong>Two of the three are implemented.</strong> <see cref="AwaitCompletion"/> is
/// refused at build time by <c>FLOWX1026</c> — it needs a durable suspension point, and
/// there is no journal to suspend into. The member stays in the enum because the id is a
/// forever commitment (constraint C7) and because deleting it would turn a documented
/// mode into a spelling mistake; a diagnostic that names the reason is more use than a
/// missing member.
/// </para>
/// </remarks>
public enum SubFlowMode
{
    /// <summary>
    /// Runs inline, sharing the parent's correlation, tenant and remaining budget. Child
    /// failure fails the parent, and work the child completed is undone when the
    /// <em>parent</em> later fails.
    /// </summary>
    Inline = 0,

    /// <summary>
    /// Fire-and-forget: the child gets its own context, its own deadline and its own
    /// lifecycle, and the parent does not wait for it or hear about its failure.
    /// </summary>
    Detached = 1,

    /// <summary>
    /// The parent suspends until the child completes. Durable flows only, and refused by
    /// <c>FLOWX1026</c> until the journal exists.
    /// </summary>
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

    /// <summary>
    /// Composes another flow. Sub-flow cycles are a build error (FLOWX1021) — the graph is
    /// always a DAG.
    /// </summary>
    /// <typeparam name="TFlow">
    /// The flow to run. Constrained to <see cref="Flow"/> so that composing something that
    /// is not a flow — a capability, a contract — is an ordinary C# error on the author's
    /// own line rather than a missing member in generated code.
    /// </typeparam>
    /// <typeparam name="TSubIn">The child's input contract, inferred from <paramref name="map"/>.</typeparam>
    /// <param name="map">
    /// Builds the child's input from the parent's context. Obeys the same determinism rule
    /// as a <c>When</c> predicate — context, flow input and prior step results only
    /// (FLOWX1011) — and is evaluated on the parent's thread, before the child starts, so
    /// the child never holds a reference to the parent's pooled context.
    /// </param>
    /// <param name="mode">
    /// How the child relates to the parent. <see cref="SubFlowMode.AwaitCompletion"/> is
    /// refused by FLOWX1026 in this release.
    /// </param>
    IFlowBuilder<TIn, TOut> SubFlow<TFlow, TSubIn>(
        Func<FlowContext<TIn>, TSubIn> map,
        SubFlowMode mode = SubFlowMode.Inline)
        where TFlow : Flow;

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

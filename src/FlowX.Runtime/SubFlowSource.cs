namespace FlowX.Runtime;

/// <summary>
/// The child a <see cref="StepKind.SubFlow"/> step is about to run, as the engine sees it:
/// a plan, a dispatcher, and an input it never looks inside.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="Input"/> is <c>object</c> for the reason
/// <see cref="IterationSource.Items"/> is.</strong> The engine owns control flow and knows
/// no contract types — that is the whole basis of <see cref="IStepDispatcher"/> — so it
/// cannot hold a <c>TSubIn</c>. It carries the reference from
/// <see cref="IStepDispatcher.BeginSubFlow"/> to
/// <see cref="IStepDispatcher.EnterSubFlow"/>, and the generated dispatcher, which does
/// know <c>TSubIn</c>, seeds it into the child's context under its own type.
/// </para>
/// <para>
/// <strong>The split into two calls is what keeps the child's lifecycle in the engine.</strong>
/// A single <c>ExecuteSubFlowAsync</c> on the dispatcher would have been shorter and would
/// have moved renting the child context, deriving its deadline, running its steps and
/// unwinding its compensation into generated code — four things the engine exists to own,
/// duplicated once per flow that composes another. Instead the dispatcher answers the one
/// question only it can (<em>which plan, which dispatcher, which input</em>) and the engine
/// does the rest.
/// </para>
/// <para>
/// A struct, so asking a flow what it is about to compose costs nothing.
/// </para>
/// </remarks>
public readonly struct SubFlowSource : IEquatable<SubFlowSource>
{
    /// <summary>Creates a source over a compiled child flow.</summary>
    /// <param name="plan">The child's compiled plan.</param>
    /// <param name="dispatcher">The child's generated dispatcher.</param>
    /// <param name="input">The child's input, opaque to the engine.</param>
    public SubFlowSource(ExecutionPlan plan, IStepDispatcher dispatcher, object? input)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);

        Plan = plan;
        Dispatcher = dispatcher;
        Input = input;
    }

    /// <summary>The child's compiled plan: its own graph, profile and deadline.</summary>
    public ExecutionPlan Plan { get; }

    /// <summary>The child's dispatcher, which knows the child's step types and nothing about this flow's.</summary>
    public IStepDispatcher Dispatcher { get; }

    /// <summary>
    /// The child's input, produced by the author's <c>map</c> delegate on the
    /// <em>parent's</em> thread before the child starts.
    /// </summary>
    /// <remarks>
    /// The timing is load-bearing rather than incidental. Mapping eagerly is what lets a
    /// <see cref="SubFlowMode.Detached"/> child outlive its parent safely: the value has
    /// already been taken out of the parent's pooled context, so nothing the child holds
    /// can still be pointing at a context that has since been reset and handed to another
    /// tenant's flow.
    /// </remarks>
    public object? Input { get; }

    /// <inheritdoc />
    public bool Equals(SubFlowSource other) =>
        ReferenceEquals(Plan, other.Plan) &&
        ReferenceEquals(Dispatcher, other.Dispatcher) &&
        ReferenceEquals(Input, other.Input);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SubFlowSource other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Plan, Dispatcher, Input);

    /// <summary>Compares two sources.</summary>
    public static bool operator ==(SubFlowSource left, SubFlowSource right) => left.Equals(right);

    /// <summary>Compares two sources.</summary>
    public static bool operator !=(SubFlowSource left, SubFlowSource right) => !left.Equals(right);
}

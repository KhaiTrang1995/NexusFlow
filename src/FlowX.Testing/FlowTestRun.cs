using FlowX.Runtime;

namespace FlowX.Testing;

/// <summary>What one <see cref="FlowTestHost"/> run produced: the engine's result, and a trace.</summary>
/// <remarks>
/// A class rather than a struct, unlike <see cref="FlowExecutionResult"/>: this is a test
/// object read once per test, so nothing is bought by making it copyable and something is
/// lost — a struct holding a trace reference reads as a value and is not one.
/// </remarks>
public sealed class FlowTestRun
{
    internal FlowTestRun(
        FlowExecutionResult result,
        FlowTestTrace trace,
        IReadOnlyList<string> unusedSubstitutions)
    {
        Result = result;
        Trace = trace;
        UnusedSubstitutions = unusedSubstitutions;
    }

    /// <summary>The engine's own result: error, completed steps, compensation outcome.</summary>
    public FlowExecutionResult Result { get; }

    /// <summary>What the flow did, in order.</summary>
    public FlowTestTrace Trace { get; }

    /// <summary>
    /// Capabilities this host was told to substitute that the run never reached.
    /// </summary>
    /// <remarks>
    /// Empty on a run that did what the test intended. A non-empty list is the signal
    /// that an assertion is passing for the wrong reason — the branch carrying the
    /// substituted step was not taken, or the flow failed before reaching it — which is
    /// exactly the failure a substitution-based test is otherwise blind to.
    /// </remarks>
    public IReadOnlyList<string> UnusedSubstitutions { get; }

    /// <summary>The business error, or <c>null</c> when the flow completed.</summary>
    public Error? Error => Result.Error;

    /// <summary>True when every step completed.</summary>
    public bool IsSuccess => Result.IsSuccess;

    /// <summary>True when the flow ended with an error.</summary>
    public bool IsFailure => Result.IsFailure;

    /// <summary>What happened to the compensations.</summary>
    public CompensationOutcome Compensation => Result.Compensation;

    /// <summary>How many steps completed before the flow ended.</summary>
    public int CompletedSteps => Result.CompletedSteps;

    /// <inheritdoc />
    public override string ToString() =>
        (Result.IsSuccess ? "completed" : "failed: " + Result.Error!.Code) + "\n" + Trace;
}

/// <summary>
/// What one run produced, together with the value the flow's <c>.Return(...)</c> projected.
/// </summary>
/// <typeparam name="TOutput">The flow's declared output contract.</typeparam>
/// <remarks>
/// Separate from <see cref="FlowTestRun"/> for the reason
/// <see cref="FlowExecutionResult{TOut}"/> is separate from
/// <see cref="FlowExecutionResult"/>: a flow triggered by a queue consumer has no caller
/// to return a value to, and forcing every such test to name an output type it discards
/// would be ceremony.
/// </remarks>
public sealed class FlowTestRun<TOutput>
{
    internal FlowTestRun(
        FlowExecutionResult<TOutput> result,
        FlowTestTrace trace,
        IReadOnlyList<string> unusedSubstitutions)
    {
        Result = result;
        Trace = trace;
        UnusedSubstitutions = unusedSubstitutions;
    }

    /// <summary>The engine's own result, including the projected output.</summary>
    public FlowExecutionResult<TOutput> Result { get; }

    /// <summary>What the flow did, in order.</summary>
    public FlowTestTrace Trace { get; }

    /// <inheritdoc cref="FlowTestRun.UnusedSubstitutions" />
    public IReadOnlyList<string> UnusedSubstitutions { get; }

    /// <summary>
    /// The projected output. Reading it on a failed flow throws, exactly as it does on
    /// <see cref="FlowExecutionResult{TOut}.Value"/>.
    /// </summary>
    public TOutput Output => Result.Value;

    /// <summary>The business error, or <c>null</c> when the flow completed.</summary>
    public Error? Error => Result.Error;

    /// <summary>True when every step completed.</summary>
    public bool IsSuccess => Result.IsSuccess;

    /// <summary>True when the flow ended with an error.</summary>
    public bool IsFailure => Result.IsFailure;

    /// <summary>What happened to the compensations.</summary>
    public CompensationOutcome Compensation => Result.Compensation;

    /// <summary>How many steps completed before the flow ended.</summary>
    public int CompletedSteps => Result.CompletedSteps;

    /// <inheritdoc />
    public override string ToString() =>
        (Result.IsSuccess ? "completed" : "failed: " + Result.Error!.Code) + "\n" + Trace;
}

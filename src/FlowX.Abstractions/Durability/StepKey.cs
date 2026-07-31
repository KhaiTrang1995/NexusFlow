namespace FlowX;

/// <summary>
/// The primary key of a journal row: one append-only record per
/// <c>(instance_id, scope, step_id, attempt)</c>.
/// </summary>
/// <param name="InstanceId">The flow instance. A sub-flow child is its own instance.</param>
/// <param name="Scope">
/// Which iteration the step ran in. <see cref="StepScope.Root"/> for the flow body — see
/// <see cref="StepScope"/> for why this is part of the key and not metadata.
/// </param>
/// <param name="StepId">The step's flat index, matching the plan, the manifest and traces.</param>
/// <param name="Attempt">
/// One-based. A retry writes a new row rather than replacing the failed one, so the attempt
/// history survives — which is what makes the replay contract in
/// <c>docs/06-Execution-Engine.md §5</c> provable rather than asserted.
/// </param>
/// <remarks>
/// A <c>readonly record struct</c> so a store can use it as a dictionary key, and so the
/// four fields travel together. They are only meaningful together: three of them identify a
/// row in most flows and all four are needed in a flow with a loop and a retry policy, which
/// is an ordinary flow.
/// </remarks>
public readonly record struct StepKey(Guid InstanceId, StepScope Scope, int StepId, int Attempt)
{
    /// <summary>The first attempt at a step in the flow body.</summary>
    /// <param name="instanceId">The flow instance.</param>
    /// <param name="stepId">The step's flat index.</param>
    public static StepKey First(Guid instanceId, int stepId) =>
        new(instanceId, StepScope.Root, stepId, 1);

    /// <summary>The next attempt at the same step in the same scope.</summary>
    public StepKey NextAttempt() => this with { Attempt = Attempt + 1 };

    /// <inheritdoc />
    public override string ToString() =>
        $"{InstanceId}/[{Scope.Text}]/step {StepId} attempt {Attempt}";
}

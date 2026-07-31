namespace FlowX.Postgres;

/// <summary>
/// Translates the closed enums in the journal contract to and from the text the
/// <c>CHECK</c> constraints accept.
/// </summary>
/// <remarks>
/// <para>
/// Written out as a switch rather than delegated to <c>Enum.Parse</c>. Three reasons, and
/// the first is the one that matters: the stored spelling is a schema, kept for the whole
/// retention window and read by operators and by <c>psql</c>. Deriving it from a member
/// name means renaming a C# member silently changes what is in the table and breaks every
/// row already written. A switch makes the rename a compile error instead.
/// </para>
/// <para>
/// The second is that a value the database returns is input, and <c>Enum.Parse</c> would
/// accept <c>"7"</c> and any comma-separated combination as valid. The third is that this
/// path is on every read of every row.
/// </para>
/// <para>
/// <c>StoredEnumTests</c> holds the mapping to the <c>CHECK</c> constraints in
/// <c>0001_initial_schema.sql</c>, so the closed set ADR-0015 describes stays one set
/// rather than two that agree today.
/// </para>
/// </remarks>
internal static class StoredEnums
{
    /// <summary>The text stored in <c>flow_instance.state</c>.</summary>
    /// <param name="state">The state to store.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a declared member.</exception>
    public static string ToText(FlowInstanceState state) => state switch
    {
        FlowInstanceState.Pending => "Pending",
        FlowInstanceState.Running => "Running",
        FlowInstanceState.Suspended => "Suspended",
        FlowInstanceState.Compensating => "Compensating",
        FlowInstanceState.Completed => "Completed",
        FlowInstanceState.Failed => "Failed",
        FlowInstanceState.TimedOut => "TimedOut",
        FlowInstanceState.CompensationFailed => "CompensationFailed",
        _ => throw new ArgumentOutOfRangeException(
            nameof(state),
            state,
            "The set of instance states is closed and matches the CHECK constraint on " +
            "flow_instance.state. A new member is a schema change, not an enum edit."),
    };

    /// <summary>The state a stored value denotes.</summary>
    /// <param name="text">The value read from <c>flow_instance.state</c>.</param>
    /// <exception cref="InvalidOperationException">The row holds a state this build cannot read.</exception>
    public static FlowInstanceState ToInstanceState(string text) => text switch
    {
        "Pending" => FlowInstanceState.Pending,
        "Running" => FlowInstanceState.Running,
        "Suspended" => FlowInstanceState.Suspended,
        "Compensating" => FlowInstanceState.Compensating,
        "Completed" => FlowInstanceState.Completed,
        "Failed" => FlowInstanceState.Failed,
        "TimedOut" => FlowInstanceState.TimedOut,
        "CompensationFailed" => FlowInstanceState.CompensationFailed,
        _ => throw new InvalidOperationException(
            $"flow_instance.state holds '{text}', which this build does not know. A newer " +
            "release wrote it, and reading it as anything else would misreport whether the " +
            "instance is finished."),
    };

    /// <summary>The text stored in <c>flow_step.outcome</c>.</summary>
    /// <param name="outcome">The outcome to store.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a declared member.</exception>
    public static string ToText(JournalOutcome outcome) => outcome switch
    {
        JournalOutcome.Success => "Success",
        JournalOutcome.Failure => "Failure",
        JournalOutcome.Compensated => "Compensated",
        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome),
            outcome,
            "The set of step outcomes is closed and matches the CHECK constraint on " +
            "flow_step.outcome."),
    };

    /// <summary>The outcome a stored value denotes.</summary>
    /// <param name="text">The value read from <c>flow_step.outcome</c>.</param>
    /// <exception cref="InvalidOperationException">The row holds an outcome this build cannot read.</exception>
    public static JournalOutcome ToOutcome(string text) => text switch
    {
        "Success" => JournalOutcome.Success,
        "Failure" => JournalOutcome.Failure,
        "Compensated" => JournalOutcome.Compensated,
        _ => throw new InvalidOperationException(
            $"flow_step.outcome holds '{text}', which this build does not know."),
    };

    /// <summary>Whether an instance in this state takes no further steps.</summary>
    /// <param name="state">The state to classify.</param>
    /// <remarks>
    /// The four states a flow ends in. <c>Compensating</c> is not one of them: an unwinding
    /// flow is still writing rows, and its compensations are the rows it writes.
    /// </remarks>
    public static bool IsTerminal(FlowInstanceState state) => state
        is FlowInstanceState.Completed
        or FlowInstanceState.Failed
        or FlowInstanceState.TimedOut
        or FlowInstanceState.CompensationFailed;
}

namespace FlowX;

/// <summary>A <c>flow_instance</c> row as a store hands it back.</summary>
/// <remarks>
/// The payload columns come back as the JSON that was stored rather than as
/// <see cref="JournalPayload"/>. The asymmetry is the security property, not an oversight: a
/// value can only enter the journal through a type that redacts, and what comes out is what
/// was written — a reader cannot recover a member that was never stored, and no read path
/// can be the one that leaks it.
/// </remarks>
public sealed record FlowInstanceRecord
{
    /// <summary>The instance's identity.</summary>
    public required Guid InstanceId { get; init; }

    /// <summary>The flow's business identity.</summary>
    public required string FlowId { get; init; }

    /// <summary>The version this instance is pinned to.</summary>
    public required string FlowVersion { get; init; }

    /// <summary>What the instance is doing.</summary>
    public required FlowInstanceState State { get; init; }

    /// <summary>The highest fencing token this instance has seen. A write below it is refused.</summary>
    public required FencingToken Fence { get; init; }

    /// <summary>The partition key.</summary>
    public string? TenantId { get; init; }

    /// <summary>The trigger input as stored.</summary>
    public string? InputJson { get; init; }

    /// <summary>The last committed state bag, as stored.</summary>
    public string? StateBagJson { get; init; }

    /// <summary>Ties every row of this instance to the request that started it.</summary>
    public string CorrelationId { get; init; } = string.Empty;

    /// <summary>The W3C trace id, when the trigger carried one.</summary>
    public string? TraceId { get; init; }

    /// <summary>When this instance must be finished.</summary>
    public DateTimeOffset? DeadlineAt { get; init; }

    /// <summary>
    /// The instance that composed this one, or null for a flow a trigger started.
    /// </summary>
    /// <remarks>
    /// Nullable on read even for a child whose parent has been archived. A detached sub-flow
    /// can outlive its parent, so retention can remove a parent while a child is still
    /// running, and an orphan must be legible rather than a foreign-key error.
    /// </remarks>
    public Guid? ParentInstanceId { get; init; }

    /// <summary>The scope of the parent step that composed this one.</summary>
    public StepScope ParentScope { get; init; }

    /// <summary>The parent's step index that composed this one.</summary>
    public int? ParentStepId { get; init; }

    /// <summary>The operator's hint. Never the resume position — see <see cref="StepCommit.ResumeHint"/>.</summary>
    public int? ResumeHint { get; init; }

    /// <summary>
    /// The wait a <see cref="FlowInstanceState.Suspended"/> instance is parked at and the
    /// instant it is due, or <c>null</c> when nothing is due to wake it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the whole of a durable timer.</strong> A wait is three values on a row,
    /// so a million parked instances cost a million rows and no compute — and what ends the
    /// wait is a sweep reading a column, not a process holding a <c>Task.Delay</c> for seven
    /// days.
    /// </para>
    /// <para>
    /// <strong>Read by the step loop as well as by the sweep, and the step loop is the one
    /// that decides.</strong> A sweep says "this instance is due"; the engine, standing on the
    /// node the instance is parked at, compares the recorded instant against its clock and
    /// either walks on or parks again. That keeps the decision beside the plan that declared
    /// the duration rather than in a query that would have to be told about it.
    /// </para>
    /// </remarks>
    public FlowWake? Wake { get; init; }

    /// <summary>When the instance was recorded.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the instance row last changed.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A committed <c>flow_step</c> row.</summary>
public sealed record JournalStep
{
    /// <summary>Which step, in which iteration, on which attempt.</summary>
    public required StepKey Key { get; init; }

    /// <summary>
    /// The instance-local commit order, strictly increasing.
    /// </summary>
    /// <remarks>
    /// History is the truth, and the order in which it happened is part of it. Step index
    /// order is not commit order once a flow forks, and neither is timestamp order once two
    /// nodes have clocks.
    /// </remarks>
    public required long Sequence { get; init; }

    /// <summary>The capability the step invoked.</summary>
    public required string CapabilityId { get; init; }

    /// <summary>The version that was resolved when it ran.</summary>
    public required string CapabilityVersion { get; init; }

    /// <summary>How the attempt ended.</summary>
    public required JournalOutcome Outcome { get; init; }

    /// <summary>The result or error as stored.</summary>
    public string? ResultJson { get; init; }

    /// <summary>What the step read that it could not have computed, for replay.</summary>
    public NondeterminismCapture Nondeterminism { get; init; } = NondeterminismCapture.None;

    /// <summary>How long the attempt took.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>When the row was committed.</summary>
    public DateTimeOffset CommittedAt { get; init; }
}

/// <summary>A staged outbox row.</summary>
public sealed record OutboxRecord
{
    /// <summary>The event's identity, assigned by the store.</summary>
    public required Guid EventId { get; init; }

    /// <summary>The instance that emitted it.</summary>
    public required Guid InstanceId { get; init; }

    /// <summary>The event type.</summary>
    public required string Type { get; init; }

    /// <summary>The event contract's semantic version.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>The key that preserves per-key ordering at the broker.</summary>
    public string? PartitionKey { get; init; }

    /// <summary>The event body as stored.</summary>
    public string? PayloadJson { get; init; }

    /// <summary>When it was published, or null while it is pending.</summary>
    public DateTimeOffset? PublishedAt { get; init; }
}

/// <summary>
/// Everything a node needs to work out where a instance got to: the instance's own row and
/// every step that committed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A set, not a cursor.</strong> A <c>Parallel</c> fork can be half done — branch A
/// finished, branch B stopped at step 12 — and no single integer describes that. The engine
/// derives the position by asking which steps have committed and comparing that against the
/// compiled plan, which it already has; the journal's job is to answer the first half
/// truthfully and nothing more.
/// </para>
/// <para>
/// Deriving rather than remembering is also what makes resume trustworthy: the plan is
/// compile-time and the committed rows are facts, so recovery never has to believe a
/// position field that a crashing node was in the middle of writing.
/// </para>
/// </remarks>
public sealed record ResumeFrontier
{
    /// <summary>The instance being resumed.</summary>
    public required FlowInstanceRecord Instance { get; init; }

    /// <summary>Every committed step, in commit order.</summary>
    public required IReadOnlyList<JournalStep> Committed { get; init; }

    /// <summary>Whether an attempt at this step in this scope has already committed.</summary>
    /// <param name="scope">The iteration the step would run in.</param>
    /// <param name="stepId">The step's flat index.</param>
    public bool IsCommitted(StepScope scope, int stepId)
    {
        for (var i = 0; i < Committed.Count; i++)
        {
            var key = Committed[i].Key;

            if (key.StepId == stepId && key.Scope == scope)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The recorded row for one key, so a resumed step returns what it returned before rather
    /// than executing again.
    /// </summary>
    /// <param name="key">The step, scope and attempt to look up.</param>
    /// <returns>The committed row, or null if that attempt never committed.</returns>
    public JournalStep? Find(StepKey key)
    {
        for (var i = 0; i < Committed.Count; i++)
        {
            if (Committed[i].Key == key)
            {
                return Committed[i];
            }
        }

        return null;
    }
}

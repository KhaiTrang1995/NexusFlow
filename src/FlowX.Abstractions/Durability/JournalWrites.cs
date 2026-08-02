namespace FlowX;

/// <summary>
/// Everything a store needs to open a <c>flow_instance</c> row.
/// </summary>
/// <remarks>
/// A sub-flow child is started the same way as a root instance, with
/// <see cref="ParentInstanceId"/> set. WP-33 refused to splice a child's steps into its
/// parent's plan at compile time — it would make the parent's manifest claim the child's
/// capabilities and discard the child's deadline and profile — and splicing in the journal
/// would reintroduce the same untruth one layer down. It is also the only shape in which a
/// detached child, which outlives the step that started it, has anywhere to live.
/// </remarks>
public sealed record FlowInstanceStart
{
    /// <summary>The instance's identity, chosen by the caller so a retry of the trigger is idempotent.</summary>
    public required Guid InstanceId { get; init; }

    /// <summary>The flow's business identity, e.g. <c>order.place</c>.</summary>
    public required string FlowId { get; init; }

    /// <summary>
    /// The flow version this instance is pinned to for its whole life, so a mid-flight
    /// deployment cannot change its semantics.
    /// </summary>
    public required string FlowVersion { get; init; }

    /// <summary>The token of the lease held while starting. Becomes the instance's opening fence.</summary>
    public required FencingToken Token { get; init; }

    /// <summary>The partition key. Null only for a single-tenant deployment.</summary>
    public string? TenantId { get; init; }

    /// <summary>The trigger input, recorded immutably.</summary>
    public JournalPayload Input { get; init; } = JournalPayload.Empty;

    /// <summary>
    /// The handle this instance's data subject is erased by, or null when the input named
    /// none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived from <see cref="Input"/> — <see cref="JournalPayload.SubjectDigest"/> — rather
    /// than supplied beside it, so the two cannot disagree about which record this is. It is
    /// carried as its own property instead of being read off the payload by each store,
    /// because a store that forgot to read it would write rows nothing can ever erase and
    /// nothing would report a problem: a column present and always null is the shape this
    /// repository has shipped before.
    /// </para>
    /// <para>
    /// A store that indexes it can answer "everything about this person" as a lookup. A store
    /// that ignores it is not wrong — it is a store that does not implement
    /// <see cref="ISubjectErasure"/>, which is a fact about that store rather than a silent
    /// loss.
    /// </para>
    /// </remarks>
    public string? SubjectDigest { get; init; }

    /// <summary>Ties every row of this instance to the request that started it.</summary>
    public string CorrelationId { get; init; } = string.Empty;

    /// <summary>The W3C trace id, when the trigger carried one.</summary>
    public string? TraceId { get; init; }

    /// <summary>When this instance must be finished, from the flow's declared deadline.</summary>
    public DateTimeOffset? DeadlineAt { get; init; }

    /// <summary>The instance that composed this one, or null for a flow a trigger started.</summary>
    public Guid? ParentInstanceId { get; init; }

    /// <summary>The scope of the parent step that composed this one.</summary>
    public StepScope ParentScope { get; init; }

    /// <summary>The parent's step index that composed this one, or null for a root instance.</summary>
    public int? ParentStepId { get; init; }
}

/// <summary>
/// One step boundary: the row to append, the state bag as it now stands, and the events the
/// step produced — all committed together or not at all.
/// </summary>
/// <remarks>
/// <para>
/// The unit of the contract is the whole commit, not the step row, because atomicity is the
/// property that removes the dual-write problem. A store that writes the step and then the
/// outbox has reintroduced it, and the conformance suite is what notices.
/// </para>
/// <para>
/// <see cref="Token"/> is checked before anything is written. A commit whose token is below
/// the instance's fence is refused and leaves the journal exactly as it was — including its
/// outbox, which is the part that is easy to get wrong and expensive to discover in
/// production.
/// </para>
/// </remarks>
public sealed record StepCommit
{
    /// <summary>Which step, in which iteration, on which attempt.</summary>
    public required StepKey Key { get; init; }

    /// <summary>The writer's lease token, checked against the instance's fence.</summary>
    public required FencingToken Token { get; init; }

    /// <summary>The capability the step invoked, e.g. <c>payment.capture</c>.</summary>
    public required string CapabilityId { get; init; }

    /// <summary>
    /// The resolved capability version, recorded per step so a mid-flight deployment cannot
    /// change what a replay means.
    /// </summary>
    public required string CapabilityVersion { get; init; }

    /// <summary>How the attempt ended.</summary>
    public required JournalOutcome Outcome { get; init; }

    /// <summary>The step's result, or its error, as the generated JSON context writes it.</summary>
    public JournalPayload Result { get; init; } = JournalPayload.Empty;

    /// <summary>The flow's state bag after this step — the snapshot that bounds a resume scan.</summary>
    public JournalPayload StateBag { get; init; } = JournalPayload.Empty;

    /// <summary>What the step read that it could not have computed.</summary>
    public NondeterminismCapture Nondeterminism { get; init; } = NondeterminismCapture.None;

    /// <summary>How long the attempt took.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>The instance's new state, when this step changed it.</summary>
    public FlowInstanceState? State { get; init; }

    /// <summary>
    /// A denormalised hint for operator queries, and nothing else.
    /// </summary>
    /// <remarks>
    /// <strong>Never read back by the engine.</strong> A scalar cursor cannot describe a
    /// half-completed fork, and a fork is a shape the DSL shipped in P1 — so the resume
    /// position is derived from committed rows by
    /// <see cref="IFlowJournal.ReadResumeFrontierAsync"/>, and a value written here by a node
    /// that then crashed cannot mislead recovery. It survives because "roughly where is this
    /// stuck instance" is a real question an operator asks of a table.
    /// </remarks>
    public int? ResumeHint { get; init; }

    /// <summary>Events the step emitted, written in the same transaction as the step row.</summary>
    public IReadOnlyList<OutboxWrite> Outbox { get; init; } = [];
}

/// <summary>An event to publish, staged in the same transaction that records the step.</summary>
/// <remarks>
/// The transactional outbox is what makes "the state changed and the event was emitted" a
/// single fact. Publishing it, retrying it and marking it published belong to the publisher
/// (<c>docs/11-Distributed-Runtime.md §5</c>) and are not part of this contract; staging it
/// atomically is.
/// </remarks>
public sealed record OutboxWrite
{
    /// <summary>The event type, e.g. <c>order.placed</c>.</summary>
    public required string Type { get; init; }

    /// <summary>The event contract's semantic version.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>The key that preserves per-key ordering at the broker. Null for unordered events.</summary>
    public string? PartitionKey { get; init; }

    /// <summary>The event body, redacted by the same rules as any other journal payload.</summary>
    public JournalPayload Payload { get; init; } = JournalPayload.Empty;
}

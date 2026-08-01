namespace FlowX;

/// <summary>
/// The append-only record of what a durable flow instance has done, and the only place a
/// durable step boundary is made permanent.
/// </summary>
/// <remarks>
/// <para>
/// One mechanism, three consumers: the journal is the replay source, the audit source and
/// the operator's view of a stuck instance. That is why it records step boundaries rather
/// than state-bag writes — a history of effects is legible to all three, and a history of
/// dictionary mutations is legible to none of them.
/// </para>
/// <para>
/// <strong>Every member here is pinned by the conformance suite.</strong> A store either
/// passes <c>JournalConformance</c> or it is not an <see cref="IFlowJournal"/>, whatever it
/// compiles against — a syntactic interface is not a sufficient specification for behaviour
/// like fencing, atomicity and append-only refusal (ADR-0009). The suite is where the
/// semantics are written down; the summaries here point at them.
/// </para>
/// <para>
/// <strong>Errors are values.</strong> A refusal — a stale token, a duplicate key, a
/// finished instance — is an <see cref="Error"/> from <see cref="DurabilityErrors"/>, not an
/// exception. An exception is reserved for a store that is broken or unreachable, which is
/// a different thing from a store that is working correctly and saying no.
/// </para>
/// </remarks>
public interface IFlowJournal
{
    /// <summary>
    /// Opens the <c>flow_instance</c> row for a new instance, root or child.
    /// </summary>
    /// <param name="start">The instance to record.</param>
    /// <param name="cancellationToken">Cancels the store call.</param>
    /// <returns>
    /// The recorded row, or <see cref="DurabilityErrors.InstanceExists"/> if that id is
    /// already known. Starting twice is refused rather than merged: an append-only history
    /// cannot be replaced.
    /// </returns>
    ValueTask<Result<FlowInstanceRecord>> StartAsync(
        FlowInstanceStart start,
        CancellationToken cancellationToken);

    /// <summary>
    /// Raises the instance's fence to a newly acquired lease token, before the new owner
    /// writes anything.
    /// </summary>
    /// <param name="instanceId">The instance being taken over.</param>
    /// <param name="token">The token the lease store just issued.</param>
    /// <param name="cancellationToken">Cancels the store call.</param>
    /// <returns>The instance's fence after the call, or an error.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Without this, fencing has a window.</strong> If the journal only learned a
    /// token from a write, then between node 2 acquiring token 8 and node 2's first commit,
    /// a zombie node 1 could still commit with token 7 — the journal's highest seen token
    /// would still be 7, so the write would be accepted. The sequence in
    /// <c>docs/11-Distributed-Runtime.md §3</c> requires the rejection to hold from the
    /// moment of acquisition, which means acquisition has to reach the journal.
    /// </para>
    /// <para>
    /// It is a separate call because the lease store and the journal are separate plugins:
    /// a Redis lease store and a Postgres journal share no transaction, so the fence is
    /// raised by the node that just won the lease, immediately after winning it and before
    /// reading history.
    /// </para>
    /// </remarks>
    ValueTask<Result<FencingToken>> FenceAsync(
        Guid instanceId,
        FencingToken token,
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends one step boundary: the step row, the state-bag snapshot and any outbox rows,
    /// in one transaction, guarded by the fencing token.
    /// </summary>
    /// <param name="commit">The step boundary to record.</param>
    /// <param name="cancellationToken">Cancels the store call.</param>
    /// <returns>
    /// The committed row, or a refusal: <see cref="DurabilityErrors.FencedOut"/> when the
    /// token is below the fence, <see cref="DurabilityErrors.DuplicateStep"/> when the key
    /// has already been written, <see cref="DurabilityErrors.InstanceTerminal"/> when the
    /// instance has finished.
    /// </returns>
    /// <remarks>
    /// A refused commit writes <em>nothing</em> — no step row, no state-bag change, no outbox
    /// row. That is the half of atomicity that is easy to lose and expensive to discover.
    /// </remarks>
    ValueTask<Result<JournalStep>> CommitAsync(
        StepCommit commit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves the instance to the state it comes to rest in and records its final state bag.
    /// </summary>
    /// <param name="instanceId">The instance that has finished, or has parked.</param>
    /// <param name="token">The writer's lease token, checked against the fence.</param>
    /// <param name="state">The state to rest in.</param>
    /// <param name="stateBag">The final state bag, or <see cref="JournalPayload.Empty"/>.</param>
    /// <param name="wake">
    /// The wait a <see cref="FlowInstanceState.Suspended"/> instance is parked at and when it
    /// is due, or <c>null</c> for one nothing is due to wake. Recorded in the same write that
    /// records the state, and cleared by every other state.
    /// </param>
    /// <param name="cancellationToken">Cancels the store call.</param>
    /// <returns>The instance row as it now stands, or an error.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Not every state here is terminal, and <paramref name="wake"/> is why the
    /// distinction has to be in the contract.</strong> A suspended instance is the one that
    /// carries on afterwards, and what it is waiting for and until when is state — values on a
    /// row, not a timer in a process. Writing them in a second call would leave a window in
    /// which an instance is parked with nothing scheduled to wake it, which is a wait that
    /// never ends and is indistinguishable from the defect this parameter exists to fix.
    /// </para>
    /// <para>
    /// <strong>Cleared by every other state, and that is a requirement rather than a
    /// courtesy.</strong> A timer sweep reads the instances whose wake instant has passed; a
    /// completed instance keeping the one it was waiting on before it finished would be woken
    /// for ever.
    /// </para>
    /// </remarks>
    ValueTask<Result<FlowInstanceRecord>> CompleteAsync(
        Guid instanceId,
        FencingToken token,
        FlowInstanceState state,
        JournalPayload stateBag,
        FlowWake? wake,
        CancellationToken cancellationToken);

    /// <summary>Reads one instance row.</summary>
    /// <param name="instanceId">The instance to read.</param>
    /// <param name="cancellationToken">Cancels the store call.</param>
    /// <returns>The row, or <see cref="DurabilityErrors.InstanceNotFound"/>.</returns>
    ValueTask<Result<FlowInstanceRecord>> ReadInstanceAsync(
        Guid instanceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads everything needed to work out where an instance got to.
    /// </summary>
    /// <param name="instanceId">The instance to resume.</param>
    /// <param name="cancellationToken">Cancels the store call.</param>
    /// <returns>The instance row and its committed steps in commit order, or an error.</returns>
    /// <remarks>
    /// The engine derives the resume position from this against the compiled plan. The
    /// journal never returns a position of its own, because a scalar cannot describe a
    /// half-completed fork and a field written by a crashing node cannot be trusted.
    /// </remarks>
    ValueTask<Result<ResumeFrontier>> ReadResumeFrontierAsync(
        Guid instanceId,
        CancellationToken cancellationToken);

    /// <summary>Reads the outbox rows staged by an instance, in commit order.</summary>
    /// <param name="instanceId">The instance whose events to read.</param>
    /// <param name="cancellationToken">Cancels the store call.</param>
    /// <returns>The staged rows, or an error.</returns>
    /// <remarks>
    /// Present so that the atomicity of a commit is observable through the contract rather
    /// than only through a store's own schema. Publishing, retrying and marking published
    /// belong to the outbox publisher and are deliberately not here.
    /// </remarks>
    ValueTask<Result<IReadOnlyList<OutboxRecord>>> ReadOutboxAsync(
        Guid instanceId,
        CancellationToken cancellationToken);
}

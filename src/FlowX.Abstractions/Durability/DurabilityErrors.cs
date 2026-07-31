namespace FlowX;

/// <summary>
/// The errors an <see cref="IFlowJournal"/> or an <see cref="ILeaseStore"/> returns, as
/// values rather than exceptions (ADR-0007).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The codes are part of the contract, not of any one store.</strong> A caller
/// deciding whether to abort, resume or read a recorded result branches on the code, and it
/// must be able to do that without knowing whether it is talking to Postgres, Redis or a
/// test double. The conformance suite asserts the code and the category on every refusal for
/// exactly that reason.
/// </para>
/// <para>
/// <strong>The categories carry the retry decision, so they are the important half.</strong>
/// <see cref="FencedOut"/> is <see cref="ErrorCategory.Forbidden"/> — terminal — because a
/// node that lost the lease must abort and discard its work. Retrying with the same stale
/// token is guaranteed to fail again, and a retryable classification would turn a correct
/// refusal into a loop.
/// </para>
/// </remarks>
public static class DurabilityErrors
{
    /// <summary>An instance with this id has already been recorded.</summary>
    public const string InstanceExistsCode = "journal.instance_exists";

    /// <summary>No instance with this id.</summary>
    public const string InstanceNotFoundCode = "journal.instance_not_found";

    /// <summary>The writer's fencing token is below the instance's fence.</summary>
    public const string FencedOutCode = "journal.fenced_out";

    /// <summary>A row with this exact key has already been committed.</summary>
    public const string DuplicateStepCode = "journal.duplicate_step";

    /// <summary>The instance has already reached a terminal state.</summary>
    public const string InstanceTerminalCode = "journal.instance_terminal";

    /// <summary>Another node holds a live lease on this instance.</summary>
    public const string LeaseHeldCode = "lease.held";

    /// <summary>Nobody holds a live lease on this instance.</summary>
    public const string LeaseNotHeldCode = "lease.not_held";

    /// <summary>The caller's lease was superseded or expired; it no longer owns the instance.</summary>
    public const string LeaseLostCode = "lease.lost";

    /// <summary>An instance with this id has already been recorded.</summary>
    /// <param name="instanceId">The instance that already exists.</param>
    public static Error InstanceExists(Guid instanceId) => new(
        InstanceExistsCode,
        $"Flow instance '{instanceId}' has already been started. The journal is append-only: " +
        "starting an instance twice would replace a history rather than extend it.",
        ErrorCategory.Conflict);

    /// <summary>No instance with this id.</summary>
    /// <param name="instanceId">The instance that was looked for.</param>
    public static Error InstanceNotFound(Guid instanceId) => new(
        InstanceNotFoundCode,
        $"Flow instance '{instanceId}' is not in the journal. It was never started, or its " +
        "retention window has passed.",
        ErrorCategory.NotFound);

    /// <summary>The writer's fencing token is below the instance's fence.</summary>
    /// <param name="instanceId">The instance being written to.</param>
    /// <param name="presented">The token the writer offered.</param>
    /// <param name="fence">The instance's current fence.</param>
    public static Error FencedOut(Guid instanceId, FencingToken presented, FencingToken fence) => new(
        FencedOutCode,
        $"Fencing token {presented} is below instance '{instanceId}'s fence of {fence}. " +
        "Another node owns this instance now. Abort, discard the work and release — retrying " +
        "with this token can never succeed.",
        ErrorCategory.Forbidden);

    /// <summary>A row with this exact key has already been committed.</summary>
    /// <param name="key">The key that is already present.</param>
    public static Error DuplicateStep(StepKey key) => new(
        DuplicateStepCode,
        $"A row for {key} is already committed. The journal is append-only, so this is " +
        "refused rather than overwritten; read the recorded row instead of re-committing, or " +
        "record a new attempt.",
        ErrorCategory.Conflict);

    /// <summary>The instance has already reached a terminal state.</summary>
    /// <param name="instanceId">The instance being written to.</param>
    /// <param name="state">The terminal state it reached.</param>
    public static Error InstanceTerminal(Guid instanceId, FlowInstanceState state) => new(
        InstanceTerminalCode,
        $"Flow instance '{instanceId}' is {state} and takes no further steps.",
        ErrorCategory.Conflict);

    /// <summary>Another node holds a live lease on this instance.</summary>
    /// <param name="instanceId">The instance that is held.</param>
    /// <param name="ownerNode">Who holds it.</param>
    public static Error LeaseHeld(Guid instanceId, string ownerNode) => new(
        LeaseHeldCode,
        $"Instance '{instanceId}' is leased to '{ownerNode}'. Wait for the lease to expire or " +
        "be released; a live lease is not renewed by acquiring it again.",
        ErrorCategory.Conflict);

    /// <summary>Nobody holds a live lease on this instance.</summary>
    /// <param name="instanceId">The instance that is unleased.</param>
    public static Error LeaseNotHeld(Guid instanceId) => new(
        LeaseNotHeldCode,
        $"No live lease on instance '{instanceId}'.",
        ErrorCategory.NotFound);

    /// <summary>The caller's lease was superseded or expired.</summary>
    /// <param name="instanceId">The instance whose lease was lost.</param>
    /// <param name="presented">The token the caller offered.</param>
    public static Error LeaseLost(Guid instanceId, FencingToken presented) => new(
        LeaseLostCode,
        $"Lease token {presented} on instance '{instanceId}' is no longer current: it expired " +
        "or another node has acquired since. Discard the work in flight — this token cannot " +
        "renew, release or commit.",
        ErrorCategory.Forbidden);
}

namespace FlowX;

/// <summary>Whether an erasure reports what it would do, or does it.</summary>
/// <remarks>
/// Two modes rather than one call and a flag on the receipt, because the difference is what
/// the store is asked to do rather than what it says afterwards. An operator running the
/// first one is answering "what does this system hold about this person"; running the second
/// is answering it and then destroying the answer.
/// </remarks>
public enum ErasureMode
{
    /// <summary>Report what would be erased and change nothing.</summary>
    DryRun = 0,

    /// <summary>Erase.</summary>
    Confirm = 1,
}

/// <summary>Why one instance was left alone.</summary>
/// <param name="InstanceId">The instance that was not erased.</param>
/// <param name="Reason">The condition that has to clear first, in words an operator can act on.</param>
public readonly record struct ErasureWithheld(Guid InstanceId, string Reason);

/// <summary>What an erasure was asked to do.</summary>
public sealed record ErasureRequest
{
    /// <summary>The subject, as <see cref="SubjectDigest.Of"/> computes it.</summary>
    /// <remarks>
    /// A digest rather than the identifier, and the caller computes it. The alternative —
    /// taking the identifier and hashing it here — would put a live national identifier into
    /// the argument list of a store call, where it would be caught by every parameter-logging
    /// interceptor in the deployment, on the one code path whose entire purpose is to make
    /// that identifier stop existing.
    /// </remarks>
    public required string SubjectDigest { get; init; }

    /// <summary>Whose data. Null only on a single-tenant deployment.</summary>
    /// <remarks>
    /// Scoping is not optional in a multi-tenant deployment and is not enforced here: the
    /// store is expected to be a tenant-scoped one, so the database refuses a cross-tenant
    /// erasure for the same reason and by the same policy that refuses a cross-tenant read.
    /// This carries the value so a receipt can name it.
    /// </remarks>
    public string? TenantId { get; init; }

    /// <summary>Report, or erase.</summary>
    public ErasureMode Mode { get; init; } = ErasureMode.DryRun;
}

/// <summary>
/// What an erasure did, or would have done. The record an operator files.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Not signed, deliberately.</strong> A signature is only worth as much as the key
/// management behind it, and this repository ships none: there is no key store, no rotation,
/// no revocation and nothing that would verify a signature later. A receipt signed with a key
/// generated at start-up is a compliance artefact that proves nothing while looking like it
/// proves something, which is worse than a plain one. What makes this trustworthy is that it
/// is a return value of the call that did the work, produced from the row counts that call
/// observed — not a document assembled afterwards from a second query.
/// </para>
/// <para>
/// <strong>The counts are not the same number.</strong> <see cref="Matched"/> is what the
/// subject has in this store; <see cref="Erased"/> is what this call cleared;
/// <see cref="Withheld"/> is the difference and says why for each one. An erasure that
/// reported only a total would let "three of five" read as success.
/// </para>
/// </remarks>
public sealed record ErasureReceipt
{
    /// <summary>The subject this receipt is about.</summary>
    public required string SubjectDigest { get; init; }

    /// <summary>Whose data, as the request named it.</summary>
    public string? TenantId { get; init; }

    /// <summary>Whether this receipt describes work done or work proposed.</summary>
    public required ErasureMode Mode { get; init; }

    /// <summary>Instances carrying this subject's digest.</summary>
    public required int Matched { get; init; }

    /// <summary>Instances whose payloads this call cleared, or would clear.</summary>
    public required int Erased { get; init; }

    /// <summary>Step rows whose recorded payloads were cleared, or would be.</summary>
    public required int StepsCleared { get; init; }

    /// <summary>Staged event bodies that were cleared, or would be.</summary>
    public required int EventsCleared { get; init; }

    /// <summary>Instances left alone, each with the reason.</summary>
    public IReadOnlyList<ErasureWithheld> Withheld { get; init; } = [];

    /// <summary>When the store did the work, from the store's clock.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Whether every matched instance was dealt with.</summary>
    public bool IsComplete => Withheld.Count == 0;
}

/// <summary>
/// Removes everything a store holds about one data subject.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What "erase" means here: the payloads go, the skeleton stays.</strong> The
/// instance row, its step rows and its outbox rows survive with every recorded value set to
/// null — input, state bag, step results, captured nondeterminism, event bodies — and with
/// the subject digest cleared, so a second erasure of the same subject matches nothing.
/// Deleting the rows outright was rejected: an instance row also records that a flow ran, at
/// what version, with what outcome and when, which is the operational and audit history of
/// the *system* rather than personal data about the subject, and a durable execution that
/// vanished mid-recovery would be indistinguishable from one that never happened.
/// </para>
/// <para>
/// <strong>A live instance is withheld, never erased.</strong> Clearing the state bag of an
/// instance that is still running hands the rest of its steps the values of a flow nobody
/// executed — the precise failure <c>IStepDispatcher.RestoreState</c> refuses to resume
/// into. So an instance that is not in a terminal state is reported in
/// <see cref="ErasureReceipt.Withheld"/> and the caller runs the erasure again when it has
/// finished. The same holds for an instance still holding an unpublished outbox event: the
/// publisher would drain an event whose body had been emptied under it, and the consumer
/// downstream would receive a message that never carried what it says it carries.
/// </para>
/// <para>
/// <strong>What this cannot reach, stated so nobody assumes otherwise.</strong> A result
/// cache entry and an idempotency record are keyed by a hash of the step's *input* and carry
/// no subject, so they cannot be found by subject at all — a scan of every key in the store,
/// deserialising each entry, is the only way, and it would be a second disclosure with an
/// unbounded cost. Both are bounded instead by the window and the TTL their declarations
/// already carry, which is a statement about how long a value survives rather than about
/// whether it can be removed on request. A deployment whose retention obligation is shorter
/// than its cache window has to shorten the window.
/// </para>
/// </remarks>
public interface ISubjectErasure
{
    /// <summary>Erases one subject's payloads, or reports what erasing them would do.</summary>
    /// <param name="request">Whose data, in which tenant, and whether to do it.</param>
    /// <param name="cancellationToken">Cancels the store calls.</param>
    /// <returns>The receipt, or the store's refusal.</returns>
    ValueTask<Result<ErasureReceipt>> EraseAsync(
        ErasureRequest request,
        CancellationToken cancellationToken);
}

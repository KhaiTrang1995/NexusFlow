namespace FlowX;

/// <summary>What a key was found to be when a caller presented it.</summary>
public enum IdempotencyState
{
    /// <summary>
    /// Nobody held it, and this caller now does. The caller runs the step and must finish with
    /// <see cref="IIdempotencyStore.CompleteAsync"/> or
    /// <see cref="IIdempotencyStore.AbandonAsync"/>.
    /// </summary>
    Started = 0,

    /// <summary>
    /// Another caller is running it. <c>docs/10-Policy-Framework.md</c> §7's third arm — the one
    /// it calls "the most common bug in hand-rolled idempotency" when it is missing.
    /// </summary>
    InFlight = 1,

    /// <summary>It ran, it succeeded, and <see cref="IdempotencyEntry.Record"/> is what it produced.</summary>
    Completed = 2,
}

/// <summary>What <see cref="IIdempotencyStore.BeginAsync"/> found.</summary>
/// <param name="State">Which of the three arms this is.</param>
/// <param name="Record">
/// The recorded document, non-null exactly when <paramref name="State"/> is
/// <see cref="IdempotencyState.Completed"/>. Whatever the caller stored: this contract does not
/// read it.
/// </param>
/// <param name="RetryAfter">
/// How long the in-flight holder's marker has left, for
/// <see cref="IdempotencyState.InFlight"/>. Zero otherwise.
/// </param>
public readonly record struct IdempotencyEntry(
    IdempotencyState State,
    string? Record,
    TimeSpan RetryAfter);

/// <summary>
/// Records what a step produced against a key, so that a caller presenting the key again is
/// answered rather than re-executed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The key is not this contract's to invent.</strong> It arrives built, from
/// <c>ctx.IdempotencyKey</c> — which is stable across a flow and across every attempt of a
/// retried step, and reaches the capability already — narrowed by the capability id and the
/// declared <see cref="IdempotencyScope"/>. A store never parses it. See
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0041-an-idempotency-record-is-keyed-by-the-invocations-key.md">ADR-0041</a>.
/// </para>
/// <para>
/// <strong>Only a success is ever recorded.</strong> A step that failed calls
/// <see cref="AbandonAsync"/> and its key is free again. That is
/// <c>docs/10-Policy-Framework.md</c> §8's "negative caching: off — stale failures are worse
/// than a retry" applied one stage earlier, where it matters more: a recorded failure would be
/// replayed for the whole window, so a transient outage at the moment a key was first presented
/// would make that key unusable for as long as the author declared — and the caller's remedy,
/// presenting it again, is exactly what would keep failing.
/// </para>
/// <para>
/// <strong><see cref="BeginAsync"/> is a compare-and-set, never a read followed by a
/// write.</strong> Two callers presenting one key at the same instant is the case the whole
/// policy exists for. Every implementation performs the check and the claim as one indivisible
/// step against its own server's clock, for <see cref="ILeaseStore"/>'s reason.
/// </para>
/// <para>
/// <strong>What a store may be handed is decided elsewhere and is decided severely.</strong> The
/// record is a document that has already been through <see cref="JournalPayload"/>'s single
/// redacting exit, and the engine will not write one that exit had to change — a replayed
/// <c>[redacted]</c> returned as if it were the value is a fabricated answer rather than a
/// degraded policy. See
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0042-a-recorded-result-is-replayed-only-when-recording-lost-nothing.md">ADR-0042</a>
/// and <a href="https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1040.md">FLOWX1040</a>.
/// </para>
/// <para>
/// A step declaring an idempotency window with no store registered is <strong>refused</strong>,
/// and so is one whose store call fails. Dispatching on doubt is the duplicate the policy was
/// declared to prevent.
/// </para>
/// <para>
/// Every member here is pinned by <c>IdempotencyStoreConformance</c>.
/// </para>
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>
    /// Claims <paramref name="key"/> for this caller, or reports who has it and what they got.
    /// </summary>
    /// <param name="key">The built key. Opaque to the store.</param>
    /// <param name="window">
    /// How long a completed record is replayed for — the author's declared
    /// <c>.Idempotency(window)</c>. Positive.
    /// </param>
    /// <param name="inFlightFor">
    /// How long this caller's claim survives without being completed or abandoned. Positive, and
    /// separate from <paramref name="window"/> on purpose: the claim is a lease, so a node that
    /// takes a key and then dies must not wedge every repeat of it for a declared 24 hours. The
    /// engine bounds it by the flow's own remaining deadline.
    /// </param>
    /// <param name="cancellationToken">Cancels the store call.</param>
    ValueTask<Result<IdempotencyEntry>> BeginAsync(
        string key,
        TimeSpan window,
        TimeSpan inFlightFor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records what the step produced, replacing this caller's claim.
    /// </summary>
    /// <param name="key">The key claimed by a <see cref="BeginAsync"/> that answered
    /// <see cref="IdempotencyState.Started"/>.</param>
    /// <param name="record">The document to replay. Never null; may be empty.</param>
    /// <param name="window">How long to keep it. Positive.</param>
    /// <param name="cancellationToken">Cancels the store call.</param>
    /// <returns>
    /// <c>true</c> when the record was stored. <c>false</c> when the claim had already lapsed
    /// and somebody else has the key — a truthful "somebody else owns this outcome now", not an
    /// error: the step still succeeded and its caller still gets its own answer.
    /// </returns>
    ValueTask<Result<bool>> CompleteAsync(
        string key,
        string record,
        TimeSpan window,
        CancellationToken cancellationToken);

    /// <summary>
    /// Releases this caller's claim without recording anything, so the key is free again.
    /// </summary>
    /// <param name="key">The claimed key.</param>
    /// <param name="cancellationToken">Cancels the store call.</param>
    /// <returns>
    /// <c>true</c> when a claim was released. <c>false</c> when there was none left to release,
    /// which a lapsed claim and a double abandon both produce and neither is an error.
    /// </returns>
    /// <remarks>
    /// Never removes a <em>completed</em> record. An abandon is a caller giving up its own
    /// in-flight claim, and a caller that failed cannot un-record the success another caller
    /// already wrote.
    /// </remarks>
    ValueTask<Result<bool>> AbandonAsync(string key, CancellationToken cancellationToken);
}

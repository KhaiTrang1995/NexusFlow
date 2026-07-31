namespace FlowX.Runtime;

/// <summary>
/// What a node holds while it executes one durable instance: the lease it won, a background
/// renewal that keeps it, and the only way to obtain the fencing token every journal write
/// is guarded by.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The fence rises on acquisition, and this type is shaped so that it cannot rise
/// anywhere else.</strong> <see cref="Token"/> is only reachable from a live lease, and the
/// two ways to start executing under it — <see cref="BeginAsync"/> and
/// <see cref="ResumeAsync"/> — are the two that raise the fence before anything is read or
/// written: a fresh instance's opening fence is the token
/// <see cref="IFlowJournal.StartAsync"/> records, and a taken-over instance's is raised by
/// <see cref="IFlowJournal.FenceAsync"/> before its history is read.
/// <c>JournalConformance.TheFenceRisesOnAcquisitionNotOnTheFirstWrite</c> is the assertion
/// that this ordering exists to satisfy: a journal that learned tokens only from writes
/// would accept a zombie's commit in the window between a new owner acquiring and its first
/// commit.
/// </para>
/// <para>
/// <strong>What a renewal guarantees, and what it does not.</strong> Renewal keeps the lease
/// from lapsing while a step runs, so an ordinary long step does not hand the instance to
/// another node. It cannot promise that: a node paused past its own TTL — a stop-the-world
/// pause, a partition, a suspended container — wakes up believing it still holds a lease
/// that has been reissued. What is guaranteed is that its <em>writes</em> stop, at the next
/// step boundary, because the journal's fence has risen above its token. What is not
/// guaranteed is its <em>effects</em>: the step in flight when the lease lapsed runs to
/// completion and whatever it did stands. That is
/// <c>docs/11-Distributed-Runtime.md §4</c>'s honest statement, unchanged by anything here,
/// and it is why <c>Idempotent = false</c> exists.
/// </para>
/// <para>
/// <strong>Nothing on the ephemeral path touches this type.</strong> It is not referenced by
/// <see cref="FlowEngine"/> at all — the engine takes a <see cref="DurableExecution"/> that
/// already carries a token — so budget B2 is unaffected by its existence, which is the same
/// bargain the seam itself struck.
/// </para>
/// </remarks>
public sealed class DurableLease : IAsyncDisposable
{
    private readonly ILeaseStore _store;
    private readonly LeasePolicy _policy;
    private readonly CancellationTokenSource _stopped = new();
    private readonly Lock _gate = new();

    private FlowLease _lease;
    private Task _renewals = Task.CompletedTask;
    private State _state = State.Held;

    private DurableLease(ILeaseStore store, FlowLease lease, LeasePolicy policy)
    {
        _store = store;
        _lease = lease;
        _policy = policy;
    }

    private enum State
    {
        Held,
        Lost,
        Released,
    }

    /// <summary>The instance this lease covers.</summary>
    public Guid InstanceId => _lease.InstanceId;

    /// <summary>Which node holds it.</summary>
    public string OwnerNode => _lease.OwnerNode;

    /// <summary>
    /// The token issued at acquisition, which every write under this lease carries.
    /// </summary>
    /// <remarks>
    /// Fixed for the life of the lease. A renewal extends the expiry and keeps the token —
    /// re-issuing one on renewal would fence out the holder's own in-flight writes.
    /// </remarks>
    public FencingToken Token => _lease.Token;

    /// <summary>When the lease lapses if the renewal loop stops keeping it.</summary>
    public DateTimeOffset ExpiresAt
    {
        get
        {
            lock (_gate)
            {
                return _lease.ExpiresAt;
            }
        }
    }

    /// <summary>Whether this node still believes it owns the instance.</summary>
    public bool IsHeld
    {
        get
        {
            lock (_gate)
            {
                return _state == State.Held;
            }
        }
    }

    /// <summary>
    /// Whether a renewal was refused, or could not be made before the lease would have
    /// lapsed.
    /// </summary>
    /// <remarks>
    /// <strong>Observable, and deliberately not wired to a cancellation token.</strong>
    /// Cancelling the execution would take the flow down the failure path, and the failure
    /// path compensates — which would run this node's undo effects against an instance
    /// another node has already resumed forwards. The fence is the mechanism that stops a
    /// lost lease, and it stops it at the write, which is where stopping is safe.
    /// </remarks>
    public bool IsLost
    {
        get
        {
            lock (_gate)
            {
                return _state == State.Lost;
            }
        }
    }

    /// <summary>
    /// Takes exclusive ownership of an instance and starts renewing it.
    /// </summary>
    /// <param name="store">Where leases are issued.</param>
    /// <param name="instanceId">The instance to take.</param>
    /// <param name="ownerNode">This node's identity, as an operator would read it.</param>
    /// <param name="policy">How long the lease lives and how often it is renewed.</param>
    /// <param name="cancellationToken">Cancels the acquisition, not the lease.</param>
    /// <returns>
    /// The held lease, or the store's refusal unchanged — <see cref="DurabilityErrors.LeaseHeld"/>
    /// when another node owns the instance, which is a normal answer during a recovery scan
    /// and not a failure.
    /// </returns>
    public static async ValueTask<Result<DurableLease>> AcquireAsync(
        ILeaseStore store,
        Guid instanceId,
        string ownerNode,
        LeasePolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerNode);

        var effective = policy ?? LeasePolicy.Default;
        effective.Validate();

        var acquired = await store
            .AcquireAsync(instanceId, ownerNode, effective.Ttl, cancellationToken)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {
            return Result.Fail<DurableLease>(acquired.Error);
        }

        var session = new DurableLease(store, acquired.Value, effective);

        session.StartRenewing();

        return Result.Ok(session);
    }

    /// <summary>
    /// Opens the instance under this lease, so its opening fence is this lease's token.
    /// </summary>
    /// <param name="journal">Where the step boundaries go.</param>
    /// <param name="plan">The flow being started.</param>
    /// <param name="invocation">Correlation, tenant and the caller's remaining budget.</param>
    /// <param name="input">The trigger input, redacted on the way in.</param>
    /// <param name="cancellationToken">Cancels the store call.</param>
    /// <remarks>
    /// A fresh instance has no row to fence, so <see cref="IFlowJournal.StartAsync"/> carries
    /// the token instead: the row is created with this fence already on it, which is the same
    /// property <see cref="IFlowJournal.FenceAsync"/> gives a resumed one.
    /// </remarks>
    public ValueTask<Result<DurableExecution>> BeginAsync(
        IFlowJournal journal,
        ExecutionPlan plan,
        FlowInvocation invocation,
        JournalPayload? input = null,
        CancellationToken cancellationToken = default) =>
        DurableExecution.BeginAsync(
            journal, plan, invocation, InstanceId, Token, input, cancellationToken);

    /// <summary>
    /// Raises the instance's fence to this lease's token and reads the history to resume
    /// from.
    /// </summary>
    /// <param name="journal">The journal holding the instance.</param>
    /// <param name="cancellationToken">Cancels the store calls.</param>
    public ValueTask<Result<DurableExecution>> ResumeAsync(
        IFlowJournal journal,
        CancellationToken cancellationToken = default) =>
        DurableExecution.ResumeAsync(journal, InstanceId, Token, cancellationToken);

    /// <summary>
    /// Stops renewing and gives the lease up, so another node can take the instance without
    /// waiting for expiry.
    /// </summary>
    /// <param name="cancellationToken">Cancels the store call.</param>
    /// <returns>
    /// <c>true</c> when this call released a lease it still held; <c>false</c> when there was
    /// nothing to release, because it had already been released or lost. A store error is
    /// returned rather than thrown.
    /// </returns>
    /// <remarks>
    /// This is what makes a rolling update a non-event: the next node acquires immediately
    /// rather than after a TTL (<c>docs/11-Distributed-Runtime.md §7</c>). Idempotent, so the
    /// ordinary path — release at the end of the flow, dispose in a <c>finally</c> — does not
    /// have to know which of the two got there first.
    /// </remarks>
    public async ValueTask<Result<bool>> ReleaseAsync(CancellationToken cancellationToken = default)
    {
        await StopRenewingAsync().ConfigureAwait(false);

        FlowLease lease;

        lock (_gate)
        {
            if (_state != State.Held)
            {
                return Result.Ok(false);
            }

            _state = State.Released;
            lease = _lease;
        }

        var released = await _store.ReleaseAsync(lease, cancellationToken).ConfigureAwait(false);

        // A lease that was already lost cannot be released, and saying so as an error would
        // make every shutdown after a partition look like a failure. The state is what the
        // caller asked for either way: this node no longer owns the instance.
        return released.IsSuccess || released.Error.Code == DurabilityErrors.LeaseLostCode
            ? Result.Ok(released.IsSuccess)
            : Result.Fail<bool>(released.Error);
    }

    /// <summary>Releases the lease if it is still held, and never throws doing it.</summary>
    /// <remarks>
    /// Disposal runs in a <c>finally</c>, often while another failure is in flight. A store
    /// that has gone away must not replace the error the caller was about to report — and a
    /// lease nobody released simply expires, which is the slower half of the same outcome.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        try
        {
            _ = await ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // See the remarks: disposal must not throw over the top of
        catch (Exception)      //   whatever is already being reported.
        {
            // Left to expire.
        }
#pragma warning restore CA1031
        finally
        {
            _stopped.Dispose();
        }
    }

    private void StartRenewing()
    {
        // Not Task.Run: the loop is a timer and two store calls, so it does not need a
        // thread-pool hop to start, and holding the Task is what lets ReleaseAsync wait for
        // it rather than racing the renewal it is about to invalidate.
        _renewals = RenewAsync();
    }

    private async Task RenewAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(_policy.RenewalInterval);

            while (await timer.WaitForNextTickAsync(_stopped.Token).ConfigureAwait(false))
            {
                if (!await RenewOnceAsync().ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Released, or the process is going away. Neither is a lost lease.
        }
    }

    /// <summary>One renewal. False when the lease is gone and the loop should stop.</summary>
    private async ValueTask<bool> RenewOnceAsync()
    {
        FlowLease current;

        lock (_gate)
        {
            if (_state != State.Held)
            {
                return false;
            }

            current = _lease;
        }

        Result<FlowLease> renewed;

        try
        {
            renewed = await _store
                .RenewAsync(current, _policy.Ttl, _stopped.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
#pragma warning disable CA1031 // A store that throws is unreachable rather than refusing, and
        catch (Exception)      //   the difference decides whether to keep trying: there is
        {                      //   still time on the lease, so a blip is worth another tick.
            if (current.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return true;
            }

            Lose();
            return false;
        }
#pragma warning restore CA1031

        if (renewed.IsFailure)
        {
            // A store that is working and saying no is definitive: the lease expired, or
            // another node has acquired since. Renewal never resurrects a lost lease —
            // that is precisely the split brain the token exists to prevent.
            Lose();
            return false;
        }

        lock (_gate)
        {
            if (_state != State.Held)
            {
                return false;
            }

            _lease = renewed.Value;
        }

        return true;
    }

    private void Lose()
    {
        lock (_gate)
        {
            if (_state == State.Held)
            {
                _state = State.Lost;
            }
        }
    }

    private async ValueTask StopRenewingAsync()
    {
        if (!_stopped.IsCancellationRequested)
        {
            await _stopped.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await _renewals.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The renewal loop's own failures are already recorded in the
        catch (Exception)      //   lease state; re-throwing them out of a release would turn
        {                      //   an orderly shutdown into a fault.
            // Recorded as Lost by the loop itself.
        }
#pragma warning restore CA1031
    }
}

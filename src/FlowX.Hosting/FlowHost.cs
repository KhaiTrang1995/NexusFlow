using FlowX.Runtime;

namespace FlowX.Hosting;

/// <summary>
/// The entry point a trigger uses to run a flow, and the thing that knows how many
/// flows are in flight.
/// </summary>
/// <remarks>
/// <para>
/// A thin wrapper over <see cref="FlowEngine"/> that adds exactly two concerns the
/// engine deliberately does not have: <strong>lifecycle</strong> and, since WP-55,
/// <strong>ownership</strong>. The engine executes a plan; this decides whether the process
/// is still willing to start one, and — for a flow that declared <c>Durable</c> — takes the
/// lease that makes this node the instance's only writer before it does.
/// </para>
/// <para>
/// Keeping both here rather than in the engine matters. The engine is on the hot path and
/// its allocation budget is a hard zero; a lease has a TTL, a renewal timer and a store
/// round trip, and none of those belong inside a step loop an ephemeral flow shares. An
/// ephemeral flow reaches the engine through the same call it always did, past one
/// comparison on the plan's declared profile.
/// </para>
/// </remarks>
public sealed class FlowHost
{
    private readonly FlowEngine _engine;
    private readonly FlowXOptions _options;
    private readonly FlowDurability? _durability;
    private readonly LeasePolicy _policy;
    private readonly object _sync = new();
    private readonly HashSet<DurableLease> _leases = [];

    /// <summary>
    /// Why work is refused during a drain. Accepting work during a drain is why drains
    /// never finish; <see cref="ErrorCategory.Unavailable"/> is what tells a caller — or
    /// a load balancer — that another node can take this.
    /// </summary>
    private static readonly Error Draining = new(
        "host.draining",
        "This node is shutting down and is not accepting new flows.",
        ErrorCategory.Unavailable);

    private TaskCompletionSource? _idle;
    private int _inFlight;
    private volatile bool _draining;
    private volatile bool _ready;

    /// <summary>Creates a host over an engine.</summary>
    /// <param name="engine">The step loop.</param>
    /// <param name="options">The validated host options.</param>
    /// <param name="durability">
    /// The journal and lease store, when this host is wired for durable execution. Null
    /// leaves a <c>Durable</c> flow refused with <c>flow.durability_not_configured</c>, which
    /// is the honest answer for a host that has registered no journal.
    /// </param>
    public FlowHost(FlowEngine engine, FlowXOptions options, FlowDurability? durability = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);

        _engine = engine;
        _options = options;
        _durability = durability;
        _policy = FlowDurability.PolicyFor(options);
    }

    /// <summary>How many flows are executing right now.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>How many durable instances this node currently owns a lease on.</summary>
    public int HeldLeases
    {
        get
        {
            lock (_sync)
            {
                return _leases.Count;
            }
        }
    }

    /// <summary>True once shutdown has begun. New work is refused from this point.</summary>
    public bool IsDraining => _draining;

    /// <summary>True once the host is ready to serve. Drives the readiness probe.</summary>
    public bool IsReady => _ready && !_draining;

    /// <summary>Whether this host can execute a flow that declares <c>Durable</c>.</summary>
    public bool IsDurabilityConfigured => _durability is not null;

    /// <summary>Marks the host ready. Called by the hosted service after registration completes.</summary>
    public void MarkReady() => _ready = true;

    /// <summary>Runs a flow, unless the host is shutting down.</summary>
    public async ValueTask<FlowExecutionResult> RunAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);

        if (!TryEnter())
        {
            return FlowExecutionResult.Rejected(Draining);
        }

        var scope = FlowScope.Start(plan, invocation);

        try
        {
            if (!IsJournaled(plan))
            {
                var ephemeral = await _engine
                    .ExecuteAsync(plan, StepTelemetry.Wrap(plan, invocation, dispatcher), invocation, ct)
                    .ConfigureAwait(false);

                scope.Complete(ephemeral);

                return ephemeral;
            }

            var opened = await OpenAsync(plan, dispatcher, invocation, input: null, suppliedId: null, ct).ConfigureAwait(false);

            if (opened.IsFailure)
            {
                var refused = FlowExecutionResult.Rejected(opened.Error);
                scope.Complete(refused);

                return refused;
            }

            var session = opened.Value;

            try
            {
                var journaled = await _engine
                    .ExecuteAsync(
                        plan,
                        StepTelemetry.Wrap(plan, invocation, dispatcher, session.Run.InstanceId),
                        invocation,
                        session.Run,
                        ct)
                    .ConfigureAwait(false);

                scope.Complete(journaled);

                return journaled;
            }
            finally
            {
                await CloseAsync(session.Lease).ConfigureAwait(false);
            }
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Runs a flow with an input, unless the host is shutting down.</summary>
    public ValueTask<FlowExecutionResult> RunAsync<TIn>(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowInvocation invocation,
        TIn input,
        CancellationToken ct = default)
        where TIn : notnull =>
        RunAsync(plan, dispatcher, invocation, input, instanceId: null, ct);

    /// <summary>
    /// Runs a flow with an input under an instance id the caller derived, so that a second
    /// delivery of the same event is refused rather than executed.
    /// </summary>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Invokes the capability behind each step index.</param>
    /// <param name="invocation">Correlation, tenant and the caller's remaining budget.</param>
    /// <param name="input">The flow's input.</param>
    /// <param name="instanceId">
    /// The id this delivery names. Derived from something the sender and every receiver agree
    /// on — for a schedule, the occurrence
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0026-an-occurrence-names-the-instance-it-starts.md">ADR-0026</a>)
    /// — never minted here.
    /// </param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>
    /// How the flow ended, or the stores' refusal:
    /// <see cref="DurabilityErrors.LeaseHeld"/> while another node is running this same
    /// delivery, and <see cref="DurabilityErrors.InstanceExists"/> once one has.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Not a second way into a flow.</strong> This is
    /// <see cref="RunAsync{TIn}(ExecutionPlan, IStepDispatcher, FlowInvocation, TIn, CancellationToken)"/>
    /// with the one line that mints an id replaced by the caller's, and both reach the same
    /// <c>FlowEngine.ExecuteAsync</c> through the same <c>OpenAsync</c>. <c>OpenAsync</c>'s own
    /// remarks have named this path since WP-55 — <em>"a trigger that wants a redelivery to be
    /// idempotent supplies its own id"</em> — and until now nothing supplied one.
    /// </para>
    /// <para>
    /// <strong>The refusal is the mechanism, so it is not an error.</strong> A caller that
    /// receives <c>journal.instance_exists</c> has learned that this delivery has already been
    /// taken, which is the answer it asked for. Treating it as a failure would make an
    /// at-least-once transport's ordinary case look like an outage.
    /// </para>
    /// <para>
    /// <strong>The flow must be <c>Durable</c>, and this method cannot check that.</strong> An
    /// <c>Ephemeral</c> plan journals nothing, so there is no primary key to refuse the second
    /// delivery and the id is inert — the flow simply runs, once per delivery. Whoever
    /// registers a schedule refuses an ephemeral flow at registration
    /// (<c>FlowScheduleCatalog.Add</c>), and <c>FLOWX1037</c> refuses one at compile time.
    /// </para>
    /// </remarks>
    public ValueTask<FlowExecutionResult> RunAsync<TIn>(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowInvocation invocation,
        TIn input,
        Guid instanceId,
        CancellationToken ct = default)
        where TIn : notnull =>
        RunAsync(plan, dispatcher, invocation, input, (Guid?)instanceId, ct);

    private async ValueTask<FlowExecutionResult> RunAsync<TIn>(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowInvocation invocation,
        TIn input,
        Guid? instanceId,
        CancellationToken ct)
        where TIn : notnull
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);

        if (!TryEnter())
        {
            return FlowExecutionResult.Rejected(Draining);
        }

        var scope = FlowScope.Start(plan, invocation);

        try
        {
            if (!IsJournaled(plan))
            {
                var ephemeral = await _engine
                    .ExecuteAsync(
                        plan, StepTelemetry.Wrap(plan, invocation, dispatcher), invocation, input, ct)
                    .ConfigureAwait(false);

                scope.Complete(ephemeral);

                return ephemeral;
            }

            var opened = await OpenAsync(plan, dispatcher, invocation, input, instanceId, ct)
                .ConfigureAwait(false);

            if (opened.IsFailure)
            {
                var refused = FlowExecutionResult.Rejected(opened.Error);
                scope.Complete(refused);

                return refused;
            }

            var session = opened.Value;

            try
            {
                var journaled = await _engine
                    .ExecuteAsync(
                        plan,
                        StepTelemetry.Wrap(plan, invocation, dispatcher, session.Run.InstanceId),
                        invocation,
                        input,
                        session.Run,
                        ct)
                    .ConfigureAwait(false);

                scope.Complete(journaled);

                return journaled;
            }
            finally
            {
                await CloseAsync(session.Lease).ConfigureAwait(false);
            }
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Runs a flow and projects its declared output, unless the host is shutting down.</summary>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Invokes the capability behind each step index.</param>
    /// <param name="invocation">Correlation, tenant and the caller's remaining budget.</param>
    /// <param name="input">The flow's input.</param>
    /// <param name="projection">The generated <c>.Return(...)</c> clause.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    public async ValueTask<FlowExecutionResult<TOut>> RunAsync<TIn, TOut>(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowInvocation invocation,
        TIn input,
        Func<FlowContext, TOut> projection,
        CancellationToken ct = default)
        where TIn : notnull
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);

        if (!TryEnter())
        {
            return FlowExecutionResult.Rejected<TOut>(Draining);
        }

        var scope = FlowScope.Start(plan, invocation);

        try
        {
            if (!IsJournaled(plan))
            {
                var ephemeral = await _engine
                    .ExecuteAsync(
                        plan,
                        StepTelemetry.Wrap(plan, invocation, dispatcher),
                        invocation,
                        input,
                        projection,
                        ct)
                    .ConfigureAwait(false);

                scope.Complete(ephemeral.Outcome);

                return ephemeral;
            }

            var opened = await OpenAsync(plan, dispatcher, invocation, input, suppliedId: null, ct)
                .ConfigureAwait(false);

            if (opened.IsFailure)
            {
                var refused = FlowExecutionResult.Rejected<TOut>(opened.Error);
                scope.Complete(refused.Outcome);

                return refused;
            }

            var session = opened.Value;

            try
            {
                var journaled = await _engine
                    .ExecuteAsync(
                        plan,
                        StepTelemetry.Wrap(plan, invocation, dispatcher, session.Run.InstanceId),
                        invocation,
                        input,
                        projection,
                        session.Run,
                        ct)
                    .ConfigureAwait(false);

                scope.Complete(journaled.Outcome);

                return journaled;
            }
            finally
            {
                await CloseAsync(session.Lease).ConfigureAwait(false);
            }
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Takes over an instance another node left running and finishes it, through the same
    /// step loop that started it.
    /// </summary>
    /// <param name="instanceId">The instance to take over.</param>
    /// <param name="registration">The plan the instance is pinned to, and its dispatcher.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>
    /// How the resumed instance ended, or a rejection: <c>host.draining</c> when this node is
    /// shutting down, <c>lease.held</c> when another node got there first — which during a
    /// recovery scan is the ordinary answer and not a failure — or the journal's refusal.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>There is no recovery path here, and that is the design.</strong> The lease is
    /// acquired, its token raises the instance's fence, the frontier is read, and what comes
    /// back is handed to the same <c>ExecuteAsync</c> a fresh instance uses. Compensation
    /// ordering, deadline handling and <c>ForEach</c> scoping cannot drift between a first
    /// run and a resumed one because there is only one of each (ADR-0015).
    /// </para>
    /// <para>
    /// The invocation is rebuilt from the instance row rather than invented: the correlation
    /// id ties the resumed steps to the request that started the flow, and the deadline is
    /// the one that trigger bought. An instance whose deadline has passed resumes, finds it
    /// expired at its first step and times out — which is the correct end for it, and a
    /// quieter one than never being picked up.
    /// </para>
    /// </remarks>
    public ValueTask<FlowExecutionResult> ResumeAsync(
        Guid instanceId,
        FlowRegistration registration,
        CancellationToken ct = default) =>
        ResumeAsync(instanceId, registration, signal: null, ct);

    /// <summary>
    /// Delivers a signal to an instance that is waiting for one, and runs it on from there.
    /// </summary>
    /// <param name="instanceId">The waiting instance.</param>
    /// <param name="registration">The plan the instance is pinned to, and its dispatcher.</param>
    /// <param name="signal">The signal to deliver.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>
    /// How the instance ended — which may be <c>IsSuspended</c> again, if the flow has a
    /// second wait after this one — or a rejection, in the same set
    /// <see cref="ResumeAsync(Guid, FlowRegistration, CancellationToken)"/> returns.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>This is <see cref="ResumeAsync(Guid, FlowRegistration, CancellationToken)"/>
    /// with a signal attached, and deliberately nothing more.</strong> A signal is not a
    /// different way of running an instance: the lease is acquired, its token raises the
    /// fence, the frontier is read, and the same <c>FlowEngine.ExecuteAsync</c> a recovery
    /// scan calls is called. The step loop steps over every <c>(scope, step)</c> that
    /// committed and stops at the first that has not, which is the wait — and the wait is
    /// satisfied because this invocation is carrying what it asked for.
    /// </para>
    /// <para>
    /// <strong>A signal for an instance that is not waiting for it is inert, not an
    /// error.</strong> The instance runs forward to wherever it actually is and stops there,
    /// leaving <c>Suspended</c> untouched. Refusing would mean the host deciding what a flow
    /// is waiting for, and the journal already answers that: the open wait is the first
    /// <c>AwaitSignal</c> with no committed row.
    /// </para>
    /// <para>
    /// A signal delivered twice re-enters an instance whose wait now <em>has</em> a committed
    /// row, so the second delivery steps over it and changes nothing. That is the same
    /// idempotence the frontier gives every other step, rather than a check written for
    /// signals.
    /// </para>
    /// </remarks>
    public ValueTask<FlowExecutionResult> SignalAsync(
        Guid instanceId,
        FlowRegistration registration,
        FlowSignal signal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        return ResumeAsync(instanceId, registration, signal, ct);
    }

    /// <inheritdoc cref="ResumeAsync(Guid, FlowRegistration, CancellationToken)" />
    private async ValueTask<FlowExecutionResult> ResumeAsync(
        Guid instanceId,
        FlowRegistration registration,
        FlowSignal? signal,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registration);

        if (_durability is null)
        {
            return FlowExecutionResult.Rejected(
                FlowErrors.DurabilityNotConfigured(registration.Plan.Flow.Id));
        }

        if (!TryEnter())
        {
            return FlowExecutionResult.Rejected(Draining);
        }

        try
        {
            var acquired = await DurableLease
                .AcquireAsync(_durability.Leases, instanceId, _options.NodeName, _policy, ct)
                .ConfigureAwait(false);

            if (acquired.IsFailure)
            {
                return FlowExecutionResult.Rejected(acquired.Error);
            }

            var lease = acquired.Value;

            Track(lease);

            try
            {
                var resumed = await lease.ResumeAsync(_durability.Journal, ct).ConfigureAwait(false);

                if (resumed.IsFailure)
                {
                    return FlowExecutionResult.Rejected(resumed.Error);
                }

                var run = resumed.Value;
                var record = run.Frontier!.Instance;

                if (signal is not null)
                {
                    run.WithSignal(signal);
                }

                var invocation = new FlowInvocation(
                    record.CorrelationId,
                    instanceId.ToString(),
                    record.TenantId,
                    record.DeadlineAt);

                // Opened after the lease and the frontier read, unlike a fresh execution's:
                // an instance another node got to first is not a flow this node ran, and a
                // span for it would put a duration on work that never started here.
                var scope = FlowScope.Start(registration.Plan, invocation);

                var resumedOutcome = await _engine
                    .ExecuteAsync(
                        registration.Plan,
                        StepTelemetry.Wrap(
                            registration.Plan, invocation, registration.Dispatcher, instanceId),
                        invocation,
                        run,
                        ct)
                    .ConfigureAwait(false);

                scope.Complete(resumedOutcome);

                return resumedOutcome;
            }
            finally
            {
                await CloseAsync(lease).ConfigureAwait(false);
            }
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Stops accepting work and waits for in-flight flows, up to
    /// <see cref="FlowXOptions.ShutdownDrainTimeout"/>.
    /// </summary>
    /// <returns>
    /// <c>true</c> when everything finished in time; <c>false</c> when the budget ran
    /// out with work still running.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Bounded on purpose. A drain that waits forever turns a rolling deploy into an
    /// outage: the orchestrator sends SIGKILL on its own schedule regardless, so the
    /// choice is between giving up loudly and being killed silently.
    /// </para>
    /// <para>
    /// <strong>Detached sub-flows are drained too, and they are not in this counter.</strong>
    /// A <c>.SubFlow&lt;T&gt;(Detached)</c> child is started from inside a step, so it never
    /// passed through <see cref="TryEnter"/> and the host does not know it exists. Waiting
    /// only on <see cref="InFlight"/> would report "everything finished" while a
    /// fire-and-forget saga was mid-way through its effects — and the kill that follows a
    /// successful drain would leave them uncompensated. The engine counts them; this waits
    /// for both.
    /// </para>
    /// <para>
    /// <strong>Held leases are released last, and only the ones nothing gave back.</strong>
    /// A flow that finished inside the budget released its own; what is left belongs to one
    /// the budget ran out on, and leaving those to expire would make the next node wait a
    /// full TTL for work this one has abandoned — the delay
    /// <c>docs/11-Distributed-Runtime.md §7</c> exists to remove. Releasing while an
    /// execution is still running is safe in the sense that matters: the next owner fences
    /// the journal, so this node's remaining commits are refused rather than accepted, and a
    /// refusal that says "you are not the writer" no longer compensates. It is not safe in
    /// the other sense — a step already in flight completes and its effects stand — which is
    /// the same trade the TTL makes, taken sooner and deliberately.
    /// </para>
    /// </remarks>
    public async ValueTask<bool> DrainAsync(CancellationToken ct = default)
    {
        Task idle;

        lock (_sync)
        {
            _draining = true;

            if (_inFlight == 0)
            {
                idle = Task.CompletedTask;
            }
            else
            {
                _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                idle = _idle.Task;
            }
        }

        var drained = true;

        if (!idle.IsCompleted)
        {
            try
            {
                await idle.WaitAsync(_options.ShutdownDrainTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                drained = false;
            }
            catch (OperationCanceledException)
            {
                drained = false;
            }
        }

        // After the tracked flows, not before: a flow still running may yet start a
        // detached child, so waiting on the children first would leave a window where one
        // appears after the wait returned.
        var detached = await _engine
            .WaitForDetachedAsync(_options.ShutdownDrainTimeout, ct)
            .ConfigureAwait(false);

        await ReleaseRemainingLeasesAsync().ConfigureAwait(false);

        return drained && detached;
    }

    /// <summary>Whether this execution journals: the flow asked for it and the host can.</summary>
    /// <remarks>
    /// One comparison on the ephemeral path, against a field the plan already holds — the
    /// same bargain <c>ExecutionPlan.HasParallel</c> struck for the fork lock. A
    /// <c>Durable</c> flow on a host with no journal falls through to the engine, which
    /// refuses it with <c>flow.durability_not_configured</c>: the refusal belongs where the
    /// profile is read, and not in two places that can disagree about it.
    /// </remarks>
    private bool IsJournaled(ExecutionPlan plan) =>
        _durability is not null && plan.Flow.Profile == ExecutionProfile.Durable;

    /// <summary>
    /// Wins the instance and opens it: acquire, then start, in that order and no other.
    /// </summary>
    /// <remarks>
    /// An id the caller supplied is used unchanged; otherwise one is minted here, and it is
    /// version 7, so a journal's primary key is time-ordered rather than scattered across its
    /// index. A trigger that wants a redelivery to be idempotent supplies its own — a schedule
    /// derives one from the occurrence
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0026-an-occurrence-names-the-instance-it-starts.md">ADR-0026</a>)
    /// — and minting one per invocation is the honest behaviour for a caller with none to
    /// offer.
    /// <para>
    /// The dispatcher is taken as a parameter for one reason: it is the only code that can
    /// name a <c>JsonTypeInfo</c> for the flow's input contract, and therefore the only code
    /// that can put the request into the instance row.
    /// </para>
    /// </remarks>
    private async ValueTask<Result<Session>> OpenAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowInvocation invocation,
        object? input,
        Guid? suppliedId,
        CancellationToken ct)
    {
        var durability = _durability!;
        var instanceId = suppliedId ?? Guid.CreateVersion7();

        var acquired = await DurableLease
            .AcquireAsync(durability.Leases, instanceId, _options.NodeName, _policy, ct)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {
            return Result.Fail<Session>(acquired.Error);
        }

        var lease = acquired.Value;

        Track(lease);

        // The token the lease just issued becomes the instance's opening fence, because
        // StartAsync carries it. There is no window in which the row exists at a fence lower
        // than the lease that created it.
        //
        // The input is asked of the dispatcher rather than serialised here, and until WP-59
        // this line passed the literal `input: null`. That made flow_instance.input NULL on
        // every row ever written — a replay could not reconstruct what was requested, the
        // audit trail had no record of it, and [Sensitive] on an input contract protected
        // nothing because nothing was stored. The host cannot fix that itself: recording an
        // input needs a JsonTypeInfo<TIn> and only generated code can name one. What comes
        // back is a JournalPayload carrying the flow's SensitiveMembers, so the stored input
        // is redacted by the same single exit as every other payload.
        var begun = await lease
            .BeginAsync(durability.Journal, plan, invocation, dispatcher.DescribeInput(input), ct)
            .ConfigureAwait(false);

        if (begun.IsFailure)
        {
            await CloseAsync(lease).ConfigureAwait(false);

            return Result.Fail<Session>(begun.Error);
        }

        return Result.Ok(new Session(lease, begun.Value));
    }

    private void Track(DurableLease lease)
    {
        lock (_sync)
        {
            _leases.Add(lease);
        }
    }

    private async ValueTask CloseAsync(DurableLease lease)
    {
        lock (_sync)
        {
            _leases.Remove(lease);
        }

        await lease.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask ReleaseRemainingLeasesAsync()
    {
        DurableLease[] remaining;

        lock (_sync)
        {
            if (_leases.Count == 0)
            {
                return;
            }

            remaining = [.. _leases];
            _leases.Clear();
        }

        foreach (var lease in remaining)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private bool TryEnter()
    {
        lock (_sync)
        {
            if (_draining)
            {
                return false;
            }

            _inFlight++;
            return true;
        }
    }

    private void Exit()
    {
        lock (_sync)
        {
            _inFlight--;

            if (_inFlight == 0)
            {
                _idle?.TrySetResult();
            }
        }
    }

    /// <summary>One durable execution as this host holds it: the lease, and what it opened.</summary>
    private readonly record struct Session(DurableLease Lease, DurableExecution Run);
}

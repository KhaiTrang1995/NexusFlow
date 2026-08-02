using FlowX.Runtime;
using System.Security.Claims;

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
    private readonly ITenantResolver _tenants;
    private readonly TenantAdmissionControl? _fairness;
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
    /// <param name="tenants">
    /// How a tenant is decided for each invocation. Defaults to
    /// <see cref="ClaimTenantResolver"/> over the level <paramref name="options"/> declares,
    /// which for the default <see cref="TenantIsolation.None"/> resolves nothing and refuses
    /// nobody.
    /// </param>
    /// <param name="limiter">
    /// Where a per-tenant rate limit and quota are spent. Consulted only where
    /// <see cref="FlowXOptions.Fairness"/> declares one; the startup validator refuses a
    /// deployment that declares a budget and registers no store, so a null here on a bounded
    /// host is a host built by hand and is refused per call rather than admitted unbounded.
    /// </param>
    public FlowHost(
        FlowEngine engine,
        FlowXOptions options,
        FlowDurability? durability = null,
        ITenantResolver? tenants = null,
        IRateLimiterStore? limiter = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);

        // Refused where the host is built rather than where the first tenanted call arrives.
        // The options validator cannot see the store and the store cannot see the options, so
        // this is the one place that holds both — and it holds them before anything has been
        // journaled under a level the store was never going to serve.
        //
        // Only above Row, deliberately. Row over a store that cannot scope is the trade ADR-0046
        // §3 recorded and accepted: admission still refuses, and FlowDurability.CanIsolateTenants
        // is what reports the missing second wall. Schema over a row store has no equivalent
        // reading — the rows would all be in one schema — so it is refused rather than reported.
        if (options.TenantIsolation > TenantIsolation.Row
            && durability is not null
            && durability.IsolationEnforced < options.TenantIsolation)
        {
            throw new InvalidOperationException(
                TenantErrors
                    .IsolationNotEnforceable(options.TenantIsolation, durability.IsolationEnforced)
                    .Message);
        }

        _engine = engine;
        _options = options;
        _durability = durability;
        _policy = FlowDurability.PolicyFor(options);
        _tenants = tenants ?? new ClaimTenantResolver(options.TenantIsolation);

        // Null on a single-tenant deployment and on one that bounds nothing, so admission's
        // whole fairness path is a null check rather than a branch on four settings. B2 is
        // untouched structurally: an Ephemeral flow on a host with no isolation reaches the
        // engine past exactly the comparisons it always did.
        _fairness = options.TenantIsolation != TenantIsolation.None && options.Fairness.BoundsAdmission
            ? new TenantAdmissionControl(options.Fairness, limiter)
            : null;
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

        var admitted = await AdmitAsync(invocation, ct).ConfigureAwait(false);

        if (admitted.Refusal is { } refusal)
        {
            return FlowExecutionResult.Rejected(refusal);
        }

        invocation = admitted.Invocation;

        if (!TryEnter())
        {
            admitted.Permit.Release();

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
            admitted.Permit.Release();
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
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md">ADR-0026</a>)
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
    /// (<c>FlowScheduleCatalog.Add</c>), and <c>FLOWX1038</c> refuses one at compile time.
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

        var admitted = await AdmitAsync(invocation, ct).ConfigureAwait(false);

        if (admitted.Refusal is { } refusal)
        {
            return FlowExecutionResult.Rejected(refusal);
        }

        invocation = admitted.Invocation;

        if (!TryEnter())
        {
            admitted.Permit.Release();

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
            admitted.Permit.Release();
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

        var admitted = await AdmitAsync(invocation, ct).ConfigureAwait(false);

        if (admitted.Refusal is { } refusal)
        {
            return FlowExecutionResult.Rejected<TOut>(refusal);
        }

        invocation = admitted.Invocation;

        if (!TryEnter())
        {
            admitted.Permit.Release();

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
            admitted.Permit.Release();
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
    /// <param name="tenantId">
    /// The tenant the instance belongs to, as the scan that found it read from the candidate
    /// row, or <c>null</c> for a deployment that does not isolate.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>The tenant is supplied rather than discovered, and it has to be.</strong> The
    /// journal is bound to a tenant <em>before</em> the instance row is read — that is what
    /// makes another tenant's instance invisible rather than merely refused — so reading the
    /// row to find out which tenant to bind to would be the cross-tenant read this is
    /// preventing, performed in order to prevent it. <c>AbandonedInstance.TenantId</c> and
    /// <c>DueInstance.TenantId</c> have carried the value for exactly this since they were
    /// written; until now nothing passed it on.
    /// </para>
    /// <para>
    /// A scan is node-wide platform work and is not a tenant, so it legitimately resumes every
    /// tenant's instances — each one scoped to its own. A scan that must not see a tenant at
    /// all is narrowed at the store instead, through
    /// <see cref="AbandonedInstanceQuery.TenantId"/>.
    /// </para>
    /// </remarks>
    public ValueTask<FlowExecutionResult> ResumeAsync(
        Guid instanceId,
        FlowRegistration registration,
        string? tenantId = null,
        CancellationToken ct = default) =>
        ResumeAsync(instanceId, registration, signal: null, principal: null, tenantId, ct);

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
    /// <see cref="ResumeAsync(Guid, FlowRegistration, string, CancellationToken)"/> returns.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>This is <see cref="ResumeAsync(Guid, FlowRegistration, string, CancellationToken)"/>
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
    /// <param name="principal">
    /// Who is delivering the signal, resolved from validated claims by whatever transport
    /// carried it, or <c>null</c> for an anonymous delivery.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>The deliverer authorises the steps after the wait, and the original caller
    /// does not.</strong> A resumed instance's invocation is rebuilt from its journal row,
    /// and that row carries a correlation id, a tenant and a deadline — no claims. That is
    /// deliberate and is argued in
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0028-identity-arrives-on-the-invocation.md">ADR-0028</a>:
    /// persisting a principal would put claims at rest for the life of the instance, and
    /// would then authorise a payment on Friday with a grant proved on Monday, which the
    /// grant's issuer has had four days to revoke.
    /// </para>
    /// <para>
    /// So the caller who delivers the countersignature is the caller the remaining steps are
    /// decided against. Leaving this <c>null</c> resumes anonymously, and a step declaring
    /// <see cref="Authorization.Authenticated"/> or <see cref="Authorization.Permission"/>
    /// after the wait is then refused — loudly, as a <see cref="ErrorCategory.Forbidden"/>
    /// failure, rather than run unauthorised.
    /// </para>
    /// </remarks>
    public ValueTask<FlowExecutionResult> SignalAsync(
        Guid instanceId,
        FlowRegistration registration,
        FlowSignal signal,
        ClaimsPrincipal? principal = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        return ResumeAsync(instanceId, registration, signal, principal, tenantId: null, ct);
    }

    /// <inheritdoc cref="ResumeAsync(Guid, FlowRegistration, string, CancellationToken)" />
    private async ValueTask<FlowExecutionResult> ResumeAsync(
        Guid instanceId,
        FlowRegistration registration,
        FlowSignal? signal,
        ClaimsPrincipal? principal,
        string? tenantId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registration);

        if (_durability is null)
        {
            return FlowExecutionResult.Rejected(
                FlowErrors.DurabilityNotConfigured(registration.Plan.Flow.Id));
        }

        // Admitted before the lease is taken, on the same terms a fresh execution is. A signal
        // carries a deliverer and is decided against that deliverer's claims; a sweep carries
        // nobody and continues under the tenant its candidate row named. What this refuses is
        // the case in between — somebody authenticated delivering a signal to an instance in a
        // tenant their claims do not place them in.
        var admission = new FlowInvocation(
            instanceId.ToString(),
            instanceId.ToString(),
            tenantId,
            Principal: principal,
            IsContinuation: signal is null && principal is null);

        var accepted = await AdmitAsync(admission, ct).ConfigureAwait(false);

        if (accepted.Refusal is { } refusal)
        {
            return FlowExecutionResult.Rejected(refusal);
        }

        admission = accepted.Invocation;

        if (!TryEnter())
        {
            accepted.Permit.Release();

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
                // Bound before the row is read, so an instance belonging to another tenant is
                // not there to be resumed rather than being read and then refused. The journal
                // answers journal.instance_not_found, which is the truthful answer under the
                // policy and discloses nothing about whether the id exists elsewhere.
                var journal = JournalFor(admission.TenantId);
                var resumed = await lease.ResumeAsync(journal, ct).ConfigureAwait(false);

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
                    record.DeadlineAt,

                    // Whoever is resuming it, and never whoever started it. The row carries no
                    // claims by design (ADR-0028).
                    principal,

                    // And when nobody is resuming it — a timer sweep, a recovery scan — this is
                    // the platform continuing an instance it already admitted rather than a
                    // caller asking for something, so the stances of the steps after the wait
                    // are not re-decided against an absence that will never be filled.
                    IsContinuation: signal is null && principal is null);

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
            accepted.Permit.Release();
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

    /// <summary>
    /// Decides the invocation's tenant, or refuses it before anything is allocated.
    /// </summary>
    /// <param name="invocation">
    /// What the trigger produced. Replaced on success by one carrying the <em>resolved</em>
    /// tenant rather than the asserted one, so that everything downstream — the instance row,
    /// the scoped connection, the span's tenant tag — reads the value the claims supported and
    /// not the value the caller sent.
    /// </param>
    /// <param name="ct">Cancels the store calls a per-tenant budget makes.</param>
    /// <returns>The refusal, or <c>null</c> when the call is admitted.</returns>
    /// <remarks>
    /// <para>
    /// <strong>At admission, before the lease and before the journal row.</strong>
    /// <c>docs/16 §3</c> requires it — "<em>unresolvable tenant → rejected at admission, before
    /// a flow instance exists</em>" — and the requirement is not stylistic: a refusal after the
    /// lease has been taken leaves an instance id fenced under a tenant that was never
    /// admitted, and one after <c>StartAsync</c> leaves a row.
    /// </para>
    /// <para>
    /// <strong>Not in the step loop, and this is the one place this feature parts company with
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0027-authorisation-runs-in-the-step-loop.md">ADR-0027</a>.</strong>
    /// That record put authorisation in the step loop because a stance belongs to a
    /// <em>capability</em> and a flow's steps are chosen at run time, so no earlier point knows
    /// which stances apply. A tenant belongs to the invocation as a whole: every step of the
    /// flow has the same one, no branch can change it, and there is no union to over-refuse or
    /// intersection to under-refuse. ADR-0027 §1.1's objection therefore does not transfer, and
    /// ADR-0027's own "revisit when" anticipated this case by name. See ADR-0043.
    /// </para>
    /// </remarks>
    private async ValueTask<Admission> AdmitAsync(FlowInvocation invocation, CancellationToken ct)
    {
        if (Resolve(ref invocation) is { } refusal)
        {
            return new Admission(refusal, invocation, default);
        }

        // One boolean on a deployment that isolates and bounds nothing, and not even that on one
        // that does not isolate: the field is null and this is a null check. docs/16 §4 requires
        // the bounds at stage 1 — before authentication, before any allocation, before any
        // journal write — and this is the only point every activation passes through.
        // A continuation is exempt, and it is not a bypass — the same distinction, for the same
        // reason, that ClaimTenantResolver already draws. A sweep resuming an instance is
        // finishing work this platform admitted and spent budget on once; refusing it now is not
        // backpressure, it is abandoning a saga halfway with its compensations unrun. What
        // bounds a continuation is the sweep that issues it: a bounded page, a bounded number of
        // slots, and TenantFairShare deciding whose work fills them.
        if (_fairness is null
            || invocation.IsContinuation
            || invocation.TenantId is not { Length: > 0 } tenant)
        {
            return new Admission(null, invocation, default);
        }

        var acquired = await _fairness.AcquireAsync(tenant, ct).ConfigureAwait(false);

        return acquired.IsFailure
            ? new Admission(acquired.Error, invocation, default)
            : new Admission(null, invocation, acquired.Value);
    }

    /// <summary>What admission decided: a refusal, the resolved invocation, and the permit.</summary>
    /// <remarks>
    /// A struct rather than the <c>ref</c> parameter this used to take, because the bounds are
    /// spent against a shared store and a store call is asynchronous. The permit travels with the
    /// answer so that no call site can spend a bulkhead slot and forget which one to release.
    /// </remarks>
    private readonly record struct Admission(
        Error? Refusal,
        FlowInvocation Invocation,
        TenantPermit Permit);

    /// <inheritdoc cref="AdmitAsync" />
    private Error? Resolve(ref FlowInvocation invocation)
    {
        var resolved = _tenants.Resolve(in invocation);

        if (resolved.Refused)
        {
            // Which of the two refusals it is follows from the one fact the host holds: a call
            // that named a tenant and was refused named one its claims do not support, and a
            // call that named none was missing the claim entirely. The two lead to different
            // repairs — "ask for a token in the right tenant" against "ask for a token with a
            // tenant claim" — so they are different codes rather than one with a message.
            return (invocation.TenantId is { Length: > 0 } asserted
                    ? TenantErrors.CrossTenantDenied(asserted)
                    : TenantErrors.TenantRequired(_options.TenantIsolation))
                .With("reason", resolved.Reason);
        }

        if (resolved.TenantId is { Length: > 0 } tenant
            && !string.Equals(invocation.TenantId, tenant, StringComparison.Ordinal))
        {
            invocation = invocation with { TenantId = tenant };
        }

        return null;
    }

    /// <summary>
    /// The journal this execution commits to: scoped to its tenant, but only where the
    /// deployment declared that it isolates.
    /// </summary>
    /// <remarks>
    /// <strong>The level is the switch, and the tenant is not.</strong> Gating on
    /// <c>tenantId is not null</c> alone would be wrong in a way that is easy to miss:
    /// <c>HttpTriggerReader</c> populates <see cref="FlowInvocation.TenantId"/> from claims on
    /// every deployment, isolating or not, and a journal's rows may carry a tenant that
    /// predates any of this. A single-tenant host would then start binding connections and
    /// assuming a restricted role because its tokens happen to have a <c>tid</c> claim — a
    /// round trip per call, and a set of policies applied to a deployment that never asked for
    /// them. <see cref="TenantIsolation.None"/> takes the path it always took.
    /// </remarks>
    private IFlowJournal JournalFor(string? tenantId) =>
        _options.TenantIsolation == TenantIsolation.None
            ? _durability!.Journal
            : _durability!.JournalFor(tenantId);

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
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md">ADR-0026</a>)
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
        // Scoped to the tenant admission resolved, so every statement this instance's execution
        // ever issues — the opening row, each step commit, the outbox rows, the completion —
        // travels a connection the database has already restricted. The tenant is not passed to
        // the journal's methods and cannot be got wrong at a call site, because no call site
        // sees it.
        var begun = await lease
            .BeginAsync(
                JournalFor(invocation.TenantId),
                plan,
                invocation,
                dispatcher.DescribeInput(input),
                ct)
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

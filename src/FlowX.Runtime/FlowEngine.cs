using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FlowX.Observability;

namespace FlowX.Runtime;

/// <summary>
/// Executes a compiled <see cref="ExecutionPlan"/>: walks its steps, honours the
/// deadline, and unwinds compensation when something fails.
/// </summary>
/// <remarks>
/// <para>
/// The engine owns <em>control flow</em> and nothing else. It does not know what a
/// capability is, what types a step consumes, or how a step was triggered — those
/// belong to the dispatcher, to the generator and to the transport plugin
/// respectively. That separation is what lets the same flow run behind HTTP, Kafka
/// or cron without changing (quality goal Q4), and what keeps this class small
/// enough to reason about.
/// </para>
/// <para>
/// <strong>Branching is flat.</strong> A <c>When</c>/<c>Otherwise</c> is compiled into
/// the same step array as everything else, as a <see cref="StepKind.Branch"/> carrying a
/// false target and a <see cref="StepKind.Jump"/> closing the <c>then</c> block. A
/// <c>Switch</c> is the same shape with more destinations: one
/// <see cref="StepKind.Switch"/> carrying a target per case, and a jump closing each case
/// block. So the engine does not recurse, holds no branch stack, and allocates nothing to
/// take a branch or a case — the only difference from a linear flow is that the loop
/// index sometimes moves by more than one. A tree of nested plan objects would have read
/// more naturally and would have cost an enumerator per level on the hot path.
/// </para>
/// <para>
/// <strong>Parallel is where that stops being the whole story, and it is worth being exact
/// about how much stops.</strong> A fork uses the identical flat layout — one node
/// carrying a target per branch, each branch a contiguous span closed by a jump to the
/// join — so the plan, the graph validation, the termination proof and the manifest's
/// <c>branches</c> array are all unchanged. What changes is that the engine runs
/// <em>every</em> span instead of choosing one, concurrently, which means that between a
/// fork and its join there is no single loop index describing the flow. The engine
/// therefore recurses exactly once per fork, into <c>RunRangeAsync</c> over a sub-range;
/// it still holds no branch stack, still builds no plan objects, and a flow that does not
/// fork never reaches that code at all. A fork allocates — a linked token source, a task
/// per branch, their awaiters — and that is the one documented exception to budget B2,
/// measured rather than waved at.
/// </para>
/// <para>
/// <strong>Branches share one pooled context.</strong> That is what the disjoint-slot rule
/// (FLOWX1013) is for. The runtime's half of the bargain is that the state bag and the
/// compensation stack are serialised while a fork is in flight, so a race is a wrong value
/// and never a corrupted dictionary; the compiler's half is that two branches must not
/// write the same slot in the first place.
/// </para>
/// <para>
/// <strong>Deadlines are enforced at step boundaries; a <c>Timeout</c> is what
/// interrupts a step already running.</strong> The engine will not start a step whose
/// flow has run out of budget, and it does not interrupt a long step that declared
/// nothing — the timer allocation that would take is paid only by the steps whose
/// author asked for it. A step carrying a stage-4 <c>Timeout</c> gets a linked
/// <see cref="CancellationTokenSource"/> for the shorter of its declared duration and
/// what is left of the deadline; every other step reaches the dispatcher with the
/// caller's own token, exactly as before.
/// </para>
/// <para>
/// <strong>A sub-flow is where "one array is one execution" stops being true, and it is
/// worth saying exactly what breaks.</strong> The flat array survives intact: a
/// <see cref="StepKind.SubFlow"/> occupies one index, carries no target, and moves nothing
/// around it — the graph validation, the termination proof for <em>this</em> graph and the
/// manifest are all unchanged. What no longer holds is that the array in front of the loop
/// contains every step that runs. The child has its own <see cref="ExecutionPlan"/>, its own
/// dispatcher, its own context and its own compensation stack, and this engine recurses into
/// a second, independent execution to run it. The alternative — splicing the child's steps
/// into the parent's array at compile time — was rejected because it would make the parent's
/// manifest claim the child's capabilities as its own, discard the child's deadline and
/// profile, and be impossible the moment the child lives in another assembly.
/// </para>
/// </remarks>
public sealed class FlowEngine
{
    private const int DefaultMaxPooledContexts = 128;

    /// <summary>
    /// How many sub-flow boundaries deep the runtime will follow a composition.
    /// </summary>
    /// <remarks>
    /// The run-time half of the DAG guarantee. <c>FLOWX1021</c> refuses a cycle at build
    /// time and does so soundly for every edge it can see, but it can only see the flows
    /// whose <c>Define</c> bodies are in the compilation — a cycle closed through a
    /// referenced assembly is invisible to it. Without a cap that is unbounded recursion:
    /// a stack overflow, which takes the process down rather than failing one flow. Thirty-two
    /// is far past anything a person composes on purpose and far short of the stack.
    /// </remarks>
    public const int MaxSubFlowDepth = 32;

    private readonly IClock _clock;
    private readonly ContextPool _contexts;
    private readonly ICompensationAlertSink? _alerts;
    private readonly IRateLimiterStore? _rateLimiter;
    private readonly IIdempotencyStore? _idempotency;
    private readonly IResultCache? _cache;
    private readonly IAuditSink? _audit;
    private readonly object _detachedSync = new();

    /// <summary>
    /// The breakers and bulkheads this engine's policed steps share, keyed by capability id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On the engine rather than on the plan, because both are <em>state</em> and a plan is a
    /// compiled artifact that several executions read at once. A breaker that lived on the
    /// plan would also be per flow rather than per dependency, so two flows calling one failing
    /// payment gateway would each have to discover the outage — which is the opposite of what
    /// a breaker keyed by capability is for.
    /// </para>
    /// <para>
    /// Two dictionaries per engine, never per execution, so budget B2 is untouched: a host
    /// holds one engine, and a flow that declares no stage-4 policy never looks in either.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<string, CircuitBreakerState> _breakers =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, BulkheadGate> _bulkheads =
        new(StringComparer.Ordinal);

    private readonly bool _breakersPerTenant;

    private TaskCompletionSource? _detachedIdle;
    private int _detachedInFlight;

    /// <summary>Creates an engine.</summary>
    /// <param name="clock">
    /// The time source. Injected so deadline behaviour is testable without sleeping,
    /// and so a durable replay can drive journaled time through the same path.
    /// </param>
    /// <param name="maxPooledContexts">How many contexts to retain between executions.</param>
    /// <param name="alerts">
    /// Where a compensation that has exhausted its policy is reported, or <c>null</c> to
    /// report it nowhere. Optional because the one terminal state it describes is also
    /// recorded on the instance row, so a deployment with no alerting is degraded rather than
    /// blind.
    /// </param>
    /// <param name="rateLimiter">
    /// Where a declared <c>RateLimit</c>'s budget lives, or <c>null</c> when none was registered.
    /// </param>
    /// <param name="idempotency">
    /// Where a declared <c>Idempotency</c> window's records live, or <c>null</c> when none was
    /// registered.
    /// </param>
    /// <param name="cache">
    /// The store behind a declared <c>Cache</c>, or <c>null</c> for none.
    /// </param>
    /// <param name="audit">
    /// The sink behind a declared <c>Audit</c>, or <c>null</c> for none.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>The four seams are optional to construct, and three of them are mandatory to
    /// declare against.</strong> A step whose resolved policy names a <c>RateLimit</c>, an
    /// <c>Idempotency</c> window or an <c>Audit</c> whose store is absent is <em>refused</em>,
    /// never dispatched — which is the whole of what makes stages 1, 3 and 7 honest, and is the
    /// opposite of <see cref="ICompensationAlertSink"/>'s bargain above. A missing alert sink
    /// leaves a deployment blind about a state the instance row still records; a missing limiter
    /// would leave one admitting every caller behind a declaration that reads as a
    /// deployment-wide bound, and a missing audit sink would let a regulated write whose record
    /// is the reason it is allowed to happen proceed with no record. Degraded and wrong are not
    /// the same absence. See
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0040-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md">ADR-0040</a>
    /// §2.2 and ADR-0025 §2.4.
    /// </para>
    /// <para>
    /// <strong>The cache is the one seam that forgives its own absence.</strong> A plan
    /// declaring a <c>Cache</c> with no <see cref="IResultCache"/> configured dispatches every
    /// time, which is the behaviour it had before stage 5 existed — an unconsulted cache is
    /// slower and never wrong (ADR-0025 §2.3). The asymmetry is the point: a cache's absence
    /// costs latency, and the other three's absence costs correctness.
    /// </para>
    /// <para>
    /// There is deliberately no in-memory default for any of them. A process-local rate limiter
    /// admits n × the declared rate across n nodes, and a process-local idempotency store
    /// deduplicates only the callers that happened to land on the same replica.
    /// </para>
    /// </remarks>
    /// <param name="breakersPerTenant">
    /// Whether a circuit breaker is keyed by capability and tenant rather than by capability
    /// alone. <c>docs/16 §4</c>'s fifth mechanism: with one breaker per capability, the tenant
    /// whose own downstream is failing trips the breaker for everybody, and the other tenants
    /// see an outage they are not having. Off by default and it has to be — a shared breaker is
    /// <em>faster</em> to protect a shared dependency, and a deployment where every tenant calls
    /// the same downstream wants that. It is turned on where the dependency is per tenant, which
    /// is the deployment that also sets the rest of <c>FlowXOptions.Fairness</c>.
    /// </param>
    public FlowEngine(
        IClock clock,
        int maxPooledContexts = DefaultMaxPooledContexts,
        ICompensationAlertSink? alerts = null,
        IRateLimiterStore? rateLimiter = null,
        IIdempotencyStore? idempotency = null,
        IResultCache? cache = null,
        IAuditSink? audit = null,
        bool breakersPerTenant = false)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPooledContexts);

        _breakersPerTenant = breakersPerTenant;
        _clock = clock;
        _contexts = new ContextPool(maxPooledContexts);
        _alerts = alerts;
        _rateLimiter = rateLimiter;
        _idempotency = idempotency;
        _cache = cache;
        _audit = audit;
    }

    /// <summary>Executes a plan to completion, to first failure, or to its deadline.</summary>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Invokes the capability behind each step index.</param>
    /// <param name="invocation">Correlation, tenant and the caller's remaining budget.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    public ValueTask<FlowExecutionResult> ExecuteAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowInvocation invocation,
        CancellationToken ct = default) =>
        RentAndRunAsync(plan, dispatcher, invocation, durable: null, ct);

    /// <summary>
    /// Executes a plan whose flow declares <see cref="ExecutionProfile.Durable"/>, journaling
    /// every step boundary under <paramref name="durable"/>'s fencing token.
    /// </summary>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Invokes the capability behind each step index.</param>
    /// <param name="invocation">Correlation, tenant and the caller's remaining budget.</param>
    /// <param name="durable">
    /// The instance, its journal and its token — and, when the instance is being picked up
    /// again, the committed history the loop derives its cursor from.
    /// </param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <remarks>
    /// <para>
    /// <strong>The same loop, not a second one.</strong> This overload differs from its
    /// sibling in exactly one thing: it supplies a journal. Compensation ordering, deadline
    /// handling, <c>ForEach</c> scoping, fork draining and sub-flow recursion are the code
    /// the ephemeral tests pin, unchanged — which is ADR-0015's central decision and the
    /// reason a second engine was rejected. A resumed instance re-enters here too; there is
    /// no recovery entry point to drift.
    /// </para>
    /// <para>
    /// A flow that does not declare <c>Durable</c> is refused here rather than journaled
    /// anyway: the profile is the declaration, and a journal written for a flow that did not
    /// ask for one is a cost nobody signed up for.
    /// </para>
    /// </remarks>
    public ValueTask<FlowExecutionResult> ExecuteAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowInvocation invocation,
        DurableExecution durable,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(durable);

        return RentAndRunAsync(plan, dispatcher, invocation, durable, ct);
    }

    /// <summary>
    /// Rents a context, runs the plan through it, and gives it back — the one place the
    /// pooling contract is honoured, whether or not there is a journal.
    /// </summary>
    private async ValueTask<FlowExecutionResult> RentAndRunAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowInvocation invocation,
        DurableExecution? durable,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);

        var context = _contexts.Rent();

        try
        {
            context.Initialise(plan, invocation, _clock, dispatcher);
            return await RunAsync(plan, dispatcher, context, durable, ct).ConfigureAwait(false);
        }
        finally
        {
            _contexts.Return(context);
        }
    }

    /// <summary>
    /// Executes a plan, seeding the flow's input so steps can bind to it by type.
    /// </summary>
    /// <remarks>
    /// Generic at the entry point and nowhere else. The step loop still knows nothing
    /// about types — it is this one call that puts the input into the context under its
    /// own type, which is what lets the generated dispatcher write
    /// <c>ctx.Get&lt;PlaceOrder&gt;()</c> for the first step and
    /// <c>ctx.Get&lt;ValidatedOrder&gt;()</c> for the second.
    /// </remarks>
    public ValueTask<FlowExecutionResult> ExecuteAsync<TIn>(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowInvocation invocation,
        TIn input,
        CancellationToken ct = default)
        where TIn : notnull =>
        RentAndRunAsync(plan, dispatcher, invocation, input, durable: null, ct);

    /// <summary>
    /// Executes a durable plan, seeding the flow's input so steps can bind to it by type.
    /// </summary>
    /// <typeparam name="TIn">The flow's input contract.</typeparam>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Invokes the capability behind each step index.</param>
    /// <param name="invocation">Correlation, tenant and the caller's remaining budget.</param>
    /// <param name="input">The flow's input, seeded into the context under its own type.</param>
    /// <param name="durable">The instance, its journal and its token.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <remarks>
    /// On a resumed instance the input is seeded first and the journaled state bag is
    /// restored over it, so a value a step produced wins over the trigger's copy of it. The
    /// input is seeded at all because a resumed flow's remaining steps may still bind to it,
    /// and the trigger is the only thing that has it.
    /// </remarks>
    public ValueTask<FlowExecutionResult> ExecuteAsync<TIn>(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowInvocation invocation,
        TIn input,
        DurableExecution durable,
        CancellationToken ct = default)
        where TIn : notnull
    {
        ArgumentNullException.ThrowIfNull(durable);

        return RentAndRunAsync(plan, dispatcher, invocation, input, durable, ct);
    }

    /// <inheritdoc cref="RentAndRunAsync(ExecutionPlan, IStepDispatcher, FlowInvocation, DurableExecution?, CancellationToken)" />
    private async ValueTask<FlowExecutionResult> RentAndRunAsync<TIn>(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowInvocation invocation,
        TIn input,
        DurableExecution? durable,
        CancellationToken ct)
        where TIn : notnull
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(input);

        var context = _contexts.Rent();

        try
        {
            context.Initialise(plan, invocation, _clock, dispatcher);
            context.Set(input);

            return await RunAsync(plan, dispatcher, context, durable, ct).ConfigureAwait(false);
        }
        finally
        {
            _contexts.Return(context);
        }
    }

    /// <summary>
    /// Executes a plan and projects its declared output from the finished context.
    /// </summary>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Invokes the capability behind each step index.</param>
    /// <param name="invocation">Correlation, tenant and the caller's remaining budget.</param>
    /// <param name="input">The flow's input, seeded into the context under its own type.</param>
    /// <param name="projection">
    /// The generated <c>.Return(...)</c> clause. A static delegate on the generated
    /// partial class, so passing it allocates nothing.
    /// </param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <remarks>
    /// The projection runs <em>here</em>, inside the rental, and not in the caller. The
    /// context is pooled and reset the moment this method returns, so a caller handed the
    /// context would read another flow's data — this is the only place the output can be
    /// taken safely.
    /// </remarks>
    public ValueTask<FlowExecutionResult<TOut>> ExecuteAsync<TIn, TOut>(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowInvocation invocation,
        TIn input,
        Func<FlowContext, TOut> projection,
        CancellationToken ct = default)
        where TIn : notnull =>
        RentAndRunAsync(plan, dispatcher, invocation, input, projection, durable: null, ct);

    /// <summary>
    /// Executes a durable plan and projects its declared output from the finished context.
    /// </summary>
    /// <typeparam name="TIn">The flow's input contract.</typeparam>
    /// <typeparam name="TOut">The flow's declared output contract.</typeparam>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Invokes the capability behind each step index.</param>
    /// <param name="invocation">Correlation, tenant and the caller's remaining budget.</param>
    /// <param name="input">The flow's input, seeded into the context under its own type.</param>
    /// <param name="projection">The generated <c>.Return(...)</c> clause.</param>
    /// <param name="durable">The instance, its journal and its token.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <remarks>
    /// The projection runs inside the rental for the reason it always did — the context is
    /// pooled and reset the moment this returns — and it runs after the instance has been
    /// closed in the journal, so a projection cannot observe an instance the store refused
    /// to finish.
    /// </remarks>
    public ValueTask<FlowExecutionResult<TOut>> ExecuteAsync<TIn, TOut>(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowInvocation invocation,
        TIn input,
        Func<FlowContext, TOut> projection,
        DurableExecution durable,
        CancellationToken ct = default)
        where TIn : notnull
    {
        ArgumentNullException.ThrowIfNull(durable);

        return RentAndRunAsync(plan, dispatcher, invocation, input, projection, durable, ct);
    }

    /// <inheritdoc cref="RentAndRunAsync(ExecutionPlan, IStepDispatcher, FlowInvocation, DurableExecution?, CancellationToken)" />
    private async ValueTask<FlowExecutionResult<TOut>> RentAndRunAsync<TIn, TOut>(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowInvocation invocation,
        TIn input,
        Func<FlowContext, TOut> projection,
        DurableExecution? durable,
        CancellationToken ct)
        where TIn : notnull
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(projection);

        var context = _contexts.Rent();

        try
        {
            context.Initialise(plan, invocation, _clock, dispatcher);
            context.Set(input);

            var outcome = await RunAsync(plan, dispatcher, context, durable, ct).ConfigureAwait(false);

            return outcome.IsSuccess
                ? new FlowExecutionResult<TOut>(outcome, projection(context))
                : new FlowExecutionResult<TOut>(outcome, default);
        }
        finally
        {
            _contexts.Return(context);
        }
    }

    private async ValueTask<FlowExecutionResult> RunAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        DurableExecution? durable,
        CancellationToken ct)
    {
        var cursor = OpenJournal(plan, dispatcher, context, durable, out var refusal);

        if (refusal is not null)
        {
            return FlowExecutionResult.Rejected(refusal);
        }

        // Recorded on the context so the unwind can find it without a sixth parameter on
        // every method in the loop. Null for an ephemeral execution, which is the same
        // always-false branch the journaling sites already make.
        context.Run = cursor.Run;

        // Only steps that both completed and declared a compensation go on the stack,
        // so the unwind never has to filter. The stack belongs to the pooled context,
        // so a saga costs no more per execution than a query does.
        var compensations = plan.HasCompensation ? context.Compensations : null;

        var outcome = await RunRangeAsync(
            plan, dispatcher, context, context, compensations, 0, plan.Graph.Count, cursor, ct)
            .ConfigureAwait(false);

        var result = await CompleteAsync(plan, dispatcher, context, compensations, outcome).ConfigureAwait(false);

        return cursor.IsJournaled
            ? await SealAsync(cursor, result, ct).ConfigureAwait(false)
            : result;
    }

    /// <summary>
    /// Decides whether this execution journals, and rehydrates a resumed one before the loop
    /// starts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is where the runtime reads <see cref="ExecutionProfile"/>, and both
    /// mismatches are refused rather than papered over.</strong> A <c>Durable</c> flow with
    /// no journal would be the exact defect <c>FLOWX1028</c> existed to describe — a
    /// declaration that buys nothing and says nothing — so it is refused with an error naming
    /// what is missing, on the first invocation rather than on the first crash. An
    /// <c>Ephemeral</c> flow handed a journal is refused too: journaling a flow that did not
    /// ask for it charges it for a guarantee its author declined.
    /// </para>
    /// <para>
    /// Rehydration is a dispatcher call because the state bag holds contract types and the
    /// engine holds none. It happens once, before the first step, and only when the instance
    /// actually committed a snapshot.
    /// </para>
    /// </remarks>
    private static JournalCursor OpenJournal(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        DurableExecution? durable,
        out Error? refusal)
    {
        refusal = null;

        if (!ExecutionProfiles.IsJournaled(plan.Flow.Profile))
        {
            if (durable is not null)
            {
                refusal = FlowErrors.ProfileIsNotDurable(plan.Flow.Id, plan.Flow.Profile);
            }

            return default;
        }

        if (durable is null)
        {
            refusal = FlowErrors.DurabilityNotConfigured(plan.Flow.Id);
            return default;
        }

        if (durable.Frontier?.Instance.StateBagJson is { } snapshot)
        {
            try
            {
                dispatcher.RestoreState(context, snapshot);
            }
#pragma warning disable CA1031 // Same reasoning as every other generated-code call site: a
            catch (Exception exception) //   dispatcher that cannot read back what it wrote is
            {                           //   a defect, and resuming with an empty bag would run
                refusal = FlowErrors    //   the rest of the flow against values no step made.
                    .StateRestoreFailed(plan.Flow.Id, durable.InstanceId, exception);
                return default;
            }
#pragma warning restore CA1031
        }

        return new JournalCursor(durable, StepScope.Root);
    }

    /// <summary>Records where the instance came to rest once the loop and the unwind are done.</summary>
    /// <remarks>
    /// <para>
    /// A refusal here becomes the flow's error even when every step succeeded. Being fenced
    /// out at the last moment means another node owns this instance and will finish it; a
    /// success reported to the caller would be this node claiming an outcome it no longer
    /// controls.
    /// </para>
    /// <para>
    /// A flow that already ended because this node was disowned is not sealed at all. The
    /// terminal state belongs to whoever holds the instance now, and a write under a token
    /// below the fence can only be refused a second time — which would replace the error that
    /// explains what happened with a duplicate of itself.
    /// </para>
    /// <para>
    /// <strong>Not every state written here is terminal.</strong> A flow that stopped at a
    /// suspension point is recorded <see cref="FlowInstanceState.Suspended"/> through the same
    /// call, because that is what makes the wait a row rather than a thread — and because a
    /// journal correctly refuses a write to an instance it believes is finished, so recording
    /// a wait as anything terminal would make the resume that follows it impossible.
    /// </para>
    /// </remarks>
    private static async ValueTask<FlowExecutionResult> SealAsync(
        JournalCursor cursor,
        FlowExecutionResult result,
        CancellationToken ct)
    {
        var instanceId = cursor.Run!.InstanceId;

        if (result.Error is { } ended && Disowned(ended))
        {
            return Stamped(result, instanceId);
        }

        var refusal = await CloseInstanceAsync(cursor, InstanceStateFor(result), result.Wake, ct)
            .ConfigureAwait(false);

        return refusal is null
            ? Stamped(result, instanceId)
            : new FlowExecutionResult(
                refusal, result.CompletedSteps, result.Compensation, instanceId: instanceId);
    }

    /// <summary>Puts the instance's identity on the result a caller gets back.</summary>
    /// <remarks>
    /// The id is minted inside the host, so without this a caller has no way to name the
    /// instance a signal belongs to — which makes a suspended flow unreachable rather than
    /// merely opaque.
    /// </remarks>
    private static FlowExecutionResult Stamped(FlowExecutionResult result, Guid instanceId) =>
        new(
            result.Error,
            result.CompletedSteps,
            result.Compensation,
            result.IsSuspended,
            instanceId,
            result.Wake);

    /// <summary>Moves one instance — root, composed or detached — to the state it rests in.</summary>
    /// <remarks>
    /// The state bag is deliberately <see cref="JournalPayload.Empty"/>: the last committed
    /// step already carried the snapshot, and re-serialising the bag at the end would make
    /// the final row disagree with the step that produced it whenever compensation has run.
    /// </remarks>
    private static async ValueTask<Error?> CloseInstanceAsync(
        JournalCursor cursor,
        FlowInstanceState state,
        FlowWake? wake,
        CancellationToken ct)
    {
        var run = cursor.Run!;

        var closed = await run.Journal
            .CompleteAsync(run.InstanceId, run.Token, state, JournalPayload.Empty, wake, ct)
            .ConfigureAwait(false);

        return closed.IsSuccess ? null : closed.Error;
    }

    /// <summary>Which state an execution that has stopped leaves on the instance row.</summary>
    /// <remarks>
    /// <para>
    /// <c>CompensationFailed</c> outranks the rest: it is the one terminal state with no
    /// automatic resolution — two systems now disagree about the same business fact — and
    /// recording it as a plain failure would hide the one outcome an operator has to be told
    /// about.
    /// </para>
    /// <para>
    /// <c>Suspended</c> is tested first among the rest and is the one answer here that is not
    /// terminal. It is also what keeps a waiting instance out of a recovery scan's candidate
    /// set: both shipped indexes list <c>Pending</c>, <c>Running</c> and <c>Compensating</c>
    /// only, so a flow waiting for a countersignature is not swept up every TTL and re-suspended.
    /// </para>
    /// </remarks>
    private static FlowInstanceState InstanceStateFor(FlowExecutionResult result) => result switch
    {
        { Compensation: CompensationOutcome.PartiallyFailed } => FlowInstanceState.CompensationFailed,
        { IsSuspended: true } => FlowInstanceState.Suspended,
        { IsSuccess: true } => FlowInstanceState.Completed,
        { Error.Code: FlowErrors.DeadlineExceededCode } => FlowInstanceState.TimedOut,
        _ => FlowInstanceState.Failed,
    };

    /// <summary>
    /// Where in a journaled instance the loop currently is: which instance, and which
    /// iteration of which loop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One parameter instead of two, and a <c>default</c> that means "ephemeral".</strong>
    /// Every method in the step loop has to carry both, and an ephemeral execution passes a
    /// pair of null references — no allocation, no branch beyond the one null check the
    /// journaling sites already make. That is the same shape <c>ExecutionPlan.HasParallel</c>
    /// uses to keep a linear flow from paying for a fork.
    /// </para>
    /// <para>
    /// <see cref="Scope"/> is <see cref="StepScope"/> — the type <c>ForEach</c>'s journal key
    /// already needed — and not a second notion of iteration invented for the engine. It is
    /// rendered text so that a store persists one column and an operator can read where a
    /// step ran without joining anything.
    /// </para>
    /// </remarks>
    private readonly struct JournalCursor(DurableExecution? run, StepScope scope)
    {
        /// <summary>The instance being journaled, or <c>null</c> for an ephemeral execution.</summary>
        public DurableExecution? Run { get; } = run;

        /// <summary>Which iteration of which loop the steps under this cursor run in.</summary>
        public StepScope Scope { get; } = scope;

        /// <summary>Whether anything under this cursor is written down.</summary>
        public bool IsJournaled => Run is not null;

        /// <summary>The cursor one element of a loop entered from here runs under.</summary>
        /// <remarks>
        /// The scope path is only built when there is a journal to write it to, so an
        /// ephemeral <c>ForEach</c> does not pay a string per element for a column nobody
        /// reads.
        /// </remarks>
        public JournalCursor Element(int index) =>
            Run is null ? default : new JournalCursor(Run, Scope.Element(index));

        /// <summary>The cursor a composed child instance runs under.</summary>
        public JournalCursor Child(Guid childInstanceId) =>
            Run is null ? default : new JournalCursor(Run.ForChild(childInstanceId), StepScope.Root);
    }

    /// <summary>
    /// Turns a finished range into the flow's result: compensating on failure, and
    /// releasing whatever a successful sub-flow left rented.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="RunAsync"/> because a detached sub-flow needs exactly the
    /// same ending — it fails, compensates and cleans up on its own — and having two copies
    /// of "what the end of a flow means" is how the second one drifts.
    /// </remarks>
    private async ValueTask<FlowExecutionResult> CompleteAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        CompensationStack? compensations,
        RangeOutcome outcome)
    {
        if (outcome.Failure is null)
        {
            // A sub-flow that succeeded with compensations pending is still holding a
            // pooled context, because the parent might yet have to undo it. The parent did
            // not, so give them back. Gated on the plan so a flow that composes nothing
            // does not pay an iterator for the possibility that another flow does — which
            // is what keeps budget B2 a hard zero where it always was.
            //
            // A suspended flow gives them back too, and that is a limit rather than a
            // choice: the invocation is returning, and a pooled context still held by a
            // waiting instance would be a context the next flow never gets. It is the same
            // gap ADR-0015 already records for a resumed parent that skips a completed
            // child — the child's undo cannot be rebuilt from the parent's rows — reached
            // through a second door.
            if (plan.HasSubFlow)
            {
                ReleaseRetainedSubFlows(context);
            }

            return outcome.Suspended
                ? FlowExecutionResult.Suspended(outcome.Completed, outcome.Wake)
                : new FlowExecutionResult(null, outcome.Completed, CompensationOutcome.NotRequired);
        }

        context.SetError(outcome.Failure);

        CompensationOutcome compensation;

        if (compensations is null)
        {
            compensation = CompensationOutcome.NotRequired;
        }
        else if (Disowned(outcome.Failure))
        {
            // The instance belongs to another node now, and that node has the journal, the
            // frontier and — if it comes to it — the only compensation stack that describes
            // what the instance actually did. Unwinding here would run this node's undo
            // effects against work the new owner is carrying forwards, which is a second
            // set of real effects on top of the ones the lease already failed to prevent.
            compensation = compensations.IsEmpty
                ? CompensationOutcome.NotRequired
                : CompensationOutcome.Abandoned;
        }
        else
        {
            compensation = await CompensateAsync(compensations, dispatcher, context).ConfigureAwait(false);
        }

        return new FlowExecutionResult(outcome.Failure, outcome.Completed, compensation);
    }

    /// <summary>
    /// Whether an error means this node is no longer the writer for this instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two refusals say it. <c>journal.fenced_out</c> is a lease that was reissued while this
    /// node was executing — a pause, a partition, a step that outlived its own TTL — and
    /// <c>journal.instance_terminal</c> is an instance another node has already finished.
    /// Both are <see cref="ErrorCategory.Forbidden"/> or <see cref="ErrorCategory.Conflict"/>
    /// and neither is retryable: presenting the same token again can only fail again.
    /// </para>
    /// <para>
    /// Read off the code rather than the category, because the category is shared with
    /// refusals that <em>are</em> this flow's failure — a duplicate step key is a
    /// <see cref="ErrorCategory.Conflict"/> and is a defect in this node, not a change of
    /// ownership.
    /// </para>
    /// <para>
    /// On the failure path only, so an ephemeral flow that never fails never evaluates it and
    /// one that does pays a string comparison against two constants. Budget B2 is untouched.
    /// </para>
    /// </remarks>
    private static bool Disowned(Error failure) =>
        failure.Code is DurabilityErrors.FencedOutCode or DurabilityErrors.InstanceTerminalCode;

    /// <summary>How a range of steps ended: the first failure in it, and how many ran.</summary>
    /// <remarks>
    /// <para>
    /// A struct so a branch's result costs nothing to return. The whole flow is one range,
    /// <c>[0, Count)</c>, so the sequential path and a parallel branch are literally the
    /// same code — which is the point of the shape, and the reason a branch inherits the
    /// deadline check, the compensation recording and the exception handling for free
    /// rather than by being kept in step with them.
    /// </para>
    /// <para>
    /// <strong>Three endings rather than two, since WP-63.</strong> A range that stopped at a
    /// suspension point has neither failed nor finished, and the two existing answers both
    /// say something untrue about it: a failure would unwind steps nothing went wrong with,
    /// and a success would let the range after it run.
    /// </para>
    /// </remarks>
    private readonly struct RangeOutcome(
        Error? failure,
        int completed,
        bool suspended = false,
        FlowWake? wake = null)
    {
        public Error? Failure { get; } = failure;

        public int Completed { get; } = completed;

        /// <summary>
        /// Whether the range stopped at a suspension point — an
        /// <see cref="StepKind.AwaitSignal"/> or a <see cref="StepKind.Delay"/>.
        /// </summary>
        public bool Suspended { get; } = suspended;

        /// <summary>
        /// The wait the range parked at and when it is due, or <c>null</c> when nothing is
        /// due to wake it.
        /// </summary>
        /// <remarks>
        /// Carried out of the range rather than written where it is decided, because the write
        /// that records it is the same one that records <c>Suspended</c> — a wake instant
        /// stored by a second call would leave a window in which an instance is parked with
        /// nothing scheduled to wake it.
        /// </remarks>
        public FlowWake? Wake { get; } = wake;
    }

    /// <summary>
    /// Runs the steps in <c>[from, end)</c> — the whole flow, or one branch of a fork.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The range is what makes <c>Parallel</c> fit the flat array.</strong> A
    /// branch is a contiguous half-open span of the same one array, and
    /// <see cref="StepNode.BranchTargets"/> is validated to be strictly ascending inside
    /// the fork, so the spans are disjoint and cover the fork exactly. Termination still
    /// follows from the same fact it always did: every target points strictly forward, and
    /// a target past <paramref name="end"/> simply ends the range.
    /// </para>
    /// <para>
    /// <strong>What the flat model gives up.</strong> Between a fork and its join, "the
    /// loop index" stops describing the flow — there are several, one per branch, on
    /// several threads. Everything else survives: no branch stack, no nested plan objects,
    /// no per-step recursion, and a linear or conditional flow never leaves this method.
    /// </para>
    /// <para>
    /// <strong><paramref name="scope"/> is what the dispatcher sees;
    /// <paramref name="context"/> is what the engine keeps its books in.</strong> They are
    /// the same object everywhere except inside a <c>ForEach</c> body, where the scope is
    /// the iteration's view — the element, shadowing the shared state bag on its own type.
    /// The split is deliberate: the deadline, the compensation stack and the running-step
    /// identity belong to the flow and must not fork per element, while what a step
    /// <em>reads</em> must.
    /// </para>
    /// <para>
    /// <strong><paramref name="cursor"/> is the durable seam, and it is one parameter
    /// deep.</strong> When it carries a journal, every capability step in this range commits
    /// a row before control moves on, and a step whose row is already committed is skipped
    /// rather than re-run — which is the whole of resumption. When it does not, every branch
    /// that reads it is false and the range is byte-for-byte the execution it always was.
    /// There is no second version of this method for durable flows, because two loops
    /// diverge and the ephemeral one gets the features first.
    /// </para>
    /// </remarks>
    private async ValueTask<RangeOutcome> RunRangeAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        FlowContext scope,
        CompensationStack? compensations,
        int from,
        int end,
        JournalCursor cursor,
        CancellationToken ct)
    {
        var steps = plan.Graph.Steps;
        var completed = 0;
        var suspended = false;
        FlowWake? wake = null;
        Error? failure = null;

        // Not `for (i = from; i < end; i++)`. A conditional is compiled into this same flat
        // array as a Branch and a Jump, so the index advances either by one or to a
        // target. The loop is still guaranteed to terminate: StepGraph rejects any target
        // that is out of range or points backwards, which is the whole reason that check
        // exists.
        var i = from;

        while (i < end)
        {
            var step = steps[i];

            // A control transfer does no work. It invokes nothing, so it cannot fail and
            // cannot be compensated; it consumes no measurable time, so charging it a
            // deadline check would buy nothing but a clock read. The step it lands on
            // does both.
            if (step.Kind == StepKind.Jump)
            {
                // `.Value`, not `.GetValueOrDefault()`. The factories make a control
                // transfer without a target unrepresentable, so this cannot be null — but
                // if it ever were, defaulting to zero would silently restart the flow and
                // loop forever, and a throw is the diagnosable failure.
                i = step.Target!.Value;
                continue;
            }

            if (step.Kind == StepKind.Branch)
            {
                bool taken;

                try
                {
                    taken = dispatcher.Evaluate(i, scope);
                }
#pragma warning disable CA1031 // Same reasoning as the capability call below, plus one
                catch (Exception exception) //   more: a predicate escaping here would skip
                {                           //   the compensation the already-completed
                    failure = FlowErrors    //   steps need, leaving exactly the dangling
                        .PredicateFailed(plan.Flow.Id, i, exception); // state a saga prevents.
                    break;
                }
#pragma warning restore CA1031

                // The `then` block is laid out immediately after the branch, so the true
                // path is the ordinary next index and only the false path needs a target.
                i = taken ? i + 1 : step.Target!.Value;
                continue;
            }

            if (step.Kind == StepKind.Switch)
            {
                int arm;

                try
                {
                    arm = dispatcher.Select(i, scope);
                }
#pragma warning disable CA1031 // Same reasoning as the predicate above: a selector that
                catch (Exception exception) //   escapes here would skip the compensation
                {                           //   the already-completed steps need.
                    failure = FlowErrors.SelectorFailed(plan.Flow.Id, i, exception);
                    break;
                }
#pragma warning restore CA1031

                // Unsigned, so "no case matched" (-1) and a dispatcher that answered out
                // of range take the same path — the default target — rather than throwing
                // an IndexOutOfRangeException from the middle of a flow. One comparison,
                // one array read, nothing allocated.
                var cases = step.CaseTargets;

                i = (uint)arm < (uint)cases.Length ? cases[arm] : step.Target!.Value;
                continue;
            }

            // Resumption, and the whole of it. A row for this (scope, step) is already
            // committed, so the step has already happened: its effects are done and the row
            // is the truth about this instance. Re-running it is the one thing resumption
            // exists to avoid.
            //
            // Placed here rather than beside the capability call so that a composed sub-flow
            // is skipped too — a child instance that completed must not be composed a second
            // time. A fork or a loop never has a row of its own, so this scan never matches
            // one and their bodies are asked the same question one level down.
            //
            // The books the loop keeps are kept anyway: the step counts as work this instance
            // has done, and a compensable one goes back on the unwind stack, because a
            // resumed flow that later fails must undo what the node before it did.
            if (cursor.IsJournaled && cursor.Run!.Completed(cursor.Scope, i) is not null)
            {
                completed++;

                // Except a sub-flow's, which cannot be rebuilt from here: the parent records
                // the composition as one entry bound to the *child's* context, and that
                // context died with the node that ran it. Rebuilding the child's stack from
                // the child's own instance rows needs a "which instances are under this
                // parent" query that IFlowJournal deliberately does not answer — it is a
                // recovery scan's, which is why IRecoveryIndex was split out — so WP-57 left
                // it standing rather than widening a contract every store implements. It is
                // named rather than quietly approximated: a compensation stack that is
                // silently short is the failure a saga exists to prevent.
                //
                // Except, too, a step whose undo has already committed. That is rule 4 of
                // 06 §7 read from the other end: a crash during compensation resumes
                // compensation, so a step the dead node finished undoing is not put back on
                // the stack for the new one to undo again.
                if (compensations is not null &&
                    step.Kind != StepKind.SubFlow &&
                    !cursor.Run!.Compensated(cursor.Scope, i))
                {
                    context.RecordCompleted(
                        step, ReferenceEquals(scope, context) ? null : scope, cursor.Scope);
                }

                // A committed suspension point is one a signal satisfied — the timeout path
                // writes no row for itself — so it resumes where a delivered signal left it,
                // past the escalation block rather than into it.
                i = SignalTargetOf(step) ?? i + 1;
                continue;
            }

            if (step.Kind == StepKind.Parallel)
            {
                var forked = await RunParallelAsync(
                    plan, dispatcher, context, scope, compensations, step, cursor, ct).ConfigureAwait(false);

                // Counted whether or not the merge held: those steps really ran, and a
                // caller reading CompletedSteps to decide what was touched needs the
                // work a cancelled branch had already done to be in the number.
                completed += forked.Completed;

                if (forked.Failure is not null)
                {
                    failure = forked.Failure;
                    break;
                }

                i = step.Target!.Value;
                continue;
            }

            if (step.Kind == StepKind.ForEach)
            {
                var iterated = await RunForEachAsync(
                    plan, dispatcher, context, scope, compensations, step, cursor, ct).ConfigureAwait(false);

                // Counted for the same reason a fork's are: those steps really ran, and a
                // loop that stopped at the third of ten elements has still done the work
                // of the first two.
                completed += iterated.Completed;

                if (iterated.Failure is not null)
                {
                    failure = iterated.Failure;
                    break;
                }

                i = step.Target!.Value;
                continue;
            }

            if (step.Kind == StepKind.SubFlow)
            {
                // No target: a sub-flow occupies exactly one index, so control resumes at
                // the next one exactly as it would after a capability.
                var composed = await RunSubFlowAsync(
                    plan, dispatcher, context, scope, compensations, step, cursor, ct).ConfigureAwait(false);

                // The child's steps count towards the parent's total. They really ran, and
                // a caller reading CompletedSteps to decide what was touched needs work the
                // composition did to be in the number — the whole point of composing is
                // that the child's effects are the flow's effects.
                completed += composed.Completed;

                if (composed.Failure is not null)
                {
                    failure = composed.Failure;
                    break;
                }

                i++;
                continue;
            }

            // The identity is taken from the return value rather than read back off the
            // context. Inside a fork a sibling overwrites the field between the throw and
            // the catch, and an error naming the wrong capability is worse than none.
            var capabilityId = context.EnterStep(step);

            if (context.UtcNow >= context.Deadline)
            {
                failure = FlowErrors.DeadlineExceeded(plan.Flow.Id, context.Deadline);
                break;
            }

            // The suspension points, and the whole of them. The step has no committed row —
            // the skip above did not take — so it is being reached rather than replayed, and
            // one of four things is true: what it waits for is in this invocation, its wait is
            // over, its wait is still running, or it already timed out and this is a second
            // pass over the block that ran when it did.
            //
            // After the deadline check on purpose: an instance whose budget has already gone
            // must time out rather than park, because a suspension is the one ending from
            // which nothing but a signal or a sweep would ever look again.
            //
            // A wait that is satisfied falls through to the ordinary path below. The
            // dispatcher answers Success for both kinds — there is no capability to call — and
            // the commit that follows writes the row and the state-bag snapshot, which is why
            // resumption needs no signal table and no timer table of its own.
            if (step.Kind is StepKind.AwaitSignal or StepKind.Delay)
            {
                var verdict = ResolveWait(cursor, context, step, out var due);

                if (verdict == WaitVerdict.Waiting)
                {
                    suspended = true;
                    wake = new FlowWake(cursor.Scope, step.Index, due);
                    break;
                }

                if (verdict == WaitVerdict.Unmet)
                {
                    // Nowhere for the timeout to go. Continuing at the next index would run
                    // steps that bind a payload nothing delivered, so the wait ending is the
                    // flow ending — and the completed compensable steps unwind behind it,
                    // which is the whole reason an escalation is worth declaring.
                    failure = FlowErrors.SignalNotReceived(
                        plan.Flow.Id, step.Index, step.SignalType!, step.SignalTimeout!.Value);
                    break;
                }

                if (verdict == WaitVerdict.Escalated)
                {
                    // The block is laid out immediately after the wait, so taking it is the
                    // ordinary next index — the mirror of a Branch, whose `then` block is
                    // contiguous and whose target is the path that skips it.
                    i++;
                    continue;
                }
            }

            // ADR-0023, and the whole of the forward policy hook: one comparison against a
            // field the plan already holds. A flow that declares no executed policy reaches
            // StepPolicy.None, the loop below runs exactly once, and the execution is
            // byte-for-byte the one it always was — which is what keeps budget B2 a hard zero
            // for the shapes that have always had it.
            //
            // Seven kinds are counted now rather than four, and no second flag went with them.
            // That is ADR-0023's own "widening is mechanical" taken literally, so its "a third
            // flag of this shape is proposed" revisit condition did not fire: stages 1, 3 and 5
            // arrive on the same PolicyChain that StepPolicy.From already walks, unlike a stance,
            // which is resolved from a capability attribute and therefore needed
            // HasAuthorizedSteps of its own. Stage 7 is the one that did need a flag, and earns
            // it by running outside the wrapping the other stages share.
            var policy = plan.HasStepPolicies ? step.StepPolicy : StepPolicy.None;

            // Stage 1 · Admission. Before the authorisation below, which is ADR-0011's order and
            // docs/10 §2's "rate limit after authentication → unauthenticated flood exhausts the
            // token validator" row: an unadmitted caller must not reach the claim lookup.
            //
            // Outside the retry loop below, which is the other half of the position. A permit
            // taken per attempt would make a RateLimit(20, PT1S) beside a Retry(3) admit
            // somewhere between seven and twenty callers a second depending on how healthy the
            // dependency was — a limit whose effective value is a function of an outage.
            if (policy.HasRateLimit &&
                await AdmitAsync(policy, context, capabilityId, ct).ConfigureAwait(false) is { } unadmitted)
            {
                failure = unadmitted;
                break;
            }

            // ADR-0027, and the whole of the authorisation hook: ADR-0023's shape struck a
            // second time. One comparison against a field the plan already holds, and a plan
            // nobody can be refused from — every step Public, Internal or unstanced — never
            // reads a principal or a claim.
            //
            // Before the retry loop, deliberately. A refusal is terminal: asking the same
            // question of the same principal three times gets the same answer three times,
            // while the backoff spends the flow's deadline and one audit event becomes three.
            //
            // Before the dispatch, even more deliberately. A check that ran after the call
            // would have authorised nothing, because the payment has already been captured.
            // `!context.IsContinuation` is not a bypass, and the distinction is on
            // FlowInvocation: a timer sweep and a recovery scan are the platform continuing an
            // instance it already admitted, with no caller asking for anything and no claims on
            // the journal row to ask about. Re-deciding a stance there would make
            // `.Delay(TimeSpan.FromHours(1))` a construct no flow could place before an
            // authenticated step, and would turn a node restart into a refusal. A signal is the
            // opposite — somebody is delivering something now — and carries its deliverer.
            if (plan.HasAuthorizedSteps
                && !context.IsContinuation
                && step.StepAuthorization.Decide(context.Principal, capabilityId) is { } denial)
            {
                failure = denial;
                break;
            }

            // Stage 3 · Integrity. After admission and identity, before the retry loop — which is
            // ADR-0011's order and, for the retry, a correctness requirement rather than a
            // preference: a claim taken per attempt would find its own in-flight marker on
            // attempt two and deadlock the step against itself.
            //
            // Three outcomes. A replay skips the dispatch entirely and restores what the first
            // execution produced; an in-flight repeat is refused with the holder's remaining
            // lease; a fresh key is claimed and released again below.
            string? idempotencyKey = null;

            if (policy.HasIdempotency)
            {
                var began = await BeginIdempotentAsync(
                    policy, dispatcher, context, scope, capabilityId, ct).ConfigureAwait(false);

                if (began.Refusal is { } notClaimed)
                {
                    failure = notClaimed;
                    break;
                }

                if (began.Replayed)
                {
                    completed++;

                    // Registered for compensation exactly as a dispatched step is, and the
                    // reason is that the effect exists: some earlier caller made it, under this
                    // same idempotency key, so the two are one logical request. A replayed step
                    // left off the stack would leave a real effect with nothing pointing at it
                    // when a later step fails — which is the saga losing track of work it is
                    // standing on.
                    if (compensations is not null && step.IsCompensable)
                    {
                        context.RecordCompleted(
                            step, ReferenceEquals(scope, context) ? null : scope, cursor.Scope);
                    }

                    i = SignalTargetOf(step) ?? i + 1;
                    continue;
                }

                idempotencyKey = began.Key;
            }

            var attempt = 0;
            Error? stepFailure = null;
            var abandoned = false;

            // The retry is the outermost of the four stage-4 kinds (ADR-0024), so it is a loop
            // around the dispatch and the commit rather than something inside either. That is
            // also what makes the journal's key honest: run.NextAttempt derives the attempt
            // number from the committed history, so a retried step writes one row per attempt
            // without this node having to remember a number that dies with it.
            while (true)
            {
                attempt++;

                // Only read when there is a row to put it on. An ephemeral step does not pay a
                // clock read to measure a duration nobody records.
                var startedAt = cursor.IsJournaled ? _clock.UtcNow : default;

                StepOutcome outcome;
                Error? thrown = null;

                try
                {
                    outcome = policy.IsActive
                        ? await DispatchPolicedAsync(dispatcher, context, step, policy, i, scope, ct)
                            .ConfigureAwait(false)
                        : await dispatcher.ExecuteAsync(i, scope, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Caller-initiated, or a sibling branch's failure cancelling this one, and
                    // not this step's fault — but the work already done still has to be
                    // undone, so this joins the failure path rather than propagating.
                    //
                    // Deliberately not journaled: the token that would guard the write is the
                    // one this node may be losing, and a store call on a cancelled path is the
                    // least likely of all writes to succeed. The attempt simply has no row,
                    // which is what makes it re-runnable on resume.
                    //
                    // A timeout never arrives here: DispatchPolicedAsync converts its own
                    // cancellation into an Error and lets the caller's through, so "the step
                    // ran out of budget" and "the caller went away" stay distinguishable.
                    failure = FlowErrors.Cancelled(plan.Flow.Id);
                    abandoned = true;
                    break;
                }
#pragma warning disable CA1031 // A capability that throws is a defect; the engine converts
                catch (Exception exception)  //   it into an error rather than letting it kill
                {                            //   the trigger's consumer loop. This is the one
                    outcome = StepOutcome.Success;  //   place a general catch is correct, and
                    thrown = FlowErrors             //   it re-reports rather than swallowing.
                        .Unhandled(capabilityId, exception);
                }
#pragma warning restore CA1031

                stepFailure = thrown ?? outcome.Error;

                // One commit per (instance, scope, step, attempt), before control moves on —
                // the whole of ADR-0015's decision, in one place. A failed attempt is recorded
                // too: the attempt history is what makes the replay contract provable rather
                // than asserted, and an effect that happened before the failure is exactly
                // what a resumed instance must not repeat blindly.
                if (cursor.IsJournaled)
                {
                    var refusal = await CommitStepAsync(
                        plan, dispatcher, context, scope, cursor, step, stepFailure, startedAt,
                        capabilityVersion: null, attempt, ct)
                        .ConfigureAwait(false);

                    if (refusal is not null)
                    {
                        // Fenced out, duplicated, or written to a finished instance. Every one
                        // of those means this node is no longer the writer, so it stops rather
                        // than carrying on with work nobody will accept.
                        failure = refusal;
                        abandoned = true;
                        break;
                    }
                }

                if (stepFailure is null || !policy.AllowsAnotherAttempt(stepFailure, attempt))
                {
                    if (policy.IsRetrying)
                    {
                        // Once per step that carried a retry, at the point the retry stops
                        // asking. `exhausted` is the outcome that matters: a step that used
                        // every attempt it was allowed and still failed is the one whose
                        // dependency an operator has to go and look at.
                        PolicyApplied(
                            StepPolicy.RetryKind,
                            capabilityId,
                            stepFailure is null ? PolicyMetrics.OkOutcome : PolicyMetrics.ExhaustedOutcome);
                    }

                    break;
                }

                // docs/10 §5's second guarantee: a retry never outlives the deadline. The wait
                // is planned before it is taken, so an attempt whose backoff alone would run
                // past the budget is refused rather than started and then killed by the step
                // loop's own deadline check.
                //
                // Random.Shared rather than ctx.Random, for the reason the compensation
                // backoff gives: how long a failed attempt waited is not one of the step's
                // decisions, and recording it would put a number in the replay envelope that
                // no replay could act on.
                var backoff = policy.DelayBefore(attempt, Random.Shared.NextDouble());

                if (_clock.UtcNow + backoff >= context.Deadline)
                {
                    // The retry wanted another attempt and the deadline refused it. That is
                    // still an exhausted retry from the operator's side — the step failed with
                    // attempts left on paper — so it is reported rather than passed over.
                    PolicyApplied(StepPolicy.RetryKind, capabilityId, PolicyMetrics.ExhaustedOutcome);

                    break;
                }

                // After the deadline check and before the wait, so the count is of attempts
                // that were actually made rather than of attempts that were contemplated. The
                // attempt label is the one about to run, which is why it is never 1.
                PolicyMetrics.Retried(capabilityId, attempt + 1, stepFailure.Code);

                await _clock.DelayAsync(backoff, ct).ConfigureAwait(false);
            }

            // Stage 3 · Integrity, closing. After the retry rather than after each attempt, so
            // the record describes the step's outcome rather than one attempt's, and so a step
            // that failed twice and succeeded on the third records once.
            //
            // Before the `abandoned` check below on purpose: a node that lost its lease
            // mid-step still holds an idempotency claim, and leaving it to lapse would refuse
            // every repeat of that key until the in-flight lease ran out — for work this node
            // has already stopped doing.
            if (idempotencyKey is not null)
            {
                var recorded = await EndIdempotentAsync(
                    idempotencyKey, policy, dispatcher, scope, capabilityId, i,
                    stepFailure is null && !abandoned, ct).ConfigureAwait(false);

                if (recorded is not null && stepFailure is null && !abandoned)
                {
                    // The step worked and the record could not be written honestly — ADR-0042's
                    // guard, or a store that stopped answering. Reported rather than swallowed:
                    // a policy that silently recorded nothing would leave the declaration
                    // looking satisfied, which is the whole of what ADR-0025 rejects.
                    stepFailure = recorded;
                }
            }

            if (abandoned)
            {
                break;
            }

            if (stepFailure is not null)
            {
                failure = stepFailure;
                break;
            }

            completed++;

            if (compensations is not null)
            {
                // Through the context, not the stack directly: two branches can complete a
                // compensable step at the same instant, and the context is what serialises
                // the push. A lost push is an undo that never runs.
                //
                // The scope goes on the stack with the step, and is null everywhere except
                // inside an iteration — that is what lets the undo of "the line this step
                // reserved" find the right line once the loop is over. The journal's spelling
                // of the same iteration goes with it, so the row the undo writes lands under
                // the element it undid rather than colliding with the first one's.
                context.RecordCompleted(
                    step, ReferenceEquals(scope, context) ? null : scope, cursor.Scope);
            }

            // Stage 7, and the last thing that happens to a step. ADR-0023's shape struck a
            // fourth time: one comparison against a field the plan already holds, and a flow
            // that audits nothing reads no principal, asks the dispatcher for no payload and
            // touches no sink.
            //
            // After the commit, because a record of a step the journal does not have is a
            // record of something that may yet be re-run. After RecordCompleted, because a
            // record that cannot be written has to unwind the step it was going to describe —
            // the money moved and nothing can say who moved it, which is the one outcome an
            // audited step exists to make impossible. A failure here therefore joins the
            // ordinary failure path and the compensable steps behind it, this one included,
            // are undone.
            if (plan.HasAuditedSteps && step.StepAudit.IsAudited)
            {
                var refusal = await RecordAuditAsync(
                    plan, dispatcher, context, cursor, step, scope, capabilityId, ct)
                    .ConfigureAwait(false);

                if (refusal is not null)
                {
                    failure = refusal;
                    break;
                }
            }

            i = SignalTargetOf(step) ?? i + 1;
        }

        return new RangeOutcome(failure, completed, suspended, wake);
    }

    /// <summary>
    /// Stage 1: takes a permit for <paramref name="capabilityId"/>, or produces the refusal.
    /// </summary>
    /// <param name="policy">The resolved policy. <c>HasRateLimit</c> is true.</param>
    /// <param name="context">The execution, for the tenant and principal the scope keys by.</param>
    /// <param name="capabilityId">The dependency whose budget is being spent.</param>
    /// <param name="ct">The caller's token.</param>
    /// <returns><c>null</c> when the caller is admitted, otherwise the error the step fails with.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Three ways not to be admitted, and all three refuse.</strong> No store was
    /// registered; the store answered no; the store did not answer. The first two are obvious.
    /// The third is the one worth stating: a limiter that cannot reach its server does not know
    /// whether this caller is inside the budget, and admitting on doubt turns an outage of the
    /// limiter into an unbounded flood of whatever it was bounding — see
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0040-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md">ADR-0040</a>
    /// §2.2.
    /// </para>
    /// <para>
    /// The refusal error is the same one in all three cases only in the second; the other two
    /// name the cause, because "the limiter is not wired up" and "you are going too fast" lead
    /// to different repairs and an operator reading a `429` should not have to guess which.
    /// </para>
    /// </remarks>
    private async ValueTask<Error?> AdmitAsync(
        StepPolicy policy,
        FlowExecutionContext context,
        string capabilityId,
        CancellationToken ct)
    {
        if (_rateLimiter is null)
        {
            return FlowErrors.RateLimiterUnavailable(capabilityId);
        }

        var key = PolicyKeys.RateLimit(
            capabilityId, policy.RateScope, context.TenantId, context.Principal?.Identity?.Name);

        var verdict = await _rateLimiter
            .TryAcquireAsync(key, policy.Permits, policy.RateWindow, ct)
            .ConfigureAwait(false);

        if (verdict.IsFailure)
        {
            return FlowErrors.RateLimiterUnavailable(capabilityId, verdict.Error);
        }

        if (verdict.Value.Admitted)
        {
            // Counted on admission as well as on refusal, for the reason the breaker is:
            // docs/10 §9 labels flowx_policy_invocations_total by outcome, and a refusal rate
            // needs the callers who got in as its denominator.
            PolicyApplied(StepPolicy.RateLimitKind, AdmissionStage, capabilityId, PolicyMetrics.OkOutcome);

            return null;
        }

        PolicyApplied(StepPolicy.RateLimitKind, AdmissionStage, capabilityId, PolicyMetrics.RejectedOutcome);
        PolicyMetrics.RateLimitRefused(policy.RateScope.ToString(), context.TenantId);

        return FlowErrors.RateLimited(capabilityId, verdict.Value.RetryAfter);
    }

    /// <summary>What <see cref="BeginIdempotentAsync"/> found.</summary>
    /// <param name="Refusal">The error the step fails with, or <c>null</c>.</param>
    /// <param name="Replayed">Whether a recorded result was restored and the dispatch skipped.</param>
    /// <param name="Key">The claimed key, when this caller now holds it.</param>
    private readonly record struct IdempotentBegin(Error? Refusal, bool Replayed, string? Key);

    /// <summary>
    /// Stage 3, opening: claims the key, or replays what a previous caller recorded under it.
    /// </summary>
    private async ValueTask<IdempotentBegin> BeginIdempotentAsync(
        StepPolicy policy,
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        FlowContext scope,
        string capabilityId,
        CancellationToken ct)
    {
        if (_idempotency is null)
        {
            return new IdempotentBegin(FlowErrors.IdempotencyStoreUnavailable(capabilityId), false, null);
        }

        var key = PolicyKeys.Idempotency(
            context.IdempotencyKey, capabilityId, policy.IdempotencyScope, context.TenantId);

        // The claim is bounded by the flow's own remaining budget rather than by the declared
        // window. A node that takes a key and then dies must not wedge every repeat of it for a
        // declared PT24H, and an execution that cannot outlive its deadline cannot legitimately
        // hold a claim past it either.
        var lease = context.Deadline - context.UtcNow;

        if (lease <= TimeSpan.Zero)
        {
            lease = MinimumIdempotencyLease;
        }

        var began = await _idempotency
            .BeginAsync(key, policy.IdempotencyWindow!.Value, lease, ct)
            .ConfigureAwait(false);

        if (began.IsFailure)
        {
            return new IdempotentBegin(
                FlowErrors.IdempotencyStoreUnavailable(capabilityId, began.Error), false, null);
        }

        switch (began.Value.State)
        {
            case IdempotencyState.Completed:
                // The one path that restores. RestoreState is the only call that turns stored
                // JSON back into the typed values the steps after this one bind to, and only
                // generated code can make it — the same reason DescribeStep wrote the document.
                dispatcher.RestoreState(scope, began.Value.Record ?? string.Empty);

                PolicyApplied(
                    StepPolicy.IdempotencyKind, IntegrityStage, capabilityId, PolicyMetrics.ReplayedOutcome);
                PolicyMetrics.IdempotencyReplayed(capabilityId, policy.IdempotencyScope.ToString());

                return new IdempotentBegin(null, true, null);

            case IdempotencyState.InFlight:
                PolicyApplied(
                    StepPolicy.IdempotencyKind, IntegrityStage, capabilityId, PolicyMetrics.RejectedOutcome);

                return new IdempotentBegin(
                    FlowErrors.IdempotencyInProgress(capabilityId, began.Value.RetryAfter), false, null);

            default:
                PolicyApplied(
                    StepPolicy.IdempotencyKind, IntegrityStage, capabilityId, PolicyMetrics.OkOutcome);

                return new IdempotentBegin(null, false, key);
        }
    }

    /// <summary>
    /// Stage 3, closing: records what the step produced, or gives the key back.
    /// </summary>
    /// <returns>
    /// The error the step fails with, or <c>null</c>. Non-null only on the success path — a step
    /// that already failed has nothing further to report, and its key is simply released.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Only a success is recorded</strong>
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0041-an-idempotency-record-is-keyed-by-the-invocations-key.md">ADR-0041</a>
    /// §2.3). A recorded failure would be replayed for the whole declared window, so one
    /// transient outage at the moment a key was first presented would make that key unusable
    /// for as long as the author declared — and the caller's remedy, presenting it again, is
    /// exactly what would keep failing. It is <c>docs/10</c> §8's "negative caching: off" one
    /// stage earlier.
    /// </para>
    /// <para>
    /// <strong>The document goes out through <c>TryToReplayableJson</c> and never through
    /// <c>ToJson</c></strong>
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0042-a-recorded-result-is-replayed-only-when-recording-lost-nothing.md">ADR-0042</a>).
    /// A payload the redaction pass had to change is not what the step produced, and replaying
    /// it would hand a later step the literal <c>[redacted]</c> as if it were the value. There
    /// is no third option here: the record is honest or the step fails.
    /// </para>
    /// </remarks>
    private async ValueTask<Error?> EndIdempotentAsync(
        string key,
        StepPolicy policy,
        IStepDispatcher dispatcher,
        FlowContext scope,
        string capabilityId,
        int index,
        bool succeeded,
        CancellationToken ct)
    {
        if (_idempotency is null)
        {
            return null;
        }

        if (!succeeded)
        {
            await _idempotency.AbandonAsync(key, ct).ConfigureAwait(false);

            return null;
        }

        var described = dispatcher.DescribeStep(index, scope).StateBag;

        if (!described.TryToReplayableJson(out var record))
        {
            // Two causes, one refusal. Either the flow marks a member [Sensitive] and the
            // document came back with a placeholder where a value was, or the dispatcher
            // describes no state bag at all. Both mean the same thing to this policy: there is
            // nothing here that could honestly be replayed, and recording it anyway is how the
            // second caller gets a fabricated answer.
            await _idempotency.AbandonAsync(key, ct).ConfigureAwait(false);

            return FlowErrors.IdempotencyNotReplayable(capabilityId, described.IsEmpty);
        }

        var completed = await _idempotency
            .CompleteAsync(key, record, policy.IdempotencyWindow!.Value, ct)
            .ConfigureAwait(false);

        return completed.IsFailure
            ? FlowErrors.IdempotencyStoreUnavailable(capabilityId, completed.Error)
            : null;
    }

    /// <summary>
    /// The floor on an idempotency claim's lease, for an execution whose budget has already gone.
    /// </summary>
    /// <remarks>
    /// A claim of zero would be released by the store before the step it stands for finished, so
    /// two concurrent callers of an over-budget flow would both execute — which is the one thing
    /// the in-flight state exists to prevent. Small, because such an execution is about to fail
    /// its deadline check anyway.
    /// </remarks>
    private static readonly TimeSpan MinimumIdempotencyLease = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Dispatches one attempt at a step through its declared stage-4 policies.
    /// </summary>
    /// <param name="dispatcher">Invokes the capability.</param>
    /// <param name="context">The flow's context, for the deadline the timeout is clamped to.</param>
    /// <param name="step">The node being run, for the capability its gates are keyed by.</param>
    /// <param name="policy">The resolved policy. Never <see cref="StepPolicy.None"/>.</param>
    /// <param name="index">Position in the plan's step graph.</param>
    /// <param name="scope">The context the capability sees — an iteration's, inside a loop.</param>
    /// <param name="ct">The caller's token, kept distinguishable from the timeout's.</param>
    /// <remarks>
    /// <para>
    /// <strong>The nesting is
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0024-stage-four-is-a-fixed-nesting.md">ADR-0024</a>'s,
    /// read from the inside out.</strong> The retry is the caller's loop; what is left here is
    /// <c>CircuitBreaker { Bulkhead { Timeout { capability } } }</c>. The breaker is asked
    /// first so that an open one refuses without taking a permit — the other way round, a
    /// dependency that is down would hold every permit in the pool for as long as it takes each
    /// caller to be told the breaker is open, which turns the isolation policy into the thing
    /// that spreads the outage.
    /// </para>
    /// <para>
    /// <strong>A refusal is an <see cref="Error"/> and never an exception.</strong>
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0007-result-over-exceptions.md">ADR-0007</a>
    /// makes a business outcome a value; a policy refusal is not a business outcome, but it is
    /// an outcome the flow has to handle — the compensable steps behind it unwind exactly as
    /// they would behind a declined payment — so it arrives on the same path as one. Throwing
    /// would put it on the defect path beside <c>capability.unhandled</c>, where a saga's
    /// unwind is a rescue rather than the design.
    /// </para>
    /// <para>
    /// <strong>Only calls that were made are recorded against the breaker.</strong> A step the
    /// breaker itself refused, one the bulkhead turned away, and one whose budget had already
    /// gone before the dispatch all say nothing about the dependency. Counting them would make
    /// an open breaker self-sustaining, which is a breaker that never closes.
    /// </para>
    /// </remarks>
    private async ValueTask<StepOutcome> DispatchPolicedAsync(
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        StepNode step,
        StepPolicy policy,
        int index,
        FlowContext scope,
        CancellationToken ct)
    {
        var capabilityId = step.Identity;

        if (policy.HasBreaker)
        {
            if (!Breaker(capabilityId, context.TenantId).TryEnter(policy, _clock.UtcNow, out var until))
            {
                PolicyApplied(StepPolicy.CircuitBreakerKind, capabilityId, PolicyMetrics.OpenOutcome);

                return StepOutcome.Failed(FlowErrors.CircuitOpen(capabilityId, until));
            }

            // Counted on admission as well as on refusal. docs/10 §9 labels this counter by
            // outcome, and a refusal rate needs the calls the breaker let through as its
            // denominator — "this breaker refused forty" means nothing without them.
            PolicyApplied(StepPolicy.CircuitBreakerKind, capabilityId, PolicyMetrics.OkOutcome);
        }

        BulkheadGate? bulkhead = null;

        if (policy.HasBulkhead)
        {
            bulkhead = Bulkhead(capabilityId, policy);

            if (!await bulkhead.EnterAsync(ct).ConfigureAwait(false))
            {
                PolicyApplied(StepPolicy.BulkheadKind, capabilityId, PolicyMetrics.RejectedOutcome);

                return StepOutcome.Failed(
                    FlowErrors.BulkheadRejected(capabilityId, policy.MaxConcurrency));
            }

            PolicyApplied(StepPolicy.BulkheadKind, capabilityId, PolicyMetrics.OkOutcome);
        }

        try
        {
            // docs/10 §11: "timeout longer than the flow deadline — the step is killed by the
            // deadline anyway; the timeout is a lie". Taking the minimum is what makes that
            // sentence false rather than merely discouraged.
            var budget = policy.EffectiveTimeout(context.UtcNow, context.Deadline);

            if (budget is { } expired && expired <= TimeSpan.Zero)
            {
                // Refused rather than started. A step with no budget cannot finish inside one,
                // and dispatching it would cost the dependency a call whose answer is thrown
                // away — which is the one thing a timeout exists to stop.
                PolicyApplied(StepPolicy.TimeoutKind, capabilityId, PolicyMetrics.TimedOutOutcome);

                return StepOutcome.Failed(FlowErrors.StepTimedOut(capabilityId, expired));
            }

            var succeeded = false;
            CancellationTokenSource? timeout = null;

            try
            {
                var token = ct;

                if (budget is { } granted)
                {
                    timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(granted);
                    token = timeout.Token;
                }

                // Stage 5, and the whole of where it goes: outside the dispatch and inside
                // stage 4, which is the insertion point ADR-0025 §2.5 named before there was
                // anything to insert. Inside the timeout on purpose — a store round trip is
                // part of what the author's budget for this step has to cover — and inside the
                // breaker and the bulkhead, so a cache hit still counts as a call that
                // succeeded and a refused caller never reaches the store at all.
                var outcome = policy.HasCache && _cache is not null
                    ? await DispatchCachedAsync(dispatcher, context, step, policy, index, scope, token)
                        .ConfigureAwait(false)
                    : await dispatcher.ExecuteAsync(index, scope, token).ConfigureAwait(false);

                succeeded = outcome.Error is null;

                if (budget is not null)
                {
                    // The timeout was armed and the call finished inside it. That is the
                    // denominator for the row below, and without it "this capability timed
                    // out 12 times" has no scale.
                    PolicyApplied(StepPolicy.TimeoutKind, capabilityId, PolicyMetrics.OkOutcome);
                }

                return outcome;
            }
            catch (OperationCanceledException)
                when (timeout is not null && timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // The timeout fired and the caller did not. Distinguishing the two is what
                // keeps "this dependency is slow" from being reported as "the caller went
                // away", and it is why the linked source is kept rather than passing `ct`
                // through a `CancelAfter` on a source the caller owns.
                PolicyApplied(StepPolicy.TimeoutKind, capabilityId, PolicyMetrics.TimedOutOutcome);

                return StepOutcome.Failed(FlowErrors.StepTimedOut(capabilityId, budget!.Value));
            }
            finally
            {
                // A capability that threw leaves `succeeded` false, which is right: a defect is
                // a failure of the call. A cancelled caller is excluded, because a deployment
                // draining a node must not open every breaker on its way out.
                if (policy.HasBreaker && !ct.IsCancellationRequested)
                {
                    Breaker(capabilityId, context.TenantId).Record(policy, _clock.UtcNow, succeeded);
                }

                timeout?.Dispose();
            }
        }
        finally
        {
            bulkhead?.Exit();
        }
    }

    /// <summary>
    /// Writes the audit record for a step that succeeded, or reports why the flow must stop.
    /// </summary>
    /// <param name="plan">The compiled flow, for the identity and version the record carries.</param>
    /// <param name="dispatcher">The only thing that can describe the step's payload.</param>
    /// <param name="context">The execution's context: the principal, the tenant, the keys.</param>
    /// <param name="cursor">The journal, whose frontier says whether this execution resumed.</param>
    /// <param name="step">The node that just succeeded.</param>
    /// <param name="scope">The context the step ran under — an iteration's, inside a loop.</param>
    /// <param name="capabilityId">What was invoked, for the record and for the metric.</param>
    /// <param name="ct">The caller's token.</param>
    /// <returns>The error that must fail the flow, or <c>null</c> when the record was written.</returns>
    /// <remarks>
    /// <para>
    /// <strong>A failure here is the flow's failure, and that is the one place this engine
    /// refuses to degrade.</strong> Every other plugin seam on this path falls back to the
    /// behaviour that existed before it — an unreachable cache dispatches, an unset alert sink
    /// reports nowhere — because in each case the fallback is the honest older behaviour. Here
    /// the fallback would be a step that happened with no record that it happened, and
    /// the deleted <c>FLOWX1032</c>'s third remedy was explicit that such a flow should
    /// not ship: "a regulated write whose audit record is the reason it is allowed to happen".
    /// So a missing sink and a sink that threw both fail the step, and the compensable work
    /// behind them unwinds.
    /// </para>
    /// <para>
    /// <strong>The payload is the journal's payload, redacted twice.</strong> The dispatcher
    /// composes it out of the step's input and its result and hands it the flow's
    /// <c>SensitiveMembers</c> together with the policy's <c>redact</c> list, so the record
    /// goes through the one redaction pass with a longer list of names and can only ever be
    /// less revealing than the row beside it. There is no second exit from a value: the record
    /// carries a <see cref="JournalPayload"/> and a sink reads it through
    /// <see cref="JournalPayload.ToJson"/> like everything else.
    /// </para>
    /// </remarks>
    private async ValueTask<Error?> RecordAuditAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        JournalCursor cursor,
        StepNode step,
        FlowContext scope,
        string capabilityId,
        CancellationToken ct)
    {
        var audit = step.StepAudit;

        if (_audit is null)
        {
            return FlowErrors.AuditSinkNotConfigured(plan.Flow.Id, capabilityId, audit.Category!);
        }

        JournalPayload payload;

        try
        {
            payload = dispatcher.DescribeAudit(step.Index, scope, audit.Redact);
        }
#pragma warning disable CA1031 // Unlike the cache's version of this catch, the failure is not
        catch (Exception exception) //   absorbed: a payload that cannot be described is a
        {                           //   record that cannot be written, and this is the seam
            return FlowErrors       //   that does not degrade.
                .AuditNotRecorded(capabilityId, audit.Category!, exception);
        }
#pragma warning restore CA1031

        var record = new AuditRecord
        {
            Category = audit.Category!,
            FlowId = plan.Flow.Id,
            FlowVersion = plan.Flow.Version,
            InstanceId = cursor.Run?.InstanceId.ToString(),
            CorrelationId = context.CorrelationId,
            IdempotencyKey = context.IdempotencyKey,
            TenantId = context.TenantId,
            StepIndex = step.Index,
            CapabilityId = capabilityId,
            CapabilityVersion = step.Capability?.Version,
            RecordedAt = _clock.UtcNow,
            Authority = AuthorityOf(context, cursor),
            Principal = context.Principal?.Identity?.IsAuthenticated == true
                ? context.Principal.Identity.Name
                : null,
            Stance = step.StepAuthorization.Stance,
            Permission = step.StepAuthorization.Value,
            Payload = payload,
        };

        try
        {
            await _audit.WriteAsync(record, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller went away, not the sink. Propagated so the step loop's own handler
            // reports it as a cancellation rather than as an audit that could not be written.
            throw;
        }
#pragma warning disable CA1031 // A sink is somebody else's code and may throw anything; what
        catch (Exception exception) //   it must not do is let the flow continue as though the
        {                           //   record existed.
            return FlowErrors.AuditNotRecorded(capabilityId, audit.Category!, exception);
        }
#pragma warning restore CA1031

        PolicyApplied(
            StepAudit.AuditKind, ConsistencyStage, capabilityId, PolicyMetrics.RecordedOutcome);

        return null;
    }

    /// <summary>
    /// Whose authority a step ran under, from what the invocation already carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is ADR-0028 §3's first negative, answered.</strong> That record accepted
    /// that "a durable flow's authorisation is discontinuous across a wait … an auditor
    /// reconstructing 'who authorised this transfer' must read two events. Nothing yet writes
    /// those events." These are the events, and this is the field that makes the two readings
    /// distinguishable without the reader having to know where the flow's waits are.
    /// </para>
    /// <para>
    /// <strong>The frontier is what separates a starter from a deliverer.</strong> An execution
    /// that rehydrated a committed history is a resumption, and ADR-0028 §2.2 says a resumption
    /// carrying a principal can only have got it from <c>FlowHost.SignalAsync</c> — the journal
    /// row deliberately keeps no claims, so there is nowhere else for one to have come from.
    /// An execution with no frontier is the one that started the instance.
    /// </para>
    /// <para>
    /// <strong><c>IsContinuation</c> is asked first, and that ordering is the control.</strong>
    /// A timer sweep and a recovery scan are the platform continuing an instance it already
    /// admitted; they carry no principal by construction (§2.3 — it is set in one place and
    /// only where there is neither a signal nor a principal). Asking about the principal first
    /// would file them under <see cref="AuditAuthority.Anonymous"/> and make "a step ran for
    /// nobody" ambiguous between a public step and a sweep.
    /// </para>
    /// </remarks>
    private static AuditAuthority AuthorityOf(FlowExecutionContext context, JournalCursor cursor)
    {
        if (context.IsContinuation)
        {
            return AuditAuthority.Platform;
        }

        if (context.Principal?.Identity?.IsAuthenticated != true)
        {
            return AuditAuthority.Anonymous;
        }

        return cursor.Run?.Frontier is null ? AuditAuthority.Starter : AuditAuthority.Deliverer;
    }

    /// <summary>
    /// Runs one attempt at a step through its declared <c>Cache</c>: consult, dispatch on a
    /// miss, and hold what the step produced.
    /// </summary>
    /// <param name="dispatcher">Invokes the capability, and is the only thing that knows its types.</param>
    /// <param name="context">The execution's context, for the tenant and the principal a key is scoped by.</param>
    /// <param name="step">The node being run, for the capability the entry is keyed by.</param>
    /// <param name="policy">The resolved policy. <see cref="StepPolicy.HasCache"/> is true.</param>
    /// <param name="index">Position in the plan's step graph.</param>
    /// <param name="scope">The context the capability sees — an iteration's, inside a loop.</param>
    /// <param name="ct">The attempt's token: the caller's, narrowed by any armed timeout.</param>
    /// <remarks>
    /// <para>
    /// <strong>Every failure here degrades to a dispatch.</strong> A store that is down, a key
    /// that cannot be built, an entry that cannot be read back — each means the capability is
    /// called, which is what the step did before anything cached it. ADR-0025 §2.3's argument
    /// for skipping stage 5 entirely was that "an unconsulted cache means the call happens.
    /// Slower, never wrong"; that is now this method's failure mode rather than its behaviour.
    /// </para>
    /// <para>
    /// <strong>A hit is put back through <c>RestoreState</c>, which is not a convenience.</strong>
    /// The entry is composed as a one-member state-bag document, so the code that reads it back
    /// is the code a resumed instance already uses. There is one deserialiser for a stored
    /// contract value in this runtime, and a cache that had its own would be a second thing to
    /// keep in step with the generated context.
    /// </para>
    /// <para>
    /// <strong>Only a success is stored.</strong> Caching a failure is <c>docs/10 §8</c>'s
    /// "negative caching: off — stale failures are worse than a retry", and it is also what
    /// would make a cache and a circuit breaker disagree: the breaker is there to stop calling
    /// a dependency that is failing, and a cached failure would keep answering for it long
    /// after it recovered.
    /// </para>
    /// </remarks>
    private async ValueTask<StepOutcome> DispatchCachedAsync(
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        StepNode step,
        StepPolicy policy,
        int index,
        FlowContext scope,
        CancellationToken ct)
    {
        var capabilityId = step.Identity;
        var scopeLabel = CacheScopeLabel(policy.CacheScope);
        var key = CacheKey(dispatcher, context, step, policy, index, scope);

        if (key is null)
        {
            // Nothing keyable. Either the dispatcher describes no input for this step, or the
            // document that would key it came out redacted — see CacheKey. Not a miss: a miss
            // is a cache that was asked, and this one was not.
            return await dispatcher.ExecuteAsync(index, scope, ct).ConfigureAwait(false);
        }

        if (await ReadCacheAsync(key, ct).ConfigureAwait(false) is { } entry &&
            TryRestore(dispatcher, scope, entry))
        {
            PolicyMetrics.CacheHit(capabilityId, scopeLabel);
            PolicyApplied(StepPolicy.CacheKind, EfficiencyStage, capabilityId, PolicyMetrics.OkOutcome);

            return StepOutcome.Success;
        }

        PolicyMetrics.CacheMiss(capabilityId, scopeLabel);
        PolicyApplied(StepPolicy.CacheKind, EfficiencyStage, capabilityId, PolicyMetrics.MissedOutcome);

        var outcome = await dispatcher.ExecuteAsync(index, scope, ct).ConfigureAwait(false);

        if (outcome.IsSuccess)
        {
            await WriteCacheAsync(dispatcher, key, policy, index, scope, ct).ConfigureAwait(false);
        }

        return outcome;
    }

    /// <summary>
    /// What this step's result is held under, or <c>null</c> when it must not be held at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>docs/10 §8</c>'s key, with the one component it names that this runtime can build for
    /// each scope: <em>capability id + capability version + input document + tenant + principal
    /// permission set</em>. The version is in it because a capability that changed its answer
    /// for the same input is a different capability from the cache's point of view, and serving
    /// the old one across a deployment is the failure a TTL cannot bound.
    /// </para>
    /// <para>
    /// <strong>The input arrives already redacted, and that is what decides the null.</strong>
    /// The dispatcher answers with a <see cref="JournalPayload"/> whose only exit replaces every
    /// <c>[Sensitive]</c> member with <see cref="JournalPayload.Redacted"/>. Two transfers from
    /// two different accounts would therefore key identically, and the second caller would be
    /// served the first one's result — a cross-principal leak of exactly the shape
    /// <c>docs/10 §2</c>'s "cache before authorisation" row describes, arriving through the key
    /// instead of through the ordering. So a key document carrying the placeholder is refused,
    /// and the step is dispatched. There is no arrangement in which a marked member is read
    /// unredacted to key with: that would be the second exit from <c>JournalPayload</c> that
    /// WP-59 was careful not to open.
    /// </para>
    /// <para>
    /// <strong>Hashed, and the hash is of the whole document.</strong> An input contract can be
    /// arbitrarily large and a store's key length is not FlowX's to assume, so the components
    /// are hashed rather than concatenated. SHA-256 because a cache key that collides serves one
    /// caller another's data, which puts this in the same class as the redaction above rather
    /// than in the class of a hash table's bucket function.
    /// </para>
    /// </remarks>
    private static string? CacheKey(
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        StepNode step,
        StepPolicy policy,
        int index,
        FlowContext scope)
    {
        string? input;

        try
        {
            input = dispatcher.DescribeCacheKey(index, scope).ToJson();
        }
#pragma warning disable CA1031 // Generated code, and the same stance every other call into it
        catch (Exception)      //   takes: a dispatcher that cannot describe an input is a
        {                      //   defect, and it must not turn a working step into a failed
            return null;       //   one. It turns a cached step into an uncached one.
        }
#pragma warning restore CA1031

        if (input is null || input.Contains(JournalPayload.Redacted, StringComparison.Ordinal))
        {
            return null;
        }

        var material = new StringBuilder()
            .Append(step.Identity).Append(KeySeparator)
            .Append(step.Capability?.Version).Append(KeySeparator)
            .Append(policy.CacheScope == CacheScope.Global ? null : context.TenantId).Append(KeySeparator)
            .Append(policy.CacheScope == CacheScope.Principal ? PermissionSet(context.Principal) : null)
            .Append(KeySeparator)
            .Append(input)
            .ToString();

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>
    /// The principal's permission-bearing claims, ordered, as a key component.
    /// </summary>
    /// <remarks>
    /// <c>docs/10 §8</c>'s "principal permission set", and read from exactly the claim types
    /// <see cref="StepAuthorization.PermissionClaimTypes"/> names — so what a cache key is
    /// scoped by and what an authorisation stance is decided from are the same set of claims,
    /// read the same way. A cache scoped by a permission set the engine derived differently
    /// from the one that authorised the step would be a cache keyed on a fiction.
    /// <para>
    /// Sorted, because a token's claim order is the issuer's business and two requests from one
    /// caller must not key differently for it.
    /// </para>
    /// </remarks>
    private static string PermissionSet(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return string.Empty;
        }

        var granted = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var claim in principal.Claims)
        {
            if (Array.IndexOf(StepAuthorization.PermissionClaimTypes, claim.Type) < 0)
            {
                continue;
            }

            foreach (var permission in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                granted.Add(permission);
            }
        }

        return string.Join(' ', granted);
    }

    /// <summary>Asks the cache, and treats every refusal as a miss.</summary>
    private async ValueTask<string?> ReadCacheAsync(string key, CancellationToken ct)
    {
        var read = await _cache!.GetAsync(key, ct).ConfigureAwait(false);

        return read.IsSuccess ? read.Value.Value : null;
    }

    /// <summary>Puts a cached document back into the bag, or reports that it could not.</summary>
    /// <remarks>
    /// The one place a hit can still become a miss. A document written by an older build of the
    /// flow may name a contract this build no longer has — <c>RestoreState</c> ignores an
    /// unknown member, so the bag would be left without the value the next step binds to.
    /// Dispatching is the only safe answer, and it is the answer to a failed read too.
    /// </remarks>
    private static bool TryRestore(IStepDispatcher dispatcher, FlowContext scope, string entry)
    {
        try
        {
            dispatcher.RestoreState(scope, entry);
            return true;
        }
#pragma warning disable CA1031 // A dispatcher that cannot read back what it wrote is a defect,
        catch (Exception)      //   and the flow must not fail for it: the capability is still
        {                      //   there to be called, which is what an unusable cache means.
            return false;
        }
#pragma warning restore CA1031
    }

    /// <summary>Holds what the step produced, and never holds a document that was redacted.</summary>
    /// <remarks>
    /// The write half of <see cref="CacheKey"/>'s argument, and the reason it is enforced twice
    /// rather than once. A key is refused when it was redacted because it would collide; an
    /// entry is refused when it was redacted because a hit would hand the flow
    /// <see cref="JournalPayload.Redacted"/> where a capability's answer should be, and the
    /// steps after it would bind to a value nothing produced. Between the two, a
    /// <c>[Sensitive]</c> member cannot reach a cache and cannot come back out of one.
    /// </remarks>
    private async ValueTask WriteCacheAsync(
        IStepDispatcher dispatcher,
        string key,
        StepPolicy policy,
        int index,
        FlowContext scope,
        CancellationToken ct)
    {
        string? entry;

        try
        {
            entry = dispatcher.DescribeCacheEntry(index, scope).ToJson();
        }
#pragma warning disable CA1031 // As above: a dispatcher that cannot describe a result leaves
        catch (Exception)      //   the step uncached rather than failing it. The step has
        {                      //   already succeeded by this point, so the alternative would be
            return;            //   failing a flow over the bookkeeping that follows it.
        }
#pragma warning restore CA1031

        if (entry is null || entry.Contains(JournalPayload.Redacted, StringComparison.Ordinal))
        {
            return;
        }

        await _cache!.SetAsync(key, entry, policy.CacheTtl!.Value, ct).ConfigureAwait(false);
    }

    /// <summary>The declared cache scope as a metric label, without allocating one.</summary>
    private static string CacheScopeLabel(CacheScope scope) => scope switch
    {
        CacheScope.Principal => nameof(CacheScope.Principal),
        CacheScope.Global => nameof(CacheScope.Global),
        _ => nameof(CacheScope.Tenant),
    };

    /// <summary>
    /// Counts one application of a stage-4 policy, for <c>docs/10 §9</c>'s
    /// <c>flowx_policy_invocations_total</c>.
    /// </summary>
    /// <param name="kind">The descriptor kind, which is also the metric's <c>policy</c> label.</param>
    /// <param name="capabilityId">The dependency the step invokes.</param>
    /// <param name="outcome">What the policy decided.</param>
    /// <remarks>
    /// The stage-4 overload. Its <c>stage</c> label is a literal because every kind that reaches
    /// it is stage 4; stage 1 and stage 3 call the four-argument form below with their own
    /// names, which is what <c>ADR-0025</c>'s successor predicted a landing stage would do —
    /// add its own call site rather than make this one derive a stage from the descriptor the
    /// resolved <c>StepPolicy</c> exists to avoid reading.
    /// </remarks>
    private static void PolicyApplied(string kind, string capabilityId, string outcome) =>
        PolicyMetrics.Applied(kind, ResilienceStage, capabilityId, outcome);

    /// <summary>Counts one application of a policy outside stage 4.</summary>
    /// <param name="kind">The descriptor kind, which is also the metric's <c>policy</c> label.</param>
    /// <param name="stage">The stage it belongs to, by name.</param>
    /// <param name="capabilityId">The dependency the step invokes.</param>
    /// <param name="outcome">What the policy decided.</param>
    /// <remarks>
    /// The overload above kept its literal stage rather than calling this one with
    /// <see cref="ResilienceStage"/>, because the four resilience call sites are the hot ones
    /// and each already knows its stage at the point it is written. This one exists because
    /// stages 1, 3, 5 and 7 now reach the same counter, which is the whole point of §9's
    /// <c>stage</c> label — a label that distinguished nothing while one stage executed.
    /// </remarks>
    private static void PolicyApplied(
        string kind, string stage, string capabilityId, string outcome) =>
        PolicyMetrics.Applied(kind, stage, capabilityId, outcome);

    /// <summary>Stage 4, as a metric label.</summary>
    private static readonly string ResilienceStage = nameof(PolicyStage.Resilience);

    /// <summary>Stage 1, as a metric label.</summary>
    private static readonly string AdmissionStage = nameof(PolicyStage.Admission);

    /// <summary>Stage 3, as a metric label.</summary>
    private static readonly string IntegrityStage = nameof(PolicyStage.Integrity);

    /// <summary>Stage 5, as a metric label.</summary>
    private static readonly string EfficiencyStage = nameof(PolicyStage.Efficiency);

    /// <summary>Stage 7, as a metric label.</summary>
    private static readonly string ConsistencyStage = nameof(PolicyStage.Consistency);

    /// <summary>The separator between a cache key's components.</summary>
    /// <remarks>
    /// A unit separator rather than a colon or a slash, so no component can forge a boundary: a
    /// capability id containing the delimiter would otherwise let two different keys render
    /// identically, and a cache key that collides serves one caller another's data. U+001F
    /// cannot appear in an id (<c>Identifiers</c> refuses it), in a JSON document (it is escaped
    /// on the way in) or in a permission claim that any issuer emits.
    /// </remarks>
    private const char KeySeparator = '\u001f';

    /// <summary>This engine's breaker for one capability, created on first use.</summary>
    /// <remarks>
    /// <strong>Keyed by capability alone, or by capability and tenant.</strong> <c>docs/10 §6</c>
    /// describes a composite key — <c>Capability | Downstream | Tenant | Partition</c> — of
    /// which the tenant is the component <c>docs/16 §4</c> asks for by name: "one tenant's bad
    /// downstream does not trip everyone". The widening is opt-in because it trades protection
    /// for isolation. One breaker per capability opens on the first tenant's failures and
    /// spares every other tenant the calls; one per tenant makes each tenant discover the
    /// outage for itself, which is right when the dependency is per tenant and wrong when it is
    /// shared. Off, the key is the capability id and nothing allocates.
    /// </remarks>
    private CircuitBreakerState Breaker(string capabilityId, string? tenantId) =>
        _breakersPerTenant && tenantId is { Length: > 0 } tenant
            ? _breakers.GetOrAdd(
                PolicyKeys.Breaker(capabilityId, tenant),
                static key => new CircuitBreakerState(key))
            : _breakers.GetOrAdd(capabilityId, static key => new CircuitBreakerState(key));

    /// <summary>This engine's bulkhead for one capability, created on first use.</summary>
    /// <remarks>
    /// The bound comes from whichever declaration reached the engine first — see
    /// <see cref="BulkheadGate"/>, which says why the pool is per dependency rather than per
    /// declaration. A static factory rather than a closure, so the lookup allocates nothing.
    /// </remarks>
    private BulkheadGate Bulkhead(string capabilityId, StepPolicy policy) =>
        _bulkheads.GetOrAdd(
            capabilityId,
            static (key, declared) =>
                new BulkheadGate(declared.MaxConcurrency, declared.QueueDepth, key),
            policy);

    /// <summary>
    /// Where control goes when a suspension point's signal <em>arrives</em>, or <c>null</c>
    /// when that is simply the next index.
    /// </summary>
    /// <remarks>
    /// Non-null only for an <see cref="StepKind.AwaitSignal"/> whose author declared an
    /// <c>.OnTimeout(...)</c>. That block is laid out immediately after the wait — it is the
    /// one of the two paths that has an end to jump over — so the satisfied path is the one
    /// that needs a target, and this is where a delivered signal skips the escalation.
    /// </remarks>
    private static int? SignalTargetOf(StepNode step) =>
        step.Kind == StepKind.AwaitSignal ? step.Target : null;

    /// <summary>What the loop does when it arrives at a suspension point.</summary>
    private enum WaitVerdict
    {
        /// <summary>
        /// The wait is over: the signal is in this invocation, or the timer has come due. The
        /// step takes the ordinary path — dispatch, commit, move on.
        /// </summary>
        Satisfied,

        /// <summary>The wait is still running. The instance parks, holding nothing.</summary>
        Waiting,

        /// <summary>The wait expired and the author declared what to do instead.</summary>
        Escalated,

        /// <summary>The wait expired and the author declared nothing. The flow fails.</summary>
        Unmet,
    }

    /// <summary>
    /// Decides what a suspension point does on arrival, and delivers the signal when one is
    /// what ends it.
    /// </summary>
    /// <param name="cursor">The instance, and which iteration it is running in.</param>
    /// <param name="context">Where a delivered signal's payload is seeded.</param>
    /// <param name="step">The <see cref="StepKind.AwaitSignal"/> or <see cref="StepKind.Delay"/> node.</param>
    /// <param name="due">When the wait is due, meaningful only for <see cref="WaitVerdict.Waiting"/>.</param>
    /// <remarks>
    /// <para>
    /// <strong>One method for both kinds, because they are one mechanism.</strong> A delay is a
    /// wait with nothing to deliver; a suspension point is a wait with something that can end
    /// it early. Everything else — how the due instant is found, what parking means, what a
    /// second pass over an expired wait does — is identical, and two copies of it would be two
    /// chances to park an instance wrongly.
    /// </para>
    /// <para>
    /// <strong>An expired wait writes no row of its own</strong>, so what says the decision was
    /// already taken is the block: a committed row between the wait and the index a delivered
    /// signal would have jumped to. The two paths rejoin there, so replaying the block is safe
    /// whichever way it originally went — every step in it that committed is skipped, and
    /// control arrives where the signal path would have arrived. Recording the timeout as the
    /// wait's own row instead would put "this step happened" into an operator's history for a
    /// step that did not.
    /// </para>
    /// </remarks>
    private WaitVerdict ResolveWait(
        JournalCursor cursor,
        FlowExecutionContext context,
        StepNode step,
        out DateTimeOffset due)
    {
        due = default;

        if (step.Kind == StepKind.AwaitSignal)
        {
            if (cursor.Run?.TakeSignal(step.SignalType) is { } delivered)
            {
                context.Deliver(delivered);

                return WaitVerdict.Satisfied;
            }

            if (TimeoutBlockWasEntered(cursor, step))
            {
                return WaitVerdict.Escalated;
            }
        }

        var wait = step.Kind == StepKind.AwaitSignal ? step.SignalTimeout!.Value : step.Delay!.Value;

        due = DueAt(cursor, step, wait);

        if (_clock.UtcNow < due)
        {
            return WaitVerdict.Waiting;
        }

        // A delay that has come due is simply over; there is nothing else it could have been
        // waiting for, so it walks on and commits its row like any other step.
        return step.Kind == StepKind.Delay
            ? WaitVerdict.Satisfied
            : step.Target is null ? WaitVerdict.Unmet : WaitVerdict.Escalated;
    }

    /// <summary>
    /// When the wait this step declares is due, in wall-clock terms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The instant the row recorded for <em>this</em> wait, and the clock for one being
    /// reached.</strong> Re-deriving it from the clock on every resume would restart a
    /// seven-day wait every time anything touched the instance — an inert signal, an operator,
    /// a sweep — which is a wait that can be made to last for ever by observing it. Trusting a
    /// recorded instant without checking whose it is would be the opposite failure, and the
    /// worse one: a wait walked through before it began.
    /// </para>
    /// <para>
    /// A wait reached on a fresh instance, and a wait reached past one the instance has already
    /// finished, are the same case and take the same answer.
    /// </para>
    /// </remarks>
    private DateTimeOffset DueAt(JournalCursor cursor, StepNode step, TimeSpan wait) =>
        cursor.Run?.RecordedWake(cursor.Scope, step.Index) ?? _clock.UtcNow + wait;

    /// <summary>
    /// Whether an earlier invocation already found this wait expired and ran into the block it
    /// declared.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The timeout path writes no row of its own — the wait did not happen, so recording it as
    /// having happened would be a lie an operator reads — so what says the decision was taken
    /// is the block: any committed row between the wait and the index a delivered signal would
    /// have jumped to. The two paths rejoin at that index, so replaying the block is safe
    /// whichever way it originally went: every step in it that committed is skipped, and
    /// control arrives where the signal path would have arrived.
    /// </para>
    /// <para>
    /// A wait with no block cannot be here: it fails the flow when it expires, and a failed
    /// flow is terminal.
    /// </para>
    /// </remarks>
    private static bool TimeoutBlockWasEntered(JournalCursor cursor, StepNode step)
    {
        if (cursor.Run?.Frontier is not { } frontier || step.Target is not { } signalled)
        {
            return false;
        }

        for (var index = step.Index + 1; index < signalled; index++)
        {
            if (frontier.IsCommitted(cursor.Scope, index))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Appends one step boundary: the row, the state-bag snapshot and what the step read
    /// that it could not have computed, under the lease's fencing token.
    /// </summary>
    /// <param name="plan">The flow being journaled.</param>
    /// <param name="dispatcher">Asked to describe the step, because it is what knows types.</param>
    /// <param name="context">The execution whose non-determinism capture is taken here.</param>
    /// <param name="scope">The view the step ran under, so what is described is what it saw.</param>
    /// <param name="cursor">Which instance, and which iteration of which loop.</param>
    /// <param name="step">The node that just finished.</param>
    /// <param name="failure">The step's error, or <c>null</c> when it succeeded.</param>
    /// <param name="startedAt">When the attempt began, for the recorded duration.</param>
    /// <param name="capabilityVersion">
    /// The resolved version to record. Supplied by the caller only for a sub-flow, where the
    /// meaningful version is the child flow's rather than a capability's.
    /// </param>
    /// <param name="attempt">
    /// Which attempt at this step this node is committing, counting from one. Added to the
    /// number derived from the committed history, because that history is the frontier this
    /// run began from and does not contain the rows this run has already written.
    /// </param>
    /// <param name="ct">Cancels the store call.</param>
    /// <returns>
    /// <c>null</c> when the row is committed, or the journal's refusal — which ends the flow,
    /// because every refusal means this node is no longer the writer of this instance.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>The key is <c>(instance, scope, step, attempt)</c>, and every part of it is
    /// load-bearing.</strong> <c>scope</c> is there because a <c>ForEach</c> re-enters one
    /// range of the flat step array once per element, so a 500-element loop writes step 7
    /// five hundred times and an append-only table cannot overwrite a row. <c>attempt</c> is
    /// there because a retry writes a new row rather than replacing the failed one, so the
    /// history survives. The compensation stack met the first problem first and answered it
    /// the same way; the journal is not allowed to be less precise than the stack that has to
    /// undo it.
    /// </para>
    /// <para>
    /// <strong>A failed attempt carries no result payload.</strong> An <c>Error</c> is not in
    /// any generated JSON context — <c>JournalPayload.Of</c> requires one, on
    /// purpose — so the row records the outcome, the capability and the timing, and the
    /// error itself reaches the caller and the trace. Journaling errors as payloads is
    /// WP-59's contract question, not something to settle with a reflecting serialiser here.
    /// </para>
    /// <para>
    /// The attempt number is derived from the committed history rather than counted in
    /// memory, for the same reason the resume position is: a number this node is holding is
    /// exactly what is lost when this node is.
    /// </para>
    /// <para>
    /// <strong>A store that is unreachable is not caught here, and that is deliberate.</strong>
    /// <see cref="IFlowJournal"/> draws the line: a refusal is a working store saying no and
    /// arrives as an <see cref="Error"/>; an exception means the store is broken or gone. The
    /// second is not this flow's failure and must not be turned into one — compensating would
    /// undo work a resumed instance would have carried on from, and reporting success would be
    /// worse. Letting it propagate leaves the instance <c>Running</c> with its committed
    /// prefix intact, which is exactly the state a recovery scan exists to find.
    /// </para>
    /// </remarks>
    private async ValueTask<Error?> CommitStepAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        FlowContext scope,
        JournalCursor cursor,
        StepNode step,
        Error? failure,
        DateTimeOffset startedAt,
        string? capabilityVersion,
        int attempt,
        CancellationToken ct)
    {
        var run = cursor.Run!;
        var entry = StepJournalEntry.Nothing;

        if (failure is null)
        {
            try
            {
                entry = dispatcher.DescribeStep(step.Index, scope);
            }
#pragma warning disable CA1031 // A describe that throws is a defect in generated code, and
            catch (Exception exception) //   it must not escape into the caller's consumer
            {                           //   loop — the completed steps still need unwinding.
                return FlowErrors.JournalPayloadFailed(plan.Flow.Id, step.Index, exception);
            }
#pragma warning restore CA1031
        }

        var commit = new StepCommit
        {
            // NextAttempt derives its number from the committed history, which is the frontier
            // this run started from and therefore does not include the rows this run has
            // already written. That was exactly right while a forward step was dispatched once
            // — and a Retry writes a row per attempt, so the second one would collide with the
            // first and the journal would refuse it as a duplicate. The in-flight attempt is
            // added for the same reason CommitCompensationAsync adds its own: the derived
            // number answers "what has survived me", and this answers "what have I done since".
            Key = new StepKey(
                run.InstanceId,
                cursor.Scope,
                step.Index,
                run.NextAttempt(cursor.Scope, step.Index) + attempt - 1),
            Token = run.Token,
            CapabilityId = step.Identity,
            CapabilityVersion = capabilityVersion ?? step.Capability?.Version ?? plan.Flow.Version,
            Outcome = failure is null ? JournalOutcome.Success : JournalOutcome.Failure,
            Result = entry.Result,
            StateBag = entry.StateBag,
            Nondeterminism = context.TakeNondeterminism(),
            Duration = _clock.UtcNow - startedAt,

            // Denormalised, and never read back. A scalar cursor cannot describe a
            // half-completed fork, so the resume position is derived from the rows above;
            // this survives because "roughly where is this stuck instance" is a real question
            // an operator asks of a table.
            ResumeHint = step.Index,

            // The event the step emitted, staged by the same write that records the step.
            // Gated on the plan rather than on the entry, so a flow that emits nothing never
            // reads the field — the bargain HasParallel and HasCompensationPolicies struck.
            Outbox = plan.HasEmit && entry.Event is { } emitted ? [emitted] : [],
        };

        var committed = await run.Journal.CommitAsync(commit, ct).ConfigureAwait(false);

        return committed.IsSuccess ? null : committed.Error;
    }

    /// <summary>
    /// Runs every branch of a fork and applies its merge strategy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This method allocates, and that is the deliberate exception to budget B2.</strong>
    /// A linked <see cref="CancellationTokenSource"/>, a <see cref="Task"/> per branch, and
    /// the awaiters behind them are what concurrency costs; there is no version of running
    /// three things at once that costs nothing. The budget stays a hard zero for the
    /// linear, conditional and switch paths, which never reach this method, and
    /// <c>EngineAllocationTests</c> records the parallel figure as a ceiling rather than
    /// pretending it is zero.
    /// </para>
    /// <para>
    /// <strong>How concurrent the branches actually are.</strong> Each branch is started
    /// eagerly on the calling thread and runs until its first incomplete await, then yields;
    /// the rest interleave on the thread pool. So branches whose steps all complete
    /// synchronously run one after another — they were never going to overlap, and forcing
    /// them onto <see cref="Task.Run(Func{Task})"/> would buy a thread-pool dispatch per
    /// branch to make CPU-bound work contend. Branches that do I/O, which is what a fork is
    /// for, genuinely overlap.
    /// </para>
    /// <para>
    /// <strong>Every branch is drained before this returns, cancelled or not.</strong> That
    /// is not tidiness: the context is pooled, so a branch still writing to it after the
    /// engine has moved on would eventually write into the <em>next</em> flow's context,
    /// which may belong to another tenant. It is also what makes compensation sound —
    /// nothing is still running when the unwind starts.
    /// </para>
    /// </remarks>
    private async ValueTask<RangeOutcome> RunParallelAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        FlowContext scope,
        CompensationStack? compensations,
        StepNode step,
        JournalCursor cursor,
        CancellationToken ct)
    {
        var targets = step.BranchTargets;
        var join = step.Target!.Value;
        var merge = step.Merge;

        // AllSettled cancels nothing, so it needs no source at all. The other three do,
        // and the source is linked so the caller's own cancellation still reaches the
        // branches.
        using var cancellation = merge.Kind == MergeKind.AllSettled
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct);

        var branchToken = cancellation?.Token ?? ct;

        // Two collections on purpose. `started` keeps declaration order, which is what
        // AllSettled publishes and what a reader matches against the manifest's `branches`
        // array; `pending` is drained as branches finish, and completion order is not
        // declaration order. Collapsing them would mean either losing the branch's identity
        // or reordering the array a reader is meant to index into.
        var started = new Task<RangeOutcome>[targets.Length];
        var pending = new List<Task<RangeOutcome>>(targets.Length);

        for (var b = 0; b < targets.Length; b++)
        {
            // Branch b owns [targets[b], targets[b + 1]) — or up to the join, for the
            // last. StepNode.ForParallel has already proved the spans are ascending and
            // non-empty, so this arithmetic cannot produce an overlap.
            var branchEnd = b + 1 < targets.Length ? targets[b + 1] : join;

            // The branches share the fork's cursor unchanged. A fork does not re-enter a
            // range — its spans are disjoint and each index appears once — so (scope, step)
            // stays unique without a per-branch scope, and the frontier can describe "branch
            // A done, branch B stopped at step 12" purely from which rows exist. That is
            // exactly why the resume position is derived rather than remembered.
            started[b] = RunRangeAsync(
                plan, dispatcher, context, scope, compensations, targets[b], branchEnd, cursor, branchToken)
                .AsTask();

            pending.Add(started[b]);
        }

        var errors = new Error?[targets.Length];
        var completed = 0;
        var succeeded = 0;
        var suspended = false;
        FlowWake? wake = null;
        Error? firstFailure = null;

        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(finished);

            var outcome = Observe(finished, plan.Flow.Id);

            completed += outcome.Completed;
            errors[Array.IndexOf(started, finished)] = outcome.Failure;

            // A branch that stopped at a suspension point is neither. Counting it as a
            // success would let Quorum(2) be satisfied by a branch that is still waiting,
            // and counting it as a failure would unwind a fork nothing went wrong in.
            suspended |= outcome.Suspended;

            // The earliest, when two branches are both waiting. The fork is resumed when the
            // first of them is due — the other finds its own wait still running and parks
            // again, which costs one resume and is the only reading under which a branch
            // waiting an hour is not held for the seven days its sibling asked for.
            wake = Earlier(wake, outcome.Wake);

            if (outcome.Suspended)
            {
                // Deliberately not cancelled: the siblings have work of their own to
                // finish, their rows are what a resume steps over, and cancelling them
                // would make the resumed fork redo work this invocation had already done.
            }
            else if (outcome.Failure is null)
            {
                succeeded++;
            }
            else
            {
                firstFailure ??= outcome.Failure;
            }

            // Cancelling does not end the loop: the remaining branches still have to be
            // drained, for the reason given in the remarks. It only stops them doing more
            // work than the merge needs.
            if (ShouldCancelSiblings(merge, outcome.Failure is null, succeeded))
            {
                await StopSiblingsAsync(cancellation).ConfigureAwait(false);
            }
        }

        // A failure outranks a suspension: the fork has a reason to unwind, and a saga that
        // waited for a signal it could no longer act on would be holding open a decision
        // that has already been made. With no failure the fork itself suspends, and the
        // merge is left un-judged — a strategy applied to a fork that has not finished
        // would report `flow.merge_not_satisfied` for branches that are merely waiting.
        if (suspended && firstFailure is null)
        {
            return new RangeOutcome(null, completed, suspended: true, wake);
        }

        return new RangeOutcome(Verdict(plan, step, merge, errors, succeeded, firstFailure, context), completed);
    }

    /// <summary>The wait that comes due first, when two branches of a fork are both waiting.</summary>
    /// <remarks>
    /// One row carries one wait, so a fork around two of them has to choose. The earlier is the
    /// only choice that cannot lose a wait: the instance is resumed when it is due, the branch
    /// it belongs to proceeds, and the other parks again — see <see cref="FlowWake"/> for what
    /// that costs the sibling.
    /// </remarks>
    private static FlowWake? Earlier(FlowWake? left, FlowWake? right) => (left, right) switch
    {
        (null, _) => right,
        (_, null) => left,
        _ => right!.Value.At < left!.Value.At ? right : left,
    };

    /// <summary>
    /// Runs the body of a <see cref="StepKind.ForEach"/> once per element.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The body is one range, run <em>n</em> times.</strong> Everything else in the
    /// DSL executes each index at most once; this does not, and that is the single fact
    /// worth being clear about. It costs a pass over the same span per element — so a
    /// ten-line order runs the body's step loop ten times, with ten sets of dispatcher
    /// calls — and it buys the flat array staying flat: no unrolled copy of the body per
    /// element, no separate sub-plan, nothing in the graph that depends on the size of the
    /// data. The manifest of a flow that reserves one line and one that reserves a thousand
    /// is the same document.
    /// </para>
    /// <para>
    /// <strong>The element count is read once, before the first pass.</strong> That is what
    /// bounds the loop, and it is why the DSL types the selector as
    /// <c>IReadOnlyList&lt;TItem&gt;</c>: the forward-target rule proves each pass
    /// terminates, and a count fixed in advance proves the loop over passes does.
    /// </para>
    /// <para>
    /// <strong>Two paths, and the second is <c>Parallel</c>'s.</strong> A bound of one runs
    /// the elements in order on the calling thread, allocating nothing beyond one scope per
    /// element and never touching a task. Above one, this is a fork with a sliding window:
    /// the same linked <see cref="CancellationTokenSource"/>, the same
    /// <see cref="Task.WhenAny(IEnumerable{Task})"/> drain, the same rule that every started
    /// iteration is awaited before the method returns — because the context is pooled, and
    /// an iteration still writing to it after the engine has moved on would eventually write
    /// into the next flow's context.
    /// </para>
    /// </remarks>
    private async ValueTask<RangeOutcome> RunForEachAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        FlowContext scope,
        CompensationStack? compensations,
        StepNode step,
        JournalCursor cursor,
        CancellationToken ct)
    {
        IterationSource source;

        try
        {
            source = dispatcher.BeginIteration(step.Index, scope);
        }
#pragma warning disable CA1031 // Same reasoning as the predicate and the switch selector:
        catch (Exception exception) //   a selector escaping here would skip the compensation
        {                           //   the already-completed steps need.
            return new RangeOutcome(FlowErrors.IterationFailed(plan.Flow.Id, step.Index, exception), 0);
        }
#pragma warning restore CA1031

        // An empty collection is not a failure and not a special case worth a diagnostic:
        // a loop over nothing does nothing, exactly as a `When` nobody took does.
        if (source.Count == 0)
        {
            return default;
        }

        var body = step.Index + 1;
        var join = step.Target!.Value;

        return step.MaxDegreeOfParallelism == 1
            ? await RunIterationsInOrderAsync(
                plan, dispatcher, context, scope, compensations, step, source, body, join, cursor, ct)
                .ConfigureAwait(false)
            : await RunIterationsConcurrentlyAsync(
                plan, dispatcher, context, scope, compensations, step, source, body, join, cursor, ct)
                .ConfigureAwait(false);
    }

    /// <summary>Runs the elements one at a time, in order.</summary>
    /// <remarks>
    /// No task, no token source, no drain list — a sequential loop is a sequential loop,
    /// and wrapping it in the concurrent path's machinery to save a method would charge
    /// every ordinary <c>ForEach</c> for concurrency it explicitly did not ask for.
    /// </remarks>
    private async ValueTask<RangeOutcome> RunIterationsInOrderAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        FlowContext scope,
        CompensationStack? compensations,
        StepNode step,
        IterationSource source,
        int body,
        int join,
        JournalCursor cursor,
        CancellationToken ct)
    {
        var errors = step.ContinueOnError ? new Error?[source.Count] : null;
        var completed = 0;
        var suspended = false;
        FlowWake? wake = null;
        Error? firstFailure = null;
        Error? fatal = null;

        for (var element = 0; element < source.Count; element++)
        {
            FlowContext iteration;

            try
            {
                iteration = dispatcher.EnterIteration(step.Index, in source, element, scope);
            }
#pragma warning disable CA1031 // Reading one element out of a list the selector already
            catch (Exception exception) //   produced should not throw; if it does, the
            {                           //   already-completed elements still need unwinding.
                fatal = FlowErrors.IterationFailed(plan.Flow.Id, step.Index, exception);
                break;
            }
#pragma warning restore CA1031

            // The element index becomes the journal scope, and it is the same
            // IterationScope the body already runs under seen from the key's side — not a
            // second notion of "which iteration". Nested loops chain, so a body two levels
            // down commits under `7/2`.
            var outcome = await RunRangeAsync(
                plan, dispatcher, context, iteration, compensations, body, join,
                cursor.Element(element), ct).ConfigureAwait(false);

            completed += outcome.Completed;

            if (outcome.Suspended)
            {
                // The whole loop waits, not just this element. Running the elements behind a
                // waiting one would commit their rows while an earlier iteration is still
                // open, so a resumed loop would re-enter element 3 having already done
                // element 4 — a `ForEach` is ordered by declaration and this keeps it so.
                suspended = true;
                wake = outcome.Wake;
                break;
            }

            if (outcome.Failure is null)
            {
                continue;
            }

            firstFailure ??= outcome.Failure;

            if (errors is null)
            {
                // ContinueOnError = false, which is the documented default: the first
                // failing element stops the iteration. The elements that already ran are
                // not undone here — they are on the compensation stack, and the flow's
                // own unwind takes them in strict reverse along with everything before
                // the loop. Compensating them here would undo half a saga twice.
                break;
            }

            errors[element] = outcome.Failure;
        }

        return Iterated(step, errors, firstFailure, fatal, suspended, wake, completed, context);
    }

    /// <summary>Runs the elements with a sliding window of at most <c>MaxDegreeOfParallelism</c>.</summary>
    /// <remarks>
    /// A window rather than a task per element: a thousand-line order must not start a
    /// thousand reservations, which is the whole reason the bound is required rather than
    /// optional. Every started iteration is drained before this returns, cancelled or not,
    /// for the reason given on <see cref="RunParallelAsync"/> — the context is pooled.
    /// </remarks>
    private async ValueTask<RangeOutcome> RunIterationsConcurrentlyAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        FlowContext scope,
        CompensationStack? compensations,
        StepNode step,
        IterationSource source,
        int body,
        int join,
        JournalCursor cursor,
        CancellationToken ct)
    {
        // ContinueOnError cancels nothing, so it needs no source at all — the same shape
        // AllSettled uses. The other case links to the caller's token so cancellation
        // still reaches the elements in flight.
        using var cancellation = step.ContinueOnError
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct);

        var iterationToken = cancellation?.Token ?? ct;
        var window = Math.Min(step.MaxDegreeOfParallelism, source.Count);

        var pending = new List<Task<RangeOutcome>>(window);
        var elements = new List<int>(window);

        var errors = step.ContinueOnError ? new Error?[source.Count] : null;
        var next = 0;
        var completed = 0;
        var stopped = false;
        var suspended = false;
        FlowWake? wake = null;
        Error? firstFailure = null;
        Error? fatal = null;

        while (next < source.Count || pending.Count > 0)
        {
            while (!stopped && next < source.Count && pending.Count < window)
            {
                FlowContext iteration;

                try
                {
                    iteration = dispatcher.EnterIteration(step.Index, in source, next, scope);
                }
#pragma warning disable CA1031 // As in the sequential path, and with the same consequence:
                catch (Exception exception) //   whatever is already in flight still has to
                {                           //   be drained before this returns.
                    fatal = FlowErrors.IterationFailed(plan.Flow.Id, step.Index, exception);
                    stopped = true;
                    await StopSiblingsAsync(cancellation).ConfigureAwait(false);
                    break;
                }
#pragma warning restore CA1031

                pending.Add(RunRangeAsync(
                    plan, dispatcher, context, iteration, compensations, body, join,
                    cursor.Element(next), iterationToken).AsTask());

                elements.Add(next);
                next++;
            }

            if (pending.Count == 0)
            {
                break;
            }

            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
            var slot = pending.IndexOf(finished);
            var element = elements[slot];

            pending.RemoveAt(slot);
            elements.RemoveAt(slot);

            var outcome = Observe(finished, plan.Flow.Id);

            completed += outcome.Completed;

            if (outcome.Suspended)
            {
                // No more elements are started, and the ones in flight are drained rather
                // than cancelled: their rows are what a resume steps over, and cancelling
                // them would make the resumed loop redo work this invocation had done.
                suspended = true;
                wake = Earlier(wake, outcome.Wake);
                stopped = true;
                continue;
            }

            if (outcome.Failure is null)
            {
                continue;
            }

            firstFailure ??= outcome.Failure;

            if (errors is null)
            {
                // Stopping does not end the loop: the iterations already in flight still
                // have to be drained. It only stops the window from starting more.
                stopped = true;
                await StopSiblingsAsync(cancellation).ConfigureAwait(false);
                continue;
            }

            errors[element] = outcome.Failure;
        }

        return Iterated(step, errors, firstFailure, fatal, suspended, wake, completed, context);
    }

    /// <summary>
    /// Turns a finished loop into the loop's own outcome, suspension included.
    /// </summary>
    /// <remarks>
    /// A wrapper over <see cref="IterationVerdict"/> rather than a parameter on it, because
    /// the verdict answers "what error, if any" and a suspension is not one. Keeping the two
    /// apart is what stops <c>ContinueOnError</c> publishing a <see cref="ForEachOutcome"/>
    /// for a loop that has not finished — a step after the loop would read it and decide
    /// something about elements that are still waiting.
    /// </remarks>
    private static RangeOutcome Iterated(
        StepNode step,
        Error?[]? errors,
        Error? firstFailure,
        Error? fatal,
        bool suspended,
        FlowWake? wake,
        int completed,
        FlowExecutionContext context)
    {
        // A failure outranks a suspension, for the reason a fork's does: the loop has a
        // reason to unwind, and waiting on a decision already made is not one of the
        // endings a saga has.
        if (suspended && fatal is null && firstFailure is null)
        {
            return new RangeOutcome(null, completed, suspended: true, wake);
        }

        return new RangeOutcome(IterationVerdict(step, errors, firstFailure, fatal, context), completed);
    }

    /// <summary>Turns the element outcomes into the loop's own outcome.</summary>
    /// <remarks>
    /// <para>
    /// With <c>ContinueOnError = false</c> the loop reports the failing element's own
    /// error, because there is one failure and it is the reason — the same choice
    /// <c>AllMustSucceed</c> makes. With <c>ContinueOnError = true</c> it reports success
    /// and publishes a <see cref="ForEachOutcome"/>, because the author has said that a
    /// failed element is a result rather than an end, and the step after the loop is the
    /// only thing entitled to decide what a partial success means.
    /// </para>
    /// <para>
    /// <paramref name="fatal"/> outranks both. <c>ContinueOnError</c> is a statement about
    /// elements failing, not about the loop itself being unable to produce one; a flow that
    /// swallowed a selector that threw would carry on over a collection it never read.
    /// </para>
    /// </remarks>
    private static Error? IterationVerdict(
        StepNode step,
        Error?[]? errors,
        Error? firstFailure,
        Error? fatal,
        FlowExecutionContext context)
    {
        if (errors is null)
        {
            return fatal ?? firstFailure;
        }

        context.Set(new ForEachOutcome(step.Index, errors));

        return fatal;
    }

    /// <summary>Reads a finished branch, converting a fault into an error rather than rethrowing.</summary>
    /// <remarks>
    /// <see cref="RunRangeAsync"/> is written not to throw — it converts every failure into
    /// an <see cref="Error"/>. This exists for the case where that is one day untrue: a
    /// branch that faulted must not take the whole fork down through an unobserved
    /// exception, because the siblings still need draining and the flow still needs
    /// compensating.
    /// </remarks>
    private static RangeOutcome Observe(Task<RangeOutcome> finished, string flowId)
    {
        if (finished.IsCompletedSuccessfully)
        {
            return finished.Result;
        }

        if (finished.IsCanceled)
        {
            return new RangeOutcome(FlowErrors.Cancelled(flowId), 0);
        }

        // Unwrapped when there is exactly one, which is every case a branch can produce:
        // an AggregateException wrapper in the message would name the plumbing rather than
        // the defect.
        Exception fault = finished.Exception switch
        {
            { InnerExceptions.Count: 1 } aggregate => aggregate.InnerExceptions[0],
            { } aggregate => aggregate,
            _ => new InvalidOperationException(
                "A parallel branch reported neither success, cancellation nor an exception."),
        };

        return new RangeOutcome(FlowErrors.Unhandled(flowId, fault), 0);
    }

    /// <summary>
    /// Cancels a fork's linked source and waits for its callbacks, without running any of
    /// them on the engine's own thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CancellationTokenSource.Cancel()"/> invokes every registered callback
    /// <em>synchronously, on the caller</em>. The caller here is the thread draining a fork
    /// or a <c>ForEach</c> window, and the callbacks belong to whatever the branches handed
    /// the token to — an HTTP handler, a database driver, a capability's own registration.
    /// One of those blocking stalls the drain; one of them throwing derails it. The drain is
    /// what keeps a branch from outliving its flow and writing into the <em>next</em>
    /// execution's pooled context, which may belong to another tenant, so it is not
    /// something to run arbitrary third-party code in the middle of.
    /// </para>
    /// <para>
    /// <strong>The await is the point, not an artefact of the signature.</strong>
    /// <c>Cancel()</c> guarantees that cancellation has fully propagated by the time it
    /// returns. Discarding the task <see cref="CancellationTokenSource.CancelAsync"/> hands
    /// back would give that guarantee up and let the callbacks race the drain they exist to
    /// unblock. Awaiting keeps the guarantee and moves the callbacks onto the thread pool,
    /// which is the whole difference between the two methods.
    /// </para>
    /// <para>
    /// <strong>Why awaiting is safe at all three call sites.</strong> None of them is in a
    /// <c>finally</c>, so the await cannot be reached during an unwind. None of them ends
    /// the loop it sits in: cancelling stops branches doing <em>more</em> work, it never
    /// stops them being observed, and every started branch is still awaited before the
    /// method returns. And the state each site has already recorded — the outcome, the
    /// first failure, the <c>stopped</c> flag — is written before the await, so a
    /// continuation resuming on another thread reads it rather than races it.
    /// </para>
    /// <para>
    /// <c>null</c> is the case that cancels nothing and therefore allocated no source at all:
    /// <c>AllSettled</c> for a fork, <c>ContinueOnError</c> for a loop.
    /// </para>
    /// </remarks>
    private static Task StopSiblingsAsync(CancellationTokenSource? cancellation) =>
        cancellation?.CancelAsync() ?? Task.CompletedTask;

    /// <summary>Whether the branch that just finished means the rest can stop.</summary>
    private static bool ShouldCancelSiblings(MergeStrategy merge, bool branchSucceeded, int succeeded) =>
        merge.Kind switch
        {
            // The documented rule: "first failure cancels siblings via linked token".
            MergeKind.AllMustSucceed => !branchSucceeded,

            // "the flow continues; branch errors available in context" — nothing is cancelled.
            MergeKind.AllSettled => false,

            // FirstSuccess is Quorum(1); both stop as soon as they have enough.
            _ => branchSucceeded && succeeded >= merge.RequiredSuccesses,
        };

    /// <summary>Turns the branch outcomes into the fork's own outcome.</summary>
    /// <remarks>
    /// <c>AllMustSucceed</c> reports the branch's own error, because there is one failure
    /// and it is the reason. <c>FirstSuccess</c> and <c>Quorum</c> report a count, because
    /// the reason is that not enough branches worked and picking one of several errors to
    /// stand for that would be arbitrary — the errors are still on the branch outcomes, and
    /// the first is attached to the flow's own error data.
    /// </remarks>
    private static Error? Verdict(
        ExecutionPlan plan,
        StepNode step,
        MergeStrategy merge,
        Error?[] errors,
        int succeeded,
        Error? firstFailure,
        FlowExecutionContext context)
    {
        switch (merge.Kind)
        {
            case MergeKind.AllSettled:
                // The one strategy that writes something for the next step to read. It is
                // also the only one that can reach the next step having failed at all.
                context.Set(new ParallelOutcome(step.Index, errors));
                return null;

            case MergeKind.AllMustSucceed:
                return firstFailure;

            default:
                return succeeded >= merge.RequiredSuccesses
                    ? null
                    : FlowErrors.MergeNotSatisfied(
                        plan.Flow.Id, step.Index, merge.RequiredSuccesses, succeeded, errors.Length);
        }
    }

    /// <summary>
    /// Runs the flow a <see cref="StepKind.SubFlow"/> step composes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The engine owns the child's lifecycle; the dispatcher owns its types.</strong>
    /// <see cref="IStepDispatcher.BeginSubFlow"/> answers the one question only generated
    /// code can — which plan, which dispatcher, which input — and everything after that is
    /// the same work this engine does for a top-level flow, done once here instead of once
    /// per composing flow in emitted source.
    /// </para>
    /// <para>
    /// <strong>The mapping runs first, on this thread, against the parent's scope.</strong>
    /// That ordering is what makes a detached child safe: the input has already been taken
    /// out of the parent's pooled context by the time anything is started, so nothing the
    /// child holds can still point at a context that will be reset and handed to another
    /// tenant's flow.
    /// </para>
    /// </remarks>
    private async ValueTask<RangeOutcome> RunSubFlowAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        FlowContext scope,
        CompensationStack? compensations,
        StepNode step,
        JournalCursor cursor,
        CancellationToken ct)
    {
        if (context.Depth >= MaxSubFlowDepth)
        {
            return new RangeOutcome(
                FlowErrors.SubFlowTooDeep(plan.Flow.Id, step.SubFlowId!, MaxSubFlowDepth), 0);
        }

        SubFlowSource source;

        try
        {
            source = dispatcher.BeginSubFlow(step.Index, scope);
        }
#pragma warning disable CA1031 // Same reasoning as the predicate and the selectors: a
        catch (Exception exception) //   mapping escaping here would skip the compensation
        {                           //   the already-completed steps need.
            return new RangeOutcome(
                FlowErrors.SubFlowMappingFailed(plan.Flow.Id, step.Index, exception), 0);
        }
#pragma warning restore CA1031

        // The child's own declaration governs the child, exactly as ADR-0003 says: an
        // ephemeral child composed by a durable parent is not journaled, and a durable child
        // is — as its own instance. A durable child under a parent that has no journal is
        // refused rather than run ephemerally, which is the same refusal the top of the
        // engine makes and for the same reason.
        var childIsDurable = ExecutionProfiles.IsJournaled(source.Plan.Flow.Profile);

        if (childIsDurable && !cursor.IsJournaled)
        {
            return new RangeOutcome(FlowErrors.DurabilityNotConfigured(source.Plan.Flow.Id), 0);
        }

        var child = _contexts.Rent();
        var retained = false;

        try
        {
            // Correlation, tenant and idempotency key are the parent's for an inline child:
            // it is one operation, so it is one trace and one deduplication identity. A
            // detached child keeps the correlation — identity, not lifetime — and drops the
            // deadline, which is what "its own lifecycle" means.
            //
            // The deadline passed for an inline child is the parent's *absolute* instant,
            // and Initialise takes the smaller of that and the child's own declared budget.
            // So composition can only ever shorten: a child cannot buy itself time its
            // parent does not have, and a parent cannot buy the child more than its own
            // author allowed.
            child.Initialise(
                source.Plan,
                new FlowInvocation(
                    context.CorrelationId,
                    context.IdempotencyKey,
                    context.TenantId,
                    step.Mode == SubFlowMode.Detached ? null : context.Deadline),
                _clock,
                source.Dispatcher,
                context.Depth + 1);

            // The parent's dispatcher, not the child's: the cast back to TSubIn belongs to
            // the `.SubFlow<TFlow, TSubIn>(...)` call site, which is in the parent.
            dispatcher.EnterSubFlow(step.Index, in source, child);
        }
#pragma warning disable CA1031 // A generated cast that fails means the plan and this
        catch (Exception exception)  //   dispatcher came from different builds; the parent's
        {                            //   completed steps still need unwinding either way.
            _contexts.Return(child);
            return new RangeOutcome(
                FlowErrors.SubFlowMappingFailed(plan.Flow.Id, step.Index, exception), 0);
        }
#pragma warning restore CA1031

        var childCursor = default(JournalCursor);

        if (childIsDurable)
        {
            childCursor = cursor.Child(Guid.NewGuid());

            var opened = await OpenChildInstanceAsync(source, context, cursor, childCursor, step, ct)
                .ConfigureAwait(false);

            if (opened is not null)
            {
                _contexts.Return(child);
                return new RangeOutcome(opened, 0);
            }

            // The child's own instance, so the child's own unwind writes its compensation
            // rows against the instance its step indices mean something in.
            child.Run = childCursor.Run;
        }

        if (step.Mode == SubFlowMode.Detached)
        {
            Detach(source, child, childCursor);
            return default;
        }

        var startedAt = cursor.IsJournaled ? _clock.UtcNow : default;

        try
        {
            var childCompensations = source.Plan.HasCompensation ? child.Compensations : null;

            var outcome = await RunRangeAsync(
                source.Plan, source.Dispatcher, child, child, childCompensations,
                0, source.Plan.Graph.Count, childCursor, ct).ConfigureAwait(false);

            // An inline child cannot suspend, and this is where that is refused rather than
            // where it goes wrong. The parent's composition row is written only when the
            // child finishes, so a parent resumed past a waiting child would find no row for
            // the composition and compose a *second* child instance — repeating every effect
            // the first one had already had. Nothing in the journal could tell the two apart
            // afterwards, which is why this is a refusal and not a limitation to document.
            //
            // A detached child is a different shape and is not refused: it has its own
            // instance, its own lifecycle and no row the parent is waiting on, so it suspends
            // and is signalled exactly as a flow a trigger started.
            if (outcome.Suspended)
            {
                outcome = new RangeOutcome(
                    FlowErrors.SuspensionInsideComposition(
                        plan.Flow.Id, step.Index, source.Plan.Flow.Id),
                    outcome.Completed);
            }

            if (outcome.Failure is not null)
            {
                // The child unwinds itself, here, before the parent hears about it. Its
                // steps are its own and its dispatcher is the only thing that can undo
                // them; handing the parent a half-finished saga to think about would mean
                // the child's failure left the child's own effects standing.
                child.SetError(outcome.Failure);

                if (childCompensations is not null)
                {
                    await CompensateAsync(childCompensations, source.Dispatcher, child)
                        .ConfigureAwait(false);
                }

                var failed = FlowErrors.SubFlowFailed(
                    plan.Flow.Id, step.Index, source.Plan.Flow.Id, outcome.Failure);

                // The journal's own refusal is discarded here, as it is on a compensation
                // row and for the same reason. The flow is ending either way, and the
                // child's error is the reason an operator needs;
                // replacing "the payment was declined" with "your fencing token is stale"
                // would hide the business fact behind the ownership one.
                _ = await RecordCompositionAsync(
                    plan, dispatcher, context, scope, cursor, childCursor, step, source, failed, startedAt, ct)
                    .ConfigureAwait(false);

                return new RangeOutcome(failed, outcome.Completed);
            }

            var refusal = await RecordCompositionAsync(
                plan, dispatcher, context, scope, cursor, childCursor, step, source, null, startedAt, ct)
                .ConfigureAwait(false);

            if (refusal is not null)
            {
                return new RangeOutcome(refusal, outcome.Completed);
            }

            // The child succeeded and left work behind that the *parent* may still have to
            // undo. See the class remarks on why that is the right answer and what it
            // costs: the child's context stays rented until the parent finishes, because
            // the undo binds to what the child's steps produced.
            if (childCompensations is { IsEmpty: false } && compensations is not null)
            {
                context.RecordCompleted(step, child, cursor.Scope);
                context.RetainSubFlow(child);
                retained = true;

                // The child's instance has just been sealed Completed, and a journal refuses
                // a write to a finished instance — correctly, because "finished" is what the
                // row says. So a deferred unwind of this child records no compensation rows,
                // and it drops the session rather than issuing writes it knows will be
                // refused. Stated here because it is the one place 06 §7's rule 4 is still
                // only half true: the child's undo runs, and nothing writes down that it did.
                child.Run = null;
            }

            return new RangeOutcome(null, outcome.Completed);
        }
        finally
        {
            if (!retained)
            {
                _contexts.Return(child);
            }
        }
    }

    /// <summary>
    /// Opens the <c>flow_instance</c> row a composed durable child gets of its own.
    /// </summary>
    /// <remarks>
    /// ADR-0015's third schema commitment, executed. The row carries
    /// <c>parent_instance_id</c> and the parent's <c>(scope, step_id)</c>, and the child's
    /// steps are not spliced into the parent's history — which is what keeps the parent's
    /// manifest from claiming the child's capabilities, keeps the child's own deadline and
    /// profile, and gives a detached child, which outlives the step that started it,
    /// somewhere to live.
    /// </remarks>
    private static async ValueTask<Error?> OpenChildInstanceAsync(
        SubFlowSource source,
        FlowExecutionContext context,
        JournalCursor parent,
        JournalCursor child,
        StepNode step,
        CancellationToken ct)
    {
        var run = child.Run!;

        var start = new FlowInstanceStart
        {
            InstanceId = run.InstanceId,
            FlowId = source.Plan.Flow.Id,
            FlowVersion = source.Plan.Flow.Version,
            Token = run.Token,
            TenantId = context.TenantId,

            // Identity, not lifetime. A detached child keeps the correlation so one operation
            // is still one trace, and drops the deadline, which is what "its own lifecycle"
            // means — so the deadline is the child's own from here.
            CorrelationId = context.CorrelationId,
            ParentInstanceId = parent.Run!.InstanceId,
            ParentScope = parent.Scope,
            ParentStepId = step.Index,
        };

        var started = await run.Journal.StartAsync(start, ct).ConfigureAwait(false);

        return started.IsSuccess ? null : started.Error;
    }

    /// <summary>
    /// Closes a composed child's instance and appends the parent's row for the composition.
    /// </summary>
    /// <remarks>
    /// Both, in that order, because the parent's row is what a resume reads to decide the
    /// composition is done. Writing the parent's row first would let a crash in between leave
    /// a parent that believes the child finished and a child row that says it is still
    /// running — the one ordering that produces an instance nobody will ever pick up.
    /// </remarks>
    private async ValueTask<Error?> RecordCompositionAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        FlowContext scope,
        JournalCursor cursor,
        JournalCursor childCursor,
        StepNode step,
        SubFlowSource source,
        Error? failure,
        DateTimeOffset startedAt,
        CancellationToken ct)
    {
        if (childCursor.IsJournaled)
        {
            var closed = await CloseInstanceAsync(
                childCursor,
                failure is null ? FlowInstanceState.Completed : FlowInstanceState.Failed,
                wake: null,
                ct).ConfigureAwait(false);

            if (closed is not null)
            {
                return closed;
            }
        }

        return cursor.IsJournaled
            ? await CommitStepAsync(
                plan, dispatcher, context, scope, cursor, step, failure, startedAt,
                source.Plan.Flow.Version, attempt: 1, ct).ConfigureAwait(false)
            : null;
    }

    /// <summary>
    /// Starts a detached child and stops caring about its result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Untracked would have been wrong.</strong> <c>FlowHost</c> counts the flows it
    /// starts and <c>DrainAsync</c> waits for that count to reach zero; a child started from
    /// inside the engine is not one of them, so without a counter here a drain would report
    /// "everything finished" while a fire-and-forget saga was halfway through reserving
    /// inventory — and the SIGKILL that follows would leave it reserved. The counter lives
    /// on the engine rather than on the host because the engine is the only thing that knows
    /// a detached child exists.
    /// </para>
    /// <para>
    /// <see cref="CancellationToken.None"/> on purpose. A child whose whole point is to
    /// outlive its parent must not be cancelled when the parent's token is.
    /// </para>
    /// </remarks>
    private void Detach(SubFlowSource source, FlowExecutionContext child, JournalCursor cursor)
    {
        child.Run = cursor.Run;

        lock (_detachedSync)
        {
            _detachedInFlight++;
        }

        _ = RunDetachedAsync(source, child, cursor);
    }

    private async Task RunDetachedAsync(SubFlowSource source, FlowExecutionContext child, JournalCursor cursor)
    {
        try
        {
            var compensations = source.Plan.HasCompensation ? child.Compensations : null;

            var outcome = await RunRangeAsync(
                source.Plan, source.Dispatcher, child, child, compensations,
                0, source.Plan.Graph.Count, cursor, CancellationToken.None).ConfigureAwait(false);

            // The result is deliberately discarded — that is what fire-and-forget means —
            // but the *ending* is not: a detached child that failed still compensates its
            // own completed steps, exactly as it would if a trigger had started it.
            var result = await CompleteAsync(source.Plan, source.Dispatcher, child, compensations, outcome)
                .ConfigureAwait(false);

            // Its instance row is closed for the same reason. A detached child outlives the
            // step that started it, so nothing else is left to say what became of it — and an
            // instance stuck at Running forever is exactly what a recovery scan picks up and
            // tries to finish.
            if (cursor.IsJournaled)
            {
                _ = await CloseInstanceAsync(
                    cursor, InstanceStateFor(result), result.Wake, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // Nothing awaits this task, so an escaping exception would be
        catch (Exception)      //   an unobserved TaskException — a process-level event in some
        {                      //   hosts, and one nobody can attribute to a flow.
        }
#pragma warning restore CA1031
        finally
        {
            _contexts.Return(child);

            lock (_detachedSync)
            {
                _detachedInFlight--;

                if (_detachedInFlight == 0)
                {
                    _detachedIdle?.TrySetResult();
                }
            }
        }
    }

    /// <summary>How many detached sub-flows this engine has started and not yet finished.</summary>
    public int DetachedInFlight
    {
        get
        {
            lock (_detachedSync)
            {
                return _detachedInFlight;
            }
        }
    }

    /// <summary>
    /// Waits for every detached sub-flow to finish, up to <paramref name="timeout"/>.
    /// </summary>
    /// <param name="timeout">How long to wait before giving up.</param>
    /// <param name="ct">Cancels the wait, not the children.</param>
    /// <returns><c>true</c> when nothing detached is still running.</returns>
    /// <remarks>
    /// Exists so <c>FlowHost.DrainAsync</c> can keep its promise. A detached child is not
    /// one of the host's in-flight flows — it was started from inside a step — so without
    /// this the host would drain the flows it knows about and report success while a saga
    /// nobody is counting is still mid-way through its effects.
    /// </remarks>
    public async ValueTask<bool> WaitForDetachedAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        Task idle;

        lock (_detachedSync)
        {
            if (_detachedInFlight == 0)
            {
                return true;
            }

            _detachedIdle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            idle = _detachedIdle.Task;
        }

        try
        {
            await idle.WaitAsync(timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gives back the pooled contexts of sub-flows that succeeded and were never undone.
    /// </summary>
    /// <remarks>
    /// The mirror of the retention in <see cref="RunSubFlowAsync"/>. Recursive, because a
    /// child that itself composed a flow is holding a grandchild the same way — and a
    /// context that is never returned is not a correctness bug (the pool simply builds a new
    /// one) but it is a pool that stops pooling, which is how the allocation budget goes
    /// quietly from zero to per-execution.
    /// </remarks>
    private void ReleaseRetainedSubFlows(FlowExecutionContext context)
    {
        var retained = context.RetainedSubFlows;

        // Indexed, not enumerated. This runs on the *success* path of every flow that
        // composes another, and `foreach` over a List<T> is fine but walking the
        // compensation stack instead — which is where these children can also be found —
        // costs one iterator per nesting level per execution. That was measured at 96 B for
        // a single composition, on the path budget B2 is about.
        for (var i = 0; i < retained.Count; i++)
        {
            var child = retained[i];

            ReleaseRetainedSubFlows(child);
            _contexts.Return(child);
        }

        retained.Clear();
    }

    /// <summary>
    /// Unwinds a sub-flow that succeeded and whose parent then failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the answer to "must a successful child compensate when its parent
    /// later fails".</strong> Yes. A saga's guarantee is that when the operation fails,
    /// everything it completed is undone in strict reverse — and composing steps into a
    /// sub-flow must not weaken that, or <c>FLOWX1005</c>'s advice to "extract the shared
    /// steps into a sub-flow" would be advice to silently lose compensation.
    /// </para>
    /// <para>
    /// <strong>Strict reverse survives the boundary, at the granularity that matters.</strong>
    /// The parent records the sub-flow as one entry, in its own stack, in the position the
    /// composition occupied. So for a parent that completed <c>A</c>, then a child that
    /// completed <c>X</c> and <c>Y</c>, then <c>B</c>, the unwind is <c>B, Y, X, A</c> —
    /// which is exactly what it would have been had the child's steps been written inline.
    /// The child's own stack supplies the inner order and the parent's supplies the outer;
    /// neither has to know about the other.
    /// </para>
    /// <para>
    /// The undo is dispatched through the <em>child's</em> dispatcher, against the
    /// <em>child's</em> context. Both are load-bearing: the indices on that stack are the
    /// child's step indices, which mean something entirely different in the parent's
    /// dispatcher, and the compensation binds to what the child's steps produced, which
    /// only the child's context holds.
    /// </para>
    /// </remarks>
    private async ValueTask<bool> CompensateSubFlowAsync(CompensationEntry entry)
    {
        if (entry.Scope is not FlowExecutionContext child || child.Dispatcher is null)
        {
            // Unreachable: the entry is only pushed with a child context that was
            // initialised with its dispatcher. Reported as a failed compensation rather
            // than ignored, because silently skipping an undo is the one outcome a saga
            // must never produce.
            return false;
        }

        try
        {
            return await CompensateAsync(child.Compensations, child.Dispatcher, child)
                .ConfigureAwait(false) != CompensationOutcome.PartiallyFailed;
        }
        finally
        {
            _contexts.Return(child);
        }
    }

    /// <summary>
    /// Unwinds the completed compensable steps, newest first, and keeps going when one
    /// of them fails.
    /// </summary>
    /// <remarks>
    /// Best-effort by design. Abandoning the remaining undo work because one
    /// compensation failed leaves strictly more inconsistency than continuing does —
    /// a failed refund is no reason to also leak the inventory reservation. The
    /// partial failure is reported, not hidden.
    /// <para>
    /// Cancellation is deliberately not honoured here: a cancelled flow still has to
    /// clean up after itself, and a token cancelled mid-unwind would leave the
    /// dangling state the unwind exists to prevent.
    /// </para>
    /// <para>
    /// <strong>The plan and the instance come off the context rather than down the call
    /// chain.</strong> This method is reached from four places — the end of a flow, a child
    /// that failed, a detached child, and a parent unwinding a child that had already
    /// succeeded — and the last of those happens long after the composition returned, with
    /// nothing but the child's context left to say which plan and which instance its steps
    /// belonged to. Threading two more parameters through would have made three of the four
    /// call sites carry values only the fourth could not supply.
    /// </para>
    /// </remarks>
    private async ValueTask<CompensationOutcome> CompensateAsync(
        CompensationStack compensations,
        IStepDispatcher dispatcher,
        FlowExecutionContext context)
    {
        if (compensations.IsEmpty)
        {
            return CompensationOutcome.NotRequired;
        }

        // The same bargain HasParallel strikes. A flow whose undos declare no policy takes
        // exactly the path it always did: one dispatch per entry, no clock read, no retry
        // bookkeeping — one predictable, always-false comparison for a feature it does not use.
        var retrying = context.Plan?.HasCompensationPolicies ?? false;
        var run = context.Run;
        var allSucceeded = true;

        foreach (var entry in compensations.Unwind())
        {
            // EnterCompensation, not EnterStep: what is about to run is the step's inverse,
            // and a context that named the step being reversed would hand every compensator
            // the forward step's identity to key its writes on.
            _ = context.EnterCompensation(entry.Step);

            if (entry.Step.Kind == StepKind.SubFlow)
            {
                // Not this dispatcher's business: the indices on the child's stack are the
                // child's, and handing them here would compensate whatever this flow
                // happens to have at index 2.
                allSucceeded &= await CompensateSubFlowAsync(entry).ConfigureAwait(false);
                continue;
            }

            var failure = await UndoAsync(entry, dispatcher, context, run, retrying).ConfigureAwait(false);

            allSucceeded &= failure is null;
        }

        return allSucceeded ? CompensationOutcome.Succeeded : CompensationOutcome.PartiallyFailed;
    }

    /// <summary>
    /// Runs one step's compensation under its own policy, journals every attempt, and alerts
    /// when it gives up.
    /// </summary>
    /// <param name="entry">The completed step whose inverse is pending.</param>
    /// <param name="dispatcher">The dispatcher whose indices this entry's are.</param>
    /// <param name="context">The execution the undo runs against.</param>
    /// <param name="run">The instance to record attempts on, or <c>null</c> when ephemeral.</param>
    /// <param name="retrying">Whether this plan declares any compensation policy at all.</param>
    /// <returns>The last failure, or <c>null</c> when the undo worked.</returns>
    /// <remarks>
    /// <para>
    /// <strong>This is the whole of the policy engine on the failure path, and it is one
    /// policy at one stage.</strong> A compensation retry is declared at
    /// <see cref="PolicyStage.Consistency"/>, which is where ADR-0011's fixed order puts
    /// compensation; the unwind is that stage's obligation discharged later, and the retry is
    /// a parameter of it. Nothing else executes here: the forward path's stage-4 policies are
    /// applied around a step's own dispatch and none of them reaches an undo, which is why
    /// <see cref="PolicyChain.ForStep"/> and <see cref="PolicyChain.ForCompensation"/> split a
    /// declared set by what each policy wraps. <see cref="PolicyChain"/> remains the only
    /// thing in the system that decides what runs before what.
    /// </para>
    /// <para>
    /// <strong>The first attempt is not bounded by the deadline; the retries are.</strong>
    /// <c>docs/10-Policy-Framework.md §5</c> says a retry never outlives the deadline, and the
    /// planned backoff is subtracted before the next attempt is armed. It says nothing about
    /// the undo itself, and it should not: a flow that failed <em>because</em> it ran out of
    /// budget is exactly the flow whose effects most need reversing, and skipping the unwind
    /// to respect a deadline that has already passed would leave the dangling state the saga
    /// exists to prevent.
    /// </para>
    /// <para>
    /// <strong>The commit is outside the catch.</strong> A store that is unreachable throws,
    /// and <see cref="IFlowJournal"/> draws that line on purpose: it is not this flow's
    /// failure and must not be recorded as one. Letting it propagate leaves the instance
    /// mid-unwind with its committed prefix intact, which is the state a resumed instance
    /// reads to work out which undos are still owed.
    /// </para>
    /// </remarks>
    private async ValueTask<Error?> UndoAsync(
        CompensationEntry entry,
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        DurableExecution? run,
        bool retrying)
    {
        var policy = retrying ? entry.Step.CompensationRetry : CompensationPolicy.None;
        var attempt = 0;
        Error? failure;

        while (true)
        {
            attempt++;

            // Only read when there is a row to put it on, exactly as the forward path does.
            var startedAt = run is null ? default : _clock.UtcNow;

            failure = await DispatchUndoAsync(entry, dispatcher, context).ConfigureAwait(false);

            if (run is not null)
            {
                await CommitCompensationAsync(entry, context, run, attempt, failure, startedAt)
                    .ConfigureAwait(false);
            }

            if (failure is null)
            {
                return null;
            }

            if (!policy.AllowsAnotherAttempt(failure, attempt))
            {
                break;
            }

            // Full jitter needs a draw, and this is the only randomness the engine has. It is
            // deliberately not ctx.Random: that one is seeded, journaled and replayed because
            // a step's *decisions* have to be reproducible, and how long a failed undo waited
            // is not a decision — recording it would put a number in the replay envelope that
            // no replay could act on.
            var delay = policy.DelayBefore(attempt, Random.Shared.NextDouble());

            if (_clock.UtcNow + delay >= context.Deadline)
            {
                break;
            }

            await _clock.DelayAsync(delay, CancellationToken.None).ConfigureAwait(false);
        }

        Alert(entry, context, run, attempt, failure);

        return failure;
    }

    /// <summary>Dispatches one attempt at an undo, converting a throw into an error.</summary>
    /// <remarks>
    /// The scope is the one the step completed in — the flow's own context everywhere except
    /// inside a <c>ForEach</c>, where it is the iteration's, so <c>ReleaseLine</c> undoes the
    /// line <c>ReserveLine</c> reserved rather than whichever line the loop happened to end on.
    /// <para>
    /// A throwing compensation used to be counted as a failure and otherwise discarded. It is
    /// converted into an <see cref="Error"/> now because there is something to do with one: a
    /// policy has to decide whether it is worth another attempt, and an alert has to name what
    /// went wrong. <see cref="ErrorCategory.Internal"/> is what a defect is, and it is
    /// retryable — a capability that throws on one call and works on the next is a
    /// dependency's flakiness surfacing as an exception rather than as a result.
    /// </para>
    /// </remarks>
    private static async ValueTask<Error?> DispatchUndoAsync(
        CompensationEntry entry,
        IStepDispatcher dispatcher,
        FlowExecutionContext context)
    {
        try
        {
            var outcome = await dispatcher
                .CompensateAsync(entry.Index, entry.Scope ?? context, CancellationToken.None)
                .ConfigureAwait(false);

            return outcome.Error;
        }
#pragma warning disable CA1031 // Same reasoning as the step loop: a throwing compensation is a
        catch (Exception exception)  //   defect, and one broken undo must not abandon the others.
        {
            return FlowErrors.Unhandled(entry.Step.CompensationIdentity, exception);
        }
#pragma warning restore CA1031
    }

    /// <summary>Appends one row saying what an undo attempt did.</summary>
    /// <remarks>
    /// <para>
    /// <c>docs/06-Execution-Engine.md §7</c> rule 4, which said of itself that it was not
    /// implemented: "the journal records what ran forward; it says nothing about what has been
    /// undone". This is what it now says instead — one row per attempt, carrying the
    /// <em>compensating</em> capability's id so an undo is never mistaken for the step it
    /// reverses, and <see cref="JournalOutcome.Compensated"/> when it worked.
    /// </para>
    /// <para>
    /// <strong>The attempt number is offset past the forward rows rather than counted from
    /// one.</strong> The key is <c>(instance, scope, step, attempt)</c> and an append-only
    /// table cannot overwrite, so a compensation row keyed at attempt 1 would collide with the
    /// forward row that put the step on the stack in the first place.
    /// <see cref="DurableExecution.NextAttempt"/> is one past everything the committed history
    /// holds for this key, and the undo's own attempt is added to that — which is unique for a
    /// fresh instance, for a resumed one that skipped the step, and for a resumed one that
    /// re-ran it, without this node having to remember a number that dies with it.
    /// </para>
    /// <para>
    /// <strong>The instance is moved to <see cref="FlowInstanceState.Compensating"/> by the
    /// same write.</strong> An operator watching a saga unwind should not have to infer it
    /// from the absence of new step rows, and the state is a column a store already keeps.
    /// </para>
    /// <para>
    /// <strong>The journal's refusal is discarded, and this is the second place that is
    /// true.</strong> The undo has already run: its effect is real whether or not the row
    /// lands. Turning a fenced-out write into a compensation failure would report an undo that
    /// worked as one that did not, and would replace a business fact with an ownership one —
    /// the same trade <c>RecordCompositionAsync</c> makes and for the same reason.
    /// </para>
    /// </remarks>
    private async ValueTask CommitCompensationAsync(
        CompensationEntry entry,
        FlowExecutionContext context,
        DurableExecution run,
        int attempt,
        Error? failure,
        DateTimeOffset startedAt)
    {
        var commit = new StepCommit
        {
            Key = new StepKey(
                run.InstanceId,
                entry.JournalScope,
                entry.Index,
                run.NextAttempt(entry.JournalScope, entry.Index) + attempt),
            Token = run.Token,
            CapabilityId = entry.Step.CompensationIdentity,
            CapabilityVersion = entry.Step.Compensation?.Version ?? context.FlowVersion,
            Outcome = failure is null ? JournalOutcome.Compensated : JournalOutcome.Failure,
            State = FlowInstanceState.Compensating,
            Duration = _clock.UtcNow - startedAt,
            ResumeHint = entry.Index,
        };

        _ = await run.Journal.CommitAsync(commit, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Reports one compensation that has been given up on.</summary>
    /// <remarks>
    /// <para>
    /// The observable half of <c>docs/11-Distributed-Runtime.md §8</c>'s last row — the one
    /// case with no automatic resolution. It is raised once per exhausted compensation rather
    /// than once per flow, because the runbook is per step:
    /// <c>flowx replay --instance &lt;id&gt; --from &lt;step&gt;</c>.
    /// </para>
    /// <para>
    /// A sink that throws is swallowed. It is somebody else's code, called on the failure path
    /// of a flow that is unwinding, and a broken pager must not leak the inventory reservation
    /// the engine was in the middle of releasing.
    /// </para>
    /// </remarks>
    private void Alert(
        CompensationEntry entry,
        FlowExecutionContext context,
        DurableExecution? run,
        int attempts,
        Error failure)
    {
        if (_alerts is null)
        {
            return;
        }

        var alert = new CompensationAlert(
            context.FlowId,
            context.FlowVersion,
            run?.InstanceId,
            entry.Index,
            entry.Step.CompensationIdentity,
            context.CorrelationId,
            context.TenantId,
            attempts,
            failure);

        try
        {
            _alerts.CompensationExhausted(in alert);
        }
#pragma warning disable CA1031 // An alert sink is third-party code, called on the failure path
        catch (Exception)      //   of a flow that is already unwinding. One broken pager must
        {                      //   not leak the reservation the engine was releasing, and
        }                      //   there is nowhere better than here to report a failure to
#pragma warning restore CA1031 //   report a failure.
    }
}

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
/// <strong>Deadlines are enforced at step boundaries.</strong> The engine will not
/// start a step whose flow has run out of budget, but it does not interrupt a step
/// already running. Interrupting in-flight work is the Timeout policy's job (P4),
/// and conflating the two would put a timer allocation on every execution to solve a
/// problem most flows do not have.
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
    private readonly object _detachedSync = new();

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
    public FlowEngine(
        IClock clock,
        int maxPooledContexts = DefaultMaxPooledContexts,
        ICompensationAlertSink? alerts = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPooledContexts);

        _clock = clock;
        _contexts = new ContextPool(maxPooledContexts);
        _alerts = alerts;
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

        if (plan.Flow.Profile != ExecutionProfile.Durable)
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

    /// <summary>Moves the instance to its terminal state once the loop and the unwind are done.</summary>
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
    /// </remarks>
    private static async ValueTask<FlowExecutionResult> SealAsync(
        JournalCursor cursor,
        FlowExecutionResult result,
        CancellationToken ct)
    {
        if (result.Error is { } ended && Disowned(ended))
        {
            return result;
        }

        var refusal = await CloseInstanceAsync(cursor, TerminalState(result), ct).ConfigureAwait(false);

        return refusal is null
            ? result
            : new FlowExecutionResult(refusal, result.CompletedSteps, result.Compensation);
    }

    /// <summary>Moves one instance — root, composed or detached — to a terminal state.</summary>
    /// <remarks>
    /// The state bag is deliberately <see cref="JournalPayload.Empty"/>: the last committed
    /// step already carried the snapshot, and re-serialising the bag at the end would make
    /// the final row disagree with the step that produced it whenever compensation has run.
    /// </remarks>
    private static async ValueTask<Error?> CloseInstanceAsync(
        JournalCursor cursor,
        FlowInstanceState state,
        CancellationToken ct)
    {
        var run = cursor.Run!;

        var closed = await run.Journal
            .CompleteAsync(run.InstanceId, run.Token, state, JournalPayload.Empty, ct)
            .ConfigureAwait(false);

        return closed.IsSuccess ? null : closed.Error;
    }

    /// <summary>Which terminal state a finished execution leaves on the instance row.</summary>
    /// <remarks>
    /// <c>CompensationFailed</c> outranks the rest: it is the one terminal state with no
    /// automatic resolution — two systems now disagree about the same business fact — and
    /// recording it as a plain failure would hide the one outcome an operator has to be told
    /// about.
    /// </remarks>
    private static FlowInstanceState TerminalState(FlowExecutionResult result) => result switch
    {
        { Compensation: CompensationOutcome.PartiallyFailed } => FlowInstanceState.CompensationFailed,
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
            if (plan.HasSubFlow)
            {
                ReleaseRetainedSubFlows(context);
            }

            return new FlowExecutionResult(null, outcome.Completed, CompensationOutcome.NotRequired);
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
    /// A struct so a branch's result costs nothing to return. The whole flow is one range,
    /// <c>[0, Count)</c>, so the sequential path and a parallel branch are literally the
    /// same code — which is the point of the shape, and the reason a branch inherits the
    /// deadline check, the compensation recording and the exception handling for free
    /// rather than by being kept in step with them.
    /// </remarks>
    private readonly struct RangeOutcome(Error? failure, int completed)
    {
        public Error? Failure { get; } = failure;

        public int Completed { get; } = completed;
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

                i++;
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

            // Only read when there is a row to put it on. An ephemeral step does not pay a
            // clock read to measure a duration nobody records.
            var startedAt = cursor.IsJournaled ? _clock.UtcNow : default;

            StepOutcome outcome;
            Error? thrown = null;

            try
            {
                outcome = await dispatcher.ExecuteAsync(i, scope, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Caller-initiated, or a sibling branch's failure cancelling this one, and
                // not this step's fault — but the work already done still has to be
                // undone, so this joins the failure path rather than propagating.
                //
                // Deliberately not journaled: the token that would guard the write is the
                // one this node may be losing, and a store call on a cancelled path is the
                // least likely of all writes to succeed. The attempt simply has no row, which
                // is what makes it re-runnable on resume.
                failure = FlowErrors.Cancelled(plan.Flow.Id);
                break;
            }
#pragma warning disable CA1031 // A capability that throws is a defect; the engine converts
            catch (Exception exception)  //   it into an error rather than letting it kill the
            {                            //   trigger's consumer loop. This is the one place a
                outcome = StepOutcome.Success;  //   general catch is correct, and it re-reports
                thrown = FlowErrors             //   rather than swallowing.
                    .Unhandled(capabilityId, exception);
            }
#pragma warning restore CA1031

            var stepFailure = thrown ?? outcome.Error;

            // One commit per (instance, scope, step, attempt), before control moves on — the
            // whole of ADR-0015's decision, in one place. A failed attempt is recorded too:
            // the attempt history is what makes the replay contract provable rather than
            // asserted, and an effect that happened before the failure is exactly what a
            // resumed instance must not repeat blindly.
            if (cursor.IsJournaled)
            {
                var refusal = await CommitStepAsync(
                    plan, dispatcher, context, scope, cursor, step, stepFailure, startedAt,
                    capabilityVersion: null, ct)
                    .ConfigureAwait(false);

                if (refusal is not null)
                {
                    // Fenced out, duplicated, or written to a finished instance. Every one of
                    // those means this node is no longer the writer, so it stops rather than
                    // carrying on with work nobody will accept.
                    failure = refusal;
                    break;
                }
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

            i++;
        }

        return new RangeOutcome(failure, completed);
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
            Key = new StepKey(
                run.InstanceId, cursor.Scope, step.Index, run.NextAttempt(cursor.Scope, step.Index)),
            Token = run.Token,
            CapabilityId = JournalIdentity(step),
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

    /// <summary>What the journal records a step as having invoked.</summary>
    /// <remarks>
    /// The same expression <see cref="FlowExecutionContext.EnterStep"/> uses, so a row's
    /// <c>capability_id</c> and the identity a failure is reported against can never disagree
    /// about the same step.
    /// </remarks>
    private static string JournalIdentity(StepNode step) =>
        step.Capability?.Id ?? step.EventType ?? step.SignalType ?? step.SubFlowId ?? string.Empty;

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
        Error? firstFailure = null;

        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(finished);

            var outcome = Observe(finished, plan.Flow.Id);

            completed += outcome.Completed;
            errors[Array.IndexOf(started, finished)] = outcome.Failure;

            if (outcome.Failure is null)
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

        return new RangeOutcome(Verdict(plan, step, merge, errors, succeeded, firstFailure, context), completed);
    }

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

        return new RangeOutcome(IterationVerdict(step, errors, firstFailure, fatal, context), completed);
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
        var childIsDurable = source.Plan.Flow.Profile == ExecutionProfile.Durable;

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
                ct).ConfigureAwait(false);

            if (closed is not null)
            {
                return closed;
            }
        }

        return cursor.IsJournaled
            ? await CommitStepAsync(
                plan, dispatcher, context, scope, cursor, step, failure, startedAt,
                source.Plan.Flow.Version, ct).ConfigureAwait(false)
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
                _ = await CloseInstanceAsync(cursor, TerminalState(result), CancellationToken.None)
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
            _ = context.EnterStep(entry.Step);

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
    /// <strong>This is the whole of the policy engine that P2 ships, and it is one policy at
    /// one stage.</strong> A compensation retry is declared at
    /// <see cref="PolicyStage.Consistency"/>, which is where ADR-0011's fixed order puts
    /// compensation; the unwind is that stage's obligation discharged later, and the retry is
    /// a parameter of it. Nothing at stages 1–6 executes here or anywhere else, so no policy
    /// runs out of order — executing only the last stage cannot skip an earlier one. That is
    /// what makes the slice safe to ship before the engine that runs the other fifteen
    /// policies, and it is why <see cref="PolicyChain"/> remains the only thing in the system
    /// that decides what runs before what.
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
            return FlowErrors.Unhandled(entry.Step.Compensation?.Id ?? context.CapabilityId, exception);
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
            CapabilityId = entry.Step.Compensation?.Id ?? string.Empty,
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
            entry.Step.Compensation?.Id ?? string.Empty,
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

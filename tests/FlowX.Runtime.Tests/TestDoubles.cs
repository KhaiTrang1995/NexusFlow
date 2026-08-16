using FlowX.Runtime;

namespace FlowX.Runtime.Tests;

/// <summary>
/// A hand-written <see cref="IStepDispatcher"/> that records what the engine asked
/// it to do.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately shaped like the code <c>FlowPlanGenerator</c> will emit at
/// WP-5: a switch on the step index, with the step's typed state held in the
/// dispatcher's own fields rather than in a state bag. Writing it by hand first
/// proves the shape works before a generator has to produce it — and if this is
/// awkward to write, the generated version would have been awkward to debug.
/// </para>
/// <para>
/// The type erasure matters. The engine's loop cannot know each step's input and
/// output types; if it boxed them into <c>object</c> it would allocate per step and
/// budget B2 would be lost on the first commit. So the dispatcher keeps the types
/// and the engine sees only success or failure.
/// </para>
/// </remarks>
internal sealed class RecordingDispatcher : IStepDispatcher
{
    /// <summary>
    /// Guards the recording collections, because a parallel flow calls this from several
    /// threads at once.
    /// </summary>
    /// <remarks>
    /// A test double, not the engine, and the lock is here for the same reason a test
    /// double is written by hand at all: an unguarded <c>List&lt;int&gt;.Add</c> under a
    /// fork drops entries at random, and the test that then fails one run in twenty
    /// teaches the team that the suite is flaky rather than that the engine is wrong.
    /// </remarks>
    private readonly Lock _recording = new();

    private readonly Dictionary<int, Error> _failures = [];
    private readonly Dictionary<int, Error> _compensationFailures = [];
    private readonly Dictionary<int, bool> _predicates = [];
    private readonly Dictionary<int, int> _cases = [];
    private readonly HashSet<int> _yieldingSteps = [];

    // Shaped exactly like the two methods the generator emits: a delegate that knows the
    // element type, so the engine sees only an opaque handle and a FlowContext. Writing
    // them by hand first is what proves the contract is implementable at all.
    private readonly Dictionary<int, IterationSource> _collections = [];
    private readonly Dictionary<int, Func<IterationSource, int, FlowContext, FlowContext>> _scopes = [];

    private readonly Dictionary<(int Index, int Visit), Error> _visitFailures = [];
    private readonly Dictionary<int, int> _visits = [];

    // The sub-flow pair, shaped exactly like the two methods the generator emits: the
    // mapping is evaluated here, on the parent's thread, and the seeder is the only code
    // that knows the child's input type.
    private readonly Dictionary<int, (ExecutionPlan Plan, IStepDispatcher Dispatcher)> _children = [];
    private readonly Dictionary<int, Action<FlowContext>> _seeds = [];

    /// <summary>A name for this dispatcher in <see cref="Trace"/>, so two of them can be told apart.</summary>
    public string Name { get; set; } = "flow";

    /// <summary>
    /// A log shared by a parent and its child, so a test can assert an ordering that spans
    /// the boundary.
    /// </summary>
    /// <remarks>
    /// <see cref="Executed"/> and <see cref="Compensated"/> are per-dispatcher and per-index,
    /// which is exactly right until the question is "did the parent's undo run before the
    /// child's" — two lists of indices cannot answer that, because index 2 means a different
    /// step in each. Assigning both dispatchers the same list makes the interleaving visible.
    /// </remarks>
    public List<string> Trace { get; set; } = [];

    /// <summary>Names this dispatcher and gives it a shared trace.</summary>
    public RecordingDispatcher As(string name, List<string> trace)
    {
        Name = name;
        Trace = trace;
        return this;
    }

    /// <summary>Makes step <paramref name="index"/> compose <paramref name="plan"/>.</summary>
    /// <remarks>
    /// The two delegates are the hand-written version of what the generator emits, for the
    /// reason <see cref="IterateOver"/>'s are: the input is carried behind a
    /// <see cref="SubFlowSource"/> the engine treats as opaque, and only this method — which
    /// knows <typeparamref name="TIn"/> — ever seeds it into the child's context.
    /// </remarks>
    public RecordingDispatcher ComposeAt<TIn>(
        int index, ExecutionPlan plan, IStepDispatcher dispatcher, TIn input)
        where TIn : notnull
    {
        _children[index] = (plan, dispatcher);
        _seeds[index] = child => child.Set(input);

        return this;
    }

    /// <summary>Set to make a sub-flow input mapping throw rather than produce an input.</summary>
    public int? ThrowAtSubFlow { get; set; }

    /// <summary>Sub-flow step indices the engine asked to compose, in the order it asked.</summary>
    public List<int> Composed { get; } = [];

    /// <summary>The deadline each child context carried when its first step ran.</summary>
    /// <remarks>
    /// Captured from inside the child, because the child's context is pooled too: reading it
    /// after the composition returned would read a reset instance.
    /// </remarks>
    public List<DateTimeOffset> DeadlinesSeen { get; } = [];

    /// <summary>Step indices executed, in the order the engine invoked them.</summary>
    public List<int> Executed { get; } = [];

    /// <summary>Step indices compensated, in the order the engine unwound them.</summary>
    public List<int> Compensated { get; } = [];

    /// <summary>Branch indices the engine asked about, in the order it asked.</summary>
    /// <remarks>
    /// Recorded so a test can assert the engine consulted the branch <em>once</em>. A
    /// predicate evaluated twice would be free here and expensive in a real flow, where
    /// it reads the context and, in a durable flow, has to answer the same way on replay.
    /// </remarks>
    public List<int> Evaluated { get; } = [];

    /// <summary>Switch indices the engine asked about, in the order it asked.</summary>
    /// <remarks>
    /// Recorded for the same reason as <see cref="Evaluated"/>: a selector consulted
    /// twice would be free here and, in a durable flow, has to answer the same way on
    /// replay.
    /// </remarks>
    public List<int> Selected { get; } = [];

    /// <summary>Set to make a branch throw rather than answer.</summary>
    public int? ThrowAtBranch { get; set; }

    /// <summary>Set to make a switch selector throw rather than answer.</summary>
    public int? ThrowAtSwitch { get; set; }

    /// <summary>Set to make a collection selector throw rather than produce a collection.</summary>
    public int? ThrowAtIteration { get; set; }

    /// <summary>Iteration indices the engine asked for a collection, in the order it asked.</summary>
    /// <remarks>
    /// Recorded so a test can assert the selector ran <em>once</em> for a loop of ten
    /// elements. Running it per element would be free here and, in a durable flow, would
    /// have to produce the same collection every time.
    /// </remarks>
    public List<int> Iterated { get; } = [];

    /// <summary>
    /// The contexts each compensation ran under, in unwind order.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ContextsSeen"/> these <em>can</em> be read after the flow: inside a
    /// loop the entry is the iteration's scope, which is not pooled and still holds its
    /// element. That is what lets a test check that the undo of the third line undid the
    /// third line.
    /// </remarks>
    public List<FlowContext> CompensationScopes { get; } = [];

    /// <summary>The context instances seen, for reference-identity assertions only.</summary>
    /// <remarks>
    /// Do not read values off these after the engine returns: the context is reset the
    /// moment it goes back to the pool, so every field reads as empty. That the naive
    /// version of this test failed is the clearest evidence the reset works — see
    /// <see cref="Snapshots"/> for values captured while the step was running.
    /// </remarks>
    public List<FlowContext> ContextsSeen { get; } = [];

    /// <summary>Context values captured <em>during</em> each step, while they are still live.</summary>
    public List<ContextSnapshot> Snapshots { get; } = [];

    /// <summary>Set to have a step observe cancellation instead of completing.</summary>
    public int? CancelAtStep { get; set; }

    /// <summary>Invoked before each step runs, so a test can advance a fake clock.</summary>
    public Action<int>? BeforeStep { get; set; }

    /// <summary>
    /// Reads something off the context <em>while</em> each step is running, into
    /// <see cref="Observed"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ContextsSeen"/> cannot answer a question about a value, because the
    /// context is pooled and reset the moment the flow returns — a child's context most of
    /// all, since it is given back as soon as the composition is settled. Anything a test
    /// wants to know about what a step could see has to be taken while the step is running,
    /// which is what this is for.
    /// </remarks>
    public Func<FlowContext, object?>? Observe { get; set; }

    /// <summary>What <see cref="Observe"/> returned, in step order.</summary>
    public List<object?> Observed { get; } = [];

    /// <summary>Makes step <paramref name="index"/> fail with <paramref name="error"/>.</summary>
    public RecordingDispatcher FailAt(int index, Error error)
    {
        _failures[index] = error;
        return this;
    }

    /// <summary>
    /// Makes the <paramref name="visit"/>-th execution of step <paramref name="index"/>
    /// fail, counting from one.
    /// </summary>
    /// <remarks>
    /// <see cref="FailAt"/> is not enough inside a loop: one index runs once per element,
    /// so "fail step 3" means "fail every element". Pinning the failure to a visit is what
    /// lets a test say that the <em>third</em> line was the one that was declined.
    /// </remarks>
    public RecordingDispatcher FailAtNthVisit(int index, int visit, Error error)
    {
        _visitFailures[(index, visit)] = error;
        return this;
    }

    /// <summary>Makes the compensation for step <paramref name="index"/> fail.</summary>
    public RecordingDispatcher FailCompensationAt(int index, Error error)
    {
        _compensationFailures[index] = error;
        return this;
    }

    /// <summary>
    /// Makes the first <paramref name="attempts"/> attempts at step <paramref name="index"/>'s
    /// compensation fail, and every one after that succeed.
    /// </summary>
    /// <remarks>
    /// <see cref="FailCompensationAt"/> is not enough once an undo can be retried: "the
    /// broker was down and then came back" is the ordinary transient case, and a double that
    /// can only fail for ever cannot express it.
    /// </remarks>
    public RecordingDispatcher FailCompensationForAttempts(int index, int attempts, Error error)
    {
        _compensationBudget[index] = attempts;
        _compensationFailures[index] = error;
        return this;
    }

    /// <summary>Reads something off the context while each compensation is running.</summary>
    /// <remarks>
    /// The compensation's counterpart of <see cref="Observe"/>, and needed for the same
    /// reason: the context is pooled, so a question about what an undo could see has to be
    /// asked while the undo is running.
    /// </remarks>
    public Action<FlowContext>? OnCompensate { get; set; }

    private readonly Dictionary<int, int> _compensationBudget = [];
    private readonly Dictionary<int, int> _compensationAttempts = [];

    /// <summary>Makes the branch at <paramref name="index"/> answer <paramref name="answer"/>.</summary>
    /// <remarks>An unlisted branch answers <c>true</c>, so a test states only what it cares about.</remarks>
    public RecordingDispatcher AnswerAt(int index, bool answer)
    {
        _predicates[index] = answer;
        return this;
    }

    /// <summary>Makes the switch at <paramref name="index"/> select case <paramref name="arm"/>.</summary>
    /// <remarks>
    /// An unlisted switch selects nothing — arm <c>-1</c> — so a test that says nothing
    /// exercises the default path, which is the arm most likely to be forgotten.
    /// </remarks>
    public RecordingDispatcher SelectAt(int index, int arm)
    {
        _cases[index] = arm;
        return this;
    }

    /// <summary>
    /// Makes step <paramref name="index"/> complete asynchronously rather than
    /// synchronously.
    /// </summary>
    /// <remarks>
    /// The only way to make a fork's branches genuinely overlap. The engine starts each
    /// branch eagerly on the calling thread, so a branch whose every step completes
    /// synchronously runs to the end before its sibling starts — correct, and useless for
    /// proving concurrency. A step that yields lets the sibling in.
    /// </remarks>
    public RecordingDispatcher YieldAt(int index)
    {
        _yieldingSteps.Add(index);
        return this;
    }

    /// <summary>Makes the iteration at <paramref name="index"/> walk <paramref name="items"/>.</summary>
    /// <remarks>
    /// The two delegates are the hand-written version of what the generator emits: the
    /// collection is captured behind an <see cref="IterationSource"/> the engine treats as
    /// opaque, and only this method — which knows <typeparamref name="TItem"/> — ever turns
    /// it back into an element.
    /// </remarks>
    public RecordingDispatcher IterateOver<TItem>(int index, IReadOnlyList<TItem> items)
    {
        _collections[index] = new IterationSource(items, items.Count);
        _scopes[index] = (source, element, ctx) =>
            IterationScope.For(ctx, ((IReadOnlyList<TItem>)source.Items!)[element]);

        return this;
    }

    /// <summary>
    /// Holds step <paramref name="index"/> inside the dispatcher until
    /// <paramref name="release"/> completes, signalling <paramref name="entered"/> on the way in.
    /// </summary>
    /// <remarks>
    /// <see cref="YieldAt"/> lets a sibling in; this keeps a caller <em>in</em> the step, which
    /// is the only shape that can prove a concurrency bound. A bulkhead of one permit is
    /// indistinguishable from no bulkhead at all unless a second caller arrives while the
    /// first still holds the permit, and nothing else here can arrange that.
    /// </remarks>
    public RecordingDispatcher HoldAt(int index, Task release, TaskCompletionSource entered)
    {
        _held = (index, release, entered);
        return this;
    }

    /// <summary>
    /// Holds only the <paramref name="visit"/>th call at step <paramref name="index"/>, and
    /// lets that call notice a cancellation.
    /// </summary>
    /// <remarks>
    /// <see cref="HoldAt"/> holds every visit, which is the right shape for a bulkhead — two
    /// callers, one step — and the wrong one for a hedge, where the two calls are visits to the
    /// same step and only one of them is supposed to be slow. The wait observes the token so
    /// that the losing call ends the way a real one does: cancelled, from inside the capability.
    /// </remarks>
    public RecordingDispatcher HoldAtVisit(int index, int visit, Task release, TaskCompletionSource entered)
    {
        _heldVisit = (index, visit, release, entered);
        return this;
    }

    private (int Index, Task Release, TaskCompletionSource Entered)? _held;

    private (int Index, int Visit, Task Release, TaskCompletionSource Entered)? _heldVisit;

    /// <summary>Highest number of steps observed running at once. 1 means nothing overlapped.</summary>
    public int PeakConcurrency { get; private set; }

    /// <summary>The last <see cref="ForEachOutcome"/> any step could see, or <c>null</c>.</summary>
    public ForEachOutcome? OutcomeAfterTheLoop { get; private set; }

    private int _running;

    /// <inheritdoc />
    public async ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
    {
        BeforeStep?.Invoke(stepIndex);

        if (CancelAtStep == stepIndex)
        {
            ct.ThrowIfCancellationRequested();
        }

        int visit;

        lock (_recording)
        {
            _running++;
            PeakConcurrency = Math.Max(PeakConcurrency, _running);
            Executed.Add(stepIndex);
            Trace.Add(Name + ".run." + stepIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            DeadlinesSeen.Add(ctx.Deadline);

            if (Observe is not null)
            {
                Observed.Add(Observe(ctx));
            }

            ContextsSeen.Add(ctx);
            Snapshots.Add(ContextSnapshot.Of(ctx));

            visit = _visits.TryGetValue(stepIndex, out var seen) ? seen + 1 : 1;
            _visits[stepIndex] = visit;

            // Captured whenever it is there, so a test can read what a loop published
            // without the double having to know which step follows the loop.
            if (ctx.TryGet<ForEachOutcome>(out var outcome))
            {
                OutcomeAfterTheLoop = outcome;
            }
        }

        try
        {
            if (_yieldingSteps.Contains(stepIndex))
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
            }

            if (_held is { } held && held.Index == stepIndex)
            {
                held.Entered.TrySetResult();
                await held.Release.ConfigureAwait(false);
            }

            if (_heldVisit is { } heldVisit && heldVisit.Index == stepIndex && heldVisit.Visit == visit)
            {
                heldVisit.Entered.TrySetResult();
                await heldVisit.Release.WaitAsync(ct).ConfigureAwait(false);
            }

            if (_visitFailures.TryGetValue((stepIndex, visit), out var visitError))
            {
                return StepOutcome.Failed(visitError);
            }

            return _failures.TryGetValue(stepIndex, out var error)
                ? StepOutcome.Failed(error)
                : StepOutcome.Success;
        }
        finally
        {
            lock (_recording)
            {
                _running--;
            }
        }
    }

    /// <summary>Step indices whose fallback capability the engine asked, in order.</summary>
    /// <remarks>
    /// Separate from <see cref="Executed"/> on purpose, and it is the assertion that a
    /// capability fallback is a dispatch of its own rather than a second visit to the step: a
    /// double that recorded both in one list could not tell "the fallback answered" from "the
    /// retry ran once more".
    /// </remarks>
    public List<int> FellBackAt { get; } = [];

    private readonly Dictionary<int, Error> _fallbackFailures = [];
    private readonly Dictionary<int, Action<FlowContext>> _fallbackAnswers = [];

    /// <summary>
    /// Makes step <paramref name="index"/>'s fallback capability answer with
    /// <paramref name="answer"/>.
    /// </summary>
    /// <remarks>
    /// The typed <c>ctx.Set</c> is the whole point: it stands in for the line the generated
    /// <c>ExecuteFallbackAsync</c> emits, which is the only code that may name the contract.
    /// Writing it by hand here is what proves the seam is implementable, exactly as this
    /// double's forward switch proved <c>ExecuteAsync</c>'s.
    /// </remarks>
    public RecordingDispatcher FallBackWith<TValue>(int index, TValue answer)
        where TValue : notnull
    {
        _fallbackAnswers[index] = ctx => ctx.Set(answer);
        return this;
    }

    /// <summary>Makes step <paramref name="index"/>'s fallback capability fail too.</summary>
    public RecordingDispatcher FailFallbackAt(int index, Error error)
    {
        _fallbackFailures[index] = error;
        return this;
    }

    /// <inheritdoc />
    public ValueTask<StepOutcome> ExecuteFallbackAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
    {
        lock (_recording)
        {
            FellBackAt.Add(stepIndex);
            Trace.Add(
                Name + ".fallback." + stepIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (_fallbackFailures.TryGetValue(stepIndex, out var error))
        {
            return ValueTask.FromResult(StepOutcome.Failed(error));
        }

        if (_fallbackAnswers.TryGetValue(stepIndex, out var answer))
        {
            answer(ctx);
        }

        return ValueTask.FromResult(StepOutcome.Success);
    }

    /// <inheritdoc />
    public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
    {
        int attempt;

        lock (_recording)
        {
            Compensated.Add(stepIndex);
            Trace.Add(Name + ".undo." + stepIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            CompensationScopes.Add(ctx);

            attempt = _compensationAttempts.TryGetValue(stepIndex, out var seen) ? seen + 1 : 1;
            _compensationAttempts[stepIndex] = attempt;

            OnCompensate?.Invoke(ctx);
        }

        if (!_compensationFailures.TryGetValue(stepIndex, out var error))
        {
            return ValueTask.FromResult(StepOutcome.Success);
        }

        // An unlisted budget means "fail for ever", which is what FailCompensationAt has
        // always meant and what several existing tests rely on.
        var budget = _compensationBudget.TryGetValue(stepIndex, out var allowed) ? allowed : int.MaxValue;

        return attempt <= budget
            ? ValueTask.FromResult(StepOutcome.Failed(error))
            : ValueTask.FromResult(StepOutcome.Success);
    }

    private readonly Dictionary<int, ValidationOutcome> _validations = [];

    /// <summary>Step indices the engine asked to validate, in the order it asked.</summary>
    public List<int> Validated { get; } = [];

    /// <summary>
    /// Makes step <paramref name="index"/> answer <paramref name="outcome"/> when validated.
    /// </summary>
    /// <remarks>
    /// A stored answer rather than emitted comparisons, because what the engine tests are about
    /// is what stage 3 does with an answer, not how the answer was reached. The comparisons are
    /// the generator's, and <c>GeneratedValidationTests</c> compiles a real contract and runs
    /// the real emitted checks against it — including the assertion that no message carries a
    /// value.
    /// </remarks>
    public RecordingDispatcher ValidatesAt(int index, ValidationOutcome outcome)
    {
        _validations[index] = outcome;
        return this;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A step nothing was configured for answers <see cref="ValidationOutcome.Unavailable"/> —
    /// the interface's own default, reproduced here rather than inherited so that the double
    /// keeps recording what it was asked. That is the shape a hand-written dispatcher has, and
    /// the engine refuses it.
    /// </remarks>
    public ValidationOutcome Validate(int stepIndex, FlowContext ctx)
    {
        lock (_recording)
        {
            Validated.Add(stepIndex);
        }

        return _validations.TryGetValue(stepIndex, out var outcome)
            ? outcome
            : ValidationOutcome.Unavailable;
    }

    /// <inheritdoc />
    public bool Evaluate(int stepIndex, FlowContext ctx)
    {
        lock (_recording)
        {
            Evaluated.Add(stepIndex);
        }

        if (ThrowAtBranch == stepIndex)
        {
            // The realistic failure: a predicate reading a value no step on the path so
            // far produced. FlowContext.Get<T> throws exactly this.
            throw new InvalidOperationException("The predicate read a value no step produced.");
        }

        return !_predicates.TryGetValue(stepIndex, out var answer) || answer;
    }

    /// <inheritdoc />
    public int Select(int stepIndex, FlowContext ctx)
    {
        lock (_recording)
        {
            Selected.Add(stepIndex);
        }

        if (ThrowAtSwitch == stepIndex)
        {
            // The realistic failure: a selector reading a value no step on the path so
            // far produced. FlowContext.Get<T> throws exactly this.
            throw new InvalidOperationException("The selector read a value no step produced.");
        }

        return _cases.TryGetValue(stepIndex, out var arm) ? arm : -1;
    }

    /// <inheritdoc />
    public IterationSource BeginIteration(int stepIndex, FlowContext ctx)
    {
        lock (_recording)
        {
            Iterated.Add(stepIndex);
        }

        if (ThrowAtIteration == stepIndex)
        {
            // The realistic failure: a selector reading a value no step on the path so
            // far produced. FlowContext.Get<T> throws exactly this.
            throw new InvalidOperationException("The selector read a collection no step produced.");
        }

        // An unlisted iteration walks nothing, so a test that says nothing exercises the
        // empty-collection path — which is the one most likely to be forgotten.
        return _collections.TryGetValue(stepIndex, out var source) ? source : IterationSource.Empty;
    }

    /// <inheritdoc />
    public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
        _scopes[stepIndex](source, iteration, ctx);

    /// <inheritdoc />
    public SubFlowSource BeginSubFlow(int stepIndex, FlowContext ctx)
    {
        lock (_recording)
        {
            Composed.Add(stepIndex);
        }

        if (ThrowAtSubFlow == stepIndex)
        {
            // The realistic failure: a mapping reading a value no step on the path so far
            // produced. FlowContext.Get<T> throws exactly this.
            throw new InvalidOperationException("The mapping read a value no step produced.");
        }

        var child = _children[stepIndex];

        return new SubFlowSource(child.Plan, child.Dispatcher, ctx);
    }

    /// <inheritdoc />
    public void EnterSubFlow(int stepIndex, in SubFlowSource source, FlowContext child) =>
        _seeds[stepIndex](child);

    /// <summary>
    /// What each step contributes to a journal row, standing in for what the generator will
    /// emit once the payload writer exists.
    /// </summary>
    /// <remarks>
    /// A delegate rather than a fixed value, because the shape being exercised is that the
    /// <em>dispatcher</em> decides — it is the only code that can name a
    /// <c>JsonTypeInfo&lt;T&gt;</c> and the flow's <c>SensitiveMembers</c>, and a test double
    /// that hard-coded a payload would prove nothing about that division of labour.
    /// </remarks>
    public Func<int, FlowContext, StepJournalEntry>? Describe { get; set; }

    /// <summary>What rehydration does with a resumed instance's journaled state bag.</summary>
    public Action<FlowContext, string>? Restore { get; set; }

    /// <summary>The snapshots the engine handed back for rehydration, in order.</summary>
    /// <remarks>
    /// Recorded so a test can assert the engine asked <em>once</em>, before the first step,
    /// and only when the instance actually committed a snapshot.
    /// </remarks>
    public List<string> Restored { get; } = [];

    /// <inheritdoc />
    public StepJournalEntry DescribeStep(int stepIndex, FlowContext ctx) =>
        Describe?.Invoke(stepIndex, ctx) ?? StepJournalEntry.Nothing;

    /// <inheritdoc />
    public void RestoreState(FlowContext ctx, string stateBagJson)
    {
        lock (_recording)
        {
            Restored.Add(stateBagJson);
        }

        Restore?.Invoke(ctx, stateBagJson);
    }

    /// <summary>What this step's input is, for a cache key. Null means "not cacheable".</summary>
    /// <remarks>
    /// A delegate for <see cref="Describe"/>'s reason, and returning a
    /// <see cref="JournalPayload"/> rather than a string because that is the contract: the
    /// engine hashes what <c>ToJson</c> produced, so a double that handed back a raw string
    /// would not exercise the redaction the key's correctness depends on.
    /// </remarks>
    public Func<int, FlowContext, JournalPayload>? CacheKey { get; set; }

    /// <summary>What this step produced, for the cache to hold.</summary>
    public Func<int, FlowContext, JournalPayload>? CacheEntry { get; set; }

    /// <summary>What an audited step contributes to its record, given the declared redact list.</summary>
    public Func<int, FlowContext, IReadOnlyList<string>, JournalPayload>? Audit { get; set; }

    /// <summary>The redact lists the engine passed, in order.</summary>
    /// <remarks>
    /// Recorded so a test can assert that the list an author wrote reached the payload builder
    /// rather than being dropped between the DSL and the record — which is the whole of what
    /// <c>redact</c> means.
    /// </remarks>
    public List<IReadOnlyList<string>> RedactionsAsked { get; } = [];

    /// <inheritdoc />
    public JournalPayload DescribeCacheKey(int stepIndex, FlowContext ctx) =>
        CacheKey?.Invoke(stepIndex, ctx) ?? JournalPayload.Empty;

    /// <inheritdoc />
    public JournalPayload DescribeCacheEntry(int stepIndex, FlowContext ctx) =>
        CacheEntry?.Invoke(stepIndex, ctx) ?? JournalPayload.Empty;

    /// <inheritdoc />
    public JournalPayload DescribeAudit(int stepIndex, FlowContext ctx, IReadOnlyList<string> redact)
    {
        lock (_recording)
        {
            RedactionsAsked.Add(redact);
        }

        return Audit?.Invoke(stepIndex, ctx, redact) ?? JournalPayload.Empty;
    }
}

/// <summary>An <see cref="IResultCache"/> that holds entries in a dictionary.</summary>
/// <remarks>
/// <para>
/// Here rather than in the conformance project because these tests are about the
/// <em>engine's</em> use of a cache — that a hit skips a dispatch, that a redacted document is
/// not stored — and a real store would make them about a store. The two implementations the
/// contract is actually held to are Redis and PostgreSQL, through
/// <c>ResultCacheConformance</c>.
/// </para>
/// <para>
/// It records every call, because "the engine did not consult the cache" and "the engine
/// consulted it and missed" are different facts and a test that could not tell them apart
/// would pass against an engine that ignored the policy.
/// </para>
/// </remarks>
internal sealed class RecordingCache : IResultCache
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

    /// <summary>Keys the engine asked for, in order.</summary>
    public List<string> Reads { get; } = [];

    /// <summary>Keys and documents the engine stored, in order.</summary>
    public List<(string Key, string Value, TimeSpan Ttl)> Writes { get; } = [];

    /// <summary>Set to fail every call, so a broken cache can be told to degrade.</summary>
    public bool IsDown { get; set; }

    /// <inheritdoc />
    public ValueTask<Result<CacheEntry>> GetAsync(string key, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            Reads.Add(key);

            if (IsDown)
            {
                return ValueTask.FromResult(
                    Result.Fail<CacheEntry>(CacheErrors.Unavailable("the double is down")));
            }

            return ValueTask.FromResult(
                _entries.TryGetValue(key, out var held)
                    ? Result.Ok(new CacheEntry(held, DateTimeOffset.UnixEpoch))
                    : Result.Fail<CacheEntry>(CacheErrors.Miss(key)));
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<bool>> SetAsync(
        string key, string value, TimeSpan ttl, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            Writes.Add((key, value, ttl));

            if (IsDown)
            {
                return ValueTask.FromResult(
                    Result.Fail<bool>(CacheErrors.Unavailable("the double is down")));
            }

            _entries[key] = value;

            return ValueTask.FromResult(Result.Ok(true));
        }
    }
}

/// <summary>An <see cref="IAuditSink"/> that keeps every record it is given.</summary>
internal sealed class RecordingAuditSink : IAuditSink
{
    private readonly Lock _sync = new();

    /// <summary>Every record written, in the order the engine wrote them.</summary>
    public List<AuditRecord> Records { get; } = [];

    /// <summary>Set to refuse every write, so the engine's refusal to degrade can be proved.</summary>
    public Exception? Refusal { get; set; }

    /// <inheritdoc />
    public ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        if (Refusal is not null)
        {
            throw Refusal;
        }

        lock (_sync)
        {
            Records.Add(record);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The values a context carried at the instant a step ran.
/// </summary>
/// <remarks>
/// Needed because the context is pooled and reset. Asserting on the live object after
/// the flow finished would assert on a cleared instance — which is correct behaviour
/// and a useless test.
/// </remarks>
internal readonly record struct ContextSnapshot(
    string FlowId,
    string FlowVersion,
    string CorrelationId,
    string IdempotencyKey,
    string? TenantId,
    string CapabilityId,
    DateTimeOffset UtcNow,
    TimeSpan TimeRemaining,
    Error? Error,
    Guid NewId,
    bool HasRandom)
{
    public static ContextSnapshot Of(FlowContext ctx) => new(
        ctx.FlowId,
        ctx.FlowVersion,
        ctx.CorrelationId,
        ctx.IdempotencyKey,
        ctx.TenantId,
        ctx.CapabilityId,
        ctx.UtcNow,
        ctx.TimeRemaining,
        ctx.Error,
        ctx.NewId(),
        ctx.Random is not null);
}

/// <summary>A clock a test can move, so deadline behaviour is deterministic.</summary>
internal sealed class FakeClock(DateTimeOffset start) : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; } = start;

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan by) => UtcNow += by;

    /// <summary>Every wait the runtime asked for, in the order it asked.</summary>
    public List<TimeSpan> Delays { get; } = [];

    /// <inheritdoc />
    /// <remarks>
    /// Recorded and simulated rather than served, which is the whole reason
    /// <see cref="IClock"/> exists: a suite that actually slept through a compensation
    /// backoff would spend seconds proving something the clock can state exactly. Completing
    /// synchronously also keeps the engine synchronous, which is what the allocation budget
    /// is measured against.
    /// </remarks>
    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        Delays.Add(delay);
        UtcNow += delay;

        return ValueTask.CompletedTask;
    }
}

/// <summary>Plans the engine tests execute.</summary>
internal static class Plans
{
    public static CapabilityDescriptor Validate { get; } =
        CapabilityDescriptor.Create("order.validate", "1.0.0", isIdempotent: true);

    public static CapabilityDescriptor Reserve { get; } =
        CapabilityDescriptor.Create("inventory.reserve", "1.0.0", isIdempotent: true, "inventory-ledger");

    public static CapabilityDescriptor Release { get; } =
        CapabilityDescriptor.Create("inventory.release", "1.0.0", isIdempotent: true, "inventory-ledger");

    public static CapabilityDescriptor Capture { get; } =
        CapabilityDescriptor.Create("payment.capture", "2.1.0", isIdempotent: false, "payment-gateway");

    public static CapabilityDescriptor Refund { get; } =
        CapabilityDescriptor.Create("payment.refund", "2.1.0", isIdempotent: true, "payment-gateway");

    /// <summary>The second rating service a degraded step asks. No side effects, by FLOWX1053.</summary>
    /// <remarks>
    /// A distinct id, which is the whole of what makes a degraded row legible: the journal keys
    /// on <c>(instance, scope, step, attempt)</c> and the column beside the key says which
    /// capability answered, so a row carrying this rather than the step's own is a degraded one
    /// (ADR-0079 §2.2).
    /// </remarks>
    public static CapabilityDescriptor Secondary { get; } =
        CapabilityDescriptor.Create("rating.secondary", "1.0.0", isIdempotent: true);

    /// <summary>Four steps; steps 1 and 2 are compensable; step 3 emits.</summary>
    public static ExecutionPlan FourStepSaga(TimeSpan? deadline = null) => ExecutionPlan.Create(
        FlowDescriptor.Create(
            "order.place", "1.0.0", ExecutionProfile.Ephemeral, deadline ?? TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForCapability(1, Reserve, Release),
            StepNode.ForCapability(2, Capture, Refund),
            StepNode.ForEmit(3, "order.placed"),
        ]));

    /// <summary>
    /// A conditional, written out as the flat layout the compiler produces:
    /// <c>0 validate · 1 branch(else→5) · 2 reserve · 3 capture · 4 jump→6 · 5 validate · 6 emit</c>.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than built by a helper, because the layout <em>is</em> what
    /// these tests are about. A helper that computed the targets would compute them the
    /// same way the emitter does, and a shared bug would then pass on both sides.
    /// Step 2 is compensable so the unwind can be checked to cover only the branch that
    /// actually ran.
    /// </remarks>
    public static ExecutionPlan Conditional() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.review", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForBranch(1, falseTarget: 5),
            StepNode.ForCapability(2, Reserve, Release),
            StepNode.ForCapability(3, Capture),
            StepNode.ForJump(4, target: 6),
            StepNode.ForCapability(5, Validate),
            StepNode.ForEmit(6, "order.reviewed"),
        ]));

    /// <summary>
    /// A <c>When</c> with no <c>Otherwise</c> and nothing after it, so the false path
    /// targets one past the last step and ends the flow.
    /// </summary>
    public static ExecutionPlan ConditionalWithoutOtherwise() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.maybe", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForBranch(1, falseTarget: 3),
            StepNode.ForCapability(2, Capture),
        ]));

    /// <summary>
    /// A three-case switch with a default, written out as the flat layout the compiler
    /// produces:
    /// <c>0 validate · 1 switch(→2,4,6 else 8) · 2 capture · 3 jump→9 · 4 reserve ·
    /// 5 jump→9 · 6 capture · 7 jump→9 · 8 validate · 9 emit</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spelled out rather than built by a helper, for the same reason
    /// <see cref="Conditional"/> is: the layout <em>is</em> what these tests are about,
    /// and a helper computing the targets the way the emitter does would let a shared bug
    /// pass on both sides.
    /// </para>
    /// <para>
    /// Every case block but the last is closed by a jump to the join, and the default
    /// block is not, because nothing follows it to skip. The step in case 1 is
    /// compensable, so an unwind can be checked to cover only the arm that actually ran.
    /// </para>
    /// </remarks>
    public static ExecutionPlan Switching() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.price", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForSwitch(1, [2, 4, 6], defaultTarget: 8),
            StepNode.ForCapability(2, Capture),
            StepNode.ForJump(3, target: 9),
            StepNode.ForCapability(4, Reserve, Release),
            StepNode.ForJump(5, target: 9),
            StepNode.ForCapability(6, Capture),
            StepNode.ForJump(7, target: 9),
            StepNode.ForCapability(8, Validate),
            StepNode.ForEmit(9, "order.priced"),
        ]));

    /// <summary>
    /// A switch with no <c>Default</c> and nothing after it, so a value that matches no
    /// case lands one past the last step and ends the flow.
    /// </summary>
    public static ExecutionPlan SwitchWithoutDefault() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.route", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForSwitch(1, [2, 4], defaultTarget: 5),
            StepNode.ForCapability(2, Reserve),
            StepNode.ForJump(3, target: 5),
            StepNode.ForCapability(4, Capture),
        ]));

    /// <summary>
    /// A three-branch fork, written out as the flat layout the compiler produces:
    /// <c>0 validate · 1 parallel(→2,3,4 join 5) · 2 reserve · 3 capture · 4 validate · 5 emit</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spelled out rather than built by a helper, for the same reason
    /// <see cref="Conditional"/> and <see cref="Switching"/> are.
    /// </para>
    /// <para>
    /// <strong>No closing jumps, unlike a switch.</strong> A branch's range already ends
    /// where the next branch begins, so a jump to the join would be a step that exists only
    /// to say what the range bound already says. A switch needs them because its arms fall
    /// through into one another; a fork's do not, because nothing runs an arm it did not
    /// start.
    /// </para>
    /// <para>
    /// Step 2 is compensable, so an unwind can be checked to cover work a cancelled sibling
    /// had already completed.
    /// </para>
    /// </remarks>
    public static ExecutionPlan Parallel(MergeStrategy merge = default) => ExecutionPlan.Create(
        FlowDescriptor.Create("order.screen", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForParallel(1, [2, 3, 4], joinTarget: 5, merge),
            StepNode.ForCapability(2, Reserve, Release),
            StepNode.ForCapability(3, Capture),
            StepNode.ForCapability(4, Validate),
            StepNode.ForEmit(5, "order.screened"),
        ]));

    /// <summary>
    /// A fork whose branches are two steps each, so a branch is a range rather than a
    /// single index: <c>0 parallel(→1,3 join 5) · 1,2 · 3,4 · 5 emit</c>.
    /// </summary>
    /// <remarks>
    /// The <em>first</em> step of each branch is the compensable one, deliberately. That is
    /// what makes the cancellation test possible: branch 0 completes step 1 before it
    /// reaches anything that can yield, so when a sibling's failure cancels its step 2 there
    /// is provably already work on the unwind stack.
    /// </remarks>
    public static ExecutionPlan ParallelWithMultiStepBranches(MergeStrategy merge = default) => ExecutionPlan.Create(
        FlowDescriptor.Create("order.enrich", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForParallel(0, [1, 3], joinTarget: 5, merge),
            StepNode.ForCapability(1, Reserve, Release),
            StepNode.ForCapability(2, Validate),
            StepNode.ForCapability(3, Capture, Refund),
            StepNode.ForCapability(4, Validate),
            StepNode.ForEmit(5, "order.enriched"),
        ]));

    /// <summary>
    /// A loop, written out as the flat layout the compiler produces:
    /// <c>0 validate · 1 foreach(body 2..4, join 4) · 2 reserve · 3 capture · 4 emit</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spelled out rather than built by a helper, for the same reason
    /// <see cref="Conditional"/>, <see cref="Switching"/> and <see cref="Parallel"/> are.
    /// </para>
    /// <para>
    /// <strong>The body appears once</strong>, however many elements the collection turns
    /// out to hold — that is the whole shape. It has no target of its own and no closing
    /// jump: it is the span between the loop node and its join, and the engine re-enters
    /// that span per element.
    /// </para>
    /// <para>
    /// Step 2 is compensable, so an unwind can be checked to cover every element's work in
    /// strict reverse.
    /// </para>
    /// </remarks>
    public static ExecutionPlan ForEach(int maxDegreeOfParallelism = 1, bool continueOnError = false) =>
        ExecutionPlan.Create(
            FlowDescriptor.Create("order.reserve", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(0, Validate),
                StepNode.ForEach(1, joinTarget: 4, new ForEachOptions
                {
                    MaxDegreeOfParallelism = maxDegreeOfParallelism,
                    ContinueOnError = continueOnError,
                }),
                StepNode.ForCapability(2, Reserve, Release),
                StepNode.ForCapability(3, Capture),
                StepNode.ForEmit(4, "order.reserved"),
            ]));

    /// <summary>
    /// A loop with a single-step body, so the per-element cost is not diluted by the work
    /// inside it: <c>0 foreach(body 1..2, join 2) · 1 reserve · 2 emit</c>.
    /// </summary>
    public static ExecutionPlan ForEachWithOneStepBody(int maxDegreeOfParallelism = 1) => ExecutionPlan.Create(
        FlowDescriptor.Create("order.count", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForEach(0, joinTarget: 2, new ForEachOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
            }),
            StepNode.ForCapability(1, Validate),
            StepNode.ForEmit(2, "order.counted"),
        ]));

    /// <summary>
    /// A loop inside a loop: <c>0 foreach(body 1..4) · 1 foreach(body 2..3) · 2 reserve ·
    /// 3 capture · 4 emit</c>.
    /// </summary>
    /// <remarks>
    /// The inner loop is an ordinary step of the outer's body, numbered from the same flat
    /// counter, and step 3 is what follows it inside that body. Nothing about the layout is
    /// special-cased for nesting — which is the claim worth testing, because the alternative
    /// is a scope chain that resolves to the wrong element.
    /// </remarks>
    public static ExecutionPlan NestedForEach() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.explode", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForEach(0, joinTarget: 4, new ForEachOptions { MaxDegreeOfParallelism = 1 }),
            StepNode.ForEach(1, joinTarget: 3, new ForEachOptions { MaxDegreeOfParallelism = 1 }),
            StepNode.ForCapability(2, Reserve),
            StepNode.ForCapability(3, Capture),
            StepNode.ForEmit(4, "order.exploded"),
        ]));

    /// <summary>
    /// A parent that composes another flow:
    /// <c>0 validate · 1 subflow(order.fulfil) · 2 capture · 3 emit</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spelled out rather than built by a helper, for the same reason every other plan here
    /// is. What the layout shows is the whole claim: the composition is <strong>one
    /// index</strong> with no target, and the step after it is the ordinary next one. None
    /// of the child's steps appear — they are in <see cref="Child"/>, which is a different
    /// array entirely.
    /// </para>
    /// <para>
    /// Step 2 is compensable, so an unwind can be checked to interleave the parent's own
    /// undo with the child's in strict reverse across the boundary.
    /// </para>
    /// </remarks>
    public static ExecutionPlan Composing(SubFlowMode mode = SubFlowMode.Inline) => ExecutionPlan.Create(
        FlowDescriptor.Create("order.place", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForSubFlow(1, "order.fulfil", mode),
            StepNode.ForCapability(2, Capture, Refund),
            StepNode.ForEmit(3, "order.placed"),
        ]));

    /// <summary>
    /// A parent whose only compensable work is the child's:
    /// <c>0 subflow(order.fulfil) · 1 capture</c>, with nothing of its own to undo.
    /// </summary>
    /// <remarks>
    /// The shape that proves the composition node is what makes the plan compensable. If
    /// <c>StepNode.IsCompensable</c> did not report <c>true</c> for an inline sub-flow, this
    /// plan would carry no compensation stack at all and the child's completed work would be
    /// silently unrecoverable when step 1 failed.
    /// </remarks>
    public static ExecutionPlan ComposingOnly() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.thin", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForSubFlow(0, "order.fulfil"),
            StepNode.ForCapability(1, Capture),
        ]));

    /// <summary>
    /// The child: <c>0 reserve (compensable) · 1 validate</c>, on its own deadline.
    /// </summary>
    /// <remarks>
    /// A short declared deadline on purpose — shorter than the parent's — so a test can
    /// show that the child's budget is the smaller of the two rather than whichever one the
    /// engine happened to read last.
    /// </remarks>
    public static ExecutionPlan Child(TimeSpan? deadline = null) => ExecutionPlan.Create(
        FlowDescriptor.Create(
            "order.fulfil", "1.0.0", ExecutionProfile.Ephemeral, deadline ?? TimeSpan.FromSeconds(10)),
        StepGraph.Create([
            StepNode.ForCapability(0, Reserve, Release),
            StepNode.ForCapability(1, Validate),
        ]));

    /// <summary>A child that composes a child: <c>0 subflow(order.fulfil) · 1 validate</c>.</summary>
    public static ExecutionPlan ComposingChild() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.middle", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(20)),
        StepGraph.Create([
            StepNode.ForSubFlow(0, "order.fulfil"),
            StepNode.ForCapability(1, Validate),
        ]));

    /// <summary>Two steps, neither compensable.</summary>
    public static ExecutionPlan TwoStepQuery() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.get", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(5)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForCapability(1, Validate),
        ]));

    /// <summary>One emit step and nothing else, so the first commit is the one that stages.</summary>
    /// <remarks>
    /// Used where the assertion is about the emit step's own commit being refused. In
    /// <see cref="FourStepSaga"/> the emit is last, so three commits would land before the
    /// refusal and the test would be reading an outbox that three unrelated rows had already
    /// had their chance to write to.
    /// </remarks>
    public static ExecutionPlan OneStepEmit() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.place", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForEmit(0, "order.placed"),
        ]));

    public static FlowInvocation Invocation { get; } = new("corr-1", "idem-1", TenantId: "acme");
}

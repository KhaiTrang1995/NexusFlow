using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowX.Conformance.InMemory;
using FlowX.Runtime;

namespace FlowX.Runtime.Tests;

/// <summary>
/// What a corpus flow accumulates as it runs: one line per thing a step did, and the value a
/// predicate or a selector later branches on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the observation mechanism, and it is deliberately the state bag rather
/// than a side channel.</strong> Every step folds what it did — and every ambient value it
/// read — into this record, and the dispatcher hands it to the journal as the step's state
/// bag. So the stored JSON of every row is a byte-wise transcript of the flow up to that
/// step, written by the same path a real flow's state reaches the journal through. A replay
/// that took a different arm, ran a step twice, skipped one, or read a fresh clock produces
/// different JSON on the first row where it differed, and the comparison points at that row.
/// </para>
/// <para>
/// A side channel would have been easier and would have proved less: it would have compared
/// what the harness watched rather than what the journal holds, and the replay contract is
/// about the journal.
/// </para>
/// </remarks>
internal sealed record Ledger
{
    /// <summary>A flow that has not run a step yet.</summary>
    public static Ledger Empty { get; } = new();

    /// <summary>What every step so far did, in order, separated by a bullet.</summary>
    public string Entries { get; init; } = string.Empty;

    /// <summary>
    /// The value the flow's control flow reads.
    /// </summary>
    /// <remarks>
    /// Held in the state bag on purpose. ADR-0015's first amendment says the journal records
    /// what <em>ran</em> and the branch taken is <em>derived</em> by re-evaluating the
    /// predicate against the restored state bag — so a corpus whose predicates read a constant
    /// would exercise none of that. These read this field, which a step wrote, which the
    /// journal stored.
    /// </remarks>
    public int Route { get; init; }

    /// <summary>Appends one entry.</summary>
    public Ledger Then(string what) =>
        this with { Entries = Entries.Length == 0 ? what : Entries + " · " + what };

    /// <summary>Records the value the flow will branch on.</summary>
    public Ledger Routing(int route) => this with { Route = route };
}

/// <summary>The corpus's serialisable state, in the shape the generator will emit.</summary>
/// <remarks>
/// <para>
/// Source-generated, because <see cref="JournalPayload.Of{T}"/> takes a
/// <c>JsonTypeInfo&lt;T&gt;</c> and has no overload that reflects over a type. A corpus
/// contract outside a generated context could not reach the journal at all, which is the
/// property that keeps the write path trim- and NativeAOT-safe — and it means the corpus
/// writes its state bag through exactly the door a real flow does.
/// </para>
/// <para>
/// Its own context rather than an extra <c>[JsonSerializable]</c> on the one
/// <c>DurableSeamTests</c> opened: <c>JsonSourceGenerator</c> emits per declaration and
/// collides on its own hint names when a context is declared partially in two files, so the
/// alternative was not a smaller change but a broken build.
/// </para>
/// </remarks>
[JsonSerializable(typeof(Ledger))]
internal sealed partial class CorpusContracts : JsonSerializerContext;

/// <summary>
/// A clock whose every read is a different instant.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Never repeating is the whole point, and it is what a fixed clock cannot do.</strong>
/// If the original run and the replay both read a clock pinned to one instant, a replay that
/// ignored the capture entirely and re-read the clock would produce the same value and the
/// corpus would pass by comparing nothing — which is the failure mode WP-61's exit criterion
/// names. Here a fresh read is provably not the captured one: the two runs are given clocks a
/// hundred days apart, and within a run no two reads agree either.
/// </para>
/// <para>
/// Simulated rather than served, like <c>FakeClock</c>, so the suite is deterministic and
/// costs no wall time.
/// </para>
/// </remarks>
internal sealed class DriftingClock(DateTimeOffset start) : IClock
{
    private readonly Lock _gate = new();
    private long _reads;

    /// <inheritdoc />
    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
            {
                return start.AddMilliseconds(++_reads);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}

/// <summary>
/// One step of a corpus flow, while it is running: what it can read, and how what it reads
/// is recorded.
/// </summary>
/// <remarks>
/// The ambient accessors are not conveniences. A body that called <c>ctx.UtcNow</c> directly
/// would read the value and leave no trace of having read it, so a replay that read a
/// different one would only be caught if the body happened to fold it into the ledger. Going
/// through here means every ambient read lands in two places at once — the trace, which is
/// in-process, and the ledger, which reaches the journal — and the two channels catch
/// different things.
/// </remarks>
internal readonly struct Running(CorpusFlow flow, int index, FlowContext ctx)
{
    /// <summary>The scope the step is running under.</summary>
    public FlowContext Context => ctx;

    /// <summary>Records what the step did.</summary>
    public void Wrote(string what)
    {
        flow.Note($"run {index} · {what}");
        Fold(ledger => ledger.Then(what));
    }

    /// <summary>Records the value the flow's control flow will read.</summary>
    public void Routes(int route)
    {
        flow.Note($"run {index} · routes to {route}");
        Fold(ledger => ledger.Routing(route).Then($"routed to {route}"));
    }

    /// <summary>Reads <c>ctx.UtcNow</c>, and records that it did.</summary>
    public DateTimeOffset Now()
    {
        var now = ctx.UtcNow;

        flow.Note($"run {index} · ctx.UtcNow = {Text(now)}");
        Fold(ledger => ledger.Then($"clock {Text(now)}"));

        return now;
    }

    /// <summary>Reads <c>ctx.NewId()</c>, and records that it did.</summary>
    public Guid NewId()
    {
        var id = ctx.NewId();

        flow.Note($"run {index} · ctx.NewId() = {id}");
        Fold(ledger => ledger.Then($"id {id}"));

        return id;
    }

    /// <summary>Draws from <c>ctx.Random</c>, and records that it did.</summary>
    public int Roll(int exclusiveUpperBound)
    {
        var drawn = ctx.Random.Next(exclusiveUpperBound);

        flow.Note($"run {index} · ctx.Random.Next({exclusiveUpperBound}) = {Text(drawn)}");
        Fold(ledger => ledger.Then($"roll {Text(drawn)}"));

        return drawn;
    }

    /// <summary>
    /// Mints an id outside the flow's context — the impurity <c>FLOWX1008</c> refuses at
    /// build time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Here so that the harness can be shown to catch one. An analyzer that reports a pattern
    /// and a corpus that would not notice the same pattern are two different guarantees; WP-58
    /// bought the first, and a build in which the rule is suppressed, or a capability the
    /// compilation cannot see, still reaches this runtime.
    /// </para>
    /// <para>
    /// <c>Guid.NewGuid()</c> rather than <c>DateTime.UtcNow</c> for the ambient-clock flavour
    /// of the same defect, because two ids can never coincide and two clock reads on a coarse
    /// platform clock can. A gate whose red depends on timer resolution is a gate that goes
    /// green for the wrong reason on somebody else's machine. The clock flavour is proved
    /// instead by withholding a capture from a step that reads <c>ctx.UtcNow</c>, where the
    /// two runs are a hundred days apart and cannot agree.
    /// </para>
    /// </remarks>
    public Guid AmbientId()
    {
        var id = Guid.NewGuid();

        flow.Note($"run {index} · Guid.NewGuid() = {id}");
        Fold(ledger => ledger.Then($"ambient id {id}"));

        return id;
    }

    /// <summary>
    /// Reads and advances mutable static state — the impurity <c>FLOWX1009</c> refuses.
    /// </summary>
    /// <remarks>
    /// The third ambient family, and the one with no capture envelope of any kind: a step's
    /// row records the clock, the ids and the seed, and nothing at all about a static a flow
    /// happened to read. Strictly increasing, so the two runs provably cannot agree.
    /// </remarks>
    public long AmbientCount()
    {
        var counted = Interlocked.Increment(ref CorpusFlow.AmbientCounter);

        flow.Note($"run {index} · static counter = {Text(counted)}");
        Fold(ledger => ledger.Then($"ambient count {Text(counted)}"));

        return counted;
    }

    private void Fold(Func<Ledger, Ledger> change) =>
        ctx.Set(change(ctx.TryGet<Ledger>(out var held) ? held : Ledger.Empty));

    private static string Text(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// One flow of the replay corpus: a hand-written dispatcher whose steps read the context,
/// write a ledger, and — when it is replaying — are handed the ambient values the journal
/// captured before they run.
/// </summary>
/// <remarks>
/// <para>
/// Shaped like <c>RecordingDispatcher</c>, and deliberately not it. That double records what
/// the <em>engine</em> asked for, which is what the engine's own suites are about. This one
/// records what the <em>flow</em> did and read, which is what a replay has to reproduce, and
/// it carries the one thing a replay driver needs and no other dispatcher does: the capture
/// to hand each step before it runs.
/// </para>
/// <para>
/// <strong>The priming happens here rather than in the engine, and that is a limit worth
/// stating.</strong> The step loop skips a committed step instead of re-running it — that is
/// what resumption is — so no engine path ever reaches a step holding a capture for it. A
/// replay therefore has to be driven from outside, and the first hook outside the engine that
/// runs per step is the dispatcher. The consequence is that the engine's own deadline check,
/// which reads the clock before this method is entered, is not replayed; everything the flow
/// itself reads is.
/// </para>
/// </remarks>
internal sealed class CorpusFlow(string name) : IStepDispatcher
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Action<Running>> _bodies = [];
    private readonly Dictionary<int, Action<Running>> _undos = [];
    private readonly Dictionary<int, Error> _failures = [];
    private readonly Dictionary<int, Func<FlowContext, bool>> _predicates = [];
    private readonly Dictionary<int, Func<FlowContext, int>> _selectors = [];
    private readonly Dictionary<int, IterationSource> _collections = [];
    private readonly Dictionary<int, Func<IterationSource, int, FlowContext, FlowContext>> _scopes = [];
    private readonly Dictionary<int, (ExecutionPlan Plan, CorpusFlow Flow)> _children = [];
    private readonly Dictionary<int, Action<FlowContext>> _seeds = [];
    private readonly List<CorpusFlow> _composed = [];

    private readonly Dictionary<int, Task> _waits = [];
    private readonly Dictionary<int, TaskCompletionSource> _releases = [];

    /// <summary>
    /// The mutable static a corpus flow can be made to read, so that the impurity
    /// <c>FLOWX1009</c> refuses has something to refuse.
    /// </summary>
    /// <remarks>
    /// Deliberately shared and deliberately never reset. Two executions of the same flow read
    /// two different values, which is precisely what makes a static unreplayable and what the
    /// rule exists to prevent.
    /// </remarks>
    internal static long AmbientCounter;

    /// <summary>The trace this flow and every flow it composes write to, in call order.</summary>
    public List<string> Trace { get; set; } = [];

    /// <summary>
    /// The captures this run is being replayed from, keyed by step index and consumed in
    /// commit order; <c>null</c> for an original run.
    /// </summary>
    public CaptureSource? Replaying { get; set; }

    /// <summary>Set to have this flow read one ambient value it was never given.</summary>
    /// <remarks>
    /// The deliberate divergence. A replay driver that primes every step and a corpus that
    /// would not notice one that was missed are two different things.
    /// </remarks>
    public int? SkipPrimingAt { get; set; }

    /// <summary>The root context, for priming a step that runs under an iteration scope.</summary>
    /// <remarks>
    /// <para>
    /// Inside a <c>ForEach</c> body the scope handed to a step is an
    /// <c>IterationScope&lt;TItem&gt;</c>, which forwards <c>UtcNow</c>, <c>NewId()</c> and
    /// <c>Random</c> to the execution context underneath it but is not that context. So the
    /// context to prime is the one the loop itself runs under, which the engine passes to
    /// every call that is not inside the body — and which is captured here the first time one
    /// of those calls arrives.
    /// </para>
    /// <para>
    /// A public <c>FlowContext.Root</c> would be tidier and would widen a forever-contract for
    /// a test harness's benefit. This is the smaller price.
    /// </para>
    /// </remarks>
    private FlowExecutionContext? _root;

    /// <summary>This flow's name in the trace.</summary>
    public string Name => name;

    /// <summary>The flows this one composes, outermost first, in composition order.</summary>
    public IReadOnlyList<CorpusFlow> Descendants
    {
        get
        {
            var found = new List<CorpusFlow> { this };

            for (var i = 0; i < found.Count; i++)
            {
                found.AddRange(found[i]._composed);
            }

            return found;
        }
    }

    /// <summary>Gives every flow in this tree the same trace and the same replay stance.</summary>
    public void Drive(List<string> trace, IReadOnlyList<CaptureSource>? captures)
    {
        var tree = Descendants;

        for (var i = 0; i < tree.Count; i++)
        {
            tree[i].Trace = trace;
            tree[i].Replaying = captures is null ? null : captures[Math.Min(i, captures.Count - 1)];
        }
    }

    /// <summary>Makes step <paramref name="index"/> do <paramref name="body"/>.</summary>
    public CorpusFlow Does(int index, Action<Running> body)
    {
        _bodies[index] = body;
        return this;
    }

    /// <summary>Makes step <paramref name="index"/>'s undo do <paramref name="body"/>.</summary>
    public CorpusFlow Undoes(int index, Action<Running> body)
    {
        _undos[index] = body;
        return this;
    }

    /// <summary>Makes step <paramref name="index"/> fail.</summary>
    public CorpusFlow Fails(int index, Error error)
    {
        _failures[index] = error;
        return this;
    }

    /// <summary>Makes the branch at <paramref name="index"/> read the context.</summary>
    public CorpusFlow Answers(int index, Func<FlowContext, bool> predicate)
    {
        _predicates[index] = predicate;
        return this;
    }

    /// <summary>Makes the switch at <paramref name="index"/> read the context.</summary>
    public CorpusFlow Selects(int index, Func<FlowContext, int> selector)
    {
        _selectors[index] = selector;
        return this;
    }

    /// <summary>Makes the loop at <paramref name="index"/> walk <paramref name="items"/>.</summary>
    public CorpusFlow Iterates<TItem>(int index, IReadOnlyList<TItem> items)
    {
        _collections[index] = new IterationSource(items, items.Count);
        _scopes[index] = (source, element, ctx) =>
            IterationScope.For(ctx, ((IReadOnlyList<TItem>)source.Items!)[element]);

        return this;
    }

    /// <summary>Makes step <paramref name="index"/> compose <paramref name="child"/>.</summary>
    public CorpusFlow Composes<TIn>(int index, ExecutionPlan plan, CorpusFlow child, TIn input)
        where TIn : notnull
    {
        _children[index] = (plan, child);
        _seeds[index] = context => context.Set(input);
        _composed.Add(child);

        return this;
    }

    /// <summary>
    /// Makes step <paramref name="index"/> suspend after its body until <paramref name="gate"/>
    /// is released, and step <paramref name="releasedBy"/> release it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A rendezvous rather than a yield, because the fidelity limit being demonstrated
    /// is an ordering and a yield only makes one possible.</strong> A test that reproduces a
    /// race one run in twenty teaches a team that the suite is flaky; this pins the exact
    /// interleaving ADR-0015 describes — a sibling minting an id between one branch's body
    /// finishing and its commit — so the demonstration is the same on every run and on every
    /// machine.
    /// </para>
    /// <para>
    /// The source completes its continuations asynchronously on purpose: the default would
    /// run the suspended branch inline on the releasing branch's thread, which is the one
    /// interleaving this is not trying to show.
    /// </para>
    /// </remarks>
    public CorpusFlow Rendezvous(int index, int releasedBy)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _waits[index] = gate.Task;
        _releases[releasedBy] = gate;

        return this;
    }

    /// <summary>Appends one line to the shared trace.</summary>
    public void Note(string what)
    {
        lock (_gate)
        {
            Trace.Add($"{name} {what}");
        }
    }

    /// <inheritdoc />
    public async ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
    {
        Remember(ctx);
        Prime(stepIndex);

        if (_bodies.TryGetValue(stepIndex, out var body))
        {
            body(new Running(this, stepIndex, ctx));
        }
        else
        {
            new Running(this, stepIndex, ctx).Wrote($"step {stepIndex.ToString(CultureInfo.InvariantCulture)}");
        }

        if (_releases.TryGetValue(stepIndex, out var release))
        {
            release.TrySetResult();
        }

        if (_waits.TryGetValue(stepIndex, out var wait))
        {
            await wait.ConfigureAwait(false);
        }

        return _failures.TryGetValue(stepIndex, out var error)
            ? StepOutcome.Failed(error)
            : StepOutcome.Success;
    }

    /// <inheritdoc />
    public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
    {
        Remember(ctx);

        if (_undos.TryGetValue(stepIndex, out var undo))
        {
            undo(new Running(this, stepIndex, ctx));
        }
        else
        {
            Note($"undo {stepIndex.ToString(CultureInfo.InvariantCulture)}");
        }

        return ValueTask.FromResult(StepOutcome.Success);
    }

    /// <inheritdoc />
    public bool Evaluate(int stepIndex, FlowContext ctx)
    {
        Remember(ctx);

        var taken = !_predicates.TryGetValue(stepIndex, out var predicate) || predicate(ctx);

        Note($"evaluate {stepIndex.ToString(CultureInfo.InvariantCulture)} -> {taken}");

        return taken;
    }

    /// <inheritdoc />
    public int Select(int stepIndex, FlowContext ctx)
    {
        Remember(ctx);

        var arm = _selectors.TryGetValue(stepIndex, out var selector) ? selector(ctx) : -1;

        Note($"select {stepIndex.ToString(CultureInfo.InvariantCulture)} -> {arm.ToString(CultureInfo.InvariantCulture)}");

        return arm;
    }

    /// <inheritdoc />
    public IterationSource BeginIteration(int stepIndex, FlowContext ctx)
    {
        Remember(ctx);

        var source = _collections.TryGetValue(stepIndex, out var held) ? held : IterationSource.Empty;

        Note($"iterate {stepIndex.ToString(CultureInfo.InvariantCulture)} -> {source.Count.ToString(CultureInfo.InvariantCulture)}");

        return source;
    }

    /// <inheritdoc />
    public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx)
    {
        Remember(ctx);
        Note($"element {stepIndex.ToString(CultureInfo.InvariantCulture)}[{iteration.ToString(CultureInfo.InvariantCulture)}]");

        return _scopes[stepIndex](source, iteration, ctx);
    }

    /// <inheritdoc />
    public SubFlowSource BeginSubFlow(int stepIndex, FlowContext ctx)
    {
        Remember(ctx);

        var child = _children[stepIndex];

        Note($"compose {stepIndex.ToString(CultureInfo.InvariantCulture)} -> {child.Flow.Name}");

        return new SubFlowSource(child.Plan, child.Flow, ctx);
    }

    /// <inheritdoc />
    public void EnterSubFlow(int stepIndex, in SubFlowSource source, FlowContext child) =>
        _seeds[stepIndex](child);

    /// <inheritdoc />
    public StepJournalEntry DescribeStep(int stepIndex, FlowContext ctx) => StepJournalEntry.Of(
        null,
        JournalPayload.Of(
            ctx.TryGet<Ledger>(out var ledger) ? ledger : Ledger.Empty, CorpusContracts.Default.Ledger));

    /// <inheritdoc />
    public void RestoreState(FlowContext ctx, string stateBagJson)
    {
        Remember(ctx);
        Note($"restore {stateBagJson}");

        ctx.Set(JsonSerializer.Deserialize(stateBagJson, CorpusContracts.Default.Ledger)!);
    }

    /// <summary>Keeps the execution context a step under an iteration scope cannot reach.</summary>
    private void Remember(FlowContext ctx)
    {
        if (ctx is FlowExecutionContext execution)
        {
            _root = execution;
        }
    }

    /// <summary>Hands the step about to run what the journal says it read last time.</summary>
    /// <remarks>
    /// A step with no row is not primed and reads the world — which is the honest behaviour
    /// for a step the original execution never ran, and shows up as a divergence the moment
    /// its row is compared against nothing.
    /// </remarks>
    private void Prime(int stepIndex)
    {
        if (Replaying is not { } source || SkipPrimingAt == stepIndex)
        {
            return;
        }

        if (_root is not { } context)
        {
            throw new InvalidOperationException(
                $"Flow '{name}' reached step {stepIndex.ToString(CultureInfo.InvariantCulture)} " +
                "without the harness ever seeing its execution context, so it cannot be given " +
                "the capture to replay.");
        }

        if (source.Next(stepIndex) is { } captured)
        {
            context.ReplayNondeterminism(captured);
        }
    }
}

/// <summary>
/// The captures of one journaled instance, handed back a step at a time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Keyed by step index and consumed in commit order, not by position.</strong> A
/// <c>ForEach</c> commits the same step index once per element and a retry commits it again,
/// so "the next row" is only meaningful within one index; and a <c>SubFlow</c> commits a row
/// for a step that never reaches <c>ExecuteAsync</c> at all, so a strict one-row-per-call
/// pairing would slip by one and blame the wrong step. Asking for the next unconsumed row of
/// the index that is about to run is the pairing that survives every shape in the corpus.
/// </para>
/// <para>
/// Running out is not an error here. A step the original never ran has no capture, is not
/// primed, and reads the world — and the row it then writes is what the comparison reports.
/// Throwing would replace a divergence report with a stack trace.
/// </para>
/// </remarks>
internal sealed class CaptureSource
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Queue<NondeterminismCapture>> _byStep = [];

    /// <summary>Reads the successful forward rows of one instance, in commit order.</summary>
    /// <remarks>
    /// Successful rows only. A failed attempt's capture belongs to an attempt the replay makes
    /// again from the start, and a compensation row carries no capture at all — the engine
    /// does not take one when it commits an undo, which is a fidelity limit
    /// <c>ReplayDeterminismTests</c> pins rather than papers over.
    /// </remarks>
    public CaptureSource(IReadOnlyList<JournalStep> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        foreach (var row in rows.OrderBy(row => row.Sequence))
        {
            if (row.Outcome is JournalOutcome.Compensated)
            {
                continue;
            }

            if (!_byStep.TryGetValue(row.Key.StepId, out var queued))
            {
                queued = new Queue<NondeterminismCapture>();
                _byStep[row.Key.StepId] = queued;
            }

            queued.Enqueue(row.Nondeterminism);
        }
    }

    /// <summary>The next unconsumed capture for a step index, or null when there is none.</summary>
    public NondeterminismCapture? Next(int stepIndex)
    {
        lock (_gate)
        {
            return _byStep.TryGetValue(stepIndex, out var queued) && queued.Count > 0
                ? queued.Dequeue()
                : null;
        }
    }
}

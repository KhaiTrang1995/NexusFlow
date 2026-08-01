using FlowX.Conformance.InMemory;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// What <c>ctx.CapabilityId</c> names while a saga unwinds.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The defect these tests were written against.</strong> The engine entered each
/// compensation with the <em>forward</em> step node, so inside <c>inventory.release</c> the
/// context reported <c>inventory.reserve</c>. A compensating capability that derived an
/// idempotency key from <c>ctx.CapabilityId</c> — which is the obvious thing to do, and what
/// <c>samples/banking</c> did first — produced a key byte-identical to the forward step's.
/// A store that deduplicates on that key then treated the contra entry as a replay of the
/// original, returned the original's result, and moved nothing; the capability reported
/// success and the engine reported <see cref="CompensationOutcome.Succeeded"/> over an undo
/// that never happened.
/// </para>
/// <para>
/// <strong>Why it was invisible.</strong> The journal row was already right — the engine
/// writes the <em>compensating</em> capability's id onto it — so the audit trail said
/// <c>inventory.release</c> ran while the code that ran saw <c>inventory.reserve</c>. Two
/// readers of "what is executing" disagreeing is what let the loss hide behind
/// correct-looking telemetry, so the agreement between them is asserted here rather than
/// left to be inferred.
/// </para>
/// </remarks>
public sealed class CompensationIdentityTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static readonly Error Declined =
        new("payment.declined", "issuer declined", ErrorCategory.Conflict);

    private static readonly FencingToken First = new(1);

    private static readonly string[] TwoLines = ["a", "b"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FlowEngine Engine() => new(new FakeClock(T0));

    /// <summary>The key a capability that keys on its own identity would write under.</summary>
    /// <remarks>
    /// Deliberately the naive expression — <c>ctx.IdempotencyKey + ":" + ctx.CapabilityId</c>
    /// — because the whole point is that the obvious thing has to be the correct thing. A
    /// helper that took the compensating id as an argument would prove only that a
    /// workaround works.
    /// </remarks>
    private static string KeyFor(CapabilityContext ctx) => ctx.IdempotencyKey + ":" + ctx.CapabilityId;

    private static ExecutionPlan Durable(ExecutionPlan plan) => ExecutionPlan.Create(
        FlowDescriptor.Create(
            plan.Flow.Id, plan.Flow.Version, ExecutionProfile.Durable, plan.Flow.Deadline),
        plan.Graph);

    // -------------------------------------------------------------- the characterising test

    /// <summary>
    /// An undo that keys on the context is keyed differently from the write it reverses.
    /// </summary>
    [Fact]
    public async Task AnUndoKeyedOnTheContextDoesNotCollideWithTheStepItUndoes()
    {
        var forward = new List<string>();
        var undo = new List<string>();

        var dispatcher = new RecordingDispatcher().FailAt(2, Declined);
        dispatcher.Observe = ctx => { forward.Add(KeyFor(ctx)); return null; };
        dispatcher.OnCompensate = ctx => undo.Add(KeyFor(ctx));

        var result = await Engine().ExecuteAsync(Plans.FourStepSaga(), dispatcher, Plans.Invocation, Ct);

        result.Compensation.ShouldBe(CompensationOutcome.Succeeded);

        undo.ShouldBe(["idem-1:inventory.release"],
            "The capability that ran is inventory.release, so that is what it must be able " +
            "to key its contra write on. Reading inventory.reserve here is the whole defect: " +
            "the key would be the debit's own key and the store would dedupe the undo away.");

        undo.ShouldNotContain(
            forward[1],
            "An undo keyed identically to the write it reverses is deduplicated away by any " +
            "store that honours the idempotency key, and both halves then report success.");
    }

    // ------------------------------------------------------------- the forward path is unchanged

    [Fact]
    public async Task AStepOnTheForwardPathStillNamesItself()
    {
        var dispatcher = new RecordingDispatcher();

        await Engine().ExecuteAsync(Plans.FourStepSaga(), dispatcher, Plans.Invocation, Ct);

        dispatcher.Snapshots.Select(static s => s.CapabilityId)
            .ShouldBe(["order.validate", "inventory.reserve", "payment.capture", "order.placed"],
                "Nothing about the forward path changes: a step names the capability it is, " +
                "and a kind that has no capability still names the one thing it is about.");
    }

    // --------------------------------------------------------------- the journal and the context

    /// <summary>
    /// The row an operator reads and the identity the code reads are the same string.
    /// </summary>
    /// <remarks>
    /// The journal was the half that was already right, which is exactly why the
    /// disagreement was invisible — an audit trail that says <c>inventory.release</c> ran
    /// looks like proof that it did.
    /// </remarks>
    [Fact]
    public async Task TheJournalRowAndTheContextAgreeDuringAnUnwind()
    {
        var journal = new InMemoryFlowJournal();
        var plan = Durable(Plans.FourStepSaga());
        var instanceId = Guid.NewGuid();

        var begun = await DurableExecution.BeginAsync(
            journal, plan, Plans.Invocation, instanceId, First, input: null, Ct);
        begun.IsSuccess.ShouldBeTrue(begun.IsFailure ? begun.Error.ToString() : null);

        var seen = new List<string>();
        var dispatcher = new RecordingDispatcher().FailAt(2, Declined);
        dispatcher.OnCompensate = ctx => seen.Add(ctx.CapabilityId);

        await Engine().ExecuteAsync(plan, dispatcher, Plans.Invocation, begun.Value, Ct);

        var frontier = await journal.ReadResumeFrontierAsync(instanceId, Ct);
        frontier.IsSuccess.ShouldBeTrue();

        var undone = frontier.Value.Committed
            .Where(static row => row.Outcome == JournalOutcome.Compensated)
            .Select(static row => row.CapabilityId)
            .ToList();

        undone.ShouldBe(["inventory.release"]);
        seen.ShouldBe(undone, "The store and the context must answer 'what ran' identically.");
    }

    // ------------------------------------------------------------------------- across a sub-flow

    /// <summary>
    /// A child's undo names the child's compensation, never the composition that ran it.
    /// </summary>
    [Fact]
    public async Task AChildsCompensationNamesTheChildsCapabilityNotTheParents()
    {
        var trace = new List<string>();
        var child = new RecordingDispatcher().As("child", trace);
        var parent = new RecordingDispatcher().As("parent", trace);

        parent.ComposeAt(1, Plans.Child(), child, new FulfilOrder("o-1"));
        parent.FailAt(2, Declined);

        var seen = new List<string>();
        child.OnCompensate = ctx => seen.Add(ctx.CapabilityId);
        parent.OnCompensate = ctx => seen.Add(ctx.CapabilityId);

        await Engine().ExecuteAsync(Plans.Composing(), parent, Plans.Invocation, Ct);

        seen.ShouldBe(["inventory.release"],
            "The child completed inventory.reserve, the parent then failed, and the child's " +
            "undo runs against the child's own context — which must name the child's " +
            "compensation and not order.fulfil, the composition being unwound.");
    }

    // ---------------------------------------------------------------------- inside a loop

    /// <summary>
    /// An undo inside a <c>ForEach</c> body names the compensation too.
    /// </summary>
    /// <remarks>
    /// The body runs under an <c>IterationScope</c>, a second context that forwards every
    /// value to the flow's own. A fix applied only to the pooled context would be invisible
    /// from inside a loop, which is where a saga most often has compensable work.
    /// </remarks>
    [Fact]
    public async Task AnUndoInsideALoopNamesTheCompensation()
    {
        var dispatcher = new RecordingDispatcher()
            .IterateOver(1, TwoLines)
            .FailAtNthVisit(3, 2, Declined);

        var seen = new List<string>();
        dispatcher.OnCompensate = ctx => seen.Add(ctx.CapabilityId);

        await Engine().ExecuteAsync(Plans.ForEach(), dispatcher, Plans.Invocation, Ct);

        seen.ShouldBe(["inventory.release", "inventory.release"],
            "Both elements' reservations are released, and each undo names the capability " +
            "releasing them rather than the one that reserved.");
    }

    // ------------------------------------------------------- the other half of the question

    /// <summary>
    /// The step being undone is still readable, on a member of its own.
    /// </summary>
    /// <remarks>
    /// An operator reading a trace, and a log line explaining why an undo is running at all,
    /// want the fact <c>ctx.CapabilityId</c> used to carry. It did not stop being useful; it
    /// stopped being the answer to "what is running", which is the question every other
    /// reader was asking of it.
    /// </remarks>
    [Fact]
    public async Task TheContextAlsoNamesTheStepBeingUndone()
    {
        var dispatcher = new RecordingDispatcher().FailAt(2, Declined);

        var undoing = new List<string?>();
        dispatcher.OnCompensate = ctx => undoing.Add(ctx.CompensatingFor);

        await Engine().ExecuteAsync(Plans.FourStepSaga(), dispatcher, Plans.Invocation, Ct);

        undoing.ShouldBe(["inventory.reserve"],
            "Both facts are on the type because both have readers, and neither is derivable " +
            "from the other at the point of use.");
    }

    [Fact]
    public async Task AStepOnTheForwardPathIsNotCompensating()
    {
        var dispatcher = new RecordingDispatcher();

        var seen = new List<bool>();
        dispatcher.Observe = ctx => { seen.Add(ctx.IsCompensating); return ctx.CompensatingFor; };

        await Engine().ExecuteAsync(Plans.FourStepSaga(), dispatcher, Plans.Invocation, Ct);

        seen.ShouldAllBe(static compensating => !compensating);
        dispatcher.Observed.ShouldAllBe(static undoing => undoing == null);
    }

    /// <summary>
    /// The pooled context does not carry an unwind's identity into the next flow.
    /// </summary>
    /// <remarks>
    /// The field is written on the failure path and the context is returned to the pool the
    /// moment the flow settles, so a flow that never compensates would otherwise be told it
    /// was compensating something from a run that finished before it started — possibly
    /// another tenant's.
    /// </remarks>
    [Fact]
    public async Task AnUnwindDoesNotLeakIntoTheNextFlowThroughThePool()
    {
        var engine = Engine();

        await engine.ExecuteAsync(
            Plans.FourStepSaga(), new RecordingDispatcher().FailAt(2, Declined), Plans.Invocation, Ct);

        var next = new RecordingDispatcher();
        next.Observe = ctx => ctx.CompensatingFor;

        await engine.ExecuteAsync(Plans.FourStepSaga(), next, Plans.Invocation, Ct);

        next.Observed.ShouldAllBe(static undoing => undoing == null);
    }

    /// <summary>The child's input contract, mapped before the child starts.</summary>
    private sealed record FulfilOrder(string OrderId);
}

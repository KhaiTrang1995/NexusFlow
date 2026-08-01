using System.Text.Json;
using System.Text.Json.Serialization;
using FlowX.Conformance.InMemory;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// The seam WP-52 is named for: the runtime reads <see cref="ExecutionProfile"/>, a
/// <c>Durable</c> flow journals its step boundaries, and a resumed instance is the same step
/// loop re-entered from a cursor derived by replaying the journal.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The journal under test is the reference implementation from the conformance
/// suite</strong>, not one written for these tests. A store written here would be a store
/// nothing holds to <c>JournalConformance</c>, and the first thing it would get wrong is the
/// fencing check — which is the behaviour the whole seam's correctness rests on. Everything
/// asserted below is asserted against a store that passes 100 % of that suite; the only
/// addition is a note of which instances it was asked to open, because a composed child's id
/// is minted by the engine and a test cannot know it in advance.
/// </para>
/// <para>
/// <strong>What is deliberately not here.</strong> Lease acquisition and renewal, the
/// recovery scan that decides <em>which</em> instance to pick up, the Postgres and Redis
/// adapters, the outbox, and replaying a captured non-determinism envelope back into
/// execution. Those are WP-53 to WP-56 and WP-61. What is here is the boundary they all
/// write through.
/// </para>
/// </remarks>
public sealed class DurableSeamTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static readonly FencingToken First = new(1);
    private static readonly FencingToken Second = new(2);

    /// <summary>Collections a loop walks. Fields, so the same array is passed every time.</summary>
    private static readonly string[] ThreeLines = ["a", "b", "c"];

    private static readonly string[] TwoOrders = ["x", "y"];

    private static readonly int[] TwoLines = [1, 2];

    /// <summary>
    /// The same plan, re-declared <c>Durable</c>.
    /// </summary>
    /// <remarks>
    /// Rebuilt from an existing graph rather than written out again, and that is the point of
    /// the suite as a whole: a durable flow is not a different shape, it is the same shape
    /// with a different declaration. If a plan had to be authored differently to be
    /// journaled, the seam would be a second engine wearing one engine's name.
    /// </remarks>
    private static ExecutionPlan Durable(ExecutionPlan plan) => ExecutionPlan.Create(
        FlowDescriptor.Create(
            plan.Flow.Id, plan.Flow.Version, ExecutionProfile.Durable, plan.Flow.Deadline),
        plan.Graph);

    /// <summary>
    /// Runs a flow that is killed part-way, and returns the instance it left behind.
    /// </summary>
    /// <remarks>
    /// The journal throws rather than refusing, which is the contract's own distinction: a
    /// refusal is a working store saying no, an exception is a store that is unreachable. So
    /// the process stops mid-step with its committed prefix intact and nothing unwound, and
    /// the instance is left <c>Running</c> for another node — which is what a SIGKILL leaves
    /// behind, and the state resumption exists for.
    /// </remarks>
    private static async Task KilledAsync(
        FlowEngine engine, ExecutionPlan plan, IStepDispatcher dispatcher, DurableExecution run)
    {
        await Should.ThrowAsync<NodeDiedException>(() => engine
            .ExecuteAsync(plan, dispatcher, Plans.Invocation, run, TestContext.Current.CancellationToken)
            .AsTask());
    }

    private static async Task<DurableExecution> BeginAsync(
        WatchedJournal journal, ExecutionPlan plan, Guid instanceId, JournalPayload? input = null)
    {
        var begun = await DurableExecution.BeginAsync(
            journal, plan, Plans.Invocation, instanceId, First, input);

        begun.IsSuccess.ShouldBeTrue(begun.IsFailure ? begun.Error.ToString() : null);

        return begun.Value;
    }

    private static async Task<IReadOnlyList<JournalStep>> RowsAsync(IFlowJournal journal, Guid instanceId)
    {
        var frontier = await journal.ReadResumeFrontierAsync(instanceId, TestContext.Current.CancellationToken);

        frontier.IsSuccess.ShouldBeTrue();

        return frontier.Value.Committed;
    }

    private static string Where(JournalStep row) => $"{row.Key.StepId}[{row.Key.Scope.Text}]";

    // ---------------------------------------------------------------------------------
    // The profile is now behaviour
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A flow declaring <c>Durable</c> and given no journal is refused, not run.
    /// </summary>
    /// <remarks>
    /// This is the behaviour <c>FLOWX1028</c> existed to warn about, inverted. Before this
    /// package a <c>Durable</c> flow ran the ephemeral path — no journal, no lease, no
    /// resume — and a process kill lost it, silently. Running it silently again here would
    /// reintroduce the gap one layer lower, where no diagnostic is left to raise it.
    /// </remarks>
    [Fact]
    public async Task ADurableFlowStartedWithoutAJournalIsRefusedRatherThanRunEphemerally()
    {
        var engine = new FlowEngine(new FakeClock(T0));
        var dispatcher = new RecordingDispatcher();

        var result = await engine.ExecuteAsync(
            Durable(Plans.FourStepSaga()), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("flow.durability_not_configured");
        result.CompletedSteps.ShouldBe(0);

        dispatcher.Executed.ShouldBeEmpty(
            "A refused flow must not have run a step: there is nothing to compensate, and an " +
            "effect that happened with no row to record it is exactly the state resumption " +
            "cannot reason about.");
    }

    /// <summary>An <c>Ephemeral</c> flow handed a journal is refused for the mirror reason.</summary>
    /// <remarks>
    /// The profile is the declaration in both directions. Journaling a flow whose author
    /// declined durability would charge it a store round trip per step for a guarantee it did
    /// not ask for, and quietly ignoring the journal would leave the caller believing an
    /// instance exists that nothing will ever write to.
    /// </remarks>
    [Fact]
    public async Task AnEphemeralFlowHandedAJournalIsRefused()
    {
        var journal = new WatchedJournal();
        var plan = Plans.FourStepSaga();
        var instanceId = Guid.NewGuid();
        var run = await BeginAsync(journal, plan, instanceId);

        var engine = new FlowEngine(new FakeClock(T0));

        var result = await engine.ExecuteAsync(
            plan, new RecordingDispatcher(), Plans.Invocation, run, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("flow.profile_is_not_durable");

        (await RowsAsync(journal, instanceId)).ShouldBeEmpty();
    }

    /// <summary>An ordinary ephemeral run is never even asked about a journal.</summary>
    /// <remarks>
    /// The negative control for the whole package. Every other assertion here is about what a
    /// durable flow writes; this is the one that says an ephemeral flow's behaviour did not
    /// change, which is the half a seam is most likely to break.
    /// </remarks>
    [Fact]
    public async Task AnEphemeralFlowIsTheExecutionItAlwaysWas()
    {
        var engine = new FlowEngine(new FakeClock(T0));

        var dispatcher = new RecordingDispatcher
        {
            Describe = static (_, _) => throw new InvalidOperationException(
                "An ephemeral flow must never be asked to describe a step for a journal."),
        };

        var result = await engine.ExecuteAsync(
            Plans.FourStepSaga(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.CompletedSteps.ShouldBe(4);
        dispatcher.Executed.ShouldBe([0, 1, 2, 3]);
    }

    // ---------------------------------------------------------------------------------
    // What journals, and when
    // ---------------------------------------------------------------------------------

    /// <summary>One row per step boundary, in commit order, keyed as ADR-0015 fixes it.</summary>
    [Fact]
    public async Task ADurableFlowCommitsOneRowPerStepBoundary()
    {
        var journal = new WatchedJournal();
        var plan = Durable(Plans.FourStepSaga());
        var instanceId = Guid.NewGuid();
        var run = await BeginAsync(journal, plan, instanceId);

        var engine = new FlowEngine(new FakeClock(T0));

        var result = await engine.ExecuteAsync(
            plan, new RecordingDispatcher(), Plans.Invocation, run, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();

        var rows = await RowsAsync(journal, instanceId);

        rows.Select(static row => row.Key.StepId).ShouldBe([0, 1, 2, 3]);
        rows.ShouldAllBe(row => row.Key.InstanceId == instanceId);
        rows.ShouldAllBe(row => row.Key.Scope == StepScope.Root);
        rows.ShouldAllBe(row => row.Key.Attempt == 1);
        rows.ShouldAllBe(row => row.Outcome == JournalOutcome.Success);

        // The capability and the version it resolved to, per step, so a mid-flight
        // deployment cannot change what a replay of this instance means.
        rows.Select(static row => row.CapabilityId)
            .ShouldBe(["order.validate", "inventory.reserve", "payment.capture", "order.placed"]);

        rows.Select(static row => row.CapabilityVersion)
            .ShouldBe(["1.0.0", "1.0.0", "2.1.0", "1.0.0"]);

        rows.Select(static row => row.Sequence).ShouldBeInOrder();
    }

    /// <summary>The instance row records how the flow ended.</summary>
    [Theory]
    [InlineData(null, FlowInstanceState.Completed)]
    [InlineData(0, FlowInstanceState.Failed)]
    [InlineData(2, FlowInstanceState.Failed)]
    public async Task TheInstanceRowRecordsHowTheFlowEnded(int? failAt, FlowInstanceState expected)
    {
        var journal = new WatchedJournal();
        var plan = Durable(Plans.FourStepSaga());
        var instanceId = Guid.NewGuid();
        var run = await BeginAsync(journal, plan, instanceId);

        var dispatcher = new RecordingDispatcher();

        if (failAt is { } index)
        {
            dispatcher.FailAt(index, new Error("payment.declined", "declined", ErrorCategory.Conflict));
        }

        var engine = new FlowEngine(new FakeClock(T0));

        _ = await engine.ExecuteAsync(
            plan, dispatcher, Plans.Invocation, run, TestContext.Current.CancellationToken);

        var instance = await journal.ReadInstanceAsync(instanceId, TestContext.Current.CancellationToken);

        instance.IsSuccess.ShouldBeTrue();
        instance.Value.State.ShouldBe(expected);
    }

    /// <summary>
    /// A failed attempt gets a row of its own, and the flow stops at it.
    /// </summary>
    /// <remarks>
    /// The attempt history is what makes the replay contract provable rather than asserted: a
    /// journal that recorded only successes could not tell an instance that never reached
    /// step 2 from one whose step 2 was declined, and those two want opposite things done to
    /// them.
    /// </remarks>
    [Fact]
    public async Task AFailedAttemptIsRecordedAndTheFlowStopsThere()
    {
        var journal = new WatchedJournal();
        var plan = Durable(Plans.FourStepSaga());
        var instanceId = Guid.NewGuid();
        var run = await BeginAsync(journal, plan, instanceId);

        var dispatcher = new RecordingDispatcher()
            .FailAt(2, new Error("payment.declined", "declined", ErrorCategory.Conflict));

        var engine = new FlowEngine(new FakeClock(T0));

        var result = await engine.ExecuteAsync(
            plan, dispatcher, Plans.Invocation, run, TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("payment.declined");

        var rows = await RowsAsync(journal, instanceId);

        rows.Select(static row => row.Key.StepId).ShouldBe([0, 1, 2, 1],
            "Steps 0 and 1 succeeded, step 2 was declined — and then step 1's compensation " +
            "ran and got a row of its own. Until WP-57 the fourth row was not there and the " +
            "journal said nothing about what had been undone, which is what made a crash " +
            "mid-unwind lose it.");

        rows[2].Outcome.ShouldBe(JournalOutcome.Failure);

        rows[3].Outcome.ShouldBe(JournalOutcome.Compensated);
        rows[3].CapabilityId.ShouldBe("inventory.release",
            "The row names what ran, so an undo is never mistaken for the step it reverses.");

        rows[3].Key.Attempt.ShouldBeGreaterThan(rows[1].Key.Attempt,
            "The key is (instance, scope, step, attempt) and the table is append-only, so the " +
            "compensation row has to sit past the forward row that put the step on the stack.");
    }

    /// <summary>
    /// A commit the journal refuses ends the flow, whatever the step did.
    /// </summary>
    /// <remarks>
    /// A stale fencing token means another node owns this instance now. Carrying on would be
    /// this node producing effects nobody will record and claiming an outcome it no longer
    /// controls — the split brain the token exists to prevent. Retrying is guaranteed to fail
    /// again, which is why <c>FencedOut</c> is <c>Forbidden</c> rather than retryable.
    /// </remarks>
    [Fact]
    public async Task ACommitRefusedByTheFenceEndsTheFlow()
    {
        var journal = new WatchedJournal();
        var plan = Durable(Plans.FourStepSaga());
        var instanceId = Guid.NewGuid();
        var run = await BeginAsync(journal, plan, instanceId);

        // Another node acquires the lease and raises the fence. This node still holds the
        // token it was issued, and that token is now below the fence.
        var stolen = await journal.FenceAsync(
            instanceId, new FencingToken(9), TestContext.Current.CancellationToken);

        stolen.IsSuccess.ShouldBeTrue();

        var dispatcher = new RecordingDispatcher();
        var engine = new FlowEngine(new FakeClock(T0));

        var result = await engine.ExecuteAsync(
            plan, dispatcher, Plans.Invocation, run, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(DurabilityErrors.FencedOutCode);
        result.Error.Category.ShouldBe(ErrorCategory.Forbidden);

        dispatcher.Executed.ShouldBe([0],
            "The zombie must stop at the first refused commit rather than running the rest " +
            "of the flow against a journal that will not accept a word of it.");

        (await RowsAsync(journal, instanceId)).ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------------
    // The scope key
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A <c>ForEach</c> writes one row per element per step, under the element's own scope.
    /// </summary>
    /// <remarks>
    /// The reason <c>StepScope</c> is in the key at all. The loop re-enters one range of the
    /// flat step array once per element, so <c>(instance, step)</c> is not unique — without
    /// the scope the second element is a primary-key violation on an append-only table and
    /// the flow cannot be journaled at all. <c>CompensationStack</c> met the same problem
    /// first and answered it the same way.
    /// </remarks>
    [Fact]
    public async Task AForEachKeysEveryElementUnderItsOwnScope()
    {
        var journal = new WatchedJournal();
        var plan = Durable(Plans.ForEach());
        var instanceId = Guid.NewGuid();
        var run = await BeginAsync(journal, plan, instanceId);

        var dispatcher = new RecordingDispatcher().IterateOver(1, ThreeLines);
        var engine = new FlowEngine(new FakeClock(T0));

        var result = await engine.ExecuteAsync(
            plan, dispatcher, Plans.Invocation, run, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();

        (await RowsAsync(journal, instanceId)).Select(Where).ShouldBe([
            "0[]",
            "2[0]", "3[0]",
            "2[1]", "3[1]",
            "2[2]", "3[2]",
            "4[]",
        ]);
    }

    /// <summary>Nested loops chain their scopes, which is what the rendered path is for.</summary>
    [Fact]
    public async Task ANestedForEachChainsItsScopes()
    {
        var journal = new WatchedJournal();
        var plan = Durable(Plans.NestedForEach());
        var instanceId = Guid.NewGuid();
        var run = await BeginAsync(journal, plan, instanceId);

        var dispatcher = new RecordingDispatcher()
            .IterateOver(0, TwoOrders)
            .IterateOver(1, TwoLines);

        var engine = new FlowEngine(new FakeClock(T0));

        var result = await engine.ExecuteAsync(
            plan, dispatcher, Plans.Invocation, run, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();

        (await RowsAsync(journal, instanceId)).Select(Where).ShouldBe([
            "2[0/0]", "2[0/1]", "3[0]",
            "2[1/0]", "2[1/1]", "3[1]",
            "4[]",
        ]);
    }

    /// <summary>
    /// A fork's branches commit under the enclosing scope, and every index appears once.
    /// </summary>
    /// <remarks>
    /// The reason a fork needs no scope of its own: <c>StepNode.ForParallel</c> validates that
    /// the branch spans are ascending and disjoint, so each index runs at most once and
    /// <c>(scope, step)</c> stays unique. That is also what lets the resume position be
    /// <em>derived</em> — "branch A done, branch B stopped at 12" is exactly the set of rows
    /// that exist, and no scalar cursor could ever have said it.
    /// </remarks>
    [Fact]
    public async Task AForkCommitsEveryBranchUnderTheEnclosingScope()
    {
        var journal = new WatchedJournal();
        var plan = Durable(Plans.Parallel(MergeStrategy.AllMustSucceed));
        var instanceId = Guid.NewGuid();
        var run = await BeginAsync(journal, plan, instanceId);

        var engine = new FlowEngine(new FakeClock(T0));

        var result = await engine.ExecuteAsync(
            plan, new RecordingDispatcher(), Plans.Invocation, run, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();

        var rows = await RowsAsync(journal, instanceId);

        rows.ShouldAllBe(row => row.Key.Scope == StepScope.Root);
        rows.Select(static row => row.Key.StepId).Order().ShouldBe([0, 2, 3, 4, 5]);
    }

    // ---------------------------------------------------------------------------------
    // A sub-flow is its own instance
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A composed durable child gets its own <c>flow_instance</c> row, linked to the parent's
    /// step.
    /// </summary>
    /// <remarks>
    /// ADR-0015's third schema commitment. WP-33 refused to splice a child's steps into its
    /// parent's plan at compile time — it would make the parent's manifest claim the child's
    /// capabilities and discard the child's deadline and profile — and splicing in the journal
    /// would reintroduce that untruth one layer down.
    /// </remarks>
    [Fact]
    public async Task AComposedDurableChildIsItsOwnInstanceLinkedToTheParentStep()
    {
        var journal = new WatchedJournal();
        var plan = Durable(Plans.Composing());
        var childPlan = Durable(Plans.Child());
        var instanceId = Guid.NewGuid();
        var run = await BeginAsync(journal, plan, instanceId);

        var child = new RecordingDispatcher();
        var parent = new RecordingDispatcher().ComposeAt(1, childPlan, child, "input");

        var engine = new FlowEngine(new FakeClock(T0));

        var result = await engine.ExecuteAsync(
            plan, parent, Plans.Invocation, run, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();

        var parentRows = await RowsAsync(journal, instanceId);

        // The composition is one row in the parent, carrying the child flow's id and version
        // — and none of the child's steps.
        parentRows.Select(static row => row.Key.StepId).ShouldBe([0, 1, 2, 3]);
        parentRows[1].CapabilityId.ShouldBe("order.fulfil");
        parentRows[1].CapabilityVersion.ShouldBe("1.0.0");

        var children = await journal.ChildrenOfAsync(instanceId);

        children.Count.ShouldBe(1);

        children[0].FlowId.ShouldBe("order.fulfil");
        children[0].ParentStepId.ShouldBe(1);
        children[0].ParentScope.ShouldBe(StepScope.Root);
        children[0].State.ShouldBe(FlowInstanceState.Completed);
        children[0].CorrelationId.ShouldBe(Plans.Invocation.CorrelationId);

        (await RowsAsync(journal, children[0].InstanceId))
            .Select(static row => row.Key.StepId).ShouldBe([0, 1]);
    }

    /// <summary>
    /// An ephemeral child composed by a durable parent is not journaled, and a durable child
    /// composed by a parent with no journal is refused.
    /// </summary>
    /// <remarks>
    /// ADR-0003 makes durability a per-flow declaration, so the child's own declaration
    /// governs the child. The refusal is the same one the top of the engine makes and for the
    /// same reason: a flow that asked for a journal and did not get one must be told.
    /// </remarks>
    [Fact]
    public async Task TheChildsOwnDeclarationGovernsTheChild()
    {
        var journal = new WatchedJournal();
        var engine = new FlowEngine(new FakeClock(T0));

        // An ephemeral child under a durable parent: composed, run, and not journaled.
        var durableParent = Durable(Plans.Composing());
        var instanceId = Guid.NewGuid();
        var run = await BeginAsync(journal, durableParent, instanceId);

        var ephemeralChild = new RecordingDispatcher();
        var parent = new RecordingDispatcher().ComposeAt(1, Plans.Child(), ephemeralChild, "input");

        var composed = await engine.ExecuteAsync(
            durableParent, parent, Plans.Invocation, run, TestContext.Current.CancellationToken);

        composed.IsSuccess.ShouldBeTrue();
        ephemeralChild.Executed.ShouldBe([0, 1]);
        (await journal.ChildrenOfAsync(instanceId)).ShouldBeEmpty();

        // A durable child under an ephemeral parent: refused, because there is no journal for
        // it to be an instance in.
        var refusing = new RecordingDispatcher()
            .ComposeAt(1, Durable(Plans.Child()), new RecordingDispatcher(), "input");

        var refused = await engine.ExecuteAsync(
            Plans.Composing(), refusing, Plans.Invocation, TestContext.Current.CancellationToken);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe("flow.durability_not_configured");
    }

    // ---------------------------------------------------------------------------------
    // Resumption: the same loop, from a derived cursor
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A resumed instance skips what is committed and runs what is not — through the same
    /// entry point, the same loop and the same dispatcher.
    /// </summary>
    /// <remarks>
    /// The whole of ADR-0015's second half. There is no recovery routine: the frontier is
    /// read, the loop is entered at zero as always, and every step whose row exists is stepped
    /// over. What that buys is that compensation ordering, deadline handling and
    /// <c>ForEach</c> scoping cannot drift between a first run and a resumed one, because
    /// there is only one of each.
    /// </remarks>
    [Fact]
    public async Task ResumeSkipsWhatIsCommittedAndRunsWhatIsNot()
    {
        // The node dies on the third commit: steps 0 and 1 are in the journal, step 2's
        // effect happened and its row did not.
        var journal = new WatchedJournal { DiesOnCommit = 3 };
        var plan = Durable(Plans.FourStepSaga());
        var instanceId = Guid.NewGuid();
        var first = await BeginAsync(journal, plan, instanceId);

        var engine = new FlowEngine(new FakeClock(T0));
        var crashed = new RecordingDispatcher();

        await KilledAsync(engine, plan, crashed, first);

        crashed.Executed.ShouldBe([0, 1, 2]);

        var abandoned = await journal.ReadInstanceAsync(instanceId, TestContext.Current.CancellationToken);

        abandoned.Value.State.ShouldBe(FlowInstanceState.Running,
            "A killed node seals nothing, which is what leaves an instance for the recovery " +
            "scan to find.");

        // Another node takes the instance over with a higher token.
        var resumed = await DurableExecution.ResumeAsync(
            journal, instanceId, Second, TestContext.Current.CancellationToken);

        resumed.IsSuccess.ShouldBeTrue();
        resumed.Value.IsResumed.ShouldBeTrue();
        resumed.Value.Frontier!.Committed.Count.ShouldBe(2);

        var recovered = new RecordingDispatcher();

        var finished = await engine.ExecuteAsync(
            plan, recovered, Plans.Invocation, resumed.Value, TestContext.Current.CancellationToken);

        finished.IsSuccess.ShouldBeTrue();

        recovered.Executed.ShouldBe([2, 3],
            "Steps 0 and 1 committed, so their effects have happened and re-running them " +
            "would repeat them. Step 2's row was never written, so as far as the journal is " +
            "concerned it never happened — and it runs again. That is the honest limit " +
            "ADR-0006 states and this package does not weaken: a non-idempotent effect that " +
            "landed before the commit is repeated.");

        finished.CompletedSteps.ShouldBe(4,
            "The count is the instance's work, not this node's: a resumed flow reporting two " +
            "would make a caller believe half of it never happened.");
    }

    /// <summary>
    /// A retried step appends a new attempt rather than replacing the failed one.
    /// </summary>
    /// <remarks>
    /// An append-only table cannot overwrite, and the attempt number is derived from the rows
    /// already committed rather than counted in memory — for the same reason the resume
    /// position is derived: a number this node is holding is exactly what is lost when this
    /// node is.
    /// </remarks>
    [Fact]
    public async Task ARetriedStepAppendsANewAttemptRatherThanReplacingTheFailedOne()
    {
        // A query flow, so nothing compensates and the only thing under test is the key. The
        // node records step 1's failure and then dies before it can seal the instance, which
        // is the window in which a failed attempt is still a resumable one.
        var journal = new WatchedJournal { DiesWhileSealing = true };
        var plan = Durable(Plans.TwoStepQuery());
        var instanceId = Guid.NewGuid();
        var first = await BeginAsync(journal, plan, instanceId);

        var engine = new FlowEngine(new FakeClock(T0));

        await KilledAsync(
            engine,
            plan,
            new RecordingDispatcher().FailAt(1, new Error("gateway.unavailable", "gone", ErrorCategory.Unavailable)),
            first);

        // The node that picks the instance up is a different one, and its store is fine.
        journal.DiesWhileSealing = false;

        var resumed = await DurableExecution.ResumeAsync(
            journal, instanceId, Second, TestContext.Current.CancellationToken);

        var finished = await engine.ExecuteAsync(
            plan, new RecordingDispatcher(), Plans.Invocation, resumed.Value,
            TestContext.Current.CancellationToken);

        finished.IsSuccess.ShouldBeTrue();

        (await RowsAsync(journal, instanceId))
            .Select(static row => (row.Key.StepId, row.Key.Attempt, row.Outcome))
            .ShouldBe([
                (0, 1, JournalOutcome.Success),
                (1, 1, JournalOutcome.Failure),
                (1, 2, JournalOutcome.Success),
            ]);
    }

    /// <summary>
    /// A resumed loop is rehydrated from the journaled state bag, once, before its first step.
    /// </summary>
    /// <remarks>
    /// The engine holds a <c>Dictionary&lt;Type, object&gt;</c> and knows no contract types,
    /// so turning stored JSON back into the values the remaining steps bind to is the
    /// dispatcher's job — the same division of labour that keeps the step loop
    /// reflection-free. What the engine guarantees is the <em>when</em>: once, before anything
    /// runs, and only when the instance actually committed a snapshot.
    /// </remarks>
    [Fact]
    public async Task ResumeRehydratesTheStateBagThroughTheDispatcherBeforeTheFirstStep()
    {
        var journal = new WatchedJournal { DiesOnCommit = 3 };
        var plan = Durable(Plans.FourStepSaga());
        var instanceId = Guid.NewGuid();
        var first = await BeginAsync(journal, plan, instanceId);

        var engine = new FlowEngine(new FakeClock(T0));

        var crashed = new RecordingDispatcher
        {
            Describe = static (index, _) => StepJournalEntry.Of(
                null, JournalPayload.Of(new Bag($"after step {index}"), Contracts.Default.Bag)),
        };

        await KilledAsync(engine, plan, crashed, first);

        var resumed = await DurableExecution.ResumeAsync(
            journal, instanceId, Second, TestContext.Current.CancellationToken);

        var recovered = new RecordingDispatcher
        {
            Restore = static (ctx, json) =>
                ctx.Set(JsonSerializer.Deserialize(json, Contracts.Default.Bag)!),
            Observe = static ctx => ctx.TryGet<Bag>(out var bag) ? bag.Marker : null,
        };

        var finished = await engine.ExecuteAsync(
            plan, recovered, Plans.Invocation, resumed.Value, TestContext.Current.CancellationToken);

        finished.IsSuccess.ShouldBeTrue();

        recovered.Restored.Count.ShouldBe(1, "Once, before the first step, and never again.");
        recovered.Restored[0].ShouldContain("after step 1");

        recovered.Observed[0].ShouldBe("after step 1",
            "The first resumed step must see what the last committed one left behind, or the " +
            "rest of the flow runs against values no step produced.");
    }

    /// <summary>
    /// An instance with no journaled snapshot is not asked to restore one.
    /// </summary>
    /// <remarks>
    /// The default <c>RestoreState</c> throws, deliberately — a dispatcher that wrote a bag
    /// and cannot read it back must fail loudly rather than resume against an empty one. So
    /// the engine must not ask when there is nothing to ask about, or a dispatcher that
    /// journals only boundaries could never resume.
    /// </remarks>
    [Fact]
    public async Task AnInstanceWithNoSnapshotIsNotAskedToRestoreOne()
    {
        var journal = new WatchedJournal { DiesOnCommit = 3 };
        var plan = Durable(Plans.FourStepSaga());
        var instanceId = Guid.NewGuid();
        var first = await BeginAsync(journal, plan, instanceId);

        var engine = new FlowEngine(new FakeClock(T0));

        await KilledAsync(engine, plan, new RecordingDispatcher(), first);

        var resumed = await DurableExecution.ResumeAsync(
            journal, instanceId, Second, TestContext.Current.CancellationToken);

        var recovered = new RecordingDispatcher();

        var finished = await engine.ExecuteAsync(
            plan, recovered, Plans.Invocation, resumed.Value, TestContext.Current.CancellationToken);

        finished.IsSuccess.ShouldBeTrue();
        recovered.Restored.ShouldBeEmpty();
    }

    /// <summary>
    /// A resumed loop that cannot be rehydrated is refused rather than run against an empty
    /// bag.
    /// </summary>
    /// <remarks>
    /// Worse than not resuming at all, because the effects would be real: every step after the
    /// frontier would run against values no step produced. The usual cause is a deployment
    /// whose contracts no longer match the ones the instance was pinned to.
    /// </remarks>
    [Fact]
    public async Task AResumeThatCannotRehydrateIsRefused()
    {
        var journal = new WatchedJournal { DiesOnCommit = 3 };
        var plan = Durable(Plans.FourStepSaga());
        var instanceId = Guid.NewGuid();
        var first = await BeginAsync(journal, plan, instanceId);

        var engine = new FlowEngine(new FakeClock(T0));

        var crashed = new RecordingDispatcher
        {
            Describe = static (_, _) => StepJournalEntry.Of(
                null, JournalPayload.Of(new Bag("written"), Contracts.Default.Bag)),
        };

        await KilledAsync(engine, plan, crashed, first);

        var resumed = await DurableExecution.ResumeAsync(
            journal, instanceId, Second, TestContext.Current.CancellationToken);

        var broken = new RecordingDispatcher
        {
            Restore = static (_, _) => throw new InvalidOperationException("the contract moved"),
        };

        var refused = await engine.ExecuteAsync(
            plan, broken, Plans.Invocation, resumed.Value, TestContext.Current.CancellationToken);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe("flow.state_restore_failed");
        broken.Executed.ShouldBeEmpty();
    }

    /// <summary>A resumed <c>ForEach</c> picks up at the element that did not finish.</summary>
    /// <remarks>
    /// The scope key earning its place. A loop that stopped at the third of three elements
    /// must resume at the third — not at the first, which would repeat two elements' effects,
    /// and not after the loop, which would skip one entirely.
    /// </remarks>
    [Fact]
    public async Task AResumedLoopPicksUpAtTheElementThatDidNotFinish()
    {
        // Commits go 0[], 2[0], 3[0], 2[1], 3[1], 2[2]… so the node dies at the sixth,
        // part-way through the third element.
        var journal = new WatchedJournal { DiesOnCommit = 6 };
        var plan = Durable(Plans.ForEach());
        var instanceId = Guid.NewGuid();
        var first = await BeginAsync(journal, plan, instanceId);

        var engine = new FlowEngine(new FakeClock(T0));

        await KilledAsync(engine, plan, new RecordingDispatcher().IterateOver(1, ThreeLines), first);

        var resumed = await DurableExecution.ResumeAsync(
            journal, instanceId, Second, TestContext.Current.CancellationToken);

        var recovered = new RecordingDispatcher().IterateOver(1, ThreeLines);

        var finished = await engine.ExecuteAsync(
            plan, recovered, Plans.Invocation, resumed.Value, TestContext.Current.CancellationToken);

        finished.IsSuccess.ShouldBeTrue();

        recovered.Executed.ShouldBe([2, 3, 4],
            "Elements 0 and 1 committed both of their body steps and are skipped entirely. " +
            "Element 2 committed neither, so its body runs, and the step after the loop " +
            "follows it.");

        (await RowsAsync(journal, instanceId))
            .Count(static row => row.Key.Scope == StepScope.Root.Element(2))
            .ShouldBe(2, "The third element's two body steps, committed by the second node.");
    }

    // ---------------------------------------------------------------------------------
    // [Sensitive] never reaches the journal in the clear
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A marked member is redacted before the store sees it, on every payload the seam
    /// writes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The journal is a new sink for <c>[Sensitive]</c> values and it lands three phases
    /// before <c>RedactionCannotBeBypassed</c>, so the control has to be structural rather
    /// than remembered. It is: there is no accessor for the value on
    /// <see cref="JournalPayload"/>, its only exit is <see cref="JournalPayload.ToJson"/>, and
    /// that redacts. A store is handed the payload and never a blob, so no store under
    /// deadline pressure can walk around it by serialising the graph itself.
    /// </para>
    /// <para>
    /// Read back off the row rather than asserted on the payload, because what matters is what
    /// a table retained for thirty to a hundred and eighty days actually holds.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASensitiveMemberIsRedactedBeforeItReachesTheJournal()
    {
        var journal = new WatchedJournal();
        var plan = Durable(Plans.FourStepSaga());
        var instanceId = Guid.NewGuid();

        var order = new PlaceOrder("order-77", "tok_live_4242424242424242");

        // Exactly the shape the generator emits: the value, its generated JsonTypeInfo, and
        // the flow's own SensitiveMembers array.
        var payload = JournalPayload.Of(order, Contracts.Default.PlaceOrder, PlaceOrderFlow.SensitiveMembers);

        var run = await BeginAsync(journal, plan, instanceId, payload);

        var dispatcher = new RecordingDispatcher
        {
            Describe = (_, _) => StepJournalEntry.Of(payload, payload),
        };

        var engine = new FlowEngine(new FakeClock(T0));

        var result = await engine.ExecuteAsync(
            plan, dispatcher, Plans.Invocation, run, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();

        var instance = await journal.ReadInstanceAsync(instanceId, TestContext.Current.CancellationToken);
        var rows = await RowsAsync(journal, instanceId);

        var written = new List<string?> { instance.Value.InputJson, instance.Value.StateBagJson };

        written.AddRange(rows.Select(static row => row.ResultJson));

        written.Count.ShouldBe(6, "The input, the state-bag snapshot, and one result per step.");

        foreach (var stored in written)
        {
            var text = stored.ShouldNotBeNull();

            text.ShouldNotContain("tok_live_4242424242424242", Case.Sensitive,
                "A marked member reached a table retained for months, which is strictly worse " +
                "than the log line the attribute was written for.");

            text.ShouldContain(JournalPayload.Redacted, Case.Sensitive,
                "The key must survive redacted rather than vanish: an operator reading this " +
                "row at three in the morning needs to know the field was received and " +
                "withheld, not that it never arrived.");

            text.ShouldContain("order-77", Case.Sensitive,
                "Only the marked member is withheld. Redacting the whole document would make " +
                "the journal useless as the audit source it is also meant to be.");
        }
    }

    // ---------------------------------------------------------------------------------
    // The non-determinism envelope
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A durable step records the clock it read, the ids it minted and the seed its generator
    /// was built from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ADR-0015's fourth commitment, and the place a known defect is fixed rather than
    /// documented: <c>new Random()</c> picks a seed and shows it to nobody, so the replay
    /// guarantee <c>FlowExecutionContext.Random</c>'s own remarks describe could not have
    /// held. The seed is now a journaled value.
    /// </para>
    /// <para>
    /// The reads come from <c>ContextSnapshot.Of</c>, which every recorded step already makes
    /// — one <c>ctx.NewId()</c> and one touch of <c>ctx.Random</c> — so what is asserted is
    /// the engine's capture of reads the double was making anyway rather than reads staged for
    /// the assertion.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ADurableStepRecordsWhatItReadFromOutsideItself()
    {
        var journal = new WatchedJournal();
        var plan = Durable(Plans.FourStepSaga());
        var instanceId = Guid.NewGuid();
        var run = await BeginAsync(journal, plan, instanceId);

        var dispatcher = new RecordingDispatcher();
        var engine = new FlowEngine(new FakeClock(T0));

        var result = await engine.ExecuteAsync(
            plan, dispatcher, Plans.Invocation, run, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();

        var rows = await RowsAsync(journal, instanceId);

        rows.ShouldAllBe(row => row.Nondeterminism.UtcNow == T0);

        rows.SelectMany(static row => row.Nondeterminism.NewIds)
            .ShouldBe(dispatcher.Snapshots.Select(static snapshot => snapshot.NewId));

        rows.Count(static row => row.Nondeterminism.RandomSeed is not null).ShouldBe(1,
            "The seed is drawn once per execution, so it belongs on the row for the step that " +
            "first asked for randomness and on no other. Repeating it would make a run that " +
            "drew one number indistinguishable from one that drew several.");

        rows[0].Nondeterminism.RandomSeed.ShouldNotBeNull();
    }

    // ---------------------------------------------------------------------------------
    // Doubles and contracts
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The reference journal, plus a note of every instance it was asked to open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Delegation, not reimplementation. The semantics under test are
    /// <see cref="InMemoryFlowJournal"/>'s, which passes <c>JournalConformance</c>; what this
    /// adds is the one question the contract deliberately does not answer — "which instances
    /// exist under this parent". That is a recovery scan's query, and a recovery scan is the
    /// host's, so putting it on <see cref="IFlowJournal"/> for a test's convenience would
    /// widen a contract two stores have to implement.
    /// </para>
    /// <para>
    /// A composed child's instance id is minted by the engine, which is the only thing that
    /// knows a child exists, so a test cannot know one in advance and has to be told.
    /// </para>
    /// </remarks>
    private sealed class WatchedJournal : IFlowJournal
    {
        private readonly InMemoryFlowJournal _inner = new();
        private readonly Lock _gate = new();
        private readonly List<Guid> _started = [];

        private int _commits;

        /// <summary>
        /// Which commit the node dies on, counting from one, or <c>null</c> to survive.
        /// </summary>
        /// <remarks>
        /// A store that is unreachable throws; a store that is working and saying no returns
        /// an <see cref="Error"/>. That distinction is the journal contract's, and it is what
        /// makes a crash simulable at all: the process stops mid-step with its committed
        /// prefix intact, nothing is unwound, and the instance stays <c>Running</c> for
        /// another node to pick up. Which is exactly what a SIGKILL leaves behind.
        /// </remarks>
        public int? DiesOnCommit { get; init; }

        /// <summary>Set to have the node die while sealing the instance.</summary>
        /// <remarks>
        /// Settable rather than fixed at construction, because the node that takes the
        /// instance over talks to the same store and its store is not the one that died.
        /// </remarks>
        public bool DiesWhileSealing { get; set; }

        public ValueTask<Result<FlowInstanceRecord>> StartAsync(
            FlowInstanceStart start, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(start);

            lock (_gate)
            {
                _started.Add(start.InstanceId);
            }

            return _inner.StartAsync(start, cancellationToken);
        }

        public ValueTask<Result<FencingToken>> FenceAsync(
            Guid instanceId, FencingToken token, CancellationToken cancellationToken) =>
            _inner.FenceAsync(instanceId, token, cancellationToken);

        public ValueTask<Result<JournalStep>> CommitAsync(
            StepCommit commit, CancellationToken cancellationToken)
        {
            int attempt;

            lock (_gate)
            {
                attempt = ++_commits;
            }

            return attempt == DiesOnCommit
                ? throw new NodeDiedException()
                : _inner.CommitAsync(commit, cancellationToken);
        }

        public ValueTask<Result<FlowInstanceRecord>> CompleteAsync(
            Guid instanceId,
            FencingToken token,
            FlowInstanceState state,
            JournalPayload stateBag,
            FlowWake? wake,
            CancellationToken cancellationToken) =>
            DiesWhileSealing
                ? throw new NodeDiedException()
                : _inner.CompleteAsync(instanceId, token, state, stateBag, wake, cancellationToken);

        public ValueTask<Result<FlowInstanceRecord>> ReadInstanceAsync(
            Guid instanceId, CancellationToken cancellationToken) =>
            _inner.ReadInstanceAsync(instanceId, cancellationToken);

        public ValueTask<Result<ResumeFrontier>> ReadResumeFrontierAsync(
            Guid instanceId, CancellationToken cancellationToken) =>
            _inner.ReadResumeFrontierAsync(instanceId, cancellationToken);

        public ValueTask<Result<IReadOnlyList<OutboxRecord>>> ReadOutboxAsync(
            Guid instanceId, CancellationToken cancellationToken) =>
            _inner.ReadOutboxAsync(instanceId, cancellationToken);

        /// <summary>Every instance opened under <paramref name="parentInstanceId"/>.</summary>
        public async Task<IReadOnlyList<FlowInstanceRecord>> ChildrenOfAsync(Guid parentInstanceId)
        {
            Guid[] candidates;

            lock (_gate)
            {
                candidates = [.. _started];
            }

            var children = new List<FlowInstanceRecord>();

            foreach (var candidate in candidates)
            {
                var record = await ReadInstanceAsync(candidate, TestContext.Current.CancellationToken);

                if (record.IsSuccess && record.Value.ParentInstanceId == parentInstanceId)
                {
                    children.Add(record.Value);
                }
            }

            return children;
        }
    }
}

/// <summary>A contract with a member the flow declared <c>[Sensitive]</c>.</summary>
internal sealed record PlaceOrder(string OrderId, string PaymentToken);

/// <summary>A stand-in for whatever a flow's state bag serialises to.</summary>
internal sealed record Bag(string Marker);

/// <summary>
/// The generated <c>System.Text.Json</c> context ADR-0008 chose, in miniature.
/// </summary>
/// <remarks>
/// Source-generated rather than hand-written, because that is the point:
/// <see cref="JournalPayload.Of{T}"/> takes a <c>JsonTypeInfo&lt;T&gt;</c> and has no
/// overload that reflects over a type, so a contract outside the generated context cannot
/// reach the journal at all — a compile error rather than a convention, and the reason the
/// write path stays trim- and NativeAOT-safe.
/// </remarks>
[JsonSerializable(typeof(PlaceOrder))]
[JsonSerializable(typeof(Bag))]
internal sealed partial class Contracts : JsonSerializerContext;

/// <summary>
/// Shaped exactly like the array the generator emits onto every flow's partial class.
/// </summary>
/// <remarks>
/// Reproduced rather than imported so that the runtime tests keep their one project
/// reference. What is being proved is that the seam consults <c>SensitiveMembers</c> at
/// all; that the generator emits it correctly is asserted where the generator is.
/// </remarks>
internal static class PlaceOrderFlow
{
    public static readonly string[] SensitiveMembers = ["PaymentToken"];
}

/// <summary>The store becoming unreachable, which is how a killed node is simulated here.</summary>
/// <remarks>
/// Its own type rather than a general exception so that a test asserting a crash cannot pass
/// on some unrelated failure that happened to throw.
/// </remarks>
internal sealed class NodeDiedException() : InvalidOperationException("The node holding this instance went away.");

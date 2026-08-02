using FlowX.Hosting;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// What a signal arriving <em>between two poll attempts</em> does, against a real journal.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The claim under test is about one row.</strong> A poll parks the instance holding
/// one <c>flow_instance</c> row carrying one <c>wake_at</c>/<c>wake_step_id</c>/<c>wake_scope</c>
/// triple and no lease. A delivered signal has to end <em>that</em> wait — not open a second
/// one, not wait for the schedule it is parked on, and not make one more call to the provider —
/// which is the whole of what
/// <a href="../../docs/adr/ADR-0058-a-poll-is-one-wait-not-a-race-between-two.md">ADR-0058</a>
/// refused a fork in order to keep true.
/// </para>
/// <para>
/// <strong>Against PostgreSQL rather than the reference stores, because the facts are
/// columns.</strong> Whether the wake was cleared, whether the poll node's own row exists and
/// what it is named, and how many attempt rows there are, are all questions about what a real
/// append-only table holds after a real fence and a real commit. The in-memory journal answers
/// them from a list this process built; the point of asking here is that nothing in the answer
/// came from the same object that produced it.
/// </para>
/// <para>
/// <strong>Nothing simulates time.</strong> The instance is parked on an instant minutes away
/// and the signal is delivered immediately, so a run in which the wait ended because the
/// schedule came due cannot be mistaken for one in which it ended because the delivery arrived.
/// </para>
/// <para>
/// The skip behaviour is <see cref="PostgresTestDatabase"/>'s and is inherited: no connection
/// string configured is a skip carrying a reason, a connection string with no server behind it
/// is a failure.
/// </para>
/// </remarks>
public sealed class PollSignalHostTests
{
    private const string SignalType = "ocr.completed";

    private static readonly CapabilityDescriptor Upload =
        CapabilityDescriptor.Create("ocr.upload", "1.0.0", isIdempotent: true);

    private static readonly CapabilityDescriptor Status =
        CapabilityDescriptor.Create("ocr.status", "1.0.0", isIdempotent: true);

    private static readonly CapabilityDescriptor Extract =
        CapabilityDescriptor.Create("document.extract", "1.0.0", isIdempotent: true);

    /// <summary>The flat index of the poll node, and of the attempt it re-enters.</summary>
    /// <remarks>
    /// Written out rather than searched for, because this plan is four lines above and the
    /// arithmetic is the thing being asserted: the attempt is the poll's index plus one, and
    /// the satisfied path is the index after it.
    /// </remarks>
    private const int PollNode = 1;

    private const int PollAttempt = 2;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// A signal delivered while the instance is parked between attempts ends that wait, on that
    /// row, without another attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Five facts, and each of them is one of the constraints the construct is built
    /// under.</strong> The instance parks with a wake in the future, so nothing else could have
    /// continued it. Exactly one attempt row exists afterwards — the one the first invocation
    /// made — so the delivery ended the wait rather than bringing an attempt forward. The poll
    /// node has committed a row of its own, named after the signal, which is the only row a poll
    /// node ever writes and the only thing a later resume can read to learn that the polling
    /// stopped. The step after the poll ran, so control took the satisfied path rather than the
    /// escalation. And the wake is gone with the state, because one wait ended once.
    /// </para>
    /// <para>
    /// <strong>The predicate never holds.</strong> The stand-in answers <c>false</c> for the
    /// whole test, so there is exactly one thing that can have ended this wait and the
    /// assertions cannot pass for the other reason.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASignalBetweenTwoAttemptsEndsTheSameWaitOnTheSameRow()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var durability = new FlowDurability(schema.Journal, schema.Leases, schema.RecoveryIndex);
        var host = new FlowHost(new FlowEngine(SystemClock.Instance), Options(), durability);
        var dispatcher = new PollingDispatcher();

        var started = await host.RunAsync(Plan(), dispatcher, Invocation(), Cancellation);

        started.IsSuspended.ShouldBeTrue("the poll parks between attempts: " + started.Error);

        var instance = started.InstanceId!.Value;
        var parked = await schema.Journal.ReadInstanceAsync(instance, Cancellation);

        parked.Value.State.ShouldBe(FlowInstanceState.Suspended);
        parked.Value.Wake!.Value.StepId.ShouldBe(PollNode, "the wake names the poll it is parked at");
        parked.Value.Wake!.Value.Scope.Text.ShouldBe("0", "and the attempt the gap follows");

        parked.Value.Wake!.Value.At.ShouldBeGreaterThan(
            SystemClock.Instance.UtcNow,
            "the next attempt is minutes away, so nothing but the delivery can end this wait");

        dispatcher.Executed.ShouldBe([0, PollAttempt], "one upload and one attempt");

        var signalled = await host.SignalAsync(
            instance,
            new FlowRegistration(Plan(), dispatcher),
            FlowSignal.Of(SignalType, new Completion("job-1")),
            principal: null,
            Cancellation);

        signalled.IsSuccess.ShouldBeTrue("the delivery ran the flow to the end: " + signalled.Error);

        dispatcher.Executed.ShouldBe(
            [0, PollAttempt, 3],
            "the wait ended on the delivery, so the poll made no second attempt and control " +
            "continued on the satisfied path");

        var rows = await Rows(schema, instance);

        rows.Count(row => row.Key.StepId == PollAttempt).ShouldBe(
            1, "one attempt, made by the invocation that started the flow");

        var ending = rows.Single(row => row.Key.StepId == PollNode);

        ending.CapabilityId.ShouldBe(
            SignalType,
            "the one row a poll node ever commits says which of its two endings happened");

        ending.Key.Scope.ShouldBe(
            StepScope.Root,
            "the poll's own scope, not an attempt's — the attempts are the loop and this is " +
            "the wait ending");

        var finished = await schema.Journal.ReadInstanceAsync(instance, Cancellation);

        finished.Value.State.ShouldBe(FlowInstanceState.Completed);
        finished.Value.Wake.ShouldBeNull("one wait, ended once — the wake went with the state");

        (await schema.Leases.ReadAsync(instance, Cancellation)).Error.Code.ShouldBe(
            "lease.not_held", "and nothing is holding the instance afterwards");
    }

    /// <summary>
    /// A resume arriving after a signal ended the poll does not go back to polling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is why the ending commits a row.</strong> A poll that ends on its own
    /// predicate needs none: the predicate is still true on the next arrival, which is
    /// ADR-0058's decision 5. A poll that ends on a delivery has no such witness — the predicate
    /// says "not yet" and the signal has been consumed by the invocation that took it — so
    /// without the row, a recovery scan, a redelivered trigger or an operator touching the
    /// instance would find the poll unfinished and call the provider again, for ever.
    /// </para>
    /// <para>
    /// The second delivery is what makes the test sharp rather than incidental: it arrives at an
    /// instance that has already finished, carrying the very identity the poll ends on, and the
    /// answer has to be that nothing runs.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AResumePastASignalEndedPollDoesNotPollAgain()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var durability = new FlowDurability(schema.Journal, schema.Leases, schema.RecoveryIndex);
        var host = new FlowHost(new FlowEngine(SystemClock.Instance), Options(), durability);
        var dispatcher = new PollingDispatcher();
        var registration = new FlowRegistration(Plan(), dispatcher);

        var started = await host.RunAsync(Plan(), dispatcher, Invocation(), Cancellation);
        var instance = started.InstanceId!.Value;

        await host.SignalAsync(
            instance,
            registration,
            FlowSignal.Of(SignalType, new Completion("job-1")),
            principal: null,
            Cancellation);

        var after = dispatcher.Executed.Count;

        await host.SignalAsync(
            instance,
            registration,
            FlowSignal.Of(SignalType, new Completion("job-1")),
            principal: null,
            Cancellation);

        dispatcher.Executed.Count.ShouldBe(
            after,
            "the poll's own row says the wait is over, so a second pass over the plan skips it " +
            "rather than asking the provider again");

        (await Rows(schema, instance))
            .Count(row => row.Key.StepId == PollAttempt)
            .ShouldBe(1, "and no attempt row was added by the resume");
    }

    // -----------------------------------------------------------------------------------
    // Fixture
    // -----------------------------------------------------------------------------------

    /// <summary>What the webhook delivers. Never read; the engine seeds it by contract.</summary>
    private sealed record Completion(string JobId);

    /// <summary>
    /// Upload, then poll a status capability that never satisfies its predicate, then extract.
    /// </summary>
    /// <remarks>
    /// No <c>.OnTimeout</c> block, so the satisfied path is the index after the one-step attempt
    /// and the poll carries no target — which is the layout that makes "past the escalation"
    /// and "past the attempt" the same index and therefore worth asserting on.
    /// </remarks>
    private static ExecutionPlan Plan() => ExecutionPlan.Create(
        FlowDescriptor.Create("document.process", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromHours(6)),
        StepGraph.Create([
            StepNode.ForCapability(0, Upload),
            StepNode.ForPoll(
                PollNode,
                Backoff.Exponential(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)),
                TimeSpan.FromHours(4),
                signalType: SignalType),
            StepNode.ForCapability(PollAttempt, Status),
            StepNode.ForCapability(3, Extract),
        ]));

    private static FlowXOptions Options() => new()
    {
        ApplicationName = "Sample.App",
        NodeName = "node-1",
        ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
    };

    private static FlowInvocation Invocation() => new("corr-doc", "doc-1", "acme");

    private static async Task<IReadOnlyList<JournalStep>> Rows(PostgresTestSchema schema, Guid instance) =>
        (await schema.Journal.ReadResumeFrontierAsync(instance, Cancellation)).Value.Committed;

    /// <summary>
    /// A dispatcher whose poll predicate never holds, so only a delivery can end the wait.
    /// </summary>
    private sealed class PollingDispatcher : IStepDispatcher
    {
        private readonly Lock _gate = new();
        private readonly List<int> _executed = [];

        public IReadOnlyList<int> Executed
        {
            get
            {
                lock (_gate)
                {
                    return [.. _executed];
                }
            }
        }

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            lock (_gate)
            {
                _executed.Add(stepIndex);
            }

            return ValueTask.FromResult(StepOutcome.Success);
        }

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        /// <summary>The provider is never finished, for the whole of every test in this file.</summary>
        public bool Evaluate(int stepIndex, FlowContext ctx) => false;

        public int Select(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no switch step.");

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to begin.");

        public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to enter.");
    }
}

using FlowX;
using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;
using FlowX.Testing;
using Shouldly;
using Xunit;

namespace Workflow.Tests;

/// <summary>
/// <c>offer.accept</c> waits for a person, and this is what that costs and buys.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the class that replaces
/// <c>TheAbsentHalfTests.AnAwaitSignalStepDoesNotWaitForAnything</c>.</strong> That test was
/// written to go red on the day WP-63 landed, and it did: an <c>AwaitSignal</c> step used to
/// be dispatched like any other, the step after it ran in the same millisecond, and the
/// instance finished <c>Completed</c> with three committed rows. Everything below is the
/// inverse of that run.
/// </para>
/// <para>
/// <strong>The flow is this sample's own and the plan is the compiled one.</strong> Nothing
/// here hand-builds a <c>StepGraph</c> — which the old test had to, because no flow in the
/// repository was allowed to declare a suspension point. The host, the engine, the lease, the
/// fencing token and the journal are the production ones, with the conformance suite's
/// reference stores behind them; what this project adds is a recording dispatcher in front of
/// the generated one.
/// </para>
/// </remarks>
public sealed class SuspensionTests
{
    /// <summary>
    /// The identity this application delivers is the identity the plan waits for.
    /// </summary>
    /// <remarks>
    /// <c>Signals.OfferCountersigned</c> is a copy of a decision <c>FlowAnalyzer</c> makes when
    /// it derives a signal's identity from its contract's name, and a copy of a decision is a
    /// chance to disagree with it. This reads the identity off the compiled plan, so a change
    /// to that convention fails here rather than leaving this application delivering to a name
    /// nothing waits for.
    /// </remarks>
    [Fact]
    public void TheSignalIdentityThisApplicationSendsIsTheOneThePlanWaitsFor() =>
        AcceptOfferFlow.Plan.Graph.Steps
            .Single(step => step.Kind == StepKind.AwaitSignal)
            .SignalType
            .ShouldBe(Signals.OfferCountersigned);

    /// <summary>And the durations the plan carries are the ones the flow declared.</summary>
    /// <remarks>
    /// The defect a deleted diagnostic was raised over: <c>FlowEmitter</c> wrote
    /// <c>TimeSpan.FromHours(1)</c> for every suspension point whatever the author declared, so
    /// a flow written to wait seven days produced a plan that said one hour. Both durations are
    /// the author's now, and both are armed — the instance records which wait it is parked at
    /// and when it is due, and a sweep is what comes back for it.
    /// </remarks>
    [Fact]
    public void ThePlanCarriesTheDurationsTheFlowDeclared()
    {
        AcceptOfferFlow.Plan.Graph.Steps
            .Single(step => step.Kind == StepKind.AwaitSignal)
            .SignalTimeout
            .ShouldBe(Waits.Countersignature);

        AcceptOfferFlow.Plan.Graph.Steps
            .Single(step => step.Kind == StepKind.Delay)
            .Delay
            .ShouldBe(Waits.Settling);
    }

    /// <summary>
    /// The escalation is laid out after the wait, and the signal path skips it.
    /// </summary>
    /// <remarks>
    /// The layout is what makes <c>.OnTimeout</c> mean what it says. If the wait fell through
    /// to the block on the signal path, a countersigned offer would be withdrawn; if it
    /// carried no target, a timeout would run the steps after the wait against a payload
    /// nothing delivered. Asserted off the compiled plan of this sample's own flow, so it is
    /// the layout the engine walks rather than one a fixture arranged.
    /// </remarks>
    [Fact]
    public void TheEscalationSitsBetweenTheWaitAndTheStepsAfterIt()
    {
        var wait = AcceptOfferFlow.Plan.Graph.Steps.Single(step => step.Kind == StepKind.AwaitSignal);

        wait.Target.ShouldBe(
            wait.Index + 2,
            "the block is one step — a Fail — so a delivered signal carries on two past the " +
            "wait, and the block falls through to the same index.");

        AcceptOfferFlow.Plan.Graph.Steps[wait.Index + 1].Kind.ShouldBe(
            StepKind.Fail,
            "the escalation is contiguous with the wait, because the other path out of a " +
            "suspension point is the rest of the flow and has no end to jump over.");
    }

    /// <summary>
    /// The request that starts the flow returns at the wait, holding nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The offer has gone out — that effect is real and its row is committed — and the step
    /// after the wait has not been dispatched. The lease is released rather than renewed for
    /// the duration of the wait, which is what makes a million waiting offers a million rows
    /// instead of a million renewal timers.
    /// </para>
    /// <para>
    /// The result is neither a success nor a failure. A caller that treated it as a success
    /// would project <c>.Return(ctx =&gt; new AcceptedOffer(..., ctx.Get&lt;OnboardingStarted&gt;()...))</c>
    /// over a step that has not run, which is why <c>Value</c> throws and says so.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheRequestThatStartsTheOfferReturnsAtTheWait()
    {
        var harness = OfferHarness.Create();
        var ct = TestContext.Current.CancellationToken;

        var started = await harness.StartAsync(AnOffer, ct);

        started.Result.IsSuspended.ShouldBeTrue("the offer is out and the flow is waiting");
        started.Result.IsSuccess.ShouldBeFalse("waiting is not finishing");
        started.Result.Error.ShouldBeNull("and it is not failing either");

        started.Trace.Executed.ShouldBe(
            ["offer.send"], "onboarding.start binds the signal, so it cannot have run yet");

        harness.Desk.OpenEnvelopes.ShouldBe(1, "the offer really went out");
        harness.Desk.StartedOnboardings.ShouldBe(0);

        harness.Journal.Instances[0].State.ShouldBe(FlowInstanceState.Suspended);

        started.Projected.ShouldBeNull(
            "projecting a flow that has not finished would build an answer out of work that " +
            "has not happened, so reading Value on it throws and says so.");
    }

    /// <summary>
    /// The countersignature resumes it, and the step after the wait binds what it carried.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole feature in one assertion. <c>offer.send</c> is stepped over — its row
    /// is committed — the suspension point is satisfied by the delivery, and
    /// <c>onboarding.start</c> runs binding an <c>OfferCountersigned</c> that no step produced.
    /// The engine seeded it into the state bag before dispatching the wait, and the commit that
    /// recorded the wait journaled it with the rest of the bag.
    /// </para>
    /// <para>
    /// The dispatcher is a fresh one, so nothing carried over in memory from the invocation
    /// that suspended. Everything the resumed flow knows, it read out of the journal.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheCountersignatureResumesItAndTheNextStepBindsWhatItCarried()
    {
        var harness = OfferHarness.Create();
        var ct = TestContext.Current.CancellationToken;

        var started = await harness.StartAsync(AnOffer, ct);
        var resumed = await harness.SignalAsync(started.Result.InstanceId!.Value, Countersigned, ct);

        resumed.Result.IsFailure.ShouldBeFalse(resumed.Result.Error?.ToString() ?? resumed.Trace.ToString());

        resumed.Result.IsSuspended.ShouldBeTrue(
            "the signal satisfied the wait and the flow ran on into the settling period, " +
            "which is a second wait — this time on a clock, with nothing to deliver.");

        resumed.Trace.Executed.ShouldBe(
            ["await:offer.countersigned"],
            "offer.send is stepped over because its row is committed; the suspension point " +
            "is dispatched — which only happens when the invocation carries the signal it " +
            "waits for — and the flow stops at the delay before onboarding.start.");

        // The signal it carried is in the journaled state bag, waiting for the step that
        // binds it. It has to be: the node that took the delivery will not be the one that
        // starts onboarding a day later.
        harness.Clock.Advance(Waits.Settling);

        var woken = await harness.WakeAsync(ct);

        woken.Report.Woken.ShouldBe(1, woken.ToString());

        woken.Trace.Executed.ShouldBe(
            ["delay:flow.delay", "onboarding.start"],
            "the sweep resumed the instance through the same ResumeAsync a recovery scan " +
            "uses, the delay was over, and the step after it ran.");

        harness.Desk.OnboardingsBySignatory.ShouldBe(
            ["ada"],
            "onboarding.start read the name off the OfferCountersigned it bound — a contract " +
            "no step produced, which the engine seeded from the delivered signal and the " +
            "commit that recorded the wait journaled with the rest of the bag.");

        harness.Journal.Instances[0].State.ShouldBe(FlowInstanceState.Completed);
    }

    /// <summary>
    /// An offer nobody signs is withdrawn by the clock, and the block is what withdraws it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The half that used to compile to nothing.</strong> The wait carried the
    /// author's seven days into the plan and nothing armed it, so an unsigned offer sat
    /// <c>Suspended</c> until <c>[FlowDeadline("P30D")]</c> — three weeks past the window the
    /// business declared, with the envelope open the whole time.
    /// </para>
    /// <para>
    /// The unwind is the point of ending the block with a <c>.Fail</c> rather than letting it
    /// fall through: <c>offer.withdraw</c> was put on the compensation stack before the flow
    /// suspended, on a node that is gone, and it is rebuilt from the journal's committed rows
    /// by the same frontier scan that decides which steps to skip.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnOfferNobodySignsIsWithdrawnWhenTheWindowCloses()
    {
        var harness = OfferHarness.Create();
        var ct = TestContext.Current.CancellationToken;

        await harness.StartAsync(AnOffer, ct);

        (await harness.WakeAsync(ct)).Report.Examined.ShouldBe(
            0, "the window is still open, so nothing is due");

        harness.Clock.Advance(Waits.Countersignature);

        var woken = await harness.WakeAsync(ct);

        woken.Report.Woken.ShouldBe(1, woken.ToString());

        woken.Trace.Compensated.ShouldBe(
            ["offer.withdraw"],
            "the escalation failed the flow, and the entry it unwound was rebuilt from the " +
            "journal rather than held in this node's memory.");

        harness.Desk.OpenEnvelopes.ShouldBe(0, "the envelope that went out is closed");
        harness.Desk.StartedOnboardings.ShouldBe(0, "and nobody was onboarded");

        harness.Journal.Instances[0].State.ShouldBe(FlowInstanceState.Failed);
    }

    /// <summary>
    /// A sweep that runs before the wait is due leaves the instance exactly where it was.
    /// </summary>
    /// <remarks>
    /// The property that makes a ten-second sweep interval affordable against a table of
    /// week-long waits: the candidate set is filtered at the store on an instant the instance
    /// chose, so a healthy parked instance is never fetched, never leased and never resumed.
    /// </remarks>
    [Fact]
    public async Task ASweepBeforeTheWaitIsDueTouchesNothing()
    {
        var harness = OfferHarness.Create();
        var ct = TestContext.Current.CancellationToken;

        var started = await harness.StartAsync(AnOffer, ct);

        harness.Clock.Advance(Waits.Countersignature - TimeSpan.FromMinutes(1));

        var woken = await harness.WakeAsync(ct);

        woken.Report.Examined.ShouldBe(0, "a minute short of due is not due");
        woken.Trace.Executed.ShouldBeEmpty();

        harness.Journal.Instances[0].State.ShouldBe(FlowInstanceState.Suspended);

        harness.Journal.Instances[0].Wake!.Value.StepId.ShouldBe(
            started.Result.Wake!.Value.StepId,
            "and the instant on the row is the one the invocation that parked it wrote — a " +
            "wait that were re-derived on every look could be made to last for ever by " +
            "looking at it.");
    }

    /// <summary>
    /// A failure after the wait unwinds the step that ran before it, days earlier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the property a saga exists for, and the wait is what makes it hard.</strong>
    /// <c>offer.send</c>'s compensation was registered on a node that has since gone; the
    /// compensation stack it was registered on died with it. The resumed flow rebuilds the
    /// stack from the journal's committed rows — the same frontier scan that decides which
    /// steps to skip also decides which to put back on the unwind — so a failure three days
    /// later still withdraws the offer.
    /// </para>
    /// <para>
    /// And the undo binds <c>OfferToAccept</c>, which came back out of the journaled state bag.
    /// Without that, an unwind across a suspension point would run with an empty context.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFailureAfterTheWaitWithdrawsTheOfferThatWentOutBeforeIt()
    {
        var harness = OfferHarness.Create();
        var ct = TestContext.Current.CancellationToken;

        var started = await harness.StartAsync(AnOffer, ct);

        harness.Substitute("onboarding.start", OfferErrors.OnboardingRefused("headcount frozen"));

        await harness.SignalAsync(started.Result.InstanceId!.Value, Countersigned, ct);

        // Two waits and two nodes, which is what makes this the hard case. The signature was
        // taken by one invocation and the failure happens in another, a settling period later,
        // and the entry that has to be unwound was registered by a third before either.
        harness.Clock.Advance(Waits.Settling);

        var woken = await harness.WakeAsync(ct);

        woken.Report.Woken.ShouldBe(1, woken.ToString());

        woken.Trace.Compensated.ShouldBe(
            ["offer.withdraw"],
            "the entry was put back on the stack from the journal, not from this node's memory");

        harness.Desk.OpenEnvelopes.ShouldBe(0, "the offer that went out before the wait is withdrawn");
        harness.Journal.Instances[0].State.ShouldBe(FlowInstanceState.Failed);
    }

    /// <summary>
    /// A countersignature delivered twice starts one onboarding, not two.
    /// </summary>
    /// <remarks>
    /// At-least-once delivery is the ordinary case for a transport, and nothing in this flow is
    /// written to defend against it. What defends against it is the frontier: the second
    /// delivery re-enters an instance whose wait now has a committed row and which is already
    /// <c>Completed</c>, so the journal refuses the write outright. The effect is idempotence
    /// nobody had to code.
    /// </remarks>
    [Fact]
    public async Task ACountersignatureDeliveredTwiceStartsOneOnboarding()
    {
        var harness = OfferHarness.Create();
        var ct = TestContext.Current.CancellationToken;

        var started = await harness.StartAsync(AnOffer, ct);
        var instanceId = started.Result.InstanceId!.Value;

        await harness.SignalAsync(instanceId, Countersigned, ct);

        harness.Clock.Advance(Waits.Settling);

        await harness.WakeAsync(ct);

        var again = await harness.SignalAsync(instanceId, Countersigned, ct);

        again.Trace.Executed.ShouldBeEmpty("nothing ran a second time");

        harness.Desk.StartedOnboardings.ShouldBe(1, "and one onboarding was started, not two");
    }

    /// <summary>
    /// A waiting offer is not something the recovery scan picks up.
    /// </summary>
    /// <remarks>
    /// The failure this guards against is a sweep with its own name on it: a scan that treated
    /// <c>Suspended</c> as abandoned would take a lease on every waiting offer every TTL,
    /// resume it, find the same wait still open, and put it back — for seven days.
    /// </remarks>
    [Fact]
    public async Task AWaitingOfferIsNotSweptUpAsAbandonedWork()
    {
        var harness = OfferHarness.Create();
        var ct = TestContext.Current.CancellationToken;

        await harness.StartAsync(AnOffer, ct);

        var report = await harness.SweepAsync(ct);

        report.Examined.ShouldBe(0, "a flow that is waiting is not a flow a node abandoned");
        harness.Journal.Instances[0].State.ShouldBe(FlowInstanceState.Suspended);
        harness.Desk.StartedOnboardings.ShouldBe(0);
    }

    private static OfferToAccept AnOffer => new("c-1", "staff-engineer", "london");

    private static OfferCountersigned Countersigned =>
        new("env-c-1", "ada", DateTimeOffset.UnixEpoch);
}

/// <summary>
/// A host for <c>offer.accept</c>: the real one, with the conformance suite's reference stores.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="OnboardingHarness"/> rather than folded into it, because the two
/// answer different questions and share no capability: that one is about a saga's shape, this
/// one is about a flow that stops. What they do share is the reason they exist at all —
/// <c>FlowTestHost</c> cannot run a <c>Durable</c> flow, which
/// <see cref="WhyTheseTestsDoNotUseFlowTestHostTests"/> asserts — and both should be deleted
/// the day it grows a durable seam.
/// </para>
/// <para>
/// <strong>The host is held rather than rebuilt per call</strong>, unlike
/// <see cref="OnboardingHarness"/>'s. A signal is delivered to an instance that a previous
/// call left waiting, so the two calls have to be the same node's — which is also the arrangement
/// a deployment has, where the request that starts a flow and the request that signals it reach
/// whichever replica the load balancer picked.
/// </para>
/// </remarks>
internal sealed class OfferHarness
{
    private readonly Dictionary<string, Error> _failures = new(StringComparer.Ordinal);

    private OfferHarness(FlowXOptions options)
    {
        Options = options;
        Index = new InMemoryRecoveryIndex(Journal);
        Timers = new InMemoryTimerIndex(Journal);
        Durability = new FlowDurability(Journal, Leases, Index, Timers);
        Host = new FlowHost(new FlowEngine(Clock), options, Durability);
    }

    /// <summary>Where offers are sent, withdrawn and turned into onboardings.</summary>
    public InMemoryOfferDesk Desk { get; } = new();

    /// <summary>The clock the engine reads.</summary>
    public FlowTestClock Clock { get; } = new();

    /// <summary>The journal every step boundary commits to.</summary>
    public InMemoryFlowJournal Journal { get; } = new();

    /// <summary>Where exclusive ownership and fencing tokens come from.</summary>
    public InMemoryLeaseStore Leases { get; } = new();

    /// <summary>The one query a recovery scan needs, over the same journal.</summary>
    public InMemoryRecoveryIndex Index { get; }

    /// <summary>The one query a timer sweep needs, over the same journal.</summary>
    /// <remarks>
    /// A second index over the same instances rather than a second store, because a durable
    /// timer is a value on the instance row. The two sweeps partition the unfinished rows
    /// between them: this one reads <c>Suspended</c>, which <see cref="Index"/> excludes.
    /// </remarks>
    public InMemoryTimerIndex Timers { get; }

    /// <summary>The host both calls go through — the same node, deliberately.</summary>
    public FlowHost Host { get; }

    /// <summary>The stores, as the host and the scan both see them.</summary>
    public FlowDurability Durability { get; }

    /// <summary>The validated host options.</summary>
    public FlowXOptions Options { get; }

    /// <summary>Starts a harness over a fresh offer desk and a fresh pair of stores.</summary>
    public static OfferHarness Create() => new(new FlowXOptions
    {
        ApplicationName = "Workflow.Tests",
        NodeName = "test-node",
        ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
    });

    /// <summary>Makes a capability fail, so a test can drive the unwind.</summary>
    public OfferHarness Substitute(string capabilityId, Error error)
    {
        _failures[capabilityId] = error;

        return this;
    }

    /// <summary>Runs <c>offer.accept</c> up to wherever it gets.</summary>
    public async ValueTask<OfferRun> StartAsync(OfferToAccept offer, CancellationToken ct)
    {
        var trace = new DurableTrace();

        var result = await Host
            .RunAsync(
                AcceptOfferFlow.Plan,
                Wrap(trace),
                new FlowInvocation("corr-offer", offer.CandidateId),
                offer,
                AcceptOfferFlow.Projection,
                ct)
            .ConfigureAwait(false);

        // TryGetValue rather than Value: a suspended flow's projection has not run, and
        // reading it throws — which is the behaviour, not something to work around.
        result.TryGetValue(out var projected);

        return new OfferRun(result.Outcome, result.IsSuccess ? projected : null, trace);
    }

    /// <summary>Delivers the countersignature to a waiting instance.</summary>
    /// <remarks>
    /// A fresh dispatcher and a fresh trace, so nothing this invocation knows came from the one
    /// that suspended. Everything the resumed flow has, it read out of the journal.
    /// </remarks>
    public async ValueTask<OfferRun> SignalAsync(
        Guid instanceId, OfferCountersigned signed, CancellationToken ct)
    {
        var trace = new DurableTrace();

        var result = await Host
            .SignalAsync(
                instanceId,
                new FlowRegistration(AcceptOfferFlow.Plan, Wrap(trace)),
                FlowSignal.Of(Signals.OfferCountersigned, signed),
                ct)
            .ConfigureAwait(false);

        // No projected output, and not because this harness declines to ask for one:
        // `SignalAsync` has no projecting overload, for the same reason `ResumeAsync` has
        // none. A recovery scan has no caller to hand an output to, and a signal endpoint
        // that wants one reads the instance back. That is a real edge worth stating in a
        // sample rather than smoothing over.
        return new OfferRun(result, projected: null, trace);
    }

    /// <summary>Runs one recovery sweep over the same stores.</summary>
    public ValueTask<RecoveryScanReport> SweepAsync(CancellationToken ct) =>
        new FlowRecoveryScan(Host, Catalogue(new DurableTrace()), Durability, Options, Clock)
            .RunOnceAsync(ct);

    /// <summary>Runs one timer sweep over the same stores, and reports what it ran.</summary>
    /// <remarks>
    /// The trace comes back with the report because that is the only place a woken instance's
    /// steps are observable: the sweep resumes through <c>FlowHost.ResumeAsync</c> and hands
    /// its caller counts, exactly as it does in a deployment, so the flow's own behaviour has
    /// to be read off the dispatcher the catalogue handed it.
    /// </remarks>
    public async ValueTask<TimerRun> WakeAsync(CancellationToken ct)
    {
        var trace = new DurableTrace();

        var report = await new FlowTimerScan(Host, Catalogue(trace), Durability, Options, Clock)
            .RunOnceAsync(ct)
            .ConfigureAwait(false);

        return new TimerRun(report, trace);
    }

    /// <summary>What a sweep resolves a journal row's flow id and version back into.</summary>
    private FlowCatalog Catalogue(DurableTrace trace) =>
        new FlowCatalog().Add(AcceptOfferFlow.Plan, Wrap(trace));

    private RecordingDispatcher Wrap(DurableTrace trace) => new(
        AcceptOfferFlow.Plan,
        new AcceptOfferFlow.Dispatcher(
            new SendOfferForSignature(Desk),
            new StartOnboarding(Desk),
            new WithdrawOffer(Desk)),
        trace,
        _failures.ToDictionary(
            pair => pair.Key,
            pair => (CapabilityStandIn)((_, _) => ValueTask.FromResult(StepOutcome.Failed(pair.Value))),
            StringComparer.Ordinal));
}

/// <summary>What one timer sweep found, and what the instance it woke then did.</summary>
internal sealed class TimerRun
{
    internal TimerRun(TimerScanReport report, DurableTrace trace)
    {
        Report = report;
        Trace = trace;
    }

    /// <summary>The sweep's own counts.</summary>
    public TimerScanReport Report { get; }

    /// <summary>What the woken instance ran.</summary>
    public DurableTrace Trace { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"examined {Report.Examined}, woke {Report.Woken}\n{Trace}";
}

/// <summary>What one invocation of <c>offer.accept</c> did.</summary>
internal sealed class OfferRun
{
    internal OfferRun(FlowExecutionResult result, AcceptedOffer? projected, DurableTrace trace)
    {
        Result = result;
        Projected = projected;
        Trace = trace;
    }

    /// <summary>The engine's own result.</summary>
    public FlowExecutionResult Result { get; }

    /// <summary>
    /// What the flow's <c>.Return(...)</c> produced, or <c>null</c> when it did not run.
    /// </summary>
    /// <remarks>
    /// Null for a suspended flow, and that is the point rather than a convenience: the
    /// projection reads values the steps after the wait were going to produce, so there is
    /// nothing to project until they have.
    /// </remarks>
    public AcceptedOffer? Projected { get; }

    /// <summary>What the flow did.</summary>
    public DurableTrace Trace { get; }

    /// <inheritdoc />
    public override string ToString() =>
        (Result.IsSuspended ? "suspended" : Result.IsSuccess ? "completed" : "failed: " + Result.Error!.Code)
        + "\n" + Trace;
}

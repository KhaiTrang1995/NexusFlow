using FlowX.Conformance.InMemory;
using FlowX.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace FlowX.Hosting.Tests;

/// <summary>
/// A durable flow, run end to end by a host: the lease is taken before the instance is
/// opened, every commit carries its token, the lease is given back, and an instance a dead
/// node left behind is found and finished by another node.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is what retires <c>flow.durability_not_configured</c> as the normal
/// path.</strong> Since WP-52 a <c>Durable</c> flow has been refused at its first invocation
/// because nothing acquired a lease for it; the refusal was the right call over running it
/// ephemerally, and it left durable flows unable to run at all. A host with a journal and a
/// lease store now runs them. A host with neither still refuses them, which is a different
/// thing and stays true.
/// </para>
/// <para>
/// The stores are the conformance suite's reference implementations, so nothing asserted
/// here rests on a double that was written to agree with the code under test.
/// </para>
/// </remarks>
public sealed class DurableHostTests
{
    private const string NodeOne = "node-1";
    private const string NodeTwo = "node-2";

    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static readonly CapabilityDescriptor Validate =
        CapabilityDescriptor.Create("order.validate", "1.0.0", isIdempotent: true);

    /// <summary>A two-step flow that declares <c>Durable</c>.</summary>
    private static ExecutionPlan DurablePlan(string version = "1.0.0") => ExecutionPlan.Create(
        FlowDescriptor.Create("order.place", version, ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForCapability(1, Validate),
        ]));

    /// <summary>The same shape with a suspension point in the middle of it.</summary>
    /// <remarks>
    /// <c>Durable</c>, because <c>ExecutionPlan</c> refuses a suspension point under any other
    /// profile — an in-memory wait does not survive a deployment, which is what
    /// <c>FLOWX1017</c> says at build time and this says at plan construction.
    /// </remarks>
    private static ExecutionPlan WaitingPlan() => ExecutionPlan.Create(
        FlowDescriptor.Create("offer.accept", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromDays(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForAwaitSignal(1, "contract.countersigned", TimeSpan.FromDays(7)),
            StepNode.ForCapability(2, Validate),
        ]));

    private static FlowXOptions Options(Action<FlowXOptions>? configure = null)
    {
        var options = new FlowXOptions
        {
            ApplicationName = "Sample.App",
            NodeName = NodeOne,
            ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
        };

        configure?.Invoke(options);

        return options;
    }

    private static FlowHost NewHost(FlowDurability? durability, FlowXOptions? options = null) =>
        new(new FlowEngine(new FixedClock()), options ?? Options(), durability);

    // ---------------------------------------------------------------------------------
    // A durable flow runs
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A host with a journal and a lease store runs a durable flow, and gives the lease back.
    /// </summary>
    [Fact]
    public async Task ADurableFlowRunsEndToEndAndTheLeaseIsGivenBack()
    {
        var journal = new ScannableJournal();
        var leases = new InMemoryLeaseStore();
        var host = NewHost(new FlowDurability(journal, leases, journal));
        var dispatcher = new CountingDispatcher();
        var ct = TestContext.Current.CancellationToken;

        var result = await host.RunAsync(DurablePlan(), dispatcher, Plans.Invocation, ct);

        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());
        result.CompletedSteps.ShouldBe(2);
        dispatcher.Executed.ShouldBe([0, 1]);

        var instances = journal.Instances;

        instances.Count.ShouldBe(1, "One flow, one instance row.");
        instances[0].State.ShouldBe(FlowInstanceState.Completed);
        instances[0].Fence.Value.ShouldBe(1, "The first lease issued for this instance.");
        instances[0].CorrelationId.ShouldBe(Plans.Invocation.CorrelationId);

        var rows = await journal.ReadResumeFrontierAsync(instances[0].InstanceId, ct);

        rows.Value.Committed.Count.ShouldBe(2, "One row per step boundary.");

        host.HeldLeases.ShouldBe(0);

        (await leases.ReadAsync(instances[0].InstanceId, ct)).Error.Code.ShouldBe("lease.not_held",
            "Released at the end of the flow, so the next node does not wait a TTL for it.");
    }

    /// <summary>
    /// A <c>Durable</c> flow on a host that registered no journal is still refused.
    /// </summary>
    /// <remarks>
    /// The error keeps a job, and it is a narrower one than it had: it no longer means "this
    /// is not built yet", it means "this deployment has no journal". Running such a flow
    /// ephemerally would be the defect <c>FLOWX1028</c> existed to name, one layer lower and
    /// with no diagnostic left to raise it.
    /// </remarks>
    [Fact]
    public async Task ADurableFlowOnAHostWithNoJournalIsStillRefused()
    {
        var host = NewHost(durability: null);
        var dispatcher = new CountingDispatcher();

        host.IsDurabilityConfigured.ShouldBeFalse();

        var result = await host.RunAsync(
            DurablePlan(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("flow.durability_not_configured");
        dispatcher.Executed.ShouldBeEmpty("Nothing ran, so there is nothing to compensate.");
    }

    /// <summary>An ephemeral flow on a durable host is not journaled and takes no lease.</summary>
    /// <remarks>
    /// The negative control for the whole package. The profile is the declaration in both
    /// directions: a flow whose author declined durability must not be charged a lease
    /// acquisition and a store round trip per step because the host happens to have one.
    /// </remarks>
    [Fact]
    public async Task AnEphemeralFlowOnADurableHostIsTheExecutionItAlwaysWas()
    {
        var journal = new ScannableJournal();
        var leases = new InMemoryLeaseStore();
        var host = NewHost(new FlowDurability(journal, leases, journal));
        var dispatcher = new CountingDispatcher();
        var ct = TestContext.Current.CancellationToken;

        var result = await host.RunAsync(Plans.TwoStep(), dispatcher, Plans.Invocation, ct);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.Executed.ShouldBe([0, 1]);

        journal.Instances.ShouldBeEmpty("No instance was opened.");
        journal.Commits.ShouldBe(0);
    }

    /// <summary>The input and projection overloads journal too.</summary>
    [Fact]
    public async Task TheInputAndProjectionOverloadsJournalAsWell()
    {
        var journal = new ScannableJournal();
        var leases = new InMemoryLeaseStore();
        var host = NewHost(new FlowDurability(journal, leases, journal));
        var ct = TestContext.Current.CancellationToken;

        var seeded = await host.RunAsync(
            DurablePlan(), new CountingDispatcher(), Plans.Invocation, "input", ct);

        seeded.IsSuccess.ShouldBeTrue();

        var projected = await host.RunAsync(
            DurablePlan(),
            new CountingDispatcher(),
            Plans.Invocation,
            "input",
            static ctx => ctx.Get<string>().Length,
            ct);

        projected.IsSuccess.ShouldBeTrue();
        projected.Value.ShouldBe(5);

        journal.Instances.Count.ShouldBe(2);
        journal.Commits.ShouldBe(4, "Two steps each.");
        host.HeldLeases.ShouldBe(0);
    }

    /// <summary>
    /// A drain releases a lease the budget ran out on rather than leaving it to expire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second thing a drain has to give back, beside the detached children it already
    /// waited for. Leaving it makes the next node wait a full TTL for work this one has
    /// abandoned — the delay <c>docs/11-Distributed-Runtime.md §7</c> exists to remove.
    /// </para>
    /// <para>
    /// What is given up by releasing early is stated rather than hidden: the step in flight
    /// keeps running and its effects stand. What is not given up is the journal — the next
    /// owner's acquisition raises the fence, so this node's remaining commits are refused.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ADrainReleasesALeaseTheBudgetRanOutOn()
    {
        var journal = new ScannableJournal();
        var leases = new InMemoryLeaseStore();

        var host = NewHost(
            new FlowDurability(journal, leases, journal),
            Options(options => options.ShutdownDrainTimeout = TimeSpan.FromMilliseconds(150)));

        var dispatcher = new GatedDispatcher();
        var ct = TestContext.Current.CancellationToken;

        var running = host.RunAsync(DurablePlan(), dispatcher, Plans.Invocation, ct).AsTask();

        await dispatcher.WaitUntilEntered(ct);

        host.HeldLeases.ShouldBe(1);

        var drained = await host.DrainAsync(ct);

        drained.ShouldBeFalse("The flow is still inside its first step.");
        host.HeldLeases.ShouldBe(0, "The drain gave back what the flow did not.");

        var instances = journal.Instances;

        (await leases.ReadAsync(instances[0].InstanceId, ct)).Error.Code.ShouldBe("lease.not_held",
            "Released, so another node acquires immediately instead of after a TTL.");

        dispatcher.Release();
        await running;
    }

    // ---------------------------------------------------------------------------------
    // Suspension and signals
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A flow that waits gives its lease back and leaves one row behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is what "costs no thread, no memory and no lease while it waits"
    /// means concretely.</strong> The invocation returns, the pooled context goes back to the
    /// pool, the lease is released rather than renewed for the duration of the wait, and what
    /// is left is the instance row and the boundaries committed before it.
    /// </para>
    /// <para>
    /// Holding the lease would be the opposite arrangement, and a worse one: an offer waiting
    /// seven days for a countersignature would hold a lease for seven days, so a node restart
    /// would strand it for a TTL and a thousand waiting offers would be a thousand renewal
    /// timers on one node.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFlowThatWaitsForASignalSuspendsAndGivesItsLeaseBack()
    {
        var journal = new ScannableJournal();
        var leases = new InMemoryLeaseStore();
        var host = NewHost(new FlowDurability(journal, leases, journal));
        var dispatcher = new CountingDispatcher();
        var ct = TestContext.Current.CancellationToken;

        var result = await host.RunAsync(WaitingPlan(), dispatcher, Plans.Invocation, ct);

        result.IsSuspended.ShouldBeTrue("the flow reached its suspension point");
        result.IsSuccess.ShouldBeFalse("waiting is not finishing");
        result.Error.ShouldBeNull("and it is not failing either");
        dispatcher.Executed.ShouldBe([0], "the step after the wait was never dispatched");

        result.InstanceId.ShouldBe(
            journal.Instances[0].InstanceId,
            "the caller is told which instance to deliver the signal to. Without it a " +
            "suspended flow is unreachable, because the id is minted inside the host.");

        journal.Instances[0].State.ShouldBe(FlowInstanceState.Suspended);

        host.HeldLeases.ShouldBe(0, "a wait holds no lease");

        (await leases.ReadAsync(journal.Instances[0].InstanceId, ct)).Error.Code.ShouldBe(
            "lease.not_held",
            "released, so the node that delivers the signal does not wait a TTL to take it");
    }

    /// <summary>
    /// <c>SignalAsync</c> is <c>ResumeAsync</c> with a signal attached, and nothing else.
    /// </summary>
    /// <remarks>
    /// The lease is acquired, its token raises the fence, the frontier is read and the same
    /// <c>FlowEngine.ExecuteAsync</c> a recovery scan reaches is reached. The committed prefix
    /// is stepped over for free, because that is what the frontier already does — a signalled
    /// resume does not reimplement resumption, it <em>is</em> resumption carrying a value.
    /// </remarks>
    [Fact]
    public async Task ASignalResumesTheWaitingInstanceThroughTheSameCallARecoveryScanUses()
    {
        var journal = new ScannableJournal();
        var leases = new InMemoryLeaseStore();
        var host = NewHost(new FlowDurability(journal, leases, journal));
        var ct = TestContext.Current.CancellationToken;

        var suspended = await host.RunAsync(WaitingPlan(), new CountingDispatcher(), Plans.Invocation, ct);

        var second = new CountingDispatcher();

        var resumed = await host.SignalAsync(
            suspended.InstanceId!.Value,
            new FlowRegistration(WaitingPlan(), second),
            FlowSignal.Of("contract.countersigned", "signed-by-ada"),
            principal: null,
            ct);

        resumed.IsSuccess.ShouldBeTrue(resumed.Error?.ToString());
        second.Executed.ShouldBe([1, 2], "the wait is satisfied and the flow runs on from it");

        journal.Instances[0].State.ShouldBe(FlowInstanceState.Completed);

        host.HeldLeases.ShouldBe(0, "and the lease taken to deliver the signal is given back");
    }

    /// <summary>
    /// The same signal delivered twice does not run the flow's remaining steps twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a check written for signals. The second delivery re-enters an instance whose wait
    /// now has a committed row, so the frontier steps over it exactly as it steps over any
    /// completed step, and over everything after it too — the loop reaches the end having
    /// dispatched nothing. At-least-once delivery is the ordinary case for a transport, and
    /// this is the property that makes it safe.
    /// </para>
    /// <para>
    /// <strong>It is reported as a success, not as a refusal, and that is worth being exact
    /// about.</strong> The redelivery re-seals an instance that is already <c>Completed</c>
    /// with the same state, which both shipped stores accept — PostgreSQL's
    /// <c>CompleteAsync</c> refuses a terminal instance only when the state would
    /// <em>change</em>. So a transport that redelivers gets a second 200 rather than a
    /// conflict, and the flow's effects happened exactly once. A caller that needs to tell
    /// the two apart reads the instance.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASignalDeliveredTwiceDoesNotRunTheFlowTwice()
    {
        var journal = new ScannableJournal();
        var leases = new InMemoryLeaseStore();
        var host = NewHost(new FlowDurability(journal, leases, journal));
        var ct = TestContext.Current.CancellationToken;

        var suspended = await host.RunAsync(WaitingPlan(), new CountingDispatcher(), Plans.Invocation, ct);
        var signal = FlowSignal.Of("contract.countersigned", "signed-by-ada");
        var registration = new FlowRegistration(WaitingPlan(), new CountingDispatcher());

        var first = await host.SignalAsync(suspended.InstanceId!.Value, registration, signal, principal: null, ct);

        first.IsSuccess.ShouldBeTrue(first.Error?.ToString());

        var commits = journal.Commits;
        var again = new CountingDispatcher();

        var redelivered = await host.SignalAsync(
            suspended.InstanceId!.Value,
            new FlowRegistration(WaitingPlan(), again),
            signal,
            principal: null,
            ct);

        again.Executed.ShouldBeEmpty("nothing ran a second time");

        journal.Commits.ShouldBe(commits, "and nothing was written a second time either");

        redelivered.IsSuccess.ShouldBeTrue(
            redelivered.Error?.ToString() ??
            "a redelivery walks a fully committed frontier and re-seals the instance in the " +
            "state it is already in, which both shipped stores accept.");

        journal.Instances[0].State.ShouldBe(FlowInstanceState.Completed);
    }

    /// <summary>
    /// An instance waiting for a signal is not something a recovery scan picks up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The alternative is a stampede with the sweep's own name on it.</strong> A
    /// waiting instance is idle by definition, so a scan that treated <c>Suspended</c> as
    /// abandoned would take a lease on every waiting flow every TTL, resume it, find the same
    /// wait still open, and put it back — for as long as the wait lasts, which is the whole
    /// point of the feature.
    /// </para>
    /// <para>
    /// Both shipped indexes already list <c>Pending</c>, <c>Running</c> and
    /// <c>Compensating</c> only, so nothing had to change for this to hold. It is asserted
    /// because it now <em>matters</em>: until a flow could suspend, the state was
    /// unreachable and the exclusion was untested by anything that produced one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARecoveryScanDoesNotPickUpAnInstanceThatIsWaitingForASignal()
    {
        var journal = new ScannableJournal();
        var leases = new InMemoryLeaseStore();
        var durability = new FlowDurability(journal, leases, journal);
        var host = NewHost(durability);
        var ct = TestContext.Current.CancellationToken;

        var suspended = await host.RunAsync(WaitingPlan(), new CountingDispatcher(), Plans.Invocation, ct);

        suspended.IsSuspended.ShouldBeTrue();

        var dispatcher = new CountingDispatcher();
        var catalog = new FlowCatalog().Add(WaitingPlan(), dispatcher);

        var report = await NewScan(host, catalog, durability).RunOnceAsync(ct);

        report.Examined.ShouldBe(0, "a waiting instance is not abandoned work");
        report.Resumed.ShouldBe(0);
        dispatcher.Executed.ShouldBeEmpty();

        journal.Instances[0].State.ShouldBe(
            FlowInstanceState.Suspended, "and it is left exactly where it was");
    }

    // ---------------------------------------------------------------------------------
    // The recovery scan
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The scan finds an instance a dead node left <c>Running</c> and finishes it through the
    /// same step loop.
    /// </summary>
    /// <remarks>
    /// The committed prefix is skipped and the rest runs, which is WP-52's resumption reached
    /// by WP-55's route: nothing about the engine changes because the instance arrived from a
    /// scan rather than from a trigger.
    /// </remarks>
    [Fact]
    public async Task TheScanFinishesAnInstanceADeadNodeLeftRunning()
    {
        var journal = new ScannableJournal();
        var leases = new InMemoryLeaseStore();
        var durability = new FlowDurability(journal, leases, journal);
        var ct = TestContext.Current.CancellationToken;

        var abandoned = await AbandonAsync(journal, leases, DurablePlan(), ct);

        var host = NewHost(durability, Options(options => options.NodeName = NodeTwo));
        var dispatcher = new CountingDispatcher();
        var catalog = new FlowCatalog().Add(DurablePlan(), dispatcher);
        var scan = NewScan(host, catalog, durability);

        var report = await scan.RunOnceAsync(ct);

        report.Examined.ShouldBe(1);
        report.Resumed.ShouldBe(1);
        report.Contended.ShouldBe(0);
        report.NotRunnable.ShouldBe(0);
        report.Error.ShouldBeNull();

        dispatcher.Executed.ShouldBe([1],
            "Step 0 committed before the node died, so its effect happened and it is skipped. " +
            "Step 1 has no row, so as far as the journal is concerned it never ran.");

        var record = await journal.ReadInstanceAsync(abandoned, ct);

        record.Value.State.ShouldBe(FlowInstanceState.Completed);
        record.Value.Fence.Value.ShouldBe(2, "The second lease issued for this instance.");

        host.HeldLeases.ShouldBe(0);
        (await leases.ReadAsync(abandoned, ct)).Error.Code.ShouldBe("lease.not_held");
    }

    /// <summary>
    /// An instance that was written to recently is not a candidate at all.
    /// </summary>
    /// <remarks>
    /// The first and cheapest of the anti-stampede measures, and the one that decides what a
    /// healthy fleet costs. A node executing an instance writes its row at every step
    /// boundary, so in steady state the query returns nothing and a sweep is one indexed
    /// read — rather than every node fetching every live instance and being refused a lease
    /// on each.
    /// </remarks>
    [Fact]
    public async Task AnInstanceWrittenToRecentlyIsNotEvenACandidate()
    {
        var journal = new ScannableJournal();
        var leases = new InMemoryLeaseStore();
        var durability = new FlowDurability(journal, leases, journal);
        var ct = TestContext.Current.CancellationToken;

        await AbandonAsync(journal, leases, DurablePlan(), ct);

        var host = NewHost(durability);
        var catalog = new FlowCatalog().Add(DurablePlan(), new CountingDispatcher());

        // The clock as it really is: the row was written milliseconds ago, and the query asks
        // for rows untouched for a whole lease TTL.
        var scan = new FlowRecoveryScan(host, catalog, durability, Options(), SystemClock.Instance);

        var report = await scan.RunOnceAsync(ct);

        report.Examined.ShouldBe(0);
        report.Resumed.ShouldBe(0);
    }

    /// <summary>
    /// Two nodes sweeping the same backlog run each instance once between them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Acquisition is the arbiter and losing it is free: <c>lease.held</c> is a skip, not a
    /// retry and not an error. What this pins is the consequence — no instance is executed
    /// twice, however many nodes saw it — and it is the assertion that would fail first if
    /// the scan ever decided to wait for a contended instance instead of moving on.
    /// </para>
    /// <para>
    /// The two nodes start at a random offset into the same page, which is what stops ten
    /// nodes from contending for its first row and then its second. That is a property about
    /// wasted round trips rather than correctness, so it is described here and asserted only
    /// through its effect: both nodes get work done.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TwoNodesSweepingTheSameBacklogRunEachInstanceOnce()
    {
        var journal = new ScannableJournal();
        var leases = new InMemoryLeaseStore();
        var durability = new FlowDurability(journal, leases, journal);
        var ct = TestContext.Current.CancellationToken;

        const int Backlog = 12;

        for (var i = 0; i < Backlog; i++)
        {
            await AbandonAsync(journal, leases, DurablePlan(), ct);
        }

        // One dispatcher, shared, so a step executed twice anywhere shows up in one list.
        var dispatcher = new CountingDispatcher();
        var catalog = new FlowCatalog().Add(DurablePlan(), dispatcher);

        var first = NewHost(durability, Options(options => options.NodeName = NodeOne));
        var second = NewHost(durability, Options(options => options.NodeName = NodeTwo));

        var reports = await Task.WhenAll(
            NewScan(first, catalog, durability).RunOnceAsync(ct).AsTask(),
            NewScan(second, catalog, durability).RunOnceAsync(ct).AsTask());

        dispatcher.Executed.Count.ShouldBe(Backlog,
            "One remaining step per instance. A second node resuming an instance the first " +
            "already took would re-run its uncommitted step, and the count would be higher.");

        (reports[0].Resumed + reports[1].Resumed).ShouldBeGreaterThanOrEqualTo(Backlog);

        var instances = journal.Instances;

        instances.Count.ShouldBe(Backlog);
        instances.ShouldAllBe(record => record.State == FlowInstanceState.Completed);

        first.HeldLeases.ShouldBe(0);
        second.HeldLeases.ShouldBe(0);
    }

    /// <summary>
    /// An instance pinned to a version this node does not carry is left alone, without a
    /// lease being taken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A durable instance keeps the flow version it started with until it completes, so
    /// resuming it against another version would replay a committed prefix into a plan that
    /// never produced it. Skipping is the only safe answer.
    /// </para>
    /// <para>
    /// <strong>Skipped before acquiring, not after.</strong> Taking a lease this node cannot
    /// use would deny the instance to a node that can, for a whole TTL, on every sweep — a
    /// rolling update would starve exactly the instances it is meant to hand over.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnInstancePinnedToAVersionThisNodeDoesNotCarryIsLeftAlone()
    {
        var journal = new ScannableJournal();
        var leases = new InMemoryLeaseStore();
        var durability = new FlowDurability(journal, leases, journal);
        var ct = TestContext.Current.CancellationToken;

        var abandoned = await AbandonAsync(journal, leases, DurablePlan("2.0.0"), ct);

        var host = NewHost(durability);
        var dispatcher = new CountingDispatcher();
        var catalog = new FlowCatalog().Add(DurablePlan("1.0.0"), dispatcher);

        var report = await NewScan(host, catalog, durability).RunOnceAsync(ct);

        report.Examined.ShouldBe(1);
        report.NotRunnable.ShouldBe(1);
        report.Resumed.ShouldBe(0);
        dispatcher.Executed.ShouldBeEmpty();

        (await leases.ReadAsync(abandoned, ct)).Error.Code.ShouldBe("lease.not_held",
            "No lease was taken, so the node that does carry 2.0.0 can pick it up now.");

        catalog.TryGet("order.place", "2.0.0", out _).ShouldBeFalse();
        catalog.TryGet("order.place", "1.0.0", out var registration).ShouldBeTrue();
        registration!.Plan.Flow.Version.ShouldBe("1.0.0");
    }

    /// <summary>A sweep takes no more than the node's recovery concurrency allows.</summary>
    /// <remarks>
    /// The bound that matters after an outage, when the backlog is unbounded and what one
    /// node should pull from it is not. The next page is not requested until these finish, so
    /// the limit needs no second counter to keep correct.
    /// </remarks>
    [Fact]
    public async Task ASweepTakesNoMoreThanItsRecoveryConcurrencyAllows()
    {
        var journal = new ScannableJournal();
        var leases = new InMemoryLeaseStore();
        var durability = new FlowDurability(journal, leases, journal);
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 9; i++)
        {
            await AbandonAsync(journal, leases, DurablePlan(), ct);
        }

        var options = Options(o =>
        {
            o.MaxConcurrentRecoveries = 3;
            o.RecoveryScanBatchSize = 5;
        });

        var host = NewHost(durability, options);
        var dispatcher = new CountingDispatcher();
        var catalog = new FlowCatalog().Add(DurablePlan(), dispatcher);

        var report = await NewScan(host, catalog, durability, options).RunOnceAsync(ct);

        report.Examined.ShouldBe(3, "It stopped asking once it had all it could take.");
        report.Resumed.ShouldBe(3);
        dispatcher.Executed.Count.ShouldBe(3);
    }

    /// <summary>A draining node stops scanning before it stops running.</summary>
    /// <remarks>
    /// Taking on another node's abandoned instance while this one is shutting down would
    /// create exactly the work the drain is trying to finish, and the takeover would be
    /// refused by <c>TryEnter</c> a moment later anyway — after a lease had been acquired and
    /// released for nothing.
    /// </remarks>
    [Fact]
    public async Task ADrainingNodeDoesNotScan()
    {
        var journal = new ScannableJournal();
        var leases = new InMemoryLeaseStore();
        var durability = new FlowDurability(journal, leases, journal);
        var ct = TestContext.Current.CancellationToken;

        await AbandonAsync(journal, leases, DurablePlan(), ct);

        var host = NewHost(durability);
        var catalog = new FlowCatalog().Add(DurablePlan(), new CountingDispatcher());
        var scan = NewScan(host, catalog, durability);

        await host.DrainAsync(ct);

        (await scan.RunOnceAsync(ct)).Examined.ShouldBe(0);
    }

    /// <summary>A journal that cannot answer the query means a node that does not sweep.</summary>
    /// <remarks>
    /// Not a failure. Durable flows still run on that host and its own instances still
    /// complete; what it does not do is pick up another node's, which a single-node
    /// deployment can live with quite reasonably.
    /// </remarks>
    [Fact]
    public async Task AHostWhoseJournalCannotBeScannedDoesNotSweep()
    {
        var journal = new ScannableJournal();
        var leases = new InMemoryLeaseStore();
        var durability = new FlowDurability(journal, leases);
        var ct = TestContext.Current.CancellationToken;

        await AbandonAsync(journal, leases, DurablePlan(), ct);

        durability.CanScan.ShouldBeFalse();

        var scan = NewScan(NewHost(durability), new FlowCatalog(), durability);

        scan.IsEnabled.ShouldBeFalse();
        (await scan.RunOnceAsync(ct)).ShouldBe(RecoveryScanReport.Nothing);
    }

    /// <summary>A store that refuses the query is reported, not thrown and not swallowed.</summary>
    [Fact]
    public async Task AQueryTheStoreRefusesIsReportedOnTheSweep()
    {
        var journal = new ScannableJournal { RefuseTheQuery = true };
        var leases = new InMemoryLeaseStore();
        var durability = new FlowDurability(journal, leases, journal);

        var scan = NewScan(NewHost(durability), new FlowCatalog(), durability);

        var report = await scan.RunOnceAsync(TestContext.Current.CancellationToken);

        report.Error.ShouldNotBeNull();
        report.Error.Code.ShouldBe("journal.instance_not_found");
        report.Resumed.ShouldBe(0);
    }

    // ---------------------------------------------------------------------------------
    // Wiring
    // ---------------------------------------------------------------------------------

    /// <summary>Registering the stores is what turns durability on. Nothing else is needed.</summary>
    [Fact]
    public void RegisteringAJournalAndALeaseStoreIsWhatWiresDurability()
    {
        var journal = new ScannableJournal();

        var services = new ServiceCollection();
        services.AddFlowX(options => options.ApplicationName = "Sample.App");
        services.AddSingleton<IFlowJournal>(journal);
        services.AddSingleton<ILeaseStore>(new InMemoryLeaseStore());
        services.AddSingleton<IRecoveryIndex>(journal);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<FlowHost>().IsDurabilityConfigured.ShouldBeTrue();
        provider.GetRequiredService<FlowCatalog>().Count.ShouldBe(0);
    }

    /// <summary>A host that registered no stores is left exactly as it was.</summary>
    [Fact]
    public void AHostWithNoStoresRegisteredIsNotWiredForDurability()
    {
        var services = new ServiceCollection();
        services.AddFlowX(options => options.ApplicationName = "Sample.App");

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<FlowHost>().IsDurabilityConfigured.ShouldBeFalse();
    }

    /// <summary>Half a pair is not durability, and is treated as none of it.</summary>
    /// <remarks>
    /// A journal with no lease store would write under a token nothing issued; a lease store
    /// with no journal would fence nothing. Accepting either would produce a half-durable
    /// instance discovered in production rather than a refusal at the first invocation.
    /// </remarks>
    [Fact]
    public void AJournalWithNoLeaseStoreIsNotDurability()
    {
        var services = new ServiceCollection();
        services.AddFlowX(options => options.ApplicationName = "Sample.App");
        services.AddSingleton<IFlowJournal>(new ScannableJournal());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<FlowHost>().IsDurabilityConfigured.ShouldBeFalse();
    }

    /// <summary>The catalogue keys on the version as well as the id.</summary>
    [Fact]
    public void TheCatalogueKeysOnTheVersionAsWellAsTheId()
    {
        var catalog = new FlowCatalog()
            .Add(DurablePlan("1.0.0"), new CountingDispatcher())
            .Add(DurablePlan("2.0.0"), new CountingDispatcher());

        catalog.Count.ShouldBe(2, "Two versions of one flow are two entries, not one.");

        catalog.TryGet("order.place", "1.0.0", out var pinned).ShouldBeTrue();
        pinned!.Plan.Flow.Version.ShouldBe("1.0.0");

        catalog.TryGet("order.place", "3.0.0", out _).ShouldBeFalse();
        catalog.TryGet("order.cancel", "1.0.0", out _).ShouldBeFalse();

        Should.Throw<ArgumentNullException>(() => catalog.Add(null!, new CountingDispatcher()));
        Should.Throw<ArgumentNullException>(() => catalog.Add(DurablePlan(), null!));
    }

    /// <summary>Both stores are required to build a bundle, and neither may be null.</summary>
    [Fact]
    public void ADurabilityBundleRequiresBothStores()
    {
        Should.Throw<ArgumentNullException>(() => new FlowDurability(null!, new InMemoryLeaseStore()));
        Should.Throw<ArgumentNullException>(() => new FlowDurability(new ScannableJournal(), null!));
    }

    /// <summary>The scan refuses to be built without the things it reads.</summary>
    [Fact]
    public void TheScanRejectsNullDependencies()
    {
        var journal = new ScannableJournal();
        var durability = new FlowDurability(journal, new InMemoryLeaseStore(), journal);
        var host = NewHost(durability);
        var catalog = new FlowCatalog();
        var options = Options();

        Should.Throw<ArgumentNullException>(
            () => new FlowRecoveryScan(null!, catalog, durability, options, SystemClock.Instance));
        Should.Throw<ArgumentNullException>(
            () => new FlowRecoveryScan(host, null!, durability, options, SystemClock.Instance));
        Should.Throw<ArgumentNullException>(
            () => new FlowRecoveryScan(host, catalog, null!, options, SystemClock.Instance));
        Should.Throw<ArgumentNullException>(
            () => new FlowRecoveryScan(host, catalog, durability, null!, SystemClock.Instance));
        Should.Throw<ArgumentNullException>(
            () => new FlowRecoveryScan(host, catalog, durability, options, null!));
    }

    /// <summary>Resuming through a host with no journal is refused rather than attempted.</summary>
    [Fact]
    public async Task ResumingOnAHostWithNoJournalIsRefused()
    {
        var host = NewHost(durability: null);

        var result = await host.ResumeAsync(
            Guid.NewGuid(),
            new FlowRegistration(DurablePlan(), new CountingDispatcher()),
            ct: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("flow.durability_not_configured");

        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await host.ResumeAsync(
                Guid.NewGuid(), null!, ct: TestContext.Current.CancellationToken));
    }

    // ---------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Leaves behind exactly what a killed node leaves: a <c>Running</c> instance with one
    /// committed step, no terminal state, and no live lease.
    /// </summary>
    /// <remarks>
    /// Built through the journal rather than by killing a real execution, because what is
    /// under test here is the scan and not the crash — <c>DurableSeamTests</c> already pins
    /// that a node dying mid-step leaves this shape.
    /// </remarks>
    private static async Task<Guid> AbandonAsync(
        IFlowJournal journal, ILeaseStore leases, ExecutionPlan plan, CancellationToken ct)
    {
        var instanceId = Guid.NewGuid();

        var lease = await DurableLease.AcquireAsync(leases, instanceId, "dead-node", LeasePolicy.Default, ct);

        var begun = await lease.Value.BeginAsync(journal, plan, Plans.Invocation, cancellationToken: ct);

        begun.IsSuccess.ShouldBeTrue();

        var committed = await journal.CommitAsync(
            new StepCommit
            {
                Key = StepKey.First(instanceId, 0),
                Token = lease.Value.Token,
                CapabilityId = "order.validate",
                CapabilityVersion = "1.0.0",
                Outcome = JournalOutcome.Success,
            },
            ct);

        committed.IsSuccess.ShouldBeTrue();

        // The lease goes; the instance does not. A crash reaches the same place a TTL later.
        await lease.Value.DisposeAsync();

        return instanceId;
    }

    private static FlowRecoveryScan NewScan(
        FlowHost host, FlowCatalog catalog, FlowDurability durability, FlowXOptions? options = null) =>
        new(host, catalog, durability, options ?? Options(), new AheadClock());

    /// <summary>
    /// A clock an hour ahead, so instances written moments ago are as stale as ones abandoned
    /// before lunch.
    /// </summary>
    /// <remarks>
    /// The alternative is a lease TTL of a few milliseconds, which would make every sweep in
    /// this file race its own renewal loop and turn a suite into a flake. Moving the clock
    /// exercises the real staleness filter rather than defeating it —
    /// <see cref="AnInstanceWrittenToRecentlyIsNotEvenACandidate"/> is the same filter with
    /// the real clock, asserting the other answer.
    /// </remarks>
    private sealed class AheadClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow.AddHours(1);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => T0;
    }

    /// <summary>A dispatcher that records the indices it was asked to run.</summary>
    private sealed class CountingDispatcher : IStepDispatcher
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

        public bool Evaluate(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no branch step.");

        public int Select(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no switch step.");

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to begin.");

        public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to enter.");
    }

    /// <summary>A dispatcher that blocks inside a step until released.</summary>
    private sealed class GatedDispatcher : IStepDispatcher
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilEntered(CancellationToken ct) => _entered.Task.WaitAsync(ct);

        public void Release() => _gate.TrySetResult();

        public async ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            _entered.TrySetResult();
            await _gate.Task.ConfigureAwait(false);
            return StepOutcome.Success;
        }

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no branch step.");

        public int Select(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no switch step.");

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to begin.");

        public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to enter.");
    }

    /// <summary>
    /// The reference journal, plus the one query a recovery scan needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A wrapper rather than a rewrite: everything a durable flow writes goes through a store
    /// that passes <c>JournalConformance</c>, and what is added is exactly
    /// <see cref="IRecoveryIndex"/> — which is a separate interface precisely because it is
    /// not part of executing an instance and not every store can serve it cheaply.
    /// </para>
    /// <para>
    /// <strong>The query is the reference index's, not this file's.</strong> It used to be
    /// implemented here, which made this the only implementation of a shipped contract that
    /// nothing held to a suite — the exact thing this project's build file says it does not do,
    /// two interfaces out of three. <c>InMemoryRecoveryIndex</c> now answers it and
    /// <c>RecoveryIndexConformance</c> holds that to the contract, so the double these tests
    /// sweep against is the one the suite passes.
    /// </para>
    /// </remarks>
    private sealed class ScannableJournal : IFlowJournal, IRecoveryIndex
    {
        private readonly InMemoryFlowJournal _inner = new();
        private readonly InMemoryRecoveryIndex _index;

        private int _commits;

        public ScannableJournal() => _index = new InMemoryRecoveryIndex(_inner);

        /// <summary>Set to have the index refuse rather than answer.</summary>
        public bool RefuseTheQuery { get; init; }

        /// <summary>How many step boundaries have been committed, across every instance.</summary>
        public int Commits => Volatile.Read(ref _commits);

        /// <summary>Every instance this journal has been asked to open, in that order.</summary>
        public IReadOnlyList<FlowInstanceRecord> Instances => _inner.Instances;

        public ValueTask<Result<FlowInstanceRecord>> StartAsync(
            FlowInstanceStart start, CancellationToken cancellationToken) =>
            _inner.StartAsync(start, cancellationToken);

        public ValueTask<Result<FencingToken>> FenceAsync(
            Guid instanceId, FencingToken token, CancellationToken cancellationToken) =>
            _inner.FenceAsync(instanceId, token, cancellationToken);

        public ValueTask<Result<JournalStep>> CommitAsync(
            StepCommit commit, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _commits);

            return _inner.CommitAsync(commit, cancellationToken);
        }

        public ValueTask<Result<FlowInstanceRecord>> CompleteAsync(
            Guid instanceId,
            FencingToken token,
            FlowInstanceState state,
            JournalPayload stateBag,
            FlowWake? wake,
            CancellationToken cancellationToken) =>
            _inner.CompleteAsync(instanceId, token, state, stateBag, wake, cancellationToken);

        public ValueTask<Result<FlowInstanceRecord>> ReadInstanceAsync(
            Guid instanceId, CancellationToken cancellationToken) =>
            _inner.ReadInstanceAsync(instanceId, cancellationToken);

        public ValueTask<Result<ResumeFrontier>> ReadResumeFrontierAsync(
            Guid instanceId, CancellationToken cancellationToken) =>
            _inner.ReadResumeFrontierAsync(instanceId, cancellationToken);

        public ValueTask<Result<IReadOnlyList<OutboxRecord>>> ReadOutboxAsync(
            Guid instanceId, CancellationToken cancellationToken) =>
            _inner.ReadOutboxAsync(instanceId, cancellationToken);

        /// <inheritdoc />
        /// <remarks>
        /// The refusal is the only thing this adds. It is a knob no store has — a real store
        /// fails this query by being unreachable — and it exists so that
        /// <c>AQueryTheStoreRefusesIsReportedOnTheSweep</c> can check that the sweep reports an
        /// error rather than swallowing or throwing it.
        /// </remarks>
        public ValueTask<Result<IReadOnlyList<AbandonedInstance>>> ListAbandonedAsync(
            AbandonedInstanceQuery query, CancellationToken cancellationToken) =>
            RefuseTheQuery
                ? new(Result.Fail<IReadOnlyList<AbandonedInstance>>(
                    DurabilityErrors.InstanceNotFound(Guid.Empty)))
                : _index.ListAbandonedAsync(query, cancellationToken);
    }
}

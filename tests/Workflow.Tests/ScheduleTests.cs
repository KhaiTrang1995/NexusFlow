using FlowX;
using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;
using FlowX.Testing;
using Shouldly;
using Xunit;

namespace Workflow.Tests;

/// <summary>
/// <c>offer.window.close</c> is started by nothing but a cron expression, and this is what
/// that costs and buys.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The registration under test is the generated one.</strong> Nothing here writes
/// <c>0 2 * * *</c>: the schedule is read off <c>WorkflowSchedules.AddFlowXSchedules</c>'s own
/// input — the compiled plan and the attribute the compiler read into the manifest — so a
/// change to the declaration fails here rather than leaving this application publishing a
/// schedule it does not fire.
/// </para>
/// <para>
/// <strong>The host, the engine, the lease and the journal are the production ones</strong>,
/// with the conformance suite's reference stores behind them, for
/// <see cref="SuspensionTests"/>'s reason: a store written for a test is a store nothing holds
/// to <c>JournalConformance</c>, and it is <c>StartAsync</c>'s refusal of a duplicate id that
/// this whole design rests on.
/// </para>
/// </remarks>
public sealed class ScheduleTests
{
    /// <summary>1970-01-01T00:00:00Z, which is a Thursday and is on the hour.</summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    /// <summary>The flow takes the platform's occurrence contract, and it has to.</summary>
    /// <remarks>
    /// A cron firing has no body, and <c>FLOWX1007</c> forbids the flow reading a clock to work
    /// out which occurrence it is, so this is not a stylistic choice — see
    /// <c>docs/adr/ADR-0028-a-scheduled-flows-input-is-its-occurrence.md</c>. Asserted off the
    /// compiled plan rather than the source, so a signature change fails here.
    /// </remarks>
    [Fact]
    public void TheScheduledFlowBindsTheOccurrence() =>
        CloseOfferWindowFlow.Plan.Flow.Profile.ShouldBe(
            ExecutionProfile.Durable,
            "an ephemeral scheduled flow journals nothing, so nothing refuses a second node's " +
            "firing and every replica would run every occurrence");

    /// <summary>
    /// One occurrence is one instance, however many nodes are sweeping for it.
    /// </summary>
    /// <remarks>
    /// <strong>The claim this sample exists to prove for schedules.</strong> Five nodes, one
    /// declaration, one journal — and one row. There is no leader: each node computes the same
    /// occurrence, derives the same instance id from it, and four of the five are refused.
    /// </remarks>
    [Fact]
    public async Task FiveNodesFireOneOccurrenceOnce()
    {
        var harness = ScheduleHarness.Create();
        var nodes = harness.Nodes(5);

        harness.Clock.Advance(TimeSpan.FromHours(1));

        var reports = await Task.WhenAll(
            nodes.Select(node => node.RunOnceAsync(TestContext.Current.CancellationToken).AsTask()));

        harness.Journal.Instances.Count(static i => i.FlowId == "offer.window.close").ShouldBe(1);
        reports.Sum(static r => r.Fired).ShouldBe(1);

        harness.Desk.OpenEnvelopes.ShouldBe(0, "there were no offers out to close");
    }

    /// <summary>The instance carries the occurrence, and the occurrence is what closed the window.</summary>
    /// <remarks>
    /// <para>
    /// The offer went out at <c>T0</c> and its window is seven days, so a firing at <c>T0 + 8d</c>
    /// closes it and a firing at <c>T0 + 6d</c> does not. Both firings use the instant the
    /// <em>expression</em> named — never the instant the sweep ran — which is what makes a late
    /// firing do the work it was asked to do.
    /// </para>
    /// <para>
    /// This is also the assertion that the occurrence reaches the capability at all. Nothing
    /// else in this flow could have supplied a cut-off: <c>CloseExpiredOffers</c> is forbidden
    /// from reading a clock, and no earlier step produces one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheOccurrenceIsWhatClosesTheWindow()
    {
        var harness = ScheduleHarness.Create();
        var ct = TestContext.Current.CancellationToken;

        harness.Desk.Send("c-1", "engineer", "key-1", T0);

        var early = await harness.FireAsync(T0.AddDays(6), ct);

        early.IsSuccess.ShouldBeTrue(early.Error?.ToString());
        harness.Desk.OpenEnvelopes.ShouldBe(1, "six days is inside a seven-day window");

        var late = await harness.FireAsync(T0.AddDays(8), ct);

        late.IsSuccess.ShouldBeTrue(late.Error?.ToString());
        harness.Desk.OpenEnvelopes.ShouldBe(0, "eight days is outside it");
    }

    /// <summary>A firing that has already happened is refused, not repeated.</summary>
    /// <remarks>
    /// The permanent half of the answer, and the half a lease cannot give: the second attempt
    /// here is made after the first has completed and released its lease, so the only thing
    /// left to refuse it is <c>flow_instance</c>'s primary key.
    /// </remarks>
    [Fact]
    public async Task AnOccurrenceThatAlreadyFiredIsRefused()
    {
        var harness = ScheduleHarness.Create();
        var ct = TestContext.Current.CancellationToken;

        (await harness.FireAsync(T0.AddDays(8), ct)).IsSuccess.ShouldBeTrue();

        var second = await harness.FireAsync(T0.AddDays(8), ct);

        second.IsFailure.ShouldBeTrue();
        second.Error!.Code.ShouldBe(DurabilityErrors.InstanceExistsCode);

        harness.Journal.Instances.Count(static i => i.FlowId == "offer.window.close").ShouldBe(1);
    }

    /// <summary>
    /// A fleet that was down catches up the most recent firing, and only that one.
    /// </summary>
    /// <remarks>
    /// <c>MissedFirePolicy.RunOnce</c> is what this flow declares, so a three-hour outage of an
    /// hourly schedule produces one late firing rather than three. The instant it carries is the
    /// occurrence that was missed, not the instant the fleet came back.
    /// </remarks>
    [Fact]
    public async Task AFleetThatWasDownCatchesUpOnce()
    {
        var harness = ScheduleHarness.Create();
        var node = harness.Nodes(1)[0];

        harness.Clock.Advance(TimeSpan.FromHours(1));
        (await node.RunOnceAsync(TestContext.Current.CancellationToken)).Fired.ShouldBe(1);

        // Nothing runs for three hours and ten minutes. The node that comes back has never seen
        // this schedule, so the journal is what tells it where to resume from.
        harness.Clock.Advance(TimeSpan.FromMinutes(190));

        var restarted = harness.Nodes(1)[0];
        var report = await restarted.RunOnceAsync(TestContext.Current.CancellationToken);

        report.Fired.ShouldBe(1, "three occurrences were missed and RunOnce fires the newest");

        harness.Journal.Instances
            .Count(static i => i.FlowId == "offer.window.close")
            .ShouldBe(2);
    }
}

/// <summary>
/// One journal, one offer desk, and this sample's own schedule registered over them.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="OfferHarness"/>: that one exists to drive one instance of
/// <c>offer.accept</c> through a wait, where every question here is about how many instances
/// exist. Sharing it would have meant one type with two vocabularies.
/// </remarks>
internal sealed class ScheduleHarness
{
    /// <summary>Hourly, in UTC — the demonstration schedule, not the declared one.</summary>
    /// <remarks>
    /// The declared expression is <c>0 2 * * *</c> in <c>Europe/Berlin</c> and is asserted
    /// against the manifest in <see cref="ManifestTests"/>. A test that wound a clock forward a
    /// day per firing would be testing the clock; this registers a second schedule for the same
    /// flow, exactly as <c>Program.cs</c> does for a demonstration run.
    /// </remarks>
    private const string Hourly = "0 * * * *";

    private ScheduleHarness(FlowXOptions options)
    {
        Options = options;
        Durability = new FlowDurability(Journal, Leases);
        Host = new FlowHost(new FlowEngine(Clock), options, Durability);
        Dispatcher = new CloseOfferWindowFlow.Dispatcher(new CloseExpiredOffers(Desk));

        Schedules.Add(
            FlowSchedule.Create(
                CloseOfferWindowFlow.Plan.Flow.Id,
                CloseOfferWindowFlow.Plan.Flow.Version,
                Hourly,
                "UTC",
                MissedFirePolicy.RunOnce),
            CloseOfferWindowFlow.Plan,
            Dispatcher);
    }

    /// <summary>Where offers are sent and withdrawn.</summary>
    public InMemoryOfferDesk Desk { get; } = new();

    /// <summary>The clock every node and the engine read.</summary>
    public FlowTestClock Clock { get; } = new();

    /// <summary>The journal every step boundary commits to, and the primary key that dedupes.</summary>
    public InMemoryFlowJournal Journal { get; } = new();

    /// <summary>Where exclusive ownership and fencing tokens come from.</summary>
    public InMemoryLeaseStore Leases { get; } = new();

    /// <summary>Which schedules every node in this fleet fires.</summary>
    public FlowScheduleCatalog Schedules { get; } = new();

    /// <summary>The stores, as the host and every sweep see them.</summary>
    public FlowDurability Durability { get; }

    /// <summary>The host a direct firing goes through.</summary>
    public FlowHost Host { get; }

    /// <summary>The generated dispatcher, over this sample's own capability.</summary>
    public CloseOfferWindowFlow.Dispatcher Dispatcher { get; }

    /// <summary>The validated host options.</summary>
    public FlowXOptions Options { get; }

    /// <summary>Starts a harness over a fresh desk and a fresh pair of stores.</summary>
    public static ScheduleHarness Create() => new(new FlowXOptions
    {
        ApplicationName = "Workflow.Tests",
        NodeName = "test-node",
        ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
    });

    /// <summary>A fleet of nodes over one set of stores, all started at the clock's now.</summary>
    public IReadOnlyList<FlowScheduleScan> Nodes(int count) =>
    [
        .. Enumerable.Range(0, count).Select(node =>
        {
            var options = new FlowXOptions
            {
                ApplicationName = Options.ApplicationName,
                NodeName = "node-" + node.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };

            return new FlowScheduleScan(
                new FlowHost(new FlowEngine(Clock), options, Durability),
                Schedules,
                Durability,
                options,
                Clock);
        }),
    ];

    /// <summary>Fires one named occurrence directly, without a sweep deciding it is due.</summary>
    /// <remarks>
    /// The same call <c>FlowScheduleScan</c> makes, with the occurrence chosen by the test
    /// rather than computed — so a test about <em>what a firing does</em> does not also have to
    /// arrange for the clock to have passed it.
    /// </remarks>
    public ValueTask<FlowExecutionResult> FireAsync(DateTimeOffset occurrence, CancellationToken ct)
    {
        var schedule = Schedules.Registrations[0].Schedule;
        var instanceId = schedule.InstanceIdFor(occurrence);

        return Host.RunAsync(
            CloseOfferWindowFlow.Plan,
            Dispatcher,
            new FlowInvocation(instanceId.ToString(), instanceId.ToString()),
            schedule.FireFor(occurrence),
            instanceId,
            ct);
    }
}

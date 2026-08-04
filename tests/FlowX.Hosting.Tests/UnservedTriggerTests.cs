using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Hosting.Tests;

/// <summary>
/// Whether a node that declared an address nothing on it serves starts anyway.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The quietest failure this repository has.</strong> A <c>[BusTrigger]</c> whose
/// subscription was never registered compiles, reaches the manifest, is published, is diffed —
/// and never runs. Every symptom points at the transport: no message arrives, nothing is logged,
/// and the flow is reachable in every way a reader can check without starting the host.
/// </para>
/// <para>
/// It has been found in <c>samples/crm</c> twice. Once for the three subscriptions on
/// <c>lead.created</c>, and again for the two <c>[CronTrigger]</c> sweeps, which were declared,
/// listed in the README's table of surfaces, and fired by nothing at all from the day they were
/// written until this check refused to start the host without
/// <c>app.Services.AddFlowXSchedules()</c>.
/// </para>
/// <para>
/// The match is on <c>(flow id, version)</c> and not on the address. A subscription is registered
/// per flow, and a version-pinned instance resumes only on a node carrying its version, so an
/// address that moved between versions is a different declaration with a row of its own.
/// </para>
/// </remarks>
public sealed class UnservedTriggerTests
{
    private static readonly CapabilityDescriptor Score =
        CapabilityDescriptor.Create("lead.score", "1.0.0", isIdempotent: true);

    [Fact]
    public async Task ADeclaredBusAddressWithNoSubscriptionRefusesToStart()
    {
        var refusal = await Should.ThrowAsync<InvalidOperationException>(
            () => Validation(Declaring("lead.scoring", "Bus", "lead.created"))
                .StartAsync(TestContext.Current.CancellationToken));

        refusal.Message.ShouldContain("lead.scoring@1.0.0");
        refusal.Message.ShouldContain("lead.created");
        refusal.Message.ShouldContain(
            "AddFlowXSubscriptions()",
            customMessage: "a refusal that does not name the call is a puzzle rather than a fix.");
    }

    /// <summary>The one the CRM sample was missing, and the reason this file exists.</summary>
    [Fact]
    public async Task ADeclaredScheduleWithNoRegistrationRefusesToStart()
    {
        var refusal = await Should.ThrowAsync<InvalidOperationException>(
            () => Validation(Declaring("crm.task.escalation", "Schedule", "0 * * * *"))
                .StartAsync(TestContext.Current.CancellationToken));

        refusal.Message.ShouldContain("AddFlowXSchedules()");
    }

    [Fact]
    public async Task ADeclaredAddressThatIsServedStarts()
    {
        var bus = new FlowBusCatalog().Add(
            new BusSubscription("lead.scoring", "1.0.0", "lead.created", "scoring"),
            PlanFor("lead.scoring"),
            new SilentDispatcher());

        await Validation(Declaring("lead.scoring", "Bus", "lead.created"), bus)
            .StartAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// HTTP and agent declarations are not checked, and a worker that serves no routes is a
    /// deployment rather than a mistake.
    /// </summary>
    /// <remarks>
    /// A missing <c>MapFlowX()</c> is a 404 at the first request. The four kinds this class does
    /// check fail by staying quiet for ever, which is what earns a start-up refusal.
    /// </remarks>
    [Fact]
    public async Task AnHttpDeclarationIsNotCheckedHere() =>
        await Validation(Declaring("lead.capture", "Http", "/api/v1/leads"))
            .StartAsync(TestContext.Current.CancellationToken);

    /// <summary>A host composed by hand declares nothing, and this check has no opinion on it.</summary>
    [Fact]
    public async Task AHostThatDeclaredNothingStarts() =>
        await new FlowXStartupValidation(
            null,
            new FlowBusCatalog(),
            new FlowChangeCatalog(),
            new FlowScheduleCatalog(),
            new FlowStreamCatalog())
            .StartAsync(TestContext.Current.CancellationToken);

    private static FlowXDeclaredTriggers Declaring(string flowId, string kind, string address)
    {
        var declared = new FlowXDeclaredTriggers();

        declared.Add(new DeclaredTrigger(flowId, "1.0.0", kind, address));

        return declared;
    }

    private static FlowXStartupValidation Validation(
        FlowXDeclaredTriggers declared, FlowBusCatalog? bus = null) =>
        new(declared,
            bus ?? new FlowBusCatalog(),
            new FlowChangeCatalog(),
            new FlowScheduleCatalog(),
            new FlowStreamCatalog());

    private static ExecutionPlan PlanFor(string flowId) => ExecutionPlan.Create(
        FlowDescriptor.Create(flowId, "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
        StepGraph.Create([StepNode.ForCapability(0, Score)]));

    /// <summary>Never called: the check reads the catalogues and starts nothing.</summary>
    private sealed class SilentDispatcher : IStepDispatcher
    {
        private const string Unused = "This double exists to occupy a catalogue entry, not to run.";

        public ValueTask<StepOutcome> ExecuteAsync(
            int stepIndex, FlowContext ctx, CancellationToken ct) =>
            throw new NotSupportedException(Unused);

        public ValueTask<StepOutcome> CompensateAsync(
            int stepIndex, FlowContext ctx, CancellationToken ct) =>
            throw new NotSupportedException(Unused);

        public bool Evaluate(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException(Unused);

        public int Select(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException(Unused);

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException(Unused);

        public FlowContext EnterIteration(
            int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException(Unused);
    }
}

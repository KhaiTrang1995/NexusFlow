using EventDriven;
using FlowX;
using Shouldly;
using Xunit;

namespace EventDriven.Tests;

/// <summary>
/// Quality goal Q4 and vision criterion V2, as a test rather than as a sentence: the same
/// business chain runs behind four transports, and this fails if it stops.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This file is the structural half and <see cref="TransportEquivalenceTests"/> is the
/// behavioural one, and neither is sufficient alone.</strong> Comparing four compiled plans says
/// the four flows declare one chain; it cannot say the chain still <em>runs</em> when the
/// transport changes — a stance a broker delivery cannot satisfy, a contract the journal cannot
/// serialise or an adapter that decodes the wrong field all leave these assertions untouched and
/// break the sample. Running one billing reference through four real transports says the chain
/// works; it cannot say the four flows are the same chain rather than four chains that happen to
/// agree on one input.
/// </para>
/// <para>
/// <strong>Read out of the compiled <c>ExecutionPlan</c> and not out of the source text.</strong>
/// The plan is what the engine executes and what the manifest is written from, so a difference
/// this cannot see is a difference nothing downstream can see either. A source comparison —
/// which is what the sample's README originally proposed, over git history — would additionally
/// need the other commit to be present, which is false in every CI clone that fetches one
/// branch.
/// </para>
/// </remarks>
public sealed class TransportPortabilityTests
{
    /// <summary>The chain the sample declares, once, in the order it declares it.</summary>
    /// <remarks>
    /// Written out rather than derived from one of the four plans. Four plans compared only with
    /// each other agree perfectly when all four are wrong — a deleted step, a lost compensation
    /// or a renamed event is invisible to an equality check whose expected value is one of the
    /// subjects.
    /// </remarks>
    private static readonly string[] DeclaredChain =
    [
        "Capability invoice.validate@1.0.0",
        "Capability invoice.tax@1.0.0",
        "Capability invoice.persist@1.0.0 compensated by invoice.void@1.0.0",
        "Emit invoice.issued",
    ];

    /// <summary>Every flow that issues an invoice, and the transport that starts it.</summary>
    public static TheoryData<string, string> Transports => new()
    {
        { "invoice.issue.http", "Http" },
        { "invoice.issue.bus", "Bus" },
        { "invoice.issue.change", "Change" },
        { "invoice.issue.schedule", "Schedule" },
    };

    /// <summary>The four flows compile to one chain, and it is the chain the sample declares.</summary>
    [Theory]
    [MemberData(nameof(Transports))]
    public void EveryTransportCompilesToTheDeclaredChain(string flowId, string transport)
    {
        var plan = Plans.Single(candidate => candidate.Flow.Id == flowId);

        BusinessChainOf(plan).ShouldBe(
            DeclaredChain,
            $"'{flowId}' is started over {transport} and its chain below the transport adapter " +
            "has diverged from the one the other three run. That is quality goal Q4 gone: the " +
            "same business logic no longer moves between transports, and the divergence is in " +
            "this flow rather than in the platform.");
    }

    /// <summary>
    /// Each flow's transport costs it exactly one adapter step, and the four adapters are the
    /// three the platform's input contracts require.
    /// </summary>
    /// <remarks>
    /// This is the honest form of "only the attribute differs", and it is worth pinning because
    /// it is where the vision's illustration and the platform disagree. Each trigger kind fixes
    /// the flow's input contract — <c>ScheduledFire</c>, <c>BusMessage</c>, or the flow's own
    /// request type — so a bus flow and a cron flow cannot be one class, and the difference is
    /// paid as one decoding step rather than as a branch inside the chain. If a fifth transport
    /// ever costs two steps, or a shared step moves above this line, the claim has changed shape
    /// and this is where it is noticed (ADR-0062).
    /// </remarks>
    [Fact]
    public void ATransportCostsExactlyOneAdapterStep()
    {
        var adapters = Plans
            .OrderBy(static plan => plan.Flow.Id, StringComparer.Ordinal)
            .Select(static plan => plan.Flow.Id + " -> " + string.Join(
                ", ", AdapterChainOf(plan)))
            .ToArray();

        adapters.ShouldBe(
            [
                "invoice.issue.bus -> Capability invoice.read_request@1.0.0",
                "invoice.issue.change -> Capability invoice.read_request@1.0.0",
                "invoice.issue.http -> ",
                "invoice.issue.schedule -> Capability invoice.due@1.0.0",
            ],
            "A transport is meant to cost one decoding step and nothing else. The bus and the " +
            "change feed share theirs because both hand the flow a BusMessage; HTTP has none " +
            "because its input contract is the request itself.");
    }

    /// <summary>The four plans this gate inspects are the four the sample ships.</summary>
    /// <remarks>
    /// Without this, a renamed flow empties the subject list and every assertion above passes by
    /// having nothing to check. This repository has already shipped one test that could not fail.
    /// </remarks>
    [Fact]
    public void TheFourIssuingFlowsAreThePlansUnderTest()
    {
        Plans.Select(static plan => plan.Flow.Id).Order(StringComparer.Ordinal).ShouldBe(
            ["invoice.issue.bus", "invoice.issue.change", "invoice.issue.http", "invoice.issue.schedule"]);

        Plans.ShouldAllBe(
            static plan => plan.Flow.Profile == ExecutionProfile.Durable,
            "Three of the four transports are refused without Durable, and the fourth declares " +
            "it so that all four are measured the same way.");
    }

    // ------------------------------------------------------------------ helpers

    private static IReadOnlyList<ExecutionPlan> Plans =>
    [
        IssueInvoiceOverHttpFlow.Plan,
        IssueInvoiceOverBusFlow.Plan,
        IssueInvoiceOverChangeFlow.Plan,
        IssueInvoiceOverScheduleFlow.Plan,
    ];

    /// <summary>The steps from the first shared one onwards.</summary>
    private static string[] BusinessChainOf(ExecutionPlan plan) =>
        [.. Rendered(plan).SkipWhile(static step => !step.StartsWith(
            "Capability invoice.validate@", StringComparison.Ordinal))];

    /// <summary>The steps before the first shared one — the transport's whole cost.</summary>
    private static string[] AdapterChainOf(ExecutionPlan plan) =>
        [.. Rendered(plan).TakeWhile(static step => !step.StartsWith(
            "Capability invoice.validate@", StringComparison.Ordinal))];

    /// <summary>
    /// One plan's steps, rendered so that a difference in kind, capability, compensation or
    /// emitted event is a difference in the string.
    /// </summary>
    private static IEnumerable<string> Rendered(ExecutionPlan plan) =>
        plan.Graph.Steps.Select(static step => step.Kind switch
        {
            StepKind.Capability => step.Compensation is null
                ? $"Capability {step.Capability!.QualifiedName}"
                : $"Capability {step.Capability!.QualifiedName} compensated by {step.Compensation.QualifiedName}",
            StepKind.Emit => $"Emit {step.EventType}",
            _ => step.Kind.ToString(),
        });
}

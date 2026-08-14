using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// Criterion V1 — <em>≤ 3 files, ≤ 60 lines for a 4-step flow</em>
/// (docs/01-Vision.md §7), stopped being a review.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why it needed to stop being one.</strong> V1's "verified by" is a *sample audit*,
/// which means a person counted once. Endpoint generation later cut the sample's registration
/// from twelve lines to two: the number this criterion is about moved, in the right direction,
/// and nothing in the repository knew what the number had been or what it became. A criterion
/// that only moves when somebody re-counts is a criterion that records the last time somebody
/// was interested.
/// </para>
/// <para>
/// <strong>The subject, chosen and not inferred.</strong> V1 names
/// <c>samples/ecommerce</c>, and that sample's own README states the claim it exists to prove
/// — "≤ 3 files for a 4-step flow" — as the reason its files are flat rather than in the
/// <c>&lt;Feature&gt;/</c> layout every other sample uses. <c>order.place</c> is the flow the
/// claim is about: four plan steps (validate, reserve, capture, and the emit that is a step in
/// the plan and in the manifest), one of them compensated. The other flows in that sample are
/// the consuming half and are not what V1 was written about.
/// </para>
/// <para>
/// <strong>What counts as a file.</strong> Every file declaring something the use case would
/// not exist without: the flow, the capabilities its chain names — compensations included —
/// and the contracts those signatures exchange. Found by reading the chain and the base lists,
/// so adding a step that needs a new capability in a new file moves the number by itself. The
/// composition root counts too, and only if it names one of them; today it names none, which
/// is the whole of what endpoint generation bought and is asserted separately below rather
/// than assumed here.
/// </para>
/// <para>
/// <strong>What counts as a line, and what does not.</strong> Code lines of the flow
/// declaration — attributes, class, <c>Define</c>, and the suppressions written inside it —
/// plus every line of the composition root that names the flow or its chain. Blank lines and
/// commentary are not code, because a budget that counts them is payable by deleting
/// documentation, and this sample's documentation is most of what it is for.
/// </para>
/// <para>
/// <strong>What this deliberately does not count, and the honest consequence.</strong> The
/// bodies of the capabilities. A capability is a plain class holding a business rule, and its
/// size is a property of the business, not of FlowX — counting it would make "use case cost"
/// grow when a tax table does, and V1 sits between "dispatch overhead" and "build overhead" in
/// that table. Say it plainly: read as *every* code line of the three files, the same use case
/// is several times sixty and V1 would be false. What is gated here is what the platform costs
/// to express the use case, not how big the use case is, and the file count is what keeps that
/// reading from shrinking to "one flow class" — the capabilities and the contracts still have
/// to fit in the three files with it.
/// </para>
/// </remarks>
public sealed class UseCaseCostTests
{
    /// <summary>The sample V1 names, and the flow inside it the criterion is about.</summary>
    private const string Sample = "samples/ecommerce";

    private const string ReferenceFlow = "order.place";

    /// <summary>The criterion's two numbers, as written in 01-Vision §7.</summary>
    private const int FileBudget = 3;

    private const int LineBudget = 60;

    /// <summary>The use case is declared in three files or fewer.</summary>
    [Fact]
    public void TheReferenceUseCaseIsDeclaredInThreeFilesOrFewer()
    {
        var useCase = UseCase.Read();

        useCase.Files.Count.ShouldBeLessThanOrEqualTo(
            FileBudget,
            $"Criterion V1 allows {FileBudget} files for a 4-step flow and {ReferenceFlow} is "
            + $"now spread over {useCase.Files.Count}:" + Environment.NewLine
            + string.Join(Environment.NewLine, useCase.Files));
    }

    /// <summary>The flow itself, plus whatever it costs the composition root, is under 60 lines.</summary>
    [Fact]
    public void TheReferenceFlowCostsSixtyLinesOrFewer()
    {
        var useCase = UseCase.Read();

        useCase.Lines.ShouldBeLessThanOrEqualTo(
            LineBudget,
            $"Criterion V1 allows {LineBudget} lines for a 4-step flow. {ReferenceFlow} costs "
            + $"{useCase.Lines}: {useCase.Flow.CodeLines} in {useCase.Flow.File} and "
            + $"{useCase.RegistrationLines.Count} in the composition root.");
    }

    /// <summary>
    /// The composition root costs the use case nothing at all.
    /// </summary>
    /// <remarks>
    /// This is the assertion the recorded drift asks for. The registration used to be twelve
    /// lines of <c>MapPost</c> and is now two generated calls that name no flow — <c>MapFlowX</c>
    /// and <c>AddFlowXCapabilities</c> are written once per application however many use cases
    /// it serves, so the marginal cost of this one is zero — and the day something re-introduces a
    /// hand-written route, an explicit capability registration or a flow-shaped
    /// <c>AddSingleton</c>, that is a change in what a use case costs and it is this test that
    /// says so, not a re-count months later.
    /// </remarks>
    [Fact]
    public void TheCompositionRootCostsTheUseCaseNothing()
    {
        var useCase = UseCase.Read();

        useCase.RegistrationLines.ShouldBeEmpty(
            "The composition root names the flow or its chain, so the use case now costs lines "
            + "outside its own files. Endpoint, capability and subscription registration are "
            + "generated from the declarations; a hand-written one is either a gap in the "
            + "generator or a line that did not need writing:" + Environment.NewLine
            + string.Join(Environment.NewLine, useCase.RegistrationLines));
    }

    /// <summary>
    /// The survey still finds a four-step flow with capabilities and contracts behind it.
    /// </summary>
    /// <remarks>
    /// Both budgets above are satisfied perfectly by a use case the survey cannot see: a
    /// renamed flow id, a chain the parser stopped recognising or a sample that moved would
    /// each leave one file and zero lines. What is checked is the shape V1 describes — four
    /// steps, and the capabilities and contracts they name declared somewhere.
    /// </remarks>
    [Fact]
    public void TheUseCaseSurveyCanStillSeeItsSubject()
    {
        var useCase = UseCase.Read();

        useCase.Flow.CodeLines.ShouldBeGreaterThan(
            10,
            $"{ReferenceFlow} measured as almost nothing, so the line budget is passing vacuously.");

        useCase.Flow.Chain.Count(static call => call.Name is "Step" or "Emit").ShouldBe(
            4,
            $"{ReferenceFlow} is V1's four-step flow and its chain no longer has four steps. "
            + "Either the sample changed shape — in which case V1's subject needs re-choosing "
            + "rather than the count re-reading — or the chain is no longer being parsed.");

        useCase.Capabilities.Count.ShouldBeGreaterThan(
            3,
            "The chain's capabilities are no longer being found, so the file count is counting "
            + "the flow's own file and nothing else.");

        useCase.Contracts.ShouldNotBeEmpty(
            "The contracts the chain exchanges are no longer being found, so a contracts file "
            + "would not be counted against the budget.");
    }

    /// <summary>What one use case costs: the declarations behind it, the files, the lines.</summary>
    private sealed record UseCase(
        TypeSite Flow,
        IReadOnlyList<TypeSite> Capabilities,
        IReadOnlyList<TypeSite> Contracts,
        IReadOnlyList<string> RegistrationLines,
        IReadOnlyList<string> Files)
    {
        /// <summary>The flow's own code lines plus whatever it costs the composition root.</summary>
        public int Lines => Flow.CodeLines + RegistrationLines.Count;

        public static UseCase Read()
        {
            var types = SampleSurvey.TypesIn(Sample);

            var flow = types.SingleOrDefault(static type => type.FlowId == ReferenceFlow);

            flow.ShouldNotBeNull(
                $"No flow declares [Flow(\"{ReferenceFlow}\")] under {Sample}. That flow is "
                + "V1's subject; if it moved or was renamed, this gate's subject moves with it "
                + "and the criterion's own wording in 01-Vision §7 has to move too.");

            var steps = flow!.Steps.ToHashSet(StringComparer.Ordinal);
            var capabilities = types.Where(type => steps.Contains(type.Name)).ToList();

            var contractNames = flow.BaseNames
                .Concat(flow.Chain.SelectMany(static call => call.TypeArguments))
                .Concat(capabilities.SelectMany(static capability => capability.BaseNames))
                .Where(name => !steps.Contains(name))
                .ToHashSet(StringComparer.Ordinal);

            var contracts = types.Where(type => contractNames.Contains(type.Name)).ToList();

            var declared = new[] { flow }.Concat(capabilities).Concat(contracts).ToList();
            var named = declared.Select(static type => type.Name).ToHashSet(StringComparer.Ordinal);

            var registration = SampleSurvey.CompositionRoots(Sample)
                .SelectMany(root => SampleSurvey.LinesNaming(root, named, [ReferenceFlow]))
                .ToList();

            var files = declared.Select(static type => type.File)
                .Concat(registration.Select(static line => line[..line.IndexOf(':', StringComparison.Ordinal)]))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();

            return new UseCase(flow, capabilities, contracts, registration, files);
        }
    }
}

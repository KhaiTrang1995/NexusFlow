using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// A method a reader can hold in their head, enforced as a number.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only cognitive complexity is gated, and that is a deliberate choice between two
/// metrics.</strong> Cyclomatic complexity counts decision points, which makes it blind to the
/// difference this gate cares about: <c>SeedReader.Normalise</c> in this repository scores 39
/// cyclomatic and 0 cognitive, because it is one flat list of <c>?? []</c> defaults — a
/// coalesce per collection on a seed document — and there is nothing wrong with it. Gating
/// cyclomatic would demand that list be broken up, making it worse, while ninety other methods
/// argued about their <c>switch</c> arms. Cognitive complexity charges nesting rather than
/// breadth, so it ranks the tangled method above the wide one, which is the ranking a reader
/// would give.
/// </para>
/// <para>
/// <strong>Nesting depth is measured but not separately gated.</strong> Almost every method
/// that nests more than three deep is already over the cognitive budget or within a point or
/// two of it, because deep nesting is most of what the cognitive score charges for. A second
/// gate on the same underlying property would mostly fail twice for one cause and give the
/// author two numbers to satisfy for one fix. <see cref="MethodComplexity.MaxNesting"/> is
/// carried so the figure is available to a reviewer looking at a specific method; it is not a
/// threshold anyone has to argue with.
/// </para>
/// </remarks>
public sealed class ComplexityFitnessTests
{
    /// <summary>
    /// The most a method may cost a reader before it has to be recorded as debt.
    /// </summary>
    /// <remarks>
    /// Fifteen is SonarQube's default and is kept rather than tuned to what this repository
    /// happens to score, so the number means the same thing here as it does in the tooling
    /// everyone already knows.
    /// </remarks>
    public const int CognitiveBudget = 15;

    /// <summary>
    /// Every method already over budget, with the score it is not allowed to exceed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A ratchet rather than an exemption list.</strong> Each entry records what the
    /// method scores today. New code has to meet the budget; these may only improve, and
    /// improving one fails <see cref="NoRecordedExceedanceIsStale"/> until the number here is
    /// lowered to match — so the list can only ever shrink, and the build reports the moment it
    /// could. That is the difference between debt that is being paid and debt that has been
    /// renamed "accepted".
    /// </para>
    /// <para>
    /// <strong>One entry is large and is staying that way.</strong>
    /// <c>FlowEngine.RunRangeAsync</c> is the engine's step interpreter: one loop over a flat
    /// step array with an arm per step kind, where every arm needs the same nine pieces of
    /// state and communicates back by moving the loop index. Splitting it produces nine methods
    /// that can only be understood by mentally re-inlining them, which is a worse artefact than
    /// the loop.
    /// </para>
    /// <para>
    /// <strong>It moved from 151 to 160 when stage 4 gained its sixth kind, and to 163 when
    /// that kind learned to answer with a capability</strong>, which is the ratchet doing the
    /// other half of its job: a policy that answers for a step rather than wrapping a call has
    /// nowhere to live but the loop that owns the step's outcome. The work itself is in
    /// <c>DegradeAsync</c> — the loop keeps the four lines that decide what the step's outcome
    /// now is, because moving those out would hide the assignment that makes a failed step
    /// succeed. The three the capability half added are the resume rule: a step whose fallback
    /// already has a committed row skips the retry entirely, and that decision has to be taken
    /// where the retry is. <c>DispatchHedgedAsync</c> is recorded at 24 on the same terms as
    /// <c>KafkaBusConsumer.ReceiveAsync</c>: it is one race, and its cost is the two loops that
    /// express "wait for whichever of these happens first, and then decide whether to issue
    /// another" — which is what a hedge is, and what any extraction would have to re-inline to
    /// read.
    /// </para>
    /// <para>
    /// <strong>The one that left is what the ratchet is for.</strong> <c>SeedReader.Validate</c>
    /// was recorded at 205 — a straight-line sequence of per-collection checks that this record
    /// described as "would genuinely repay extraction" and then did not extract. It scores 2 now,
    /// and the row is gone: the checks are one <c>Error?</c>-returning method each, chained with
    /// <c>??</c> so the first failure still wins in the order it always did. That is the shape
    /// the entry was holding a place for, and <see cref="NoRecordedExceedanceIsStale"/> is what
    /// made the improvement report itself rather than sit unnoticed behind an allowance of 205.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, int> RecordedExceedances = new(StringComparer.Ordinal)
    {
        ["FlowEngine.RunRangeAsync"] = 163,
        ["SeedReader.Declarations"] = 53,
        ["SeedApplier.ApplyAsync"] = 41,
        ["FlowXOptionsValidator.Validate"] = 29,
        ["OpenApi.WritePaths"] = 27,
        ["KafkaBusConsumer.ReceiveAsync"] = 24,
        ["StepModel.SelfAndNested.get"] = 24,
        ["CronSchedule.MinutesOfDay"] = 23,
        ["CustomValues.Validate"] = 23,
        ["FlowEngine.RunPollAsync"] = 22,
        ["ProcessPublishing.Validate"] = 22,
        ["DefineCrmListView.Layout"] = 21,
        ["FlowEngine.RunSubFlowAsync"] = 20,
        ["FlowXOptionsValidator.ValidateFairness"] = 20,
        ["SchemaSet.WriteType"] = 17,
        ["SeedReader.Commitment"] = 20,
        ["CronSchedule.TryTerm"] = 19,
        ["ErrorCatalogueReader.Dispatch"] = 19,
        ["FlowScheduleScan.DueForAsync"] = 19,
        ["FlowEngine.RunIterationsConcurrentlyAsync"] = 18,
        ["StepModel.Switch"] = 18,
        ["TenantFairShare.Order"] = 18,
        ["MermaidRenderer.Label"] = 17,
        ["ProfileCostCheck.UsesDurabilityOrCannotSay"] = 17,
        ["AzureServiceBusConsumer.Partition"] = 16,
        ["FlowEmitter.EmitStepNodes"] = 16,
        ["FlowEngine.DispatchGuardedAsync"] = 16,
        ["FlowEngine.DispatchHedgedAsync"] = 24,
        ["RabbitMqBusConsumer.Partition"] = 16,
    };

    /// <summary>
    /// No method is over the cognitive budget except the ones already recorded, and none of
    /// those has got worse.
    /// </summary>
    [Fact]
    public void NoMethodExceedsTheCognitiveBudget()
    {
        var offenders = ComplexitySurvey.Methods
            .Where(static method => method.Cognitive > CognitiveBudget)
            .Where(static method =>
                !RecordedExceedances.TryGetValue(method.Name, out var recorded)
                || method.Cognitive > recorded)
            .Select(static method => $"{method.Where} scores {method.Cognitive}"
                + (RecordedExceedances.TryGetValue(method.Name, out var recorded)
                    ? $", recorded at {recorded}"
                    : string.Empty))
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            $"Cognitive complexity above {CognitiveBudget} means a reader has to hold more " +
            "branching in their head than is reasonable. Reduce the nesting — guard clauses " +
            "and early returns are usually the whole fix — or, if the method is genuinely " +
            "cohesive and reads top to bottom, record it in RecordedExceedances with a reason:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Every recorded exceedance still describes a method, and still describes it accurately.
    /// </summary>
    /// <remarks>
    /// This is the half that makes the record a ratchet rather than a licence. Without it, a
    /// method that improved from 205 to 20 would keep a recorded allowance of 205 and could
    /// drift back up unnoticed, and a method that was deleted or renamed would leave an excuse
    /// behind for whatever is written next under that name.
    /// </remarks>
    [Fact]
    public void NoRecordedExceedanceIsStale()
    {
        var byName = ComplexitySurvey.Methods
            .GroupBy(static method => method.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Max(m => m.Cognitive),
                StringComparer.Ordinal);

        var wrong = RecordedExceedances
            .Where(entry => !byName.TryGetValue(entry.Key, out var actual) || actual != entry.Value)
            .Select(entry => byName.TryGetValue(entry.Key, out var actual)
                ? $"{entry.Key} is recorded at {entry.Value} and now scores {actual} — "
                    + (actual <= CognitiveBudget
                        ? "it is within budget, so delete the row."
                        : $"lower the row to {actual}.")
                : $"{entry.Key} is recorded at {entry.Value} and no longer exists — delete the row.")
            .Order(StringComparer.Ordinal)
            .ToArray();

        wrong.ShouldBeEmpty(
            "The complexity record no longer matches what the code scores:" +
            Environment.NewLine + string.Join(Environment.NewLine, wrong));
    }

    /// <summary>
    /// The complexity survey still measures the repository the budget is written against.
    /// </summary>
    /// <remarks>
    /// The budget gate passes on an empty set, and an empty set is what a moved directory or a
    /// parser that silently returns nothing produces. The second assertion is the sharper one:
    /// a scorer that always returned zero would pass every gate above and fail this.
    /// </remarks>
    [Fact]
    public void TheComplexitySurveyStillMeasuresTheRepository()
    {
        ComplexitySurvey.Methods.Count.ShouldBeGreaterThanOrEqualTo(
            3000, "the complexity survey stopped finding the repository's methods.");

        ComplexitySurvey.Methods.Count(static method => method.Cognitive > 0)
            .ShouldBeGreaterThanOrEqualTo(
                500, "the survey is finding methods but scoring them all zero, so the budget " +
                     "gate is passing vacuously.");
    }
}

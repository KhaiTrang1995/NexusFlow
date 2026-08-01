using FlowX.Cli.Manifest;

namespace FlowX.Cli.Replay;

/// <summary>
/// The half of an instance's history that lives in the manifest rather than in the journal.
/// </summary>
/// <remarks>
/// <para>
/// A journal row's <c>step_id</c> means nothing on its own — it is an index into a compiled
/// plan. The manifest publishes that plan, which is why
/// [ADR-0020](../../../docs/adr/ADR-0020-cli-reads-the-journal-as-rows.md)) can describe
/// <c>inspect</c> as a join between two published contracts rather than as a read of an
/// internal.
/// </para>
/// <para>
/// <strong>What the join is actually for here is a warning, not a decoration.</strong> A
/// <c>Parallel</c>'s branches share one execution context, so
/// <c>TakeNondeterminism</c> at a branch's commit takes everything minted since the last
/// commit — including a sibling's. The journal cannot show that: both rows look ordinary.
/// Only the plan says which steps are branches of a fork, so only the plan can tell the
/// reader which captures are unreliable.
/// </para>
/// </remarks>
internal sealed class PlanIndex
{
    private readonly HashSet<int> _inParallel;

    private PlanIndex(HashSet<int> inParallel) => _inParallel = inParallel;

    /// <summary>The step ids that sit inside some <c>Parallel</c>'s branches.</summary>
    public IReadOnlyCollection<int> StepsInsideAFork => _inParallel;

    /// <summary>Whether the flow forks at all.</summary>
    public bool HasFork => _inParallel.Count > 0;

    /// <summary>
    /// Indexes the flow an instance is pinned to, or reports that the manifest does not
    /// describe it.
    /// </summary>
    /// <param name="manifest">The manifest to read the plan out of.</param>
    /// <param name="flowId">The instance's flow id.</param>
    /// <param name="flowVersion">The version the instance is pinned to.</param>
    /// <returns>The index, or <c>null</c> when the manifest has no such flow.</returns>
    /// <remarks>
    /// Matched on id **and** version, and null rather than a best guess when the version
    /// differs. An instance is pinned to its version for its whole life precisely so a
    /// mid-flight deployment cannot change what it means; joining it against a different
    /// version's plan would undo that at exactly the moment somebody is relying on it.
    /// </remarks>
    public static PlanIndex? ForFlow(ManifestDocument manifest, string flowId, string flowVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var flow = manifest.Flows.Find(candidate =>
            string.Equals(candidate.Id, flowId, StringComparison.Ordinal) &&
            string.Equals(candidate.Version, flowVersion, StringComparison.Ordinal));

        if (flow is null)
        {
            return null;
        }

        var inParallel = new HashSet<int>();

        Walk(flow.Steps, insideFork: false, inParallel);

        return new PlanIndex(inParallel);
    }

    /// <summary>Whether a committed step ran inside a fork.</summary>
    /// <param name="stepId">The step's index in the plan.</param>
    /// <returns>Whether its capture may carry a sibling's values.</returns>
    public bool IsInsideAFork(int stepId) => _inParallel.Contains(stepId);

    private static void Walk(List<ManifestStep> steps, bool insideFork, HashSet<int> inParallel)
    {
        foreach (var step in steps)
        {
            if (insideFork)
            {
                inParallel.Add(step.Id);
            }

            // Once inside a fork, everything below stays inside it: a ForEach nested in a
            // branch shares the branch's context, and so does a Condition's body. The flag
            // only ever turns on.
            var nested = insideFork || string.Equals(step.Kind, "Parallel", StringComparison.Ordinal);

            foreach (var branch in step.Branches)
            {
                Walk(branch, nested, inParallel);
            }
        }
    }
}

using System.Globalization;

namespace FlowX.Cli.Replay;

/// <summary>
/// What a reader of an <c>inspect</c> output must not assume.
/// </summary>
/// <remarks>
/// <para>
/// <strong>These are not disclaimers.</strong> Each one names a way the journal is known to
/// be less than it looks, and every one of them is pinned by a test somewhere else in this
/// repository. A history rendered without them would be the CLI reporting the store as more
/// reliable than it is, which is worse than not rendering it at all: an operator acts on
/// this during an incident, when they are least able to go and check.
/// </para>
/// <para>
/// <strong>Scoped wherever scoping is possible.</strong> The fork caveat names the steps it
/// applies to, because a warning that appears on every history is one a reader learns to skip.
/// That scoping is what the manifest join buys, and when there is no manifest the output says
/// so rather than staying silent — silence would read as "the check ran and found nothing".
/// </para>
/// </remarks>
internal static class Caveats
{
    /// <summary>Everything worth saying about this history, in the order it matters.</summary>
    /// <param name="history">The instance being rendered.</param>
    /// <param name="plan">The joined plan, or null when there was no manifest to join.</param>
    /// <param name="manifestGiven">Whether a manifest was found at all.</param>
    /// <returns>The caveats, possibly empty.</returns>
    public static IReadOnlyList<string> For(
        InstanceHistory history,
        PlanIndex? plan,
        bool manifestGiven)
    {
        ArgumentNullException.ThrowIfNull(history);

        var caveats = new List<string>();

        if (!history.InputIsKnown)
        {
            caveats.Add(
                "flow_instance.input is NULL for this instance, so the flow's input is not " +
                "shown. NULL does not distinguish 'started with no input' from 'the input was " +
                "never captured', and the tool will not guess between them.");
        }

        AddForkCaveat(caveats, history, plan, manifestGiven);

        if (history.Steps.Any(static step => string.Equals(step.Outcome, "Compensated", StringComparison.Ordinal)))
        {
            caveats.Add(
                "A compensation's ambient reads are captured by nothing, so the " +
                "non-deterministic values under a 'comp' row are whatever the forward step " +
                "recorded and not what the compensation itself read (ADR-0015).");
        }

        return caveats;
    }

    private static void AddForkCaveat(
        List<string> caveats,
        InstanceHistory history,
        PlanIndex? plan,
        bool manifestGiven)
    {
        if (plan is null)
        {
            caveats.Add(
                manifestGiven
                    ? "The manifest does not describe this flow at this version, so the plan " +
                      "was not joined. Steps inside a Parallel cannot be identified, and the " +
                      "attribution caveat below could not be scoped to them."
                    : "Read with no manifest, so the plan was not joined. A step's id alone " +
                      "does not say whether it ran inside a Parallel, so this history cannot " +
                      "tell you which non-deterministic captures may be misattributed. Pass " +
                      "--manifest to scope that warning; until then assume it applies.");

            return;
        }

        var forked = history.Steps
            .Where(step => plan.IsInsideAFork(step.StepId))
            .Select(static step => step.StepId)
            .Distinct()
            .OrderBy(static id => id)
            .ToList();

        if (forked.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", forked.Select(static id => "step " + id.ToString(CultureInfo.InvariantCulture)));

        caveats.Add(
            $"{names} are branches of a Parallel. A fork's branches share one execution " +
            "context, so a capture taken at one branch's commit carries everything minted " +
            "since the previous commit — including a sibling's. A value shown against one of " +
            "these steps may have been minted by another, and a branch that did mint one may " +
            "show none. This is ADR-0015's best-effort attribution, measured by " +
            "ReplayDeterminismTests.AForkAttributesOneBranchsCapturedIdToItsSiblingsRow.");
    }
}

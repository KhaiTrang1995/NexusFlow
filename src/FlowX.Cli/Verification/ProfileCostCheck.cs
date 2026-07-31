using FlowX.Cli.Manifest;

namespace FlowX.Cli.Verification;

/// <summary>
/// Flags flows that declare the <c>Durable</c> profile and use nothing durability
/// provides — the signature of a profile chosen by accident.
/// </summary>
/// <remarks>
/// <para>
/// The rule is
/// <a href="../../../docs/adr/ADR-0003-execution-profiles.md">ADR-0003</a>'s, in its
/// words: durable flows with <em>no compensation, no signals and no timers</em>. The ADR
/// records it as the cost-control lever the profile decision leaves open, and
/// <a href="../../../docs/18-Cloud-Native.md">18 §Cost</a> puts a number on why it
/// matters — a read-heavy flow mistakenly marked <c>Durable</c> can cost 100× its
/// <c>Ephemeral</c> equivalent in storage and IOPS for zero benefit.
/// </para>
/// <para>
/// <strong>It reads the manifest and nothing else.</strong> Every input the rule needs is
/// already published: the profile, each step's kind, and the compensation registered
/// against a step. So the check costs no new contract and keeps
/// <c>CliDependsOnNothingButTheManifest</c> green — which is the point of putting it here
/// rather than in an analyzer that would need the source.
/// </para>
/// <para>
/// <strong>The step kinds are read as the schema defines them, not as this repository's
/// compiler currently emits them.</strong> <c>flowx.manifest.schema.json</c> lists
/// <c>Delay</c> among a step's kinds; the generator has no <c>Delay</c> case yet. Keying
/// on the schema means the day it does, this check is already right, and it is the same
/// stance the rest of the CLI takes: the manifest is the contract.
/// </para>
/// </remarks>
public static class ProfileCostCheck
{
    /// <summary>The one rule this check has.</summary>
    public const string ProfileChosenByAccident = "FLOWX-VERIFY-001";

    private const string DurableProfile = "Durable";
    private const string AwaitSignalStep = "AwaitSignal";
    private const string DelayStep = "Delay";
    private const string SubFlowStep = "SubFlow";
    private const string DetachedMode = "Detached";
    private const string AwaitCompletionMode = "AwaitCompletion";

    /// <summary>Runs the check over one manifest.</summary>
    /// <param name="manifest">The parsed manifest.</param>
    public static CostReport Run(ManifestDocument manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var durable = manifest.Flows
            .Where(flow => string.Equals(flow.Profile, DurableProfile, StringComparison.Ordinal))
            .ToList();

        var findings = durable
            .Where(flow => !UsesDurabilityOrCannotSay(
                flow, manifest, new HashSet<string>(StringComparer.Ordinal)))
            .OrderBy(flow => flow.Id, StringComparer.Ordinal)
            .ThenBy(flow => flow.Version, StringComparer.Ordinal)
            .Select(Flag)
            .ToList();

        return new CostReport
        {
            Application = manifest.Application?.Name ?? string.Empty,
            Version = manifest.Application?.Version ?? string.Empty,
            DurableFlows = durable.Count,
            Findings = findings,
        };
    }

    /// <summary>
    /// Whether the flow uses something only a durable execution can provide — or whether
    /// the manifest does not describe enough of it for the check to say that it does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two answers are deliberately collapsed into one, and always in the flow's
    /// favour. A check that accuses a flow it cannot fully see is a check that gets
    /// switched off after the first false positive, and then it catches nothing at all.
    /// Missing a genuinely wasteful flow costs storage; accusing a correct one costs the
    /// rule.
    /// </para>
    /// <para>
    /// The case that forces it is composition: a saga whose compensating leg lives in an
    /// inline sub-flow compiled into another assembly. This manifest names the child and
    /// does not describe it, so the parent looks bare and is not.
    /// </para>
    /// </remarks>
    private static bool UsesDurabilityOrCannotSay(
        ManifestFlow flow, ManifestDocument manifest, HashSet<string> visited)
    {
        if (!string.IsNullOrEmpty(flow.Id) && !visited.Add(flow.Id!))
        {
            // Already inspected on this walk. It answered then, and the answer cannot
            // have been "uses durability" or the walk would have stopped there — so it
            // contributes nothing, and recursing again would not terminate.
            return false;
        }

        foreach (var step in Flatten(flow.Steps))
        {
            if (!string.IsNullOrEmpty(step.Compensation)
                || step.Kind is AwaitSignalStep or DelayStep)
            {
                return true;
            }

            if (step.Kind != SubFlowStep)
            {
                continue;
            }

            // AwaitCompletion suspends the parent until the child finishes, which is
            // durability by itself and needs no further inspection. Detached gives the
            // child its own deadline, lifecycle and profile, so what it does says nothing
            // about the parent. Inline — the DSL's default, and so the reading when the
            // manifest carries no mode — runs the child's steps inside the parent's
            // execution, which makes the child's compensations and signals the parent's.
            if (string.Equals(step.Mode, AwaitCompletionMode, StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(step.Mode, DetachedMode, StringComparison.Ordinal))
            {
                continue;
            }

            var child = string.IsNullOrEmpty(step.Flow)
                ? null
                : manifest.Flows.FirstOrDefault(
                    candidate => string.Equals(candidate.Id, step.Flow, StringComparison.Ordinal));

            if (child is null || UsesDurabilityOrCannotSay(child, manifest, visited))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every step of a block, steps nested in a branch included.</summary>
    /// <remarks>
    /// Nesting matters here rather than being tidiness: a saga that compensates inside a
    /// <c>When</c> arm, or awaits a signal in one case of a <c>Switch</c>, is using
    /// durability exactly as intended. Reading only the top level would report every one
    /// of them.
    /// </remarks>
    private static IEnumerable<ManifestStep> Flatten(IEnumerable<ManifestStep> steps) =>
        steps.SelectMany(step => step.Branches.SelectMany(Flatten).Prepend(step));

    private static CostFinding Flag(ManifestFlow flow)
    {
        var id = string.IsNullOrEmpty(flow.Id) ? "(unnamed)" : flow.Id;

        return new CostFinding
        {
            Code = ProfileChosenByAccident,
            Subject = string.IsNullOrEmpty(flow.Version) ? $"flow {id}" : $"flow {id}@{flow.Version}",
            Summary = "profile is Durable, with no compensation, no signal and no timer",
            Consequence =
                "It pays for a journal write per step, a lease and a resumption path, and "
                + "uses none of the three.",
        };
    }
}

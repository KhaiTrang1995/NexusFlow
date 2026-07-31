using System.Text.Json.Serialization;

namespace FlowX.Cli.Verification;

/// <summary>The verdict of <c>flowx verify --cost</c> on one manifest.</summary>
/// <remarks>
/// <para>
/// A value rather than a stream of console writes, for the reason
/// <see cref="Diffing.DiffReport"/> is one: the rule is the part worth testing, and it is
/// only testable in isolation when deciding and printing are separate. It is also what
/// lets one run produce text for a person and JSON for whatever reads the build
/// afterwards, with no way for the two to disagree.
/// </para>
/// <para>
/// Property order is the serialised order, so a consumer that only needs the verdict
/// reads the first line of the document rather than all of it.
/// </para>
/// </remarks>
public sealed class CostReport
{
    /// <summary>The application the manifest describes.</summary>
    public string Application { get; init; } = string.Empty;

    /// <summary>The version of that application.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Whether every durable flow uses something durability provides.</summary>
    /// <remarks>The single bit a CI gate reads. Everything else explains it.</remarks>
    public bool Passed => Findings.Count == 0;

    /// <summary>
    /// How many flows declared <c>Durable</c> and were therefore examined.
    /// </summary>
    /// <remarks>
    /// Published because a bare "0 findings" cannot be told apart from a check that
    /// examined nothing — a manifest read from the wrong path, or an application that
    /// declares no durable flow at all. The denominator is what makes the numerator
    /// mean something.
    /// </remarks>
    public int DurableFlows { get; init; }

    /// <summary>How many of those were flagged.</summary>
    public int Flagged => Findings.Count;

    /// <summary>Every flow worth reporting, in flow-id order.</summary>
    public IReadOnlyList<CostFinding> Findings { get; init; } = [];
}

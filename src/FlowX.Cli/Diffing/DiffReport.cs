using System.Text.Json.Serialization;

namespace FlowX.Cli.Diffing;

/// <summary>The verdict on one pair of manifests.</summary>
/// <remarks>
/// <para>
/// Deliberately a value, not a stream of console writes. The classification rules are
/// the part worth testing, and they are only testable in isolation if deciding and
/// printing are separate things — which is also what lets the same run produce text for
/// a human and JSON for whatever reads the build afterwards.
/// </para>
/// <para>
/// Property order is the serialised order: the verdict and the counts come before the
/// findings, so a consumer that only needs to know whether to fail the build reads the
/// first few hundred bytes rather than the whole document.
/// </para>
/// </remarks>
public sealed class DiffReport
{
    /// <summary>The application both manifests describe.</summary>
    public string Application { get; init; } = string.Empty;

    /// <summary>Version of the manifest compared against.</summary>
    public string BaselineVersion { get; init; } = string.Empty;

    /// <summary>Version of the manifest under test.</summary>
    public string CandidateVersion { get; init; } = string.Empty;

    /// <summary>Whether the candidate can replace the baseline without breaking a consumer.</summary>
    /// <remarks>
    /// The single bit the CI gate reads. Everything else in this report exists to explain
    /// it.
    /// </remarks>
    public bool Compatible => Breaking == 0;

    /// <summary>Number of breaking findings.</summary>
    public int Breaking => Count(DiffSeverity.Breaking);

    /// <summary>Number of additive findings.</summary>
    public int Additive => Count(DiffSeverity.Additive);

    /// <summary>Number of neutral findings.</summary>
    public int Neutral => Count(DiffSeverity.Neutral);

    /// <summary>Every difference worth reporting, ordered deterministically.</summary>
    public IReadOnlyList<DiffFinding> Findings { get; init; } = [];

    /// <summary>Whether at least one consumer-visible break was found.</summary>
    /// <remarks>
    /// The inverse of <see cref="Compatible"/>, for call sites that read better positively.
    /// Not serialised: one fact stated twice in a document invites the two to disagree.
    /// </remarks>
    [JsonIgnore]
    public bool HasBreakingChange => !Compatible;

    private int Count(DiffSeverity severity) => Findings.Count(f => f.Severity == severity);
}

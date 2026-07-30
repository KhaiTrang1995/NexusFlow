using System.Text.Json.Serialization;

namespace FlowX.Cli.Diffing;

/// <summary>What a difference between two manifests costs the people downstream.</summary>
/// <remarks>
/// Three values rather than two, because "did anything change?" is the question that
/// makes a diff tool useless. A build changes the manifest constantly — a line moved,
/// a version bumped — and a tool that reports all of it is a tool whose output people
/// learn to skip. The severity is the whole product: it decides what fails the build,
/// what belongs in a changelog, and what is not worth a reader's attention.
/// </remarks>
public enum DiffSeverity
{
    /// <summary>Recorded for a human, but no consumer has to do anything about it.</summary>
    Neutral,

    /// <summary>Safe for every existing consumer: nothing that worked stops working.</summary>
    Additive,

    /// <summary>An existing consumer breaks. Fails the build.</summary>
    Breaking,
}

/// <summary>One classified difference between a baseline manifest and a candidate.</summary>
/// <remarks>
/// <para>
/// <see cref="Consequence"/> exists because a diff that says <c>idempotent: true → false</c>
/// has told a reviewer what changed and nothing about whether to care. The rule that
/// produced the finding knows why it is breaking; saying so at the point of the finding
/// is the difference between a gate people obey and a gate people override.
/// </para>
/// <para>
/// <see cref="Code"/> is stable and public. It is what a CI log is grepped for, what an
/// ADR waiver cites, and — the day a suppression mechanism exists — what gets suppressed.
/// Codes are therefore never reused for a different rule, only retired.
/// </para>
/// </remarks>
public sealed record DiffFinding
{
    /// <summary>Stable rule identifier, e.g. <c>FLOWX-DIFF-013</c>.</summary>
    public required string Code { get; init; }

    /// <summary>How much the difference costs a consumer.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<DiffSeverity>))]
    public required DiffSeverity Severity { get; init; }

    /// <summary>What changed, e.g. <c>capability payment.capture@2</c>.</summary>
    public required string Subject { get; init; }

    /// <summary>How it changed, in one line.</summary>
    public required string Summary { get; init; }

    /// <summary>Why it matters to somebody who depends on the subject.</summary>
    public string Consequence { get; init; } = string.Empty;
}

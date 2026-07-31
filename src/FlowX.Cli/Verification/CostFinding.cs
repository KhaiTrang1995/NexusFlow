namespace FlowX.Cli.Verification;

/// <summary>One flow that pays for an execution profile it does not use.</summary>
/// <remarks>
/// <para>
/// No severity field, unlike <see cref="Diffing.DiffFinding"/>. That report classifies
/// because its rules genuinely differ in what they cost a consumer; this check has one
/// rule and every finding it produces is the same statement about the same mistake.
/// Adding a column with one value in it would suggest a distinction the check does not
/// make.
/// </para>
/// <para>
/// <see cref="Consequence"/> carries its weight for the same reason it does in the diff:
/// "profile is Durable" tells a reader what the manifest says and nothing about whether
/// to act. The rule knows why it fired, and saying so at the point of the finding is what
/// separates a report people read from one people filter out.
/// </para>
/// </remarks>
public sealed record CostFinding
{
    /// <summary>Stable rule identifier, e.g. <c>FLOWX-VERIFY-001</c>.</summary>
    /// <remarks>
    /// Stable and public, like a diff code: it is what a build log is grepped for and what
    /// an ADR cites. Codes are retired, never reused for a different rule.
    /// </remarks>
    public required string Code { get; init; }

    /// <summary>What the finding is about, e.g. <c>flow report.daily@1.0.0</c>.</summary>
    public required string Subject { get; init; }

    /// <summary>What is wrong, in one line.</summary>
    public required string Summary { get; init; }

    /// <summary>Why it is worth the reader's attention.</summary>
    public string Consequence { get; init; } = string.Empty;
}

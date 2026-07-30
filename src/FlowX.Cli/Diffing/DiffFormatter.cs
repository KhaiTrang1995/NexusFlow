using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowX.Cli.Diffing;

/// <summary>Renders a <see cref="DiffReport"/> for the two things that read it.</summary>
/// <remarks>
/// <para>
/// Both formats come from the same report rather than from two traversals of the
/// manifests, so the gate and the log can never disagree about what was found — the
/// failure mode where a build goes red and the printed output shows nothing wrong.
/// </para>
/// <para>
/// Text is the default because the overwhelmingly common reader is a person scrolling a
/// CI log after a build went red, and that person needs the rule code, the subject and
/// the reason on screen without piping anything through a JSON processor.
/// </para>
/// </remarks>
public static class DiffFormatter
{
    /// <summary>
    /// Serialisation bound to a relaxed escaper rather than the HTML-safe default.
    /// </summary>
    /// <remarks>
    /// The default encoder escapes <c>&gt;</c>, <c>&amp;</c> and every non-ASCII character
    /// so the result is safe to interpolate into a web page. This document is written to a
    /// file or a pipe and read by a build step and by the person looking at the build step,
    /// and <c>-></c> in place of <c>-&gt;</c> in every summary makes it materially
    /// harder for the second of those to read. Nothing here is ever emitted into markup.
    /// </remarks>
    private static readonly DiffJsonContext Json = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>Renders the report for a human reading a build log.</summary>
    /// <param name="report">The report to render.</param>
    public static string ToText(DiffReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();

        builder.Append("flowx diff — ").Append(report.Application);

        if (report.BaselineVersion.Length > 0 || report.CandidateVersion.Length > 0)
        {
            // Context, not a finding: the application version deliberately takes no part in
            // the classification, but a reader still has to know which two things were
            // compared before trusting a verdict about them.
            builder.Append(": ").Append(Show(report.BaselineVersion))
                .Append(" -> ").Append(Show(report.CandidateVersion));
        }

        builder.Append('\n');

        if (report.Findings.Count == 0)
        {
            // Said explicitly. Silence on success is indistinguishable from a tool that
            // failed to run, and this one runs in a job nobody watches until it is red.
            builder.Append("\nNo contract changes.\n");
            return builder.ToString();
        }

        Section(builder, report, DiffSeverity.Breaking);
        Section(builder, report, DiffSeverity.Additive);
        Section(builder, report, DiffSeverity.Neutral);

        builder.Append('\n')
            .Append(Plural(report.Breaking, "breaking change", "breaking changes")).Append(", ")
            .Append(Count(report.Additive)).Append(" additive, ")
            .Append(Count(report.Neutral)).Append(" neutral — ")
            .Append(report.Compatible ? "compatible." : "INCOMPATIBLE.")
            .Append('\n');

        return builder.ToString();
    }

    /// <summary>Renders the report for whatever consumes the build afterwards.</summary>
    /// <param name="report">The report to render.</param>
    public static string ToJson(DiffReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return JsonSerializer.Serialize(report, Json.DiffReport) + "\n";
    }

    private static void Section(StringBuilder builder, DiffReport report, DiffSeverity severity)
    {
        var findings = report.Findings.Where(f => f.Severity == severity).ToList();

        if (findings.Count == 0)
        {
            // An empty heading is a line the reader has to parse to learn nothing.
            return;
        }

        builder.Append('\n').Append(severity.ToString().ToUpperInvariant())
            .Append(" (").Append(Count(findings.Count)).Append(")\n");

        foreach (var finding in findings)
        {
            builder.Append("  ").Append(finding.Code).Append("  ").Append(finding.Subject).Append('\n');
            builder.Append("      ").Append(finding.Summary).Append('\n');

            if (finding.Consequence.Length > 0)
            {
                builder.Append("      ").Append(finding.Consequence).Append('\n');
            }
        }
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Plural(int value, string singular, string plural)
        => Count(value) + " " + (value == 1 ? singular : plural);

    private static string Show(string value) => value.Length == 0 ? "(unversioned)" : value;
}

/// <summary>Source-generated serialisation for the report, so the tool stays AOT-clean.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(DiffReport))]
public sealed partial class DiffJsonContext : JsonSerializerContext;

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowX.Cli.Verification;

/// <summary>Renders a <see cref="CostReport"/> for the two things that read it.</summary>
/// <remarks>
/// Both formats come from the one report, for the reason
/// <see cref="Diffing.DiffFormatter"/> does it that way: a gate and a log that traverse
/// the manifest separately can disagree, and the failure mode is a build going red with
/// nothing wrong printed anywhere.
/// </remarks>
public static class CostFormatter
{
    /// <summary>
    /// Serialisation bound to the relaxed escaper, matching the diff report.
    /// </summary>
    /// <remarks>
    /// The HTML-safe default escapes every non-ASCII character, which would turn the em
    /// dashes in a consequence into <c>—</c> for a reader who is looking at a build
    /// log, not at markup. Nothing here is ever emitted into a web page.
    /// </remarks>
    private static readonly CostJsonContext Json = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>Renders the report for a person reading a build log.</summary>
    /// <param name="report">The report to render.</param>
    public static string ToText(CostReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();

        builder.Append("flowx verify --cost — ")
            .Append(report.Application.Length == 0 ? "(unnamed application)" : report.Application);

        if (report.Version.Length > 0)
        {
            builder.Append(' ').Append(report.Version);
        }

        builder.Append('\n');

        if (report.DurableFlows == 0)
        {
            // Said in as many words. Silence reads the same as a check that failed to
            // run, and this one runs in a job nobody watches until it goes red.
            builder.Append("\nNo flow declares the Durable profile. Nothing to check.\n");
            return builder.ToString();
        }

        if (report.Passed)
        {
            builder.Append("\nEvery durable flow uses durability — ")
                .Append(Count(report.DurableFlows)).Append(" checked.\n");

            return builder.ToString();
        }

        builder.Append("\nCOST (").Append(Count(report.Flagged)).Append(")\n");

        foreach (var finding in report.Findings)
        {
            builder.Append("  ").Append(finding.Code).Append("  ").Append(finding.Subject).Append('\n');
            builder.Append("      ").Append(finding.Summary).Append('\n');

            if (finding.Consequence.Length > 0)
            {
                builder.Append("      ").Append(finding.Consequence).Append('\n');
            }
        }

        // The denominator is the point: "1 of 1" and "1 of 40" are the same finding and
        // very different situations, and the reader is deciding which one they are in.
        // The noun agrees with the total and the verb with the count that was flagged, so
        // "1 of 2 durable flows uses" and "2 of 3 durable flows use" both read.
        builder.Append('\n')
            .Append(Count(report.Flagged)).Append(" of ").Append(Count(report.DurableFlows))
            .Append(report.DurableFlows == 1 ? " durable flow" : " durable flows")
            .Append(report.Flagged == 1 ? " uses" : " use")
            .Append(" nothing durability provides.\n");

        return builder.ToString();
    }

    /// <summary>Renders the report for whatever consumes the build afterwards.</summary>
    /// <param name="report">The report to render.</param>
    public static string ToJson(CostReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return JsonSerializer.Serialize(report, Json.CostReport) + "\n";
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Source-generated serialisation for the report, so the tool stays AOT-clean.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(CostReport))]
public sealed partial class CostJsonContext : JsonSerializerContext;

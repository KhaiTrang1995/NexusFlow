using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FlowX.Cli.Replay;

/// <summary>
/// Renders an instance's history for a person, and for <c>jq</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The shape follows
/// [12-Observability §5](../../../docs/12-Observability.md#5-flow-replay--the-differentiator)</strong>
/// — header, then steps in commit order with scope, attempt, outcome, capability and
/// duration, then the non-determinism capture. Two departures from the worked example there
/// are deliberate and recorded in [22-CLI §9](../../../docs/22-CLI.md): outcome markers are
/// ASCII rather than emoji, and there is no <c>Trigger:</c> line because nothing journals a
/// trigger.
/// </para>
/// <para>
/// <strong>Both formats carry the caveats.</strong> A JSON consumer that saw only
/// <c>"input": null</c> would be free to treat it as an empty payload — the exact conclusion
/// the text output refuses to let a human draw — so <c>inputKnown</c> and <c>caveats</c> are
/// part of the document rather than decoration on the human one.
/// </para>
/// </remarks>
internal static class HistoryFormatter
{
    private const string Unknown = "unknown";

    /// <summary>Renders the history as text.</summary>
    /// <param name="history">The instance to render.</param>
    /// <param name="plan">The flow's plan, or null when no manifest was available.</param>
    /// <param name="caveats">What the reader must not assume, in the order they matter.</param>
    /// <returns>The rendered history.</returns>
    public static string ToText(
        InstanceHistory history,
        PlanIndex? plan,
        IReadOnlyList<string> caveats)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(caveats);

        var text = new StringBuilder();
        var labelled = MultiAttemptSteps(history);

        text.Append(CultureInfo.InvariantCulture, $"Flow {history.FlowId}@{history.FlowVersion}")
            .Append(CultureInfo.InvariantCulture, $"   instance {history.InstanceId}");

        if (history.TenantId is { Length: > 0 } tenant)
        {
            text.Append(CultureInfo.InvariantCulture, $"   tenant {tenant}");
        }

        text.AppendLine();

        text.Append(CultureInfo.InvariantCulture, $"State: {history.State}")
            .Append(CultureInfo.InvariantCulture, $"   Duration: {Duration(history.Elapsed.TotalMilliseconds)}")
            .Append(CultureInfo.InvariantCulture, $"   Started: {Instant(history.CreatedAt)}")
            .AppendLine();

        if (history.CorrelationId is { Length: > 0 } correlation)
        {
            text.Append(CultureInfo.InvariantCulture, $"Correlation: {correlation}").AppendLine();
        }

        AppendInput(text, history);

        text.AppendLine();

        if (history.Steps.Count == 0)
        {
            text.AppendLine("  (no steps committed)");
        }

        foreach (var step in history.Steps)
        {
            AppendStep(text, step, plan, labelled);
        }

        AppendCaptures(text, history, plan);

        text.AppendLine();

        text.Append(CultureInfo.InvariantCulture, $"{history.Steps.Count} step")
            .Append(history.Steps.Count == 1 ? string.Empty : "s")
            .AppendLine(" in commit order.");

        if (caveats.Count > 0)
        {
            text.AppendLine().AppendLine("Caveats:");

            foreach (var caveat in caveats)
            {
                text.Append("  - ").AppendLine(caveat);
            }
        }

        return text.ToString();
    }

    /// <summary>Renders the history as JSON.</summary>
    /// <param name="history">The instance to render.</param>
    /// <param name="plan">The flow's plan, or null when no manifest was available.</param>
    /// <param name="caveats">What the reader must not assume.</param>
    /// <returns>The rendered document.</returns>
    public static string ToJson(
        InstanceHistory history,
        PlanIndex? plan,
        IReadOnlyList<string> caveats)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(caveats);

        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            writer.WriteString("flow", $"{history.FlowId}@{history.FlowVersion}");
            writer.WriteString("instance", history.InstanceId);
            WriteNullable(writer, "tenant", history.TenantId);
            writer.WriteString("state", history.State);
            WriteNullable(writer, "correlationId", history.CorrelationId);
            WriteNullable(writer, "traceId", history.TraceId);
            writer.WriteString("startedAt", history.CreatedAt);
            writer.WriteString("updatedAt", history.UpdatedAt);
            writer.WriteNumber("durationMs", (long)history.Elapsed.TotalMilliseconds);

            WriteRawOrNull(writer, "input", history.Input);

            // The field that stops a machine reader repeating the mistake the text output
            // forbids a human: `"input": null` alone is indistinguishable from a flow that
            // genuinely received nothing.
            writer.WriteBoolean("inputKnown", history.InputIsKnown);

            writer.WriteStartArray("steps");

            foreach (var step in history.Steps)
            {
                writer.WriteStartObject();
                writer.WriteString("scope", step.Scope);
                writer.WriteNumber("step", step.StepId);
                writer.WriteNumber("attempt", step.Attempt);
                writer.WriteNumber("sequence", step.Sequence);
                writer.WriteString("capability", step.Capability);
                writer.WriteString("outcome", step.Outcome);
                writer.WriteNumber("durationMs", step.DurationMs);
                writer.WriteString("committedAt", step.CommittedAt);
                WriteRawOrNull(writer, "result", step.Result);
                WriteRawOrNull(writer, "nondeterministic", step.Nondeterminism);

                writer.WriteBoolean(
                    "captureMayBeMisattributed",
                    plan is not null && plan.IsInsideAFork(step.StepId));

                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteBoolean("planJoined", plan is not null);

            writer.WriteStartArray("caveats");

            foreach (var caveat in caveats)
            {
                writer.WriteStringValue(caveat);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + Environment.NewLine;
    }

    /// <summary>
    /// The steps whose attempt number has to be shown because more than one row records them.
    /// </summary>
    /// <remarks>
    /// <strong>A departure from 12 §5's worked output, and an improvement on it.</strong> That
    /// example labels an attempt only when it is not the first, which leaves a retried step's
    /// two rows looking like an unlabelled one and a labelled one. The interesting fact is
    /// that the step was attempted more than once, so every row of such a step is labelled and
    /// a step attempted exactly once is not labelled at all.
    /// </remarks>
    private static HashSet<(string Scope, int StepId)> MultiAttemptSteps(InstanceHistory history)
    {
        var seen = new HashSet<(string, int)>();
        var repeated = new HashSet<(string, int)>();

        foreach (var step in history.Steps)
        {
            if (!seen.Add((step.Scope, step.StepId)))
            {
                repeated.Add((step.Scope, step.StepId));
            }
        }

        return repeated;
    }

    private static void AppendInput(StringBuilder text, InstanceHistory history)
    {
        if (history.Input is { } input)
        {
            text.Append("Input: ").AppendLine(Clip(input));
            return;
        }

        // The single most important line in this renderer. `flow_instance.input` is NULL on
        // every row written so far, and NULL does not distinguish "this flow was started with
        // no input" from "the input was never captured". Rendering `{}` — or `none` — would be
        // the CLI making a claim the store does not support, in the one output an operator
        // reads when they are least able to check it.
        text.AppendLine($"Input: {Unknown} — flow_instance.input is NULL, which does not distinguish");
        text.AppendLine("       a flow started with no input from one whose input was never captured.");
    }

    private static void AppendStep(
        StringBuilder text,
        HistoryStep step,
        PlanIndex? plan,
        HashSet<(string Scope, int StepId)> labelled)
    {
        text.Append("  ")
            .Append(Marker(step.Outcome).PadRight(6))
            .Append(step.Label.PadRight(14))
            .Append(step.Capability.PadRight(30))
            .Append(Duration(step.DurationMs).PadLeft(8));

        if (labelled.Contains((step.Scope, step.StepId)) || step.Attempt > 1)
        {
            text.Append(CultureInfo.InvariantCulture, $"   (attempt {step.Attempt})");
        }

        if (step.Result is { Length: > 0 } result)
        {
            text.Append("   -> ").Append(Clip(result));
        }

        if (step.Nondeterminism is not null && plan is not null && plan.IsInsideAFork(step.StepId))
        {
            text.Append("   [capture may be a sibling's — see caveats]");
        }

        text.AppendLine();
    }

    private static void AppendCaptures(StringBuilder text, InstanceHistory history, PlanIndex? plan)
    {
        var captured = history.Steps.Where(static step => step.Nondeterminism is not null).ToList();

        if (captured.Count == 0)
        {
            return;
        }

        text.AppendLine().AppendLine("  Non-deterministic values captured:");

        foreach (var step in captured)
        {
            text.Append("      ")
                .Append(step.Label.PadRight(14))
                .Append(Clip(step.Nondeterminism!, 160));

            if (plan is not null && plan.IsInsideAFork(step.StepId))
            {
                text.Append("   [inside a fork]");
            }

            text.AppendLine();
        }
    }

    /// <summary>
    /// The outcome, as three characters that survive a pipe.
    /// </summary>
    /// <remarks>
    /// ASCII rather than 12 §5's emoji, and the reason is in
    /// [22-CLI §9](../../../docs/22-CLI.md). This output goes into CI logs and files at least
    /// as often as into a terminal; emoji are double-width on some terminals and single on
    /// others, which breaks the column alignment that makes a history scannable, and
    /// <c>grep FAIL</c> is a thing an operator can type.
    /// </remarks>
    private static string Marker(string outcome) => outcome switch
    {
        "Success" => "ok",
        "Failure" => "FAIL",
        "Compensated" => "comp",
        _ => outcome,
    };

    private static string Duration(double milliseconds) => milliseconds switch
    {
        <= 0 => "-",
        < 1000 => ((long)milliseconds).ToString(CultureInfo.InvariantCulture) + "ms",
        _ => (milliseconds / 1000).ToString("0.##", CultureInfo.InvariantCulture) + "s",
    };

    private static string Instant(DateTimeOffset moment) =>
        moment.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>Keeps one payload from taking the whole screen, and says when it did.</summary>
    private static string Clip(string value, int limit = 96)
    {
        var collapsed = value.ReplaceLineEndings(" ");

        return collapsed.Length <= limit
            ? collapsed
            : string.Concat(collapsed.AsSpan(0, limit), "… (clipped)");
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    /// <summary>
    /// Writes a stored payload as JSON rather than as a string containing JSON.
    /// </summary>
    /// <remarks>
    /// The column holds a document. Escaping it into a string would make every consumer parse
    /// twice and would change what the value <em>is</em> on the way through a tool whose whole
    /// job is to report the store faithfully. A column that somehow does not hold valid JSON
    /// is written as a string rather than corrupting the document around it.
    /// </remarks>
    private static void WriteRawOrNull(Utf8JsonWriter writer, string name, string? json)
    {
        if (json is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WritePropertyName(name);

        try
        {
            writer.WriteRawValue(json);
        }
        catch (JsonException)
        {
            writer.WriteStringValue(json);
        }
    }
}

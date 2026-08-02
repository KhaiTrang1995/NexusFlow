using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FlowX.Ai;

/// <summary>
/// What one <c>flowx.manifest.json</c> can be shown to say about itself.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is <c>flowx ai review</c> with the AI taken out of it, and that is a finding
/// rather than a shortcut.</strong> docs/13 §5 describes an <c>IAiProvider</c> — Anthropic,
/// OpenAI, Azure OpenAI, local — receiving the manifest and returning risk findings. Every
/// finding <c>samples/ai-agent</c>'s README shows as an example is a set operation over the
/// document: an event with a producer and no consumer is a lookup, a public capability with
/// declared side effects is a filter, an uncompensated effectful step is a difference. Putting a
/// model behind a set difference makes a reproducible answer unreproducible, unassertable in CI
/// and chargeable per run.
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0060-the-server-asks-the-caller-for-what-it-does-not-have.md">ADR-0060</a>
/// records where a model is genuinely wanted instead: narrating this, over MCP's
/// <c>sampling/createMessage</c>, using the caller's own model rather than a key this deployment
/// holds.
/// </para>
/// <para>
/// <strong>One finding from that README is deliberately absent, and it is the headline one.</strong>
/// It reads <c>"order.place step 2 (payment.capture) has a retry policy and a 2s timeout, but the
/// flow deadline is 30s. Worst case: 2s + 0.2s + 2s + 0.6s + 2s = 6.8s"</c>. The manifest cannot
/// support that sentence: <c>ManifestWriter.WritePolicies</c> emits a policy's <c>kind</c> and its
/// <c>stage</c> and none of its parameters, so there is no <c>attempts</c>, no <c>PT2S</c> and no
/// backoff in the document — the schema example in docs/13 §3 shows them and nothing writes them.
/// Recovering the numbers means reading the source, which is the one input docs/13 §5 forbids this
/// layer. It is also already answered where the numbers are: <c>DeadlineCoherenceAnalyzer</c>
/// computes that budget at compile time and fails the build, on every build, rather than when
/// somebody runs a review.
/// </para>
/// <para>
/// <strong>What is left is what a single compilation cannot see.</strong> Four of the five rules
/// below are cross-flow — an event's two ends are two flows, an agent tool's consequences are the
/// capabilities of every step it reaches — which is exactly the shape docs/13 §2 gives as the
/// reason a graph is worth having, and exactly the shape an analyzer scoped to one type is
/// worst at.
/// </para>
/// </remarks>
public sealed class ManifestReview
{
    private ManifestReview(string applicationName, IReadOnlyList<FlowFinding> findings)
    {
        ApplicationName = applicationName;
        Findings = findings;
    }

    /// <summary>The application the manifest describes.</summary>
    public string ApplicationName { get; }

    /// <summary>
    /// Everything the document proves about itself, ordered by severity then by subject.
    /// </summary>
    /// <remarks>
    /// Warnings first, because a report read top to bottom should lead with what is wrong; and
    /// ordinally within a severity, so two reviews of two builds of the same application produce
    /// diffable text.
    /// </remarks>
    public IReadOnlyList<FlowFinding> Findings { get; }

    /// <summary>Reviews one manifest.</summary>
    /// <param name="manifestJson">
    /// The contents of <c>flowx.manifest.json</c>. Any manifest, from any build — including one
    /// this process did not produce, which is what makes a reviewer that links no FlowX assembly
    /// worth having.
    /// </param>
    /// <exception cref="ArgumentException">The document is not JSON.</exception>
    public static ManifestReview Of(string manifestJson)
    {
        ArgumentNullException.ThrowIfNull(manifestJson);

        using var document = Parse(manifestJson);
        var root = document.RootElement;

        var capabilities = IndexCapabilities(root);
        var findings = new List<FlowFinding>();

        ReviewEvents(root, findings);
        ReviewCapabilities(capabilities, findings);

        foreach (var flow in Array(root, "flows"))
        {
            ReviewFlow(flow, capabilities, findings);
        }

        findings.Sort(static (left, right) =>
        {
            var bySeverity = right.Severity.CompareTo(left.Severity);

            return bySeverity != 0
                ? bySeverity
                : string.CompareOrdinal(left.Subject + left.Code, right.Subject + right.Code);
        });

        return new ManifestReview(NameOf(root), findings);
    }

    /// <summary>The report, as a human reads it.</summary>
    /// <remarks>
    /// The rendering <c>samples/ai-agent</c>'s README shows, markers and all. It is here rather
    /// than in a CLI verb because the flow that publishes this review as an agent tool needs the
    /// same text, and a second renderer would be a second thing that can disagree about what a
    /// finding said.
    /// </remarks>
    public string ToText()
    {
        if (Findings.Count == 0)
        {
            return $"{ApplicationName}: the manifest raises nothing.";
        }

        var text = new StringBuilder();

        text.Append(ApplicationName)
            .Append(": ")
            .Append(Findings.Count.ToString(CultureInfo.InvariantCulture))
            .Append(Findings.Count == 1 ? " finding." : " findings.");

        foreach (var finding in Findings)
        {
            text.Append("\n\n")
                .Append(finding.Severity == FindingSeverity.Warning ? "[warning] " : "[info]    ")
                .Append(finding.Subject)
                .Append(" — ")
                .Append(finding.Message)
                .Append(" (")
                .Append(finding.Code)
                .Append(')');
        }

        return text.ToString();
    }

    /// <summary>
    /// The material a model is handed when one is asked to narrate this review.
    /// </summary>
    /// <remarks>
    /// <strong>This method is where docs/13 §5's "AI runs on the manifest, not on data" is
    /// enforced rather than promised.</strong> It is a pure function of
    /// <see cref="Findings"/>, which are a pure function of the manifest — so the only way to put
    /// a customer record, a connection string or a line of source into a completion request is to
    /// change this method's signature. A review object that exposed the caller's own services, or
    /// took a free-text parameter, would move that guarantee back into an operating rule.
    /// </remarks>
    public string ToPrompt() =>
        "Below is a static review of a FlowX application, produced by reading its build " +
        "manifest. It contains no source code, no telemetry and no business data.\n\n" +
        ToText();

    // ------------------------------------------------------------------ rules

    /// <summary>
    /// An event with a producer and no consumer, or a consumer and no producer.
    /// </summary>
    /// <remarks>
    /// The cheapest cross-flow finding in the document and the one no analyzer can make: both
    /// ends of an event are flows, usually in different files and sometimes in different
    /// assemblies, and the manifest is the first artifact where they are in one place. An
    /// <c>Information</c> rather than a warning in both directions — a published event nobody
    /// reads yet is an ordinary state of a system that other teams subscribe to, and a consumer
    /// with no producer here is the ordinary state of one that subscribes to somebody else's.
    /// </remarks>
    private static void ReviewEvents(JsonElement root, List<FlowFinding> findings)
    {
        foreach (var evt in Array(root, "events"))
        {
            if (String(evt, "type") is not { Length: > 0 } type)
            {
                continue;
            }

            var produced = Strings(evt, "producedBy");
            var consumed = Strings(evt, "consumedBy");

            if (produced.Count > 0 && consumed.Count == 0)
            {
                findings.Add(new FlowFinding(
                    FindingSeverity.Information,
                    FlowFinding.OrphanEvent,
                    type,
                    $"is emitted by {Join(produced)} and no flow in this application observes it. " +
                    "If nothing outside the application subscribes either, the emit is a write " +
                    "nobody reads — and the outbox row, its publication and its retention are " +
                    "paid for on every instance."));
            }
            else if (consumed.Count > 0 && produced.Count == 0)
            {
                findings.Add(new FlowFinding(
                    FindingSeverity.Information,
                    FlowFinding.OrphanEvent,
                    type,
                    $"starts {Join(consumed)} and no flow in this application emits it. That is " +
                    "correct for an event another service publishes, and is a subscription that " +
                    "will never fire otherwise."));
            }
        }
    }

    /// <summary>
    /// A capability with declared side effects and no authorisation stance at all.
    /// </summary>
    /// <remarks>
    /// docs/13 §2 gives this exact query as its security-review example —
    /// <c>flowx query "capabilities with Authorization=Public and side effects"</c> — and it is
    /// the review's only <em>unconditional</em> warning, because there is no reading of the
    /// declaration under which it is safe: the capability changes something outside the process
    /// and the step loop asks nothing of whoever caused it.
    /// </remarks>
    private static void ReviewCapabilities(
        Dictionary<string, JsonElement> capabilities, List<FlowFinding> findings)
    {
        foreach (var capability in capabilities.Values.OrderBy(Key, StringComparer.Ordinal))
        {
            var effects = Strings(capability, "sideEffects");

            if (effects.Count == 0 || Mode(capability) is not "Public")
            {
                continue;
            }

            findings.Add(new FlowFinding(
                FindingSeverity.Warning,
                FlowFinding.PublicSideEffect,
                Key(capability),
                $"declares Authorization.Public and the side effects {Join(effects)}. Every " +
                "trigger that reaches it — HTTP, broker, schedule, change feed, agent — runs it " +
                "for an unauthenticated caller, because the stance is what the step loop decides " +
                "and Public decides yes."));
        }
    }

    /// <summary>Everything one flow's own entry proves.</summary>
    private static void ReviewFlow(
        JsonElement flow, Dictionary<string, JsonElement> capabilities, List<FlowFinding> findings)
    {
        if (String(flow, "id") is not { Length: > 0 } flowId)
        {
            return;
        }

        ReviewSaga(flow, flowId, capabilities, findings);
        ReviewAgentTool(flow, flowId, capabilities, findings);
    }

    /// <summary>
    /// A flow that compensates some effectful steps and not others.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Conditioned on the flow compensating <em>something</em>, and that condition is the whole
    /// rule. A flow with no compensation anywhere has not attempted a saga and reporting on it
    /// would fire on nearly every flow ever written; a flow that compensates two of its three
    /// effectful steps has attempted one and left an effect standing when the third fails, which
    /// is a decision somebody made and may not have noticed making.
    /// </para>
    /// <para>
    /// The unwind runs in reverse, so what is exposed is every effectful step <em>before</em> the
    /// failure — including the uncompensated one, whose effect nothing reverses. The message names
    /// the steps rather than counting them, because the reader's next move is to look at one.
    /// </para>
    /// </remarks>
    private static void ReviewSaga(
        JsonElement flow,
        string flowId,
        Dictionary<string, JsonElement> capabilities,
        List<FlowFinding> findings)
    {
        var compensated = 0;
        var exposed = new List<string>();

        foreach (var step in Steps(flow))
        {
            if (String(step, "capability") is not { Length: > 0 } key ||
                !capabilities.TryGetValue(key, out var capability) ||
                Strings(capability, "sideEffects").Count == 0)
            {
                continue;
            }

            if (String(step, "compensation") is { Length: > 0 })
            {
                compensated++;
            }
            else
            {
                exposed.Add(key);
            }
        }

        if (compensated == 0 || exposed.Count == 0)
        {
            return;
        }

        findings.Add(new FlowFinding(
            FindingSeverity.Warning,
            FlowFinding.PartialSaga,
            flowId,
            $"compensates {compensated.ToString(CultureInfo.InvariantCulture)} of its effectful " +
            $"steps and leaves {Join(exposed)} uncompensated. A failure after that step unwinds " +
            "the compensated ones and leaves its effect standing, so the flow's own recovery is " +
            "the thing that produces the inconsistent state."));
    }

    /// <summary>What an agent is offered, and what the offer costs.</summary>
    /// <remarks>
    /// Both rules read the flow's <c>Agent</c> trigger against the capabilities its steps reach,
    /// which is the join no single declaration carries: the trigger says how much consent is
    /// wanted and the capabilities say what there is to consent to, and they are written in
    /// different files by different people.
    /// </remarks>
    private static void ReviewAgentTool(
        JsonElement flow,
        string flowId,
        Dictionary<string, JsonElement> capabilities,
        List<FlowFinding> findings)
    {
        if (AgentTrigger(flow) is not { } trigger)
        {
            return;
        }

        var effects = Steps(flow)
            .Select(step => String(step, "capability"))
            .Where(static key => key is { Length: > 0 })
            .Where(key => capabilities.ContainsKey(key!))
            .SelectMany(key => Strings(capabilities[key!], "sideEffects"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static effect => effect, StringComparer.Ordinal)
            .ToList();

        if (effects.Count > 0 && String(trigger, "confirmation") is "Never")
        {
            findings.Add(new FlowFinding(
                FindingSeverity.Warning,
                FlowFinding.UnconfirmedAgentTool,
                flowId,
                $"is published as an agent tool with ConfirmationMode.Never and reaches " +
                $"{Join(effects)}. Its descriptor therefore publishes confirmationRequired: " +
                "false, so no client will prompt and a server enforcing confirmation has nothing " +
                "to enforce. FLOWX1046 reports the same declaration in the repository that owns " +
                "the flow; this reports it in a manifest from any build."));
        }

        var sensitive = Strings(Object(flow, "input") ?? default, "sensitive");

        if (sensitive.Count > 0)
        {
            findings.Add(new FlowFinding(
                FindingSeverity.Information,
                FlowFinding.AgentToolTakesSecret,
                flowId,
                $"is published as an agent tool whose input contract marks {Join(sensitive)} " +
                "[Sensitive]. The value is redacted from journals, logs, problem bodies and the " +
                "confirmation prompt — and a model still has to have it in order to pass it, so " +
                "it is in the client's transcript and in whatever that transcript is kept in."));
        }
    }

    // ------------------------------------------------------------------ DOM readers
    //
    // McpToolCatalog's readers and its reasoning: every one answers "what does the document
    // say", never "what should it have said", so a manifest from a newer compiler carrying a
    // field this build does not know is reviewed for what it does carry.

    private static JsonDocument Parse(string manifestJson)
    {
        try
        {
            return JsonDocument.Parse(manifestJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "The document handed to the reviewer is not JSON: " + exception.Message,
                nameof(manifestJson),
                exception);
        }
    }

    private static Dictionary<string, JsonElement> IndexCapabilities(JsonElement root)
    {
        var index = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var capability in Array(root, "capabilities"))
        {
            if (Key(capability) is { Length: > 0 } key)
            {
                index[key] = capability;
            }
        }

        return index;
    }

    /// <summary>A capability's identity as a step names it: <c>id@version</c>.</summary>
    private static string Key(JsonElement capability) =>
        String(capability, "id") is { Length: > 0 } id && String(capability, "version") is { Length: > 0 } version
            ? id + "@" + version
            : string.Empty;

    private static string? Mode(JsonElement capability) =>
        Object(capability, "authorization") is { } authorization ? String(authorization, "mode") : null;

    /// <summary>The flow's <c>Agent</c> trigger, or <c>null</c> when it declares none.</summary>
    private static JsonElement? AgentTrigger(JsonElement flow)
    {
        foreach (var trigger in Array(flow, "triggers"))
        {
            if (string.Equals(String(trigger, "kind"), "Agent", StringComparison.Ordinal))
            {
                return trigger;
            }
        }

        return null;
    }

    /// <summary>
    /// Every step the flow declares, including those inside a branch.
    /// </summary>
    /// <remarks>
    /// <c>McpToolCatalog.CapabilitiesOf</c>'s walk and its two decisions: a branch's steps are
    /// this flow's steps, and a <c>SubFlow</c> is not followed because the child's own entry
    /// carries its steps and the manifest publishes no aggregate.
    /// </remarks>
    private static List<JsonElement> Steps(JsonElement flow)
    {
        var found = new List<JsonElement>();

        Walk(Array(flow, "steps"));

        return found;

        void Walk(IEnumerable<JsonElement> steps)
        {
            foreach (var step in steps)
            {
                found.Add(step);

                foreach (var branch in Array(step, "branches"))
                {
                    Walk(Elements(branch));
                }
            }
        }
    }

    private static string NameOf(JsonElement root) =>
        Object(root, "application") is { } application
            ? String(application, "name") ?? "the application"
            : "the application";

    private static string Join(IReadOnlyList<string> values) => string.Join(", ", values);

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonElement? Object(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static IEnumerable<JsonElement> Array(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value))
        {
            yield break;
        }

        foreach (var item in Elements(value))
        {
            yield return item;
        }
    }

    private static IEnumerable<JsonElement> Elements(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in array.EnumerateArray())
        {
            yield return item;
        }
    }

    private static List<string> Strings(JsonElement element, string name) =>
        Array(element, name)
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()!)
            .ToList();
}

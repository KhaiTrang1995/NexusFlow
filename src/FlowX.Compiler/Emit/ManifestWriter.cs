using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FlowX.Compiler.Model;

namespace FlowX.Compiler.Emit;

/// <summary>
/// Writes <c>flowx.manifest.json</c> — the machine-readable description of the whole
/// application, and the artifact everything else is derived from
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0005-manifest-as-build-artifact.md">ADR-0005</a>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deterministic by construction.</strong> Two builds of identical source must
/// produce byte-identical output, or <c>flowx diff</c> reports changes nobody made and
/// people stop reading it. That rules out a build timestamp and a commit hash — both
/// belong in the manifest, but injected at publish time by the CLI, not baked in by
/// the generator. Everything else is sorted ordinally rather than left in discovery
/// order, because Roslyn does not promise a stable order across builds.
/// </para>
/// <para>
/// <strong>Structure only, never values.</strong> The manifest says a capability
/// accepts a <c>CaptureRequest</c>; it never says what was in one. That rule is what
/// makes the file safe to publish to a marketplace, feed to an agent, or attach to a
/// build, and <c>ManifestContainsNoSecrets</c> asserts it.
/// </para>
/// <para>
/// Hand-written JSON rather than a serialiser: this assembly is a Roslyn analyzer, and
/// an analyzer that drags <c>System.Text.Json</c> along has to ship it too, which is a
/// well-known way to break other people's builds. The shape is small and fixed.
/// </para>
/// </remarks>
public static class ManifestWriter
{
    /// <summary>The schema version this writer emits. Bumped when the shape changes.</summary>
    public const string SchemaVersion = "0.1.0";

    /// <summary>Writes the manifest for a whole application.</summary>
    /// <param name="applicationName">Usually the root assembly name.</param>
    /// <param name="applicationVersion">SemVer of the application.</param>
    /// <param name="flows">Every flow in the compilation.</param>
    /// <param name="projectDirectory">
    /// Absolute path of the project being compiled. Source pointers are written relative
    /// to it, so the document is identical on every machine that builds the same source.
    /// </param>
    public static string Write(
        string applicationName,
        string applicationVersion,
        IReadOnlyList<FlowModel> flows,
        string? projectDirectory = null)
    {
        if (flows is null)
        {
            throw new System.ArgumentNullException(nameof(flows));
        }

        var ordered = flows.OrderBy(f => f.FlowId, System.StringComparer.Ordinal).ToList();
        var writer = new JsonWriter();

        writer.OpenObject();
        writer.Property("schemaVersion", SchemaVersion);

        writer.PropertyName("application");
        writer.OpenObject();
        writer.Property("name", applicationName);
        writer.Property("version", applicationVersion);
        writer.CloseObject();

        writer.PropertyName("flows");
        writer.OpenArray();
        foreach (var flow in ordered)
        {
            WriteFlow(writer, flow, projectDirectory);
        }

        writer.CloseArray();

        writer.PropertyName("capabilities");
        writer.OpenArray();
        foreach (var capability in CollectCapabilities(ordered))
        {
            WriteCapability(writer, capability);
        }

        writer.CloseArray();

        writer.PropertyName("events");
        writer.OpenArray();
        foreach (var evt in CollectEvents(ordered))
        {
            writer.OpenObject();
            writer.Property("type", evt);
            writer.Property("schemaVersion", "1.0.0");
            writer.CloseObject();
        }

        writer.CloseArray();
        writer.CloseObject();

        return writer.ToString();
    }

    /// <summary>
    /// Turns an absolute <c>file:line</c> into one relative to the project directory.
    /// </summary>
    /// <remarks>
    /// The manifest is a published artifact that <c>flowx diff</c> compares across builds
    /// and machines, and the header on the generated holder promises it is byte-identical
    /// for identical source. An absolute path breaks that promise on the second machine,
    /// and ships the build agent's directory layout to anyone who reads the manifest.
    /// Falls back to the original when no project directory is known, because a slightly
    /// wrong pointer beats no pointer at all.
    /// </remarks>
    private static string Relativise(string location, string? projectDirectory)
    {
        if (string.IsNullOrEmpty(projectDirectory))
        {
            return location;
        }

        var prefix = projectDirectory!.Replace('\\', '/');

        if (!prefix.EndsWith("/", System.StringComparison.Ordinal))
        {
            prefix += "/";
        }

        var normalised = location.Replace('\\', '/');

        return normalised.StartsWith(prefix, System.StringComparison.Ordinal)
            ? normalised.Substring(prefix.Length)
            : normalised;
    }

    private static void WriteFlow(JsonWriter writer, FlowModel flow, string? projectDirectory)
    {
        writer.OpenObject();
        writer.Property("id", flow.FlowId);
        writer.Property("version", flow.Version);
        writer.Property("profile", flow.Profile);

        if (flow.Deadline != null)
        {
            writer.Property("deadline", flow.Deadline);
        }

        writer.PropertyName("input");
        writer.OpenObject();
        writer.Property("type", flow.InputTypeName);
        WriteSensitive(writer, flow.SensitiveInputMembers);
        writer.CloseObject();

        writer.PropertyName("output");
        writer.OpenObject();
        writer.Property("type", flow.OutputTypeName);
        WriteSensitive(writer, flow.SensitiveOutputMembers);
        writer.CloseObject();

        writer.PropertyName("steps");
        writer.OpenArray();
        foreach (var step in flow.Steps)
        {
            WriteStep(writer, step);
        }

        writer.CloseArray();

        writer.PropertyName("emits");
        writer.OpenArray();
        foreach (var evt in flow.AllSteps
            .Where(s => s.Kind == StepKindModel.Emit && s.EventType != null)
            .Select(s => s.EventType!)
            .Distinct(System.StringComparer.Ordinal)
            .OrderBy(e => e, System.StringComparer.Ordinal))
        {
            writer.Value(evt);
        }

        writer.CloseArray();

        if (flow.DeclarationLocation != null)
        {
            writer.Property("source", Relativise(flow.DeclarationLocation, projectDirectory));
        }

        writer.CloseObject();
    }

    /// <summary>Writes the contract's sensitive members, or nothing when it has none.</summary>
    /// <remarks>
    /// Omitted rather than emitted empty so the manifest of a contract with no secrets is
    /// unchanged by this field existing — an empty array in every flow would be noise in
    /// every <c>flowx diff</c>.
    /// </remarks>
    private static void WriteSensitive(JsonWriter writer, IReadOnlyList<string> members)
    {
        if (members.Count == 0)
        {
            return;
        }

        writer.PropertyName("sensitive");
        writer.OpenArray();

        foreach (var member in members)
        {
            writer.Value(member);
        }

        writer.CloseArray();
    }

    private static void WriteStep(JsonWriter writer, StepModel step)
    {
        writer.OpenObject();
        writer.Property("id", step.Index);
        writer.Property("kind", StepKindName(step.Kind));

        if (step.CapabilityId != null)
        {
            writer.Property("capability", step.CapabilityId + "@" + step.CapabilityVersion);
        }

        if (step.CompensationId != null)
        {
            writer.Property("compensation", step.CompensationId + "@" + step.CompensationVersion);
        }

        if (step.EventType != null)
        {
            writer.Property("event", step.EventType);
        }

        WritePolicies(writer, step);
        WriteBranches(writer, step);

        writer.CloseObject();
    }

    /// <summary>
    /// Writes a conditional's blocks as the schema's <c>branches</c>: an array of arrays
    /// of steps, <c>then</c> first and <c>Otherwise</c> second.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Nested, while the compiled plan is flat.</strong> The manifest publishes
    /// the shape the author declared, not the layout the engine executes — a reader
    /// comparing the manifest against a <c>Define</c> method should recognise it. So the
    /// branch-and-jump form stays inside the generated C#, and the jump in particular has
    /// no manifest entry at all: it exists only because the plan is one array, and
    /// publishing it would invite a consumer to draw an edge that is not part of the
    /// design.
    /// </para>
    /// <para>
    /// Step ids stay the compiled flat indices, so a manifest step and a plan step still
    /// name the same thing. The consequence is that ids are not contiguous across a
    /// conditional — the jump's index is missing — and a consumer that assumed
    /// contiguity would be wrong. Renumbering to close the gap would be worse: the ids
    /// would stop matching the indices in traces, in <c>flowx replay</c> and in the
    /// generated dispatcher.
    /// </para>
    /// <para>
    /// <strong>The predicate is not published.</strong> The schema's step object is
    /// <c>additionalProperties: false</c> and has no field for a condition, so there is
    /// nowhere to put it without changing the committed contract. A reader therefore sees
    /// that a flow branches and where each branch goes, but not on what — recorded as a
    /// gap rather than papered over by widening the schema unilaterally.
    /// </para>
    /// </remarks>
    private static void WriteBranches(JsonWriter writer, StepModel step)
    {
        if (step.Kind != StepKindModel.Condition)
        {
            return;
        }

        writer.PropertyName("branches");
        writer.OpenArray();

        WriteBranch(writer, step.Then);

        if (step.Otherwise.Count > 0)
        {
            WriteBranch(writer, step.Otherwise);
        }

        writer.CloseArray();
    }

    private static void WriteBranch(JsonWriter writer, IReadOnlyList<StepModel> block)
    {
        writer.OpenArray();

        foreach (var step in block)
        {
            WriteStep(writer, step);
        }

        writer.CloseArray();
    }

    /// <summary>
    /// The stage each policy kind runs in, fixed by ADR-0011.
    /// </summary>
    /// <remarks>
    /// Duplicated from <c>PolicySet</c> rather than referenced, because this assembly
    /// targets netstandard2.0 and cannot link against <c>FlowX.Abstractions</c> — the
    /// same constraint that makes the model layer its own thing. The duplication is
    /// pinned by <c>PolicyStagesMatchTheAbstraction</c>, which reads the real mapping by
    /// reflection and fails if these two ever disagree. An unpinned copy of a safety
    /// ordering is exactly the kind of duplication that drifts silently.
    /// </remarks>
    private static readonly System.Collections.Generic.Dictionary<string, string> PolicyStages =
        new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["RateLimit"] = "Admission",
            ["Idempotency"] = "Integrity",
            ["Timeout"] = "Resilience",
            ["Retry"] = "Resilience",
            ["CircuitBreaker"] = "Resilience",
            ["Bulkhead"] = "Resilience",
            ["Cache"] = "Efficiency",
            ["Audit"] = "Consistency",
        };

    /// <summary>Every policy kind this writer knows a stage for.</summary>
    /// <remarks>Exposed so the fitness test can compare it with the real abstraction.</remarks>
    public static IReadOnlyDictionary<string, string> KnownPolicyStages => PolicyStages;

    /// <summary>
    /// Writes the step's policies, or nothing when it declares none.
    /// </summary>
    /// <remarks>
    /// A policy whose stage this writer does not recognise is skipped rather than
    /// guessed at: the stage is the safety property, and a wrong one in the manifest
    /// would misrepresent the order things run in.
    /// </remarks>
    private static void WritePolicies(JsonWriter writer, StepModel step)
    {
        var known = step.PolicyKinds.Where(PolicyStages.ContainsKey).ToList();

        if (known.Count == 0)
        {
            return;
        }

        writer.PropertyName("policies");
        writer.OpenArray();

        foreach (var kind in known)
        {
            writer.OpenObject();
            writer.Property("kind", kind);
            writer.Property("stage", PolicyStages[kind]);
            writer.CloseObject();
        }

        writer.CloseArray();
    }

    private static void WriteCapability(JsonWriter writer, StepModel step)
    {
        writer.OpenObject();
        writer.Property("id", step.CapabilityId!);
        writer.Property("version", step.CapabilityVersion!);
        writer.Property("input", step.CapabilityInput ?? "object");
        writer.Property("output", step.CapabilityOutput ?? "object");

        writer.PropertyName("authorization");
        writer.OpenObject();
        writer.Property("mode", step.AuthorizationMode ?? "Public");
        writer.CloseObject();

        writer.Property("idempotent", step.IsIdempotent);

        writer.PropertyName("sideEffects");
        writer.OpenArray();
        foreach (var effect in step.SideEffects.OrderBy(e => e, System.StringComparer.Ordinal))
        {
            writer.Value(effect);
        }

        writer.CloseArray();
        writer.CloseObject();
    }

    /// <summary>
    /// Every distinct capability across every flow, deduplicated by <c>id@version</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deduplication is by identity and version, not by CLR type: two flows invoking
    /// the same capability must produce one manifest entry, and the same capability at
    /// two contract versions must produce two.
    /// </para>
    /// <para>
    /// <strong>Compensations count.</strong> They were previously listed only as a name on
    /// the step they undo, so their authorisation stance, side effects and idempotency
    /// never reached the manifest and <c>flowx diff</c> could not see a breaking change to
    /// one. A compensation is a capability that happens to run backwards; the manifest
    /// says so.
    /// </para>
    /// </remarks>
    private static IEnumerable<StepModel> CollectCapabilities(IEnumerable<FlowModel> flows)
    {
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        var capabilities = new List<StepModel>();

        foreach (var step in flows.SelectMany(f => f.AllSteps).SelectMany(Invoked))
        {
            if (step.CapabilityId != null && seen.Add(step.CapabilityId + "@" + step.CapabilityVersion))
            {
                capabilities.Add(step);
            }
        }

        return capabilities.OrderBy(
            c => c.CapabilityId + "@" + c.CapabilityVersion,
            System.StringComparer.Ordinal);
    }

    /// <summary>The capabilities one step invokes: itself, and its compensation if any.</summary>
    private static IEnumerable<StepModel> Invoked(StepModel step)
    {
        if (step.Kind == StepKindModel.Capability)
        {
            yield return step;
        }

        if (step.Compensation is not null)
        {
            yield return step.Compensation;
        }
    }

    private static IEnumerable<string> CollectEvents(IEnumerable<FlowModel> flows) => flows
        .SelectMany(f => f.AllSteps)
        .Where(s => s.Kind == StepKindModel.Emit && s.EventType != null)
        .Select(s => s.EventType!)
        .Distinct(System.StringComparer.Ordinal)
        .OrderBy(e => e, System.StringComparer.Ordinal);

    private static string StepKindName(StepKindModel kind) => kind switch
    {
        StepKindModel.Emit => "Emit",
        StepKindModel.AwaitSignal => "AwaitSignal",
        StepKindModel.Condition => "Condition",
        _ => "Capability",
    };

    /// <summary>Minimal, deterministic JSON emitter. Two-space indent, ordinal escaping.</summary>
    private sealed class JsonWriter
    {
        private readonly StringBuilder _builder = new StringBuilder();
        private int _indent;
        private bool _needsComma;

        public void OpenObject() => Open('{');

        public void CloseObject() => Close('}');

        public void OpenArray() => Open('[');

        public void CloseArray() => Close(']');

        public void PropertyName(string name)
        {
            Separate();
            Indent();
            _builder.Append(Quote(name)).Append(": ");
            _needsComma = false;
        }

        public void Property(string name, string value)
        {
            PropertyName(name);
            _builder.Append(Quote(value));
            _needsComma = true;
        }

        public void Property(string name, int value)
        {
            PropertyName(name);
            _builder.Append(value.ToString(CultureInfo.InvariantCulture));
            _needsComma = true;
        }

        public void Property(string name, bool value)
        {
            PropertyName(name);
            _builder.Append(value ? "true" : "false");
            _needsComma = true;
        }

        public void Value(string value)
        {
            Separate();
            Indent();
            _builder.Append(Quote(value));
            _needsComma = true;
        }

        public override string ToString() => _builder.Append('\n').ToString();

        private void Open(char brace)
        {
            // A property name has already written its own separator and indent.
            if (_needsComma || _builder.Length == 0 || _builder[_builder.Length - 1] != ' ')
            {
                Separate();
                Indent();
            }

            _builder.Append(brace);
            _indent++;
            _needsComma = false;
        }

        private void Close(char brace)
        {
            _indent--;

            if (_needsComma)
            {
                _builder.Append('\n');
                Indent();
            }

            _builder.Append(brace);
            _needsComma = true;
        }

        private void Separate()
        {
            if (_needsComma)
            {
                _builder.Append(',');
            }

            if (_builder.Length > 0)
            {
                _builder.Append('\n');
            }
        }

        private void Indent() => _builder.Append(' ', _indent * 2);

        private static string Quote(string value)
        {
            var builder = new StringBuilder("\"");

            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            return builder.Append('"').ToString();
        }
    }
}

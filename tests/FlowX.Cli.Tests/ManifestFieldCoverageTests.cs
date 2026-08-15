using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace FlowX.Cli.Tests;

/// <summary>
/// Every field the committed manifest schema declares is either classified by a
/// <c>FLOWX-DIFF-nnn</c> rule or listed in <c>docs/22-CLI.md</c> §2.2 as never reported —
/// and nothing is classified that the schema no longer declares.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is ADR-0017 F5's instrument, and it exists because the defect it is written
/// against was found by reading two files side by side.</strong> <c>FLOWX-DIFF-015</c> —
/// <c>Breaking</c> — compared <c>authorization.value</c> for months while
/// <c>ManifestWriter</c> wrote no such field, so half of a merge-blocking rule could not fire
/// on any manifest FlowX produced. <c>DiffCodeDocumentationTests</c> could not see it: that
/// one knows codes and documentation. <c>ManifestSchemaTests</c> could not either: that one
/// knows schema and instance. <strong>Nothing knew fields and rules.</strong>
/// </para>
/// <para>
/// <strong>Both directions, and the second is the one that catches a strike.</strong> A
/// field removed from the schema leaves its row here pointing at nothing, which is how a
/// rule that outlived the field it compares becomes a failing test instead of dead code —
/// the shape <c>FLOWX-DIFF-015</c> had. A field added to the schema with no row fails the
/// first direction, which is what ADR-0017's Revisit-when asks of every addition before the
/// freeze.
/// </para>
/// <para>
/// <strong>The map is written out rather than derived, because there is nothing to derive it
/// from.</strong> Which rule reads which field is a fact about <c>ManifestDiff</c>'s code and
/// about the reasoning in 22-CLI §3; no annotation carries it. A hand-written map is
/// therefore the claim, and the three assertions below are what stop the claim drifting from
/// the schema, from the emitter and from the document at once.
/// </para>
/// </remarks>
public sealed class ManifestFieldCoverageTests
{
    /// <summary>
    /// One schema field and how <c>flowx diff</c> answers for it.
    /// </summary>
    /// <param name="Field">
    /// Dotted path, named the way ADR-0017 §1 names fields: relative to the <c>$def</c> that
    /// declares it (<c>capability.authorization.value</c>), or bare at the document root.
    /// </param>
    /// <param name="Codes">The rules that classify a change to it. Empty when it is never reported.</param>
    /// <param name="NeverReportedAs">
    /// The exact phrase 22-CLI §2.2 uses for it, asserted to be there. Null when a rule
    /// classifies it.
    /// </param>
    private sealed record Classification(string Field, string[] Codes, string? NeverReportedAs = null);

    private static Classification Rule(string field, params string[] codes) => new(field, codes);

    private static Classification Silent(string field, string phrase) => new(field, [], phrase);

    /// <summary>
    /// The whole contract, field by field.
    /// </summary>
    /// <remarks>
    /// Grouped as the schema groups it. Where several fields share one rule the rule is
    /// repeated rather than factored out, because the question this map answers is asked one
    /// field at a time and a reader should not have to resolve a grouping to get the answer.
    /// </remarks>
    private static readonly Classification[] Map =
    [
        // -------------------------------------------------------------- document
        Rule("schemaVersion", "FLOWX-DIFF-200"),
        Silent("extensions", "`extensions`"),

        Rule("application.name", "FLOWX-DIFF-201"),
        Silent("application.version", "`application.version`"),

        // ------------------------------------------------------------------ flow
        // Flows are keyed by id and major, so both halves of the key report an addition
        // or a removal rather than a change of their own.
        Rule("flow.id", "FLOWX-DIFF-001", "FLOWX-DIFF-100"),
        Rule("flow.version", "FLOWX-DIFF-001", "FLOWX-DIFF-100"),
        Rule("flow.profile", "FLOWX-DIFF-004", "FLOWX-DIFF-202"),
        Rule("flow.deadline", "FLOWX-DIFF-203"),
        Silent("flow.emits", "a flow's `emits` and `errors`"),
        Silent("flow.errors", "a flow's `emits` and `errors`"),
        Silent("flow.source", "`source` (file:line)"),

        Rule("typeRef.type", "FLOWX-DIFF-002", "FLOWX-DIFF-003"),
        Rule("typeRef.sensitive", "FLOWX-DIFF-006", "FLOWX-DIFF-107"),

        // --------------------------------------------------------------- trigger
        // Six fields make the address `Describe` keys on, so a change to any of them is a
        // trigger removed at the old address and added at the new one.
        Rule("trigger.kind", "FLOWX-DIFF-005", "FLOWX-DIFF-103"),
        Rule("trigger.method", "FLOWX-DIFF-005", "FLOWX-DIFF-103"),
        Rule("trigger.route", "FLOWX-DIFF-005", "FLOWX-DIFF-103"),
        Rule("trigger.transport", "FLOWX-DIFF-005", "FLOWX-DIFF-103"),
        Rule("trigger.topic", "FLOWX-DIFF-005", "FLOWX-DIFF-103"),
        Rule("trigger.cron", "FLOWX-DIFF-005", "FLOWX-DIFF-103"),
        Rule("trigger.idempotent", "FLOWX-DIFF-007", "FLOWX-DIFF-008"),
        Rule("trigger.confirmation", "FLOWX-DIFF-009", "FLOWX-DIFF-108"),
        Rule("trigger.timeZone", "FLOWX-DIFF-205"),
        Silent("trigger.group", "`group` and `description`"),
        Silent("trigger.description", "`group` and `description`"),

        // ------------------------------------------------------------------ step
        // A step describes what the flow *does*, and refactoring that is what FlowX exists
        // to make safe. The wait is the exception, because it is what the flow requires
        // from outside.
        Rule("step.signal", "FLOWX-DIFF-021", "FLOWX-DIFF-022"),
        Rule("step.timeout", "FLOWX-DIFF-206"),
        Silent("step.id", "a flow's `steps`"),
        Silent("step.kind", "a flow's `steps`"),
        Silent("step.capability", "a flow's `steps`"),
        Silent("step.compensation", "a flow's `steps`"),
        Silent("step.fallback", "a flow's `steps`"),
        Silent("step.event", "a flow's `steps`"),
        Silent("step.flow", "a flow's `steps`"),
        Silent("step.mode", "a flow's `steps`"),
        Silent("step.merge", "a flow's `steps`"),
        Silent("policy.kind", "a flow's `steps`"),
        Silent("policy.stage", "a flow's `steps`"),

        // ------------------------------------------------------------ capability
        Rule("capability.id", "FLOWX-DIFF-010", "FLOWX-DIFF-101"),
        Rule("capability.version", "FLOWX-DIFF-010", "FLOWX-DIFF-101"),
        Rule("capability.input", "FLOWX-DIFF-011"),
        Rule("capability.output", "FLOWX-DIFF-012"),
        Rule("capability.idempotent", "FLOWX-DIFF-013", "FLOWX-DIFF-105"),
        Rule("capability.sideEffects", "FLOWX-DIFF-016", "FLOWX-DIFF-106"),
        Rule("capability.deprecated", "FLOWX-DIFF-204"),
        Rule("capability.authorization.mode", "FLOWX-DIFF-014", "FLOWX-DIFF-015"),
        Rule("capability.authorization.value", "FLOWX-DIFF-015"),
        Rule("capability.errors.code", "FLOWX-DIFF-017", "FLOWX-DIFF-104", "FLOWX-DIFF-019"),
        Rule("capability.errors.category", "FLOWX-DIFF-018"),
        Silent("capability.authorization.approvedBy", "`capability.authorization.approvedBy`"),
        Silent("capability.source", "`source` (file:line)"),

        // ----------------------------------------------------------------- event
        Rule("event.type", "FLOWX-DIFF-020", "FLOWX-DIFF-102"),
        Rule("event.schemaVersion", "FLOWX-DIFF-020", "FLOWX-DIFF-102"),
        Silent("event.producedBy", "`producedBy` and `consumedBy`"),
        Silent("event.consumedBy", "`producedBy` and `consumedBy`"),
    ];

    [Fact]
    public void EveryFieldTheSchemaDeclaresIsClassified()
    {
        var unclassified = SchemaFields()
            .Except(Map.Select(m => m.Field), StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal);

        unclassified.ShouldBeEmpty(
            "A field the schema declares that no `flowx diff` rule compares and no row in " +
            "docs/22-CLI.md §2.2 excuses is a term of a public contract with no enforcement " +
            "behind it (ADR-0017 F5). Freezing it at v1.0 makes that permanent. Give it a " +
            "rule, or a row with a reason, before the bump.");
    }

    [Fact]
    public void EveryClassifiedFieldIsStillInTheSchema()
    {
        var orphaned = Map.Select(m => m.Field)
            .Except(SchemaFields(), StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal);

        orphaned.ShouldBeEmpty(
            "This map classifies a field the committed schema no longer declares. That is " +
            "the FLOWX-DIFF-015 shape — a rule outliving the field it compares — and the " +
            "direction that catches it. Remove the row, and the rule with it if the rule " +
            "read nothing else.");
    }

    [Fact]
    public void EveryRuleThisMapNamesIsOneTheCliEmits()
    {
        var emitted = EmittedCodes();

        var phantom = Map.SelectMany(m => m.Codes)
            .Distinct(StringComparer.Ordinal)
            .Where(code => !emitted.Contains(code))
            .OrderBy(code => code, StringComparer.Ordinal);

        phantom.ShouldBeEmpty(
            "This map says a field is classified by a code src/FlowX.Cli never emits, so the " +
            "field is in fact unclassified and the map is hiding it.");
    }

    [Fact]
    public void EveryFieldThisMapCallsSilentIsListedInTheNeverReportedTable()
    {
        var section = NeverReportedSection();

        var missing = Map
            .Where(m => m.NeverReportedAs is not null)
            .Select(m => m.NeverReportedAs!)
            .Distinct(StringComparer.Ordinal)
            .Where(phrase => !section.Contains(phrase, StringComparison.Ordinal))
            .OrderBy(phrase => phrase, StringComparer.Ordinal);

        missing.ShouldBeEmpty(
            "F5 allows a field to go unreported only when docs/22-CLI.md §2.2 says so *with " +
            "its reason*. This map claims a row that is not in that table, so the exemption " +
            "is asserted in a test and published nowhere.");
    }

    /// <summary>
    /// Every leaf field the committed schema declares, named relative to its <c>$def</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from <c>schemas/flowx.manifest.schema.json</c> rather than from a list, for
    /// <c>DiffCodeDocumentationTests</c>'s reason: a list would be a second place the set is
    /// written down and the test would assert that two copies agree while the contract moved.
    /// </para>
    /// <para>
    /// A property that resolves to a <c>$def</c> with properties of its own is a container
    /// and produces no row — the def is walked once, on its own name, so
    /// <c>typeRef.sensitive</c> is one field rather than one per place a <c>typeRef</c> is
    /// used. An object declared inline is walked in place, which is why
    /// <c>capability.authorization.mode</c> keeps its prefix.
    /// </para>
    /// </remarks>
    private static HashSet<string> SchemaFields()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(FindDirectory("schemas"), "flowx.manifest.schema.json")));

        var root = schema.RootElement;
        var defs = root.GetProperty("$defs");
        var fields = new HashSet<string>(StringComparer.Ordinal);

        Walk(string.Empty, root);

        foreach (var def in defs.EnumerateObject())
        {
            Walk(def.Name, def.Value);
        }

        return fields;

        void Walk(string prefix, JsonElement node)
        {
            if (!node.TryGetProperty("properties", out var properties))
            {
                return;
            }

            foreach (var property in properties.EnumerateObject())
            {
                var path = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
                var target = Unwrap(property.Value, out var isNamedDef);

                if (!target.TryGetProperty("properties", out _))
                {
                    fields.Add(path);
                }
                else if (!isNamedDef)
                {
                    Walk(path, target);
                }
            }
        }

        // Follows `$ref` into `$defs` and steps through array `items`, reporting whether the
        // thing it landed on was a named def — the caller skips those, because the def is
        // walked once under its own name.
        JsonElement Unwrap(JsonElement node, out bool isNamedDef)
        {
            isNamedDef = false;

            for (var guard = 0; guard < 8; guard++)
            {
                if (node.TryGetProperty("$ref", out var reference))
                {
                    var name = reference.GetString()!.Split('/')[^1];
                    node = defs.GetProperty(name);
                    isNamedDef = true;
                    continue;
                }

                if (node.TryGetProperty("items", out var items))
                {
                    node = items;
                    isNamedDef = false;
                    continue;
                }

                break;
            }

            return node;
        }
    }

    private static string NeverReportedSection()
    {
        var cli = File.ReadAllText(Path.Combine(FindDirectory("docs"), "22-CLI.md"));
        var start = cli.IndexOf("### 2.2 What is never reported", StringComparison.Ordinal);

        start.ShouldBeGreaterThanOrEqualTo(
            0, "docs/22-CLI.md no longer has a §2.2, so no field can be excused by one.");

        var end = cli.IndexOf("## 3. The classification rules", start, StringComparison.Ordinal);

        return end < 0 ? cli[start..] : cli[start..end];
    }

    private static HashSet<string> EmittedCodes() =>
        Directory
            .EnumerateFiles(FindDirectory("src/FlowX.Cli"), "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Replace('\\', '/').Split('/').Any(static s => s is "obj" or "bin"))
            .SelectMany(static path =>
                Regex.Matches(File.ReadAllText(path), @"FLOWX-DIFF-\d+").Select(static m => m.Value))
            .ToHashSet(StringComparer.Ordinal);

    private static string FindDirectory(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate '{relativePath}' from {AppContext.BaseDirectory}.");
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using FlowX.Compiler.Emit;
using FlowX.Compiler.Model;
using Json.Schema;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// Validates emitted manifests against <c>schemas/flowx.manifest.schema.json</c> —
/// the committed contract, not a restatement of it.
/// </summary>
/// <remarks>
/// <c>ManifestWriterTests</c> asserts specific fields, which is useful for saying *why*
/// something is required. This class asserts the whole document against the actual
/// schema, which is what catches the case those tests cannot: a field the schema
/// requires that nobody remembered to assert, or a shape that quietly stopped matching
/// when the schema changed.
/// </remarks>
public sealed class ManifestSchemaTests
{
    private static readonly JsonSchema Schema = LoadSchema();

    private static JsonSchema LoadSchema()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "schemas", "flowx.manifest.schema.json");

            if (File.Exists(candidate))
            {
                return JsonSchema.FromFile(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate schemas/flowx.manifest.schema.json.");
    }

    /// <summary>
    /// Evaluates one emitted manifest against the committed schema, failing with every
    /// error the evaluation produced.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so a sibling suite about one shape — a suspension
    /// point's fields, say — can assert against the *committed* contract instead of
    /// loading a second copy of it. A second loader is a second chance to validate
    /// against a schema nobody ships.
    /// </remarks>
    /// <param name="manifest">The emitted document.</param>
    internal static void Validate(string manifest) => ShouldValidate(manifest);

    private static void ShouldValidate(string manifest)
    {
        using var document = JsonDocument.Parse(manifest);

        var result = Schema.Evaluate(
            document.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        if (result.IsValid)
        {
            return;
        }

        var failures = string.Join(
            "\n",
            result.Details
                .Where(d => d.HasErrors)
                .SelectMany(d => d.Errors!.Select(e => $"  {d.InstanceLocation}: {e.Key} — {e.Value}")));

        throw new ShouldAssertException(
            $"The emitted manifest does not validate against the committed schema:\n{failures}\n\n{manifest}");
    }

    /// <summary>
    /// Fields the committed schema declares that no manifest in the corpus carries, with the
    /// reason each is exempt.
    /// </summary>
    /// <remarks>
    /// <strong>One entry, and the reason is the whole of ADR-0017 F6.</strong> Nothing in
    /// FlowX writes <c>extensions</c> and nothing should: the block exists so a downstream
    /// consumer can attach metadata FlowX has no opinion about, which is what ADR-0005 traded
    /// for accepting a contract it must support forever. A compiler-produced corpus is
    /// therefore the wrong place to look for it, and
    /// <c>ExtensionsEscapeHatchTests</c> in FlowX.Cli.Tests is the right one — it feeds the
    /// tool a manifest carrying one and asserts every verb tolerates it and no rule reports a
    /// change inside it.
    /// <para>
    /// This list is the pressure valve on the criterion, so it is kept to fields whose
    /// producer is deliberately outside this repository. "No fixture uses it yet" is not a
    /// reason — that is the state F1 exists to refuse, and the answer to it is a fixture.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> NotWrittenByTheCompiler = new(StringComparer.Ordinal)
    {
        ["extensions"] =
            "the consumer's block, by design (ADR-0005). Exercised by " +
            "FlowX.Cli.Tests.ExtensionsEscapeHatchTests, which is where a field nothing in " +
            "this repository produces can honestly be tested.",
    };

    /// <summary>
    /// Every field the committed schema declares is written by <c>ManifestWriter</c> into at
    /// least one manifest of a corpus, or is exempt with a reason recorded here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>ADR-0017 F1's instrument.</strong> The criterion is that each declared field
    /// "is either emitted by the compiler, or deleted from the committed schema before the
    /// bump", and the argument for checking it is that a consumer reading the schema cannot
    /// tell "this application has no owner recorded" from "the compiler never looked".
    /// Thirteen fields were in the second state when the record was written.
    /// </para>
    /// <para>
    /// <strong>Both directions, on <c>DiffCodeDocumentationTests</c>'s pattern.</strong>
    /// <see cref="EveryExemptionNamesAFieldTheSchemaStillDeclares"/> is the other half: an
    /// exemption for a field that has since left the schema is a reason nobody needs, and
    /// keeping it would let the list rot into a place where a real gap could hide.
    /// </para>
    /// <para>
    /// The corpus is every fixture in <c>Models</c> plus the emitted shapes the other tests
    /// in this class already build, written through the real writer. It is deliberately not
    /// the ecommerce baseline alone: one application exercises the fields it happens to use,
    /// and the fixture is where a field can be made to appear on purpose.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryFieldTheSchemaDeclaresIsWritten()
    {
        var written = Corpus()
            .SelectMany(manifest => PathsIn(JsonDocument.Parse(manifest).RootElement, string.Empty))
            .ToHashSet(StringComparer.Ordinal);

        var missing = DeclaredPaths()
            .Where(path => !written.Contains(path))
            .Where(path => !NotWrittenByTheCompiler.ContainsKey(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        missing.ShouldBeEmpty(
            "The committed schema declares a field no manifest in the corpus carries. A field " +
            "with no producer is indistinguishable, to a consumer, from a fact this " +
            "application does not have — and freezing it at v1.0 makes the ambiguity " +
            "permanent (ADR-0017 F1). Emit it, delete it from the schema, or exempt it in " +
            "NotWrittenByTheCompiler with a reason:" +
            Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void EveryExemptionNamesAFieldTheSchemaStillDeclares()
    {
        var declared = DeclaredPaths();

        var stale = NotWrittenByTheCompiler.Keys
            .Where(path => !declared.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal);

        stale.ShouldBeEmpty(
            "An exemption names a field the schema no longer declares. Delete it: a list of " +
            "reasons for fields that do not exist is where a real gap goes to hide.");
    }

    /// <summary>
    /// Manifests written by the real writer, covering every shape the fixtures can build.
    /// </summary>
    private static IEnumerable<string> Corpus()
    {
        yield return ManifestWriter.Write(
            "Sample.App",
            "1.0.0",
            [Models.PlaceOrder(), Models.Conditional(), Models.Switching(), Models.Parallel()],
            projectDirectory: null,
            [Models.Triggers()],
            Models.ErrorCatalogues());

        yield return ManifestWriter.Write(
            "Sample.App",
            "1.0.0",
            [Models.Iterating(), Models.Composing(), Models.Waiting(), Models.LinearQuery(), Models.Minimal()]);

        yield return ManifestWriter.Write(
            "Sample.App",
            "2.0.0",
            [Models.FullyDescribed()],
            projectDirectory: null,
            [Models.FullyDescribedTriggers()]);
    }

    /// <summary>
    /// Every leaf path an instance can carry, as a dotted path with array indices dropped.
    /// </summary>
    private static IEnumerable<string> PathsIn(JsonElement element, string prefix)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var path = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;

                    yield return path;

                    foreach (var nested in PathsIn(property.Value, path))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var nested in element.EnumerateArray().SelectMany(item => PathsIn(item, prefix)))
                {
                    yield return nested;
                }

                break;
        }
    }

    /// <summary>
    /// Every field the committed schema declares, as the dotted path an instance would use.
    /// </summary>
    /// <remarks>
    /// Read from the schema rather than listed, so a field added to the contract is in scope
    /// for this criterion the moment it is declared — which is what ADR-0017's Revisit-when
    /// asks of an addition, stated as a failing test rather than as a habit.
    /// </remarks>
    private static HashSet<string> DeclaredPaths()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(SchemaPath()));

        var defs = document.RootElement.GetProperty("$defs");
        var paths = new HashSet<string>(StringComparer.Ordinal);

        Walk(document.RootElement, string.Empty, []);

        return paths;

        void Walk(JsonElement node, string prefix, ImmutableHashSet<string> seen)
        {
            if (node.TryGetProperty("$ref", out var reference))
            {
                var name = reference.GetString()!.Split('/')[^1];

                // A step's `branches` hold steps, so the definition is cyclic. One visit per
                // path is enough: an instance nested deeper carries no field a shallower one
                // does not, and the paths this produces are index-free anyway.
                if (!seen.Contains(name))
                {
                    Walk(defs.GetProperty(name), prefix, seen.Add(name));
                }

                return;
            }

            if (node.TryGetProperty("items", out var items))
            {
                Walk(items, prefix, seen);
            }

            if (!node.TryGetProperty("properties", out var properties))
            {
                return;
            }

            foreach (var property in properties.EnumerateObject())
            {
                var path = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
                paths.Add(path);
                Walk(property.Value, path, seen);
            }
        }
    }

    private static string SchemaPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "schemas", "flowx.manifest.schema.json");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate schemas/flowx.manifest.schema.json.");
    }

    [Fact]
    public void TheCommittedSchemaItselfIsValid()
    {
        // Guards the guard. A malformed schema validates everything, and every test
        // below would pass while proving nothing.
        Schema.ShouldNotBeNull();
        Schema.GetDefs().ShouldNotBeNull();
        Schema.GetDefs()!.Count.ShouldBeGreaterThan(10);
    }

    [Fact]
    public void AFullApplicationManifestValidates()
        => ShouldValidate(ManifestWriter.Write("Sample.App", "1.0.0", [Models.PlaceOrder()]));

    [Fact]
    public void AMultiFlowManifestValidates()
        => ShouldValidate(ManifestWriter.Write("Sample.App", "1.0.0", [Models.PlaceOrder(), Models.Minimal()]));

    [Fact]
    public void AnEmptyApplicationManifestValidates()
        => ShouldValidate(ManifestWriter.Write("Empty.App", "1.0.0", []));

    /// <summary>
    /// A conditional reaches the manifest as the schema already describes it: a step of
    /// kind <c>Condition</c> whose <c>branches</c> are arrays of steps.
    /// </summary>
    /// <remarks>
    /// Worth asserting through the real schema rather than by field, because the step
    /// object is <c>additionalProperties: false</c> — anything the writer invented for
    /// this shape, a predicate string or a target index, would fail here rather than
    /// quietly shipping a document the published contract does not describe.
    /// </remarks>
    [Fact]
    public void AFlowWithAConditionalValidates()
        => ShouldValidate(ManifestWriter.Write("Sample.App", "1.0.0", [Models.Conditional()]));

    /// <summary>
    /// A fork reaches the manifest as the schema describes it: a step of kind
    /// <c>Parallel</c>, one <c>branches</c> entry per branch, and a <c>merge</c> naming the
    /// join rule.
    /// </summary>
    /// <remarks>
    /// <c>merge</c> is a field this work package added to the committed contract, so this
    /// is the test that says the addition is real rather than something the writer emits
    /// into a document nobody validates. The step object is
    /// <c>additionalProperties: false</c>, so before the schema change this document was
    /// invalid — which is the right way round.
    /// </remarks>
    [Fact]
    public void AFlowWithAParallelValidates()
        => ShouldValidate(ManifestWriter.Write("Sample.App", "1.0.0", [Models.Parallel()]));

    /// <summary>
    /// A fork that waits for a quorum validates, which it did not until the committed schema
    /// stopped declaring <c>merge</c> twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The step object carried two <c>merge</c> keys</strong> — one described, with
    /// <c>Quorum</c> in its enum, and one bare copy without it, thirty lines later. JSON
    /// duplicate keys resolve last-wins in every parser this repository uses, so the schema
    /// FlowX actually validated against was the copy: <c>FlowAnalyzer</c> maps
    /// <c>MergeStrategy.Quorum(n)</c> to the name <c>"Quorum"</c>,
    /// <c>ManifestWriter</c> writes it, and the document it produced did not validate against
    /// the contract the repository publishes.
    /// </para>
    /// <para>
    /// Nothing caught it. <c>AFlowWithAParallelValidates</c> uses <c>AllMustSucceed</c>, which
    /// both copies admit, so the only manifest ever validated with a fork in it took the arm
    /// where the two agreed. That is the shape of every defect
    /// <a href="../../../docs/adr/ADR-0017-manifest-v1-freeze-criteria.md">ADR-0017</a> is
    /// about: a contract clause with nothing exercising it.
    /// </para>
    /// </remarks>
    [Fact]
    public void AForkThatWaitsForAQuorumValidates()
    {
        var quorum = new FlowModel(
            "order.screen", "1.0.0", "Ephemeral", null, "Sample.Flows", "ScreenOrderFlow",
            "Sample.Contracts.PlaceOrder", "Sample.Contracts.OrderPlacedResult",
            [
                StepModel.Parallel(
                    0,
                    [
                        new ParallelBranchModel([Models.Validate(1)]),
                        new ParallelBranchModel([Models.Capture(2)]),
                    ],
                    "MergeStrategy.Quorum(2)",
                    "Quorum"),
            ]);

        ShouldValidate(ManifestWriter.Write("Sample.App", "1.0.0", [quorum]));
    }

    /// <summary>
    /// The duration pattern <c>StepModel</c> holds a folded wait to is the schema's own.
    /// </summary>
    /// <remarks>
    /// <c>FlowX.Compiler</c> targets netstandard2.0, loads into the compiler process and
    /// reads no files, so it cannot consult <c>schemas/flowx.manifest.schema.json</c> at
    /// build time and carries a copy of the pattern instead. This is the pin on that copy —
    /// the arrangement <c>PolicyStagesMatchTheAbstraction</c> already uses for the policy
    /// stage table, and for the same reason: an unpinned copy of a published constraint
    /// drifts silently.
    /// </remarks>
    [Fact]
    public void TheModelsDurationPatternIsTheSchemasOwn()
    {
        var declared = Schema.GetDefs()!["duration"].GetPatternValue();

        declared.ShouldNotBeNull();
        StepModel.Iso8601DurationPattern.ShouldBe(declared!);
    }

    /// <summary>
    /// A composition reaches the manifest as the schema describes it: a step of kind
    /// <c>SubFlow</c> naming the child by id and saying how it relates to the parent.
    /// </summary>
    /// <remarks>
    /// <c>flow</c> and <c>mode</c> are fields this work package added to the committed
    /// contract, so this is the test that says the addition is real rather than something
    /// the writer emits into a document nobody validates. The step object is
    /// <c>additionalProperties: false</c>, so before the schema change this document was
    /// invalid — which is the right way round.
    /// </remarks>
    [Theory]
    [InlineData("Inline")]
    [InlineData("Detached")]
    public void AFlowThatComposesAnotherValidates(string mode)
        => ShouldValidate(ManifestWriter.Write("Sample.App", "1.0.0", [Models.Composing(mode)]));

    [Fact]
    public void ADurableFlowWithASuspensionPointValidates()
    {
        var durable = new FlowModel(
            "order.fulfil", "2.0.0", "Durable", "P30D", "Sample.Flows", "FulfilOrderFlow",
            "Sample.Contracts.FulfilOrder", "Sample.Contracts.Fulfilment",
            [
                Models.Reserve(0),
                StepModel.AwaitSignal(1, "payment.confirmed"),
                Models.Capture(2),
                StepModel.Emit(3, "order.fulfilled"),
            ]);

        ShouldValidate(ManifestWriter.Write("Sample.App", "1.0.0", [durable]));
    }

    /// <summary>
    /// Every trigger kind and a full error catalogue, through the committed schema.
    /// </summary>
    /// <remarks>
    /// The schema's <c>trigger</c> object is <c>additionalProperties: false</c>, so a field
    /// the writer invented for a trigger fails here rather than shipping in a document the
    /// published contract does not describe. That is the same guard the step object gives,
    /// and it is why the constraint was added when triggers started being emitted: an open
    /// object cannot tell an emitter it has drifted.
    /// </remarks>
    [Fact]
    public void AManifestWithTriggersAndErrorCataloguesValidates()
        => ShouldValidate(ManifestWriter.Write(
            "Sample.App", "1.0.0", [Models.PlaceOrder()], null,
            [Models.Triggers()], Models.ErrorCatalogues()));

    [Fact]
    public void TheSchemaRejectsATriggerFieldItDoesNotDeclare()
    {
        const string Malformed = """
            {
              "schemaVersion": "0.1.0",
              "application": { "name": "A", "version": "1.0.0" },
              "flows": [ { "id": "a.b", "version": "1.0.0", "profile": "Ephemeral",
                           "input": { "type": "In" }, "output": { "type": "Out" },
                           "triggers": [ { "kind": "Http", "maxInFlight": 32 } ],
                           "steps": [ { "id": 0 } ] } ],
              "capabilities": []
            }
            """;

        using var document = JsonDocument.Parse(Malformed);

        Schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List })
            .IsValid.ShouldBeFalse(
                "Operational tuning is declared on the trigger attributes and deliberately not " +
                "published; the schema has to say so, or the omission is only a convention.");
    }

    [Fact]
    public void TheSchemaRejectsAnErrorCategoryOutsideTheClosedSet()
    {
        // ErrorCategory is closed on purpose: a new category silently changes the
        // transport mapping table for every existing consumer.
        const string Malformed = """
            {
              "schemaVersion": "0.1.0",
              "application": { "name": "A", "version": "1.0.0" },
              "flows": [ { "id": "a.b", "version": "1.0.0", "profile": "Ephemeral",
                           "input": { "type": "In" }, "output": { "type": "Out" },
                           "steps": [ { "id": 0 } ] } ],
              "capabilities": [ { "id": "a.b", "version": "1.0.0", "input": "In", "output": "Out",
                                  "authorization": { "mode": "Internal" }, "idempotent": true,
                                  "errors": [ { "code": "a.nope", "category": "Whoops" } ] } ]
            }
            """;

        using var document = JsonDocument.Parse(Malformed);

        Schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List })
            .IsValid.ShouldBeFalse();
    }

    [Fact]
    public void TheSchemaRejectsAManifestWithAMalformedIdentity()
    {
        // The negative control. A schema that accepts anything would make every test
        // above green and meaningless.
        const string Malformed = """
            {
              "schemaVersion": "0.1.0",
              "application": { "name": "A", "version": "1.0.0" },
              "flows": [ { "id": "NotAnIdentity", "version": "1.0.0", "profile": "Ephemeral",
                           "input": { "type": "In" }, "output": { "type": "Out" },
                           "steps": [ { "id": 0 } ] } ],
              "capabilities": []
            }
            """;

        using var document = JsonDocument.Parse(Malformed);

        Schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List })
            .IsValid.ShouldBeFalse("'NotAnIdentity' is not <domain>.<verb>, and the schema must say so.");
    }

    [Fact]
    public void TheSchemaRejectsAnUnknownExecutionProfile()
    {
        const string Malformed = """
            {
              "schemaVersion": "0.1.0",
              "application": { "name": "A", "version": "1.0.0" },
              "flows": [ { "id": "a.b", "version": "1.0.0", "profile": "Whenever",
                           "input": { "type": "In" }, "output": { "type": "Out" },
                           "steps": [ { "id": 0 } ] } ],
              "capabilities": []
            }
            """;

        using var document = JsonDocument.Parse(Malformed);

        Schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List })
            .IsValid.ShouldBeFalse();
    }
}

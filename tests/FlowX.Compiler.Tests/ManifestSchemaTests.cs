using System;
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

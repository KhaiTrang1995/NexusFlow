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

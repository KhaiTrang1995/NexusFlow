using System.Text.Json;
using System.Text.Json.Serialization;
using FlowX.Http;
using Shouldly;
using Xunit;

namespace FlowX.Http.Tests;

/// <summary>
/// The document is generated from the manifest, so it cannot drift from the application.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What makes this different from documenting the endpoints.</strong> Reflecting over what
/// was mapped describes the transport: a route, a verb, and a body of unknown shape. The manifest
/// carries what a caller actually needs and cannot discover by trying — which errors a flow can
/// produce, and whether the endpoint refuses a call that omits an idempotency key. Those two are
/// what these tests are mostly about.
/// </para>
/// <para>
/// <strong>The shapes come from the JSON source generator.</strong> The same metadata that keeps
/// the write path trim-safe describes the contract, so nothing here reflects over a property.
/// </para>
/// </remarks>
public sealed class OpenApiTests
{
    private const string Manifest = """
        {
          "schemaVersion": "0.1.0",
          "application": { "name": "Crm", "version": "2.1.0" },
          "flows": [
            {
              "id": "crm.lead.capture",
              "version": "1.0.0",
              "input": { "type": "FlowX.Http.Tests.CaptureLead" },
              "output": { "type": "FlowX.Http.Tests.LeadCaptured" },
              "triggers": [
                { "kind": "Http", "method": "POST", "route": "/api/v1/crm/leads", "idempotent": true }
              ],
              "errors": [ "crm.lead_duplicate", "crm.schema_out_of_date" ]
            },
            {
              "id": "crm.lead.read",
              "version": "1.0.0",
              "input": { "type": "FlowX.Http.Tests.CaptureLead" },
              "output": { "type": "FlowX.Http.Tests.LeadCaptured" },
              "triggers": [
                { "kind": "Http", "method": "POST", "route": "/api/v1/crm/lead-reads", "idempotent": false }
              ],
              "errors": []
            },
            {
              "id": "crm.sweep",
              "version": "1.0.0",
              "input": { "type": "FlowX.Http.Tests.CaptureLead" },
              "triggers": [ { "kind": "Cron", "expression": "0 * * * *" } ],
              "errors": []
            }
          ]
        }
        """;

    /// <summary>Every HTTP trigger becomes a path, and nothing else does.</summary>
    /// <remarks>
    /// A cron flow is a real flow with a real contract and no URL. Documenting it as one would send
    /// a generated client to an address that answers nothing.
    /// </remarks>
    [Fact]
    public void OnlyHttpTriggersBecomePaths()
    {
        var document = Document();

        var paths = document.GetProperty("paths");

        paths.EnumerateObject().Select(static path => path.Name).ShouldBe(
            ["/api/v1/crm/lead-reads", "/api/v1/crm/leads"]);

        paths.GetProperty("/api/v1/crm/leads").GetProperty("post")
            .GetProperty("operationId").GetString().ShouldBe("crm.lead.capture");
    }

    /// <summary>An idempotent endpoint documents its header as required.</summary>
    /// <remarks>
    /// The endpoint refuses a request without it, so a generated client that omitted it would fail
    /// every call with a 400 that reads like a server fault. Documenting the header as optional
    /// would document the opposite of what the endpoint does.
    /// </remarks>
    [Fact]
    public void AnIdempotentEndpointDocumentsItsHeaderAsRequired()
    {
        var document = Document();

        var parameter = document
            .GetProperty("paths").GetProperty("/api/v1/crm/leads").GetProperty("post")
            .GetProperty("parameters").EnumerateArray().Single();

        parameter.GetProperty("name").GetString().ShouldBe("Idempotency-Key");
        parameter.GetProperty("in").GetString().ShouldBe("header");
        parameter.GetProperty("required").GetBoolean().ShouldBeTrue();

        document
            .GetProperty("paths").GetProperty("/api/v1/crm/lead-reads").GetProperty("post")
            .TryGetProperty("parameters", out _)
            .ShouldBeFalse("an endpoint that does not deduplicate must not ask for the header.");
    }

    /// <summary>The error codes a flow can produce are named in its 400.</summary>
    /// <remarks>
    /// This is the half of an API that is hardest to discover by trying it, and the only half a
    /// generated client cannot infer from a successful call.
    /// </remarks>
    [Fact]
    public void TheErrorCodesAFlowCanProduceAreNamed()
    {
        var described = Document()
            .GetProperty("paths").GetProperty("/api/v1/crm/leads").GetProperty("post")
            .GetProperty("responses").GetProperty("400")
            .GetProperty("description").GetString().ShouldNotBeNull();

        described.ShouldContain("crm.lead_duplicate");
        described.ShouldContain("crm.schema_out_of_date");
    }

    /// <summary>The contract's shape comes from the generated metadata.</summary>
    [Fact]
    public void TheContractsShapeComesFromTheGeneratedMetadata()
    {
        var schema = Document()
            .GetProperty("components").GetProperty("schemas").GetProperty("CaptureLead");

        schema.GetProperty("type").GetString().ShouldBe("object");

        var properties = schema.GetProperty("properties");

        properties.GetProperty("company").GetProperty("type").GetString().ShouldBe("string");
        properties.GetProperty("leadId").GetProperty("format").GetString().ShouldBe("uuid");
        properties.GetProperty("score").GetProperty("type").GetString().ShouldBe("integer");
        properties.GetProperty("value").GetProperty("type").GetString().ShouldBe("number");
        properties.GetProperty("isQualified").GetProperty("type").GetString().ShouldBe("boolean");

        properties.GetProperty("capturedAt").GetProperty("format").GetString()
            .ShouldBe("date-time");

        properties.GetProperty("tags").GetProperty("type").GetString().ShouldBe("array");
        properties.GetProperty("tags").GetProperty("items").GetProperty("type").GetString()
            .ShouldBe("string");

        properties.GetProperty("source").GetProperty("enum").EnumerateArray()
            .Select(static value => value.GetString())
            .ShouldBe(["Web", "Referral"]);
    }

    /// <summary>A nested contract is described too, not just referred to.</summary>
    /// <remarks>
    /// Describing one type names another, which is why the writer walks by index rather than by
    /// iterator — a nested record added while the collection is being enumerated would otherwise
    /// be referenced by a pointer to nothing.
    /// </remarks>
    [Fact]
    public void ANestedContractIsDescribedAndNotOnlyReferredTo()
    {
        var schemas = Document().GetProperty("components").GetProperty("schemas");

        schemas.GetProperty("CaptureLead").GetProperty("properties").GetProperty("origin")
            .GetProperty("$ref").GetString().ShouldBe("#/components/schemas/Origin");

        schemas.GetProperty("Origin").GetProperty("properties").GetProperty("campaign")
            .GetProperty("type").GetString().ShouldBe("string");
    }

    /// <summary>A contract the context does not carry is opaque rather than invented.</summary>
    /// <remarks>
    /// A guessed shape produces a document that is confidently wrong, which is worse for a client
    /// generator than one that is honestly incomplete.
    /// </remarks>
    [Fact]
    public void AContractTheContextDoesNotCarryIsOpaqueRatherThanInvented()
    {
        var manifest = Manifest.Replace(
            "FlowX.Http.Tests.LeadCaptured", "Nothing.At.All", StringComparison.Ordinal);

        var schema = JsonDocument
            .Parse(OpenApi.Write(manifest, ContractContext.Default, null))
            .RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("All");

        schema.GetProperty("type").GetString().ShouldBe("object");
        schema.TryGetProperty("properties", out _).ShouldBeFalse();
        schema.GetProperty("description").GetString().ShouldNotBeNull()
            .ShouldContain("not described rather than guessed");
    }

    /// <summary>The document names itself as OpenAPI, and the application as its title.</summary>
    [Fact]
    public void TheDocumentCarriesTheApplicationsOwnNameAndVersion()
    {
        var document = Document();

        document.GetProperty("openapi").GetString().ShouldBe("3.1.0");
        document.GetProperty("info").GetProperty("title").GetString().ShouldBe("Crm");
        document.GetProperty("info").GetProperty("version").GetString().ShouldBe("2.1.0");
    }

    /// <summary>A manifest with no flows is an empty document rather than a throw.</summary>
    [Fact]
    public void AManifestWithNoFlowsIsAnEmptyDocument()
    {
        var document = JsonDocument
            .Parse(OpenApi.Write("""{"application":{"name":"None","version":"1.0.0"}}""",
                ContractContext.Default, null))
            .RootElement;

        document.GetProperty("paths").EnumerateObject().ShouldBeEmpty();
        document.GetProperty("components").GetProperty("schemas")
            .TryGetProperty("ProblemDetails", out _).ShouldBeTrue();
    }

    private static JsonElement Document() =>
        JsonDocument.Parse(OpenApi.Write(Manifest, ContractContext.Default, null)).RootElement;
}

/// <summary>Where a lead came from.</summary>
public enum LeadSource
{
    /// <summary>The website.</summary>
    Web,

    /// <summary>Somebody sent them.</summary>
    Referral,
}

/// <summary>A nested contract, so the writer has one to walk into.</summary>
/// <param name="Campaign">Which campaign.</param>
public sealed record Origin(string Campaign);

/// <summary>A contract with one member of each shape the writer has to describe.</summary>
/// <param name="LeadId">An identifier.</param>
/// <param name="Company">Text.</param>
/// <param name="Score">A whole number.</param>
/// <param name="Value">A decimal.</param>
/// <param name="IsQualified">A flag.</param>
/// <param name="CapturedAt">An instant.</param>
/// <param name="Tags">A list of text.</param>
/// <param name="Source">One of a closed set.</param>
/// <param name="Origin">Another contract.</param>
public sealed record CaptureLead(
    Guid LeadId,
    string Company,
    int Score,
    decimal Value,
    bool IsQualified,
    DateTimeOffset CapturedAt,
    IReadOnlyList<string> Tags,
    LeadSource Source,
    Origin Origin);

/// <summary>What comes back.</summary>
/// <param name="LeadId">The lead.</param>
public sealed record LeadCaptured(Guid LeadId);

/// <summary>The generated metadata these tests describe contracts from.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CaptureLead))]
[JsonSerializable(typeof(LeadCaptured))]
[JsonSerializable(typeof(Origin))]
public sealed partial class ContractContext : JsonSerializerContext;

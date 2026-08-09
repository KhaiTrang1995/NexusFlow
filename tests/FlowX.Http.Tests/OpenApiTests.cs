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

    /// <summary>
    /// A document whose nested contract sorts before a top-level one.
    /// </summary>
    /// <remarks>
    /// <strong><c>Coupon</c> is the whole point of this fixture.</strong> The schema collection is
    /// sorted, and a discovery only breaks a count-based pager when it lands at or before the
    /// boundary: <c>Origin</c> in the manifest above sorts after everything already written, so
    /// the bug that produced 36 duplicates in the CRM sample left that document correct by luck.
    /// <c>Coupon</c> sorts first of four, which is the case a regression has to be caught on.
    /// </remarks>
    private const string NestedManifest = """
        {
          "schemaVersion": "0.1.0",
          "application": { "name": "Bulk", "version": "1.0.0" },
          "flows": [
            {
              "id": "bulk.rows.submit",
              "version": "1.0.0",
              "input": { "type": "FlowX.Http.Tests.SubmitRows" },
              "output": { "type": "FlowX.Http.Tests.Enrolled" },
              "triggers": [
                { "kind": "Http", "method": "POST", "route": "/api/v1/rows", "idempotent": true }
              ],
              "errors": []
            },
            {
              "id": "bulk.enrol",
              "version": "1.0.0",
              "input": { "type": "FlowX.Http.Tests.Enrol" },
              "output": { "type": "FlowX.Http.Tests.Enrolled" },
              "triggers": [
                { "kind": "Http", "method": "POST", "route": "/api/v1/enrolments", "idempotent": true }
              ],
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


    /// <summary>Every schema the document names is defined in it, exactly once.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Two failures of the same bug, and neither was visible from a passing test.</strong>
    /// The schema collection is sorted and grows while it is being written — describing a contract
    /// names the contracts it holds — and the writer paged it by count. A discovery inserted at
    /// its sorted position moved the boundary, so whatever had been pushed past it was written a
    /// second time and the discovery itself was never written at all.
    /// </para>
    /// <para>
    /// <strong>A duplicate key is not a cosmetic fault.</strong> <c>JsonDocument</c> keeps the last
    /// of them and every other reader keeps a different one, so the document parses here and is
    /// rejected by the generator a client actually runs: <c>openapi-typescript</c> stops at
    /// "duplicated mapping key" and emits nothing. The raw bytes are counted below for that
    /// reason — asking the parsed document would ask the one reader that cannot see it.
    /// </para>
    /// </remarks>
    [Fact]
    public void EverySchemaIsDefinedOnceAndNothingRefersToOneThatIsNot()
    {
        var json = OpenApi.Write(NestedManifest, ContractContext.Default, null);
        var document = JsonDocument.Parse(json).RootElement;

        var defined = document.GetProperty("components").GetProperty("schemas")
            .EnumerateObject().Select(static schema => schema.Name).ToArray();

        var written = SchemaKeys(json);

        written.Where(name => written.Count(other => other == name) > 1)
            .Distinct(StringComparer.Ordinal)
            .ShouldBeEmpty("a schema written twice makes the document invalid to every reader " +
                           "that does not silently keep the last one.");

        Referenced(document).Except(defined, StringComparer.Ordinal).ShouldBeEmpty(
            "these are named by a $ref and defined nowhere, so a generated client has no type " +
            "for them.");

        defined.ShouldContain("Coupon", "the nested contract is the one the paging bug dropped.");
    }

    /// <summary>A list of dictionaries is a list of objects, not a reference to a generic.</summary>
    /// <remarks>
    /// The items branch offered "scalar, or <c>$ref</c>" and a dictionary is neither, so
    /// <c>IReadOnlyList&lt;IReadOnlyDictionary&lt;string, string?&gt;&gt;</c> — a bulk import's
    /// rows — referred to a schema named after the assembly-qualified spelling of the constructed
    /// generic, brackets, version and public key token included.
    /// </remarks>
    [Fact]
    public void AListOfDictionariesIsDescribedRatherThanReferred()
    {
        var document = JsonDocument
            .Parse(OpenApi.Write(NestedManifest, ContractContext.Default, null))
            .RootElement;

        var items = document.GetProperty("components").GetProperty("schemas")
            .GetProperty("SubmitRows").GetProperty("properties")
            .GetProperty("rows").GetProperty("items");

        items.GetProperty("type").GetString().ShouldBe("object");
        items.TryGetProperty("$ref", out _).ShouldBeFalse();

        Referenced(document).ShouldAllBe(static name => !name.Contains('=', StringComparison.Ordinal));
    }

    /// <summary>The names under components/schemas, as written rather than as parsed.</summary>
    private static List<string> SchemaKeys(string json)
    {
        var start = json.IndexOf("\"schemas\":", StringComparison.Ordinal);
        var keys = new List<string>();
        var depth = 0;

        for (var i = json.IndexOf('{', start); i < json.Length; i++)
        {
            if (json[i] == '{')
            {
                depth++;
            }
            else if (json[i] == '}')
            {
                if (--depth == 0)
                {
                    break;
                }
            }
            else if (json[i] == '"' && depth == 1)
            {
                var end = json.IndexOf('"', i + 1);
                keys.Add(json[(i + 1)..end]);
                i = end;
            }
        }

        return keys;
    }

    /// <summary>Every schema name any $ref in the document points at.</summary>
    private static IEnumerable<string> Referenced(JsonElement document)
    {
        var found = new List<string>();
        Walk(document, found);

        return found.Distinct(StringComparer.Ordinal);

        static void Walk(JsonElement node, List<string> found)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var member in node.EnumerateObject())
                    {
                        if (member.NameEquals("$ref") && member.Value.GetString() is { } pointer)
                        {
                            found.Add(pointer["#/components/schemas/".Length..]);
                        }

                        Walk(member.Value, found);
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (var item in node.EnumerateArray())
                    {
                        Walk(item, found);
                    }

                    break;

                default:
                    break;
            }
        }
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

/// <summary>A nested contract whose name sorts before the contracts that hold it.</summary>
/// <param name="Code">The code.</param>
public sealed record Coupon(string Code);

/// <summary>A contract holding one, so the writer discovers it while walking.</summary>
/// <param name="Coupon">The coupon.</param>
public sealed record Enrol(Coupon Coupon);

/// <summary>What comes back from an enrolment.</summary>
/// <param name="EnrolmentId">The enrolment.</param>
public sealed record Enrolled(Guid EnrolmentId);

/// <summary>A contract whose list holds dictionaries rather than contracts.</summary>
/// <param name="Rows">The rows, each a bag of columns, as a bulk import carries them.</param>
public sealed record SubmitRows(IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows);

/// <summary>The generated metadata these tests describe contracts from.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CaptureLead))]
[JsonSerializable(typeof(LeadCaptured))]
[JsonSerializable(typeof(Origin))]
[JsonSerializable(typeof(SubmitRows))]
[JsonSerializable(typeof(Coupon))]
[JsonSerializable(typeof(Enrol))]
[JsonSerializable(typeof(Enrolled))]
public sealed partial class ContractContext : JsonSerializerContext;

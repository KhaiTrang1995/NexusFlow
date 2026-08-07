using System.Text;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// What a seed file may say, and what it is refused for saying.
/// </summary>
/// <remarks>
/// <strong>No database here on purpose.</strong> Everything below is a property of the document,
/// and a check that needs a server to prove is a check that only runs where there is one. The
/// reader's whole job is to be the thing that fails before anything is written.
/// </remarks>
public sealed class SeedReaderTests
{
    /// <summary>A whole file is read, aliases and all.</summary>
    [Fact]
    public void AWellFormedFileIsRead()
    {
        var read = SeedReader.Read(Utf8(Fixture));

        read.IsSuccess.ShouldBeTrue(read.IsSuccess ? null : read.Error!.Message);

        var document = read.Value!;

        document.Tenant.ShouldBe("crm-northwind");
        document.Metadata.Processes.Count.ShouldBe(1);
        document.Data.Accounts.Count.ShouldBe(1);
        document.Data.Opportunities[0]!.Amount.ShouldBe(184_000m);
    }

    /// <summary>
    /// A property nobody declared is a refusal naming it, not a default nobody notices.
    /// </summary>
    /// <remarks>
    /// <strong>The test this file exists for.</strong> A seed is read once, by somebody who is
    /// about to redeploy; a silently ignored <c>"lifecycle"</c> spelt <c>"lifecyle"</c> is a
    /// tenant configured differently from the file that describes it, and nothing ever says so.
    /// </remarks>
    [Fact]
    public void AMisspeltPropertyIsRefused()
    {
        var read = SeedReader.Read(Utf8(Fixture.Replace("\"region\"", "\"regoin\"", StringComparison.Ordinal)));

        read.IsSuccess.ShouldBeFalse();
        read.Error!.Code.ShouldBe("crm.seed_unreadable");
        read.Error.Message.ShouldContain("regoin");
    }

    /// <summary>A reference to an alias the file does not declare is refused.</summary>
    [Fact]
    public void AReferenceToNothingIsRefused()
    {
        var read = SeedReader.Read(
            Utf8(Fixture.Replace("\"account\": \"northwind\"", "\"account\": \"typo\"", StringComparison.Ordinal)));

        read.IsSuccess.ShouldBeFalse();
        read.Error!.Code.ShouldBe("crm.seed_reference_unknown");
        read.Error.Message.ShouldContain("typo");
    }

    /// <summary>An opportunity naming a stage no process declares is refused.</summary>
    [Fact]
    public void AStageNoProcessDeclaresIsRefused()
    {
        var read = SeedReader.Read(
            Utf8(Fixture.Replace("\"pipeline:Discovery\"", "\"pipeline:Invented\"", StringComparison.Ordinal)));

        read.IsSuccess.ShouldBeFalse();
        read.Error!.Code.ShouldBe("crm.seed_reference_unknown");
    }

    /// <summary>
    /// Two items sharing an alias are refused, because they would share an id.
    /// </summary>
    /// <remarks>
    /// The identifier is derived from the alias, so a duplicate is not two rows — it is one row
    /// written twice, and the second write does nothing. Silently.
    /// </remarks>
    [Fact]
    public void ADuplicateAliasIsRefused()
    {
        var twice = Fixture.Replace(
            "\"leads\": [",
            """
            "leads": [
                {
                  "alias": "acme", "company": "A", "contactName": "B", "email": null,
                  "source": "Web", "status": "New", "score": 1, "owner": null
                },
            """,
            StringComparison.Ordinal);

        var read = SeedReader.Read(Utf8(twice));

        read.IsSuccess.ShouldBeFalse();
        read.Error!.Code.ShouldBe("crm.seed_alias_invalid");
        read.Error.Message.ShouldContain("twice");
    }

    /// <summary>A converted lead is refused, because conversion is a saga and not a row.</summary>
    [Fact]
    public void AConvertedLeadIsRefused()
    {
        var read = SeedReader.Read(
            Utf8(Fixture.Replace("\"status\": \"Working\"", "\"status\": \"Converted\"", StringComparison.Ordinal)));

        read.IsSuccess.ShouldBeFalse();
        read.Error!.Code.ShouldBe("crm.seed_out_of_range");
    }

    /// <summary>A probability outside the column's range is refused before the column says so.</summary>
    [Fact]
    public void AProbabilityOutsideItsRangeIsRefused()
    {
        var read = SeedReader.Read(
            Utf8(Fixture.Replace("\"probability\": 40", "\"probability\": 140", StringComparison.Ordinal)));

        read.IsSuccess.ShouldBeFalse();
        read.Error!.Code.ShouldBe("crm.seed_out_of_range");
        read.Error.Message.ShouldContain("probability");
    }

    /// <summary>A field naming both an entity and an object is refused, and so is one naming neither.</summary>
    [Fact]
    public void AFieldNeedsExactlyOneOwner()
    {
        var both = Fixture.Replace(
            "\"entity\": \"Account\", \"target\": null",
            "\"entity\": \"Account\", \"target\": \"project\"",
            StringComparison.Ordinal);

        SeedReader.Read(Utf8(both)).Error!.Code.ShouldBe("crm.seed_field_owner");

        var neither = Fixture.Replace(
            "\"entity\": \"Account\", \"target\": null",
            "\"entity\": null, \"target\": null",
            StringComparison.Ordinal);

        SeedReader.Read(Utf8(neither)).Error!.Code.ShouldBe("crm.seed_field_owner");
    }

    /// <summary>A file larger than the limit is refused without being parsed.</summary>
    [Fact]
    public void AFileLargerThanTheLimitIsRefused()
    {
        var oversized = new byte[SeedLimits.MaxBytes + 1];

        var read = SeedReader.Read(oversized);

        read.IsSuccess.ShouldBeFalse();
        read.Error!.Code.ShouldBe("crm.seed_too_large");
    }

    /// <summary>A picklist value the schema would refuse is refused here, by name.</summary>
    [Fact]
    public void AnOptionValueTheSchemaRefusesIsNamed()
    {
        var read = SeedReader.Read(
            Utf8(Fixture.Replace("\"value\": \"enterprise\"", "\"value\": \"Enterprise\"", StringComparison.Ordinal)));

        read.IsSuccess.ShouldBeFalse();
        read.Error!.Code.ShouldBe("crm.seed_name_invalid");
        read.Error.Message.ShouldContain("Enterprise");
    }

    /// <summary>A transition out of a stage and back into it is refused.</summary>
    [Fact]
    public void ATransitionToItselfIsRefused()
    {
        var read = SeedReader.Read(
            Utf8(Fixture.Replace(
                "\"from\": \"Discovery\", \"to\": \"Proposal\"",
                "\"from\": \"Discovery\", \"to\": \"Discovery\"",
                StringComparison.Ordinal)));

        read.IsSuccess.ShouldBeFalse();
        read.Error!.Code.ShouldBe("crm.seed_out_of_range");
    }

    // ------------------------------------------------------------------------------- fixtures

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>One document exercising every collection, edited per test.</summary>
    internal const string Fixture = """
        {
          "tenant": "crm-northwind",
          "metadata": {
            "objects": [
              { "alias": "project", "name": "project", "label": "Project" }
            ],
            "fields": [
              {
                "alias": "segment", "entity": "Account", "target": null,
                "name": "segment", "label": "Segment", "type": "Picklist", "required": false,
                "options": [
                  { "value": "enterprise", "label": "Enterprise" },
                  { "value": "mid_market", "label": "Mid-market" }
                ]
              },
              {
                "alias": "project_code", "entity": null, "target": "project",
                "name": "code", "label": "Code", "type": "Text", "required": true, "options": null
              }
            ],
            "relationships": [],
            "processes": [
              {
                "alias": "pipeline",
                "appliesTo": "Opportunity",
                "version": 1,
                "stages": [
                  { "name": "Discovery", "terminal": false },
                  { "name": "Proposal", "terminal": false },
                  { "name": "Closed", "terminal": true }
                ],
                "transitions": [
                  { "from": "Discovery", "to": "Proposal", "trigger": "qualified" },
                  { "from": "Proposal", "to": "Closed", "trigger": "signed" }
                ]
              }
            ]
          },
          "data": {
            "accounts": [
              {
                "alias": "northwind", "name": "Northwind Systems", "industry": "SaaS",
                "lifecycle": "Customer", "region": "NA",
                "owner": "33333333-3333-3333-3333-333333333333"
              }
            ],
            "contacts": [
              {
                "alias": "elena", "account": "northwind", "fullName": "Elena Vargas",
                "email": "e.vargas@northwind.example", "phone": null, "primary": true
              }
            ],
            "opportunities": [
              {
                "alias": "expansion", "account": "northwind", "primaryContact": "elena",
                "name": "Northwind — Platform Expansion", "amount": 184000, "currency": "EUR",
                "stage": "pipeline:Discovery", "probability": 40,
                "expectedClose": "2026-09-30",
                "owner": "33333333-3333-3333-3333-333333333333"
              }
            ],
            "leads": [
              {
                "alias": "acme", "company": "Acme Freight", "contactName": "Ron Petrov",
                "email": "ron@acme.example", "source": "Event", "status": "Working",
                "score": 55, "owner": null
              }
            ],
            "records": [
              { "alias": "atlas", "target": "project", "values": { "code": "P-ATLAS" } }
            ]
          }
        }
        """;
}

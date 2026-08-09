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

    /// <summary>A criterion on an attribute its subject does not have is refused.</summary>
    /// <remarks>
    /// The list is closed and the same one the capability checks. Reading it here turns a typo
    /// into a sentence about the file, rather than a process that is published, matches nothing,
    /// and looks exactly like a threshold nobody has crossed yet.
    /// </remarks>
    [Fact]
    public void AnApprovalCriterionOnAnUnknownAttributeIsRefused()
    {
        var read = SeedReader.Read(
            Utf8(Fixture.Replace(
                "\"attribute\": \"discount\"", "\"attribute\": \"amount\"", StringComparison.Ordinal)));

        read.IsSuccess.ShouldBeFalse("amount is an opportunity's attribute, not a quote's.");
        read.Error!.Code.ShouldBe("crm.seed_reference_unknown");
        read.Error.Message.ShouldContain("amount");
    }

    /// <summary>A step whose kind and approver disagree is refused.</summary>
    /// <remarks>
    /// Both halves fail at the moment somebody needs the thing approved, which is the worst time
    /// to find out: a role holder with no role names nobody, and the submitter's manager with a
    /// name written next to it is two answers to one question.
    /// </remarks>
    [Fact]
    public void AnApprovalStepThatNamesNobodyIsRefused()
    {
        var read = SeedReader.Read(
            Utf8(Fixture.Replace(
                "\"kind\": \"RoleHolder\", \"approver\": \"Director\"",
                "\"kind\": \"RoleHolder\", \"approver\": null",
                StringComparison.Ordinal)));

        read.IsSuccess.ShouldBeFalse();
        read.Error!.Code.ShouldBe("crm.seed_out_of_range");
    }

    /// <summary>A quote with no lines is refused.</summary>
    /// <remarks>
    /// <strong>A quote is its lines.</strong> One with none has a subtotal of zero, a negative
    /// total once a discount is taken off it, and a builder with nothing to draw beside a figure
    /// the reader cannot reconcile. Every seeded quote was exactly that until the lines existed.
    /// </remarks>
    [Fact]
    public void AQuoteWithNoLinesIsRefused()
    {
        var read = SeedReader.Read(Utf8(Fixture.Replace(
            "{ \"sku\": \"PLATFORM-ENT\", \"quantity\": 10, \"unitPrice\": 1000 },",
            string.Empty,
            StringComparison.Ordinal)
            .Replace(
                "{ \"sku\": \"ONBOARD\", \"quantity\": 1, \"unitPrice\": 2000 }",
                string.Empty,
                StringComparison.Ordinal)));

        read.IsSuccess.ShouldBeFalse();
        read.Error!.Code.ShouldBe("crm.seed_out_of_range");
        read.Error.Message.ShouldContain("lines");
    }

    /// <summary>A discount larger than the lines add up to is refused.</summary>
    /// <remarks>
    /// Checked against the sum rather than against a stated subtotal, because there is no longer
    /// a stated subtotal to check against — and a discount past it makes a negative total, which
    /// <c>numeric(19,4)</c> takes without complaint.
    /// </remarks>
    [Fact]
    public void ADiscountLargerThanTheLinesIsRefused()
    {
        var read = SeedReader.Read(
            Utf8(Fixture.Replace("\"discount\": 1200", "\"discount\": 99000", StringComparison.Ordinal)));

        read.IsSuccess.ShouldBeFalse();
        read.Error!.Code.ShouldBe("crm.seed_out_of_range");
        read.Error.Message.ShouldContain("lines add up to");
    }

    /// <summary>A report grouped by something its source does not have is refused.</summary>
    /// <remarks>
    /// The same closed list the capability checks. A dimension the source has no case for groups
    /// every row under null — which looks like a data problem and is not, and is discovered by
    /// somebody running the report rather than by the person who wrote the file.
    /// </remarks>
    [Fact]
    public void AReportGroupedBySomethingItsSourceLacksIsRefused()
    {
        var read = SeedReader.Read(Utf8(Fixture.Replace(
            "\"dimension\": \"Status\"", "\"dimension\": \"Outcome\"", StringComparison.Ordinal)));

        read.IsSuccess.ShouldBeFalse("Outcome is an opportunity's dimension, not a lead's.");
        read.Error!.Code.ShouldBe("crm.seed_reference_unknown");
        read.Error.Message.ShouldContain("Outcome");
    }

    /// <summary>A count given a field to count is refused, and so is a sum given none.</summary>
    /// <remarks>
    /// Both halves fail at the moment somebody runs the report. <c>Count</c> is of rows and has no
    /// field to name; every other measure is over one and cannot proceed without it.
    /// </remarks>
    [Fact]
    public void AMeasureAndItsFieldHaveToAgree()
    {
        SeedReader.Read(Utf8(Fixture.Replace(
            "\"measure\": \"Count\", \"measureOf\": null",
            "\"measure\": \"Count\", \"measureOf\": \"Score\"",
            StringComparison.Ordinal)))
            .Error!.Code.ShouldBe("crm.seed_out_of_range");

        SeedReader.Read(Utf8(Fixture.Replace(
            "\"measure\": \"Count\", \"measureOf\": null",
            "\"measure\": \"Sum\", \"measureOf\": null",
            StringComparison.Ordinal)))
            .Error!.Code.ShouldBe("crm.seed_out_of_range");
    }

    /// <summary>A portfolio, and the plan that rolls into it, are both read.</summary>
    /// <remarks>
    /// <strong>Both halves used to be refused.</strong> The Portfolio kind was rejected outright
    /// because a portfolio with nothing under it is a root with no children — and the only reason
    /// it could have none was that this document had no word for a parent. So a seeded year was a
    /// target with no plans against it, reporting nought committed on the executive screen.
    /// </remarks>
    [Fact]
    public void APortfolioAndThePlanThatRollsIntoItAreRead()
    {
        var read = SeedReader.Read(Utf8(PlanFixture));

        read.IsSuccess.ShouldBeTrue(read.IsSuccess ? null : read.Error!.Message);

        var plans = read.Value!.Data.Plans;

        plans[0]!.Kind.ShouldBe(PlanKind.Portfolio);
        plans[0]!.Parent.ShouldBeNull();
        plans[1]!.Parent.ShouldBe("year", "the child does not say what it rolls into.");
    }

    /// <summary>A plan rolling into a plan the file does not declare is refused.</summary>
    [Fact]
    public void APlanRollingIntoNothingIsRefused()
    {
        var read = SeedReader.Read(Utf8(PlanFixture.Replace(
            "\"parent\": \"year\"", "\"parent\": \"typo\"", StringComparison.Ordinal)));

        read.IsSuccess.ShouldBeFalse();
        read.Error!.Code.ShouldBe("crm.seed_reference_unknown");
        read.Error.Message.ShouldContain("typo");
    }

    /// <summary>A plan tree that loops is refused rather than written.</summary>
    /// <remarks>
    /// <strong>Nothing downstream would catch it.</strong> Migration <c>0018</c> constrains only
    /// the self-parent case, so two plans that are each other's parent are two individually legal
    /// rows — and the applier orders its writes parents-first, which a loop has no answer for. A
    /// file carrying one would be applied half-way and leave a tenant nobody can total.
    /// </remarks>
    [Fact]
    public void APlanTreeThatLoopsIsRefused()
    {
        var read = SeedReader.Read(Utf8(PlanFixture.Replace(
            "\"risks\": [], \"parent\": null", "\"risks\": [], \"parent\": \"quarter\"",
            StringComparison.Ordinal)));

        read.IsSuccess.ShouldBeFalse("the year rolls into the quarter that rolls into the year.");
        read.Error!.Code.ShouldBe("crm.seed_plan_tree_loops");
    }

    // ------------------------------------------------------------------------------- fixtures

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>Two periods and two plans, one rolling into the other.</summary>
    /// <remarks>
    /// Its own document rather than an edit of <see cref="Fixture"/>, which declares no calendar
    /// — and a plan is committed against a period.
    /// </remarks>
    internal const string PlanFixture = """
        {
          "tenant": "crm-northwind",
          "metadata": {
            "periods": [
              {
                "alias": "fy26", "name": "fy26", "label": "FY26",
                "startsOn": "2025-10-01", "endsOn": "2026-09-30", "parent": null
              },
              {
                "alias": "fy26_q3", "name": "fy26_q3", "label": "FY26 Q3",
                "startsOn": "2026-04-01", "endsOn": "2026-06-30", "parent": "fy26"
              }
            ]
          },
          "data": {
            "accounts": [
              {
                "alias": "northwind", "name": "Northwind Systems", "industry": "SaaS",
                "lifecycle": "Customer", "region": "NA",
                "owner": "33333333-3333-3333-3333-333333333333", "values": {}
              }
            ],
            "plans": [
              {
                "alias": "year", "name": "northwind_fy26", "label": "Northwind FY26",
                "kind": "Portfolio", "period": "fy26", "owner": "director-northwind-1",
                "subject": null, "targetAmount": 500000, "currency": "EUR",
                "objectives": [], "steps": [], "risks": [], "parent": null
              },
              {
                "alias": "quarter", "name": "northwind_fy26_q3", "label": "Northwind FY26 Q3",
                "kind": "Account", "period": "fy26_q3", "owner": "rep-northwind-1",
                "subject": "northwind", "targetAmount": 500000, "currency": "EUR",
                "objectives": [], "steps": [], "risks": [], "parent": "year"
              }
            ]
          }
        }
        """;

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
            ],
            "reports": [
              {
                "alias": "leads_by_status", "name": "leads_by_status",
                "label": "Leads by status", "source": "Lead",
                "dimension": "Status", "measure": "Count", "measureOf": null
              }
            ],
            "approvalProcesses": [
              {
                "alias": "big_discount", "name": "big_discount", "label": "Discount over 200",
                "subject": "Quote", "priority": 10,
                "criteria": [
                  { "attribute": "discount", "operator": "GreaterThan", "value": "200" }
                ],
                "steps": [
                  { "label": "The submitter's manager", "kind": "SubmittersManager", "approver": null },
                  { "label": "Sales director", "kind": "RoleHolder", "approver": "Director" }
                ]
              }
            ]
          },
          "data": {
            "accounts": [
              {
                "alias": "northwind", "name": "Northwind Systems", "industry": "SaaS",
                "lifecycle": "Customer", "region": "NA",
                "owner": "33333333-3333-3333-3333-333333333333",
                "values": { "segment": "enterprise" }
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
            ],
            "quotes": [
              {
                "alias": "expansion_q1", "opportunity": "expansion", "status": "Issued",
                "lines": [
                  { "sku": "PLATFORM-ENT", "quantity": 10, "unitPrice": 1000 },
                  { "sku": "ONBOARD", "quantity": 1, "unitPrice": 2000 }
                ],
                "discount": 1200, "currency": "EUR", "validForDays": 21
              }
            ]
          }
        }
        """;
}

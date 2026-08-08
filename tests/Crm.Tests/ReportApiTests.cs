using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The reports sales, marketing and operations ask for, and the dashboard they put them on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this suite is really about is the closed vocabulary.</strong> Every reporting
/// feature reaches the same fork: an expression the user writes — a parser, an evaluator, a
/// sandbox, an injection surface — or a fixed set of sources, dimensions and measures where the
/// user's choice is a <em>value</em>. This takes the second road, so the assertions here are about
/// what happens when somebody names something the source does not have: it is refused when the
/// report is saved, not when a dashboard runs it at nine in the morning.
/// </para>
/// <para>
/// <strong>Sales, marketing and operations are the three built-in sources.</strong> Opportunity is
/// the pipeline, Lead is the funnel, Activity is the queue. The fourth source is whatever the
/// tenant invented, where the dimension is a jsonb key and therefore already a bound value.
/// </para>
/// </remarks>
public sealed class ReportApiTests
{
    private const string Objects = "/api/v1/crm/custom/objects";
    private const string Fields = "/api/v1/crm/custom/fields";
    private const string Records = "/api/v1/crm/custom/records";
    private const string Reports = "/api/v1/crm/reports";
    private const string Runs = "/api/v1/crm/reports/runs";
    private const string Dashboards = "/api/v1/crm/dashboards";
    private const string DashboardRuns = "/api/v1/crm/dashboards/runs";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Marketing's funnel: leads by source, counted.</summary>
    [Fact]
    public async Task LeadsGroupedBySourceAreCounted()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await LeadAsync(app, "Web");
        await LeadAsync(app, "Web");
        await LeadAsync(app, "Referral");

        await ReportAsync(app, new DefineReport(
            "leads_by_source", "Leads by source",
            ReportSource.Lead, null, "Source", ReportMeasure.Count, null));

        var run = await RunAsync(app, "leads_by_source");

        run.Groups.Count.ShouldBe(2);
        run.Groups[0].Dimension.ShouldBe("Web", "the groups come back largest first.");
        run.Groups[0].Value.ShouldBe("2");
        run.Groups[1].Dimension.ShouldBe("Referral");
        run.Groups[1].Value.ShouldBe("1");
    }

    /// <summary>Sales' pipeline: opportunity amounts totalled by outcome.</summary>
    [Fact]
    public async Task OpportunityAmountsAreTotalledByOutcome()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var world = await PipelineAsync(app);

        await OpportunityAsync(app, world, 1000m, "Won");
        await OpportunityAsync(app, world, 250m, "Won");
        await OpportunityAsync(app, world, 400m, null);

        await ReportAsync(app, new DefineReport(
            "pipeline_by_outcome", "Pipeline by outcome",
            ReportSource.Opportunity, null, "Outcome", ReportMeasure.Sum, "Amount"));

        var run = await RunAsync(app, "pipeline_by_outcome");

        run.Groups[0].Dimension.ShouldBe("Won");
        run.Groups[0].Value.ShouldBe("1250", "a total of 1250.0000 and one of 1250 are one number.");
        run.Groups[0].Rows.ShouldBe(2);

        run.Groups[1].Dimension.ShouldBe(
            "(none)", "a null outcome is a group a chart can label, not an absent key.");
    }

    /// <summary>
    /// A run says what it reduced, not only how.
    /// </summary>
    /// <remarks>
    /// <strong>Without the field, a reader cannot know the unit.</strong> "Sum" is how the groups
    /// were reduced and says nothing about over what, which left a client two wrong answers to
    /// choose between: draw every figure as money and a sum of probabilities reads as euros, or
    /// draw every figure bare and a pipeline total reads as a tally. The report already knew the
    /// field; it simply did not send it.
    /// </remarks>
    [Fact]
    public async Task ARunSaysWhatItReducedAndNotOnlyHow()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var world = await PipelineAsync(app);

        await OpportunityAsync(app, world, 1000m, "Won");

        await ReportAsync(app, new DefineReport(
            "by_amount", "Pipeline by outcome",
            ReportSource.Opportunity, null, "Outcome", ReportMeasure.Sum, "Amount"));

        await ReportAsync(app, new DefineReport(
            "by_rows", "Deals by outcome",
            ReportSource.Opportunity, null, "Outcome", ReportMeasure.Count, null));

        var summed = await RunAsync(app, "by_amount");

        summed.Measure.ShouldBe("Sum");
        summed.MeasureOf.ShouldBe("Amount", "which is what makes it money rather than a count.");

        var counted = await RunAsync(app, "by_rows");

        counted.Measure.ShouldBe("Count");
        counted.MeasureOf.ShouldBeNull("a count is of rows, and there is no field to name.");
    }

    /// <summary>A custom object reports on a field an administrator invented.</summary>
    [Fact]
    public async Task ACustomObjectReportsOnAnInventedField()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        await RecordAsync(app, site, "north", "100");
        await RecordAsync(app, site, "north", "250");
        await RecordAsync(app, site, "south", "40");

        await ReportAsync(app, new DefineReport(
            "area_by_region", "Floor area by region",
            ReportSource.CustomObject, site, "region", ReportMeasure.Sum, "floor_area"));

        var run = await RunAsync(app, "area_by_region");

        run.Groups[0].Dimension.ShouldBe("north");
        run.Groups[0].Value.ShouldBe("350");
        run.Groups[1].Dimension.ShouldBe("south");
        run.Groups[1].Value.ShouldBe("40");
    }

    /// <summary>A dimension the source does not have is refused when the report is saved.</summary>
    /// <remarks>
    /// The whole point of the closed vocabulary. Refused at run time instead, this would fail on
    /// somebody's dashboard a week after the person who wrote it moved on, and would look like a
    /// data problem rather than a typo.
    /// </remarks>
    [Fact]
    public async Task ADimensionTheSourceDoesNotHaveIsRefusedWhenItIsSaved()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Reports,
            new DefineReport(
                "bad", "Bad", ReportSource.Lead, null, "Stage", ReportMeasure.Count, null),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.report_dimension_unknown");
    }

    /// <summary>A field the source cannot be aggregated over is refused too.</summary>
    [Fact]
    public async Task AMeasureTheSourceDoesNotHaveIsRefusedWhenItIsSaved()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Reports,
            new DefineReport(
                "bad", "Bad", ReportSource.Lead, null, "Source", ReportMeasure.Sum, "Amount"),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.report_measure_unknown");
    }

    /// <summary>A count that names a field, and a sum that names none, are both refused.</summary>
    [Fact]
    public async Task AMeasureAndItsFieldHaveToAgree()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        (await Code(app, new DefineReport(
            "a", "A", ReportSource.Lead, null, "Source", ReportMeasure.Count, "Score")))
            .ShouldBe("crm.report_measure_field_unwanted");

        (await Code(app, new DefineReport(
            "b", "B", ReportSource.Lead, null, "Source", ReportMeasure.Sum, null)))
            .ShouldBe("crm.report_measure_field_missing");
    }

    /// <summary>A dashboard runs its reports in one request, in the order it puts them.</summary>
    [Fact]
    public async Task ADashboardRunsItsReportsInOneRequestAndInOrder()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await LeadAsync(app, "Web");
        await OpportunityAsync(app, await PipelineAsync(app), 500m, "Won");

        await ReportAsync(app, new DefineReport(
            "leads_by_source", "Leads by source",
            ReportSource.Lead, null, "Source", ReportMeasure.Count, null));

        await ReportAsync(app, new DefineReport(
            "pipeline_by_outcome", "Pipeline by outcome",
            ReportSource.Opportunity, null, "Outcome", ReportMeasure.Sum, "Amount"));

        (await app.PostAsync(
            Dashboards,
            new DefineDashboard(
                "revenue", "Revenue", ["pipeline_by_outcome", "leads_by_source"]),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await app.PostAsync(
            DashboardRuns, new RunDashboard("revenue"), CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var run = await CrmApplication.ReadAsync<DashboardResult>(response);

        run.Tiles.Count.ShouldBe(2);
        run.Tiles[0].Name.ShouldBe(
            "pipeline_by_outcome", "a dashboard's order is what somebody arranged on a screen.");

        run.Tiles[1].Name.ShouldBe("leads_by_source");
        run.Tiles[1].Groups.ShouldHaveSingleItem().Value.ShouldBe("1");
    }

    /// <summary>A dashboard naming a report that does not exist is refused, not half-saved.</summary>
    [Fact]
    public async Task ADashboardNamingAMissingReportIsRefusedRatherThanHalfSaved()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await ReportAsync(app, new DefineReport(
            "leads_by_source", "Leads by source",
            ReportSource.Lead, null, "Source", ReportMeasure.Count, null));

        var response = await app.PostAsync(
            Dashboards,
            new DefineDashboard("revenue", "Revenue", ["leads_by_source", "no_such_report"]),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await app.PostAsync(
            DashboardRuns, new RunDashboard("revenue"), CrmTokens.Northwind, idempotencyKey: null))
            .StatusCode.ShouldBe(
                HttpStatusCode.NotFound,
                "the dashboard was written before its tiles were resolved.");
    }

    /// <summary>Running a report needs only <c>crm.read</c>; building one needs <c>crm.admin</c>.</summary>
    [Fact]
    public async Task BuildingAReportIsAdministrativeAndReadingOneIsNot()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        (await app.PostAsync(
            Reports,
            new DefineReport(
                "leads_by_source", "Leads by source",
                ReportSource.Lead, null, "Source", ReportMeasure.Count, null),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(
                HttpStatusCode.Forbidden, "a representative does not hold crm.admin.");

        await ReportAsync(app, new DefineReport(
            "leads_by_source", "Leads by source",
            ReportSource.Lead, null, "Source", ReportMeasure.Count, null));

        (await RunAsync(app, "leads_by_source")).Name.ShouldBe("leads_by_source");
    }

    /// <summary>One tenant's report never counts another's rows.</summary>
    [Fact]
    public async Task AReportNeverCountsAnotherTenantsRows()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await LeadAsync(app, "Web");

        await ReportAsync(app, new DefineReport(
            "leads_by_source", "Leads by source",
            ReportSource.Lead, null, "Source", ReportMeasure.Count, null));

        (await RunAsync(app, "leads_by_source")).Groups.ShouldHaveSingleItem().Value.ShouldBe("1");

        (await app.PostAsync(
            Runs, new RunReport("leads_by_source"), CrmTokens.Contoso, idempotencyKey: null))
            .StatusCode.ShouldBe(
                HttpStatusCode.NotFound, "a saved report belongs to the tenant that saved it.");
    }

    // ------------------------------------------------------------------------------- fixtures

    private static async Task<string?> Code(CrmApplication app, DefineReport report)
    {
        var response = await app.PostAsync(Reports, report, CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        return (await Problem(response)).GetProperty("code").GetString();
    }

    private static async Task ReportAsync(CrmApplication app, DefineReport report)
    {
        var response = await app.PostAsync(Reports, report, CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "saving '" + report.Name + "' failed.");
    }

    private static async Task<ReportResult> RunAsync(CrmApplication app, string name)
    {
        var response = await app.PostAsync(
            Runs, new RunReport(name), CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<ReportResult>(response);
    }

    private static async Task LeadAsync(CrmApplication app, string source) =>
        await app.Crm.AsTenantAsync(
            CrmTokens.NorthwindTenant,
            """
            INSERT INTO lead (lead_id, tenant_id, company, contact_name, source, status,
                              score, captured_at)
            VALUES (gen_random_uuid(), @tenant, 'Northwind', 'Ada Rowe', @source, 'New', 40, now())
            """,
            Cancellation,
            ("tenant", (object?)CrmTokens.NorthwindTenant),
            ("source", source));

    /// <summary>An account, a contact and an active process, made once per test.</summary>
    /// <remarks>
    /// Once, because a process definition is unique on (tenant, applies-to, version) — three
    /// opportunities are three rows in one pipeline, not three pipelines.
    /// </remarks>
    private static async Task<(Guid Account, Guid Contact, Guid Stage)> PipelineAsync(
        CrmApplication app)
    {
        var account = await app.Crm.AccountAsync(
            CrmTokens.NorthwindTenant, Lifecycle.Customer, Cancellation);

        var contact = await app.Crm.ContactAsync(CrmTokens.NorthwindTenant, account, Cancellation);

        var (_, stage) = await app.Crm.ProcessAsync(
            CrmTokens.NorthwindTenant, 1, true, Cancellation);

        return (account, contact, stage);
    }

    private static async Task OpportunityAsync(
        CrmApplication app, (Guid Account, Guid Contact, Guid Stage) world, decimal amount, string? outcome)
    {
        var (account, contact, stage) = world;

        await app.Crm.AsTenantAsync(
            CrmTokens.NorthwindTenant,
            """
            INSERT INTO opportunity (opportunity_id, tenant_id, account_id, primary_contact_id,
                                     name, amount, currency, stage_id, probability, expected_close,
                                     owner_id, outcome, stage_entered_at)
            VALUES (gen_random_uuid(), @tenant, @account, @contact, 'Deal', @amount, 'EUR',
                    @stage, 50, current_date, gen_random_uuid(), @outcome, now())
            """,
            Cancellation,
            ("tenant", (object?)CrmTokens.NorthwindTenant),
            ("account", account),
            ("contact", contact),
            ("amount", amount),
            ("stage", stage),
            ("outcome", outcome));
    }

    private static async Task<Guid> WorldAsync(CrmApplication app)
    {
        var response = await app.PostAsync(
            Objects, new DefineObject("site", "Site"), CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var site = (await CrmApplication.ReadAsync<ObjectDefined>(response)).ObjectId;

        await FieldAsync(app, new DefineField(
            null, site, "region", "Region", CustomFieldType.Text, IsRequired: false));
        await FieldAsync(app, new DefineField(
            null, site, "floor_area", "Floor area", CustomFieldType.Number, IsRequired: false));

        return site;
    }

    private static async Task FieldAsync(CrmApplication app, DefineField field)
    {
        (await app.PostAsync(Fields, field, CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK, "declaring '" + field.Name + "' failed.");
    }

    private static async Task RecordAsync(
        CrmApplication app, Guid site, string region, string area)
    {
        (await app.PostAsync(
            Records,
            new CreateRecord(site, new Dictionary<string, string?>
            {
                ["region"] = region,
                ["floor_area"] = area,
            }),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}

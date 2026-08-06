using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// Work a caller submits and comes back for, and what has to be true when the process running it
/// dies halfway.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What separates this from a slow request.</strong> Progress is durable, so a resumed job
/// continues rather than starting again. A row's id is derived from the job and its position, so
/// re-running a chunk writes the same ids and the insert is a no-op — which is what makes the
/// crash <em>between</em> writing a chunk and recording it cost nothing. That crash is the one
/// nobody tests for, because reproducing it needs the process to die in a two-millisecond window;
/// here it is a test, because the sweep can simply be run again from the row it left behind.
/// </para>
/// <para>
/// <strong>A job carries the submitter's authority, not the sweeper's.</strong> The sweep runs
/// minutes later with no caller attached. Deciding on its own authority would let anyone export a
/// field they cannot read by asking a background process to read it.
/// </para>
/// </remarks>
public sealed class BulkJobApiTests
{
    private const string Objects = "/api/v1/crm/custom/objects";
    private const string Fields = "/api/v1/crm/custom/fields";
    private const string Imports = "/api/v1/crm/bulk/imports";
    private const string Exports = "/api/v1/crm/bulk/exports";
    private const string Jobs = "/api/v1/crm/bulk/jobs";
    private const string Queries = "/api/v1/crm/custom/queries";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>An import is accepted at once and done by the sweep.</summary>
    [Fact]
    public async Task AnImportIsAcceptedAtOnceAndRunByTheSweep()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        var submitted = await ImportAsync(app, site, [Row("harbour"), Row("quay")]);

        submitted.Total.ShouldBe(2);

        (await StatusAsync(app, submitted.JobId)).Status.ShouldBe(
            "Pending", "a submission that had already run would be a request, not a job.");

        await SweepAsync(app);

        var status = await StatusAsync(app, submitted.JobId);

        status.Status.ShouldBe("Succeeded");
        status.Processed.ShouldBe(2);
        status.Failed.ShouldBe(0);

        (await RecordsAsync(app, site)).Count.ShouldBe(2);
    }

    /// <summary>Re-running a chunk writes the same rows rather than a second copy of them.</summary>
    /// <remarks>
    /// <strong>The reliability claim this whole design rests on.</strong> A sweeper that dies
    /// between writing a chunk and recording it leaves the job exactly as this test leaves it —
    /// claimed, its rows written, its progress unrecorded — and the next pass must not duplicate
    /// them. Derived ids are what make that true; minted ids would double every row in the chunk.
    /// </remarks>
    [Fact]
    public async Task ReRunningAChunkDoesNotWriteItsRowsTwice()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        var submitted = await ImportAsync(app, site, [Row("harbour"), Row("quay")]);

        await SweepAsync(app);

        // The crash, after the chunk's rows are in the table: the progress that would have been
        // recorded is thrown away, which is exactly what the job's row looks like when a sweeper
        // dies between the last write and the update. The next pass re-runs the whole chunk.
        await app.Crm.AsTenantAsync(
            CrmTokens.NorthwindTenant,
            "UPDATE bulk_job SET processed = 0, status = 'Pending', finished_at = NULL "
            + "WHERE job_id = '" + submitted.JobId + "'",
            Cancellation);

        await SweepAsync(app);

        (await RecordsAsync(app, site)).Count.ShouldBe(
            2, "the chunk was written twice, so every row of every resumed job is duplicated.");
    }

    /// <summary>A job larger than a chunk makes progress and is resumed.</summary>
    [Fact]
    public async Task AJobLargerThanAChunkIsFinishedAcrossPasses()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        var rows = Enumerable
            .Range(0, BulkLimits.Chunk + 5)
            .Select(index => Row("site-" + index))
            .ToList();

        var submitted = await ImportAsync(app, site, rows);

        await SweepAsync(app);

        var partway = await StatusAsync(app, submitted.JobId);

        partway.Status.ShouldBe("Pending", "a sweep that finished the job held the queue.");
        partway.Processed.ShouldBe(BulkLimits.Chunk);

        await SweepAsync(app);

        var finished = await StatusAsync(app, submitted.JobId);

        finished.Status.ShouldBe("Succeeded");
        finished.Processed.ShouldBe(rows.Count);
        (await RecordsAsync(app, site)).Count.ShouldBe(rows.Count);
    }

    /// <summary>Bad rows are reported by position and the good ones are still imported.</summary>
    /// <remarks>
    /// Refusing the batch would make a caller resubmit the rows that were fine, and reporting
    /// "3 rows failed" without saying which three is an import somebody redoes by hand.
    /// </remarks>
    [Fact]
    public async Task ABadRowIsReportedByPositionAndTheRestAreImported()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        var submitted = await ImportAsync(app, site, [
            Row("harbour"),
            new Dictionary<string, string?> { ["label"] = "quay", ["floor_area"] = "not a number" },
            Row("wharf"),
        ]);

        await SweepAsync(app);

        var status = await StatusAsync(app, submitted.JobId);

        status.Status.ShouldBe(
            "Succeeded", "a spreadsheet with one bad line is not a failed import.");

        status.Failed.ShouldBe(1);
        status.Errors.ShouldHaveSingleItem().Ordinal.ShouldBe(
            1, "the ordinal is how a caller finds the line to fix.");

        (await RecordsAsync(app, site)).Count.ShouldBe(2);
    }

    /// <summary>A job nothing can process is given up on rather than retried forever.</summary>
    [Fact]
    public async Task AJobThatCannotProgressIsGivenUpOn()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        var submitted = await ImportAsync(app, site, [Row("harbour")]);

        await app.Crm.AsTenantAsync(
            CrmTokens.NorthwindTenant,
            "UPDATE bulk_job SET attempts = " + BulkLimits.MaxAttempts
            + " WHERE job_id = '" + submitted.JobId + "'",
            Cancellation);

        await SweepAsync(app);

        (await StatusAsync(app, submitted.JobId)).Status.ShouldBe(
            "Failed", "a job at its limit is claimed forever unless something abandons it.");
    }

    /// <summary>An export comes back as a document, redacted for who submitted it.</summary>
    /// <remarks>
    /// <strong>Not for who fetched it.</strong> The sweep decides on the scopes frozen onto the
    /// job; a sweeper deciding on its own authority is how a reporting tool becomes a way to read
    /// what its users may not.
    /// </remarks>
    [Fact]
    public async Task AnExportIsRedactedForWhoSubmittedIt()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await ObjectAsync(app, "site");

        await FieldAsync(app, new DefineField(
            null, site, "label", "Label", CustomFieldType.Text, IsRequired: false));
        await FieldAsync(app, new DefineField(
            null, site, "margin", "Margin", CustomFieldType.Text, IsRequired: false,
            ReadPermission: "crm.admin"));

        (await app.PostAsync(
            "/api/v1/crm/custom/records",
            new CreateRecord(site, new Dictionary<string, string?>
            {
                ["label"] = "harbour",
                ["margin"] = "0.42",
            }),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // Submitted by a representative, who may not read `margin`.
        var response = await app.PostAsync(
            Exports, new SubmitExport(site, null), CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var submitted = await CrmApplication.ReadAsync<JobSubmitted>(response);

        await SweepAsync(app);

        var status = await StatusAsync(app, submitted.JobId);

        status.Status.ShouldBe("Succeeded");

        var row = status.Rows.ShouldNotBeNull().ShouldHaveSingleItem();

        row.Values["margin"].ShouldBe(
            "[redacted]", "the sweep read the field on behalf of somebody who cannot.");

        row.Values["label"].ShouldBe("harbour");
    }

    /// <summary>An import of no rows is refused rather than queued.</summary>
    [Fact]
    public async Task AnImportOfNoRowsIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        var response = await app.PostAsync(Imports, new SubmitImport(site, []), CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString().ShouldBe("crm.bulk_no_rows");
    }

    /// <summary>One tenant cannot read another's job.</summary>
    [Fact]
    public async Task ATenantCannotReadAnothersJob()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        var submitted = await ImportAsync(app, site, [Row("harbour")]);

        var response = await app.PostAsync(
            Jobs, new ReadJob(submitted.JobId), CrmTokens.Contoso, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------------------- fixtures

    private static Dictionary<string, string?> Row(string label) =>
        new() { ["label"] = label };

    private static async Task SweepAsync(CrmApplication app)
    {
        var swept = await app.SweepJobsAsync(CrmTokens.NorthwindTenant);

        swept.IsSuccess.ShouldBeTrue(swept.Error?.Code ?? "the sweep failed with no error.");
    }

    private static async Task<JobSubmitted> ImportAsync(
        CrmApplication app, Guid site, IReadOnlyList<IReadOnlyDictionary<string, string?>> rows)
    {
        var response = await app.PostAsync(
            Imports, new SubmitImport(site, rows), CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<JobSubmitted>(response);
    }

    private static async Task<JobStatus> StatusAsync(CrmApplication app, Guid job)
    {
        var response = await app.PostAsync(
            Jobs, new ReadJob(job), CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<JobStatus>(response);
    }

    private static async Task<IReadOnlyList<RecordView>> RecordsAsync(CrmApplication app, Guid site)
    {
        var response = await app.PostAsync(
            Queries, new QueryRecords(site, null, null, QueryLimits.Max), CrmTokens.Northwind,
            idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await CrmApplication.ReadAsync<RecordPage>(response)).Records;
    }

    private static async Task<Guid> WorldAsync(CrmApplication app)
    {
        var site = await ObjectAsync(app, "site");

        await FieldAsync(app, new DefineField(
            null, site, "label", "Label", CustomFieldType.Text, IsRequired: false));
        await FieldAsync(app, new DefineField(
            null, site, "floor_area", "Floor area", CustomFieldType.Number, IsRequired: false));

        return site;
    }

    private static async Task<Guid> ObjectAsync(CrmApplication app, string name)
    {
        var response = await app.PostAsync(
            Objects, new DefineObject(name, name), CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await CrmApplication.ReadAsync<ObjectDefined>(response)).ObjectId;
    }

    private static async Task<Guid> FieldAsync(CrmApplication app, DefineField field)
    {
        var response = await app.PostAsync(Fields, field, CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "declaring '" + field.Name + "' failed.");

        return (await CrmApplication.ReadAsync<FieldDefined>(response)).FieldId;
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}

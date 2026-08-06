using System.Text.Json;
using FlowX;
using FlowX.Runtime;

namespace Crm;

/// <summary>
/// Accepts a bulk import or export and comes straight back.
/// </summary>
/// <remarks>
/// <strong>The scopes are frozen onto the job here.</strong> The sweep runs minutes later with no
/// caller attached, and it must decide on what the submitter held. A sweeper deciding on its own
/// authority would let anyone export a field they cannot read by asking a background process to
/// read it — which is the shape of most reporting-tool data leaks.
/// </remarks>
[Capability("crm.bulk.submit", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write")]
public sealed class SubmitBulkJob : ICapability<SubmitJob, JobSubmitted>
{
    private readonly CustomSchemaStore _schema;
    private readonly BulkJobStore _jobs;

    /// <summary>Creates the capability.</summary>
    /// <param name="schema">Checks the object exists before a job is queued against it.</param>
    /// <param name="jobs">Records the job.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public SubmitBulkJob(CustomSchemaStore schema, BulkJobStore jobs)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(jobs);

        _schema = schema;
        _jobs = jobs;
    }

    /// <inheritdoc />
    public async ValueTask<Result<JobSubmitted>> ExecuteAsync(
        SubmitJob input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (input.Import is null == input.Export is null)
        {
            return Result.Fail<JobSubmitted>(BulkErrors.AskForOneOrTheOther());
        }

        var target = input.Import?.Target ?? input.Export!.Target;

        if (input.Import is { } import)
        {
            if (import.Rows.Count == 0)
            {
                return Result.Fail<JobSubmitted>(BulkErrors.NoRows());
            }

            if (import.Rows.Count > BulkLimits.MaxRows)
            {
                return Result.Fail<JobSubmitted>(BulkErrors.TooManyRows(import.Rows.Count));
            }
        }

        // Checked at submission as well as at the chunk, because a job queued against an object
        // that does not exist is a job a caller waits a minute to be told about.
        if (!await _schema.HasObjectAsync(ctx.TenantId, target, ct).ConfigureAwait(false))
        {
            return Result.Fail<JobSubmitted>(CustomSchemaErrors.ObjectNotFound(target));
        }

        var id = ctx.NewId();

        // An export's total is one document. Counting its records instead would be a number taken
        // before the export ran, and a progress bar that ends somewhere other than where it said.
        var (kind, total, request) = input.Import is { } rows
            ? ("Import", rows.Rows.Count, JsonSerializer.Serialize(rows, CrmJsonContext.Default.SubmitImport))
            : ("Export", 1, JsonSerializer.Serialize(input.Export!, CrmJsonContext.Default.SubmitExport));

        await _jobs
            .SubmitAsync(ctx.TenantId, id, kind, target, total, input.Scopes, request, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return Result.Ok(new JobSubmitted(id, total));
    }
}

/// <summary>
/// Tells a caller how their job is getting on, and hands back what it produced.
/// </summary>
[Capability("crm.bulk.read", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class ReadBulkJob : ICapability<ReadJobFor, JobStatus>
{
    private readonly BulkJobStore _jobs;

    /// <summary>Creates the capability.</summary>
    /// <param name="jobs">Reads the job.</param>
    /// <exception cref="ArgumentNullException"><paramref name="jobs"/> is null.</exception>
    public ReadBulkJob(BulkJobStore jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        _jobs = jobs;
    }

    /// <inheritdoc />
    public async ValueTask<Result<JobStatus>> ExecuteAsync(
        ReadJobFor input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var job = await _jobs
            .ReadAsync(ctx.TenantId, input.Request.JobId, ct)
            .ConfigureAwait(false);

        if (job is null)
        {
            return Result.Fail<JobStatus>(BulkErrors.JobNotFound(input.Request.JobId));
        }

        // Already redacted by the sweep, for the scopes the submitter held. Read back as it was
        // stored rather than masked again here: masking twice would be a second rule to keep in
        // step, and masking here instead would make the document depend on who fetched it —
        // which is not what "the job that ran" means.
        var rows = job.Result is { } document
            ? JsonSerializer.Deserialize(document, CrmJsonContext.Default.ListRecordView)
            : null;

        return Result.Ok(new JobStatus(
            input.Request.JobId,
            job.Kind,
            job.Status,
            job.Total,
            job.Processed,
            job.Failed,
            job.Errors,
            rows));
    }
}

/// <summary>
/// Advances one job by one chunk, every minute, per tenant.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One chunk and not one job.</strong> A sweep that ran a job to completion would hold a
/// tenant's queue against its largest import; one that advances a chunk leaves the job
/// <c>Pending</c> and comes back, so a ten-thousand-row import and a ten-row one make progress in
/// the same minute.
/// </para>
/// <para>
/// <strong>An imported row's id is derived from the job and its ordinal.</strong> That is what
/// makes a crash between writing a chunk and recording it cost nothing: the re-run writes the same
/// ids and the insert is a no-op. Minting ids instead would duplicate every row in the chunk, and
/// it is the crash nobody tests for because it needs the process to die in a two-millisecond
/// window.
/// </para>
/// </remarks>
[Capability("crm.bulk.sweep", Version = "1.0.0", Authorization = Authorization.Internal)]
public sealed class SweepBulkJobs : ICapability<ScheduledFire, JobsSwept>
{
    private readonly BulkJobStore _jobs;
    private readonly CustomSchemaStore _schema;
    private readonly FieldPolicyStore _policy;
    private readonly FormulaStore _formulas;
    private readonly QueryStore _queries;

    /// <summary>Creates the capability.</summary>
    /// <param name="jobs">Claims the work and records the outcome.</param>
    /// <param name="schema">Reads the declarations.</param>
    /// <param name="policy">Enforces the rules an imported row has to pass.</param>
    /// <param name="formulas">Computes what an imported row derives.</param>
    /// <param name="queries">Reads the records an export is of.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public SweepBulkJobs(
        BulkJobStore jobs,
        CustomSchemaStore schema,
        FieldPolicyStore policy,
        FormulaStore formulas,
        QueryStore queries)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(formulas);
        ArgumentNullException.ThrowIfNull(queries);

        _jobs = jobs;
        _schema = schema;
        _policy = policy;
        _formulas = formulas;
        _queries = queries;
    }

    /// <inheritdoc />
    public async ValueTask<Result<JobsSwept>> ExecuteAsync(
        ScheduledFire input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        // Given up on before anything is claimed, so a job at its limit is not claimed one last
        // time only to be abandoned by the next pass.
        var abandoned = await _jobs
            .AbandonAsync(ctx.TenantId, BulkLimits.MaxAttempts, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        var claimed = await _jobs
            .ClaimAsync(ctx.TenantId, BulkLimits.MaxAttempts, ct)
            .ConfigureAwait(false);

        if (claimed is null)
        {
            return Result.Ok(new JobsSwept(0, 0, abandoned, input.OccurrenceAt));
        }

        var declared = await _schema
            .FieldsForAsync(ctx.TenantId, claimed.Target, ct)
            .ConfigureAwait(false);

        var (advanced, failed, result) = claimed.Kind == "Import"
            ? await ImportAsync(claimed, declared, ctx, ct).ConfigureAwait(false)
            : await ExportAsync(claimed, declared, ctx, ct).ConfigureAwait(false);

        var processed = claimed.Processed + advanced;

        await _jobs
            .AdvanceAsync(ctx.TenantId, claimed.JobId, processed, failed, result, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return Result.Ok(new JobsSwept(
            advanced, processed >= claimed.Total ? 1 : 0, abandoned, input.OccurrenceAt));
    }

    private async ValueTask<(int Advanced, int Failed, string? Result)> ImportAsync(
        ClaimedJob claimed,
        IReadOnlyDictionary<string, CustomFieldRow> declared,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        var submitted = JsonSerializer.Deserialize(
            claimed.Request, CrmJsonContext.Default.SubmitImport);

        var rows = submitted?.Rows ?? [];
        var last = Math.Min(claimed.Processed + BulkLimits.Chunk, rows.Count);
        var refusals = new List<JobRowError>();

        for (var ordinal = claimed.Processed; ordinal < last; ordinal++)
        {
            // The derived id, and the reason the whole chunk is safe to re-run.
            var id = DerivedIdentity.From(
                claimed.JobId.ToString("N"),
                ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var refused = await RecordWriter
                .WriteAsync(
                    _schema, _policy, _formulas, id, claimed.Target,
                    declared, rows[ordinal], claimed.Scopes, ctx, ct)
                .ConfigureAwait(false);

            if (refused is { } fault)
            {
                refusals.Add(new JobRowError(ordinal, fault.Message));
            }
        }

        await _jobs.RecordErrorsAsync(ctx.TenantId, claimed.JobId, refusals, ct).ConfigureAwait(false);

        // The failure count is recomputed from the job's own error rows rather than added up
        // across passes, because a re-run chunk reports its refusals again and adding would count
        // them twice.
        var stored = await _jobs.ReadAsync(ctx.TenantId, claimed.JobId, ct).ConfigureAwait(false);

        return (last - claimed.Processed, stored?.Errors.Count ?? refusals.Count, null);
    }

    private async ValueTask<(int Advanced, int Failed, string? Result)> ExportAsync(
        ClaimedJob claimed,
        IReadOnlyDictionary<string, CustomFieldRow> declared,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        var submitted = JsonSerializer.Deserialize(
            claimed.Request, CrmJsonContext.Default.SubmitExport);

        var rows = await _queries
            .RecordsAsync(
                ctx.TenantId, claimed.Target, submitted?.Filter, null,
                BulkLimits.MaxExported, null, ct)
            .ConfigureAwait(false);

        var redacted = new SortedSet<string>(StringComparer.Ordinal);

        // Redacted for what the submitter held, which is what `scopes` on the job is for.
        var document = rows
            .Select(row => new RecordView(
                row.Id,
                CustomFieldPolicy.Mask(
                    declared, CustomValues.FromJson(row.Values), claimed.Scopes, redacted)))
            .ToList();

        return (1, 0, JsonSerializer.Serialize(document, CrmJsonContext.Default.ListRecordView));
    }
}

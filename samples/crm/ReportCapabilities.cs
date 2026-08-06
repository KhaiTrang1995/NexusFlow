using FlowX;

namespace Crm;

/// <summary>
/// Saves a report, having checked it against the vocabulary of its source.
/// </summary>
/// <remarks>
/// <strong>Checked here rather than when it is run.</strong> A report that names a dimension its
/// source does not have would otherwise fail on somebody's dashboard at nine in the morning, a
/// week after the person who built it moved on — and the failure would look like a data problem.
/// The point of a closed vocabulary is that the mistake is a refusal at declaration.
/// </remarks>
[Capability("crm.report.define", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin")]
public sealed class DefineCrmReport : ICapability<DefineReport, ReportDefined>
{
    private readonly ReportStore _reports;
    private readonly CustomSchemaStore _schema;

    /// <summary>Creates the capability.</summary>
    /// <param name="reports">Saves the report.</param>
    /// <param name="schema">Checks a custom object's fields.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public DefineCrmReport(ReportStore reports, CustomSchemaStore schema)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(schema);

        _reports = reports;
        _schema = schema;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ReportDefined>> ExecuteAsync(
        DefineReport input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (!CustomValues.IsUsableName(input.Name))
        {
            return Result.Fail<ReportDefined>(CustomSchemaErrors.NameIsNotUsable(input.Name));
        }

        if ((input.Source == ReportSource.CustomObject) != (input.Target is not null))
        {
            return Result.Fail<ReportDefined>(ReportErrors.SourceAndTargetDisagree());
        }

        if ((input.Measure == ReportMeasure.Count) != (input.MeasureOf is null))
        {
            return Result.Fail<ReportDefined>(ReportErrors.MeasureNeedsItsField(input.Measure));
        }

        var refused = input.Source == ReportSource.CustomObject
            ? await CustomFieldsExistAsync(input, ctx, ct).ConfigureAwait(false)
            : BuiltInNamesExist(input);

        if (refused is { } fault)
        {
            return Result.Fail<ReportDefined>(fault);
        }

        var id = await _reports
            .SaveReportAsync(ctx.TenantId, ctx.NewId(), input, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return id is { } saved
            ? Result.Ok(new ReportDefined(saved))
            : Result.Fail<ReportDefined>(CustomSchemaErrors.NameIsTaken(input.Name));
    }

    private static Error? BuiltInNamesExist(DefineReport input)
    {
        if (!ReportVocabulary.Dimensions(input.Source).Contains(input.Dimension, StringComparer.Ordinal))
        {
            return ReportErrors.DimensionIsNotOfSource(input.Source, input.Dimension);
        }

        return input.MeasureOf is { } field
            && !ReportVocabulary.Measures(input.Source).Contains(field, StringComparer.Ordinal)
            ? ReportErrors.MeasureIsNotOfSource(input.Source, field)
            : null;
    }

    private async ValueTask<Error?> CustomFieldsExistAsync(
        DefineReport input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        if (!await _schema.HasObjectAsync(ctx.TenantId, input.Target!.Value, ct).ConfigureAwait(false))
        {
            return CustomSchemaErrors.ObjectNotFound(input.Target.Value);
        }

        var declared = await _schema
            .FieldsForAsync(ctx.TenantId, input.Target.Value, ct)
            .ConfigureAwait(false);

        if (!declared.ContainsKey(input.Dimension))
        {
            return CustomSchemaErrors.FieldNotDeclared(input.Dimension);
        }

        return input.MeasureOf is { } field && !declared.ContainsKey(field)
            ? CustomSchemaErrors.FieldNotDeclared(field)
            : null;
    }
}

/// <summary>
/// Runs a saved report.
/// </summary>
/// <remarks>
/// <strong><c>crm.read</c> and not <c>crm.admin</c>.</strong> Building a report is an
/// administrator's; reading one is everybody's, which is the point of saving it. What a report
/// does not do is bypass field-level security — it aggregates, so no value of any row leaves,
/// only a count or a total over the rows this tenant's policies already admit.
/// </remarks>
[Capability("crm.report.run", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class RunCrmReport : ICapability<RunReport, ReportResult>
{
    private readonly ReportStore _reports;

    /// <summary>Creates the capability.</summary>
    /// <param name="reports">Reads and runs the report.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reports"/> is null.</exception>
    public RunCrmReport(ReportStore reports)
    {
        ArgumentNullException.ThrowIfNull(reports);

        _reports = reports;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ReportResult>> ExecuteAsync(
        RunReport input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var report = await _reports.ReportAsync(ctx.TenantId, input.Name, ct).ConfigureAwait(false);

        if (report is null)
        {
            return Result.Fail<ReportResult>(ReportErrors.ReportNotFound(input.Name));
        }

        var groups = await _reports.RunAsync(ctx.TenantId, report, ct).ConfigureAwait(false);

        return Result.Ok(new ReportResult(input.Name, report.Label, report.Measure, groups));
    }
}

/// <summary>
/// Saves a dashboard: an ordered set of saved reports.
/// </summary>
/// <remarks>
/// <strong>The tiles name reports rather than repeating them.</strong> A dashboard that carried
/// its own copy of each definition would be a second place every report lives, and the copies
/// would be the ones nobody updates. The foreign key is <c>ON DELETE RESTRICT</c>, so an
/// administrator deleting a report is told what is using it instead of leaving a panel that
/// renders an error every morning.
/// </remarks>
[Capability("crm.dashboard.define", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin")]
public sealed class DefineCrmDashboard : ICapability<DefineDashboard, DashboardDefined>
{
    private readonly ReportStore _reports;

    /// <summary>Creates the capability.</summary>
    /// <param name="reports">Reads the reports and saves the dashboard.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reports"/> is null.</exception>
    public DefineCrmDashboard(ReportStore reports)
    {
        ArgumentNullException.ThrowIfNull(reports);

        _reports = reports;
    }

    /// <inheritdoc />
    public async ValueTask<Result<DashboardDefined>> ExecuteAsync(
        DefineDashboard input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (!CustomValues.IsUsableName(input.Name))
        {
            return Result.Fail<DashboardDefined>(CustomSchemaErrors.NameIsNotUsable(input.Name));
        }

        if (input.Reports.Count == 0)
        {
            return Result.Fail<DashboardDefined>(ReportErrors.NoTiles());
        }

        if (input.Reports.Count > ReportLimits.MaxTiles)
        {
            return Result.Fail<DashboardDefined>(ReportErrors.TooManyTiles(input.Reports.Count));
        }

        var tiles = new List<Guid>(input.Reports.Count);

        // Resolved before anything is written, so a dashboard naming one report that does not
        // exist is refused rather than half-saved.
        foreach (var name in input.Reports)
        {
            if (await _reports.ReportAsync(ctx.TenantId, name, ct).ConfigureAwait(false)
                is not { } report)
            {
                return Result.Fail<DashboardDefined>(ReportErrors.ReportNotFound(name));
            }

            tiles.Add(report.ReportId);
        }

        var id = await _reports
            .SaveDashboardAsync(
                ctx.TenantId, ctx.NewId(), input.Name, input.Label, tiles, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return id is { } saved
            ? Result.Ok(new DashboardDefined(saved))
            : Result.Fail<DashboardDefined>(CustomSchemaErrors.NameIsTaken(input.Name));
    }
}

/// <summary>
/// Runs every report on a dashboard, in the order the dashboard puts them.
/// </summary>
/// <remarks>
/// <strong>One request, not one per tile.</strong> A screen of twelve panels that fetches twelve
/// times is twelve authentications, twelve connections and twelve chances for half a dashboard to
/// render — and on a phone, twelve round trips over the same slow link.
/// </remarks>
[Capability("crm.dashboard.run", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class RunCrmDashboard : ICapability<RunDashboard, DashboardResult>
{
    private readonly ReportStore _reports;

    /// <summary>Creates the capability.</summary>
    /// <param name="reports">Reads the dashboard and runs its tiles.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reports"/> is null.</exception>
    public RunCrmDashboard(ReportStore reports)
    {
        ArgumentNullException.ThrowIfNull(reports);

        _reports = reports;
    }

    /// <inheritdoc />
    public async ValueTask<Result<DashboardResult>> ExecuteAsync(
        RunDashboard input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var run = await _reports
            .RunDashboardAsync(ctx.TenantId, input.Name, ct)
            .ConfigureAwait(false);

        return run is { } found
            ? Result.Ok(new DashboardResult(input.Name, found.Label, found.Tiles))
            : Result.Fail<DashboardResult>(ReportErrors.DashboardNotFound(input.Name));
    }
}

using FlowX;

namespace Crm;

// -------------------------------------------------------------------------------- the vocabulary

/// <summary>What a report can be about.</summary>
/// <remarks>
/// <strong>Four, and adding a fifth is a code change on purpose.</strong> Each built-in source has
/// real columns, so its dimensions and its measures are a fixed list this build can check a saved
/// report against. The alternative — letting a report name any column — is a query language, and
/// a query language is a parser, a sandbox and an injection surface for the sake of a question
/// almost nobody asks.
/// </remarks>
public enum ReportSource
{
    /// <summary>The sales pipeline.</summary>
    Opportunity,

    /// <summary>The marketing funnel.</summary>
    Lead,

    /// <summary>The operations queue.</summary>
    Activity,

    /// <summary>Whatever this tenant invented.</summary>
    CustomObject,
}

/// <summary>How the rows of a group are reduced to a number.</summary>
public enum ReportMeasure
{
    /// <summary>How many rows. Takes no field.</summary>
    Count,

    /// <summary>Their total.</summary>
    Sum,

    /// <summary>Their mean.</summary>
    Average,

    /// <summary>The smallest.</summary>
    Min,

    /// <summary>The largest.</summary>
    Max,
}

/// <summary>Which dimensions and measures each built-in source has.</summary>
/// <remarks>
/// <strong>The list lives here and not in the SQL.</strong> The statement carries the same names
/// in a <c>CASE</c>, so a name added in one place and not the other produces a report that groups
/// everything under null — which looks like a data problem and is not. This is what the saving
/// path checks against, so that mistake is a refusal at declaration instead.
/// </remarks>
public static class ReportVocabulary
{
    /// <summary>What a source may be grouped by.</summary>
    /// <param name="source">Which source.</param>
    /// <returns>The dimensions, or empty for <see cref="ReportSource.CustomObject"/>.</returns>
    public static IReadOnlyList<string> Dimensions(ReportSource source) => source switch
    {
        ReportSource.Opportunity => ["Outcome", "Currency", "Probability"],
        ReportSource.Lead => ["Source", "Status"],
        ReportSource.Activity => ["Kind", "Status", "RelatesToKind"],
        _ => [],
    };

    /// <summary>What a source may aggregate over.</summary>
    /// <param name="source">Which source.</param>
    /// <returns>The numeric fields, or empty for <see cref="ReportSource.CustomObject"/>.</returns>
    public static IReadOnlyList<string> Measures(ReportSource source) => source switch
    {
        ReportSource.Opportunity => ["Amount", "Probability"],
        ReportSource.Lead => ["Score"],
        ReportSource.Activity => ["EscalationCount"],
        _ => [],
    };
}

// -------------------------------------------------------------------------------- what is asked

/// <summary>Saves a report an administrator built.</summary>
/// <param name="Name">What to run it by.</param>
/// <param name="Label">What to show a person.</param>
/// <param name="Source">What it is about.</param>
/// <param name="Target">The object, for a <see cref="ReportSource.CustomObject"/> report.</param>
/// <param name="Dimension">What to group by.</param>
/// <param name="Measure">How to reduce each group.</param>
/// <param name="MeasureOf">What to aggregate. Null for <see cref="ReportMeasure.Count"/>.</param>
public sealed record DefineReport(
    string Name,
    string Label,
    ReportSource Source,
    Guid? Target,
    string Dimension,
    ReportMeasure Measure,
    string? MeasureOf);

/// <summary>The report was saved.</summary>
/// <param name="ReportId">Its id.</param>
public sealed record ReportDefined(Guid ReportId);

/// <summary>Runs a saved report.</summary>
/// <param name="Name">Which one.</param>
public sealed record RunReport(string Name);

/// <summary>Saves a dashboard: an ordered set of reports.</summary>
/// <param name="Name">What to run it by.</param>
/// <param name="Label">What to show a person.</param>
/// <param name="Reports">The reports it shows, in the order they appear.</param>
public sealed record DefineDashboard(string Name, string Label, IReadOnlyList<string> Reports);

/// <summary>The dashboard was saved.</summary>
/// <param name="DashboardId">Its id.</param>
public sealed record DashboardDefined(Guid DashboardId);

/// <summary>Runs every report on a dashboard.</summary>
/// <param name="Name">Which dashboard.</param>
public sealed record RunDashboard(string Name);

// ------------------------------------------------------------------------------- what comes back

/// <summary>One group of a report.</summary>
/// <param name="Dimension">
/// What the group is. <c>"(none)"</c> where the underlying value is null, because a client that
/// receives a null key and one that receives an absent one both draw a bar with no label.
/// </param>
/// <param name="Value">The measure, formatted invariantly.</param>
/// <param name="Rows">How many rows are in the group, whatever the measure was.</param>
public sealed record ReportGroup(string Dimension, string Value, int Rows);

/// <summary>What a report found.</summary>
/// <param name="Name">Which report.</param>
/// <param name="Label">What to show a person.</param>
/// <param name="Measure">How the groups were reduced.</param>
/// <param name="MeasureOf">
/// What was reduced, or null for <see cref="ReportMeasure.Count"/>.
/// <para>
/// <strong>Without it a reader cannot know the unit.</strong> "Sum" says how the groups were
/// reduced and not over what, so a client had the choice of drawing every figure as money — which
/// makes a sum of probabilities read as euros — or as a bare number, which makes a pipeline total
/// read as a tally. The field name is the fact that decides it, and the report already knows it.
/// </para>
/// </param>
/// <param name="Groups">The groups, largest first.</param>
public sealed record ReportResult(
    string Name,
    string Label,
    string Measure,
    string? MeasureOf,
    IReadOnlyList<ReportGroup> Groups);

/// <summary>What a dashboard found.</summary>
/// <param name="Name">Which dashboard.</param>
/// <param name="Label">What to show a person.</param>
/// <param name="Tiles">Its reports' results, in the order the dashboard puts them.</param>
public sealed record DashboardResult(
    string Name,
    string Label,
    IReadOnlyList<ReportResult> Tiles);

// ------------------------------------------------------------------------------- what can go wrong

/// <summary>Refusals the reporting surface can produce.</summary>
public static class ReportErrors
{
    /// <summary>The dimension is not one this source has.</summary>
    /// <param name="source">Which source.</param>
    /// <param name="dimension">What was asked for.</param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// Refused when the report is saved, not when it is run. A report that names a dimension its
    /// source does not have would otherwise fail on somebody's dashboard at nine in the morning,
    /// a week after the person who wrote it moved on.
    /// </remarks>
    public static Error DimensionIsNotOfSource(ReportSource source, string dimension) =>
        new(
            "crm.report_dimension_unknown",
            $"'{dimension}' is not a dimension of {source}. It has: " +
            string.Join(", ", ReportVocabulary.Dimensions(source)) + ".",
            ErrorCategory.Validation);

    /// <summary>The measure field is not one this source has.</summary>
    /// <param name="source">Which source.</param>
    /// <param name="field">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error MeasureIsNotOfSource(ReportSource source, string field) =>
        new(
            "crm.report_measure_unknown",
            $"'{field}' is not something {source} can be aggregated over. It has: " +
            string.Join(", ", ReportVocabulary.Measures(source)) + ".",
            ErrorCategory.Validation);

    /// <summary>A <c>Count</c> named a field, or another measure named none.</summary>
    /// <param name="measure">Which measure.</param>
    /// <returns>The refusal.</returns>
    public static Error MeasureNeedsItsField(ReportMeasure measure) =>
        measure == ReportMeasure.Count
            ? new(
                "crm.report_measure_field_unwanted",
                "Count counts rows and takes no field.",
                ErrorCategory.Validation)
            : new(
                "crm.report_measure_field_missing",
                $"{measure} needs a field to aggregate over.",
                ErrorCategory.Validation);

    /// <summary>A custom-object report named no object, or a built-in one named one.</summary>
    public static Error SourceAndTargetDisagree() =>
        new(
            "crm.report_target_disagrees",
            "A CustomObject report names an object, and the other sources do not.",
            ErrorCategory.Validation);

    /// <summary>The report is not one this tenant has.</summary>
    /// <param name="name">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error ReportNotFound(string name) =>
        new(
            "crm.report_not_found",
            $"No report named '{name}' has been saved for this tenant.",
            ErrorCategory.NotFound);

    /// <summary>The dashboard is not one this tenant has.</summary>
    /// <param name="name">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error DashboardNotFound(string name) =>
        new(
            "crm.dashboard_not_found",
            $"No dashboard named '{name}' has been saved for this tenant.",
            ErrorCategory.NotFound);

    /// <summary>A dashboard named more tiles than one screen carries.</summary>
    /// <param name="tiles">How many were asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error TooManyTiles(int tiles) =>
        new(
            "crm.dashboard_too_many_tiles",
            $"A dashboard carries at most {ReportLimits.MaxTiles} reports, and {tiles} were named. " +
            "Each one is a separate aggregate over the whole tenant.",
            ErrorCategory.Validation);

    /// <summary>A dashboard named no reports.</summary>
    public static Error NoTiles() =>
        new(
            "crm.dashboard_no_tiles",
            "A dashboard of no reports is a blank screen with a name.",
            ErrorCategory.Validation);
}

/// <summary>What the reporting surface will serve.</summary>
public static class ReportLimits
{
    /// <summary>The most groups one report returns.</summary>
    /// <remarks>
    /// A grouped aggregate over a high-cardinality dimension is a chart nobody can read and a
    /// response nobody meant to ask for. Bounded here, and the groups come back largest first, so
    /// what is cut is the tail.
    /// </remarks>
    public const int MaxGroups = 100;

    /// <summary>The most reports one dashboard carries.</summary>
    public const int MaxTiles = 12;

    /// <summary>The placeholder for a group whose dimension is null.</summary>
    public const string NoDimension = "(none)";
}

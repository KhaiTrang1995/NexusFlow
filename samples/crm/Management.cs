using FlowX;

namespace Crm;

// -------------------------------------------------------------------------------- the vocabulary

/// <summary>What a person sees by default, which the tenant sets.</summary>
/// <remarks>
/// <strong>A scope, not a permission.</strong> All three may read; what differs is whose plans are
/// in the total. Without the distinction the only answers available are "mine" and "the whole
/// tenant", and a company with nine sales managers gets neither of the ones it wanted.
/// </remarks>
public enum OrgRole
{
    /// <summary>Sees their own plans.</summary>
    Representative,

    /// <summary>Sees their own and everyone below them in the line.</summary>
    Manager,

    /// <summary>Sees the tenant's.</summary>
    Director,
}

/// <summary>What an account objective is measured in.</summary>
/// <remarks>
/// Closed, because an objective measured in whatever somebody typed is one no two account plans
/// can be compared on — and comparing them is what a quarterly review is.
/// </remarks>
public enum ObjectiveMeasure
{
    /// <summary>Money.</summary>
    Revenue,

    /// <summary>A count of deals.</summary>
    Deals,

    /// <summary>A count of meetings held.</summary>
    Meetings,

    /// <summary>How many of the range they have bought.</summary>
    Products,

    /// <summary>Introductions they have made.</summary>
    Referrals,
}

/// <summary>Where an objective has got to.</summary>
public enum ObjectiveStatus
{
    /// <summary>Nothing has happened yet.</summary>
    NotStarted,

    /// <summary>Something has.</summary>
    InProgress,

    /// <summary>Done.</summary>
    Achieved,

    /// <summary>Given up on, deliberately. Not the same as forgotten.</summary>
    Abandoned,
}

/// <summary>What a person is to a deal.</summary>
public enum StakeholderRole
{
    /// <summary>Can spend the money without asking anybody.</summary>
    EconomicBuyer,

    /// <summary>Wants this to happen and can act.</summary>
    Champion,

    /// <summary>Shapes the decision without making it.</summary>
    Influencer,

    /// <summary>Will live with what is bought.</summary>
    User,

    /// <summary>Does not want this to happen.</summary>
    Blocker,

    /// <summary>Reviews the contract.</summary>
    Legal,

    /// <summary>Runs the buying process.</summary>
    Procurement,

    /// <summary>Decides whether it works.</summary>
    Technical,
}

/// <summary>How a person feels about it.</summary>
public enum Sentiment
{
    /// <summary>Argues for it when nobody from the vendor is in the room.</summary>
    Advocate,

    /// <summary>In favour.</summary>
    Supportive,

    /// <summary>Has not decided, or does not care.</summary>
    Neutral,

    /// <summary>Has doubts.</summary>
    Sceptical,

    /// <summary>Against.</summary>
    Opposed,
}

/// <summary>How bad a risk would be.</summary>
public enum RiskSeverity
{
    /// <summary>Annoying.</summary>
    Low,

    /// <summary>Would cost time.</summary>
    Medium,

    /// <summary>Would cost the deal's date.</summary>
    High,

    /// <summary>Would cost the deal.</summary>
    Critical,
}

/// <summary>What a KPI is computed from.</summary>
/// <remarks>
/// A closed list this build knows how to compute, for the same reason the report sources are
/// closed: the alternative is an expression language, and what a leadership team reviews is five
/// or six numbers that never change, not an arbitrary formula.
/// </remarks>
public enum KpiSource
{
    /// <summary>The sum of opportunities that have not been decided.</summary>
    OpenPipeline,

    /// <summary>The sum of those that were won inside the period.</summary>
    WonRevenue,

    /// <summary>How many leads arrived inside the period.</summary>
    LeadsCaptured,

    /// <summary>How many activities are still open.</summary>
    OpenTasks,

    /// <summary>How many agreed steps are past their date and not done.</summary>
    OverduePlanSteps,
}

/// <summary>Which way is good.</summary>
/// <remarks>
/// Without it, a target of five overdue steps and one of five million in pipeline would be scored
/// the same way, and one of the two answers would be exactly wrong.
/// </remarks>
public enum KpiDirection
{
    /// <summary>More is better.</summary>
    HigherIsBetter,

    /// <summary>Less is better.</summary>
    LowerIsBetter,
}

// -------------------------------------------------------------------------------- what is asked

/// <summary>Places a person in the organisation.</summary>
/// <param name="UserId">Their subject, as their token carries it.</param>
/// <param name="DisplayName">What to show.</param>
/// <param name="Role">What they see by default.</param>
/// <param name="ReportsTo">Their manager's subject, or null at the top.</param>
public sealed record SetOrgMember(
    string UserId,
    string DisplayName,
    OrgRole Role,
    string? ReportsTo);

/// <summary>The person was placed.</summary>
/// <param name="UserId">Whose.</param>
/// <param name="Reports">How many people are below them in the line, at any depth.</param>
public sealed record OrgMemberSet(string UserId, int Reports);

/// <summary>Writes an objective of an account plan.</summary>
/// <param name="Plan">Which plan.</param>
/// <param name="Ordinal">Where it sits.</param>
/// <param name="Description">What is to be achieved.</param>
/// <param name="Measure">In what.</param>
/// <param name="Target">How much.</param>
/// <param name="Status">Where it has got to.</param>
public sealed record SetObjective(
    string Plan,
    int Ordinal,
    string Description,
    ObjectiveMeasure Measure,
    decimal Target,
    ObjectiveStatus Status);

/// <summary>The objective was written.</summary>
/// <param name="Ordinal">Which one.</param>
/// <param name="Outstanding">How many of this plan's objectives are not yet achieved.</param>
public sealed record ObjectiveSet(int Ordinal, int Outstanding);

/// <summary>Puts a person on the relationship map.</summary>
/// <param name="Plan">Which plan.</param>
/// <param name="Contact">Who.</param>
/// <param name="Role">What they are to the deal.</param>
/// <param name="Sentiment">How they feel about it.</param>
/// <param name="Influence">How much they matter, one to five.</param>
public sealed record SetStakeholder(
    string Plan,
    Guid Contact,
    StakeholderRole Role,
    Sentiment Sentiment,
    int Influence);

/// <summary>The map was written.</summary>
/// <param name="Mapped">How many people are on it.</param>
/// <param name="Opposed">
/// How many are <see cref="Sentiment.Sceptical"/> or <see cref="Sentiment.Opposed"/> — the number
/// a coverage review actually asks for.
/// </param>
public sealed record StakeholderSet(int Mapped, int Opposed);

/// <summary>Raises a risk, or closes one.</summary>
/// <param name="Plan">Which plan.</param>
/// <param name="Ordinal">Where it sits.</param>
/// <param name="Description">What could go wrong.</param>
/// <param name="Severity">How bad it would be.</param>
/// <param name="Mitigation">What is being done about it.</param>
/// <param name="IsOpen">Whether it is still a risk.</param>
public sealed record SetRisk(
    string Plan,
    int Ordinal,
    string Description,
    RiskSeverity Severity,
    string Mitigation,
    bool IsOpen);

/// <summary>The risk was written.</summary>
/// <param name="Ordinal">Which one.</param>
/// <param name="Open">How many of this plan's risks are still open.</param>
public sealed record RiskSet(int Ordinal, int Open);

/// <summary>Declares a number the leadership team reviews.</summary>
/// <param name="Name">What to ask for it by.</param>
/// <param name="Label">What to show.</param>
/// <param name="Source">What it is computed from.</param>
/// <param name="Target">What good looks like.</param>
/// <param name="Direction">Which way is good.</param>
public sealed record DefineKpi(
    string Name,
    string Label,
    KpiSource Source,
    decimal Target,
    KpiDirection Direction);

/// <summary>The KPI was declared.</summary>
/// <param name="KpiId">Its id.</param>
public sealed record KpiDefined(Guid KpiId);

/// <summary>Records what was said about a number, when it was said.</summary>
/// <param name="Kpi">Which KPI.</param>
/// <param name="Period">For which period.</param>
/// <param name="Commentary">What was said.</param>
public sealed record ReviewKpi(string Kpi, string Period, string Commentary);

/// <summary>The review was recorded.</summary>
/// <param name="Kpi">Which KPI.</param>
/// <param name="Actual">
/// What the number was when the commentary was written. <strong>Stored, unlike a plan's
/// actual</strong> — a review is a minute of a meeting, and a minute that silently updated itself
/// would not be one.
/// </param>
/// <param name="Status">Whether it met its target, by its direction.</param>
public sealed record KpiReviewed(string Kpi, decimal Actual, string Status);

/// <summary>Who the caller is, read from their claims and never from the body.</summary>
/// <remarks>
/// <strong>The subject comes off the principal.</strong> A request that carried its own user id
/// would let anybody read anybody's roll-up by typing a different name into it — which is the
/// whole reason the tenant is a claim and not a header.
/// </remarks>
public static class Caller
{
    /// <summary>The caller's subject, or the empty string when they have none.</summary>
    /// <param name="principal">Who is calling.</param>
    /// <returns>Their subject.</returns>
    public static string Subject(System.Security.Claims.ClaimsPrincipal? principal) =>
        principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? string.Empty;
}

/// <summary>What a roll-up is given, once the flow has read the caller.</summary>
/// <param name="Period">Which period.</param>
/// <param name="UserId">Whose view it is.</param>
public sealed record ForViewer(string Period, string UserId);

/// <summary>What the review capability is given, once the flow has read the caller.</summary>
/// <param name="Request">What was asked for.</param>
/// <param name="UserId">Who is minuting it.</param>
public sealed record ForReview(ReviewKpi Request, string UserId);

/// <summary>Asks for every KPI, computed for a period.</summary>
/// <param name="Period">Which period.</param>
public sealed record ReadScorecard(string Period);

// ------------------------------------------------------------------------------- what comes back

/// <summary>One KPI, its target and what it actually is.</summary>
/// <param name="Name">Which KPI.</param>
/// <param name="Label">What to show.</param>
/// <param name="Source">What it was computed from.</param>
/// <param name="Target">What good looks like.</param>
/// <param name="Actual">What it is, read live.</param>
/// <param name="Direction">Which way is good.</param>
/// <param name="Status"><c>OnTrack</c> or <c>OffTrack</c>, decided by the direction.</param>
/// <param name="LastCommentary">What was said about it last time, or null.</param>
public sealed record KpiResult(
    string Name,
    string Label,
    string Source,
    decimal Target,
    decimal Actual,
    string Direction,
    string Status,
    string? LastCommentary);

/// <summary>Every KPI for a period.</summary>
/// <param name="Period">Which period.</param>
/// <param name="Kpis">The numbers, off-track first — which is the order a review walks them in.</param>
public sealed record Scorecard(string Period, IReadOnlyList<KpiResult> Kpis);

/// <summary>Who a caller is, and therefore whose plans they see.</summary>
/// <param name="UserId">Their subject.</param>
/// <param name="Role">What the tenant set them to.</param>
/// <param name="Scope">
/// The subjects whose plans they see. One for a representative, their line for a manager, and
/// empty for a director — where empty means "no restriction" rather than "nothing".
/// </param>
public sealed record ViewerScope(string UserId, OrgRole Role, IReadOnlyList<string> Scope);

// ------------------------------------------------------------------------------- what can go wrong

/// <summary>Refusals the management surface can produce.</summary>
public static class ManagementErrors
{
    /// <summary>The manager named is not somebody this tenant has.</summary>
    /// <param name="userId">Who was named.</param>
    /// <returns>The refusal.</returns>
    public static Error MemberNotFound(string userId) =>
        new(
            "crm.org_member_not_found",
            $"'{userId}' has not been placed in this organisation.",
            ErrorCategory.NotFound);

    /// <summary>The line would loop back on itself.</summary>
    /// <param name="userId">Who was being placed.</param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// Refused at the write, because a cycle in a reporting line makes every roll-up below it
    /// either wrong or non-terminating, and a settings screen is where the two-person loop gets
    /// created by accident.
    /// </remarks>
    public static Error ReportingLineWouldLoop(string userId) =>
        new(
            "crm.org_line_would_loop",
            $"Placing '{userId}' there would make the reporting line loop back on itself.",
            ErrorCategory.Conflict);

    /// <summary>The setting is not one this plan's kind has.</summary>
    /// <param name="kind">Which kind the plan is.</param>
    /// <param name="what">What was being written.</param>
    /// <returns>The refusal.</returns>
    public static Error NotOfThisPlanKind(string kind, string what) =>
        new(
            "crm.plan_kind_has_no",
            $"A {kind} plan has no {what}.",
            ErrorCategory.Validation);

    /// <summary>The KPI is not one this tenant has.</summary>
    /// <param name="name">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error KpiNotFound(string name) =>
        new(
            "crm.kpi_not_found",
            $"No KPI named '{name}' has been declared for this tenant.",
            ErrorCategory.NotFound);

    /// <summary>The influence was outside one to five.</summary>
    /// <param name="influence">What was sent.</param>
    /// <returns>The refusal.</returns>
    public static Error InfluenceIsOutOfRange(int influence) =>
        new(
            "crm.stakeholder_influence_range",
            $"Influence is one to five, and {influence} was sent.",
            ErrorCategory.Validation);

    /// <summary>The caller has not been placed in the organisation.</summary>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// Refused rather than defaulted to <see cref="OrgRole.Representative"/>. A default would make
    /// a director whose row was never written see one plan and conclude their organisation had
    /// stopped selling.
    /// </remarks>
    public static Error CallerIsNotInTheOrganisation() =>
        new(
            "crm.caller_not_in_organisation",
            "This caller has not been placed in the organisation, so there is no way to say " +
            "whose plans they see.",
            ErrorCategory.Forbidden);
}

/// <summary>What the management surface accepts.</summary>
public static class ManagementLimits
{
    /// <summary>How deep a reporting line is followed.</summary>
    /// <remarks>
    /// <strong>A cycle in the line cannot be prevented by a constraint</strong> — a three-person
    /// loop is three individually legal rows — so the recursive read is bounded instead. Deeper
    /// than this and an organisation has a data problem, not a hierarchy.
    /// </remarks>
    public const int MaxDepth = 20;
}

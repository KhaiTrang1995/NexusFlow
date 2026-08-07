using FlowX;

namespace Crm;

// -------------------------------------------------------------------------------- what is asked

/// <summary>Reads one plan, whole.</summary>
/// <param name="Name">
/// Its identifier, which is what <see cref="PlanTree"/> hands back for every node — so a client
/// navigates from the tree to the plan without holding an id the tree never gave it.
/// </param>
/// <remarks>
/// <para>
/// <strong>The gap this closes.</strong> A plan could be committed, given objectives, qualified,
/// have steps agreed and risks recorded — and read back only as a row in a roll-up. Everything
/// underneath it was write-only, so the account-plan and deal-plan screens had nothing to draw
/// and drew fixtures instead.
/// </para>
/// <para>
/// <strong>By name and not by id.</strong> A plan is unique per tenant by name; the tree returns
/// the name; a screen navigating to a plan therefore needs nothing it was not already given. An
/// id-keyed read would make the tree's answer insufficient to follow.
/// </para>
/// </remarks>
public sealed record ReadPlan(string Name);

// -------------------------------------------------------------------------------- what comes back

/// <summary>One plan and everything hung off it.</summary>
/// <param name="Name">Its identifier.</param>
/// <param name="Label">What a person sees.</param>
/// <param name="Kind">Account, opportunity or demand.</param>
/// <param name="Period">The period it belongs to.</param>
/// <param name="Owner">Whose it is.</param>
/// <param name="TargetAmount">What it commits, or null for a plan that commits no money.</param>
/// <param name="Currency">The unit of that, or null with it.</param>
/// <param name="Objectives">What it is trying to achieve.</param>
/// <param name="Steps">
/// The mutual action plan. An overdue step is the earliest signal a deal is not moving, which is
/// why it is a list with dates rather than a note.
/// </param>
/// <param name="Risks">What could stop it, and what is being done.</param>
/// <param name="Qualification">
/// The eight elements, answered or not. <strong>Never a self-scored rating</strong>: a number a
/// representative chooses is a number they choose to be comfortable with.
/// </param>
/// <param name="Stakeholders">Who matters, and what they think. Empty for a plan with no account.</param>
public sealed record PlanDetail(
    string Name,
    string Label,
    string Kind,
    string Period,
    string Owner,
    decimal? TargetAmount,
    string? Currency,
    IReadOnlyList<PlanObjectiveRow> Objectives,
    IReadOnlyList<PlanStepRow> Steps,
    IReadOnlyList<PlanRiskRow> Risks,
    IReadOnlyList<PlanQualificationRow> Qualification,
    IReadOnlyList<PlanStakeholderRow> Stakeholders);

/// <summary>One thing the plan is trying to achieve.</summary>
/// <param name="Ordinal">Where it sits in the list.</param>
/// <param name="Description">What it is.</param>
/// <param name="Measure">What it is counted in.</param>
/// <param name="Target">How much.</param>
/// <param name="Status">Where it has got to.</param>
public sealed record PlanObjectiveRow(
    int Ordinal,
    string Description,
    string Measure,
    decimal Target,
    string Status);

/// <summary>One agreed step.</summary>
/// <param name="Ordinal">Where it sits in the list.</param>
/// <param name="Description">What was agreed.</param>
/// <param name="DueOn">When.</param>
/// <param name="IsComplete">Whether it was done.</param>
/// <param name="IsOverdue">
/// Whether its date has passed and it is not done. <strong>Decided by the server</strong>: a
/// client comparing a date against its own clock reports a step as overdue in one time zone and
/// not in another, on the same afternoon.
/// </param>
public sealed record PlanStepRow(
    int Ordinal,
    string Description,
    DateOnly DueOn,
    bool IsComplete,
    bool IsOverdue);

/// <summary>One risk, and what is being done about it.</summary>
/// <param name="Ordinal">Where it sits in the list.</param>
/// <param name="Description">What could stop it.</param>
/// <param name="Severity">How bad it would be.</param>
/// <param name="Mitigation">What is being done.</param>
/// <param name="IsOpen">Whether it is still live.</param>
public sealed record PlanRiskRow(
    int Ordinal,
    string Description,
    string Severity,
    string Mitigation,
    bool IsOpen);

/// <summary>One element of the qualification, answered or not.</summary>
/// <param name="Element">Which element.</param>
/// <param name="IsAnswered">Whether anybody knows.</param>
/// <param name="Note">What they know, or why they do not.</param>
public sealed record PlanQualificationRow(string Element, bool IsAnswered, string Note);

/// <summary>One person who matters.</summary>
/// <param name="ContactId">Which contact.</param>
/// <param name="FullName">Their name, joined from the contact.</param>
/// <param name="Role">What they do in the decision.</param>
/// <param name="Sentiment">What they think of it.</param>
/// <param name="Influence">How much it counts, one to five.</param>
public sealed record PlanStakeholderRow(
    Guid ContactId,
    string FullName,
    string Role,
    string Sentiment,
    int Influence);

// -------------------------------------------------------------------------------- what can go wrong

/// <summary>Refusals the plan read can produce.</summary>
public static class PlanReadErrors
{
    /// <summary>This tenant has no plan by that name.</summary>
    /// <param name="name">What was asked for.</param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// Not found rather than forbidden, and the two are the same answer here on purpose: row-level
    /// security means another tenant's plan is invisible rather than refused, so a caller cannot
    /// learn from the status code whether a name exists somewhere else.
    /// </remarks>
    public static Error PlanNotFound(string name) =>
        new("crm.plan_not_found", $"No plan named '{name}'.", ErrorCategory.NotFound);
}

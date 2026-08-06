using FlowX;

namespace Crm;

// -------------------------------------------------------------------------------- the vocabulary

/// <summary>What a commitment is about.</summary>
public enum PlanKind
{
    /// <summary>A named account, and what it is expected to be worth this period.</summary>
    Account,

    /// <summary>One deal, how well it is understood, and what both sides agreed to do.</summary>
    Opportunity,

    /// <summary>How much demand one channel is expected to produce.</summary>
    MarketingLead,

    /// <summary>
    /// A set of plans, and a number of its own. A division, a region, a segment — whatever a group
    /// slices itself by.
    /// </summary>
    /// <remarks>
    /// <strong>The level that makes the gap arithmetic recursive.</strong> A portfolio's children
    /// are plans, so "does the sum of my children add up to my number" is asked at every level
    /// rather than only at the top — which is the difference between a company with one sales team
    /// and a group with divisions.
    /// </remarks>
    Portfolio,

    /// <summary>
    /// What an operations team commits to doing, counted in activities.
    /// </summary>
    /// <remarks>
    /// Measured in one of the four activity kinds the schema already records. An operations plan
    /// measured in anything else is a plan nothing can report against.
    /// </remarks>
    Operation,
}

/// <summary>What has to be known about a deal before anybody should believe its date.</summary>
/// <remarks>
/// <para>
/// <strong>A closed list, and the reason is comparison.</strong> A qualification checklist whose
/// items differ per deal is a checklist no two managers can hold against each other, and holding
/// them against each other across a pipeline is the whole point. These eight are the ones a large
/// B2B organisation asks; a tenant that wants a ninth is asking for a code change, which is the
/// correct price for changing what "qualified" means across a company.
/// </para>
/// <para>
/// <strong>Answered or not, with a note — never a score out of ten.</strong> A seller asked to
/// rate their own deal rates it high. A seller asked whether they know who signs the contract
/// either does or does not.
/// </para>
/// </remarks>
public enum QualificationElement
{
    /// <summary>What the customer will measure, in their numbers.</summary>
    Metrics,

    /// <summary>Who can spend the money without asking anybody.</summary>
    EconomicBuyer,

    /// <summary>What has to be true for them to sign.</summary>
    DecisionCriteria,

    /// <summary>The steps their side takes to get to a signature, and who takes them.</summary>
    DecisionProcess,

    /// <summary>What their legal, procurement and security teams do, and how long it takes.</summary>
    PaperProcess,

    /// <summary>The problem that makes doing nothing worse than buying.</summary>
    IdentifiedPain,

    /// <summary>Somebody inside who wants this to happen and can act.</summary>
    Champion,

    /// <summary>Who else they are talking to, and why they would win.</summary>
    Competition,
}

// -------------------------------------------------------------------------------- what is asked

/// <summary>Declares a period that plans are made for.</summary>
/// <param name="Name">What to ask for it by.</param>
/// <param name="Label">What to show a person.</param>
/// <param name="StartsOn">Its first day.</param>
/// <param name="EndsOn">Its last day.</param>
/// <param name="Parent">The period it sits inside, or null.</param>
public sealed record DefinePeriod(
    string Name,
    string Label,
    DateOnly StartsOn,
    DateOnly EndsOn,
    string? Parent);

/// <summary>The period was declared.</summary>
/// <param name="PeriodId">Its id.</param>
public sealed record PeriodDefined(Guid PeriodId);

/// <summary>Sets the number and the words for a period.</summary>
/// <param name="Period">Which period.</param>
/// <param name="Vision">What the leadership team wrote down.</param>
/// <param name="Target">What the group is going to do.</param>
/// <param name="Currency">In what.</param>
public sealed record SetStrategy(string Period, string Vision, decimal Target, string Currency);

/// <summary>The strategy was set.</summary>
/// <param name="StrategyId">Its id.</param>
public sealed record StrategySet(Guid StrategyId);

/// <summary>Commits a plan against a period.</summary>
/// <param name="Kind">What sort of commitment.</param>
/// <param name="Period">Which period.</param>
/// <param name="Name">What to ask for it by.</param>
/// <param name="Label">What to show a person.</param>
/// <param name="Owner">
/// Who is committing, as their subject claim. <strong>Text and not a uuid:</strong> the only
/// identity this system has for a person is the subject their token carries, so a uuid owner is a
/// field that resolves to nobody.
/// </param>
/// <param name="Account"><see cref="PlanKind.Account"/>: which account.</param>
/// <param name="Opportunity"><see cref="PlanKind.Opportunity"/>: which deal.</param>
/// <param name="Channel">
/// <see cref="PlanKind.MarketingLead"/>: which channel. One of the five a lead can come from — a
/// plan for a channel no lead can be attributed to is a plan nothing will ever report against.
/// </param>
/// <param name="Segment">
/// <see cref="PlanKind.MarketingLead"/>: whatever this organisation slices by. Free text, because
/// a closed list would be this build's opinion about somebody else's go-to-market.
/// </param>
/// <param name="TargetAmount">Account and Opportunity: what it is expected to be worth.</param>
/// <param name="Currency">In what.</param>
/// <param name="TargetLeads">MarketingLead: how many.</param>
/// <param name="ActivityKind">Operation: which activity is counted.</param>
/// <param name="TargetActivities">Operation: how many of them.</param>
/// <param name="Parent">
/// The plan this one rolls into, or null at the top. <strong>A name and not an id</strong>, for
/// the same reason a period is: a client committing a plan under a portfolio knows what the
/// portfolio is called and would otherwise have to fetch its id first.
/// </param>
public sealed record DefinePlan(
    PlanKind Kind,
    string Period,
    string Name,
    string Label,
    string Owner,
    Guid? Account = null,
    Guid? Opportunity = null,
    string? Channel = null,
    string? Segment = null,
    decimal? TargetAmount = null,
    string? Currency = null,
    int? TargetLeads = null,
    string? ActivityKind = null,
    int? TargetActivities = null,
    string? Parent = null);

/// <summary>The plan was committed.</summary>
/// <param name="PlanId">Its id.</param>
public sealed record PlanDefined(Guid PlanId);

/// <summary>Records what is and is not known about a deal.</summary>
/// <param name="Plan">Which plan.</param>
/// <param name="Element">Which element.</param>
/// <param name="IsAnswered">Whether it is actually known.</param>
/// <param name="Note">What is known, or what is being done to find out.</param>
public sealed record AnswerQualification(
    string Plan,
    QualificationElement Element,
    bool IsAnswered,
    string Note);

/// <summary>The answer was recorded.</summary>
/// <param name="Answered">How many elements are now answered.</param>
/// <param name="OutOf">How many there are.</param>
public sealed record QualificationRecorded(int Answered, int OutOf);

/// <summary>Adds a step both sides agreed to, or marks one done.</summary>
/// <param name="Plan">Which plan.</param>
/// <param name="Ordinal">Where it sits in the sequence.</param>
/// <param name="Description">What is to be done.</param>
/// <param name="Owner">Who does it, as their subject claim.</param>
/// <param name="DueOn">By when.</param>
/// <param name="IsComplete">Whether it is done.</param>
public sealed record SetPlanStep(
    string Plan,
    int Ordinal,
    string Description,
    string Owner,
    DateOnly DueOn,
    bool IsComplete);

/// <summary>The step was written.</summary>
/// <param name="Ordinal">Which step.</param>
/// <param name="Outstanding">How many steps of this plan are still to do.</param>
public sealed record PlanStepSet(int Ordinal, int Outstanding);

/// <summary>Asks how a period is looking.</summary>
/// <param name="Period">Which period.</param>
public sealed record ReadRollUp(string Period);

// ------------------------------------------------------------------------------- what comes back

/// <summary>One account plan, against what is actually in the pipeline for that account.</summary>
/// <param name="Plan">Which plan.</param>
/// <param name="Account">Whose account.</param>
/// <param name="Target">What was committed.</param>
/// <param name="Currency">In what.</param>
/// <param name="OpenPipeline">
/// The sum of that account's opportunities that have not been decided, <strong>read live</strong>.
/// Not stored on the plan: a cached actual is a number that is wrong between refreshes, and
/// nobody can tell which side of a refresh they are looking at.
/// </param>
public sealed record AccountCoverage(
    string Plan,
    string Account,
    decimal Target,
    string Currency,
    decimal OpenPipeline);

/// <summary>One deal plan, and the two things that say whether to believe it.</summary>
/// <param name="Plan">Which plan.</param>
/// <param name="Target">What it is expected to be worth.</param>
/// <param name="Currency">In what.</param>
/// <param name="Answered">How many qualification elements are answered.</param>
/// <param name="OutOf">How many there are.</param>
/// <param name="Steps">How many steps the mutual action plan has.</param>
/// <param name="OverdueSteps">
/// How many are past their date and not done. <strong>The earliest honest signal that a deal has
/// stopped</strong> — earlier than the stage, which a seller moves, and earlier than the close
/// date, which a seller also moves.
/// </param>
public sealed record OpportunityReadiness(
    string Plan,
    decimal Target,
    string Currency,
    int Answered,
    int OutOf,
    int Steps,
    int OverdueSteps);

/// <summary>One demand plan, against the leads that actually arrived in the period.</summary>
/// <param name="Plan">Which plan.</param>
/// <param name="Segment">Who it was aimed at.</param>
/// <param name="Channel">Where from.</param>
/// <param name="TargetLeads">How many were promised.</param>
/// <param name="ActualLeads">How many arrived, read live from the leads themselves.</param>
public sealed record LeadAttainment(
    string Plan,
    string Segment,
    string Channel,
    int TargetLeads,
    int ActualLeads);

/// <summary>How a period is looking.</summary>
/// <param name="Period">Which period.</param>
/// <param name="Vision">What the leadership team wrote down.</param>
/// <param name="Target">The number.</param>
/// <param name="Currency">In what.</param>
/// <param name="Committed">The sum of what was committed against it.</param>
/// <param name="Gap">
/// <strong>The number this whole feature exists for.</strong> Target minus committed, positive
/// when the plans do not add up to the ambition. Reported and never closed: this schema has
/// nowhere to put a reconciling adjustment, because a planning tool that balanced itself would be
/// one that told a board the number was covered when it was not.
/// </param>
/// <param name="Accounts">The account plans and their live coverage.</param>
/// <param name="Opportunities">The deal plans and how ready they are.</param>
/// <param name="Marketing">The demand plans and what actually arrived.</param>
public sealed record PeriodRollUp(
    string Period,
    string Vision,
    decimal Target,
    string Currency,
    decimal Committed,
    decimal Gap,
    IReadOnlyList<AccountCoverage> Accounts,
    IReadOnlyList<OpportunityReadiness> Opportunities,
    IReadOnlyList<LeadAttainment> Marketing);

// ------------------------------------------------------------------------------- what can go wrong

/// <summary>Refusals the planning surface can produce.</summary>
public static class PlanningErrors
{
    /// <summary>The period is not one this tenant has.</summary>
    /// <param name="name">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error PeriodNotFound(string name) =>
        new(
            "crm.period_not_found",
            $"No period named '{name}' has been declared for this tenant.",
            ErrorCategory.NotFound);

    /// <summary>The plan is not one this tenant has.</summary>
    /// <param name="name">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error PlanNotFound(string name) =>
        new(
            "crm.plan_not_found",
            $"No plan named '{name}' has been committed for this tenant.",
            ErrorCategory.NotFound);

    /// <summary>No strategy has been set for the period being rolled up.</summary>
    /// <param name="period">Which period.</param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// Refused rather than answered with a target of zero. A roll-up against no number would show
    /// every commitment covering nothing and a gap of minus everything, which reads as good news.
    /// </remarks>
    public static Error StrategyNotSet(string period) =>
        new(
            "crm.strategy_not_set",
            $"No strategy has been set for '{period}', so there is no number to roll up against.",
            ErrorCategory.NotFound);

    /// <summary>The plan named something its kind has no use for, or omitted what it needs.</summary>
    /// <param name="kind">Which kind.</param>
    /// <param name="what">Which field.</param>
    /// <returns>The refusal.</returns>
    public static Error KindAndFieldsDisagree(PlanKind kind, string what) =>
        new(
            "crm.plan_fields_disagree",
            $"A {kind} plan and '{what}' do not go together.",
            ErrorCategory.Validation);

    /// <summary>The child period is not inside its parent.</summary>
    /// <param name="child">The period being declared.</param>
    /// <param name="parent">The one it names.</param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// A quarter that sticks out of its year is a roll-up that counts something twice or loses it,
    /// and neither shows up as an error — only as a total nobody can reconcile.
    /// </remarks>
    public static Error PeriodIsNotInsideItsParent(string child, string parent) =>
        new(
            "crm.period_outside_parent",
            $"'{child}' is not inside '{parent}'. A period that sticks out of its parent makes a " +
            "roll-up that double-counts or loses.",
            ErrorCategory.Validation);

    /// <summary>The period ends before it starts.</summary>
    public static Error PeriodEndsBeforeItStarts() =>
        new(
            "crm.period_ends_before_it_starts",
            "A period's last day is not before its first.",
            ErrorCategory.Validation);

    /// <summary>The channel is not one a lead can be attributed to.</summary>
    /// <param name="channel">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error ChannelIsNotALeadSource(string channel) =>
        new(
            "crm.plan_channel_unknown",
            $"'{channel}' is not a source a lead can arrive from, so nothing would ever report " +
            "against it. It is one of: " + string.Join(", ", PlanningLimits.Channels) + ".",
            ErrorCategory.Validation);

    /// <summary>The activity kind is not one the schema records.</summary>
    /// <param name="kind">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error ActivityKindIsUnknown(string kind) =>
        new(
            "crm.plan_activity_unknown",
            $"'{kind}' is not an activity this schema records, so nothing would ever report " +
            "against it. It is one of: " + string.Join(", ", PlanningLimits.Activities) + ".",
            ErrorCategory.Validation);

    /// <summary>Rolling this plan into that one would make the tree loop.</summary>
    /// <param name="name">The plan being committed.</param>
    /// <returns>The refusal.</returns>
    public static Error PlanTreeWouldLoop(string name) =>
        new(
            "crm.plan_tree_would_loop",
            $"Rolling '{name}' up there would make the plan tree loop back on itself.",
            ErrorCategory.Conflict);

    /// <summary>The target was negative.</summary>
    public static Error TargetIsNegative() =>
        new(
            "crm.plan_target_negative",
            "A target is zero or more.",
            ErrorCategory.Validation);
}

/// <summary>What the planning surface accepts.</summary>
public static class PlanningLimits
{
    /// <summary>The sources a lead can arrive from, which are the channels a plan may name.</summary>
    /// <remarks>
    /// The same five as <c>lead.source</c>'s <c>CHECK</c>, stated here because this is where the
    /// refusal is written. A plan for a sixth channel is a plan no lead could ever be counted
    /// against, so it would report zero for ever and look like a marketing failure.
    /// </remarks>
    public static IReadOnlyList<string> Channels { get; } =
        ["Web", "Referral", "Event", "Outbound", "Partner"];

    /// <summary>How many qualification elements there are.</summary>
    public static int Elements => Enum.GetValues<QualificationElement>().Length;

    /// <summary>The activity kinds an operations plan may be counted in.</summary>
    /// <remarks>
    /// The same four as <c>activity.kind</c>'s <c>CHECK</c>, stated here because this is where the
    /// refusal is written.
    /// </remarks>
    public static IReadOnlyList<string> Activities { get; } = ["Task", "Call", "Meeting", "Note"];

    /// <summary>How deep a plan tree is walked.</summary>
    /// <remarks>
    /// A cycle between two plans is two individually legal rows, so the recursive read is bounded
    /// as well as the write being refused — the same pair of defences the reporting line has.
    /// </remarks>
    public const int MaxDepth = 20;
}

using FlowX;

namespace Crm;

// -------------------------------------------------------------------------------- the vocabulary

/// <summary>What a routing rule is about.</summary>
public enum RoutingSubject
{
    /// <summary>An account, routed by where and what it is.</summary>
    Account,

    /// <summary>A lead, routed by where it came from and what it is worth.</summary>
    Lead,
}

/// <summary>What a quota is counted in.</summary>
public enum QuotaMeasure
{
    /// <summary>Money won.</summary>
    Revenue,

    /// <summary>Leads captured.</summary>
    Leads,

    /// <summary>Activities completed.</summary>
    Activities,
}

/// <summary>Which attributes a routing rule may be written against.</summary>
/// <remarks>
/// <para>
/// <strong>Closed, and checked when the rule is saved.</strong> A rule naming an attribute the
/// subject does not have matches nothing for ever — so every account it should have routed lands
/// in no territory, and the symptom is a coverage gap that looks like missing data rather than
/// like a typo.
/// </para>
/// <para>
/// <strong>These are the columns, not the custom fields.</strong> Routing on a field an
/// administrator invented is a reasonable thing to want and is not here: it would need the record's
/// jsonb read alongside the row, and the honest version of that is a second increment rather than
/// a half-working one.
/// </para>
/// </remarks>
public static class RoutingAttributes
{
    /// <summary>What a subject may be routed on.</summary>
    /// <param name="subject">Which subject.</param>
    /// <returns>The attribute names.</returns>
    public static IReadOnlyList<string> Of(RoutingSubject subject) => subject switch
    {
        RoutingSubject.Account => ["region", "industry", "lifecycle"],
        _ => ["source", "status", "score"],
    };
}

// -------------------------------------------------------------------------------- what is asked

/// <summary>One rule of a territory. Every rule must hold for the territory to match.</summary>
/// <param name="Subject">What it is about.</param>
/// <param name="Attribute">Which attribute.</param>
/// <param name="Operator">The same five operators as everywhere else in this sample.</param>
/// <param name="Value">What to compare against. Ignored by <see cref="GuardOperator.IsSet"/>.</param>
public sealed record RoutingRule(
    RoutingSubject Subject,
    string Attribute,
    GuardOperator Operator,
    string Value);

/// <summary>Declares a territory and what falls into it.</summary>
/// <param name="Name">What to ask for it by.</param>
/// <param name="Label">What to show a person.</param>
/// <param name="Parent">The territory it sits inside, or null.</param>
/// <param name="Priority">
/// Lower runs first. Two territories can both match an account — "German manufacturing" and
/// "German" — and without an order the winner would be whichever the planner happened to return.
/// </param>
/// <param name="Rules">What must hold. All of them, never some of them.</param>
/// <param name="Owners">Whose patch it is. More than one is allowed; a named-account team is three.</param>
public sealed record DefineTerritory(
    string Name,
    string Label,
    string? Parent,
    int Priority,
    IReadOnlyList<RoutingRule> Rules,
    IReadOnlyList<string> Owners);

/// <summary>The territory was declared.</summary>
/// <param name="TerritoryId">Its id.</param>
/// <param name="Rules">How many rules it carries.</param>
public sealed record TerritoryDefined(Guid TerritoryId, int Rules);

/// <summary>Asks which territory something falls into.</summary>
/// <param name="Subject">An account or a lead.</param>
/// <param name="Id">Which one.</param>
public sealed record RouteSubject(RoutingSubject Subject, Guid Id);

/// <summary>Asks what is covered and what is not.</summary>
public sealed record ReadCoverage;

/// <summary>Assigns a person their number for a period.</summary>
/// <param name="Period">Which period.</param>
/// <param name="UserId">Whose.</param>
/// <param name="Measure">In what.</param>
/// <param name="Target">How much, before ramp.</param>
/// <param name="RampFactor">
/// The fraction of the period they are carrying, one for a full one. A seller who joined in the
/// second month does not carry the whole quarter, and pretending otherwise reports every new hire
/// as failing for their first two reviews.
/// </param>
public sealed record SetQuota(
    string Period,
    string UserId,
    QuotaMeasure Measure,
    decimal Target,
    decimal RampFactor);

/// <summary>The quota was assigned.</summary>
/// <param name="UserId">Whose.</param>
/// <param name="Target">What was assigned, after ramp.</param>
public sealed record QuotaSet(string UserId, decimal Target);

/// <summary>Asks how the assigned numbers are being met.</summary>
/// <param name="Period">Which period.</param>
public sealed record ReadQuotaAttainment(string Period);

// ------------------------------------------------------------------------------- what comes back

/// <summary>Where something routes.</summary>
/// <param name="Territory">Which territory, or null when nothing matched.</param>
/// <param name="Label">What to show.</param>
/// <param name="Owners">Whose patch it is.</param>
/// <param name="Considered">
/// How many territories were evaluated before one matched, or in total when none did. Returned
/// because a routing nobody can explain is a routing everybody overrides by hand.
/// </param>
public sealed record RoutedTo(
    string? Territory,
    string? Label,
    IReadOnlyList<string> Owners,
    int Considered);

/// <summary>What one territory covers.</summary>
/// <param name="Territory">Which one.</param>
/// <param name="Label">What to show.</param>
/// <param name="Owners">How many people cover it.</param>
/// <param name="Accounts">How many accounts fall into it.</param>
public sealed record TerritoryCoverage(string Territory, string Label, int Owners, int Accounts);

/// <summary>What is covered and what is not.</summary>
/// <param name="Territories">Each territory and what falls into it.</param>
/// <param name="Unrouted">
/// <strong>The number this read exists for.</strong> How many accounts fall into no territory at
/// all — the accounts nobody owns, which a list-per-person model cannot ask about because an
/// account missing from every list looks exactly like an account nobody has got to yet.
/// </param>
/// <param name="Unowned">
/// How many territories have nobody on them. A patch with rules and no owner routes accounts to
/// nobody, which is worse than not having the patch.
/// </param>
public sealed record Coverage(
    IReadOnlyList<TerritoryCoverage> Territories,
    int Unrouted,
    int Unowned);

/// <summary>How one person is doing against the number they were given.</summary>
/// <param name="UserId">Who.</param>
/// <param name="DisplayName">What to show.</param>
/// <param name="Measure">In what.</param>
/// <param name="Quota">What they were assigned, after ramp.</param>
/// <param name="Committed">
/// What they committed in their plans. <strong>A different number from the quota</strong>: a quota
/// is assigned downwards, a commitment is offered upwards, and the two rarely agree.
/// </param>
/// <param name="Actual">What actually happened, read live.</param>
/// <param name="Attainment">Actual over quota, or null when they carry no number.</param>
/// <param name="CommitmentGap">
/// Quota less committed. The number a sales-operations review is about: a seller carrying 500 who
/// has committed 380 has a 120 hole that no roll-up of commitments can show, because every
/// commitment in it is real.
/// </param>
public sealed record QuotaAttainment(
    string UserId,
    string DisplayName,
    string Measure,
    decimal Quota,
    decimal Committed,
    decimal Actual,
    decimal? Attainment,
    decimal CommitmentGap);

/// <summary>How the assigned numbers are being met.</summary>
/// <param name="Period">Which period.</param>
/// <param name="Rows">The people, weakest attainment first.</param>
public sealed record QuotaAttainmentReport(string Period, IReadOnlyList<QuotaAttainment> Rows);

// ------------------------------------------------------------------------------- what can go wrong

/// <summary>Refusals the territory and quota surfaces can produce.</summary>
public static class TerritoryErrors
{
    /// <summary>The attribute is not one the subject has.</summary>
    /// <param name="subject">Which subject.</param>
    /// <param name="attribute">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error AttributeIsNotOfSubject(RoutingSubject subject, string attribute) =>
        new(
            "crm.routing_attribute_unknown",
            $"'{attribute}' is not an attribute of {subject} that a rule can be written against. " +
            "It has: " + string.Join(", ", RoutingAttributes.Of(subject)) + ".",
            ErrorCategory.Validation);

    /// <summary>The territory is not one this tenant has.</summary>
    /// <param name="name">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error TerritoryNotFound(string name) =>
        new(
            "crm.territory_not_found",
            $"No territory named '{name}' has been declared for this tenant.",
            ErrorCategory.NotFound);

    /// <summary>The territory carried no rules.</summary>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// A territory with no rules matches everything, which makes it a catch-all with the highest
    /// priority somebody happened to type — and every account behind it stops routing.
    /// </remarks>
    public static Error TerritoryHasNoRules() =>
        new(
            "crm.territory_without_rules",
            "A territory with no rules matches everything, so everything behind it stops routing.",
            ErrorCategory.Validation);

    /// <summary>The thing being routed is not one this tenant has.</summary>
    /// <param name="id">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error SubjectNotFound(Guid id) =>
        new(
            "crm.routing_subject_not_found",
            $"'{id}' is not an account or a lead of this tenant.",
            ErrorCategory.NotFound);

    /// <summary>The ramp was outside nought-exclusive to one.</summary>
    /// <param name="factor">What was sent.</param>
    /// <returns>The refusal.</returns>
    public static Error RampIsOutOfRange(decimal factor) =>
        new(
            "crm.quota_ramp_out_of_range",
            $"A ramp is more than nought and at most one, and {factor} was sent. " +
            "A ramp of nought is not a part-year seller, it is somebody with no number.",
            ErrorCategory.Validation);
}

/// <summary>What the territory surface accepts.</summary>
public static class TerritoryLimits
{
    /// <summary>The most rules one territory carries.</summary>
    public const int MaxRules = 20;

    /// <summary>The most owners one territory carries.</summary>
    public const int MaxOwners = 50;
}

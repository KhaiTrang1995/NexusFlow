using FlowX;

namespace Crm;

// ------------------------------------------------------------------------------- what is asked

/// <summary>Asks for the plan tree of a period, from the top or from one node.</summary>
/// <param name="Period">Which period.</param>
/// <param name="Root">One plan's name to start from, or null for every top-level plan.</param>
public sealed record ReadPlanTree(string Period, string? Root);

/// <summary>Asks how the people in the caller's organisation are doing.</summary>
/// <param name="Period">Which period.</param>
public sealed record ReadSalesPerformance(string Period);

/// <summary>Asks how the deals themselves are doing.</summary>
/// <param name="Period">Which period.</param>
public sealed record ReadDealPerformance(string Period);

/// <summary>Asks for everything a board looks at, in one request.</summary>
/// <param name="Period">Which period.</param>
public sealed record ReadBoard(string Period);

// ------------------------------------------------------------------------------- what comes back

/// <summary>One node of the plan tree.</summary>
/// <param name="Name">Which plan.</param>
/// <param name="Label">What to show.</param>
/// <param name="Kind">What sort of commitment.</param>
/// <param name="Depth">How far below the root it sits, the root being one.</param>
/// <param name="Parent">The plan it rolls into, or null.</param>
/// <param name="Owner">Whose commitment it is.</param>
/// <param name="Target">What was committed here.</param>
/// <param name="Committed">
/// The sum of this node's children's targets. <strong>Its children, not its descendants</strong>:
/// a grandchild is already inside its own parent's total, and adding both would count it twice.
/// </param>
/// <param name="Gap">
/// Target minus committed, at <em>this</em> level. The same arithmetic the period roll-up does,
/// asked at every node — which is the difference between a company with one sales team and a
/// group with divisions.
/// </param>
/// <param name="Children">How many plans roll into it.</param>
public sealed record PlanNode(
    string Name,
    string Label,
    string Kind,
    int Depth,
    string? Parent,
    string Owner,
    decimal Target,
    decimal Committed,
    decimal Gap,
    int Children);

/// <summary>The plan tree of a period.</summary>
/// <param name="Period">Which period.</param>
/// <param name="Nodes">The nodes, parents before their children.</param>
public sealed record PlanTree(string Period, IReadOnlyList<PlanNode> Nodes);

/// <summary>How one person is doing.</summary>
/// <param name="UserId">Who.</param>
/// <param name="DisplayName">What to show.</param>
/// <param name="Role">What the tenant set them to.</param>
/// <param name="Committed">What they committed this period.</param>
/// <param name="OpenPipeline">What is open on the accounts they planned, read live.</param>
/// <param name="Won">What was won on those accounts inside the period.</param>
/// <param name="Attainment">
/// Won over committed, as a percentage to one decimal place. Null when they committed nothing —
/// <strong>null and not zero</strong>, because a person with no number is not a person at nought
/// per cent, and a table that showed them as one would rank them below everybody.
/// </param>
public sealed record SellerPerformance(
    string UserId,
    string DisplayName,
    string Role,
    decimal Committed,
    decimal OpenPipeline,
    decimal Won,
    decimal? Attainment);

/// <summary>How the people in the caller's organisation are doing.</summary>
/// <param name="Period">Which period.</param>
/// <param name="Sellers">The people, weakest attainment first — which is who a review is about.</param>
public sealed record SalesPerformance(string Period, IReadOnlyList<SellerPerformance> Sellers);

/// <summary>How the deals themselves are doing.</summary>
/// <param name="Period">Which period.</param>
/// <param name="Open">How many are undecided, and what they are worth.</param>
/// <param name="OpenValue">What the undecided ones are worth.</param>
/// <param name="Won">How many were won inside the period.</param>
/// <param name="WonValue">What they were worth.</param>
/// <param name="Lost">How many were lost.</param>
/// <param name="LostValue">What they were worth.</param>
/// <param name="WinRate">
/// Won over decided, as a percentage to one decimal place. Null when nothing was decided —
/// a win rate over no decisions is not nought per cent, it is not a number.
/// </param>
/// <param name="AverageWonValue">The average won deal, or null when none were.</param>
/// <param name="Stalled">
/// How many open deals have not changed stage in sixty days. The number a pipeline review is
/// actually for: a deal nobody has moved is not a deal that is going slowly.
/// </param>
public sealed record DealPerformance(
    string Period,
    int Open,
    decimal OpenValue,
    int Won,
    decimal WonValue,
    int Lost,
    decimal LostValue,
    decimal? WinRate,
    decimal? AverageWonValue,
    int Stalled);

/// <summary>Everything a board looks at, in one request.</summary>
/// <param name="Period">Which period.</param>
/// <param name="ViewedAs">Whose view this is, and at what level.</param>
/// <param name="RollUp">The number, what was committed against it, and the gap.</param>
/// <param name="Tree">The plan tree, so a gap can be traced to the level that owns it.</param>
/// <param name="Sales">How the people are doing.</param>
/// <param name="Deals">How the deals are doing.</param>
/// <param name="Scorecard">The KPIs, off-track first.</param>
/// <remarks>
/// <strong>One request and not six.</strong> A board screen that fetched each of these separately
/// would render in six stages, and — worse — could show a roll-up from one instant beside a
/// scorecard from another, which is how two numbers on one page stop agreeing.
/// </remarks>
public sealed record ExecutiveBoard(
    string Period,
    ViewerScope ViewedAs,
    PeriodRollUp RollUp,
    PlanTree Tree,
    SalesPerformance Sales,
    DealPerformance Deals,
    Scorecard Scorecard);

// ------------------------------------------------------------------------------ how it is asked

/// <summary>What a performance read is given, once the flow has read the caller.</summary>
/// <param name="Period">Which period.</param>
/// <param name="UserId">Whose view it is.</param>
public sealed record ForPerformance(string Period, string UserId);

/// <summary>What the performance surface counts.</summary>
public static class PerformanceLimits
{
    /// <summary>How long an open deal may sit in a stage before it counts as stalled.</summary>
    /// <remarks>
    /// <strong>A constant rather than a setting, and deliberately.</strong> A configurable
    /// staleness makes every organisation's number incomparable with every other's, and the
    /// sample's point is the shape of the read. A deployment that wants it per-tenant has
    /// <c>custom_field</c> and a validation rule to do it with.
    /// </remarks>
    public const int StalledAfterDays = 60;

    /// <summary>The most people one performance read returns.</summary>
    public const int MaxSellers = 500;
}

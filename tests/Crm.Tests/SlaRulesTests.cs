using Crm;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The escalation schedule and the staleness rule, of <c>docs/26-CRM-Sample.md</c> §10 package 10.
/// </summary>
/// <remarks>
/// <strong>"Escalates once per window" is decided here and nowhere else.</strong> The sweep asks
/// the same question of the database in one statement, and <c>TaskSweepTests</c> checks the
/// statement agrees with these. What the property actually means is these functions.
/// </remarks>
public sealed class SlaRulesTests
{
    private static readonly DateTimeOffset Due = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromDays(1);

    // ------------------------------------------------------------------------- the escalation

    [Fact]
    public void ATaskThatIsNotYetDueIsNotEscalated()
    {
        SlaRules.NeedsEscalation(Due, Due.AddSeconds(-1), escalationCount: 0, Window)
            .ShouldBeFalse();
    }

    [Fact]
    public void ATaskIsEscalatedTheMomentItIsDue()
    {
        SlaRules.NeedsEscalation(Due, Due, escalationCount: 0, Window).ShouldBeTrue();
    }

    /// <summary>
    /// The property package 10 exists for: a second sweep inside the same window does nothing.
    /// </summary>
    [Fact]
    public void AnEscalatedTaskIsLeftAloneUntilTheWindowHasPassed()
    {
        SlaRules.NeedsEscalation(Due, Due.AddHours(23), escalationCount: 1, Window)
            .ShouldBeFalse("it was escalated when it fell due, and a day has not passed.");

        SlaRules.NeedsEscalation(Due, Due + Window, escalationCount: 1, Window)
            .ShouldBeTrue("a day has passed, so it is due again.");
    }

    [Fact]
    public void TheScheduleIsCountedFromTheDueDateAndDoesNotDrift()
    {
        // A sweep that ran late does not push the next occurrence out.
        SlaRules.NeedsEscalation(Due, Due + Window + TimeSpan.FromHours(6), 1, Window)
            .ShouldBeTrue();

        SlaRules.NeedsEscalation(Due, Due + (Window * 2), 2, Window)
            .ShouldBeTrue("the third is due two days after the first, whenever the second ran.");
    }

    [Fact]
    public void ATaskWithNoDueDateIsNeverEscalated()
    {
        SlaRules.NeedsEscalation(null, Due.AddYears(1), escalationCount: 0, Window)
            .ShouldBeFalse("nothing was promised, so nothing is late.");
    }

    [Fact]
    public void EscalationStopsAtTheCeiling()
    {
        SlaRules.NeedsEscalation(Due, Due + (Window * 10), SlaPolicy.MaxEscalations, Window)
            .ShouldBeFalse("a fourth notification is not what an ignored task needs.");
    }

    // --------------------------------------------------------------------------- the backlog

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(30, SlaPolicy.MaxEscalations)]
    public void TheBacklogIsCountedInWindowsAndCapped(int daysPastDue, int expected)
    {
        SlaRules.EscalationsDue(Due, Due.AddDays(daysPastDue), Window).ShouldBe(expected);
    }

    [Fact]
    public void ATaskWithNoDueDateOwesNothing()
    {
        SlaRules.EscalationsDue(null, Due, Window).ShouldBe(0);
    }

    [Fact]
    public void AWindowOfNothingOwesNothingRatherThanDividingByZero()
    {
        SlaRules.EscalationsDue(Due, Due.AddYears(1), TimeSpan.Zero).ShouldBe(0);
    }

    // ------------------------------------------------------------------------- the staleness

    [Fact]
    public void AnOpportunityStillMovingIsNotStale()
    {
        SlaRules.IsStale(Due, Due + SlaPolicy.StaleAfter - TimeSpan.FromSeconds(1),
            SlaPolicy.StaleAfter, outcome: null).ShouldBeFalse();
    }

    [Fact]
    public void AnOpportunityThatHasSatLongEnoughIsStale()
    {
        SlaRules.IsStale(Due, Due + SlaPolicy.StaleAfter, SlaPolicy.StaleAfter, outcome: null)
            .ShouldBeTrue();
    }

    [Theory]
    [InlineData(OpportunityOutcome.Won)]
    [InlineData(OpportunityOutcome.Lost)]
    [InlineData(OpportunityOutcome.Abandoned)]
    public void AClosedOpportunityIsNeverStale(OpportunityOutcome outcome)
    {
        SlaRules.IsStale(Due, Due.AddYears(5), SlaPolicy.StaleAfter, outcome)
            .ShouldBeFalse("a closed deal sits in its final stage by design.");
    }
}

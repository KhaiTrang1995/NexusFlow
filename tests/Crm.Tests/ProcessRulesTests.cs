using Crm;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The configurable process's decision, of <c>docs/26-CRM-Sample.md</c> §7.
/// </summary>
/// <remarks>
/// <strong>No database, and that is a property of the design rather than a shortcut.</strong>
/// Matching a transition and evaluating its guards are pure functions of the definition and a
/// snapshot of the entity's whitelisted fields. If either needed a connection, a configured
/// process would not be reproducible on a replay — and these tests would have to stand a server
/// up to say anything at all.
/// </remarks>
public sealed class ProcessRulesTests
{
    private static readonly Guid Negotiation = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Closing = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Lost = Guid.Parse("33333333-3333-4333-8333-333333333333");

    // -------------------------------------------------------------------------- the operators

    [Theory]
    [InlineData(GuardOperator.Equals, "EUR", true)]
    [InlineData(GuardOperator.Equals, "USD", false)]
    [InlineData(GuardOperator.NotEquals, "USD", true)]
    [InlineData(GuardOperator.NotEquals, "EUR", false)]
    public void TextOperatorsCompareExactly(GuardOperator op, string value, bool holds)
    {
        ProcessRules.Holds(Guard(ProcessFields.Currency, op, value), Facts()).ShouldBe(holds);
    }

    [Theory]
    [InlineData(GuardOperator.GreaterThan, "40000", true)]
    [InlineData(GuardOperator.GreaterThan, "42000", false)]
    [InlineData(GuardOperator.GreaterThan, "50000", false)]
    [InlineData(GuardOperator.LessThan, "50000", true)]
    [InlineData(GuardOperator.LessThan, "42000", false)]
    [InlineData(GuardOperator.LessThan, "40000", false)]
    public void OrderingOperatorsCompareNumbers(GuardOperator op, string value, bool holds)
    {
        ProcessRules.Holds(Guard(ProcessFields.Amount, op, value), Facts()).ShouldBe(holds);
    }

    /// <summary>
    /// A guard over an amount an opportunity does not have is false, whichever way it is asked.
    /// </summary>
    [Theory]
    [InlineData(GuardOperator.Equals)]
    [InlineData(GuardOperator.GreaterThan)]
    [InlineData(GuardOperator.LessThan)]
    public void AnUnsetFieldFailsEveryPositiveOperator(GuardOperator op)
    {
        var unset = Facts() with { Amount = null };

        ProcessRules.Holds(Guard(ProcessFields.Amount, op, "50000"), unset)
            .ShouldBeFalse("a claim about a value is not true when there is no value.");
    }

    [Fact]
    public void NotEqualsHoldsAgainstAnUnsetField()
    {
        var unset = Facts() with { Currency = null };

        ProcessRules.Holds(Guard(ProcessFields.Currency, GuardOperator.NotEquals, "EUR"), unset)
            .ShouldBeTrue("an opportunity with no currency does not have EUR.");
    }

    [Theory]
    [InlineData("true", true, true)]
    [InlineData("true", false, false)]
    [InlineData("false", true, false)]
    [InlineData("false", false, true)]
    public void IsSetAsksWhetherThereIsAValue(string expected, bool present, bool holds)
    {
        var facts = present ? Facts() : Facts() with { Amount = null };

        ProcessRules.Holds(Guard(ProcessFields.Amount, GuardOperator.IsSet, expected), facts)
            .ShouldBe(holds);
    }

    /// <summary>
    /// An ordering operator over text is false rather than answered by a string comparison.
    /// </summary>
    [Fact]
    public void AnOrderingOperatorOverTextIsFalseRatherThanPlausible()
    {
        ProcessRules.Holds(Guard(ProcessFields.Region, GuardOperator.GreaterThan, "EU-EAST"), Facts())
            .ShouldBeFalse("comparing regions by ordering is a mistake, and a plausible answer would hide it.");
    }

    [Fact]
    public void AGuardOverAFieldOutsideTheWhitelistIsFalse()
    {
        ProcessRules.Holds(Guard("close_date", GuardOperator.IsSet, "true"), Facts())
            .ShouldBeFalse("the whitelist is the whole of what a guard can see.");
    }

    // ------------------------------------------------------------------------- the conjunction

    [Fact]
    public void EveryGuardMustHold()
    {
        var both = new[]
        {
            Guard(ProcessFields.Amount, GuardOperator.GreaterThan, "40000"),
            Guard(ProcessFields.Currency, GuardOperator.Equals, "EUR"),
        };

        ProcessRules.AllHold(both, Facts()).ShouldBeTrue();
        ProcessRules.AllHold(both, Facts() with { Currency = "USD" }).ShouldBeFalse();
    }

    [Fact]
    public void ATransitionWithNoGuardsIsAlwaysAllowed()
    {
        ProcessRules.AllHold([], Facts()).ShouldBeTrue();
    }

    // ---------------------------------------------------------------------------- the matching

    [Fact]
    public void TheFirstTransitionByOrdinalWhoseGuardsHoldIsTaken()
    {
        var candidates = new[]
        {
            Candidate(Closing, ordinal: 1, Guard(ProcessFields.Amount, GuardOperator.GreaterThan, "100000")),
            Candidate(Lost, ordinal: 2, Guard(ProcessFields.Amount, GuardOperator.GreaterThan, "40000")),
        };

        var matched = ProcessRules.Match(candidates, Negotiation, "advance", Facts());

        matched.ShouldNotBeNull();
        matched.Transition.To.ShouldBe(Lost, "the first one's guard does not hold, so the second is taken.");
    }

    [Fact]
    public void OrderDecidesWhenTwoTransitionsBothHold()
    {
        var candidates = new[]
        {
            Candidate(Lost, ordinal: 2),
            Candidate(Closing, ordinal: 1),
        };

        ProcessRules.Match(candidates, Negotiation, "advance", Facts())!.Transition.To
            .ShouldBe(Closing, "the administrator ordered them, and the order is the answer.");
    }

    [Fact]
    public void NoTransitionOutOfTheStageIsNotAnError()
    {
        var candidates = new[] { Candidate(Closing, ordinal: 1) };

        ProcessRules.Match(candidates, Closing, "advance", Facts())
            .ShouldBeNull("nothing leaves Closing on advance, and that is a recorded outcome.");
    }

    [Fact]
    public void ATransitionOnADifferentTriggerIsNotTaken()
    {
        var candidates = new[] { Candidate(Closing, ordinal: 1) };

        ProcessRules.Match(candidates, Negotiation, "abandon", Facts()).ShouldBeNull();
    }

    // -------------------------------------------------------------------------- the publishing

    [Fact]
    public void AGuardNamingAnUnknownFieldIsRefusedAtPublishTime()
    {
        var faults = ProcessPublishing.Validate(
            Stages(),
            [Candidate(Closing, ordinal: 1, Guard("close_date", GuardOperator.IsSet, "true"))]);

        var fault = faults.ShouldHaveSingleItem();

        fault.Reason.ShouldContain("close_date");
        fault.Reason.ShouldContain(ProcessFields.Amount, Case.Sensitive,
            "the message must say what a guard may name, or the administrator has to go and find out.");
    }

    [Fact]
    public void AnOrderingGuardWithATextBoundIsRefusedAtPublishTime()
    {
        var faults = ProcessPublishing.Validate(
            Stages(),
            [Candidate(Closing, ordinal: 1, Guard(ProcessFields.Amount, GuardOperator.GreaterThan, "a lot"))]);

        faults.ShouldHaveSingleItem().Reason.ShouldContain("is not one");
    }

    [Fact]
    public void ATransitionToAStageOfAnotherProcessIsRefused()
    {
        var elsewhere = Guid.Parse("99999999-9999-4999-8999-999999999999");

        var faults = ProcessPublishing.Validate(Stages(), [Candidate(elsewhere, ordinal: 1)]);

        faults.ShouldHaveSingleItem().Reason.ShouldContain("two stages of this process");
    }

    [Fact]
    public void AnActionKindThisBuildCannotRunIsRefused()
    {
        var unknown = new TransitionAction(
            Guid.NewGuid(), Guid.NewGuid(), (ActionKind)42, "{}", 1);

        var faults = ProcessPublishing.Validate(
            Stages(),
            [new TransitionCandidate(Transition(Closing, 1), [], [unknown])]);

        faults.ShouldHaveSingleItem().Reason.ShouldContain("code change");
    }

    [Fact]
    public void AWellFormedDefinitionHasNoFaults()
    {
        var faults = ProcessPublishing.Validate(
            Stages(),
            [Candidate(Closing, ordinal: 1, Guard(ProcessFields.Amount, GuardOperator.GreaterThan, "40000"))]);

        faults.ShouldBeEmpty();
    }

    [Fact]
    public void ADefinitionWithNoStagesIsRefused()
    {
        ProcessPublishing.Validate([], []).ShouldHaveSingleItem()
            .Reason.ShouldContain("at least one stage");
    }

    // ------------------------------------------------------------------------------- fixtures

    private static ProcessFacts Facts() =>
        new(Amount: 42_000m,
            Currency: "EUR",
            Probability: 60,
            Region: "EU-WEST",
            Industry: "Software",
            Owner: Guid.Parse("7d2b1a6c-0e4f-4a39-8c5d-1b2e3f4a5b6c"));

    private static TransitionGuard Guard(string field, GuardOperator op, string value) =>
        new(Guid.NewGuid(), Guid.NewGuid(), field, op, value);

    private static ProcessTransition Transition(Guid to, int ordinal) =>
        new(Guid.NewGuid(), Negotiation, to, "advance", ordinal);

    private static TransitionCandidate Candidate(Guid to, int ordinal, params TransitionGuard[] guards) =>
        new(Transition(to, ordinal), guards, []);

    private static ProcessStage[] Stages() =>
    [
        new(Negotiation, Guid.Empty, "Negotiation", 1, false),
        new(Closing, Guid.Empty, "Closing", 2, false),
        new(Lost, Guid.Empty, "Lost", 3, true),
    ];
}

using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// A guard naming a field an administrator declared at run time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What changed and what did not.</strong> <see cref="ProcessFields"/> used to be the
/// whole of what a guard could name, and adding a field was a deployment. It is now the built-in
/// half of a catalogue whose other half is <c>custom_field</c>. The operators are still five,
/// the comparison is still text or number, and evaluation is still a pure function of one
/// snapshot — the language did not open, the catalogue did.
/// </para>
/// <para>
/// <strong>The property worth keeping is when a bad guard is caught</strong>, and the first two
/// tests are what hold it: a guard naming a field nobody declared is a publish-time fault, not a
/// transition that quietly never fires. That is the same guarantee as before, measured against a
/// set that is now read rather than compiled.
/// </para>
/// </remarks>
public sealed class DynamicFieldWorkflowTests
{
    private static readonly Guid Negotiation = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Closing = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A guard on a declared field publishes.</summary>
    [Fact]
    public void AGuardNamingADeclaredFieldIsPublishable()
    {
        var declared = Declared("renewal_risk", CustomFieldType.Text);

        var faults = ProcessPublishing.Validate(
            Stages(),
            [Candidate(Guard("renewal_risk", GuardOperator.Equals, "high"))],
            ProcessFields.Including(declared));

        faults.ShouldBeEmpty(
            "an administrator declared renewal_risk and could not then guard on it, which " +
            "makes the whole of the dynamic catalogue unreachable from the process.");
    }

    /// <summary>A guard on a field nobody declared is still refused at publish time.</summary>
    [Fact]
    public void AGuardNamingAnUndeclaredFieldIsStillRefused()
    {
        var declared = Declared("renewal_risk", CustomFieldType.Text);

        var faults = ProcessPublishing.Validate(
            Stages(),
            [Candidate(Guard("renewal_rick", GuardOperator.Equals, "high"))],
            ProcessFields.Including(declared));

        var fault = faults.ShouldHaveSingleItem();

        fault.Reason.ShouldContain("renewal_rick");
        fault.Reason.ShouldContain(
            "renewal_risk",
            Case.Sensitive,
            "the message must list what may be named, and a typo is the case that needs it.");
    }

    /// <summary>Without a catalogue, only the built-in fields may be named.</summary>
    /// <remarks>
    /// The default argument is what every existing call site uses, so this is the assertion that
    /// making the catalogue a parameter did not quietly open it for callers that pass nothing.
    /// </remarks>
    [Fact]
    public void WithNoCatalogueOnlyTheBuiltInFieldsMayBeNamed()
    {
        var faults = ProcessPublishing.Validate(
            Stages(), [Candidate(Guard("renewal_risk", GuardOperator.Equals, "high"))]);

        faults.ShouldHaveSingleItem().Reason.ShouldContain("renewal_risk");
    }

    /// <summary>A declared field cannot shadow a built-in one.</summary>
    /// <remarks>
    /// An administrator who declared a field called <c>amount</c> would otherwise be able to
    /// change what every guard in every process means by it.
    /// </remarks>
    [Fact]
    public void ADeclaredFieldCannotShadowABuiltInOne()
    {
        var facts = new ProcessFacts(
            50000m, "EUR", 20, "emea", "Software", Guid.Empty,
            new Dictionary<string, string?> { [ProcessFields.Amount] = "1" });

        facts.Read(ProcessFields.Amount).ShouldBe(
            "50000",
            "a declared field called amount took precedence over the opportunity's own.");
    }

    /// <summary>A guard on a declared field is evaluated against the row's value.</summary>
    [Fact]
    public void AGuardOnADeclaredFieldDecidesTheTransition()
    {
        var candidates = new[]
        {
            Candidate(Guard("renewal_risk", GuardOperator.Equals, "high")),
        };

        var risky = Facts(new Dictionary<string, string?> { ["renewal_risk"] = "high" });
        var safe = Facts(new Dictionary<string, string?> { ["renewal_risk"] = "low" });

        ProcessRules.Match(candidates, Negotiation, "advance", risky).ShouldNotBeNull();
        ProcessRules.Match(candidates, Negotiation, "advance", safe).ShouldBeNull();
    }

    /// <summary>A numeric guard on a declared Number field compares as a number.</summary>
    /// <remarks>
    /// The reason <c>CustomValues.ToJson</c> writes a number rather than a string: <c>"9"</c>
    /// sorts after <c>"42"</c> as text, so this holds only because both sides are parsed.
    /// </remarks>
    [Fact]
    public void ANumericGuardOnADeclaredFieldComparesAsANumber()
    {
        var candidates = new[]
        {
            Candidate(Guard("headcount", GuardOperator.GreaterThan, "42")),
        };

        var large = Facts(new Dictionary<string, string?> { ["headcount"] = "9000" });

        ProcessRules.Match(candidates, Negotiation, "advance", large).ShouldNotBeNull();
    }

    /// <summary>
    /// The facts a guard sees carry what was written into the opportunity's <c>custom_fields</c>.
    /// </summary>
    /// <remarks>
    /// The end of the chain the tests above cover in pieces: a value merged into the
    /// <c>jsonb</c> column reaches <see cref="ProcessFacts"/> in the same read as
    /// <c>amount</c>, so a guard on a custom field and a guard on a built-in one cannot disagree
    /// about which version of the opportunity they saw.
    /// </remarks>
    [Fact]
    public async Task TheFactsAGuardSeesCarryTheOpportunitysCustomValues()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var account = await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Prospect, Cancellation);
        var contact = await crm.ContactAsync(CrmSchemaHarness.Northwind, account, Cancellation);
        var (_, stage) = await crm.ProcessAsync(CrmSchemaHarness.Northwind, 1, true, Cancellation);
        var opportunity = await crm.OpportunityAsync(
            CrmSchemaHarness.Northwind, account, contact, stage, Cancellation);

        await crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            """
            UPDATE opportunity
            SET custom_fields = '{"renewal_risk": "high", "headcount": 9000}'::jsonb
            WHERE opportunity_id = @id
            """,
            Cancellation,
            ("id", opportunity));

        var facts = await new ProcessStore(crm.DataSource)
            .ReadFactsAsync(CrmSchemaHarness.Northwind, opportunity, Cancellation);

        facts.ShouldNotBeNull();
        facts.Read("renewal_risk").ShouldBe("high");
        facts.Read("headcount").ShouldBe(
            "9000", "a jsonb number reached a guard as something other than its invariant text.");
        facts.Read(ProcessFields.Amount).ShouldBe(
            "50000.0000", "the built-in fields came from the same read and must still be there.");
    }

    private static ProcessFacts Facts(IReadOnlyDictionary<string, string?> custom) =>
        new(50000m, "EUR", 20, "emea", "Software", Guid.Empty, custom);

    private static Dictionary<string, CustomFieldRow> Declared(
        string name, CustomFieldType type) =>
        new Dictionary<string, CustomFieldRow>(StringComparer.Ordinal)
        {
            [name] = new(Guid.NewGuid(), name, name, type, IsRequired: false),
        };

    private static TransitionGuard Guard(string field, GuardOperator op, string value) =>
        new(Guid.NewGuid(), Guid.Empty, field, op, value);

    private static TransitionCandidate Candidate(params TransitionGuard[] guards) =>
        new(
            new ProcessTransition(Guid.NewGuid(), Negotiation, Closing, "advance", 1),
            guards,
            []);

    private static ProcessStage[] Stages() =>
    [
        new(Negotiation, Guid.Empty, "Negotiation", 1, false),
        new(Closing, Guid.Empty, "Closing", 2, false),
    ];
}

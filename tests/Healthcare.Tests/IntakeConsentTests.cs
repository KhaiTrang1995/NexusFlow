using FlowX;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace Healthcare.Tests;

/// <summary>
/// Consent, and the four answers an intake can get from it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every refusal here happens before the clinic's index is written to.</strong> That
/// ordering is the substance rather than the decoration: resolving a patient id writes a row
/// about a person, and writing anything about a person the clinic has no lawful basis to
/// process is precisely the processing the basis was supposed to authorise. So each test
/// asserts the outcome <em>and</em> that nothing about the patient was filed.
/// </para>
/// </remarks>
public sealed class IntakeConsentTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A standing consent for the purpose the intake declares admits the patient.</summary>
    [Fact]
    public async Task AnIntakeCoveredByConsentIsAdmitted()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation);

        var admitted = await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.BerlinTenant, "adm-1", Cancellation);

        admitted.IsSuccess.ShouldBeTrue(
            "a well-formed intake with a standing treatment consent is the path every other " +
            $"test in this project is a deviation from. {Describe(admitted)}");

        admitted.Value.PatientId.ShouldStartWith("pat-");
        admitted.Value.RecordId.ShouldBe("rec-adm-1");

        clinic.Records.For(ClinicTokens.BerlinTenant, admitted.Value.PatientId)
            .ShouldHaveSingleItem();
    }

    /// <summary>An intake quoting a consent reference the register does not hold is refused.</summary>
    [Fact]
    public async Task AnIntakeWithNoRecordedConsentIsRefused()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation);

        var refused = await clinic.AdmitAsync(
            Intakes.Treatment() with { ConsentReference = "consent-that-does-not-exist" },
            ClinicTokens.BerlinTenant,
            "adm-2",
            Cancellation);

        refused.IsFailure.ShouldBeTrue("there is no lawful basis, so there is nothing to do.");
        refused.Error!.Code.ShouldBe("consent.absent");

        refused.Error!.Category.ShouldBe(
            ErrorCategory.Forbidden,
            "not NotFound: what is missing is the basis for processing, not a document the " +
            "caller could go and fetch. The category is what makes it a 403 rather than a 404 " +
            "at every transport at once.");
    }

    /// <summary>A withdrawn consent refuses the intake, and says so distinguishably.</summary>
    [Fact]
    public async Task AnIntakeUnderAWithdrawnConsentIsRefused()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation);

        var refused = await clinic.AdmitAsync(
            Intakes.Treatment() with
            {
                ConsentReference = InMemoryConsentRegister.WithdrawnReference,
            },
            ClinicTokens.BerlinTenant,
            "adm-3",
            Cancellation);

        refused.IsFailure.ShouldBeTrue();

        refused.Error!.Code.ShouldBe(
            "consent.withdrawn",
            "distinguishable from consent.absent, because the two lead to different " +
            "conclusions: absent means somebody skipped a step, withdrawn means the patient " +
            "made a decision and the clinic's systems have to stop.");
    }

    /// <summary>
    /// Purpose limitation: a consent to be treated does not admit an intake for research.
    /// </summary>
    /// <remarks>
    /// <strong>The load-bearing consent test.</strong> The other three are about whether a
    /// decision exists; this one is about whether the decision that exists covers what is
    /// about to be done, which is GDPR Article 5(1)(b) and the thing a boolean "hasConsent"
    /// flag cannot express. Widening <c>VerifyConsent</c>'s comparison to a hierarchy — so
    /// that treatment consent also covered research — turns this test red and nothing else.
    /// </remarks>
    [Fact]
    public async Task AnIntakeForAPurposeConsentDoesNotCoverIsRefused()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation);

        var refused = await clinic.AdmitAsync(
            Intakes.Treatment() with { Purpose = CarePurpose.Research },
            ClinicTokens.BerlinTenant,
            "adm-4",
            Cancellation);

        refused.IsFailure.ShouldBeTrue(
            "the patient agreed to be treated and this intake declares research.");

        refused.Error!.Code.ShouldBe("consent.purpose_not_covered");
    }

    /// <summary>
    /// A caller who may read consent and may not write records is refused at the step that
    /// writes.
    /// </summary>
    /// <remarks>
    /// The refusal is where a permission model is supposed to bite — after the consent check,
    /// at the first step that writes — and the assertion that nothing was filed is what says
    /// the flow stopped rather than continued past it.
    /// </remarks>
    [Fact]
    public async Task ACallerWithoutTheWriteGrantFilesNothing()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation);

        var refused = await clinic.AdmitAsync(
            Intakes.Treatment(),
            ClinicTokens.BerlinTenant,
            "adm-5",
            Cancellation,
            "consent:read");

        refused.IsFailure.ShouldBeTrue($"records:write was not held. {Describe(refused)}");

        var identity = await clinic.Patients.ResolveAsync(
            ClinicTokens.BerlinTenant,
            Intakes.BerlinPatientId,
            new DateOnly(1949, 5, 23),
            Cancellation);

        clinic.Records.For(ClinicTokens.BerlinTenant, identity.PatientId).ShouldBeEmpty(
            "the refusal is at the deduplication step, so nothing downstream of it ran.");
    }

    private static string Describe(FlowExecutionResult<IntakeResult> result) =>
        result.IsFailure ? result.Error!.ToString() : string.Empty;
}

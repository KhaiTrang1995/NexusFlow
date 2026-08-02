using FlowX;
using Shouldly;
using Xunit;

namespace Healthcare.Tests;

/// <summary>
/// What one clinic cannot reach of another clinic's, decided by PostgreSQL and not by this
/// process.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every assertion here is written in the negative, and that is the point.</strong>
/// A test that a clinic can read its own patient proves only that the adapter still works;
/// it would have passed on every commit since migration <c>0001</c>. What has to be proved
/// is that Berlin cannot reach Munich's, and it is proved in both directions rather than
/// once — an isolation that holds only for whichever clinic a test happens to arrange first
/// is an isolation that holds by accident.
/// </para>
/// <para>
/// <strong>The refusal comes from the database.</strong> The scoped journal assumes
/// <c>flowx_tenant</c>, a role that cannot bypass row-level security, and sets
/// <c>flowx.tenant_id</c>; migration <c>0008</c>'s policies then decide. So an assertion here
/// fails if the policy is missing, if the role is wrong, if <c>FORCE ROW LEVEL SECURITY</c> was
/// omitted, or if the runtime forgot to scope — four ways of shipping the same defect, all
/// caught by the same line.
/// </para>
/// <para>
/// This is what <c>docs/15-Security.md</c> names <c>CrossTenantAccessIsDenied</c>, asserted for
/// the sample rather than as a repository-wide fitness function: the gate is written over
/// every trigger kind and this project has one.
/// </para>
/// </remarks>
public sealed class CrossTenantAccessTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// A clinic scoped to itself cannot read another clinic's admission, in either direction.
    /// </summary>
    /// <remarks>
    /// <see cref="DurabilityErrors.InstanceNotFound"/> rather than a refusal of its own, because
    /// that is what the database's answer honestly is: under the policy the row is not there.
    /// Inventing a distinct "forbidden" would tell the caller that the instance exists and
    /// belongs to somebody else, and it would require reading the row to say so.
    /// </remarks>
    [Fact]
    public async Task AClinicCannotReadAnotherClinicsAdmission()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation);

        var berlin = await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.BerlinTenant, "adm-berlin", Cancellation);

        var munich = await clinic.AdmitAsync(
            Intakes.Treatment(Intakes.OtherPatientId),
            ClinicTokens.MunichTenant,
            "adm-munich",
            Cancellation);

        berlin.IsSuccess.ShouldBeTrue();
        munich.IsSuccess.ShouldBeTrue();

        var ofBerlin = await clinic.InstanceAsync("adm-berlin", Cancellation);
        var ofMunich = await clinic.InstanceAsync("adm-munich", Cancellation);

        var berlinJournal = clinic.JournalFor(ClinicTokens.BerlinTenant);
        var munichJournal = clinic.JournalFor(ClinicTokens.MunichTenant);

        var berlinReadsMunich = await berlinJournal.ReadInstanceAsync(ofMunich, Cancellation);
        var munichReadsBerlin = await munichJournal.ReadInstanceAsync(ofBerlin, Cancellation);

        berlinReadsMunich.IsFailure.ShouldBeTrue(
            "Berlin read Munich's admission. The instance id is a guessable handle, and " +
            "nothing above the database was ever going to stop a caller that holds one.");

        munichReadsBerlin.IsFailure.ShouldBeTrue(
            "Munich read Berlin's admission. Asserted separately from the other direction " +
            "because an isolation that holds one way holds by accident.");

        berlinReadsMunich.Error.Code.ShouldBe(DurabilityErrors.InstanceNotFoundCode);
        munichReadsBerlin.Error.Code.ShouldBe(DurabilityErrors.InstanceNotFoundCode);

        var berlinReadsBerlin = await berlinJournal.ReadInstanceAsync(ofBerlin, Cancellation);

        berlinReadsBerlin.IsSuccess.ShouldBeTrue(
            "and the wall is a wall rather than an outage: a clinic still reaches its own.");
    }

    /// <summary>
    /// A clinic cannot read the steps of another clinic's admission either.
    /// </summary>
    /// <remarks>
    /// <c>flow_step</c> carries no <c>tenant_id</c> of its own — deliberately, because a
    /// denormalised copy can disagree with the row it copies — so its policy restates the
    /// predicate through the foreign key. This is what says that restatement works, which the
    /// instance-row test cannot.
    /// </remarks>
    [Fact]
    public async Task AClinicCannotReadTheStepsOfAnotherClinicsAdmission()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation);

        var munich = await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.MunichTenant, "adm-steps-munich", Cancellation);

        munich.IsSuccess.ShouldBeTrue();

        var ofMunich = await clinic.InstanceAsync("adm-steps-munich", Cancellation);

        var berlinReads = await clinic.JournalFor(ClinicTokens.BerlinTenant)
            .ReadResumeFrontierAsync(ofMunich, Cancellation);

        var munichReads = await clinic.JournalFor(ClinicTokens.MunichTenant)
            .ReadResumeFrontierAsync(ofMunich, Cancellation);

        munichReads.IsSuccess.ShouldBeTrue("Munich's own frontier is readable by Munich.");

        munichReads.Value.Committed.ShouldNotBeEmpty(
            "an admission commits a row per step, so a frontier that was empty for both " +
            "clinics would make the comparison below vacuous.");

        berlinReads.IsFailure.ShouldBeTrue(
            "Berlin reached Munich's step history. flow_step inherits its tenant through the " +
            "foreign key, and the policy that restates the predicate is what has to hold.");
    }

    /// <summary>
    /// An erasure run by one clinic cannot reach another clinic's rows, even for a patient
    /// both of them know.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The case the digest makes possible and would otherwise make dangerous.</strong>
    /// A subject digest is unsalted and deterministic, so one person admitted at two clinics
    /// has the <em>same</em> handle in both — which is what lets an erasure be reproducible
    /// months later, and what would let a single mis-scoped statement clear both clinics' rows.
    /// Two walls stand there: the erasure's predicate names the tenant, and the connection it
    /// runs on is scoped so the database refuses independently.
    /// </para>
    /// <para>
    /// This is the test to break first when changing <c>ErasureSql</c>. Removing the tenant
    /// predicate alone leaves it green — the policy still refuses — which is exactly why the
    /// predicate is there and why this assertion is about counts rather than about an error.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnErasureCannotReachAnotherClinicsRecordsForTheSamePatient()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation);

        var berlin = await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.BerlinTenant, "adm-shared-berlin", Cancellation);

        var munich = await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.MunichTenant, "adm-shared-munich", Cancellation);

        berlin.IsSuccess.ShouldBeTrue();
        munich.IsSuccess.ShouldBeTrue();

        var receipt = await clinic.Erasure.EraseAsync(
            new ErasureRequest
            {
                SubjectDigest = SubjectDigest.Of(Intakes.BerlinPatientId),
                TenantId = ClinicTokens.BerlinTenant,
                Mode = ErasureMode.Confirm,
            },
            Cancellation);

        receipt.IsSuccess.ShouldBeTrue();

        receipt.Value.Matched.ShouldBe(
            1,
            "the same person is admitted at both clinics and therefore has the same handle in " +
            "both. An erasure that matched two has reached across a tenant boundary.");

        var ofMunich = await clinic.InstanceAsync("adm-shared-munich", Cancellation);

        (await clinic.ColumnAsync(ofMunich, "subject_digest", Cancellation)).ShouldBe(
            SubjectDigest.Of(Intakes.BerlinPatientId),
            "Munich's row still carries its handle, so Munich can still answer its own " +
            "patient's request.");

        (await clinic.ColumnAsync(ofMunich, "input", Cancellation)).ShouldNotBeNull(
            "and its payload is untouched.");
    }
}

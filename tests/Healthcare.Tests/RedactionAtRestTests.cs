using FlowX;
using Shouldly;
using Xunit;

namespace Healthcare.Tests;

/// <summary>
/// What is actually on disk after an admission: no identifier, no name, and a handle that is
/// neither.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read as the schema's owner, not through the adapter.</strong> A test that asked the
/// journal for an instance and found no identifier in the answer would be asserting that the
/// adapter does not hand it back — a much weaker claim, and not the one <c>[Sensitive]</c> is
/// about. These read the <c>json</c> columns as a psql prompt, a backup or a support export
/// would, which is the threat model.
/// </para>
/// <para>
/// <strong>Nothing in the sample asks for any of this.</strong> No capability calls a redact
/// helper, no contract is filtered on the way to the store, and the flow's <c>Define</c> method
/// does not mention redaction. It holds because a <c>JournalPayload</c> has one exit and it
/// redacts — which is why "somebody forgot to redact the new payload" is not a defect this
/// codebase can have.
/// </para>
/// </remarks>
public sealed class RedactionAtRestTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// Neither marked member survives anywhere in the instance row, the step history or the
    /// staged event.
    /// </summary>
    /// <remarks>
    /// Asserted over every payload column at once rather than one per test, because the claim
    /// is about the exit and not about a column: a document that reached a store did so through
    /// <c>ToJson</c>, whichever column it landed in. A future payload column that bypassed it
    /// is exactly what this would catch.
    /// </remarks>
    [Fact]
    public async Task NoSensitiveValueReachesAnyStoredDocument()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation);

        var admitted = await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.BerlinTenant, "adm-red-1", Cancellation);

        admitted.IsSuccess.ShouldBeTrue();

        var instance = await clinic.InstanceAsync("adm-red-1", Cancellation);

        var documents = new List<string?>
        {
            await clinic.ColumnAsync(instance, "input", Cancellation),
            await clinic.ColumnAsync(instance, "state_bag", Cancellation),
        };

        documents.AddRange(await clinic.StepResultsAsync(instance, Cancellation));
        documents.AddRange(await clinic.EventBodiesAsync(instance, Cancellation));

        documents.Count(static document => document is not null).ShouldBeGreaterThan(
            3,
            "a test that found nothing stored would pass for the wrong reason. The instance " +
            "row carries an input and a state bag, and every step commits a result.");

        foreach (var document in documents.Where(static document => document is not null))
        {
            document!.ShouldNotContain(
                Intakes.BerlinPatientId,
                Case.Sensitive,
                "the national identifier is [Sensitive] and this document is in a table " +
                "retained for years. Nothing in the sample asks for it to be removed — " +
                "JournalPayload has one exit and it redacts.");

            document!.ShouldNotContain(
                "Wilhelm",
                Case.Sensitive,
                "the patient's name is [Sensitive] too, and it is carried forward by " +
                "ValidatedIntake under the same member name — which is redacted at every " +
                "depth without that contract carrying a marker of its own.");
        }
    }

    /// <summary>The input row keeps the shape and loses the values.</summary>
    /// <remarks>
    /// Redaction replaces rather than removes, and the difference matters at three in the
    /// morning: a key that silently vanished would read as a field the flow never received.
    /// </remarks>
    [Fact]
    public async Task TheStoredInputKeepsThePlaceholderRatherThanDroppingTheMember()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation);

        var admitted = await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.BerlinTenant, "adm-red-2", Cancellation);

        admitted.IsSuccess.ShouldBeTrue();

        var instance = await clinic.InstanceAsync("adm-red-2", Cancellation);
        var input = await clinic.ColumnAsync(instance, "input", Cancellation);

        input.ShouldNotBeNull("flow_instance.input is written from the dispatcher's DescribeInput.");
        input.ShouldContain("\"nationalId\":\"" + JournalPayload.Redacted + "\"");
        input.ShouldContain("\"fullName\":\"" + JournalPayload.Redacted + "\"");

        input.ShouldContain(
            "\"consentReference\":\"" + InMemoryConsentRegister.TreatmentReference + "\"",
            Case.Sensitive,
            "an unmarked member is stored as it arrived. A redaction that took everything " +
            "would be a journal nobody can read an incident out of.");
    }

    /// <summary>
    /// The subject's handle is on the row, is not the identifier, and is what
    /// <see cref="SubjectDigest"/> computes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The two halves of this assertion are the whole design.</strong> The column is
    /// not the identifier — that is what makes it safe to index and to read — and it is exactly
    /// reproducible from the identifier by anyone who holds it, which is what makes an erasure
    /// months later able to find these rows at all.
    /// </para>
    /// <para>
    /// It also proves the digest was taken before the redaction pass. Every stored copy of the
    /// identifier is <c>[redacted]</c>, so a digest computed from the document would be the
    /// digest of the placeholder, and every patient in the deployment would share one handle.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheInstanceCarriesTheSubjectsHandleAndNotTheSubject()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation);

        var admitted = await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.BerlinTenant, "adm-red-3", Cancellation);

        admitted.IsSuccess.ShouldBeTrue();

        var instance = await clinic.InstanceAsync("adm-red-3", Cancellation);
        var digest = await clinic.ColumnAsync(instance, "subject_digest", Cancellation);

        digest.ShouldBe(
            SubjectDigest.Of(Intakes.BerlinPatientId),
            "the handle is reproducible from the identifier and from nothing else. If this " +
            "were the digest of '[redacted]' every patient would share one handle and an " +
            "erasure for one of them would erase all of them.");

        digest.ShouldNotBeNull();
        digest.ShouldNotContain(Intakes.BerlinPatientId, Case.Sensitive);
        digest.Length.ShouldBe(SubjectDigest.Length);
    }

    /// <summary>An audit record carries the same redaction the journal row beside it does.</summary>
    /// <remarks>
    /// Three steps declare an <c>Audit</c>, so a clinical record of what ran exists — and it
    /// goes through the same single exit, which is why the sink could not write the identifier
    /// if it tried.
    /// </remarks>
    [Fact]
    public async Task TheClinicalAuditTrailIsRedactedByTheSamePass()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation);

        var admitted = await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.BerlinTenant, "adm-red-4", Cancellation);

        admitted.IsSuccess.ShouldBeTrue();

        clinic.Audit.Entries.ShouldNotBeEmpty(
            "consent.verify, patient.deduplicate and records.store each declare an Audit, and " +
            "the engine refuses an audited step it cannot record.");

        foreach (var entry in clinic.Audit.Entries)
        {
            entry.Document?.ShouldNotContain(Intakes.BerlinPatientId, Case.Sensitive);
            entry.Document?.ShouldNotContain("Wilhelm", Case.Sensitive);
        }
    }
}

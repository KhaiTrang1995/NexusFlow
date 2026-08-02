using FlowX;

namespace Healthcare;

/// <summary>
/// Why a clinic is processing this record. GDPR Article 5(1)(b): purpose limitation.
/// </summary>
/// <remarks>
/// On the contract rather than read from a header or inferred from the route, for
/// <c>TransferChannel</c>'s reason one sample over: a header would make the same intake mean
/// different things over HTTP and over a queue, and the purpose is the thing consent is
/// granted <em>for</em>. A consent to be treated is not a consent to be studied.
/// </remarks>
public enum CarePurpose
{
    /// <summary>Direct care of this patient.</summary>
    Treatment = 0,

    /// <summary>Secondary use for a study. Needs its own consent.</summary>
    Research = 1,
}

/// <summary>
/// What a clinic sends when a patient is admitted.
/// </summary>
/// <param name="NationalId">
/// The patient's national identifier. <strong>Sensitive, and the data subject.</strong>
/// </param>
/// <param name="FullName">The patient's name. <strong>Sensitive.</strong></param>
/// <param name="DateOfBirth">Used to disambiguate two patients with one name.</param>
/// <param name="Purpose">What the clinic intends to do with the record.</param>
/// <param name="ConsentReference">
/// What the clinic's consent register recorded the patient's decision under.
/// </param>
/// <remarks>
/// <para>
/// <strong><c>NationalId</c> carries both markers, and that combination is what this sample
/// exists to demonstrate.</strong> <c>[Sensitive]</c> means the value is replaced with
/// <c>[redacted]</c> in every document this flow ever writes — the instance row, each step
/// result, the state bag, the emitted event — because a <c>JournalPayload</c> has one exit and
/// it redacts. <c>[Subject]</c> means the runtime digests the same member <em>before</em> that
/// pass and writes the digest onto the instance row, so the rows can still be found when the
/// patient asks for them to be destroyed.
/// </para>
/// <para>
/// Without the second marker the first one would make erasure impossible: the only handle on
/// a patient's records would be the identifier that was deliberately not written down. Without
/// the first, the handle would be the identifier, in a table retained for years. The two
/// together are the only arrangement that is both.
/// </para>
/// <para>
/// <strong><c>FullName</c> is marked and is not the subject.</strong> A name is not an
/// identifier — two patients share one, one patient changes theirs — and digesting it would
/// give an erasure a handle that matches the wrong people and misses the right ones.
/// <c>FLOWX1047</c> is what refuses a second <c>[Subject]</c> here.
/// </para>
/// </remarks>
public sealed record PatientIntake(
    [property: Sensitive][property: Subject] string NationalId,
    [property: Sensitive] string FullName,
    DateOnly DateOfBirth,
    CarePurpose Purpose,
    string ConsentReference);

/// <summary>An intake whose fields are well formed. No decision has been taken yet.</summary>
/// <remarks>
/// Carries the identifier forward under the same member name, which is not an accident: the
/// redaction pass matches by name at every depth, so a contract two steps down the flow that
/// names its field <c>NationalId</c> is redacted in the journal exactly as the input is,
/// without carrying a marker of its own.
/// </remarks>
public sealed record ValidatedIntake(
    string NationalId,
    string FullName,
    DateOnly DateOfBirth,
    CarePurpose Purpose,
    string ConsentReference);

/// <summary>The patient's decision as the consent register holds it.</summary>
/// <param name="Reference">The register's own reference, quoted back so the decision is traceable.</param>
/// <param name="Purpose">What the patient consented to, which the flow has checked covers the intake.</param>
/// <param name="GrantedAt">When the patient gave it.</param>
public sealed record ConsentVerified(string Reference, CarePurpose Purpose, DateTimeOffset GrantedAt);

/// <summary>Who this record belongs to, once the clinic's index has been consulted.</summary>
/// <param name="PatientId">
/// The clinic's own opaque identifier — never the national one, which is why a record store
/// can be read by a clinician without the identifier the erasure is keyed on.
/// </param>
/// <param name="IsNew">Whether the index minted this id during this intake.</param>
public sealed record PatientIdentity(string PatientId, bool IsNew);

/// <summary>A record the clinic's store has accepted.</summary>
public sealed record StoredRecord(string PatientId, string RecordId, DateTimeOffset StoredAt);

/// <summary>A record the store withdrew because the flow that filed it did not finish.</summary>
/// <param name="PatientId">Whose record it was.</param>
/// <param name="Withdrawn">
/// <c>false</c> when there was nothing to withdraw, which is the ordinary outcome of undoing a
/// step that failed before it wrote anything.
/// </param>
public sealed record RecordWithdrawn(string PatientId, bool Withdrawn);

/// <summary>What the caller gets back.</summary>
/// <remarks>
/// The opaque patient id and nothing else. Echoing the national identifier back would put it
/// in the caller's logs, which is the sink this whole sample is arranged to keep it out of.
/// </remarks>
public sealed record IntakeResult(string PatientId, string RecordId);

/// <summary>Announced when a patient has been admitted.</summary>
/// <remarks>
/// <strong>The event body is redacted by the same single pass as the journal row.</strong> It
/// is staged into the outbox by the transaction that commits the step, through a
/// <c>JournalPayload</c> carrying the flow's <c>SensitiveMembers</c> — so a consumer receives
/// the opaque patient id and the purpose, and never the name or the national identifier, with
/// no code in this file having to remember that.
/// </remarks>
public sealed record PatientAdmitted(string PatientId, CarePurpose Purpose, DateTimeOffset AdmittedAt);

/// <summary>What an operator asks when a patient exercises the right to erasure.</summary>
/// <param name="NationalId">
/// The identifier the patient presents. Digested locally and never stored — see
/// <see cref="ErasureEndpoint"/>.
/// </param>
/// <param name="Confirm">
/// <c>false</c> reports what would be removed and changes nothing; <c>true</c> removes it.
/// </param>
public sealed record ErasurePetition(string NationalId, bool Confirm);

/// <summary>
/// What the operator gets back: the receipt, without the handle it was keyed on.
/// </summary>
/// <param name="Confirmed">Whether this describes work done or work proposed.</param>
/// <param name="Matched">Instances this clinic holds for the patient.</param>
/// <param name="Erased">Instances whose payloads were cleared, or would be.</param>
/// <param name="StepsCleared">Step rows whose recorded values were cleared.</param>
/// <param name="EventsCleared">Staged event bodies that were cleared.</param>
/// <param name="Complete">Whether every matched instance was dealt with.</param>
/// <param name="Withheld">Why each remaining instance was left alone.</param>
/// <param name="At">When the store did the work.</param>
/// <remarks>
/// <para>
/// <strong>The digest is deliberately not in the response.</strong> It is a stable handle for
/// the patient across every row in the deployment, so echoing it into an HTTP response body
/// would put it into the caller's logs and its proxies' — which is where the identifier itself
/// was being kept out of. The petition is the caller's own; it already knows who it asked
/// about.
/// </para>
/// <para>
/// <strong>Nor is it signed.</strong> The README this sample replaced promised "a signed
/// completion certificate for the compliance file". There is no key store here, no rotation
/// and nothing that would verify a signature later, so a receipt signed with a key minted at
/// start-up would be a compliance artefact that proves nothing and looks like it proves
/// something. What makes this trustworthy is that it is the return value of the call that did
/// the work, produced from the row counts that call observed.
/// </para>
/// </remarks>
public sealed record ErasureReport(
    bool Confirmed,
    int Matched,
    int Erased,
    int StepsCleared,
    int EventsCleared,
    bool Complete,
    IReadOnlyList<string> Withheld,
    DateTimeOffset At)
{
    /// <summary>Projects a store's receipt into what this application publishes.</summary>
    /// <param name="receipt">What the store reported.</param>
    /// <returns>The report.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="receipt"/> is null.</exception>
    public static ErasureReport From(ErasureReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        return new ErasureReport(
            receipt.Mode == ErasureMode.Confirm,
            receipt.Matched,
            receipt.Erased,
            receipt.StepsCleared,
            receipt.EventsCleared,
            receipt.IsComplete,
            [.. receipt.Withheld.Select(static withheld => withheld.Reason)],
            receipt.At);
    }
}

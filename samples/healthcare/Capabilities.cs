using FlowX;

namespace Healthcare;

/// <summary>Errors this application can produce.</summary>
/// <remarks>
/// <para>
/// Declared in one place so the codes are greppable and so two capabilities cannot invent two
/// spellings of the same condition. Each one reaches the manifest, the generated OpenAPI
/// responses and the RFC 7807 <c>type</c> URI.
/// </para>
/// <para>
/// <strong>No message here interpolates a national identifier or a name.</strong> An
/// <c>Error</c> travels to the caller as a Problem Details body, and the endpoint's redaction
/// covers structured detail attached with <c>.With(...)</c> — not the free-text <c>detail</c>
/// string. So the identifier is kept out of the message rather than trusted to be stripped
/// from it, which is the same rule <c>samples/banking</c> follows for an account number and
/// matters more here.
/// </para>
/// </remarks>
public static class IntakeErrors
{
    /// <summary>The intake is missing an identifier the record cannot be filed without.</summary>
    public static Error IncompleteIntake(string field) =>
        new Error(
            "intake.incomplete",
            $"The intake is missing '{field}'.",
            ErrorCategory.Validation)
            .With("field", field);

    /// <summary>The date of birth is in the future.</summary>
    public static Error ImplausibleDateOfBirth() =>
        new Error(
            "intake.implausible_date_of_birth",
            "The date of birth is in the future.",
            ErrorCategory.Validation);

    /// <summary>The consent register holds no decision under this reference.</summary>
    /// <remarks>
    /// <c>Forbidden</c> rather than <c>NotFound</c>, and the category is the decision. What is
    /// missing is not a document the caller could go and fetch — it is the lawful basis for
    /// processing this record at all, so the answer is "you may not", not "look elsewhere".
    /// It also keeps the endpoint from telling an unauthenticated prober which consent
    /// references exist.
    /// </remarks>
    public static Error ConsentAbsent() =>
        new Error(
            "consent.absent",
            "No consent is recorded for this intake, so there is no lawful basis to process it.",
            ErrorCategory.Forbidden);

    /// <summary>The patient has withdrawn the consent this intake relies on.</summary>
    /// <remarks>
    /// A separate code from <see cref="ConsentAbsent"/> because the two lead to different
    /// operational conclusions: absent means somebody skipped a step, withdrawn means the
    /// patient made a decision and the clinic's systems have to stop.
    /// </remarks>
    public static Error ConsentWithdrawn(DateTimeOffset withdrawnAt) =>
        new Error(
            "consent.withdrawn",
            "The patient has withdrawn consent, so this record cannot be processed.",
            ErrorCategory.Forbidden)
            .With("withdrawnAt", withdrawnAt);

    /// <summary>Consent exists and does not cover what this intake intends to do.</summary>
    /// <remarks>
    /// <strong>This is purpose limitation, and it is the reason consent is a step rather than
    /// a boolean somewhere.</strong> A patient who agreed to be treated has not agreed to be
    /// studied. The check compares what the register recorded against what the intake
    /// declared, and both are values on contracts, so the comparison is in the journal
    /// afterwards rather than in somebody's memory.
    /// </remarks>
    public static Error PurposeNotCovered(CarePurpose granted, CarePurpose requested) =>
        new Error(
            "consent.purpose_not_covered",
            $"Consent was given for {granted} and this intake declares {requested}. " +
            "A consent is granted for a purpose and does not extend to another one.",
            ErrorCategory.Forbidden)
            .With("granted", granted.ToString())
            .With("requested", requested.ToString());

    /// <summary>The clinic's record store refused the write.</summary>
    public static Error RecordStoreUnavailable() =>
        new Error(
            "records.unavailable",
            "The clinical record store did not accept the record.",
            ErrorCategory.Unavailable);
}

/// <summary>Checks the intake is well formed. Reads nothing and decides nothing else.</summary>
/// <remarks>
/// Authenticated rather than permission-gated: every clinician who can reach this deployment
/// may check whether a form is complete, and the grant that matters — <c>records:write</c> —
/// is on the step that actually files something.
/// </remarks>
[Capability("intake.validate", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    Idempotent = true)]
public sealed class ValidateIntake : ICapability<PatientIntake, ValidatedIntake>
{
    /// <inheritdoc />
    public ValueTask<Result<ValidatedIntake>> ExecuteAsync(
        PatientIntake input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (string.IsNullOrWhiteSpace(input.NationalId))
        {
            return ValueTask.FromResult<Result<ValidatedIntake>>(
                IntakeErrors.IncompleteIntake(nameof(PatientIntake.NationalId)));
        }

        if (string.IsNullOrWhiteSpace(input.FullName))
        {
            return ValueTask.FromResult<Result<ValidatedIntake>>(
                IntakeErrors.IncompleteIntake(nameof(PatientIntake.FullName)));
        }

        if (string.IsNullOrWhiteSpace(input.ConsentReference))
        {
            return ValueTask.FromResult<Result<ValidatedIntake>>(
                IntakeErrors.IncompleteIntake(nameof(PatientIntake.ConsentReference)));
        }

        // ctx.UtcNow, never DateTimeOffset.UtcNow: FLOWX1007 refuses the ambient clock because a
        // replay has to reproduce the instant the journal captured, and this flow is Durable.
        if (input.DateOfBirth > DateOnly.FromDateTime(ctx.UtcNow.UtcDateTime))
        {
            return ValueTask.FromResult<Result<ValidatedIntake>>(
                IntakeErrors.ImplausibleDateOfBirth());
        }

        return ValueTask.FromResult<Result<ValidatedIntake>>(new ValidatedIntake(
            input.NationalId,
            input.FullName,
            input.DateOfBirth,
            input.Purpose,
            input.ConsentReference));
    }
}

/// <summary>
/// Establishes that there is a lawful basis for processing this record, and that it covers
/// what the intake intends to do with it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>An ordinary capability, and that is the finding rather than a shortcut.</strong>
/// This sample's README once implied that enforcing consent needed a policy engine the
/// platform did not have. It does not. Consent is a business rule about a business record: it
/// is granted by a person, to an organisation, for a purpose, with an expiry and a withdrawal
/// — none of which a runtime can know — so the only part a platform can supply is the
/// scaffolding that makes the rule's verdict impossible to lose. That scaffolding is already
/// here: the step is in the published graph, its refusal is a <c>Result</c> and therefore a
/// <c>403</c>, the decision is committed to the journal before the next step runs, and the
/// <c>Audit</c> on the step records who asked and what was decided.
/// </para>
/// <para>
/// <strong>What removing this step would cost, since the README invites you to try it.</strong>
/// The build still succeeds — there is no rule that says a flow must verify consent, and one
/// that guessed would be wrong for every flow that has no data subject. What changes is
/// visible without running anything: the step disappears from <c>flowx.manifest.json</c> and
/// from <c>flowx graph</c>, so a reviewer diffing two manifests sees a capability with a
/// <c>consent:read</c> grant leave the flow. That is a smaller claim than "the compiler stops
/// you", and it is the true one.
/// </para>
/// </remarks>
[Capability("consent.verify", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "consent:read",
    Idempotent = true,
    SideEffects = ["consent-register"])]
public sealed class VerifyConsent : ICapability<ValidatedIntake, ConsentVerified>
{
    private readonly IConsentRegister _register;

    /// <summary>Creates the capability.</summary>
    public VerifyConsent(IConsentRegister register)
    {
        ArgumentNullException.ThrowIfNull(register);
        _register = register;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ConsentVerified>> ExecuteAsync(
        ValidatedIntake input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var record = await _register.LookupAsync(input.ConsentReference, ct).ConfigureAwait(false);

        if (record is null)
        {
            return IntakeErrors.ConsentAbsent();
        }

        if (record.WithdrawnAt is { } withdrawn)
        {
            return IntakeErrors.ConsentWithdrawn(withdrawn);
        }

        // Purpose limitation. Deliberately an equality rather than a hierarchy: "treatment
        // consent also covers research" is a clinical and legal judgement, not a fact about
        // enum ordering, and a platform sample that quietly widened one would be teaching the
        // wrong lesson in the one place it matters most.
        return record.Purpose == input.Purpose
            ? new ConsentVerified(record.Reference, record.Purpose, record.GrantedAt)
            : IntakeErrors.PurposeNotCovered(record.Purpose, input.Purpose);
    }
}

/// <summary>
/// Finds the clinic's own identifier for this patient, minting one if this is the first time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Keyed on the national identifier and answering with an opaque one</strong>, which
/// is the boundary the rest of the clinic works behind: a record store, a clinician's screen
/// and an appointment system all deal in the opaque id, so the identifier that must not be
/// written down is confined to this step and to the flow's input.
/// </para>
/// <para>
/// <c>Idempotent</c>, and it has to be: the index answers with the same id for the same
/// identifier, so a retried step does not produce a second patient. The mint is inside the
/// index for the same reason — a capability that generated an id would be
/// <c>FLOWX1008</c>'s ambient identity, and a replay would mint a different one.
/// </para>
/// </remarks>
[Capability("patient.deduplicate", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "records:write",
    Idempotent = true,
    SideEffects = ["patient-index"])]
public sealed class DeduplicatePatient : ICapability<ValidatedIntake, PatientIdentity>
{
    private readonly IPatientIndex _index;

    /// <summary>Creates the capability.</summary>
    public DeduplicatePatient(IPatientIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
    }

    /// <inheritdoc />
    public async ValueTask<Result<PatientIdentity>> ExecuteAsync(
        ValidatedIntake input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        // Scoped to the tenant, so two clinics that admit the same person hold two patients
        // and neither can see the other's. The tenant comes off the invocation, which took it
        // from validated claims and from nothing else (ADR-0046) — this capability could not
        // read a header if it wanted to, because it has none.
        var identity = await _index
            .ResolveAsync(ctx.TenantId ?? string.Empty, input.NationalId, input.DateOfBirth, ct)
            .ConfigureAwait(false);

        return identity;
    }
}

/// <summary>Files the record in the clinic's store.</summary>
/// <remarks>
/// The one step with a compensation, and the reason this flow is <c>Durable</c>: a node that
/// dies after the record is filed and before the flow finishes leaves a row saying so, and
/// another node runs <see cref="PurgeRecord"/> against it.
/// </remarks>
[Capability("records.store", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "records:write",
    Idempotent = true,
    SideEffects = ["record-store"])]
public sealed class StoreRecord : ICapability<PatientIdentity, StoredRecord>
{
    private readonly IRecordStore _records;

    /// <summary>Creates the capability.</summary>
    public StoreRecord(IRecordStore records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
    }

    /// <inheritdoc />
    public async ValueTask<Result<StoredRecord>> ExecuteAsync(
        PatientIdentity input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var stored = await _records
            .FileAsync(ctx.TenantId ?? string.Empty, input.PatientId, ctx.IdempotencyKey, ct)
            .ConfigureAwait(false);

        return stored is null ? IntakeErrors.RecordStoreUnavailable() : stored;
    }
}

/// <summary>Removes a record the flow filed and then could not finish.</summary>
/// <remarks>
/// A compensation is a second write, not the absence of the first: the store keeps the
/// withdrawal rather than forgetting the filing, so an auditor can see that a record existed
/// briefly and why it stopped existing. That is a different act from erasure, which removes
/// what a patient asked to have removed — and the two are deliberately different code paths,
/// because conflating "the flow failed" with "the patient objected" would make one auditable
/// as the other.
/// </remarks>
[Capability("records.purge", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "records:write",
    Idempotent = true,
    SideEffects = ["record-store"])]
public sealed class PurgeRecord : ICapability<PatientIdentity, RecordWithdrawn>
{
    private readonly IRecordStore _records;

    /// <summary>Creates the capability.</summary>
    public PurgeRecord(IRecordStore records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
    }

    /// <inheritdoc />
    public async ValueTask<Result<RecordWithdrawn>> ExecuteAsync(
        PatientIdentity input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        // A compensation is dispatched with the *step's input*, not its output — the engine
        // cannot promise the output exists, because the step it is undoing may have failed
        // half-way. So the record is found the way it was filed: by the flow's idempotency
        // key, which is stable across the attempt that filed it and the attempt that undoes it.
        var withdrawn = await _records
            .WithdrawAsync(ctx.TenantId ?? string.Empty, input.PatientId, ctx.IdempotencyKey, ct)
            .ConfigureAwait(false);

        return new RecordWithdrawn(input.PatientId, withdrawn);
    }
}

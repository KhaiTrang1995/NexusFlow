using FlowX;

namespace Healthcare;

/// <summary>
/// Admits a patient: validate, verify consent, deduplicate, file the record, announce it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Look at what is not in this method.</strong> No tenant parameter, no residency
/// check, no isolation-level branch, no consent cache, no redaction call and nothing that
/// names a subject. Tenancy is ambient — resolved at admission from validated claims and
/// enforced by the database (<c>docs/16-Multi-Tenant.md</c>, ADR-0046); residency is a
/// refusal taken before this flow is reached; redaction is structural, because a
/// <c>JournalPayload</c> has one exit and it redacts; and the handle a patient's records are
/// erased by is derived from the <c>[Subject]</c> marker on the input contract.
/// </para>
/// <para>
/// <strong>Which means the deployment-level claim this sample is here to prove is a narrow
/// one, and it is worth stating exactly.</strong> The same bytes of this file run at
/// isolation <strong>L1</strong> — one database, one <c>tenant_id</c> column, PostgreSQL
/// row-level security under a role that cannot bypass it — and at <strong>L2</strong>, a
/// schema and a connection pool per clinic. Moving between them is two lines of
/// <c>Program.cs</c>: <c>FLOWX_SAMPLE_TENANCY=schema</c>. It is not a claim about L3 or L4,
/// which this platform refuses to have as runtime levels at all; the sample's README says
/// which claims were dropped and
/// <a href="../../docs/adr/ADR-0051-database-isolation-is-a-topology-not-a-runtime-level.md">ADR-0051</a>
/// is the decision.
/// </para>
/// <para>
/// <strong><c>Durable</c>, and it is load-bearing twice.</strong> A node that dies after the
/// record is filed leaves a row saying so and another node runs the withdrawal — the ordinary
/// reason. The second is specific to this sample: erasure needs an instance row to write the
/// subject's handle onto, so a flow that journals nothing can carry no <c>[Subject]</c> at
/// all. <a href="../../docs/diagnostics/FLOWX1047.md">FLOWX1047</a> refuses that combination
/// rather than accepting a marker nothing records.
/// </para>
/// </remarks>
[Flow("patient.intake", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "clinical-records")]
[FlowDeadline("PT60S")]
[HttpTrigger("POST", "/api/v1/patients/intake", Idempotent = true)]
public sealed partial class PatientIntakeFlow : Flow<PatientIntake, IntakeResult>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<PatientIntake, IntakeResult> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            // Shape only. Whether the clinic may process this record at all is the next step's
            // question, and keeping the two apart is what makes "we refused for want of a
            // lawful basis" a distinguishable outcome from "the form was incomplete" — in the
            // error code, in the journal and in the audit trail.
            .Step<ValidateIntake>().WithPolicy(Policies.Admission)

            // GDPR Article 6: the lawful basis, and Article 5(1)(b): the purpose it was given
            // for. An ordinary step, because consent is an ordinary business rule — see
            // VerifyConsent's own remarks for why no policy engine is required to make this
            // one enforceable, and what removing this line actually costs.
            //
            // It sits before the deduplication on purpose. Resolving a patient id writes to
            // the clinic's index, and writing anything about a person the clinic has no basis
            // to process is the processing the basis was supposed to authorise.
            .Step<VerifyConsent>().WithPolicy(Policies.ConsentLookup)

            // The national identifier goes in and an opaque patient id comes out. Everything
            // after this line deals in the opaque one, which is why the record store below
            // never sees the identifier the erasure is keyed on.
            .Step<DeduplicatePatient>().WithPolicy(Policies.PatientLookup)

            // The one compensable step. A flow that files the record and then fails unwinds
            // it, and the undo is retried five times, because a clinical record of an
            // admission that did not happen is worse than no record.
            .Step<StoreRecord>()
                .CompensateWith<PurgeRecord>()
                .WithPolicy(Policies.RecordWrite)

            // Staged into the outbox by the transaction that commits the step. The body
            // carries the opaque id and the purpose; there is nothing to strip from it,
            // which is the point of having established the opaque id two steps earlier
            // rather than announcing the intake as it arrived.
            .Emit<PatientAdmitted>(ctx => new PatientAdmitted(
                ctx.Get<PatientIdentity>().PatientId,
                ctx.Get<ConsentVerified>().Purpose,
                ctx.Get<StoredRecord>().StoredAt))

            .Return(ctx => new IntakeResult(
                ctx.Get<PatientIdentity>().PatientId,
                ctx.Get<StoredRecord>().RecordId));
    }
}

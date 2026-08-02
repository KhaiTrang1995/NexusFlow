# Sample — Healthcare records intake

A five-step durable admission behind one HTTP endpoint: validate, verify consent, deduplicate,
file the record, announce it. It is the repository's fourth reference application and the only
one whose journal identifies a **person** — which is what makes consent, redaction at rest and
the right to erasure demonstrable here rather than argued.

> [!IMPORTANT]
> **This file used to describe a sample that did not exist, and then a sample that could not
> exist.** The original claimed "the same code at isolation L1 and L4"; the annotated version
> that replaced it recorded, correctly, that the platform then guaranteed nothing about tenant
> isolation at all. Both are gone. What is here now is the sample, and
> [what was refused](#what-this-sample-refuses-and-why) is the section where each dropped claim
> is retracted with its reason rather than quietly deleted.

```bash
export FLOWX_POSTGRES_CONNECTION="Host=localhost;Port=5432;Database=postgres;Username=postgres"

dotnet run --project samples/healthcare

curl -X POST http://localhost:5000/api/v1/patients/intake \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: adm-7f3a' \
  -H 'Authorization: Bearer clinician-berlin-token' \
  -d '{"nationalId":"DE-1949-0523-0071",
       "fullName":"Wilhelm Brandt",
       "dateOfBirth":"1949-05-23",
       "purpose":0,
       "consentReference":"consent-treatment-01"}'
```

```json
{ "patientId": "pat-9c1f0a2b4d7e", "recordId": "rec-adm-7f3a" }
```

**PostgreSQL is not optional and that is the point.** `patient.intake` declares `Durable`, so
every step boundary is committed before the next one runs — and the handle a patient's records
are erased by lives on the instance row. A deployment with no journal has no erasure either, and
discovering that when somebody exercises the right is not a thing anyone should have to do.
`Program.cs` refuses to start instead.

---

## What is here

| File | What it holds |
|---|---|
| `Contracts.cs` | The records on the wire and between steps. The `[Sensitive]` and `[Subject]` markers. |
| `Capabilities.cs` | Five capabilities, three ports, and every business rule. |
| `Policies.cs` | The declared policy sets. Every stage they declare into executes. |
| `PatientIntakeFlow.cs` | The control flow: order, compensation, the event. |
| `Authentication.cs` | Four demonstration tokens across three clinics. A stand-in for an OIDC handler. |
| `Erasure.cs` | The one endpoint no flow declares, and why an erasure is not a flow. |
| `Program.cs` | Composition. Registrations, the isolation level, the region, the migration. |
| `Infrastructure.cs` | In-memory consent register, patient index, record store, audit trail, JSON context. |

## The flow

```csharp
[Flow("patient.intake", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "clinical-records")]
[FlowDeadline("PT60S")]
[HttpTrigger("POST", "/api/v1/patients/intake", Idempotent = true)]
public sealed partial class PatientIntakeFlow : Flow<PatientIntake, IntakeResult>
{
    protected override void Define(IFlowBuilder<PatientIntake, IntakeResult> flow) => flow
        .Step<ValidateIntake>().WithPolicy(Policies.Admission)
        .Step<VerifyConsent>().WithPolicy(Policies.ConsentLookup)       // GDPR Art. 6 and 5(1)(b)
        .Step<DeduplicatePatient>().WithPolicy(Policies.PatientLookup)
        .Step<StoreRecord>().CompensateWith<PurgeRecord>().WithPolicy(Policies.RecordWrite)
        .Emit<PatientAdmitted>(ctx => new PatientAdmitted(/* … */))
        .Return(ctx => new IntakeResult(/* … */));
}
```

Notice what is **not** in it: no `tenantId` parameter, no residency check, no isolation-level
branch, no redaction call and nothing that names a subject. Tenancy is ambient — resolved at
admission from validated claims and enforced by the database ([16-Multi-Tenant](../../docs/16-Multi-Tenant.md),
[ADR-0046](../../docs/adr/ADR-0046-a-tenant-is-resolved-at-admission.md)). Residency is a refusal
taken before the flow is reached. Redaction is structural. The erasure handle is derived from a
marker on the contract.

**Consent is verified before the patient is resolved, and the ordering is the substance.**
Resolving a patient id writes a row about a person, and writing anything about a person the
clinic has no lawful basis to process is precisely the processing the basis was supposed to
authorise. `IntakePlanTests.ConsentIsVerifiedBeforeThePatientIsResolved` asserts the order rather
than the presence, because a flow that checked consent last would still have the step.

## The same code at L1 and L2

```mermaid
flowchart LR
    C["PatientIntakeFlow<br/><i>identical bytes</i>"] --> L1["L1 · shared DB + RLS<br/>default"]
    C --> L2["L2 · schema + pool per clinic<br/>FLOWX_SAMPLE_TENANCY=schema"]
    C -.->|refused| L34["L3 / L4 · a deployment per tenant<br/><i>a topology, not a runtime level</i>"]
    style C fill:#1168bd,color:#fff
```

```bash
dotnet run --project samples/healthcare                          # L1: one schema, row-level security
FLOWX_SAMPLE_TENANCY=schema dotnet run --project samples/healthcare   # L2: a schema and a pool per clinic
```

Two lines of `Program.cs` differ between them — the declared `TenantIsolation` and
`PostgresJournalOptions.TenantSchemas` — and no flow, capability, contract or policy does.
`FlowDurability.IsolationEnforced` refuses to start when those two disagree, so a deployment
cannot quietly receive less separation than it configured. `samples/banking` demonstrates the
same move over a different flow.

**L3 and L4 are refused, not missing.** [ADR-0051](../../docs/adr/ADR-0051-database-isolation-is-a-topology-not-a-runtime-level.md)
holds that a database or a cluster per tenant is a *deployment topology*: the pod serving one
clinic points its connection string at that clinic's store and declares `TenantIsolation.None`,
and there is no second tenant in the process to keep it apart from. Read as an in-process level —
a connection string per tenant inside one host — it is L2, which `Schema` already serves.
`TenantIsolation.Database` fails startup by name.

## Data residency, which is a refusal and not a placement

```csharp
options.Residency.Region = "eu-central-1";                          // where this deployment is
options.Residency.Requirements[ClinicTokens.DublinTenant] = "eu-west-1";   // where this clinic may be served
```

A call whose clinic is pinned elsewhere is refused at admission with `tenant.residency_refused`,
before a lease is taken and before a row exists — and is **not forwarded**, because forwarding
would be the platform carrying the data across the boundary the pin exists to hold.

```bash
curl -X POST http://localhost:5000/api/v1/patients/intake \
  -H 'Authorization: Bearer clinician-dublin-token' -d '…'          # 403 tenant.residency_refused
```

**This is one half of a residency guarantee and is described as one half.** The other half is
that the data was never in the wrong region to begin with, which the deployment topology provides
and no runtime check can. What this is worth is a control against misconfiguration: a stale DNS
record, a load balancer that failed a clinic into the wrong region, a client that hard-coded an
endpoint. It selects no store and moves nothing, which is why it does not reopen ADR-0051 —
[ADR-0061 §3](../../docs/adr/ADR-0061-a-subject-is-erased-by-digest-and-a-residency-is-a-refusal.md)
is the argument.

## Consent

```csharp
[Capability("consent.verify", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "consent:read",
    Idempotent = true, SideEffects = ["consent-register"])]
public sealed class VerifyConsent : ICapability<ValidatedIntake, ConsentVerified> { … }
```

**An ordinary capability, and that is the finding rather than a shortcut.** This file once
implied that enforcing consent needed a policy engine the platform did not have. It does not.
Consent is granted by a person, to an organisation, for a purpose, with an expiry and a
withdrawal — none of which a runtime can know — so the only part a platform can supply is the
scaffolding that makes the verdict impossible to lose, and that scaffolding is already here: the
step is in the published graph and the manifest, its refusal is a `Result` and therefore a `403`
at every transport at once, the decision is committed to the journal before the next step runs,
and the `Audit` on the step records who asked and what was decided.

Four answers, four codes, four tests in `IntakeConsentTests`:

| Code | When |
|---|---|
| `consent.absent` | The register holds no decision under that reference. `Forbidden`, not `NotFound` — what is missing is the lawful basis, not a document the caller could go and fetch. |
| `consent.withdrawn` | The patient took it back. A separate code because the operational conclusion differs: somebody skipped a step, versus the patient decided and the systems must stop. |
| `consent.purpose_not_covered` | **Purpose limitation.** A consent to be treated is not a consent to be studied. |
| — | It covers the intake, and the admission proceeds. |

## PII handling

```csharp
public sealed record PatientIntake(
    [property: Sensitive][property: Subject] string NationalId,
    [property: Sensitive] string FullName,
    DateOnly DateOfBirth,
    CarePurpose Purpose,
    string ConsentReference);
```

**Redaction is structural rather than remembered.** A payload reaches a store only as a
`JournalPayload`, whose sole exit is `ToJson()`, which replaces every marked member — matched by
name, case-insensitively, **at every depth** — with `[redacted]`. There is no accessor for the
value, so a store has no route to the object graph and no code path can walk around it. Nothing
in this sample asks for any of it: no capability calls a redaction helper and `Define` does not
mention it.

`RedactionAtRestTests` reads the `json` columns **as the schema's owner**, the way a psql prompt,
a backup or a support export would, and asserts that neither marked value appears in the instance
row, the state bag, any step result, any staged event body or any audit record. Note what that
implies for `ValidatedIntake`, which carries the identifier forward under the same member name
and no marker of its own: it is redacted anyway, because the pass matches names rather than
types.

```sql
SELECT input FROM flow_instance WHERE flow_id = 'patient.intake';
-- {"schemaVersion":"1.0.0","nationalId":"[redacted]","fullName":"[redacted]",
--  "dateOfBirth":"1949-05-23","purpose":0,"consentReference":"consent-treatment-01"}
```

An unmarked member is stored as it arrived. A redaction that took everything would leave a
journal nobody can resolve an incident from.

## Right to erasure

The member that must never be written down is the member the rows have to be found by. That is
what `[Subject]` resolves: the runtime digests it **inside `JournalPayload`, before the redaction
pass**, and writes the digest to `flow_instance.subject_digest`.

```bash
curl -X POST http://localhost:5000/api/v1/patients/erasure \
  -H 'Content-Type: application/json' \
  -H 'Authorization: Bearer clinician-berlin-token' \
  -d '{"nationalId":"DE-1949-0523-0071","confirm":false}'     # report what would go
```

```json
{ "confirmed": false, "matched": 1, "erased": 1, "stepsCleared": 5,
  "eventsCleared": 1, "complete": true, "withheld": [], "at": "…" }
```

**Those counts are real, not estimated.** A dry run executes the same three statements inside a
transaction nothing commits, so the numbers it reports are the numbers the confirmation will
produce — an operator deciding whether to destroy a patient's records is entitled to that rather
than to two code paths that were meant to agree. `"confirm": true` commits. **The payloads go and the skeleton stays**: the instance, its steps
and its staged events survive with every recorded value null and the handle cleared, so a second
request matches nothing. Deleting the rows outright was rejected — an instance row also records
that a flow ran, at what version and with what outcome, which is the system's own operational
history rather than personal data about the patient.

**Two conditions withhold an instance rather than erasing it**, and the receipt names each with
its reason: one that has not finished (clearing a running flow's state bag would resume it
against values no step produced) and one that still owes a staged event to a declared consumer
(emptying a body the publisher has not drained puts a message on the broker that does not carry
what its type says it carries — [ADR-0018](../../docs/adr/ADR-0018-outbox-publication-and-ordering.md)'s
guarantee, asked by the same predicate `PostgresRetention` refuses a purge with).

**The digest is a join key, not anonymisation, and the difference is stated rather than assumed.**
SHA-256 over a national identifier is preimage-resistant in the abstract and not in practice: the
space is small enough to enumerate, so anyone holding a candidate can confirm it. The column is
still personal data, still inside the clinic's row-level security. What it buys is that reading
it does not *hand out* identifiers, and that a patient's rows can be found without storing the
thing that finds them. [ADR-0061](../../docs/adr/ADR-0061-a-subject-is-erased-by-digest-and-a-residency-is-a-refusal.md)
is the argument, including what erasure **cannot** reach: a result-cache entry and an idempotency
record are keyed by a hash of a step's input and carry no subject, so they are bounded by their
windows and by nothing else.

## What this sample refuses, and why

Each of these was promised by an earlier version of this file.

| Promised | What happens instead |
|---|---|
| *"The same code at isolation L1 and **L4**"* | L1 and **L2**. L3/L4 name a deployment per tenant, which is a topology rather than a runtime level ([ADR-0051](../../docs/adr/ADR-0051-database-isolation-is-a-topology-not-a-runtime-level.md)); the pod serving one clinic declares `None`. `TenantIsolation.Database` is refused at startup by name. |
| `flowx tenant migrate` | Not a verb. There is no per-tenant store to migrate *between* at L1, and at L2 a tenant's schema is created and migrated on first use with no operator action at all. |
| `flowx purge --subject …` | Not a verb, deliberately. The CLI reads the journal as rows ([ADR-0020](../../docs/adr/ADR-0020-cli-reads-the-journal-as-rows.md)), and a national identifier on a command line lands in shell history, in the process table and in whatever collects both. The seam is `ISubjectErasure`; this sample exposes it as an endpoint whose authorisation it controls. |
| *"a signed completion certificate"* | An `ErasureReceipt`, unsigned. A signature is worth its key management, and this repository ships none — no key store, no rotation, no revocation, nothing that would verify one later. A receipt signed with a key minted at start-up proves nothing and looks like it proves something. |
| *"`flowx ai review` flags a purpose-limitation gap"* | `ai` is not a verb; see [ai-agent](../ai-agent/). Removing `VerifyConsent` still compiles — a compiler rule that demanded a consent step would be wrong for every flow with no data subject. What it does do is leave the manifest: a reviewer diffing two of them sees a capability with a `consent:read` grant disappear from the flow. |
| *"redaction reaches logs, traces, the journal and replay output"* | The **journal** (and the outbox, the audit trail and RFC 7807 error bodies) — structurally, through one exit. Logs and traces are separate sinks and this sample makes no claim about them; `RedactionCannotBeBypassed` is written over all four at once and stays absent rather than being weakened to the ones that pass. |

## Things to try

1. **Remove `[Subject]` from `NationalId`.** Everything still compiles and every intake still
   succeeds; `subject_digest` is null on every row and `SubjectErasureTests` goes red. That is
   the failure this feature exists to prevent, arriving at test speed instead of when a patient
   asks.
2. **Mark `FullName` as a second `[Subject]`.** The build fails with
   [FLOWX1047](../../docs/diagnostics/FLOWX1047.md): two answers to "whose record is this" would
   make the handle depend on serialisation order.
3. **Attempt a cross-tenant read.** `CrossTenantAccessTests` does, in both directions, against a
   real PostgreSQL — and the refusal comes from migration `0008`'s row-level security under a
   role that cannot bypass it, not from this process.
4. **Inspect a journal row directly.** `psql`, `SELECT input FROM flow_instance` — the identifier
   and the name are already `[redacted]` at rest, and `subject_digest` is sixty-four hex
   characters.
5. **Change the deployment's region** with `FLOWX_SAMPLE_REGION=eu-west-1` and repeat the Berlin
   `curl`. Same code, same clinic, `403 tenant.residency_refused`.

## Tests

`tests/Healthcare.Tests` runs against a real PostgreSQL and **skips loudly** without one — a
silently skipped isolation suite is indistinguishable from a passing one, and a promised database
that does not answer is a failure rather than a skip.

```bash
FLOWX_POSTGRES_CONNECTION="Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres" \
  dotnet test tests/Healthcare.Tests
```

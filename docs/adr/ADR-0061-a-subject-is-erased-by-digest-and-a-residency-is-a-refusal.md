# ADR-0061: A subject is erased by digest, and a residency is a refusal rather than a placement

**Status:** Accepted
**Date:** 2026-08-02
**Amends:** [15 — Security §3](../15-Security.md) · [16 — Multi-Tenancy §7](../16-Multi-Tenant.md#7-tenant-lifecycle)
**Relates to:** [ADR-0051](ADR-0051-database-isolation-is-a-topology-not-a-runtime-level.md) ·
[ADR-0015](ADR-0015-journal-schema-and-durable-execution.md) ·
[ADR-0018](ADR-0018-outbox-publication-and-ordering.md) ·
[ADR-0046](ADR-0046-a-tenant-is-resolved-at-admission.md)

> **This record exists because two obligations arrived together and one of them looks like a
> level.** Erasure is what [ADR-0051](ADR-0051-database-isolation-is-a-topology-not-a-runtime-level.md)
> named as its own revisit trigger — "*or `flowx tenant purge` is built, at which point
> de-provisioning arrives*" — and residency is the half of `Database` isolation that a reader
> could reasonably mistake for the level that record refused. §3 is why it is not.

---

## 1. A journal that cannot be read cannot be searched, which is the problem erasure starts from

Redaction here is **structural**, and the shape that makes it so is the shape that makes erasure
hard. A payload enters the store only as a `JournalPayload`, whose sole exit is `ToJson()`, which
replaces every `[Sensitive]`-named member at every depth; there is no accessor for the value, so
no store has a route to the object graph. That is what [ADR-0015](ADR-0015-journal-schema-and-durable-execution.md)'s
commitment 5 bought and it is not being reopened.

The consequence is that **the identifier a patient would be found by is the identifier that was
deliberately not written down.** "Everything this system holds about this person" could only be
answered by reading every input and every state bag in the table and deserialising each one:
the slowest query the schema admits, and a second disclosure of the data the request exists to
destroy. So the runtime records a handle instead.

**`[Subject]` names the member; the handle is `SHA-256` of it, computed inside `JournalPayload`
before the redaction pass.** Three properties follow and each was a decision:

* **Computed before the pass, not after.** The marked member is usually the member that must
  never be stored — a national identifier is both — so a digest taken from the stored document
  would digest the literal `[redacted]` and give every patient in the deployment one handle.
* **Not a second exit.** An exit returns the value; this returns a one-way function of one named
  member with no route back. There is still no accessor, still one `ToJson`, still one place that
  decides what a marked member is replaced with. The property answers the one question a store
  must be able to ask without being told the answer: *whose record is this?*
* **Unkeyed and unsalted.** Erasure arrives months later holding only the identifier and has to
  reach the same rows the intake wrote, so anything drawn at write time would have to be stored
  to be reproduced — and a stored salt beside the digest it salts protects nothing. A keyed
  digest is a key-management problem this repository ships no place to solve.

### 1.1 It is not anonymisation, and saying so is part of the decision

SHA-256 over a national identifier is preimage-resistant in the abstract and not in practice,
because the space of national identifiers is small enough to enumerate. **Anyone holding a
candidate identifier can confirm whether this deployment has seen it.** The column is therefore
still personal data, still inside the tenant's row-level security, and still granted to nobody
who could not already read the row.

What the digest buys is narrower and is worth having: reading the column does not *hand out*
identifiers — an operator, a backup, a support export and a `SELECT *` all see sixty-four hex
characters — and a subject's rows can be found without storing the thing that finds them. A
claim beyond that would be false, and a compliance control described more strongly than it
behaves is worse than none.

### 1.2 The same person at two tenants has the same handle

A consequence of being unsalted, and it cuts both ways. It is what makes an erasure reproducible;
it is also what would let one mis-scoped statement clear two tenants' rows. Two walls stand
there: the erasure's predicate names the tenant, and the connection it runs on is scoped to
`flowx_tenant` with `flowx.tenant_id` set, so migration `0008`'s policies refuse independently.
`Healthcare.Tests.CrossTenantAccessTests.AnErasureCannotReachAnotherClinicsRecordsForTheSamePatient`
is what holds it, and it is written so that removing the predicate alone leaves it green — the
policy still refuses — which is exactly why both are there.

## 2. Erasure clears payloads and keeps the skeleton

**Rejected: deleting the rows.** An instance row also records that a flow ran, at what version,
with what outcome and when. That is the system's operational history rather than personal data
about the subject, and a durable execution that vanished mid-recovery would be indistinguishable
from one that never happened. So `flow_instance.input`, `state_bag` and `subject_digest`,
`flow_step.result` and `nondeterministic`, and `outbox_event.payload` are set to null, and the
rows stay.

**The handle is cleared last and in the same transaction**, so a second erasure of the same
subject matches nothing — which makes the ordinary shape of an operator following up cheap and
truthful rather than a second sweep of the table.

**Two conditions withhold an instance rather than erasing it**, and both are reported in the
receipt with the reason:

* **It has not finished.** Clearing the state bag of a flow that is still executing hands every
  step after the frontier the values of an execution nobody performed — the failure
  `IStepDispatcher.RestoreState` refuses to resume into, arriving from the one direction it
  cannot detect, because the document would be well-formed and simply absent.
* **It owes an event to a declared consumer.** Emptying a body a publisher has not drained keeps
  the promise's shape and discards its content, so the consumer receives a message that does not
  carry what its type says it carries. This is [ADR-0018](ADR-0018-outbox-publication-and-ordering.md)'s
  guarantee and `PostgresRetention`'s own refusal, asked by **the same predicate** rather than a
  copy of it — two copies that were meant to agree are two predicates that will not, and a
  deployment could then find retention holding a row erasure had already emptied.

**Which consumers exist is a deployment fact and cannot be discovered**, because an undrained
row looks identical whether the publisher is missing or merely down. So erasure takes the same
`RetentionConsumers` retention takes, with the same default — one publisher — and a deployment
that reads its outbox nowhere says so. Left at the default, such a deployment withholds every
instance for a consumer that does not exist: truthfully, and uselessly.

### 2.1 What erasure cannot reach

**A result-cache entry and an idempotency record are keyed by a hash of a step's *input* and
carry no subject.** They cannot be found by subject at all — a scan of every key, deserialising
each entry, is the only way, and it would be a second disclosure with an unbounded cost. Both are
bounded instead by the window and the TTL their declarations already carry, which is a statement
about how long a value survives rather than about whether it can be removed on request. **A
deployment whose retention obligation is shorter than its cache window has to shorten the
window**, and nothing here will tell it so.

### 2.2 It is not a CLI verb

**Rejected: `flowx purge --subject …`**, which `samples/healthcare`'s README promised.
[ADR-0020](ADR-0020-cli-reads-the-journal-as-rows.md) makes the CLI a reader of the journal, and
a writer to it is a different tool with a different blast radius. The narrower objection is
enough on its own: a national identifier passed as `--subject` lands in shell history, in the
process table and in whatever collects both — three new places for the value the whole mechanism
exists to keep out of one. The seam is `ISubjectErasure`, and an application exposes it however
its own authorisation model requires.

**Rejected: a signed completion certificate**, which the same README promised. A signature is
worth exactly as much as the key management behind it, and this repository ships none: no key
store, no rotation, no revocation, nothing that would verify a signature later. A receipt signed
with a key minted at start-up is a compliance artefact that proves nothing while looking like it
proves something. What makes `ErasureReceipt` trustworthy is that it is the return value of the
call that did the work, produced from the row counts that call observed.

## 3. Residency is a refusal, and that is why it is not `Database` isolation

[16 §2](../16-Multi-Tenant.md#2-isolation-levels) offers L4 as *a region-pinned dedicated
deployment*, and [ADR-0051](ADR-0051-database-isolation-is-a-topology-not-a-runtime-level.md) §5
refuses `Database` as a runtime level on the grounds that a deployment per tenant is a topology:
the pod serving one tenant points its connection string at that tenant's store and declares
`None`, and the runtime's part in the separation is to have no part in it.

**Nothing here selects a store either, and that is the whole of why this does not reopen that
record.** A runtime running in one region cannot move a tenant's data to another — the pod is
where it is, the database is where it is, and neither is a decision the request path gets to
make. What it can do is *decline*: `TenantResidency` compares the region the deployment declares
against the region the tenant is pinned to, at admission, immediately after the tenant is
resolved from validated claims and before a lease, a row or a step. A refusal needs no registry,
no second store and no lease protocol — the two consequences §5 of that record says implementation
would not remove. It needs one comparison.

**Refused rather than forwarded.** Proxying the call to the right region would be the platform
carrying personal data across a boundary a customer asked it not to cross, on the strength of a
configuration entry — the one action a residency control must not take on its own.
`ErrorCategory.Forbidden`, because waiting does not move the pod.

**It is one half of a residency guarantee and is described as one half.** The other half is that
the data was never in the wrong region to begin with, which the deployment topology provides and
no runtime check can. What this is worth is a control against misconfiguration: a stale DNS
record, a global load balancer that failed a tenant into the wrong region, a client that
hard-coded an endpoint. A residency claim resting on it alone would be false.

**Absence means unpinned, not forbidden.** A tenant with no entry is served wherever it arrives,
which is the only default that lets an existing multi-region deployment adopt this one tenant at
a time. Both ways of declaring a control that cannot fire are refused at startup: a pin with no
region to compare against would refuse every pinned tenant and look like an outage, and a pin on
a host that resolves no tenant would refuse nobody and look like compliance.

---

## 4. Consequences

**Positive:**

* **A `[Sensitive]` member can be the erasure key.** The arrangement the healthcare sample is
  built on — a national identifier that is both — is the ordinary case and was previously
  impossible: the only handle on a person's records was the value that was deliberately not
  written down.
* **Erasure is an indexed lookup.** `flow_instance (tenant_id, subject_digest)` partial on
  `subject_digest IS NOT NULL`, so a deployment whose flows identify nobody pays nothing.
* **Proved against a real database, in both directions.** `Healthcare.Tests` asserts what is on
  disk — no identifier in any payload column, the handle reproducible from the identifier and
  from nothing else — and that one clinic reaches none of another's, including for a patient
  both of them know.
* **Retention and erasure ask one question about an owed event**, in one predicate, so they
  cannot come to disagree about the same row.

**Negative / accepted trade-offs:**

* **The digest is not anonymisation** (§1.1). The column is personal data and is protected as
  such; anyone holding a candidate identifier can confirm it.
* **Caches and idempotency records are out of reach** (§2.1), bounded only by their windows.
* **A deployment must declare what reads its outbox** or every erasure withholds everything. The
  default is the conservative one and it is the wrong one for a host with no broker.
* **Withholding needs a follow-up.** An erasure that meets a running instance completes
  partially, and nothing retries it; the operator runs it again. A queue of pending erasures is
  a control plane this repository does not ship.
* **Residency is half a guarantee** (§3), and a deployment that read it as the whole would be
  wrong.
* **A subject is never re-derived.** A row written before `[Subject]` was declared has a null
  handle for ever, and no backfill is possible — the identifiers were redacted on the way in.
  `FLOWX1047` exists because that is unrecoverable rather than merely inconvenient.

**Revisit when:** an idempotency or cache store gains a subject dimension, at which point §2.1's
refusal is re-argued rather than cited; or a control plane exists to queue and retry a partial
erasure, at which point withholding stops being the caller's problem; or a deployment needs a
keyed digest and brings a key store with it, at which point §1's third property is reopened —
the domain prefix is versioned so that rows written under the first scheme stay matchable.

---

**See also:** [15 — Security](../15-Security.md) · [16 — Multi-Tenancy](../16-Multi-Tenant.md) ·
[FLOWX1047](../diagnostics/FLOWX1047.md) · [`samples/healthcare`](../../samples/healthcare/)

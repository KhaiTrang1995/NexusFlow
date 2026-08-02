# ADR-0043: An audit record carries the journal's payload, redacted a second time by `redact`

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture

> Recorded because the schema was declined twice, both times on a stated objection that had to
> be answered rather than outvoted: *an audit record the engine can write carries no payload,
> which makes `redact` vacuous*.

---

## 1. Context

`FlowEngine` holds a `Dictionary<Type, object>` and knows no contract types — that is
[`IStepDispatcher`](../../src/FlowX.Runtime/IStepDispatcher.cs)'s whole basis. So everything the
engine can record by itself is metadata, and on metadata `Audit(category, redact)`'s second
argument names nothing. Shipping that would be shipping a parameter that is read, validated,
published in the manifest and applied to no value.

`[Sensitive]` redaction here is structural rather than remembered: `JournalPayload` exposes no
accessor for a value and its only exit is `ToJson`, which redacts. An audit record is a new sink
for contract values, retained for years. Any design giving it its own serialiser is the second
exit that type was shaped to refuse.

[ADR-0028](ADR-0028-identity-arrives-on-the-invocation.md) §3 accepts that a durable flow's
authorisation is discontinuous across a wait, that an auditor reconstructing "who authorised
this transfer" must read two events, and that nothing writes them. So the schema is not free to
be a generic log line.

**Rejected:** deleting `redact` from the DSL — a breaking change made to fit an implementation
choice, and a reviewer asking "what did this step move" would get no answer; a dedicated
serialiser taking the value and a redact list — the second exit, plus a second copy of the
name-matching pass to drift; carrying the whole `ClaimsPrincipal` — credentials at rest for the
life of the record, which is ADR-0028 §2.2's objection and is not changed by the table having a
different name; writing a record for a failed step — stage 7 follows execution, and auditing
what did not happen is [10 §2](../10-Policy-Framework.md#2-fixed-stage-order--the-core-decision)'s
compensation row with a different noun.

---

## 2. Decision

**The record carries the payload the journal would have written, as a `JournalPayload`;
`redact` is a longer list of names handed to the one redaction pass; and the record says which
of ADR-0028's two principals authorised the step.**

**2.1 Payload.** `IStepDispatcher.DescribeAudit(stepIndex, ctx, redact)` returns a
`JournalPayload` composed by `JournalPayload.OfState` from a `request` member and a `result`
member. Generated code builds it, because only generated code can name a `JsonTypeInfo<T>`;
composition happens inside the payload type, so the composed document goes through the same
single redaction pass as every other payload. A sink reads it through `ToJson` and has no
accessor for the value, because the type has none.

**2.2 `redact`.** The generated method concatenates the flow's `SensitiveMembers` with the
policy's list and passes the union. Matching is the existing one — by name, case-insensitively,
at every depth. Two properties follow: an audit record is never more revealing than the journal
row for the same step, and `redact` **can only remove**, which is what makes it safe to accept
from the DSL without reviewing what a policy may name.

**2.3 `AuditAuthority`.** `Platform` when `FlowInvocation.IsContinuation`; else `Anonymous` when
the principal is not authenticated; else `Starter` when the execution has no journal frontier
and `Deliverer` when it has one. The frontier is what separates the two: a resumed execution
rehydrated a committed history, and ADR-0028 §2.2 keeps no claims on that row, so a principal on
a resumption can only have arrived through `SignalAsync`. `IsContinuation` is asked first, or
every sweep would file under `Anonymous` and "ran for nobody" would be ambiguous between a
public step and a platform continuation. The record also carries the capability's `Stance` and
`Permission`, which is what makes `Anonymous` readable.

**2.4 A missing or failing sink fails the step** — the one seam on the step path that does not
degrade. `FLOWX1032`'s third remedy is explicit that a flow whose
record is the reason the write is allowed should not ship without one. The record is written
after the commit and after the step joins the compensation stack, so a refusal unwinds the step
it was going to describe.

The record carries no schema version of its own — the payload already carries
`JournalPayload.SchemaVersion`, and a second one is
[ADR-0017](ADR-0017-manifest-v1-freeze-criteria.md) F2's fabrication — no category vocabulary,
and no outcome field.

---

## 3. Consequences

**Positive:** `redact` acts on something, and
`AuditPolicyTests.AMarkedMemberIsRedactedOutOfTheRecordAndSoIsTheDeclaredRedactList` asserts both
the two removals *and* the members that survive — a test checking only absences would pass
against an empty record. `TheTrailNamesTheStarterAndTheDelivererApart` starts an instance as one
principal and signals it as another, and reads two authorities off two records. No second exit
from a value was opened. `samples/banking` loses a `#pragma warning disable FLOWX1032` together
with the argument that justified it.

**Negative / accepted trade-offs:**

* **An `Ephemeral` flow's record carries no payload.** The audit reuses the journaled-contract
  list, which is emitted only for a `Durable` flow. The record still names the step and the
  principal; it is silent about what the step carried, and the DSL does not say so.
* **The `request` half is re-evaluated at the step boundary**, not captured at dispatch. Sound
  because a mapping is pure and deterministic (`FLOWX1011`) — but that is a diagnostic rather
  than a type, so it is an argument and not a guarantee.
* **A slow sink slows every audited step.** The write is awaited inline, inside the flow's
  deadline. That is the price of §2.4: a queued write that could be lost is not an audit trail.
* **`Principal` is a name and nothing else.** An auditor who needs the grants a caller *held*,
  rather than the one the step required, cannot get it from the record.

**Revisit when:** a delegation or impersonation model lands, which needs two principals on one
record and makes `Authority` a pair; or an `Ephemeral` flow needs a payload, at which point the
journaled-contract list is the wrong source; or a regime requires the record *before* the step
commits, which §2.4's ordering forbids; or `redact` is asked to do anything but remove.

---

**See also:** [ADR-0028](ADR-0028-identity-arrives-on-the-invocation.md) ·
[ADR-0044](ADR-0044-a-cache-is-a-plugin-store-keyed-by-the-redacted-input.md) ·
`FLOWX1032`

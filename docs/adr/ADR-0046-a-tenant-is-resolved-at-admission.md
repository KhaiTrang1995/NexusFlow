# ADR-0046: A tenant is resolved at admission, derived from claims, and enforced by the database

**Status:** Accepted
**Date:** 2026-08-02
**Amends:** [16 — Multi-Tenancy §3](../16-Multi-Tenant.md#3-tenant-resolution) ·
[25 — Remaining Platform §1](../25-Remaining-Platform.md)
**Relates to:** [ADR-0027](ADR-0027-authorisation-runs-in-the-step-loop.md) ·
[ADR-0028](ADR-0028-identity-arrives-on-the-invocation.md) ·
[ADR-0029](ADR-0029-a-refusal-is-a-result-failure.md)

> **This record exists because implementing the design in [25 §1](../25-Remaining-Platform.md)
> found three places where it does not hold as drawn.** Two are shapes that cannot be built
> against the types that exist; the third is a piece of SQL that compiles, runs, reports
> success and isolates nothing. Each is corrected below with its reason, so that the divergence
> is a decision rather than a silent edit.

---

## 1. Context

`TenantId` reaches `TriggerEnvelope`, `FlowInvocation`, `FlowContext`, `JournalWrites` and the
`flow_instance.tenant_id` column indexed since migration `0001`, and `FlowTelemetry` tags spans
with it. **Nothing derived it, validated it, or isolated on it.** A caller supplied a tenant id
and it was believed; nothing stopped one tenant's flow reading, resuming or recovering
another's instance.

This is the shape [ADR-0027](ADR-0027-authorisation-runs-in-the-step-loop.md) found in
authorisation — a complete abstraction with the wire cut at the last inch — and it is worse,
because it is a data-isolation defect rather than a missing feature.

### 1.1 Where the decision could go

**In the step loop, where authorisation goes.** Rejected. ADR-0027 put authorisation there
because a stance belongs to a *capability*, and which capabilities a flow reaches is decided at
run time by `When`, `Switch` and `ForEach` — so no earlier point knows which stances apply, and
a check at the trigger would refuse the union or admit the intersection. **None of that
transfers to a tenant.** A tenant belongs to the invocation as a whole: every step has the same
one, no branch can change it, and there is no union to over-refuse. What a per-step check
*would* cost is real — the tenant has to bind the store connection *before* the first read, and
a decision taken after that has already read the row it was meant to protect.

**At each transport.** Rejected, on [ADR-0004](ADR-0004-universal-trigger-model.md)'s grounds:
it puts one rule in every plugin, which is "no parallel system to get out of sync" lost at the
first opportunity. It is also where the defect already lives — `HttpTriggerReader` derives a
tenant correctly today and nothing downstream checks its work.

**At admission, in `FlowHost`.** Chosen. It is the one point every activation passes through,
it is before the lease and before the instance row — which is what
[16 §3](../16-Multi-Tenant.md#3-tenant-resolution) requires, "*rejected at admission, before a
flow instance exists*" — and it is where the store connection is about to be bound.

ADR-0027 anticipated exactly this and said so in its own *Revisit when*: "*a stance has to be
decided before the flow starts rather than per step — an admission-time quota, for instance —
at which point §1.1's rejection of the trigger needs re-arguing rather than citing*". This
section is that re-argument.

---

## 2. Decision

### 2.1 The resolver takes a `FlowInvocation`, not a `TriggerEnvelope`

[25 §1](../25-Remaining-Platform.md)'s class view draws `Resolve(TriggerEnvelope)`.
**`FlowHost` never sees a `TriggerEnvelope`.** `HttpTriggerReader.Read` converts an
`HttpContext` straight to a `FlowInvocation`; no transport in the repository constructs an
envelope on the path to the host. Honouring the drawn signature would mean either resolving at
the transports — §1.1's rejected option — or manufacturing an envelope at the host purely to
satisfy a parameter type.

So the seam takes what is actually there. `FlowInvocation` is the normalised carrier of exactly
the two fields the decision needs: the tenant the call asserts, and the principal it
authenticated.

### 2.2 `TenantResolution`, with three outcomes

[16 §3](../16-Multi-Tenant.md#3-tenant-resolution) specifies
`ValueTask<TenantId?> ResolveAsync(in TriggerEnvelope, CancellationToken)`. Three changes, each
load-bearing:

* **Three outcomes, not a nullable.** A `string?` collapses *"this deployment does not isolate"*
  into *"this call named no tenant"*, and the convenient reading of the second — carry on — is
  the cross-tenant read. `TenantResolution` carries `Refused` and `Reason` alongside
  `TenantId`, so refusal is distinguishable from not-configured. [25 §1](../25-Remaining-Platform.md)
  says the same in one line and is followed here in preference to 16 §3.
* **Synchronous.** I/O at admission is what [16 §4](../16-Multi-Tenant.md#4-fairness--the-noisy-neighbour-problem)
  warns against — "*rejecting expensively is how rate limiting becomes the DoS*". A deployment
  needing a directory lookup does it when it issues the token, not when it spends one. The same
  constraint ADR-0027 accepted for `StepAuthorization.Decide`.
* **No `TenantId` type.** 16 §3 names one; none exists, and the column, the envelope, the
  invocation and the context all carry `string?`. Introducing a wrapper here would convert at
  four boundaries to gain nothing this work package uses.

### 2.3 The resolver derives, and treats what arrived as an assertion to check

**This is the substance.** `FlowInvocation.TenantId` arrives already populated, so a resolver
that returned it would have validated nothing. `ClaimTenantResolver` derives the tenant again
from `Principal`'s validated claims and compares:

| Claims say | Call asserts | Outcome |
|---|---|---|
| `A` | nothing, or `A` | admitted as `A` |
| `A` | `B` | **refused** — `tenant.cross_tenant_denied` |
| nothing | anything | **refused** — `tenant.required` |

That second row is [16 §3](../16-Multi-Tenant.md#3-tenant-resolution)'s rule made enforceable:
"*a resolver returning a tenant not present in validated claims fails `CrossTenantAccessTest`*".

A **continuation** is exempt and it is not a bypass: a timer sweep and a recovery scan carry no
claims and never will, because [ADR-0028](ADR-0028-identity-arrives-on-the-invocation.md) keeps
claims off the journal row on purpose. The tenant such an invocation carries came from the row,
which the platform wrote when it admitted the call that started it. Deciding against that
absence would turn every node restart into an outage. This is `IsContinuation` used for the
reason ADR-0027 already uses it.

### 2.4 A refusal is a `Result` failure, `ErrorCategory.Forbidden`

`TenantErrors.TenantRequired` and `TenantErrors.CrossTenantDenied`, per
[ADR-0007](ADR-0007-result-over-exceptions.md) and
[ADR-0029](ADR-0029-a-refusal-is-a-result-failure.md). Thrown, a bus consumer would dead-letter
a message that was merely not admissible. `Forbidden` is terminal, so no retry acts on it —
correct, because the repair is a different token, not a later attempt.

`TenantErrors.IsolationNotSupported` is `Internal` and is raised by the **options validator** at
startup: a deployment configuring `Schema` or `Database` gets a pod that never becomes ready
rather than one silently serving `Row`. Failing closed on an isolation level is not optional.

### 2.5 Isolation is bound to the connection, and the database is what refuses

`PostgresFlowJournal` implements `ITenantScopedJournal`; `FlowHost` binds it once, where the
tenant is resolved, and every statement of that execution travels a restricted connection. The
tenant is not a parameter on `IFlowJournal`'s six members — it has no meaning for `FenceAsync`
or `ReadOutboxAsync`, and threading it everywhere would give every call site a chance to pass
the wrong one, which is the "*someone forgets it once, in one query*" failure
[16 §1](../16-Multi-Tenant.md#1-tenancy-is-a-platform-concern-not-an-application-concern) opens
by naming. An `AsyncLocal` scope was rejected on ADR-0028's grounds, which apply with more
force here: a lost identity refuses a step, a lost tenant scope reads the wrong rows.

### 2.6 The RLS in `16 §5` does not isolate as written, and migration `0006` corrects it

[16 §5](../16-Multi-Tenant.md#5-data-isolation) gives:

```sql
ALTER TABLE flow_instance ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON flow_instance
  USING (tenant_id = current_setting('flowx.tenant_id'));
```

Applied verbatim, **this returns every tenant's rows to the account that runs the migrations**,
which is the ordinary arrangement and is exactly what this repository's own test connection is.
Three independent defects:

1. **A superuser bypasses RLS unconditionally, and an owner bypasses it without `FORCE`.**
   The policy installs, reports success and is never consulted. Corrected by
   `FORCE ROW LEVEL SECURITY` *and* by the runtime assuming a `flowx_tenant` role that holds
   neither privilege — the second is what makes the first reachable.
2. **`current_setting('flowx.tenant_id')` raises `42704` when unset.** Every statement on an
   unscoped connection would error, taking out every single-tenant deployment that applied the
   migration. Corrected by the two-argument form, which returns `NULL`.
3. **`=` is the wrong comparison for a nullable partition key.** Single-tenant rows carry
   `tenant_id IS NULL` and `NULL = NULL` is `NULL`, which a policy reads as *no*. Corrected by
   `IS NOT DISTINCT FROM`, with `nullif(…, '')` folding the two spellings of "no tenant"
   together — `RESET` restores a custom setting to the empty string rather than to `NULL`.

The resulting predicate **fails closed**: an unscoped connection under the restricted role sees
rows with no tenant, which is nobody's data in a deployment where every row carries one.

### 2.7 Journal partitioning is out of scope

[11 §6](../11-Distributed-Runtime.md) names sharding as a lever and stops, so building it would
be invention. `Row` isolation is what the documentation specifies in enough detail to
implement, and [25 §1](../25-Remaining-Platform.md) says so explicitly.

---

## 3. Consequences

**Positive:**

* **The published contract is enforced, and the enforcement is proved negatively.**
  `TenantIsolationTests` and `TenantHostIsolationTests` assert what tenant *A* **cannot** reach
  of tenant *B*'s — read, frontier, fence, commit, recovery candidate, timer candidate, host
  resume — in **both directions**, against real PostgreSQL. A test that only showed *A* reaching
  *A* would have passed on every commit since `0001`.
* **Defence in depth is real rather than claimed.** The runtime refuses at admission and the
  database refuses at the row. Either alone leaves a hole: refusal without scoping leaks to
  anyone holding an instance id, scoping without refusal writes rows no tenant can read back.
* **Single-tenant deployments pay nothing.** `TenantIsolation.None` is the default and is one
  field comparison per invocation: no claim walked, no connection bound, no statement issued.
  Budget **B2** is untouched and `EngineAllocationTests` is what says so.
* **One list of claim types.** `HttpTriggerReader.TenantClaimTypes` became a projection of
  `ClaimTenantResolver`'s. Two copies would drift, and a transport gaining an entry the resolver
  lacked would produce invocations the resolver then refused.

**Negative / accepted trade-offs:**

* **A scoped connection costs one extra round trip per journal call.** Two session settings, one
  statement, issued on open. Paid only by a deployment that selected `Row`. `SET LOCAL` inside a
  transaction would be leak-proof by construction but costs two round trips on the reads that
  have no transaction of their own; re-applying on every open buys the same guarantee for one.
* **`flow_lease` is not isolated, deliberately.** A lease is acquired *before* the instance row
  exists — `0001` says so, and it is why the table carries no foreign key — so a policy joining
  it to `flow_instance` would refuse the first acquisition of every instance and stop durability
  outright. A lease row holds an id, a node name, a token and an expiry: no tenant data, and the
  fence already stops one node writing another's instance.
* **Bus and schedule triggers are refused in an isolating deployment.**
  [16 §3](../16-Multi-Tenant.md#3-tenant-resolution) gives them tenant sources of their own and
  neither is built: `CronTriggerAttribute.PerTenant` is declared and inert, and no bus header
  carries a tenant. They reach the resolver with no principal and are refused with
  `tenant.required`, which names what is missing. The alternatives were worse — admitting them
  untenanted writes rows no tenant can read back, and admitting whatever they asserted is the
  believing this record exists to stop. **This is the largest known gap and it fails closed.**
* **`Schema` and `Database` are declarable and refused.** Naming them in `TenantIsolation`
  without implementing them is the "declared and inert" shape this work package exists to
  remove. It is tolerated only because the refusal is at startup and total.
* **The decision is synchronous** (§2.2), so a tenant needing a directory lookup cannot be
  expressed. Same trade ADR-0027 took, reopening on the same trigger.
* **A store that is not an `ITenantScopedJournal` gets admission control and no second wall.**
  `FlowDurability.CanIsolateTenants` reports it rather than leaving it to be discovered.

**Revisit when:** a transport needs a tenant source that is not a claim — a bus header or a
schedule's declared tenant — at which point §2.3's table gains a row and the envelope's
`TriggerKind` becomes necessary, reopening §2.1; or `TenantIsolation.Schema` is implemented, at
which point `ITenantStoreResolver` arrives and §2.5's single bound journal becomes a resolved
one; or journal partitioning is specified rather than named, reopening §2.7; or a tenant
decision needs I/O, reopening §2.2 together with ADR-0027's equivalent clause; or
`EngineAllocationTests` records a non-zero figure for a single-tenant plan, which would mean the
default stopped being free.

---

**See also:** [16 — Multi-Tenancy](../16-Multi-Tenant.md) ·
[25 — Remaining Platform](../25-Remaining-Platform.md) ·
[ADR-0027](ADR-0027-authorisation-runs-in-the-step-loop.md) ·
[ADR-0028](ADR-0028-identity-arrives-on-the-invocation.md)

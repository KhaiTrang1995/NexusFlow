# ADR-0054: A trigger with no caller attests its tenant, and holds its work when it cannot

**Status:** Accepted
**Date:** 2026-08-02
**Supersedes:** [ADR-0053 §2](ADR-0053-the-outbox-fans-out-and-the-change-feed-cannot-yet.md)'s
refusal of `AddFlowXPostgresChangeFeed`, on its own stated revisit condition
**Relates to:** [ADR-0046](ADR-0046-a-tenant-is-resolved-at-admission.md) ·
[ADR-0028](ADR-0028-identity-arrives-on-the-invocation.md) ·
[ADR-0031](ADR-0031-an-occurrence-names-the-instance-it-starts.md)

---

## 1. The contested question: what is a tenant derived *from*

ADR-0046 says a tenant is derived from validated claims and never believed from the call. That
sentence has one silent premise — that a call has a caller — and three triggers do not. A change,
a broker message and a cron occurrence reached `ClaimTenantResolver` with no principal, were
refused with `tenant.required`, and so **no non-HTTP trigger could start a flow in a tenanted
deployment at all**, at `Row` as much as at `Schema`.

The rejected repair is the one already in the code: `FlowInvocation.IsContinuation` carries a
tenant without claims. It is wrong here for a reason that is not a matter of taste — ADR-0028 also
gives that flag the authorisation bypass, which is correct for a sweep resuming an instance the
platform already admitted and false for a start, where nothing has decided anything yet. Reusing
it would have made every bus and cron flow skip the stances its own author declared.

So `FlowInvocation.TenantAttested` is a second flag, and what it asserts is **provenance rather
than authority**: this tenant came from somewhere no caller can set. The three sources are
docs/16 §3's own, built:

* a **change** is read out of a tenant's data — its schema at `Schema`, and at every level the
  emitting instance's `flow_instance.tenant_id`, which is the same foreign key the table's
  row-level policy decides visibility through;
* a **message** carries a broker field its publisher wrote from `OutboxRecord.TenantId`, beside
  the body and never inside it;
* a **cron occurrence** is fanned out over an `ITenantDirectory` when the declaration says
  `PerTenant`, and the tenant becomes a term of the derived instance id — without it, ten tenants
  share one id and the journal's primary key refuses nine of them.

An attested start is a start. Step authorisation runs, against a principal that is absent, so a
capability declaring `Authenticated` under a cron trigger is refused rather than run.

## 2. A trigger that cannot name a tenant holds

`tenant.required` was in neither scan's list of dispositions that stop progress, so a change scan
committed its cursor past a change no flow ran and a bus scan acknowledged a message admission
never let start. Both are now held: the cursor does not move, the broker keeps the message, and
the schedule sweep counts the occurrence as failed rather than fired. A stopped subscription is
visible and recoverable; a committed cursor is neither. All five tenant refusals are treated the
same way, because the three fairness ones journal nothing either.

## 3. Consequences

**Positive:**

* Every trigger kind works under `Row` and `Schema`. `AddFlowXPostgresChangeFeed` no longer
  refuses schema-per-tenant — ADR-0053 §2's revisit condition was exactly this admission path.
* The tenant a change starts in and the tenant the database would let it read are the same fact,
  read through the same key.

**Negative / accepted trade-offs:**

* **A subscription that cannot name a tenant stops for ever** until a human repairs it. That is
  the deliberate half of §2, and the alternative is losing the changes quietly.
* **A change-feed pass reads one tenant.** `IChangeFeed.CommitAsync` takes one position, so a
  merged batch would span several `change_cursor` rows; the fan-out rotates and returns the first
  tenant with anything, and the tenant travels inside the opaque position so the contract grows no
  parameter.
* **`FlowXOptions.Tenants` is a declared list at `Row`.** Row isolation has no registry — a tenant
  exists there the moment a token carrying its claim arrives — so a per-tenant schedule fires for
  the tenants the deployment names and no others. At `Schema` the registry answers instead.

**Revisit when:** a store gains a tenant registry at row isolation, at which point the declared
list becomes the fallback rather than the answer; or a second change feed exists and one pass per
tenant is measured to be the bottleneck.

---

**See also:** [ADR-0053](ADR-0053-the-outbox-fans-out-and-the-change-feed-cannot-yet.md) ·
[16 — Multi-Tenancy](../16-Multi-Tenant.md) · [09 — Trigger Model](../09-Trigger-Model.md)

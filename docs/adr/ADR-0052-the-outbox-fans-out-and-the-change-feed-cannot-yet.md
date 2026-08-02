# ADR-0052: The outbox publisher fans out per tenant; the change feed is blocked by admission, not by its cursor

**Status:** Accepted
**Date:** 2026-08-02
**Supersedes:** [ADR-0051 §4](ADR-0051-database-isolation-is-a-topology-not-a-runtime-level.md)'s
refusal of `AddFlowXPostgresOutbox`, and its stated reason for refusing
`AddFlowXPostgresChangeFeed`
**Relates to:** [ADR-0018](ADR-0018-outbox-publication-and-ordering.md) ·
[ADR-0048](ADR-0048-a-change-feed-advances-a-cursor.md) ·
[ADR-0028](ADR-0028-identity-arrives-on-the-invocation.md)

---

## 1. The reason ADR-0051 §4 gave does not distinguish the two

§4 refused both loops because each "claims rows and advances a position, so one per tenant is a
different contract rather than the same one asked twice". **That is not what happens.** A claim
was always confined to one `outbox_event`: `FOR UPDATE SKIP LOCKED` locks rows in one table, and
the `NOT EXISTS` that holds an event behind an older sibling of its `partition_key` reads that
same table. Give each tenant a table and both clauses answer the same question over less data.
ADR-0018 promises order within a key and nothing across keys, so nothing is lost — two tenants
that chose the same key string were never one stream, and at `Row` isolation their sharing a
table made one wait behind the other, which was an accident of colocation rather than a promise.

So the publisher fans out. What genuinely had to be built is what the single-schema loop got for
free: the tenant set, re-read every pass from the registry so a tenant another node provisioned a
second ago is drained; a rotating visit order, so a tenant with a permanent backlog cannot spend
the batch budget of the tenant behind it; and a failing tenant recorded on the pass and stepped
over — which the two sweeps deliberately do not do, because a sweep that abandons a page runs
again in a second and a drain that abandons a tenant stops it publishing for the length of the
outage.

## 2. The change feed's cursor fans out too; its delivery does not

`change_cursor` is keyed by subscription, so one row per tenant schema is the same key in a
different table. The visibility barrier survives the split for a reason worth writing down: it is
`staged_xid < pg_snapshot_xmin(pg_current_snapshot())`, and transaction ids are **cluster-wide**,
not per schema — so the predicate means the same thing in every tenant's schema and cannot skip a
row. It is conservative in one direction only: one tenant holding a write transaction open holds
every tenant's barrier down, which costs latency and never correctness, and which no fan-out can
repair because the counter is not the schema's to begin with.

**What blocks the feed is one level up.** A change observed in tenant *A*'s schema has to start a
flow *in* tenant A. `FlowChangeScan` starts one with no principal, so `ClaimTenantResolver`
refuses it — at `Row`, which shipped, as much as at `Schema`. The only invocation that carries a
tenant without claims is `FlowInvocation.IsContinuation`, and ADR-0028 §2.3 gives that flag to the
platform *continuing an instance it already admitted*; it also skips step authorisation, which is
right for a recovery scan and wrong for a start.

The consequence is why refusing beats fanning out. `tenant.required` is not among the dispositions
that hold the cursor, so a change scan reads the refusal as progress and commits past a change no
flow ran — asserted in `ChangeScanTests.AChangeStartsNoFlowWhereTheDeploymentIsolatesByTenant`. A
fanned-out feed would do that to every change of every tenant, silently. The empty table is the
better failure, and `AddFlowXPostgresChangeFeed` now refuses with this reason rather than with §4's.

## 3. Consequences

**Positive:**

* A deployment isolating by schema can publish. Before this it had a journal, two sweeps and no
  way to get an event out of the database at all.
* `OutboxRecord.TenantId` carries whose schema a row was claimed from, which is the only place
  that fact exists once the row is read — `outbox_event` has no such column at any level.
* The refusal that remains names the decision that is missing, so the next reader does not repeat
  this analysis to discover the cursor was never the problem.

**Negative / accepted trade-offs:**

* **A pass is serial across tenants.** One slow broker call delays the tenants after it in that
  pass, bounded by the rotation rather than removed by it. Publishing tenants concurrently would
  need a bound on in-flight broker calls that nothing specifies.
* **The first tenant to fail is the only one reported.** `OutboxPass` carries one `Error`, and a
  list would be a different shape for the single-schema case to carry too.
* **`[ChangeTrigger]` is unavailable above `TenantIsolation.None`** — not only at `Schema`. The
  refusal is registered for `Schema` because that is where the empty table makes it silent.

**Revisit when:** a platform-initiated trigger is given an admission path that carries a tenant
without granting a continuation's authorisation bypass — at which point the change feed, the bus
scan and the schedule scan all become usable under isolation and this record's §2 is reopened; or
a broker plugin ships and per-tenant concurrency in one pass has to be bounded.

---

**See also:** [ADR-0051](ADR-0051-database-isolation-is-a-topology-not-a-runtime-level.md) ·
[16 — Multi-Tenancy](../16-Multi-Tenant.md)

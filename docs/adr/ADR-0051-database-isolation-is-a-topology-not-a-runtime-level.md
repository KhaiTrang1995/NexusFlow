# ADR-0051: Schema isolation is a pool per tenant; `Database` isolation is a topology, not a runtime level

**Status:** Accepted
**Date:** 2026-08-02
**Amends:** [16 — Multi-Tenancy §5 L2](../16-Multi-Tenant.md#5-data-isolation) ·
[25 — Remaining Platform §1](../25-Remaining-Platform.md)
**Relates to:** [ADR-0046](ADR-0046-a-tenant-is-resolved-at-admission.md) ·
[ADR-0040](ADR-0040-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md)

> **This record exists because [ADR-0046](ADR-0046-a-tenant-is-resolved-at-admission.md)
> predicted a different design and named this as its own revisit trigger** — "*or
> `TenantIsolation.Schema` is implemented, at which point `ITenantStoreResolver` arrives and
> §2.5's single bound journal becomes a resolved one*". It did not arrive, and the fourth level
> is not being built at all. Both are decisions rather than omissions.

---

## 1. `Schema` needs no new abstraction, because the seam already resolves per tenant

[16 §5](../16-Multi-Tenant.md#5-data-isolation) draws `ITenantStoreResolver` mapping
`TenantId` → connection, and ADR-0046 expected it in `FlowX.Abstractions`. **It would have had
no second implementer and no caller above the plugin.** `ITenantScopedJournal.ForTenant` is
already the per-tenant seam; what schema isolation changes is what the *store* does when asked
for a tenant's journal, which is the store's business. So the map — `PostgresTenantStores` —
lives in `plugins/FlowX.Postgres`, and the abstraction gains one property instead of one
interface: `ITenantScopedJournal.Isolation`, which is how a host learns that the store it was
handed cannot serve the level the deployment declared.

That property is load-bearing. A row-filtering journal and a schema-per-tenant journal are the
same class presenting the same seam, so without it a deployment that configured `Schema` and
forgot `PostgresJournalOptions.TenantSchemas` would keep every tenant's rows in one schema while
its configuration said otherwise, and nothing would report a problem. `FlowHost`'s constructor
refuses that with `tenant.isolation_not_enforceable` — a different error from
`tenant.isolation_not_supported` because the repair is a registration, not a different level.

## 2. The schema is in the connection string, not in a `SET`

**A pool per tenant, bounded, and the pooling is the whole decision.** `Row` isolation changes
what a policy filters; `Schema` changes where a connection points, and that is session state on
a physical socket which outlives the borrower. A shared pool with `SET search_path` per borrow
is the same class of defect `TenantScope` guards against, with none of its defence: a stale
`flowx.tenant_id` makes the next borrower see *nothing*, which is loud, while a stale
`search_path` makes it see a full table of somebody else's rows, which is not.

So each tenant's schema is written into that tenant's own connection string, which Npgsql sends
in the startup packet. There is no statement that could change it, `DISCARD ALL` restores it
rather than clearing it, and the pools are disjoint — the question "could a pooled connection
carry a tenant's schema to the next borrower" has no borrower to carry it to. It also answers
[16 §9](../16-Multi-Tenant.md#9-anti-patterns)'s "connection pool shared across L2 tenants"
directly rather than by discipline.

**The row policy is applied on top, not instead.** A connection from a tenant's pool still
assumes `flowx_tenant` and still sets `flowx.tenant_id`, so migration `0008` is live inside every
tenant schema. The two walls fail in opposite directions: a wrong `search_path` lands on rows
whose `tenant_id` is somebody else's and the policy hides them; a wrong `flowx.tenant_id` lands
in a schema holding no such rows. Neither mistake reads another tenant's data.

## 3. A tenant's schema is created and migrated on first use

Rejected: refusing an unprovisioned tenant by default. It would make the level unusable without
a control plane this repository does not ship, which is the "declared and inert" state the level
is being built to leave. `PostgresMigrator` already takes an advisory lock keyed on the schema
name and applies its scripts in one transaction, so two nodes meeting the same new tenant produce
one migrated schema and one no-op — the mechanism that makes a rolling update safe, reused.
`TenantSchemaOptions.ProvisionOnFirstUse` is off for a deployment that forbids runtime DDL, and
then an unprovisioned tenant is refused *by name* rather than met with a missing relation.

Schema names are **derived** from the tenant id — a readable slug plus a digest of the exact id —
rather than looked up. A collision is not an error, it is two tenants sharing every row they own
with no policy anywhere with a reason to object, so the digest is what carries the distinction.
A registry table in the control schema records which tenants exist, and **nothing on the
execution path reads it**: a stale registry can cost a sweep a tenant and can never misroute a
read.

## 4. The node-wide sweeps fan out, and that is not optional

Under `Schema` the control schema's `flow_instance` is empty. A recovery scan or timer sweep left
unchanged would sweep correctly, find nothing, report no error, and strand every abandoned
instance and every parked one in the deployment. `PostgresTenantRecoveryIndex` and
`PostgresTenantTimerIndex` ask each registered tenant and merge, restoring both the staleness
ordering and `PerTenantLimit` — which becomes each tenant's own `Limit`, since the window function
that expresses it cannot see across schemas.

**The outbox publisher and the change feed are refused instead of fanned out.** They claim rows
and advance a position, so one per tenant is a different contract rather than the same one asked
twice, and nothing specifies it. A publisher silently draining an empty table would be the same
failure this level exists to remove, so `AddFlowXPostgresOutbox` and `AddFlowXPostgresChangeFeed`
throw when tenant schemas are on. **This is the largest known gap at this level.**

`flow_lease` stays in the control schema, node-wide and shared — ADR-0046 §2.6's reason, plus one
that is specific to this level: a lease is taken before the instance row exists, so there is no
tenant yet with which to choose a schema.

## 5. `Database` stays refused, and the reason is that it is not a runtime level

[16 §2](../16-Multi-Tenant.md#2-isolation-levels) gives `Database` as L3/L4: *a dedicated
deployment*. **A dedicated deployment serves one tenant**, so the pod running it points its
connection string at that tenant's store and declares `None`; the separation is the topology and
the runtime's part in it is to have no part in it. Read as an in-process level — a connection
string per tenant inside one host — it is L2, which §2 puts on the same row as schema-per-tenant
and which `Schema` now serves.

Two consequences follow that implementation would not remove:

* **Every store multiplies, not just the journal.** `flow_lease` is acquired before the instance
  row exists and therefore before its tenant's store could be selected. Within one database that
  is answered by keeping leases in the control schema; across databases there is no shared table
  to keep them in, and `ILeaseStore` has no scoping seam. Fencing, renewal and the recovery scan's
  steal would each become per-tenant, which is a distributed lease protocol nothing specifies.
* **Nothing could enumerate the tenants.** §4's fan-out rests on a registry that lives in the same
  database as the schemas it lists. Across databases there is no such table and no cross-database
  query, so the set would come from configuration — and a deployment that mis-supplied it would
  get isolation with silently missing recovery, which is worse than the level it replaced.

So the refusal is the answer, not a gap, and `TenantErrors.IsolationNotSupported` says so in the
message an operator reads.

---

## 6. Consequences

**Positive:**

* **Proved negatively, in both directions, at the level.** `TenantSchemaIsolationTests` and
  `TenantSchemaHostIsolationTests` assert what tenant *A* cannot reach of *B*'s — instance,
  frontier, commit, recovery candidate, timer candidate, host resume — and that the two sweeps
  reach *both* tenants, which is the failure schema isolation introduces.
* **The pooling property is asserted about the connection, not about a read.** A read can be
  refused for the right answer by the wrong wall, so the test reads `current_schema()` from a
  connection borrowed alternately for two tenants.
* **The whole journal suite runs through a tenant's pool**, unmodified, into a schema created and
  migrated at run time. It found a defect that had shipped with `Row`: migration `0008` granted
  the tables and not `outbox_event_staged_seq`, so any tenanted step emitting an event was refused
  `42501` and rolled back. Migration `0010` is the grant.

**Negative / accepted trade-offs:**

* **The outbox publisher and change feed are unavailable at this level** (§4).
* **A connection pool per tenant is a real cost**, bounded at ten by default because the
  multiplier is the tenant count and PostgreSQL's own `max_connections` default is a hundred for
  the whole server.
* **DDL runs in the request path** for a tenant seen for the first time, once per tenant per
  process. `ProvisionOnFirstUse` is the switch for a deployment that will not have it.
* **A tenant is never de-provisioned.** [16 §7](../16-Multi-Tenant.md#7-tenant-lifecycle)'s
  `offboard`/`purge` would drop a schema and a registry row; neither is built.

**Revisit when:** a broker plugin exists and per-tenant outbox publication has to be decided,
reopening §4; or `flowx tenant purge` is built, at which point de-provisioning arrives; or a
second store implements `ITenantScopedJournal` with its own notion of a schema, at which point
§1's decision to keep the map in the plugin is re-argued rather than cited.

---

**See also:** [16 — Multi-Tenancy](../16-Multi-Tenant.md) ·
[25 — Remaining Platform](../25-Remaining-Platform.md) ·
[ADR-0046](ADR-0046-a-tenant-is-resolved-at-admission.md)

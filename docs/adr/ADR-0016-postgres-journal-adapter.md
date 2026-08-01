# ADR-0016: What ADR-0015 looked like against a real database

**Status:** Accepted
**Date:** 2026-07-31
**Deciders:** Runtime team, Platform architecture
**Amends:** [ADR-0015](ADR-0015-journal-schema-and-durable-execution.md)) ·
[11 §2](../11-Distributed-Runtime.md#2-the-journal)

> This record exists because [ADR-0015](ADR-0015-journal-schema-and-durable-execution.md))
> asked for it. Its own header says the status "is decided" at WP-53, against Postgres,
> because everything it commits to is *storage* and the in-memory reference "cannot disagree
> with a single clause". This is what disagreed.

## Context

WP-53 built `plugins/FlowX.Postgres` — schema, migrations, `IFlowJournal`, `ILeaseStore`,
`IRecoveryIndex`, retention — and ran the WP-51 conformance suite against PostgreSQL 16.13.
The suite was inherited from `tests/FlowX.Conformance.Tests` **unmodified, from a different
assembly**,
which is the arrangement [17 §5](../17-Plugin-System.md) describes for a third party
claiming conformance. Nothing in that project changed to accommodate this adapter.

Result: **63 conformance assertions green against a real database**, plus 41 adapter tests —
104 in `tests/FlowX.Postgres.Tests`. *This line read "45 … plus 18" and then "45 … plus 41";
both were taken at different moments and neither was re-derived. The 45 became 63 when
`RecoveryIndexConformance` closed the first of decision 4's two named gaps.*

Three clauses did not survive. Two of them are in the ERD drawn in
[11 §2](../11-Distributed-Runtime.md#2-the-journal), which ADR-0015 already marks
superseded; the third is an inconsistency inside ADR-0015 itself.

**Decision 4 was added later and is a different kind of finding.** The first three came
from a database refusing what a document claimed. The fourth came from reading this adapter
against [11 §3](../11-Distributed-Runtime.md#3-leases-and-fencing) and noticing that two
shipped work packages had never been connected to each other — a gap no test could have
reported, because a scan nobody registers has nothing to fail.

## Decision

### 1. Payload columns are `json`, not `jsonb` — ADR-0015 commitment 5 requires it

**The ERD types `input`, `state_bag`, `result`, `nondeterministic` and `payload` as
`jsonb`. Against `jsonb`, ADR-0015 commitment 5 is false.**

`jsonb` is a parsed representation, not a document. It sorts object keys, re-renders
separators, and discards all but the last of a repeated key. Measured on 16.13:

```
written by the generated STJ context:  {"OrderId":"order-7","PaymentToken":"tok","Quantity":3}
read back from a jsonb column:         {"OrderId": "order-7", "Quantity": 3, "PaymentToken": "tok"}
```

Commitment 5 says payloads are "written through the generated `System.Text.Json` context",
and `JournalConformance.APayloadIsStoredAsTheGeneratedContextWroteIt` reads that as *what
the context produced is what comes back* — it asserts the stored text contains
`"Quantity":3`. It **fails against `jsonb`** and passes against `json`. This was confirmed
by building the adapter both ways rather than by reading the manual.

`json` stores the document verbatim. The cost is real and accepted: no GIN index, and every
JSON operator re-parses. It is the right trade for this table anyway — a journal payload is
written once, read whole, and never queried by key. **The journal is the replay source and
the audit source, and neither survives a store that silently reorders what it was given.**

### 2. `flow_lease` carries no foreign key to `flow_instance` — the ERD's is inverted in time

The ERD makes `flow_lease.instance_id` `PK,FK`. It cannot be. `FlowInstanceStart.Token` is
documented as "the token of the lease held while starting", so **the lease is acquired
before the instance row exists**. The foreign key would refuse the first acquisition of
every flow.

It would also make a Postgres lease store unusable for a journal kept elsewhere — the
arrangement `ILeaseStore`'s own remarks describe ("a Redis lease store and a Postgres
journal share no transaction") and the one WP-54 exists to demonstrate.

Pinned by `SchemaContractTests.ALeaseIsTakenBeforeTheInstanceExists`.

### 3. The state-bag snapshot needed a position column, which nothing gave it

ADR-0015's Consequences names the snapshot as budget B8's mitigation — "the state-bag
snapshot on the instance row, which bounds the scan to rows committed after it". **Neither
the Decision nor the ERD gives it a column recording which commit it came from**, and
without one it bounds nothing: a resume still has to read every row to discover which the
snapshot already covers.

Added as `flow_instance.state_bag_sequence`, in migration `0002` rather than `0001`, so
that the expand/contract rule in [11 §7](../11-Distributed-Runtime.md#7-deployment-safety)
has a worked example in this schema from the start.

### 4. `IRecoveryIndex` is a third class, not a second interface on the journal

Added after the record was first written, because the gap it closes was found by reading
this adapter against [11 §3](../11-Distributed-Runtime.md#3-leases-and-fencing) rather than
by any test: **WP-53 shipped a store and WP-55 shipped a recovery scan, and nothing
connected them.** The only `IRecoveryIndex` anywhere was a test double, and
`FlowXServiceCollectionExtensions` resolves it with `GetService`, so a Postgres-backed host
silently swept nothing. It fenced correctly and picked up no dead node's work — which is
the failure mode an optional dependency produces when the only production implementation
declines to supply it.

`PostgresRecoveryIndex` is a separate class. The argument for folding it into
`PostgresFlowJournal` is real — the query reads `flow_instance`, the journal's own table —
and it loses on three counts. `IRecoveryIndex` was split out of `IFlowJournal` precisely
because a scan is not part of *executing* an instance; putting it back gives the type every
durable write passes through a member no write uses and `JournalConformance` says nothing
about. Nothing is gained in wiring, because the container matches on service type, so a
journal that also implemented it would still need its own registration line. And it makes
"this node does not sweep" a declined registration rather than a substituted journal. The
tie-break is precedent: `PostgresRetention` already reads and deletes `flow_instance` from
outside the journal for the same reason, so this is the third class over those tables and
not the first.

**`0002`'s index was the wrong shape, and this is the record correcting itself twice.**
That migration created `flow_instance_recovery_idx` on `(state, updated_at)` and its
comment claims it "supports the recovery scan WP-55 adds". It does not: with `state`
leading, an index scan yields rows grouped by state, so the `ORDER BY updated_at` the
contract requires inherits no ordering. Measured on 200 000 rows against PostgreSQL 16, a
page of 64 cost a parallel sequential scan and a top-N heapsort touching **1 748 buffers**;
against `(updated_at)` partial on the same states it is an ordered index scan touching
**4**. The ordering is not cosmetic — `ListAbandonedAsync`'s remarks make it the
anti-starvation property. Migration `0003` adds the right index and **leaves the wrong one
in place**, because superseded is not unused and a drop belongs to a release after one that
ships with nothing planning against it.

**Two gaps were left open here, named rather than closed. One is now closed.**
*`RecoveryIndexConformance` exists as of 2026-07-31, so the following no longer describes
today — it is kept because it is why the suite was written, and because the disagreement it
predicted was real: the two implementations differed on a limit of zero or less, and the
in-memory one threw where the adapter returned an empty success.* There was no
`RecoveryIndexConformance`,
so "which states count as abandoned" was agreed between the two implementations only by
reading — and both exclude `Suspended`, on the grounds that a parked instance is unowned
rather than abandoned, sweeping it would fence out whatever eventually delivers its signal,
and it would be swept again every pass because it is permanently stale. That reasoning
lives in two comments and no assertion, which is exactly the shape of a thing that drifts.
Separately, `AbandonedInstanceQuery.TenantId` is served as a filter over the untenanted
index rather than by a second partial index, because a second partial index on
`flow_instance` is a write charged to every step boundary of every flow for a parameter
`FlowRecoveryScan` never sets.

## What survived, and what it cost to make it survive

| Clause | Verdict |
|---|---|
| Commitment 1 — PK `(instance_id, scope, step_id, attempt)` | **Holds.** A real `PRIMARY KEY`; a duplicate is a `23505` translated to `DuplicateStep`. See the portability note below |
| Commitment 2 — `resume_from_step` is a hint the engine never reads | **Holds.** `COALESCE`d on write so a later commit cannot erase it, and never consulted by the frontier read |
| Commitment 3 — a `SubFlow` child is its own row | **Holds**, and only because the parent link has no foreign key — see the retention note below |
| Commitment 4 — `nondeterministic` is the capture envelope | **Holds.** Written by hand rather than by a serialiser, so `RandomSeed` null and `RandomSeed` zero stay different documents |
| Commitment 5 — payloads through the generated context | **Holds only on `json`.** See decision 1 |
| Fence on every write; `FenceAsync` raises on acquisition | **Holds.** `token >= fence`, checked under `SELECT … FOR UPDATE` so the check, the terminal check and the sequence allocation are one decision |
| One transaction over step + instance + outbox | **Holds.** A refused commit returns before `COMMIT`, so its rollback discards the outbox rows too |
| The retention table | **Holds**, once it became data. See below |
| Expand/contract migrations | **Holds**, and is now enforced rather than intended |

### Four things the record does not say, which an implementer has to get right anyway

- **`JournalStep.Sequence` needs a column, and it must be instance-local.** The contract
  requires it; the ERD has none. It cannot be derived: `committed_at` is two clocks on two
  nodes, and `step_id` is not commit order once a flow forks. Implemented as a counter on
  the instance row, handed out under the lock the commit already takes.
- **The lease row must never be deleted.** `EveryAcquisitionIssuesAStrictlyGreaterToken`
  requires a per-instance counter that survives *both* endings. The obvious `DELETE` on
  release loses it, and the next acquisition hands a returning zombie a token equal to its
  successor's — every fence check downstream then passes. Release and expiry are an
  `UPDATE` to `expires_at`; the token stays.
- **`duration_ms int` overflows** at 24.8 days per attempt. `bigint` here.
- **`CompleteAsync` on an already-terminal instance is undefined** by ADR-0015 and by the
  conformance suite. This adapter allows an identical repeat — a caller that lost the
  response to its first call must be able to ask again — and refuses a *different* terminal
  state, which would rewrite the outcome. **A contract gap, flagged rather than closed:**
  the suite is the specification, and it does not specify this.

### Retention

[11 §2](../11-Distributed-Runtime.md#2-the-journal) states retention as a table in a
document, and a table in a document deletes nothing. The same numbers are seeded into
`retention_policy`, per flow with a `'*'` default, where an operator can change one without
a deployment. A null window means "not on a timer" — a `Suspended` instance is kept until
it completes or its deadline passes — and the arithmetic gives that for free.

ADR-0015's orphan consequence is discharged rather than restated:
`RetentionTests.PurgingAParentLeavesItsRunningChildLegible` archives a completed parent
while a detached child is still running, and the child stays readable with its parent id
intact. A foreign key on `parent_instance_id` would have turned the purge itself into the
error.

**One accepted risk, named — and discharged on 2026-07-31.** purging an instance cascades to
its outbox rows, including any that were never published. Today nothing publishes them
([FLOWX1024](../diagnostics/FLOWX1024.md)), so nothing is lost; when WP-56 lands a publisher,
the purge needs a guard against removing a pending event. This is a note for WP-56, not a
defect in it.

> **WP-56 landed the publisher and the guard with it**
> ([ADR-0018](ADR-0018-outbox-publication-and-ordering.md)), decision 5). Both purges carry a
> `NOT EXISTS` over unpublished events, unscoped by any window, and
> `RetentionSweep.HeldForPendingEvents` counts what they withheld — because the guard's own
> failure mode, a deployment that stages events and never publishes them, is otherwise
> invisible until the disk is. `RetentionTests.APurgeKeepsAnInstanceThatStillHoldsAn`
> `UnpublishedEvent` and its two siblings are where that is asserted rather than intended.
> The risk above is left standing rather than rewritten: it is the reasoning that produced
> the guard, and a record that deletes its own premise on discharging it teaches nothing.

### A portability note the record should carry

Commitment 1 works here partly by luck of dialect. `StepScope.Root` renders as **the empty
string**, and PostgreSQL treats `''` as a value distinct from `NULL`, so the flow body is a
legal primary-key component. A database that folds the two — Oracle is the usual example —
would reject every root-scope step row. Any future adapter has to map `Root` explicitly.

## Consequences

*This section had no **Positive** / **Negative** split until 2026-07-31, which
[the ADR index's own rule](README.md) — "an ADR with no 'Negative' section has not been
thought through" — makes a defect in the record rather than in the thinking. The trade-offs
were argued in the body all along, under headings that hid them from anyone scanning for
them: "the cost is real and accepted", "two gaps left open", "one accepted risk, named".
What follows **gathers** those and decides nothing new; every negative below names the
section it comes from.*

**Positive**
- **ADR-0015's five Decision commitments all hold.** What failed is the drawn ERD it
  already declared superseded, plus its own unstated snapshot position. That is a good
  outcome for the record and an argument for Accepting it.
- **`plugins/FlowX.Postgres` depends on `FlowX.Abstractions` and nothing else.** The
  journal and lease contracts, `Result` and `Error` all live there, so a store needs nothing
  from Core, Runtime or Hosting.
- **The conformance suite was derivable from another assembly without an edit**, which is
  the first evidence that [17 §5](../17-Plugin-System.md)'s self-certification story works.
  It is also the precondition WP-70 needs before packing it.
- **A gap no test could have reported was found by reading this adapter against the
  specification** (decision 4): the only `IRecoveryIndex` anywhere was a test double, so a
  Postgres-backed host fenced correctly and swept nothing. An optional dependency fails that
  quietly, and nothing in the suite could have said so — which is an argument for writing
  this kind of record and not only this kind of test.

**Negative / accepted trade-offs**
- **`json` gives up everything `jsonb` would have bought** (decision 1): no GIN index on any
  payload column, and every JSON operator re-parses the document. Accepted because a journal
  payload is written once, read whole and never queried by key — and the moment that stops
  being true, this decision and ADR-0008 re-open together, which is the Revisit condition
  below.
- **Two referential-integrity constraints the schema could have had, it deliberately does
  not.** `flow_lease` carries no foreign key to `flow_instance`, because the lease is
  acquired before the instance row exists (decision 2); `parent_instance_id` carries none,
  because a foreign key would turn a parent's purge into an error while a detached child is
  still running (Retention). Both are right, and both mean the database will not catch a
  dangling id that a bug writes.
- **A superseded index ships and is not dropped.** Migration `0002`'s `(state, updated_at)`
  is the wrong shape for the scan its own comment claims to support — 1 748 buffers against
  4 — and `0003` adds the right one while leaving it in place (decision 4). Until a later
  release drops it, every instance update pays for an index nothing plans against.
- **Two contract gaps were named rather than closed; one is now closed.**
  *`RecoveryIndexConformance` was written on 2026-07-31 and both implementations are held
  to it, so "which states count as abandoned" is an assertion rather than two comments. It
  immediately earned its keep: the two disagreed on a limit of zero or less — this adapter
  short-circuits to an empty success and the in-memory reference threw — and the adapter's
  behaviour was the correct one, because [ADR-0007](ADR-0007-result-over-exceptions.md))
  makes an exception from a store mean the store could not be reached.* What remains open is
  the second: `CompleteAsync` on an already-terminal instance is undefined by ADR-0015 *and*
  by the suite, so this adapter's rule — an identical repeat allowed, a different terminal
  state refused — is an implementation choice standing in for a specification.
- **`AbandonedInstanceQuery.TenantId` is served by a filter over the untenanted index**, so
  a tenant-scoped scan reads more than it needs. Priced deliberately: a second partial index
  on `flow_instance` is a write charged to every step boundary of every flow, for a
  parameter `FlowRecoveryScan` never sets.
- **Purging cascades to unpublished outbox rows.** Nothing publishes them today
  ([FLOWX1024](../diagnostics/FLOWX1024.md)) so nothing is lost, but WP-56 lands a publisher
  and inherits an obligation to guard the purge. **This record is the only place that
  obligation is written down** — neither planning file carries it.
- **Commitment 1 holds partly by dialect.** `StepScope.Root` renders as the empty string and
  PostgreSQL distinguishes `''` from `NULL`; a database that folds them rejects every
  root-scope row. Every future adapter inherits a mapping obligation this one never had to
  make.
- **WP-53's exit criterion is only half met, and the unmet half is not this package's to
  meet.** Conformance is green against a real database. **B7 and B8 are unreported**,
  because WP-50 — the benchmark harness they are measured against — has not started. The
  criterion asks for "an explicit pass or fail"; the honest answer is **neither yet**, and
  ADR-0006's "measured ceiling" stays a literature figure for the same reason it did when
  the take-down package corrected that record's other clauses: no harness, no benchmark.
  *This sentence read "for the same reason it did at WP-54", which reads as a completed
  package — WP-54 is the Redis lease store and has not started.*

**Revisit when:** a payload needs indexing inside the journal, at which point decision 1 and
ADR-0008 are the pair to re-open together — `json` plus a generated expression index is the
likely answer, not `jsonb`.

---

**Back to:** [ADR index](README.md) · [ADR-0015](ADR-0015-journal-schema-and-durable-execution.md)) ·
[11 — Distributed Runtime](../11-Distributed-Runtime.md)

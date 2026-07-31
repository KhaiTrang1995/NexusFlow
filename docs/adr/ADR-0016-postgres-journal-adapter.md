# ADR-0016: What ADR-0015 looked like against a real database

**Status:** Accepted
**Date:** 2026-07-31
**Deciders:** Runtime team, Platform architecture
**Amends:** [ADR-0015](ADR-0015-journal-schema-and-durable-execution.md) ·
[11 §2](../11-Distributed-Runtime.md#2-the-journal)

> This record exists because [ADR-0015](ADR-0015-journal-schema-and-durable-execution.md)
> asked for it. Its own header says the status "is decided" at WP-53, against Postgres,
> because everything it commits to is *storage* and the in-memory reference "cannot disagree
> with a single clause". This is what disagreed.

## Context

WP-53 built `plugins/FlowX.Postgres` — schema, migrations, `IFlowJournal`, `ILeaseStore`,
retention — and ran the WP-51 conformance suite against PostgreSQL 16.13. The suite was
inherited from `tests/FlowX.Conformance.Tests` **unmodified, from a different assembly**,
which is the arrangement [17 §5](../17-Plugin-System.md) describes for a third party
claiming conformance. Nothing in that project changed to accommodate this adapter.

Result: **45 conformance assertions green against a real database**, plus 18 adapter tests.

Three clauses did not survive. Two of them are in the ERD drawn in
[11 §2](../11-Distributed-Runtime.md#2-the-journal), which ADR-0015 already marks
superseded; the third is an inconsistency inside ADR-0015 itself.

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

**One accepted risk, named:** purging an instance cascades to its outbox rows, including
any that were never published. Today nothing publishes them ([FLOWX1024](../diagnostics/FLOWX1024.md)),
so nothing is lost; when WP-56 lands a publisher, the purge needs a guard against removing
a pending event. This is a note for WP-56, not a defect in it.

### A portability note the record should carry

Commitment 1 works here partly by luck of dialect. `StepScope.Root` renders as **the empty
string**, and PostgreSQL treats `''` as a value distinct from `NULL`, so the flow body is a
legal primary-key component. A database that folds the two — Oracle is the usual example —
would reject every root-scope step row. Any future adapter has to map `Root` explicitly.

## Consequences

- **ADR-0015's five Decision commitments all hold.** What failed is the drawn ERD it
  already declared superseded, plus its own unstated snapshot position. That is a good
  outcome for the record and an argument for Accepting it.
- **`plugins/FlowX.Postgres` depends on `FlowX.Abstractions` and nothing else.** The
  journal and lease contracts, `Result` and `Error` all live there, so a store needs nothing
  from Core, Runtime or Hosting.
- **The conformance suite was derivable from another assembly without an edit**, which is
  the first evidence that [17 §5](../17-Plugin-System.md)'s self-certification story works.
  It is also the precondition WP-70 needs before packing it.
- **WP-53's exit criterion is only half met, and the unmet half is not this package's to
  meet.** Conformance is green against a real database. **B7 and B8 are unreported**,
  because WP-50 — the benchmark harness they are measured against — has not started. The
  criterion asks for "an explicit pass or fail"; the honest answer is **neither yet**, and
  ADR-0006's "measured ceiling" stays a literature figure for the same reason it did at
  WP-54.

**Revisit when:** a payload needs indexing inside the journal, at which point decision 1 and
ADR-0008 are the pair to re-open together — `json` plus a generated expression index is the
likely answer, not `jsonb`.

---

**Back to:** [ADR index](README.md) · [ADR-0015](ADR-0015-journal-schema-and-durable-execution.md) ·
[11 — Distributed Runtime](../11-Distributed-Runtime.md)

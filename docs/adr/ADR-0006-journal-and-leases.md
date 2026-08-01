# ADR-0006: Use an append-only journal with fenced leases for durable execution

**Status:** Accepted
**Date:** 2026-07-30
**Deciders:** Runtime team, Platform architecture

## Context

Durable flows must survive process crashes without duplicating side effects and
without two nodes executing the same instance (split brain). Quality goal Q2
requires resumption on another node with zero duplicate effects for idempotent
capabilities.

Options considered:

- **A. Consensus-based cluster (Raft) with in-memory state.** *Rejected:*
  operationally heavy, adds a cluster to every deployment, and makes the runtime
  stateful — contradicting P7.
- **B. Distributed lock only (lease with TTL, no fencing).** *Rejected:* the
  classic split-brain bug — a node that pauses (GC, network partition) past its
  TTL wakes up believing it still owns the instance and writes stale state. TTLs
  alone are not a correctness mechanism.
- **C. Event sourcing the whole application.** *Rejected:* imposes a modelling
  style on users' business data; FlowX journals *execution*, not domain state.
- **D. Append-only journal + fenced leases, both in pluggable stores.** Chosen.
- **E. Delegate to an external durable-execution service (Temporal).**
  *Rejected:* adds a mandatory external dependency, a second programming model,
  and network latency per step; also contradicts ADR-0003's ephemeral default.

## Decision

We will implement durable execution with an **append-only journal** (one row per
step outcome, committed in the same transaction as the outbox row) plus
**leases carrying monotonic fencing tokens** that are validated on every journal
write — because these two well-understood primitives provide the required
guarantees without introducing consensus into the application runtime.

Both are `IFlowJournal` / `ILeaseStore` plugins with a shared conformance suite,
so Postgres, Redis, SQL Server or a custom store all behave identically.

> [!WARNING]
> **Accepted, and both primitives are now built against a real database. What is
> still a prediction is the arithmetic.** *This box said there was no journal, no
> lease store, no fencing token, no interface in `src/` and no conformance suite;
> it then said the journal existed as a seam and that nothing acquired a lease and
> no store implemented either interface. Both of those states have expired, in that
> order, and each clause is recorded rather than deleted:*
>
> - **The journal exists as a contract, as a seam, and as a store.** `IFlowJournal`,
>   `ILeaseStore` and `FencingToken` are in `src/FlowX.Abstractions/Durability/`,
>   `tests/FlowX.Conformance.Tests` holds the shared suite this decision promises,
>   and `FlowX.Runtime` reads `ExecutionProfile` and commits one row per step
>   boundary. A `Durable` flow no longer executes on the ephemeral path — it is
>   refused outright if no journal is supplied.
> - **A store implements both interfaces.** *"No store implements either interface"
>   and "none has run against a real database" were true until WP-53.*
>   `plugins/FlowX.Postgres` is a schema with migrations, retention and a fenced
>   write path, and the conformance suite runs against **PostgreSQL 16.13** from a
>   different assembly, unmodified — the arrangement a third party claiming
>   conformance would use. Three clauses of the schema did not survive contact and
>   are amended in [ADR-0016](ADR-0016-postgres-journal-adapter.md)). Redis is still
>   WP-54, so there is one store and nothing yet to disagree with it.
> - **A lease is acquired, renewed and released.** *"Nothing acquires a lease" and
>   "resumption on another node has no mechanism yet" were true until WP-55.*
>   `DurableLease` and `LeasePolicy` (`src/FlowX.Runtime/`) hold the lease a node
>   executes under and raise the instance's fence on acquisition rather than on the
>   first write; `FlowRecoveryScan` and `FlowRecoveryService`
>   (`src/FlowX.Hosting/`) sweep for instances a dead node left running and take
>   over as many as the node has room for.
> - **Split brain is pinned by the suite, and the suite now runs against a
>   database.** *This said the property was pinned "against a dictionary".* It is
>   also pinned against PostgreSQL, and takeover is exercised end to end by
>   `DurableHostTests` — with two hosts **in one process**, against a shared store.
>   No node has ever been killed: the chaos rig is WP-50 and has not started.
>
> The decision stands and is what **P2** is built to. The consequences below are
> still predictions — including the "measured ceiling" in the first negative, which
> remains a figure for Postgres from the literature and **not** a FlowX benchmark.
> That clause has not been repaired by WP-53, and the reason is unchanged: B7 and
> B8 still have no harness
> ([14 §8](../14-Performance.md#8-benchmark-suite-and-ci-gating)), WP-50, which was
> supposed to build it before the journal, has not started, and no store — Postgres
> included — has been benchmarked. A store that passes a conformance suite is a
> store that is *correct*, which is a different claim from a number.
>
> The schema this journal actually has is not in this record: it is
> [ADR-0015](ADR-0015-journal-schema-and-durable-execution.md)), which fixes the key
> at `(instance_id, scope, step_id, attempt)`, and what a real database did to it is
> [ADR-0016](ADR-0016-postgres-journal-adapter.md)).

## Consequences

**Positive**
- Split brain is structurally impossible: a stale writer's token is rejected,
  regardless of how long it was paused. Correctness does not depend on clocks.
- Nodes stay stateless and interchangeable (P7); scaling is a replica count.
- The journal doubles as the replay and audit source — one mechanism serves
  durability, observability and compliance.
- Atomic state+event commit removes the dual-write problem entirely.
- No consensus protocol to operate, debug or explain.

**Negative / accepted trade-offs**
- **The journal is a shared bottleneck** (risk R5). Measured ceiling ~20–50k
  step-commits/s per Postgres primary. Mitigations in order: keep `Ephemeral` the
  default, group-commit batching, tenant sharding, time partitioning. This is a
  documented, monitored boundary — not a surprise.
- **Recovery latency is bounded by the lease TTL** (~30 s default). Shorter TTLs
  recover faster but increase renewal load; this is a tuning trade-off, not a
  correctness one.
- **Storage growth** is real: durable steps × retention. Requires retention
  policy and an archival plugin.
- **Every durable step pays a network round trip.** This is precisely why
  ADR-0003 exists.
- A capability that is genuinely non-idempotent can still execute twice if the
  process dies after the effect but before the commit. We state this honestly
  rather than claim exactly-once ([11 §4](../11-Distributed-Runtime.md#4-exactly-once-honestly)).

**Revisit when:** a managed, sub-10 µs durable log primitive becomes commodity in
the major clouds, making the journal cheap enough to change ADR-0003's default.

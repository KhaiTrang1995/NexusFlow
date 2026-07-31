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
> **Accepted; half of it now implemented, and the half that is not is the half
> this record is about.** *This box said there was no journal, no lease store, no
> fencing token, no interface in `src/` and no conformance suite. That was true
> until WP-51 and WP-52 (2026-07-30 to 2026-07-31), and each clause is now
> separately false or still true:*
>
> - **The journal exists as a contract and as a seam.** `IFlowJournal`,
>   `ILeaseStore` and `FencingToken` are in `src/FlowX.Abstractions/Durability/`,
>   `tests/FlowX.Conformance.Tests` holds the shared suite this decision promises,
>   and `FlowX.Runtime` reads `ExecutionProfile` and commits one row per step
>   boundary. A `Durable` flow no longer executes on the ephemeral path — it is
>   refused outright if no journal is supplied.
> - **No store implements either interface.** The only implementation anywhere is
>   an in-memory reference in that test project; none has run against a real
>   database (Postgres is WP-53, Redis WP-54).
> - **Nothing acquires a lease.** Fencing is enforced — a write below the
>   instance's fence is rejected and ends the flow — but the token is handed to the
>   engine by its caller. There is no lease acquisition, no renewal timer and no
>   recovery scan, so "resumption on another node" has no mechanism yet (WP-55).
> - **Split brain has never been tested against anything real.** The property is
>   pinned by the conformance suite against a dictionary.
>
> The decision stands and is what **P2** is built to. The consequences below are
> still predictions — including the "measured ceiling" in the first negative, which
> remains a figure for Postgres from the literature and **not** a FlowX benchmark:
> B7 and B8 still have no harness
> ([14 §8](../14-Performance.md#8-benchmark-suite-and-ci-gating)), and WP-50, which
> was supposed to build it before the journal, has not started.
>
> The schema this journal actually has is not in this record: it is
> [ADR-0015](ADR-0015-journal-schema-and-durable-execution.md), which fixes the key
> at `(instance_id, scope, step_id, attempt)` and is deliberately still
> **Proposed** for the reason this box demonstrates.

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

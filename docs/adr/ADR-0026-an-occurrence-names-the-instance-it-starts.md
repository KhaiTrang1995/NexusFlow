# ADR-0026: A cron occurrence names the instance it starts

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture
**Amends:** [ADR-0004](ADR-0004-universal-trigger-model.md) ·
[09 §8](../09-Trigger-Model.md#8-schedule-trigger)

> **[ADR-0004](ADR-0004-universal-trigger-model.md) chose one trigger abstraction for every
> transport, and exactly one transport was bound.** `TriggerKind` declares eight;
> `EndpointEmitter` turns `[HttpTrigger]` into a registration; `Bus`, `Schedule`, `Stream`,
> `Change` and `Agent` were declaration only. `TriggerReader`'s own class remarks said so in
> those words — *"a flow declaring one of those declares an address nothing serves"*. This
> record binds the second one.
>
> **The whole of the decision is that a firing is a value both nodes already computed.** A
> schedule is the one transport with no message and no broker: nothing arrives, so nothing
> carries an id, and the obvious answer — elect a leader and let it fire — introduces three
> mechanisms and two windows where a schedule fires twice or not at all. A cron occurrence is
> already a value every node agrees on without talking. Making the instance id a function of it
> is what turns "agree who fires" into "nobody has to agree".

---

## 1. Context

### 1.1 What was already true, and what it was built for

Two primitives in the runtime were built for exactly this class of problem and were not being
used for it.

* **`FlowTimerScan` / `FlowTimerService`** — one `BackgroundService` per node, sweeping on a
  jittered interval for work that has become due. Its own remarks state the shape: *"nothing
  here sleeps for a flow"*, the candidate set is filtered at the store, the batch is walked from
  a random offset so ten nodes handed one page do not contend for its first row, and *"losing
  the lease is a free skip"*.
* **`ILeaseStore` / `DurableLease`** — time-bounded exclusive ownership of one flow instance,
  with a fencing token. Its own remarks are equally direct: *"a TTL is not a correctness
  mechanism, and this interface is shaped so that it cannot be mistaken for one"*.

A third fact turned out to be the load-bearing one, and it is not in either of those.
`IFlowJournal.StartAsync` returns `DurabilityErrors.InstanceExists` when the id is already
known: *"Starting twice is refused rather than merged: an append-only history cannot be
replaced."* That refusal is permanent where a lease is not.

And a fourth, which had been written down and never used. `FlowHost.OpenAsync` has said since
WP-55:

> The instance id is minted here and it is version 7 … A trigger that wants a redelivery to be
> idempotent supplies its own id through `DurableExecution.BeginAsync`; this path is for a
> caller that has none to offer.

Nothing in the repository supplied one. Every instance was `Guid.CreateVersion7()`, minted per
invocation.

### 1.2 What a schedule has that no other transport has

Every other trigger kind is *delivered*. A request arrives, a record arrives, a signal arrives —
and whatever arrives is a thing one party sent and one or more parties received, so an
identifier can travel with it. `[KafkaTrigger]` gets a partition and an offset; `[HttpTrigger]`
gets an `Idempotency-Key` when it asks for one.

A schedule is *computed*. `0 2 * * *` in `Europe/Berlin` names an instant, and every node in the
fleet arrives at that instant independently, from the same expression and the same clock, with
no message between them. Nothing is delivered, so nothing carries an id — and by the same token,
**nothing has to be delivered for the nodes to agree.**

That is a rare position and it is what makes the answer cheap. A leader election exists to
manufacture agreement about a fact only one party can observe. Here every party already observes
the same fact.

### 1.3 What "leader-elected" would have cost

[09 §8](../09-Trigger-Model.md#8-schedule-trigger) has said since it was written:

> The Scheduler Engine is **leader-elected** using the same lease store as durable flows. Two
> replicas never fire the same schedule; a dead leader is replaced within the lease TTL.

The second sentence does not follow from the first, and the gap is where a scheduler loses work.
A leader needs three mechanisms — election, liveness detection, hand-over — and each has a
window:

* **A leader that has lost the lease and does not know it yet** fires anyway. Leases lapse by
  wall clock and a paused process cannot observe its own pause; `DurableLease`'s remarks say
  precisely this about a node *"paused past its own TTL — a stop-the-world pause, a partition, a
  suspended container"*. Two leaders fire, and nothing downstream refuses the second, because
  each mints its own instance id.
* **A leader that dies at 01:59** takes the 02:00 firing with it until the TTL lapses and a
  successor is elected. With `LeaseTtl` at 30 seconds that is a small window; with an outage it
  is not, and the successor has no way to know what the dead leader had already done.
* **The lease itself is a single point of contention** for every schedule in the application,
  where the work is naturally partitioned by schedule.

None of that is fixable by tuning. It is the cost of putting agreement in the wrong place.

---

## 2. Decision

**A firing is identified by its occurrence, and the instance id is derived from it. There is no
leader and no election; every node fires every occurrence, and the stores refuse all but one.**

### 2.1 The derivation

```text
InstanceId = uuidv8( SHA-256( flowId ␀ flowVersion ␀ cron ␀ timeZone ␀ occurrence ) )
```

`ScheduleOccurrence.InstanceIdFor`. Five terms, NUL-separated so that no two different tuples
can produce one string, and the instant normalised to UTC and formatted invariantly so that a
node in a different culture derives the same id.

**Every term is there because leaving it out would fold two schedules into one.** The flow
version in particular: an instance is pinned to the version it started with
([11 §7](../11-Distributed-Runtime.md)), so two versions deployed side by side are two schedules,
and a shared id would let the older version's firing suppress the newer one's for the whole of a
canary.

**UUID version 8** — RFC 9562's slot for a derived id. Not version 4, which would claim the
bytes were random; not the version 7 `FlowHost` mints for a request-started instance, which
would claim the leading bits were a timestamp. An operator reading a journal row is entitled to
tell a derived key from a minted one, and the byte order is fixed big-endian so that the `uuid`
a Postgres row shows is a prefix of a digest they can recompute.

### 2.2 The two refusals, and why both are needed

| Refuses | Mechanism | Lasts |
|---|---|---|
| A second node firing **now** | `ILeaseStore.AcquireAsync` → `lease.held` | the lease TTL |
| A second node firing **ever** | `IFlowJournal.StartAsync` → `journal.instance_exists` | for ever |

The lease is the fast answer and the journal is the true one. A lease says nothing about a node
that comes back an hour later, or about a node whose sweep runs after the winner's lease has
been released; the primary key is what makes "this occurrence has fired" a permanent fact.
Neither is new code — both are refusals the stores already made, reached by giving them an id
they can compare.

**This is why a scheduled flow must declare `Durable`, and the requirement is not incidental.**
An `Ephemeral` plan takes no lease and writes no row, so the derived id is inert and every node
runs every occurrence — with no error, no duplicate-key refusal and nothing anywhere to count.
`FLOWX1037` refuses it at compile time and `FlowScheduleCatalog.Add` refuses it at startup.

### 2.3 What is generated, and from which reading

`ScheduleEmitter` turns each `[CronTrigger]` into a call to
`FlowX.Hosting.FlowScheduleRegistration.Add`, in `FlowXSchedules.g.cs`, in the user's own
assembly — the arrangement `EndpointEmitter` has, guarded the same way: the generator knows the
host only as the string `"FlowX.Hosting.FlowScheduleRegistration"` and looks it up in the user's
compilation, so a flow library compiled on its own gets no file, no type and no IL.

**The expression and the zone are copied off the `TriggerModel` the manifest published, not read
a second time**, and here that matters more than it does for a route. Those two strings are two
of the five terms above. A registration carrying a different string from the manifest's would
not merely mislead a reader — it would derive different ids and split one schedule into two that
never see each other's firings.
`ScheduleGenerationTests.TheRegisteredCronIsTheOneTheManifestPublished` reads both out of one
generator run and compares.

### 2.4 The sweep is `FlowTimerScan`'s shape, with one thing removed

`FlowScheduleScan` walks the registered schedules, asks each what has fallen due, and fires
through
`FlowHost.RunAsync(plan, dispatcher, invocation, input, instanceId, ct)` — which is `RunAsync`
with the minted id replaced by the derived one. `OpenAsync` is unchanged except for
`suppliedId ?? Guid.CreateVersion7()`, so **there is no second way into a flow**: the lease is
taken, the row is written with the lease's token as its opening fence, and the same
`FlowEngine.ExecuteAsync` an HTTP request reaches is reached ([ADR-0015](ADR-0015-journal-schema-and-durable-execution.md)).

What is removed is the store query. A durable timer reads an instant off a row and
`ITimerIndex` serves that query; a schedule has no row to read, so this sweep touches no index
at all. It is why the two are separate loops on separate intervals rather than one sweep with a
disjunction in it.

---

## 3. Options rejected

- **A. Leader election, as [09 §8](../09-Trigger-Model.md#8-schedule-trigger) described.**
  *Rejected:* §1.3. Three mechanisms and two windows to manufacture agreement about a fact every
  node can compute for itself. The clause that made it sound settled — *"a dead leader is
  replaced within the lease TTL"* — describes how quickly a **successor appears**, not what
  happens to the firing the dead leader was holding.
- **B. A `flowx_schedule` table with a `last_fired_at` column, updated under a lease.**
  *Rejected:* it is a second durable store for a fact the journal already holds, and it holds it
  worse. `flow_instance` records *which* occurrences fired and what each one did; a
  `last_fired_at` records only the newest, so `MissedFirePolicy.RunAll` could not be implemented
  against it after a restart, and a row that got ahead of the instance it claimed to represent
  would lose a firing with nothing to reconcile against. It would also need its own migration,
  its own conformance suite and its own answer to "what if the write succeeds and the flow does
  not start".
- **C. Mint a random id per firing and deduplicate on `(flow_id, occurrence)` with a unique
  index.** *Rejected,* though it reaches the same guarantee. It puts the constraint in the
  Postgres adapter's schema rather than in the contract, so a second journal adapter would have
  to reinvent it and `JournalConformance` would have nothing to hold it to. Deriving the primary
  key instead means the refusal is `StartAsync`'s existing one, which every adapter already
  implements and the suite already pins.
- **D. One node per schedule, assigned by consistent hashing over the node set.** *Rejected:*
  it needs a node set, which needs membership, which is the leader election of option A wearing
  a different hat — and it makes a firing depend on how many replicas exist, so a scale-down
  during a deployment can drop one.
- **E. Let the id be derived but keep a leader anyway, as a cost measure.** *Rejected:* the
  cost it saves is one refused lease acquisition per node per occurrence, which for an hourly
  schedule on ten nodes is nine writes an hour. The in-process memory of what a node has already
  attempted removes most of even that. Paying a correctness mechanism's complexity for that
  saving is the wrong trade, and it would put the two mechanisms in a position to disagree.

---

## 4. What this does to the manifest

Nothing, and that is [ADR-0029](ADR-0029-the-manifest-publishes-a-schedules-address.md)'s
subject rather than this record's. Stated here only because the derivation reads two fields the
manifest publishes: `cron` and `timeZone` were already written by `ManifestWriter` and already
classified by `flowx diff`, so binding the transport adds no schema field and does not move
[ADR-0017](ADR-0017-manifest-v1-freeze-criteria.md)'s F1 count.

---

## 5. Consequences

**Positive**

- **A schedule fires once across a fleet with no coordination protocol at all.** No leader, no
  election, no liveness detection, no hand-over, and therefore none of the windows each of those
  has. `ScheduleScanTests.TenNodesSweepingOneOccurrenceProduceOneInstance` is the assertion, and
  `TwoNodesStartingOneInstanceIdProduceOneInstance` is the same claim without a race in it.
- **A node dying mid-firing costs nothing new.** The instance is a durable instance like any
  other, and `FlowScheduleRegistration.Add` puts the flow in `FlowCatalog`, so
  `FlowRecoveryScan` takes it over exactly as it takes over an instance a request started.
- **"Did this schedule run last night" is answerable from the journal**, by recomputing the id
  and reading the row — not from a log, and not from a scheduler's own state.
- **The second transport added no store contract at all.** Two pure types in `FlowX.Runtime`
  (`CronSchedule`, `ScheduleOccurrence`) and a catalogue, a sweep, its report and a hosted service
  in `FlowX.Hosting` — and **no new store interface, no new migration, no new conformance suite,
  and nothing added to `IFlowJournal` or `ILeaseStore`.** ADR-0004's *"new transports are plugins;
  `FlowX.Runtime` never changes"* is not quite what happened, since `CronSchedule` is in the
  runtime; nothing transport-shaped is, and `RuntimeDoesNotReferenceAnyPlugin` is untouched.
  `FlowHost` gained one overload and one line — `suppliedId ?? Guid.CreateVersion7()`.

**Negative / accepted trade-offs**

- **Every node attempts every occurrence, so an *n*-node fleet does *n*−1 refused acquisitions
  per firing.** For an hourly schedule on ten nodes that is nine extra lease round trips an
  hour, which is nothing; for a per-minute schedule on fifty nodes it is 2,940 an hour, which is
  not nothing. The in-process memory of the newest occurrence a node has accounted for keeps it
  to one attempt per node per occurrence rather than one per sweep, and that is the whole of the
  mitigation. **Accepted, and named here so that the first deployment with a dense schedule and
  a large fleet knows where to look.**
- **The id is a hash, so it is not sortable and not time-ordered.**
  `FlowHost.OpenAsync` mints version 7 precisely so that *"a journal's primary key is
  time-ordered rather than scattered across its index"*, and a derived id gives that up: a
  scheduled application's `flow_instance` inserts land at random points in the primary key's
  B-tree. It is bounded by how many firings there are — one per schedule per occurrence, not one
  per request — which is why the trade is acceptable here and would not be on the HTTP path.
- **The derivation is now a compatibility surface.** Change any term, its order, the separator,
  the digest or the instant's format, and every schedule in every deployment forgets what it has
  fired: the ids no longer match the rows, and the first sweep after the upgrade re-fires
  whatever is inside the catch-up horizon. There is no version marker on the id and no migration
  path, because the only migration would be to recompute every historical id. **Treat
  `ScheduleOccurrence.InstanceIdFor` as a wire format.**
- **A schedule's exclusivity is now coupled to the journal being the same journal.** Two
  deployments of one application pointed at two databases fire everything twice, and neither can
  tell. That is true of durable execution generally and is not new; it is newly *load-bearing*,
  because a schedule has no caller to notice the duplicate.

**Revisit when:** any one of —
- **a schedule has to fire an instance whose id something else owns.** A sub-flow composed from
  a scheduled parent already has its own id and is unaffected; what would break this is a
  requirement that a firing continue an *existing* instance rather than start one, at which
  point the id is not the firing's to derive;
- **a journal adapter weakens `StartAsync`'s duplicate refusal** — makes it an upsert, or
  reports success for an existing row. The lease alone is not sufficient, and
  `JournalConformance` is where that would have to be caught;
- **a second transport needs occurrence-derived identity.** A change-feed trigger deduplicating
  on a log sequence number is the same shape, and at that point the derivation belongs to the
  abstraction rather than to `FlowX.Runtime` beside the cron parser;
- **`PerTenant` becomes declarable**, at which point one occurrence has to name *many* instances
  and the tenant becomes a sixth term — which is a change to the derivation and therefore, by
  the third negative above, a breaking one.

---

**Back to:** [ADR index](README.md) · [ADR-0004](ADR-0004-universal-trigger-model.md) ·
[ADR-0006](ADR-0006-journal-and-leases.md) ·
[ADR-0015](ADR-0015-journal-schema-and-durable-execution.md) ·
[ADR-0027](ADR-0027-a-missed-schedule-fires-late.md) ·
[ADR-0028](ADR-0028-a-scheduled-flows-input-is-its-occurrence.md) ·
[09 §8](../09-Trigger-Model.md#8-schedule-trigger) ·
[FLOWX1037](../diagnostics/FLOWX1037.md)

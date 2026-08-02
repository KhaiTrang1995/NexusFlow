# ADR-0050: A change trigger observes the outbox, and refuses to observe itself

**Status:** Accepted
**Date:** 2026-08-02
**Deciders:** Runtime team, Platform architecture
**Amends:** [ADR-0004](ADR-0004-universal-trigger-model.md) ·
[ADR-0018](ADR-0018-outbox-publication-and-ordering.md) ·
[ADR-0039](ADR-0039-a-bus-subscription-publishes-no-new-manifest-field.md)

> `TriggerKind.Change`'s own summary names three things — *"Change data capture, outbox, file
> watcher"* — and the second of them is a table this repository already writes, orders, indexes
> and retains. The decision is not what a change feed could be in general. It is which of the
> three has a producer today.

## Context

1. **The outbox is a change feed that already has every property one needs.** A `Durable`
   flow's `.Emit<T>()` stages a row in the same transaction as the step that emitted it, so a
   staged event *is* a committed state change with an identity (`event_id`), a type, a payload,
   a partition key and a staging order. [ADR-0018](ADR-0018-outbox-publication-and-ordering.md)
   already fixes what that order means: per `partition_key`, in staging order, with no global
   order offered.

2. **It has exactly one consumer, and that consumer consumes it destructively.**
   `PostgresOutboxPublisher` claims pending rows `FOR UPDATE SKIP LOCKED` and writes
   `published_at`. That column is the *publisher's* progress and nothing else's. A second
   consumer that wrote it would take events away from the broker; a second consumer that read
   `published_at IS NULL` would race the publisher for them. So a change subscription must
   read the table without writing it and without depending on the publisher's marker — which is
   [ADR-0048](ADR-0048-a-change-feed-advances-a-cursor.md)'s subject.

3. **A change subscription and a bus subscription are the same declaration over a different
   transport.** Both name an event type and a consumer group; both hand the flow a
   `BusMessage`; both are at-least-once. The difference is only whether a broker sits between
   the outbox and the consumer. `RepriceOrderFlow` and a change-triggered flow over the same
   type differ in one attribute and in nothing else, which is quality goal Q4 stated as a
   property of two files rather than as a claim.

4. **The manifest already has the two fields this needs.** `trigger.topic` and `trigger.group`
   are declared in `schemas/flowx.manifest.schema.json`, which is `additionalProperties: false`.
   [ADR-0017](ADR-0017-manifest-v1-freeze-criteria.md)'s rule is that F1 counts a declared
   field nothing writes, so a new field arrives with its producer *and* its `flowx diff` rule
   or not at all.

5. **A flow that observes a type it emits is a loop with nothing to stop it.** The staged event
   starts the flow, the flow stages another event of the same type under a fresh `event_id`, the
   feed offers that one, and the derived instance id is different every time so nothing refuses
   it. The outbox grows without bound. `ExecutionPlan` carries `StepNode.EventType` for every
   `Emit` node, so the condition is answerable from the plan the registration is handed.

Options rejected:

- **A general `IChangeFeed` over "any change feed" with no shipped binding** — the state
  `Stream` is in: an attribute with no runtime behind it. The abstraction is open, but the one
  implementation ships with it.
- **Logical replication / Debezium** — `PostgresOutboxOptions` already records polling versus
  CDC as a settled choice with CDC "arriving as a plugin when something needs it to". A change
  trigger does not need it: the outbox is the decoded stream already.
- **Publish a new `source` field for a change trigger's address** — a second spelling of
  `topic` for the same string, and F1 would count it until `flowx diff` learned it.
  ADR-0039's answer one transport over.
- **Let a change subscription mark `published_at`** — force 2. Two consumers, one marker, and
  every event reaches exactly one of them.
- **Detect the self-feeding cycle in the analyzer** — the emitted types are in the plan and the
  subscription is registered against that plan, so a registration-time refusal reads the same
  fact from the artifact that will actually run, and needs no syntax walking. It is
  `FlowBusCatalog.Add`'s existing shape for the `Durable` rule.

## Decision

### 1. `[ChangeTrigger(source, Group = …)]` declares a subscription to the outbox

`ChangeTriggerAttribute` carries `[TriggerKind(TriggerKind.Change)]`, takes the event type it
observes as its one positional argument and requires a `Group` — the same two terms
`[BusTrigger]` takes, for the same two reasons: the type is the address, and the group is what
makes two flows over one type two subscribers.

The flow must declare `Flow<BusMessage, TOut>` and `ExecutionProfile.Durable`, and
[FLOWX1041](../diagnostics/FLOWX1041.md) refuses one that does not — `FLOWX1039`'s rule one
transport over, with a separate id because a suppression of one must not silence the other.

### 2. What it observes is the outbox, read as a feed and never written

`IChangeFeed` is the seam, and `PostgresChangeFeed` is the one implementation this repository
ships. It reads `outbox_event` filtered by `type`, ignores `published_at` entirely, and writes
only its own cursor row. A deployment may therefore run a change subscription and the outbox
publisher over one table at once, and neither takes an event from the other.

**It observes the outbox and not "the database".** A change to a business table this repository
did not write is not a fact it can attribute to a flow, give an identity to, or order; the
outbox is the change feed FlowX itself produces, and that is the whole of what this binds.

### 3. A flow whose change source is a type it emits is refused at registration

`FlowChangeCatalog.Add` walks the plan's `Emit` nodes and throws when one of them names the
subscription's source. The pod does not become ready, which is the same answer an `Ephemeral`
bus subscription gets and for a worse reason: that one runs a flow twice, and this one runs it
for ever.

**An indirect cycle is not refused, and nothing bounds it.** A observes `x` and emits `y`, B
observes `y` and emits `x`. The catalogue sees one registration at a time and cannot see the
pair. This is the same cycle two `[BusTrigger]` flows can already form; it is not made worse
here, and it is not solved here.

### 4. The manifest publishes `kind: "Change"`, `topic` and `group`, and no new field

`TriggerReader` projects `[ChangeTrigger]` into the existing `topic` and `group`, so the
document gains no property, the schema needs no change, and `flowx diff` classifies a removed
or renamed change subscription with the rules it already has. Which transport serves it is the
host's registration and not the flow's promise, exactly as ADR-0039 argued for the bus.

## Consequences

**Positive**

- **A transport with no broker.** A deployment that already runs PostgreSQL can start a flow
  from another flow's event with no Redis, no Kafka and no publisher — the acceptance test is
  `tests/Ecommerce.Tests/ChangeStartsAFlowTests`, and it wires one store.
- **One event, two consumers, no coordination.** The broker path and the change path read the
  same rows through different mechanisms, so a team can add a change subscription to a system
  already publishing to a broker without touching the publisher.
- **The declaration is portable in the direction it claims to be.** Changing `[ChangeTrigger]`
  to `[BusTrigger]` on a flow is a one-line edit with no change to its body, its input or its
  capabilities.

**Negative / accepted trade-offs**

- **A change subscription costs a journal and a cursor row.** It is Postgres-only in practice
  until a second `IChangeFeed` exists, which makes `Change` the least portable of the four bound
  kinds even though its declaration is the most transport-neutral.
- **The feed is bounded by retention.** `PostgresRetention` purges published outbox rows after
  seven days and refuses to purge an instance with a *pending* one — a marker a change
  subscription does not set. A subscription that is down for longer than the published window
  loses the changes it never read, silently. Its cursor is the only evidence, and nothing
  currently alerts on cursor lag.
- **`published_at` no longer means "nobody needs this row".** It means "the publisher sent it".
  A deployment with a change subscription and no broker never sets it at all, so the pending
  guard in `PostgresRetention` holds every such instance for ever. That is the pre-existing
  behaviour of a host with no publisher wired, and this record makes it a configuration people
  will actually run rather than one nobody chose.
- **The self-cycle refusal is a startup failure and not a build failure.** An author learns at
  deployment rather than at compile time. The analyzer could not read the emitted types without
  walking the builder chain in syntax, and the plan states the same fact as data.

## Revisit when

- A second `IChangeFeed` implementation exists — a file watcher, a logical-replication reader —
  at which point "the source is an event type" stops being general enough and the address needs
  a shape the manifest's `topic` does not have.
- Retention learns about change cursors, at which point the pending guard can hold a row for a
  subscription that has not read it and the second trade-off above expires.
- An indirect cycle is observed in production, at which point the emitted-type graph has to be
  built across a compilation and this record's decision 3 becomes a compile-time rule.
- A change subscription needs to observe more than one type, which would make `topic` a list and
  is the first thing that would genuinely need a new manifest field.

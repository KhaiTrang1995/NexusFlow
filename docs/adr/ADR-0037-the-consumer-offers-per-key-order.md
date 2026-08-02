# ADR-0037: The consumer offers per-`partition_key` order and none across keys, and takes a lease to mean it

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Runtime team, Platform architecture
**Amends:** [ADR-0018](ADR-0018-outbox-publication-and-ordering.md)

> [ADR-0018](ADR-0018-outbox-publication-and-ordering.md) decision 3 says publication offers
> *"per `partition_key`. Global ordering is not offered"*. A consumer that offered less would make
> the publisher's guarantee unobservable; one that claimed more would be claiming something
> `SKIP LOCKED` never gave it. This record says what the consumer side offers and makes the two
> halves the same sentence.

## Context

1. **The stream *is* the partition.** `RedisKeys.EventStream` puts one `partition_key`'s events
   in one Redis stream and says why: *"a Redis stream is totally ordered, so a stream per key is
   exactly the guarantee ADR-0018 decision 3 offers — per key, and nothing across keys."* Reading
   one stream front to back therefore reproduces the publisher's order for that key exactly, with
   no sequence number to compare and no reordering buffer.

2. **A consumer group is what breaks it.** `XREADGROUP` distributes the entries of *one* stream
   across the consumers in a group: two nodes reading one stream get different entries and
   process them concurrently. The per-key order the publisher paid a per-row `NOT EXISTS` probe
   for is then lost on the last hop, in the configuration nobody tests first — which is exactly
   the failure ADR-0018 force 4 describes, one layer down.

3. **The repository already owns a fenced mutual-exclusion primitive.** `ILeaseStore` is held to
   `LeaseStoreConformance` and has two implementations. Nothing about it is specific to a flow
   instance; it takes a `Guid` and refuses a second holder.

4. **Concurrency across keys is the whole point of partitioning.** A design that serialised every
   key through one reader would offer a global order nobody asked for and would make one slow key
   everyone's problem — which is the reason ADR-0018 refuses global ordering in the first place.

Options rejected:

- **One consumer per group and document "do not scale out"** — a guarantee that holds only
  until somebody sets `replicas: 2` is not a guarantee, and nothing would report the breach.
- **A sequence number on the entry and a reordering buffer in the consumer** — unbounded memory
  for a head-of-line gap the buffer cannot distinguish from a dead-lettered event.
- **`XREAD` without a group** — no pending-entries list, so nothing tracks what a dead consumer
  was holding and at-least-once is lost.
- **Serialise the whole subscription through one lease** — gives global order, costs the
  concurrency partitioning exists for, and contradicts ADR-0018 decision 3's second sentence.

## Decision

### 1. Offered: per `partition_key`, in publication order. Not offered: any order across keys

The same pair of statements ADR-0018 decision 3 makes, now true on both sides of the broker.

### 2. A stream is read under a lease, so one node at a time reads one key

Before reading a stream, `FlowBusScan` acquires a lease on
`BusDelivery.StreamLeaseIdFor(flowId, flowVersion, topic, group, streamKey)` — the same UUID v8
derivation [ADR-0035](ADR-0035-a-delivery-names-the-instance-it-starts.md) uses for an instance
id, over the stream instead of the event. A node that does not win the lease skips that stream
this pass and takes another; a node that wins it holds it for the pass and releases it after.

**This is what makes the guarantee survive horizontal scaling.** Ten nodes in one group spread
across the streams rather than across the entries of one stream, so each key is served serially
and the keys are served in parallel — which is the shape partitioning was for.

### 3. Within a stream, entry *n+1* is not started until *n* has reached a disposition

Serially, in stream order, one at a time. Awaiting each flow is what bounds the work and what
makes the order real: dispatching two entries of one key concurrently would lose the order inside
a node having just paid a lease to keep it across nodes.

### 4. Across streams, a pass reads up to `FlowXOptions.BusMaxConcurrentStreams` of them

Concurrently and in no promised order. Two events with different keys arrive in either order,
which is the sentence ADR-0018 already carried.

## Consequences

**Positive**

- **The publisher's guarantee is now observable.** `ADeliveryOrderIsPreservedWithinOnePartitionKey`
  publishes three events under one key through the real `RedisStreamEventPublisher` and asserts
  the three flows started in that order — end to end, through a real server, with no test double
  in the path.
- **Scaling out does not silently weaken it.** The lease is the mechanism rather than a
  deployment convention, so `TwoNodesConsumingOneStreamStillStartTheFlowsInOrder` is a test
  rather than a note in a README.
- **No new primitive.** The lease store, its fencing token and its conformance suite already
  existed; this uses them for a second kind of exclusion, which is the reuse `ILeaseStore`'s own
  remarks anticipated by taking a bare `Guid`.

**Negative / accepted trade-offs**

- **A lease round trip per stream per pass.** For an application whose events are keyed by
  emitting instance — which is every `.Emit` today, since
  `FlowEmitter` writes `PartitionKey = ctx.FlowInstanceId` — that is one lease per *emitting
  instance*, and a busy application has many. The lease TTL is short and the acquire is one Redis
  command, but this is the cost of decision 2 being true rather than aspirational, and it is the
  first thing to measure if a subscription is slow.
- **Streams are discovered by `SCAN` on the key prefix.** Redis has no "list the partitions of a
  topic" operation, because it has no topic. Each pass scans `{prefix}:*:events`, which is
  O(keyspace) in the prefix. A deployment with a very large number of live partition keys pays it
  every pass; a `MaxStreamLength` and Redis's own key expiry are what keep the set bounded, and
  neither is set by default.
- **A node holding a stream lease that dies stalls that key for the lease's TTL.** Bounded and
  visible, and it is the same trade `DurableLease` already makes for an instance.
- **Order is per key and *not* per topic**, so a subscriber reasoning about "the order I saw
  events on this topic" is reasoning about something nothing offers. That was already true of
  publication; it is now true of consumption, which is the point.
- **A dead-lettered event leaves a gap in its key's order.** Stated in
  [ADR-0038](ADR-0038-a-poison-message-is-dead-lettered.md), which is where the trade is taken.

## Revisit when

- A transport partitions server-side — Kafka assigns partitions to consumers, so the lease is
  redundant there and would be a second, weaker exclusion fighting the broker's own.
- The lease-per-stream round trip is measured as the bottleneck of a real subscription, at which
  point a node holding a stream across passes (a sticky assignment) is the next design and needs
  its own record for what happens when it dies.
- A subscription needs global order, which this record refuses and ADR-0018 refuses before it.
- `partition_key` becomes declarable (`event.partitionKey`, an open row of
  [ADR-0017](ADR-0017-manifest-v1-freeze-criteria.md) F1's table), at which point the number of
  live streams becomes an authored decision rather than one instance id per flow run.

# ADR-0072: A RabbitMQ queue is a partition only while one node holds it, so an undispositioned delivery is held rather than requeued

**Status:** Accepted
**Date:** 2026-08-02
**Deciders:** Runtime team, Platform architecture
**Amends:** [ADR-0037](ADR-0037-the-consumer-offers-per-key-order.md) ·
[ADR-0018](ADR-0018-outbox-publication-and-ordering.md)

> [ADR-0037](ADR-0037-the-consumer-offers-per-key-order.md) offers per-`partition_key` order and
> was written against a transport where the partition is a server-side object: a Redis stream
> *is* a key's log, so reading one front to back reproduces the publisher's order for that key
> with no bookkeeping at all. RabbitMQ has no such object. This record says what the guarantee
> costs on a transport that delivers per *queue*, and where it stops.

## Context

1. **The publisher's half is free and the consumer's is not.** `RabbitMqEventPublisher` sends one
   event at a time down one channel and awaits each confirm, so a topic exchange receives one
   key's events in staging order and every queue bound to them is filled in that order.
   ADR-0018 decision 3 is therefore satisfied on publication by construction. Consumption is
   where the work is: one queue carries every key interleaved, so `BusPartitionBatch` has to be
   *made* out of a header rather than read off a server-side partition.

2. **`FlowBusScan` stops a partition's loop at the first delivery that reached no outcome**
   (`PartitionAsync`: "continuing would run entry *n+1* of this key before *n*"). So the runtime
   already relies on a consumer being able to re-offer a delivery it did not dispose of, ahead of
   that key's later events. On Redis that is `XAUTOCLAIM` before `XREADGROUP` — the pending entry
   comes back first because the broker keeps a pending-entries list.

3. **AMQP has no pending-entries list, and the obvious substitute reorders.** The obvious
   substitute is `basic.nack` with requeue. Measured on RabbitMQ 3.12.1: a **classic** queue puts
   a requeued message back in its original position, and a **quorum** queue puts it at the
   **back**. Three messages taken and requeued came back as `4,5,1,2,3`. A consumer that nacked
   what a failing flow left behind would therefore let that key's later events overtake it — the
   exact reordering force 2 stops the loop to prevent.

4. **The queue has to be a quorum queue for a different reason, so force 3 is not escapable by
   choosing classic.** [ADR-0038](ADR-0038-a-poison-message-is-dead-lettered.md) bounds
   redelivery on `BusDelivery.DeliveryCount`, and RabbitMQ 3.12 reports `x-delivery-count` and
   honours `x-delivery-limit` on quorum queues only. On a classic queue the count is
   `redelivered`, a boolean, and ADR-0038's undeliverable condition becomes unenforceable.

5. **A second `basic.ack` for one delivery tag closes the channel.** Measured: `406
   PRECONDITION_FAILED - unknown delivery tag`, and it takes every other outstanding delivery on
   that channel with it.
   [ADR-0036](ADR-0036-a-message-is-acknowledged-when-its-flow-is-journalled.md) acknowledges
   after a commit and a node may die between the two, so `IBusConsumer` requires
   `AcknowledgeAsync` to be idempotent and answer `false`. AMQP does not offer that.

Options rejected:

- **One queue per partition key.** The literal reading of "a queue is the partition". `FlowEmitter`
  writes `PartitionKey = ctx.FlowInstanceId`, so this is one queue per flow instance — unbounded,
  and each needs a binding. Rejected on arithmetic.
- **A classic queue, so a requeue keeps its position.** Buys force 3 and loses force 4: no
  delivery count, so no poison bound, so the head-of-line block ADR-0038 exists to end comes
  back. A weaker guarantee traded for a stronger one is not an improvement.
- **`basic.consume` with a prefetch instead of `basic.get`.** A push consumer delivers on the
  client's dispatch threads, where the lease, the journal and `FlowHost` are not — the same
  objection `IBusConsumer`'s own remarks make to a callback-shaped contract one level up.
- **Track delivery counts ourselves, keyed by message id, so a classic queue would do.** A count
  in one node's memory is not a count: a fleet would give each node its own, and a restart would
  reset it. It is the number ADR-0038's bound rests on and inventing it is worse than not having it.

## Decision

### 1. The consumer makes the partitions, from a header, in queue order

One `ReceiveAsync` pass takes messages off the queue in order, groups them by
`flowx-partition-key`, and returns one `BusPartitionBatch` per distinct key with that key's
messages in the order the queue gave them up. The partition key is a **header**, never the
routing key: routing by key would make a queue's bindings depend on which keys exist, which is
unknowable at declaration time and unbounded at run time.

### 2. A delivery nobody dispositioned is **held un-acknowledged**, never requeued

It stays outstanding on the consumer's channel — where the broker will not offer it to anyone
else — and the next `ReceiveAsync` offers it again, before anything taken since. This is force 2's
requirement met without force 3's reordering, and it is `XAUTOCLAIM`-before-`XREADGROUP` kept in
the plugin's own bookkeeping because AMQP has nowhere to keep it.

`maxPartitions` and `maxPerPartition` shape what is *offered*, not what is *taken*: a message
beyond a limit is held and offered next pass rather than handed back.

### 3. The delivery count is the broker's plus this consumer's own re-offers

`x-delivery-count` stops moving the moment the consumer takes a message and holds it, so a
message whose flow refuses every pass would be re-offered for ever with the count stuck at one —
ADR-0038's bound quietly disabled. `BusDelivery.DeliveryCount` is therefore
`x-delivery-count + offers-by-this-consumer`, which is one on a first delivery, as the contract
requires.

### 4. Acknowledgement is answered from the held set, and the token names the channel

`AcknowledgeAsync` returns `false` without touching the broker for a token the consumer is no
longer holding, which is ADR-0036's idempotency requirement met over a protocol that refuses it.
The token is `{channel-epoch}:{delivery-tag}`, because tags restart at 1 on every channel and a
token from a dead one must not be mistaken for a live tag on a new one.

### 5. Automatic recovery is off

The client's recovery reopens a channel and re-declares its topology, and in doing so silently
voids every delivery tag the consumer is holding. Decision 2 is built on those tags, so a
shutdown has to be final and visible. Everything above already retries on a schedule; a
connection reopened on the next pass is the same behaviour with none of the ambiguity.

## Consequences

**Positive**

- **`PublisherConformance` passes unchanged, against a mechanism with nothing in common with the
  first two.** That is a stronger statement about the suite than a second implementation could
  make: Redis reaches ADR-0018 decision 3 by writing one totally-ordered log per key, and this
  reaches it by publishing serially into an exchange that knows nothing about keys.
- **Per-key order survives a flow that refuses**, which is the common case and the one
  `FlowBusScan` is written around. Asserted end to end against a real broker in
  `ADeliveryThatReachedNoOutcomeIsOfferedAgainAheadOfItsKeysNextEvent`.
- **ADR-0038's bound is enforced by two independent mechanisms**: the host's, which diverts with a
  sentence, and the queue's `x-delivery-limit`, which diverts a message nothing is driving at all.

**Negative / accepted trade-offs**

- **Per-key order does not survive a node death.** When the channel closes, the broker requeues
  what was held — to the *back* of a quorum queue — so a later event of that key can be processed
  first. Redis does not have this gap: `XAUTOCLAIM` returns a dead consumer's entries in stream
  position. This is the price of force 4 and it is the whole reason this record exists rather
  than a comment. It needs a node to die, not merely a flow to fail.
- **The held set is this node's memory, and a restart forgets it.** Nothing is lost — the broker
  still owns every held message — but the *order* guarantee restarts with it, and so does the
  re-offer half of the delivery count.
- **One round trip per message, both ways.** `basic.get` is a request per message and a confirmed
  publish is a round trip per event, paid inside the outbox's claim transaction.
  `FlowXOptions.BusReceiveBatchSize` and the outbox's batch size are the knobs.
- **`DeliveryLimit` and `BusMaxDeliveries` are two numbers for one idea.** The plugin cannot see
  `FlowXOptions`, so the default here is four times the host's on purpose — the host's rule fires
  first and its dead letters carry a sentence. A deployment that sets them the other way round
  gets `x-death: delivery_limit` instead of a reason, which is worse and is not prevented.
- **A message this consumer holds is invisible to every other node** until this one dies or
  disposes. A node that hangs holding a partition's backlog stalls that key for the visibility
  timeout, which for AMQP means "until the connection's heartbeat fails".

## Revisit when

- RabbitMQ 4.x is the floor. Classic queues gained `x-delivery-limit` in 4.0, at which point
  force 4 no longer forces quorum, and a classic queue's position-preserving requeue would close
  the node-death gap in the first negative consequence above.
- A single-active-consumer queue (`x-single-active-consumer`) is measured to redeliver in
  position after a consumer is cancelled, which would let the fleet's ordering be the broker's
  rather than the partition lease's.
- The held set is found to be large enough to matter — a node holding thousands of undispositioned
  messages — at which point `maxPartitions × maxPerPartition` is the wrong bound and the consumer
  needs to release the tail deliberately, accepting the reordering to reclaim the memory.
- A transport arrives whose partition *is* a server-side object with per-consumer offsets (Kafka),
  at which point decision 1 is the wrong shape for it and this record does not apply.

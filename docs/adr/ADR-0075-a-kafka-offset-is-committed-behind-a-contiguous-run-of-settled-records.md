# ADR-0075: A Kafka offset is committed behind a contiguous run of settled records, never at the record acknowledged

**Status:** Accepted
**Date:** 2026-08-03
**Deciders:** Platform architecture, Runtime team
**Relates to:** [ADR-0036](ADR-0036-a-message-is-acknowledged-when-its-flow-is-journalled.md), [ADR-0037](ADR-0037-the-consumer-offers-per-key-order.md), [ADR-0038](ADR-0038-a-poison-message-is-dead-lettered.md), [ADR-0074](ADR-0074-service-bus-topology-is-created-by-a-deployment-not-by-a-consumer.md)

> `IBusConsumer.AcknowledgeAsync` settles **one delivery**. Kafka has no such operation: a consumer
> group commits an **offset**, which means "every record below this is done". The two are not the
> same shape, and mapping one onto the other naively loses records without failing anything.

## Context

1. **Every other transport settles one message.** Redis Streams acknowledges an entry id, RabbitMQ
   acknowledges a delivery tag, Service Bus completes a lock token. In all three, settling message
   *n* says nothing about message *n − 1*, so `IBusConsumer`'s contract maps directly.

2. **Kafka's unit is a watermark, and the runtime settles out of order.** `FlowBusScan` hands a
   partition's deliveries to flows and acknowledges each as its flow reaches a journalled outcome
   ([ADR-0036](ADR-0036-a-message-is-acknowledged-when-its-flow-is-journalled.md)). Two flows over
   two records finish in whatever order their work takes. Committing the offset of whichever
   finished first would tell the coordinator that everything below it — including a record whose
   flow is still running, or whose node has just died — is done.

3. **What that loses is invisible.** The skipped record is not redelivered, not dead-lettered and
   not reported: the group simply resumes above it. There is no error, no metric and no log line,
   because from Kafka's side nothing went wrong. That is the failure mode this repository keeps
   finding in other forms — a gate whose subject is absent — expressed as data loss.

4. **The alternatives were considered and are worse.**
   - *Commit only in order, blocking until the oldest finishes.* That is head-of-line blocking as a
     durability strategy, which [ADR-0038](ADR-0038-a-poison-message-is-dead-lettered.md) already
     calls a bug rather than a strategy.
   - *One partition per key, so nothing is ever out of order.* Kafka's partition count is fixed at
     topic creation and bounded by the cluster; a key space is not.
   - *Store settlement in the journal instead.* That makes every acknowledgement a database write
     on the consuming node's behalf and re-implements the coordinator badly.

5. **One topic carries every event type, so a group reads records it did not subscribe to.** Kafka
   has no server-side filter. Those records must be settled as they are seen — leaving them open
   would hold the watermark behind the first one for ever, which is the same loss condition
   arriving from the opposite direction.

## Decision

**`KafkaBusConsumer` keeps a per-partition ledger, and commits the highest offset with every
offset below it settled.**

1. **A poll holds every record it returns.** The ledger records the offset, counts the delivery and
   keeps the record itself, because dead-lettering needs the body and the broker will not offer it
   again while the assignment is held.

2. **`AcknowledgeAsync` settles, then commits as far as the ledger allows.** If offsets 11 and 12
   are settled while 10 is not, nothing commits. When 10 settles, the watermark moves past all
   three in one call. A partition whose oldest record never settles never commits — which is
   correct, because that record has to be redelivered, and `FlowXOptions.BusMaxDeliveries` with
   ADR-0038's rule is what eventually diverts it.

3. **The committed value is the settled offset plus one**, which is Kafka's convention for "the
   next record to read". Committing the settled offset itself replays it for ever.

4. **A record of another event type is settled on sight**, without a commit of its own. This group
   has nothing to do with it, and saying so at once is what keeps the watermark moving.

5. **The delivery count is this process's own.** Kafka does not count deliveries: a rebalance or a
   restart replays from the last commit and the record looks new. So `BusDelivery.DeliveryCount`
   is counted in the ledger and resets when the process does, which makes ADR-0038's bound a
   backstop on this transport rather than a guarantee. Stated here because a reader comparing
   transports would otherwise assume it is the broker's number, as it is on RabbitMQ and Service
   Bus.

6. **Topics are not created.** Partition count, replication factor and retention are a deployment's
   decision for [ADR-0074](ADR-0074-service-bus-topology-is-created-by-a-deployment-not-by-a-consumer.md)'s
   reasons; this package holds no `AdminClient`.

## Consequences

- **A slow flow delays a commit but loses nothing.** The cost of the rule is that a partition's
  progress is bounded by its oldest unfinished record. That is a throughput property and it is
  visible; the alternative is a correctness property and it is not.

- **A redeploy replays from the last contiguous point**, so the records above an unfinished one are
  delivered a second time. At-least-once already permits that, and it is why every capability
  reached from a bus trigger is journalled on an idempotency key.

- **`KafkaBusConsumerTests.AnOffsetIsNotCommittedPastAnUnsettledRecord` is the whole of the
  evidence.** Three records of one key, the second and third acknowledged, then the group restarted
  — all three come back. Removing the gap check from the ledger makes exactly that test fail and no
  other, which was confirmed by doing it.

- **Per-key order costs nothing here.** Records sharing a key hash to one partition and a partition
  is a total order, so ADR-0037's promise is the broker's own rather than a discipline the
  publisher keeps. Kafka is the fourth implementation `PublisherConformance` holds unchanged, and
  the first where the property is free.

## Revisit when

- The runtime gains a way to settle a partition's deliveries in order — a per-partition barrier in
  `FlowBusScan` — at which point the ledger becomes a bookkeeping layer over something the caller
  already guarantees, and could be deleted rather than kept in step.

- Kafka gains per-record acknowledgement. KIP-932's share groups are the candidate; if they reach a
  stable release and `Confluent.Kafka` binds them, a share group settles one record at a time and
  this whole class becomes unnecessary.

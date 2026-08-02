# ADR-0073: A dead-letter destination stays derived even where the broker has a first-class one, so `DeadLetter` still reaches no artifact

**Status:** Accepted
**Date:** 2026-08-02
**Deciders:** Platform architecture, Runtime team
**Amends:** [ADR-0038](ADR-0038-a-poison-message-is-dead-lettered.md)

> [ADR-0038](ADR-0038-a-poison-message-is-dead-lettered.md) names this exact condition in its
> *Revisit when*: *"A transport carries its own dead-letter destination (Azure Service Bus, SQS
> redrive), at which point deriving one from the source is the wrong shape and `DeadLetter`
> becomes readable."* RabbitMQ is that transport. This record reopens the question the condition
> asked for and answers it **no**, which is the outcome
> [ADR-0039](ADR-0039-a-bus-subscription-publishes-no-new-manifest-field.md)'s precedent
> permits: nothing arrives, and the record says which candidate was examined and why it did not.

## Context

1. **`KafkaTriggerAttribute.DeadLetter` has been declared and unread since it was written.**
   ADR-0038 left it that way and said why: "a per-subscription destination is deployment
   configuration and the manifest deliberately does not publish it". `docs/09-Trigger-Model.md §8`
   records the property as unread rather than pretending otherwise.

2. **RabbitMQ genuinely does have a first-class destination.** A queue declared with
   `x-dead-letter-exchange` diverts on rejection, on TTL expiry, on overflow and on
   `x-delivery-limit` being exceeded — with no consumer involved at all. That is materially
   different from Redis, where `RedisStreamBusConsumer` copies the entry to `{source}:dead`
   itself and nothing happens if the consumer is not running.

3. **What changed is the mechanism, not the ownership.** The revisit condition assumed the two
   move together. They do not. ADR-0039's argument was never about whether a broker can hold a
   destination; it was that publishing one *"would put deployment configuration into a contract
   document and give `flowx diff` a whole class of changes to report that no consumer can act
   on"*. A queue argument is deployment configuration in exactly the sense that a Redis key
   suffix was. Nothing about `x-dead-letter-exchange` makes the string a promise to whoever
   publishes the topic.

4. **Making it readable is not a small change and buys a worse artifact.** `DeadLetter` would
   have to reach `BusSubscriptionModel`, `BusSubscription`, the generated registration, the
   manifest schema (which is `additionalProperties: false`) and a `flowx diff` rule — and the
   field it produced would be a string that means something different on every transport and
   nothing at all on Redis. ADR-0034's test for a manifest field — *is this the string somebody
   else uses to reach the flow?* — answers no.

5. **The broker's own divert loses the sentence.** `IBusConsumer.DeadLetterAsync` asks for "why,
   in a sentence an operator can act on", and ADR-0038's value is that the sentence names which
   of its two conditions fired. A `basic.reject` that let `x-dead-letter-exchange` move the
   message records the cause as `x-first-death-reason: rejected` — one word, the same word for
   both conditions.

Options rejected:

- **Publish `DeadLetter` as `trigger.deadLetter`.** Force 4. Also refused already by ADR-0039 on
  ADR-0034's argument, which force 3 shows is unchanged.
- **Read `DeadLetter` without publishing it** — the attribute value becomes the exchange name,
  and the manifest stays as it is. Rejected because it makes a *compiled* value out of a
  *deployment* one: two environments could then no longer point the same build's dead letters at
  different exchanges, which is precisely what an address in options is for. It also leaves
  `[BusTrigger]` — which declares no `DeadLetter` — with no way to say the same thing.
- **`basic.reject` and let the DLX do everything.** Force 5. Cheaper, no duplicate risk, and it
  discards the one thing ADR-0038 exists to record.

## Decision

### 1. `KafkaTriggerAttribute.DeadLetter` stays unread, and the manifest gains no field

The revisit condition ADR-0038 named is met and the answer is no. `docs/09-Trigger-Model.md §8`'s
statement remains true; ADR-0039's count of unproduced fields does not move; no `flowx diff` rule
is added.

### 2. The destination is derived from the plugin's options, exactly as it is derived from the source stream in Redis

`RabbitMqOptions.DeadLetterExchange` (default `flowx.events.dead`) and
`RabbitMqOptions.DeadLetterQueueFor(group, topic)` — one dead-letter queue per subscription, so
"what did *this* subscription give up on" is answerable without reading everyone else's failures.
That is `RedisStreamBusConsumer.DeadLetterSuffix`'s arrangement with a broker object in place of
a key suffix.

### 3. `DeadLetterAsync` publishes a copy carrying the reason, then acknowledges the original

Copy before acknowledge, never the reverse — ADR-0038 decision 3, unchanged: a crash between the
two redelivers the original, which at-least-once already permits; the other order loses the
message. The copy keeps the original's `message-id`, so an operator returning it to the source
queue by hand does not mint an id the derived instance id would fail to recognise as a
redelivery.

### 4. The queue still declares `x-dead-letter-exchange`, and it is a backstop rather than the path

Two paths, one destination. The host's rule diverts with a sentence and is what an operator reads;
the queue's argument catches what nothing is driving — a node that crashes mid-flow every time, a
subscription removed while its queue still has a backlog, a consumer that is not FlowX's.
`RabbitMqOptions.DeliveryLimit` defaults to 20 against `FlowXOptions.BusMaxDeliveries`'s 5 **on
purpose**, so the host's rule fires first in every ordinary deployment and `x-death` never has to
stand in for a reason.

## Consequences

**Positive**

- **Three transports, one dead-letter contract.** A dead letter is a copy plus
  `flowx-dead-letter-reason` and `flowx-dead-letter-at`, wherever it is read from. An operator's
  question is the same question on Redis and on RabbitMQ.
- **The manifest stays a contract document.** ADR-0039's line holds under the first real pressure
  it has had, which is worth more than the field would have been.
- **A message nothing is driving still stops being redelivered.** Asserted against a real broker
  in `TheBrokerDeadLettersAMessagePastTheQueuesOwnDeliveryLimit`, which reaches the path by
  killing the consumer's channel each round rather than by asking for it.

**Negative / accepted trade-offs**

- **`DeadLetter` is still a declared property nothing reads, now on two transports.** A reader of
  `KafkaTriggerAttribute` sees a destination and can reasonably expect it to be one. The
  alternative was a manifest field, and this is the cheaper wrong impression.
- **Two dead-letter shapes reach one queue.** A message diverted by the host carries
  `flowx-dead-letter-reason`; one diverted by `x-delivery-limit` carries `x-death` and
  `x-first-death-reason` instead. Anything reading the queue has to handle both, and the tests do.
- **A dead letter is a publish and can be duplicated.** The copy is confirmed before the original
  is acknowledged, so a crash between them redelivers the original and dead-letters it twice. That
  is the ordering ADR-0038 chose and the duplicate is the cost of it.
- **Nothing replays a dead-lettered message.** No `flowx` verb, no automatic return path; recovery
  is reading the queue, judgement, and publishing back to the source exchange by hand — unchanged
  from ADR-0038.
- **The dead-letter queue is unbounded.** A subscription that dead-letters steadily grows a queue
  nothing reads, and there is no equivalent of `RedisStreamOptions.MaxStreamLength` here because a
  queue-length limit that *discards* would lose exactly the messages this record exists to keep.

## Revisit when

- A transport arrives whose dead-letter destination is part of the *contract* rather than of the
  deployment — a redrive policy a publisher must know about — at which point force 3's separation
  of mechanism from ownership fails and `DeadLetter` becomes readable after all.
- A cross-application registry exists that could give `event.consumedBy` a meaning
  (ADR-0039 §2), because a subscriber registry is also where a per-subscription dead-letter
  address would stop being purely local.
- Replaying a dead letter is common enough to want a verb, at which point the two shapes in the
  second negative consequence have to be reconciled into one readable record.
- A deployment is found running with `DeliveryLimit` below `BusMaxDeliveries`, which turns every
  dead letter into `x-death: delivery_limit` and silently discards the reason. If that happens by
  accident rather than by choice, the two numbers need to become one.

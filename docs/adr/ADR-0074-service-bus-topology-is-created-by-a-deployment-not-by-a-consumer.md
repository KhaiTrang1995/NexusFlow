# ADR-0074: Service Bus topology is created by a deployment, not by a consumer, so `SubscribeAsync` verifies and never creates

**Status:** Accepted
**Date:** 2026-08-03
**Deciders:** Platform architecture, Runtime team
**Relates to:** [ADR-0009](ADR-0009-plugin-contracts.md), [ADR-0036](ADR-0036-a-message-is-acknowledged-when-its-flow-is-journalled.md), [ADR-0038](ADR-0038-a-poison-message-is-dead-lettered.md), [ADR-0073](ADR-0073-a-dead-letter-destination-stays-derived-even-where-the-broker-has-one.md)

> `IBusConsumer.SubscribeAsync` is documented as *"makes the subscription exist at the broker,
> idempotently"*, and both shipped implementations do exactly that. `FlowX.AzureServiceBus` does
> not, and returns `false` — "it was already there" — on every successful call. This record is why
> that is the contract being honoured rather than broken.

## Context

1. **The contract's wording was written from two transports that can both declare.** Redis
   Streams creates a consumer group with `XGROUP CREATE … MKSTREAM`; RabbitMQ declares an
   exchange, a queue and a binding. Both operations are idempotent, cheap, and available to the
   *same credentials that consume* — so calling them on every pass costs nothing and repairs a
   broker restored from an empty state, which is the reason `RabbitMqBusConsumer` does it on every
   pass rather than once at start-up.

2. **Azure Service Bus splits those credentials in two.** Sending and receiving are data-plane
   operations authorised by `Send` and `Listen`. Creating a topic or a subscription is a
   *management-plane* operation authorised by `Manage` — a different SDK client
   (`ServiceBusAdministrationClient`), a different throttling regime, and a right that production
   deployments deliberately do not grant to an application identity. A consumer that declared its
   own topology would make `Manage` a runtime requirement of every FlowX deployment on this
   transport.

3. **Real deployments already create these entities somewhere else.** Bicep, Terraform, an ARM
   template or a portal click — with the retention, duplicate-detection window, lock duration,
   `MaxDeliveryCount`, auto-delete and forwarding settings that belong to an operations decision
   rather than to an application. A consumer that created a subscription would create it with this
   package's defaults, silently, and only when nothing existed — so the entity a deployment
   reviewed and the entity an application made would differ in every one of those settings, and
   nothing would say which one a given namespace had.

4. **Creating late is worse than not creating.** A Service Bus subscription receives only messages
   published *after* it exists. A consumer that created its own on first run would succeed, report
   success, and then read an empty backlog for ever — the events it was supposed to handle having
   been published to a topic that had nothing bound. The failure is silent, and it is the exact
   failure the "makes it exist" wording was written to prevent on transports where creating late
   is harmless.

5. **The emulator enforces this and is therefore the right thing to test against.** The Azure
   Service Bus emulator declares its entities from a configuration file at start-up and supports
   no management operations at all. A suite that ran against it while the plugin created entities
   would have to skip its own subject — which `docs/26-CRM-Sample.md §9` names as a failing gate,
   not a passing one.

## Decision

**`AzureServiceBusConsumer.SubscribeAsync` reaches the subscription and reports whether it could
be read. It creates nothing, and the package does not reference `Azure.Messaging.ServiceBus.Administration` at all.**

1. **The call still reaches the broker on every pass.** It opens the receiver and peeks. A
   namespace whose subscription somebody deleted fails loudly on the next pass rather than reading
   empty for ever, which is the property the "called on every pass" instruction in `IBusConsumer`
   exists to give.

2. **Its boolean is always `false` on success**, which is the contract's "it was already there".
   That is the honest answer: on this transport no call ever creates a group, and returning `true`
   would claim credit for a deployment's work.

3. **A failure names the entity a deployment has to have, on every failure and not only on the
   one the broker labelled `MessagingEntityNotFound`.** The error message carries the subscription
   name this package is looking for and the filter it must hold — `Subject = '<event type>'` —
   followed by whatever the broker said.

4. **There is one error code and not two.** A distinct `subscription_missing` code would be the
   more useful contract and is not one this package can honour: a namespace answers a missing
   entity with `MessagingEntityNotFound`, and the emulator answers the same condition with a
   timeout. A branch only one of the two can reach is a branch nothing tests, so the distinction
   lives in the message — where both carry it — rather than in a code a caller would branch on.

5. **The naming is fixed and injective.** A subscription is `{group}--{eventType}`; the separator
   is two characters an entity name may contain but an event type may not, so
   `scoring--lead.created` cannot also be read as group `scoring.lead` on type `created`. The name
   is the only thing carrying the mapping, so it has to be unambiguous.

## Consequences

- **A deployment must create the topic and one subscription per `[BusTrigger]` group**, each with
  a correlation filter on `Subject`. `tests/FlowX.AzureServiceBus.Tests/emulator-config.json` is a
  worked example of the shape, and the consumer's error message states it at the moment somebody
  needs it.

- **The application identity needs `Send` and `Listen` and not `Manage`.** That is a smaller grant
  than the other two transports need and it is a deliberate outcome, not a side effect.

- **`IBusConsumer`'s "makes the subscription exist" wording is now aspirational for one of three
  implementations.** It is not amended here: the sentence is right for a transport that can
  declare, and the contract's own return value already distinguishes "I made it" from "it was
  there". A third implementation returning only the second is inside what the contract says, not
  outside it.

- **Dead-lettering needs no configuration on this transport**, which is [ADR-0073](ADR-0073-a-dead-letter-destination-stays-derived-even-where-the-broker-has-one.md)'s
  conclusion reached from the other side: every subscription has a dead-letter sub-queue with no
  address to hold, so nothing is derived and nothing is published.

- **The suite runs against the emulator with zero skips**, which is what `docs/26 §9` demands of a
  broker plugin — and it runs serially, because one shared namespace is the price of not creating
  entities.

## Revisit when

- Azure Service Bus grants entity creation to a `Listen`-scoped identity, or the emulator gains
  management operations. Either would remove the reason for the split and make "declare on every
  pass" available here as it is on RabbitMQ.

- A second transport in this family arrives — SQS with its own queue-creation rights, or Event
  Grid — and the same shape has to be decided twice. At that point the "verify, never create"
  behaviour is worth pulling into `IBusConsumer` as an explicit capability rather than left as a
  per-plugin reading of one boolean.

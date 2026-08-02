# ADR-0039: A bus subscription publishes no new manifest field, and `event.consumedBy` stays open

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Platform architecture, Runtime team
**Amends:** [ADR-0017](ADR-0017-manifest-v1-freeze-criteria.md) ·
[ADR-0005](ADR-0005-manifest-as-build-artifact.md)

> The precedent set this week is that a field arrives with its producer **and** its `flowx diff`
> rule in the same commit, or not at all
> ([ADR-0021](ADR-0021-manifest-publishes-the-wait.md),
> [ADR-0034](ADR-0034-the-manifest-publishes-a-schedules-address.md)). This record is the other
> outcome that precedent permits: nothing arrives, and it says which candidate was examined and
> why it did not.

## Context

1. **The address a bus subscription has is already published.** The schema's `trigger` object
   carries `kind`, `transport`, `topic` and `group`, all four `additionalProperties: false`, and
   `TriggerReader.Shape` has written `transport`, `topic` and `group` for `[KafkaTrigger]` since
   the trigger reader was written. Binding `Bus` needs nothing the manifest does not already say.

2. **`[BusTrigger]` writes two of those four and not the third.** It names no broker, so it
   publishes no `transport` — which is the honest reading of "this flow consumes topic *T* as
   group *G*, on whatever bus the host wired". `TriggerModel.Transport`'s own summary already
   allowed for it: *"broker family … or `null` when the attribute does not name one."*

3. **What is *not* published is tuning, and the model already says so.** `MaxInFlight` and
   `DeadLetter` on `KafkaTriggerAttribute`, and `BusScanInterval`, `BusMaxDeliveries` and
   `BusMaxConcurrentStreams` on `FlowXOptions`, all configure how this deployment runs a
   subscription. `TriggerModel`'s remarks draw exactly this line — *"publishing them would put
   deployment configuration into a contract document and give `flowx diff` a whole class of
   changes to report that no consumer can act on"* — and ADR-0034 took the same decision for a
   schedule's `MissedFire`.

4. **One row of F1's table did become answerable, and that is the real question.**
   [ADR-0017](ADR-0017-manifest-v1-freeze-criteria.md) §1 tables `event.consumedBy` as
   *"nothing — there is no subscriber concept | needs a transport and a registry"*. This work
   builds a subscriber concept and a registry. The field is already in the committed schema, so
   producing it would **close** an F1 row rather than open one.

Options rejected:

- **Publish `consumedBy` from the subscriptions in this compilation.** Rejected on force 4's own
  terms, below.
- **Publish `MaxInFlight` as `trigger.maxInFlight`** — a new field, tuning, and
  `additionalProperties: false` means it is a schema change; refused by ADR-0034's argument
  unchanged.
- **Publish `DeadLetter` as an address** — it is a destination this build derives rather than
  reads ([ADR-0038](ADR-0038-a-poison-message-is-dead-lettered.md)), so publishing the declared
  value would publish a string nothing uses.

## Decision

### 1. The manifest gains no field, and F1's count of unproduced fields does not move

`[BusTrigger]` and `[KafkaTrigger]` both publish `kind`, `topic` and `group`; the Kafka one also
publishes `transport`. All four already had producers. No `flowx diff` rule is added, because no
field is added, and the existing trigger rules classify a changed `topic` or `group` exactly as
they classified them before.

### 2. `event.consumedBy` stays unproduced, and this is the reason it is not a gap

The compiler can see only the subscriptions **inside the compilation it is building**. A field
answering "who consumes this event" that lists only in-process subscribers is a *lower bound
presented as a fact*, and its absence — the common case, an event consumed by another service —
is indistinguishable from "nothing consumes this". That is precisely the failure ADR-0017 §1
names as the reason the criterion exists at all: *"a consumer reading the schema cannot tell
'this application has no owner recorded' from 'the compiler never looked'."*

It is worse than an absent field, because `flowx diff` would act on it: removing the one
in-process subscriber of an event would classify as a breaking change while removing the last
out-of-process one classifies as nothing. A gate that is confidently wrong is worse than a gate
that is empty.

**What would close the row** is a registry spanning applications — the manifests of the services
that subscribe, joined by a tool that has all of them. That is the *"registry"* half of F1's own
"needs a transport and a registry", and the transport half is now built while the registry half
is not.

### 3. The declared subscription is still visible, in the `triggers` block

A reader asking "does this application consume `order.placed`" gets a complete answer from the
flow's own trigger entry. What they do not get is the reverse index, and the reverse index is the
thing that cannot be built from one manifest.

## Consequences

**Positive**

- **The freeze criteria are untouched by the largest trigger-binding change of the phase.** F1's
  thirteen-field count is exactly where ADR-0034 left it, and this record says so explicitly
  rather than leaving it to be recomputed.
- **The schema keeps `additionalProperties: false` with no new property**, so every existing
  manifest consumer and every existing `flowx diff` rule is unaffected.
- **`event.consumedBy` now has a written reason rather than a stale table row.** Its cost line
  said *"needs a transport and a registry"*; half of that has landed and the row is unchanged,
  which is a fact a reader of F1 would otherwise have to rediscover.

**Negative / accepted trade-offs**

- **The manifest cannot answer "who consumes this event", and now visibly could have.** A reader
  who knows the compiler walks `[BusTrigger]` will reasonably expect the field, and will find the
  reason here rather than in the document.
- **`transport` is optional and now genuinely varies.** A `[KafkaTrigger]` publishes it and a
  `[BusTrigger]` does not, so a consumer diffing two manifests sees a trigger gain or lose the
  property when an author switches attributes — a Neutral change that looks like an addition.
- **An F1 row stayed open on judgement rather than on cost.** Every other open row is open
  because the fact does not exist; this one is open because the fact exists and is misleading.
  That is a different kind of "unmet" and the criterion does not currently distinguish them.

## Revisit when

- A tool joins several applications' manifests, at which point `consumedBy` can be produced with
  the whole picture behind it and this record's force 4 no longer applies — and the field should
  then arrive with its `flowx diff` rule in the same commit.
- A subscription grows an address-shaped property the manifest does not carry — a partition, a
  filter expression, a subscription name distinct from the group — which would be an address
  rather than tuning and would belong in the document.
- `PerTenant` or a filter becomes declarable on a bus trigger, which turns a fan-out into part of
  the address.
- The freeze ([ADR-0017](ADR-0017-manifest-v1-freeze-criteria.md)) closes with `event.consumedBy`
  still in the schema and still unproduced, at which point F1 requires it to be **deleted** from
  the schema, and this record is the argument for deleting rather than implementing it.

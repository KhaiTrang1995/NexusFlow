# ADR-0029: The manifest publishes a schedule's address and not its firing policy

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture
**Amends:** [ADR-0017](ADR-0017-manifest-v1-freeze-criteria.md)

> **[ADR-0017](ADR-0017-manifest-v1-freeze-criteria.md)'s third Revisit-when reads: *"the schema
> gains a field before the freeze — every addition re-opens F1 and F5 for that field"*. Binding
> a transport is exactly the moment somebody proposes one.** `[CronTrigger]` declares six
> properties; the manifest carries two; four now reach running code or a diagnostic for the first
> time, and each is a candidate.
>
> **The decision is to add nothing, and the argument is that the two published fields were the
> address all along.** This record is short because the interesting half already happened:
> `cron` and `timeZone` were declared in the schema, written by `ManifestWriter` and classified
> by `flowx diff` before anything fired them. What this change did was give them their first
> producer in a real compilation — which is a different thing from a schema addition and needs
> saying, because [ADR-0021](ADR-0021-manifest-publishes-the-wait.md) set the precedent that a
> field arrives with its producer *and* its rule in one commit, and a reader is entitled to check
> that this one did too.

---

## 1. Context

### 1.1 What was already true, verified rather than assumed

| Fact | Where |
|---|---|
| The schema declares `trigger.cron` and `trigger.timeZone` | `schemas/flowx.manifest.schema.json`, `$defs.trigger` |
| The compiler reads them off `[CronTrigger]` | `TriggerReader.Shape`, the `FlowX.CronTriggerAttribute` arm |
| The compiler writes them | `ManifestWriter`, `WriteOptional(writer, "cron", …)` / `"timeZone"` |
| The CLI reads them | `ManifestDocument.ManifestTrigger.Cron` / `.TimeZone` |
| `flowx diff` classifies a change to `cron` | `ManifestDiff.Describe` includes it in a trigger's **address**, so a changed expression is `FLOWX-DIFF-001` (removed) **plus** `FLOWX-DIFF-002` (added) — both Breaking |
| `flowx diff` classifies a change to `timeZone` | `ManifestDiff.CompareTriggerTerms` → `FLOWX-DIFF-205`, Neutral |

So neither field is one of ADR-0017 §1's twelve unproduced ones, and neither is an F5
counterexample. **What was missing was a manifest that carried them.** No sample declared a
schedule, so `cron` had a producer in the compiler and no producer in practice — the same
"producer on paper and none in practice" shape ADR-0021 §2.2 worried about for `timeout`, one
step further along. `samples/workflow/flowx.manifest.json` now carries:

```json
{ "kind": "Schedule", "cron": "0 2 * * *", "timeZone": "Europe/Berlin" }
```

### 1.2 What is newly readable and might therefore be published

Binding the transport gave four of `CronTriggerAttribute`'s six properties a status they did not
have:

| Property | Status after this change |
|---|---|
| `Cron`, `TimeZone` | published; the instance id is derived from both ([ADR-0026](ADR-0026-an-occurrence-names-the-instance-it-starts.md)) |
| `MissedFire` | **executes** — three values, three behaviours ([ADR-0027](ADR-0027-a-missed-schedule-fires-late.md)) |
| `Overlap`, `Jitter`, `PerTenant` | still reach nothing at all |

`MissedFire` is the live question. It is no longer inert, it changes whether work happens, and
`ManifestDiff` has a natural place to put it.

---

## 2. Decision

**The schema gains no field. `cron` and `timeZone` continue to be the whole of what a schedule
publishes, and `MissedFire`, `Overlap`, `Jitter` and `PerTenant` are not added.**

### 2.1 The line is `TriggerModel`'s, and it was drawn before this

`TriggerModel`'s own remarks state the rule and name two of these properties while doing it:

> **Operational tuning is deliberately absent.** A Kafka trigger's `MaxInFlight`, a cron
> trigger's `Jitter` and a stream trigger's `Checkpoint` are all declared on the same attributes,
> and none of them are here. They tune how the platform runs the trigger, not what the trigger
> promises anyone outside it; publishing them would put deployment configuration into a contract
> document and give `flowx diff` a whole class of changes to report that no consumer can act on.

The schema's `trigger` object says the same in its own `description`. This decision is that
`MissedFire` is on the tuning side of that line, and it is worth arguing rather than asserting,
because it is the closest case the line has faced.

### 2.2 Why `MissedFire` is tuning and `cron` is not

**A manifest describes what an application promises to things outside it.** The test the rest of
these records use is: *is this the string somebody else uses to reach the flow?*

`cron` passes. It is a schedule's whole address — the answer to "when can I expect this to have
run", which a downstream team plans around exactly as they plan around a route. It is also, since
[ADR-0026](ADR-0026-an-occurrence-names-the-instance-it-starts.md), the string the instance id is
derived from, so it is the one value that lets an operator reconstruct a primary key.

`MissedFire` fails it, and the reason is that **nobody outside can act on the answer.** A
downstream consumer of a nightly reconciliation cares that it runs nightly. Whether it catches
up after an outage, and how much, is a decision about this deployment's tolerance for late work;
there is no version to pin, no call to change, no contract to update. It is the same category as
`MaxInFlight` — which decides whether a Kafka consumer keeps up, and is not published either.

There is a second, sharper argument. `MissedFire`'s *effect* is bounded by
`FlowXOptions.ScheduleCatchUp`, which is host configuration and not in the manifest at all.
Publishing `"missedFire": "RunAll"` would tell a consumer that every missed firing is recovered
— and a deployment with a one-hour horizon recovers an hour of them. **A published field whose
meaning depends on unpublished configuration is worse than an absent one**, because it reads as
a promise.

### 2.3 `Overlap`, `Jitter` and `PerTenant` are not published, and they are also not diagnosed

The three that still bind nothing get neither a manifest field nor a rule. Publishing them would
be ADR-0017's F1 in its purest form: three schema-declared fields nothing reads, added on the day
the transport was bound.

They get no diagnostic either, and that is a decision rather than an omission.
[FLOWX1032](../diagnostics/FLOWX1032.md)'s argument for reporting an unapplied policy is that a
declared control is **deleted** — the manifest publishes it and the plan drops it, so the
published contract promises something no code does. These three are not deleted, because they
never reach an artifact at all: no manifest field, no plan node, nothing to disagree with. What
they are is *inert*, and the honest place to record an inert property is the status box in
[09 §8](../09-Trigger-Model.md#8-schedule-trigger) that a reader consults to find out what is
bound — which now names all three and says what each would do.

`Overlap` is the one worth watching. Its default is `Skip` and it reads as though it prevented
a catch-up under `RunAll` from running its firings concurrently; it does not, and
[ADR-0027 §4](ADR-0027-a-missed-schedule-fires-late.md#4-consequences) records that as an
accepted negative.

---

## 3. Options rejected

- **A. Publish `missedFire`, with a `flowx diff` rule in the same commit.** *Rejected:* §2.2. It
  would meet ADR-0021's price of admission — the producer and the rule exist and are cheap — and
  that is exactly why it needed a reason rather than a shrug. The reason is that its meaning
  depends on `ScheduleCatchUp`, which is not in the manifest and should not be: a consumer
  reading `"RunAll"` would be told that nothing is ever missed, which is false in every
  deployment with a horizon shorter than its worst outage.
- **B. Publish all six, and let consumers ignore what they do not need.** *Rejected:* it adds
  three fields nothing writes on the same day, which is ADR-0017's F1 moving from twelve to
  fifteen while the freeze is being counted down to. It is also the trade
  [ADR-0021 §3](ADR-0021-manifest-publishes-the-wait.md#3-options-rejected)'s option E refuses by
  name.
- **C. Publish nothing at all for a schedule — drop `cron` and `timeZone`.** *Rejected,* though
  F1 explicitly allows deletion as a way to close a criterion. A schedule would then be the one
  trigger kind whose manifest entry is a bare `"kind": "Schedule"`, so two flows with different
  schedules would be indistinguishable and `flowx diff` could not report a nightly job becoming
  an hourly one. That is
  [ADR-0021 §1](ADR-0021-manifest-publishes-the-wait.md#1-context)'s "two waiting flows are
  indistinguishable" defect, reintroduced deliberately.
- **D. Publish `cron` as a normalised or expanded form — the next occurrence, say.**
  *Rejected* twice over. A next-occurrence timestamp is a value computed at build time about a
  moment that has passed by the time anyone reads it, which is F2's *"a constant standing in for
  a fact"*. And the expression is the string the instance id is derived from, so publishing
  anything but the author's own bytes would make the id unrecomputable from the manifest.

---

## 4. How ADR-0017's criteria move

Stated explicitly, because ADR-0017's Revisit-when requires an answer against each — and here the
answer is "unchanged" for all eight, which is itself the thing worth writing down.

| Criterion | Effect of this change |
|---|---|
| **F1** — every declared field has a producer | **Unchanged at twelve.** No field is added and none is removed. `cron` and `timeZone` were already produced by `ManifestWriter`; what changed is that a manifest in this repository now carries them, so F1's eventual corpus test has a fixture that exercises the `Schedule` arm — which it did not before, and which is the same gap ADR-0021 noted for `timeout` |
| **F2** — no field is a constant standing in for a fact | **Unchanged.** Both values are read from the attribute. Option D is the version of this change that would have broken F2, and it is refused above |
| **F3** — `ManifestIsComplete` covers all four of Q3's nouns | **Untouched.** A trigger is not one of the four nouns |
| **F4** — the two absent producers have landed | **Untouched.** The outbox and the policy engine are unaffected by a trigger binding |
| **F5** — `flowx diff` can see every field the freeze makes permanent | **Unchanged, and now exercised.** `cron` is part of a trigger's address in `ManifestDiff.Describe`, so changing it is `FLOWX-DIFF-001` plus `FLOWX-DIFF-002`; `timeZone` is `FLOWX-DIFF-205`. Both rules existed and neither had ever had a manifest to fire on. F5 itself stays open, because the *instrument* that would find the next unclassified field is still unwritten |
| **F6** — the escape hatch is exercised | **Untouched.** Nothing here writes `extensions`. Worth noting *for* F6 that the four unpublished properties are the kind of one-off a consumer might reach for it with, and none of them needed to |
| **F7** — the bump is one atomic change | **Untouched.** `schemaVersion` stays `0.1.0` |
| **F8** — no field whose record is still Proposed is frozen | **Unchanged, and this record is Accepted.** `cron` and `timeZone` are governed by this record and by ADR-0004; ADR-0014 remains the only Proposed record governing a field |

**The count of fields the schema declares that nothing writes stays at twelve, and the schema is
byte-identical.** That is the sentence this record was written to be able to say, and it is a
stronger one than ADR-0021 could say — that record had to argue two additions down to zero net
cost; this one adds nothing at all.

---

## 5. Consequences

**Positive**

- **The manifest's `Schedule` arm has a producer in a real compilation for the first time.**
  `samples/workflow`'s manifest carries `{"kind": "Schedule", "cron": "0 2 * * *", "timeZone":
  "Europe/Berlin"}`, so two `flowx diff` rules that had never had an input now have one.
- **Binding a transport cost the schema nothing**, which is evidence that ADR-0004's trigger
  object was drawn at the right granularity — the address survived contact with an
  implementation.
- **The tuning-versus-address line has been tested against its hardest case and held.**
  `MissedFire` is a property that changes whether work happens and is still not a promise to
  anyone outside; §2.2 is the argument, written down so the next such property is decided rather
  than debated.

**Negative / accepted trade-offs**

- **A consumer cannot tell from the manifest whether a schedule catches up.** Two applications
  publishing identical `cron` and `timeZone` may behave completely differently after an outage —
  one recovering every firing, one recovering none. That is real, and the mitigation is that the
  difference is a deployment's to explain, not the contract's. **Accepted, and named here so a
  later reader knows it was chosen.**
- **`flowx diff` reports a changed cron as a removal plus an addition, not as a modification.**
  That falls out of `ManifestDiff.Describe` treating the expression as part of the address, which
  is right for a route and slightly noisy for a schedule: moving a job from 02:00 to 03:00 reads
  as two Breaking findings rather than one. It is correct — anybody depending on the 02:00 run no
  longer has one — and it is louder than the change deserves.
- **Nothing reports `Overlap`, `Jitter` or `PerTenant` reaching no artifact.** A reader has to
  consult [09 §8](../09-Trigger-Model.md#8-schedule-trigger) to find out, where FLOWX1032's
  precedent would suggest a diagnostic. §2.3 argues they are inert rather than deleted, which is
  a genuinely different case — but it is a distinction a reader has to be told about rather than
  one they will infer.

**Revisit when:** any one of —
- **a consumer outside the application has to act on a firing policy.** The concrete shape would
  be a downstream contract that says "we assume every nightly run happens, including after an
  outage" — at which point `missedFire` is a promise and belongs in the document;
- **`PerTenant` becomes executable.** A fan-out changes what the address *is*: one declaration
  becomes one firing per tenant, which is a fact about the flow's inbound surface rather than
  about how the platform paces it, and §2.2's test would pass;
- **`flowx diff` gains a schedule-specific rule** — a "fires more often than it did" finding, say
  — which would need more than `cron` to compute and would reopen which fields feed it;
- **the freeze ([ADR-0017](ADR-0017-manifest-v1-freeze-criteria.md)) closes** with `cron` or
  `timeZone` unclassified, which §4 says cannot happen and which F5's unwritten instrument is the
  reason nobody can yet prove.

---

**Back to:** [ADR index](README.md) · [ADR-0005](ADR-0005-manifest-as-build-artifact.md) ·
[ADR-0017](ADR-0017-manifest-v1-freeze-criteria.md) ·
[ADR-0021](ADR-0021-manifest-publishes-the-wait.md) ·
[ADR-0026](ADR-0026-an-occurrence-names-the-instance-it-starts.md) ·
[ADR-0027](ADR-0027-a-missed-schedule-fires-late.md) · [22-CLI](../22-CLI.md) ·
[09 §8](../09-Trigger-Model.md#8-schedule-trigger)

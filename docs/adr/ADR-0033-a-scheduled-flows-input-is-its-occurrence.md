# ADR-0033: A scheduled flow's input is the occurrence that fired it

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture

> **Every other trigger hands a flow something a caller sent. A cron expression sends
> nothing.** An HTTP trigger has a request body, a bus trigger has a record, a signal has a
> payload. A firing at 02:00 has an instant and four words of declaration, and `FlowHost`
> journals an input on every durable instance it opens — so "what is a scheduled flow's input"
> is not a design flourish, it is a column that is about to be `NULL` on every row.
>
> **The answer is forced by a rule that already exists.** `FLOWX1007` and `FLOWX1011` make a
> flow reading an ambient clock an error, so a scheduled flow *cannot* work out for itself which
> occurrence it is. If the platform does not tell it, nothing can.

---

## 1. Context

### 1.1 What a firing knows

`FlowScheduleScan` arrives at a firing holding exactly five values: the flow's id, its version,
the cron expression, the time zone, and the instant the expression named. The first two identify
the flow it is about to start; the last three are the whole of what happened.

The instant is the interesting one, and it is **not** the instant the sweep noticed it. A sweep
runs every ten seconds by default, so an on-time firing is up to ten seconds late; a firing
recovered after an outage is as late as
[ADR-0032](ADR-0032-a-missed-schedule-fires-late.md))'s catch-up allows, which is hours. The
occurrence and "now" are different numbers, and a job that confuses them closes a different
window from the one it was asked to close.

### 1.2 Why the flow cannot ask

`DeterminismAnalyzer` raises `FLOWX1007` on an ambient `DateTime.UtcNow` inside a `Durable`
flow's class, and `PredicatePurityAnalyzer` raises `FLOWX1011` inside every builder lambda under
a strictly stronger rule — *only* the context, the flow input and prior step results. The reason
is on `DeterminismAnalyzer` itself:

> what the journal records is exactly `ctx.UtcNow`, the ids `ctx.NewId()` produced, and the seed
> `ctx.Random` was built from … A value the capability took ambiently is in none of those
> fields, so it is precisely the part of the step a replay cannot reconstruct.

A capability *may* read `ctx.UtcNow`, because that is journalled — but `ctx.UtcNow` on a resumed
instance is the resume's clock, not the firing's, and on the first attempt it is the sweep's,
not the occurrence's. Neither is the number the work is about.

So the occurrence has to arrive as data, and it has to arrive on the path that gets journalled.

### 1.3 The column that was about to be null

`FlowHost.OpenAsync` writes `dispatcher.DescribeInput(input)` to `flow_instance.input`. WP-59
added that method for a reason its own remarks record at length: before it, *"`flow_instance.input`
was NULL on every row ever written — a replay could not reconstruct what was requested, the audit
trail had no record of it, and `[Sensitive]` on an input contract protected nothing"*.

A scheduled flow started with nothing would put that defect back for one trigger kind, and worse:
for an HTTP flow the request at least existed somewhere. For a firing there is no other copy of
the occurrence anywhere in the system except the derived primary key, which is a hash and cannot
be read backwards.

---

## 2. Decision

**A flow that declares `[CronTrigger]` takes `FlowX.ScheduledFire` as its input contract, and
`FLOWX1038` reports one that does not.**

```csharp
public sealed record ScheduledFire(DateTimeOffset OccurrenceAt, string Cron, string TimeZone);
```

### 2.1 `OccurrenceAt` is the instant that was due, never the instant it ran

This is the field the decision is about. A reconciliation that fires at 06:41 after an outage is
still reconciling the window that ended at 02:00, and a flow that reasons from `OccurrenceAt`
produces the same answer whether it ran on time, ran late, or was resumed by a third node the
following morning. That is the same property the journal gives every other step, applied to the
one value a schedule contributes.

It is also what makes a firing **replayable**. The value is committed to
`flow_instance.input` in the same write that opens the instance, so a resumed attempt binds the
number the first attempt bound.

### 2.2 `Cron` and `TimeZone` are carried, and not only for the flow

A flow rarely needs them. They are there because they are two of the five terms the instance id
is derived from ([ADR-0031](ADR-0031-an-occurrence-names-the-instance-it-starts.md))), and the id
is a hash: an operator holding a `flow_instance` row with a primary key of
`c49bd6f1-9174-8d06-…` has no way to work out where it came from unless the row also says which
declaration produced it. With all three in `input`, the key is recomputable by hand.

They are also what makes a **second schedule on one flow** legible. A flow may carry
`[CronTrigger]` twice, and two firings from two expressions are two instances whose rows are
otherwise identical except for an instant.

### 2.3 What the flow gets everything else from

A capability. That is the layer allowed to reach the outside world, and the one thing it is not
allowed to reach is the current time. A nightly close that needs the ledger, the tenant list or
a configured threshold takes them from a step, and the trigger contributes the one fact no step
could have: which occurrence this is.

### 2.4 Why this is a rule rather than a silent skip

`ProduceEndpoints` skips a flow with an `[HttpTrigger]` and no `.Return(...)`, quietly, and that
is defensible there: the flow still exists and still runs, it just publishes no route.

Here the silent version is the exact defect binding the transport was meant to end.
`TriggerReader`'s remarks said `Schedule` was *"declaration only: nothing binds them, so a flow
declaring one of those declares an address nothing serves"*. A generator that skipped an
unfireable `[CronTrigger]` would reproduce that for one flow, with the manifest still publishing
the schedule — and with no message anywhere, because the generated file simply would not mention
it.

`FLOWX1038` is raised by `TriggerDeclarationAnalyzer` rather than by the generator, so it points
at the attribute's own span, and it is an **error** for
[FLOWX1033](../diagnostics/FLOWX1033.md)'s reason: there is no release, deployment or
configuration under which a cron firing acquires a body.

---

## 3. Options rejected

- **A. Let a scheduled flow declare any input, and start it with `default`.** *Rejected:* it
  journals an instance whose recorded request is a value nobody sent, which is
  [ADR-0017](ADR-0017-manifest-v1-freeze-criteria.md))'s F2 failure — *"a constant standing in for
  a fact"* — moved from the manifest into the journal, where it is retained for months and read
  by an audit. It also loses the occurrence entirely, so §1.1's late-firing problem has no fix at
  all.
- **B. `Flow<Unit, TOut>`, and let the flow read `ctx.UtcNow`.** *Rejected:* §1.2. The clock is
  journalled, so this is not a determinism violation — it is worse than one, because it is
  *correct on replay and wrong on the first attempt*. The instance records the instant the sweep
  ran, replays it faithfully for ever, and the number was never the one the schedule named. A
  wrong answer that reproduces exactly is the hardest kind to find.
- **C. Put the occurrence on `FlowContext` rather than in the input contract.** *Rejected*, and
  it was the closest call. It would leave the flow free to declare its own input, which
  option **F** below makes attractive. What decided against it is that the context is not
  journalled as such: `flow_instance.input` is the field a replay and an audit read, and a value
  reachable only through the context would have to be copied there by name — a second mechanism
  for one trigger kind, in the layer ADR-0004 keeps transport-free. It also collides with
  `FlowContext.Trigger`, which is declared, is never assigned, and reads `default` on every
  running flow ([09 §2](../09-Trigger-Model.md#2-the-envelope)); adding a second half-populated
  trigger surface beside it would make both harder to fix.
- **D. Generate a per-flow input record, so each scheduled flow has its own contract.**
  *Rejected:* every one of them would have the same three fields, and a generated contract is a
  type the author cannot see in their own source. It would also need a name, and naming a
  generated public contract is a decision with no good answer.
- **E. Make `ScheduledFire` a `readonly record struct`.** *Rejected* on the serialiser: the input
  is journalled through a source-generated `JsonSerializerContext` and a struct is not cheaper
  there, while a class is what every other contract in the repository is.
- **F. Allow a scheduled flow to bind an input the author declares, and populate it by
  convention.** *Rejected:* the convention would have to be reflection or a constructor
  signature the compiler checks, and either is the "generated code inventing a fact"
  `EndpointEmitter` declines to do about service lifetimes. §5's first negative is what this
  option would have bought, and the price is a mapping rule nobody wrote down.

---

## 4. The consequence that contradicts a documented claim

**One flow cannot serve both an HTTP route and a schedule**, and two documents say it can.

[09 §3](../09-Trigger-Model.md#3-declaring-triggers) prints a flow carrying `[HttpTrigger]`,
`[KafkaTrigger]`, `[CronTrigger]` and `[AgentTrigger]` at once and calls it *"four transports,
zero changes to the flow body … quality goal Q4, and it is the single most visible benefit of the
model"*. [ADR-0004](ADR-0004-universal-trigger-model.md))'s first Positive says *"one flow serves
HTTP, Kafka, cron and an AI agent simultaneously"*.

That holds for every transport whose payload **the caller supplies**, and stops at the one whose
payload **the platform supplies**. An HTTP endpoint binds a request body into whatever the flow
declares; a schedule can only give a `ScheduledFire`. A flow cannot declare both.

This was found by a test rather than by reading:
`TriggerDeclarationAnalyzerTests.AllFiveTogetherAreSilent` asserted an empty diagnostic list
against exactly the flow 09 §3 prints, and began reporting `FLOWX1038`. It now asserts
`["FLOWX1038"]` with the reason attached, and [09 §3](../09-Trigger-Model.md#3-declaring-triggers)
carries the correction.

**It is a limit of this decision and not of ADR-0004.** The business operation is still
transport-free — `CloseExpiredOffers` knows nothing about cron — and the schedule still declares
an attribute the flow body cannot observe. What does not compose is two *inbound contracts* on
one flow, and the ordinary shape for that has always been two flows over one capability. The
alternative was option **C** or **F**, and §3 says what each costs.

---

## 5. Consequences

**Positive**

- **`flow_instance.input` is populated for a scheduled flow, on the first row it ever writes.**
  The occurrence, the expression and the zone are in the audit trail, in the replay source and
  in the operator's view of a stuck instance — and the derived primary key is recomputable from
  them by hand.
- **A late firing does the work it was asked to do.** A flow reasoning from `OccurrenceAt`
  produces the same answer at 02:00:03 and at 06:41, which is what makes
  [ADR-0032](ADR-0032-a-missed-schedule-fires-late.md))'s "fire late" defensible at all. Without
  this decision, that one would have had to be "skip".
- **The determinism rules are not weakened to bind a transport.** `FLOWX1007` and `FLOWX1011`
  stand exactly as they were; the occurrence arrives through the one channel a replay already
  reconstructs.
- **The rule is an error at the declaration**, so the failure mode is a build that stops rather
  than a schedule that publishes an address and fires nothing.

**Negative / accepted trade-offs**

- **A flow's input contract is now decided by an attribute**, which is a coupling the model did
  not have. `[HttpTrigger]` constrains nothing about the flow's shape; `[CronTrigger]` constrains
  it completely. **Accepted**, and §4 is what it costs.
- **One flow cannot serve HTTP and cron.** §4. The correction is in
  [09 §3](../09-Trigger-Model.md#3-declaring-triggers) and in ADR-0004's own consequences,
  because both printed the opposite.
- **`ScheduledFire` must be in a source-generated `JsonSerializerContext`**, which is one more
  line an author has to add and one more `FLOWX1006` they can hit. It is the same requirement
  every other durable contract has, and the compiler names the missing type — but it is a
  requirement on a *platform* type, which is new: until now every contract in a context was one
  the application declared.
- **The three fields are a public contract from the day they ship.** Adding one later is
  additive; changing `OccurrenceAt`'s meaning is not, and neither is removing `Cron` or
  `TimeZone` once anyone reads them out of a journal row.

**Revisit when:** any one of —
- **a scheduled flow needs data no occurrence carries and no capability can fetch.** The
  foreseeable case is `PerTenant`: a fan-out gives each firing a tenant, which is neither in the
  occurrence nor discoverable by a capability that has not been told which tenant it is for. At
  that point `ScheduledFire` gains a field or the decision moves to option C;
- **`FlowContext.Trigger` is populated for real.** `TriggerEnvelope` is declared, is never
  assigned and reads `default` on every running flow. When something assigns it, `ScheduledFire`
  overlaps it and one of the two should go — this record chose the input contract on the ground
  that the envelope does not exist, and that ground would be gone;
- **a second trigger kind wants a platform-supplied input.** A change-feed trigger would, and at
  that point there are two rules of this shape and they should be one — the question becomes how
  a trigger *declares* its input contract rather than which contract each one fixes.

---

**Back to:** [ADR index](README.md) · [ADR-0004](ADR-0004-universal-trigger-model.md)) ·
[ADR-0031](ADR-0031-an-occurrence-names-the-instance-it-starts.md)) ·
[ADR-0032](ADR-0032-a-missed-schedule-fires-late.md)) ·
[09 §3](../09-Trigger-Model.md#3-declaring-triggers) ·
[09 §8](../09-Trigger-Model.md#8-schedule-trigger) ·
[FLOWX1038](../diagnostics/FLOWX1038.md)

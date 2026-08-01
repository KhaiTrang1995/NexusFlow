# FLOWX1031 — Suspension construct is declared but not honoured by the compiler

> **Severity:** **Warning** · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** `.Delay(duration)` and `.OnTimeout(block)` on `IFlowBuilder`.
> **Scheduled for deletion:** when the timer half of [WP-63](../20-Roadmap.md#3-increment-detail)
> lands — see [When this rule is deleted](#when-this-rule-is-deleted). This is scaffolding for
> work that has not been done, not a rule about your code.

> [!IMPORTANT]
> **Narrowed on 2026-08-01, from three constructs to two.** This rule covered
> `.AwaitSignal<T>(timeout)` as well, as an **error**, and that half is gone because the
> behaviour it described is gone: a `Durable` flow now **suspends** at its suspension point,
> the instance stays in the journal at its resume frontier, and a delivered signal resumes it
> through the same step loop a recovery scan uses. The author's declared timeout reaches the
> plan. [`FLOWX1017`](FLOWX1017.md) is unchanged and is now a rule whose fix produces a
> working flow.
>
> **`Delay` and `OnTimeout` are unchanged**, because nothing implements a timer. The rule was
> narrowed rather than deleted for the reason [`FLOWX1028`](FLOWX1028.md) was narrowed to
> `Streaming` rather than deleted when `Durable` started running: deleting it would hand a
> discarded `OnTimeout` block the silence `AwaitSignal` used to have.
>
> The account of what `AwaitSignal` used to do is kept
> [below](#what-awaitsignal-used-to-do-and-what-fixed-it), because a page that quietly
> rewrites its own history teaches nobody what it got wrong.

> [!NOTE]
> **This rule is no longer the other half of [FLOWX1017](FLOWX1017.md).** That one refuses
> `AwaitSignal` under every profile except `Durable`, on the grounds that an in-memory wait
> does not survive a deployment. It was silent under `Durable` — which is where the author who
> read it, took its advice and applied its quick action ended up — and this rule was what that
> author then heard. The wait happens under `Durable` now, so there is nothing left to say.

## What it means

Two methods on `IFlowBuilder` express a flow that waits for a **clock**. Both compile.
**Neither does anything**, and until this rule neither said so:

| What the DSL promises | What the compiler does today |
|---|---|
| `.Delay(duration)` — a durable timer holding no resources while it waits | **No step at all.** `FlowAnalyzer`'s switch has no `case "Delay"`, so the call reaches the `default:` arm and is skipped |
| `.OnTimeout(block)` — what happens when the signal never arrives | **The block is discarded.** Same `default:` arm. Its steps are not in the plan, not in the generated dispatcher, not in `Descriptors`, and not in `flowx.manifest.json` |

The `default:` arm is deliberate and correct: it exists so that a generator does not error on
methods a later phase will teach it, which is what lets P1 work land on P0 code. What was
missing is that these are not methods a later phase will teach it — they are methods the DSL
already publishes, [`08 §3.5`](../08-Flow-Definition.md#35-waiting) already documents, and no
diagnostic distinguished from a `Return` the analyzer legitimately skips.

**Why that is worth a warning.** A flow written to escalate after seven days does not
escalate. It runs to completion, journals a clean set of committed rows, returns a success,
and publishes a manifest that mentions neither the delay nor the escalation. Every observable
signal a team has — the build log, the test suite, the trace, the manifest, the journal —
agrees that it worked.

## Example that triggers it

```csharp
[Flow("offer.accept", Profile = ExecutionProfile.Durable, Owner = "people-ops")]
[FlowDeadline("P30D")]
public sealed partial class AcceptOfferFlow : Flow<Offer, Acceptance>
{
    protected override void Define(IFlowBuilder<Offer, Acceptance> flow) => flow
        .Step<ValidateOffer>()
        .AwaitSignal<Countersigned>(TimeSpan.FromDays(7))     // honoured; no diagnostic
            .OnTimeout(f => f.Step<WithdrawOffer>())          // FLOWX1031, warning
        .Delay(TimeSpan.FromDays(1))                          // FLOWX1031, warning
        .Step<SendWelcomePack>()
        .Return(ctx => ctx.Get<Acceptance>());
}
```

The plan that comes out of it:

```csharp
StepGraph.Create(new StepNode[]
{
    StepNode.ForCapability(0, Descriptors.Step0),                            // ValidateOffer
    StepNode.ForAwaitSignal(1, "countersigned", TimeSpan.FromDays(7)),       // the author's seven days
    StepNode.ForCapability(2, Descriptors.Step2),                            // SendWelcomePack
});
```

`WithdrawOffer` is absent. The day-long delay is absent. **The seven days is seven days** —
that is the half this rule used to be an error about, and it is
[below](#what-awaitsignal-used-to-do-and-what-fixed-it).

A flow that declares neither construct is silent, and so is `[FlowDeadline]` — the flow's
absolute budget is the one timeout that does work, and it is not a suspension construct.

## How to fix it

**Not by deleting the wait and shipping the rest.** A flow that needed to escalate and now
does not is not a fixed flow, it is the same defect with the evidence removed. The two
constructs are the only record that this process has a clock in it, and the timer package will
need to find the flows that asked for one.

There is no fix inside FlowX in this release, in the same sense as
[FLOWX1024](FLOWX1024.md) and [FLOWX1028](FLOWX1028.md): the feature the code waits on does
not exist. Your options, in the order they should be considered:

- **Move the clock outside the flow.** This is the honest repair and it is real work, not a
  formality. A scheduled trigger replaces a `Delay`; an `OnTimeout` branch becomes a scheduled
  sweep over instances that have been waiting too long, which — now that a waiting instance is
  a `Suspended` row with a `updated_at` — is a query rather than a guess.
- **Bound the wait with the flow's own deadline instead.** `[FlowDeadline]` is checked at
  every step boundary, including the one that decides whether to suspend, so an instance whose
  budget has gone times out rather than waiting. That is coarser than an `OnTimeout` branch —
  it fails the flow rather than running an alternative — and for many processes it is enough.
- **Keep the declaration and accept that it does nothing**, if and only if you are writing
  against the timer package ahead of time in a branch nothing deploys from. That is a
  suppression, and it has rules — below.

## When to suppress

When the flow is being written ahead of the timer package and will not run in production
before it lands. Nothing else. There is no configuration, no deployment and no profile that
makes a discarded block correct.

Because this is a statement about a platform gap rather than about a line of code,
`.editorconfig` is the right place — one reviewable decision, scoped to the flows that took
it:

```ini
# FLOWX-DEBT(people-ops, 2026-12-31): these flows are written against WP-63's timers and are
#   not deployed. Nothing in this directory ships until it lands.
[src/Flows/Onboarding/**.cs]
dotnet_diagnostic.FLOWX1031.severity = suggestion
```

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry — see
[21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A suppression without
one fails the build. Do not set it project-wide: the next flow that declares a wait is the one
that most needs to be told.

## Why this is a warning and not an error

**These are omissions.** The generated plan contains less than the author wrote, and every
statement in it is true: the steps that are there are the steps that run, the indices are the
indices they run at, and the manifest describes a flow that does exactly what the plan says.
That is precisely the category [FLOWX1027](FLOWX1027.md) occupies — "code that has no effect
rather than code that is wrong, which is exactly what C#'s own `CS0162` is and exactly the
severity C# gives it".

And [FLOWX1028](FLOWX1028.md)'s first argument carries: **an error would erase the declaration
the fixing phase needs to find.** `Profile = Streaming` is a grep away from being P7's list of
flows to make work, and `.Delay(...)` is the same list for the timer package.

<a id="what-happened-to-the-timeout-you-wrote"></a>

## What `AwaitSignal` used to do, and what fixed it

**`FlowEmitter` emitted `TimeSpan.FromHours(1)` for every `AwaitSignal`, whatever the author
declared.** A flow written to wait seven days produced a plan that said one hour. That was a
defect on its own account, independent of whether the construct worked: a value the author
wrote had been replaced by a constant, and nothing anywhere said so.

*This page said the manifest published that constant too. It did not.* `ManifestWriter`
publishes `"kind": "AwaitSignal"` and no duration at all, so the fabrication reached the plan
and the generated source and stopped there. The manifest still publishes no signal identity
and no timeout, which is [owed](#what-is-still-owed) rather than fixed.

There were two honest repairs, and the release that raised this rule took the second:

1. **Carry the declared duration through to the plan.** The right answer: the compiler's step
   model had no field for a signal timeout, so carrying it needed a field, a constructor
   parameter and an emitter argument.
2. **Refuse to emit.** Between publishing a plan containing a duration nobody wrote and
   publishing no plan, the second is the only one that does not put a false fact into the
   generated source a developer reads. So `AwaitSignal`'s report was an error, the generator
   emitted no plan for the flow, and the fabricated constant was gone from `FlowEmitter`
   rather than left dead in it.

**WP-63 took the first, and the refusal stayed.** `StepModel.SignalTimeout` carries the
author's expression verbatim — copied, not folded, so a duration written as
`Waits.Countersignature` reaches the plan as that — and `FlowEmitter` writes it. The
`InvalidOperationException` is still in that arm and still says the same thing: this generator
does not invent a duration. What changed is that nothing reaches it, because
`AwaitSignal<TSignal>(TimeSpan timeout)` has no overload without a timeout. It is asserted by
`SuspensionConstructTests.TheEmitterStillRefusesAnAwaitSignalItHasNoDurationFor`, so that a
change which loses the duration on the way to the emitter fails loudly rather than putting a
constant back.

### And the step now has a meaning

`FlowEngine` has a `case StepKind.AwaitSignal`. A `Durable` flow that reaches one with no
committed row for it and no delivered signal **stops there**: the invocation returns, nothing
is compensated, and the instance is sealed `Suspended` — one row, no thread, no pooled context
and no lease. A later `FlowHost.SignalAsync` re-enters the same `FlowEngine.ExecuteAsync` that
`FlowRecoveryScan` re-enters, steps over every `(scope, step)` that committed, seeds the
signal's payload into the state bag under the contract the flow declared, and runs on.

There is **no signal table**: a delivered signal is journaled as the `AwaitSignal` step's own
row, through the same `CommitAsync` every other step boundary uses, carrying the same
state-bag snapshot. Which is why resumption needed no new journal contract and no migration.

### What is still owed

- **No timer.** The duration reaches the plan and nothing arms it, so a signal that never
  arrives leaves the instance waiting until its `[FlowDeadline]` and something resumes it.
  That is why `OnTimeout` is still reported here.
- **The manifest publishes no signal.** A step's entry carries `"kind": "AwaitSignal"` and
  neither the identity nor the duration. Adding either is a change to
  `schemas/flowx.manifest.schema.json`, whose step object is `additionalProperties: false`, and
  therefore a decision under [ADR-0017](../adr/ADR-0017-manifest-v1-freeze-criteria.md) rather
  than a line of emitter code.
- **No `[HttpTrigger]` shape for a suspended flow.** The generated endpoint answers `200` with
  the flow's projected output, and a suspended flow has none — `202 Accepted` with the
  instance id is the answer, and `plugins/FlowX.Http` has no path for it.
  `samples/workflow/Program.cs` maps that pair by hand.
- **An inline composed child may not suspend.** The parent records a composition as one row
  written when the child finishes, so a parent resumed past a waiting child would compose a
  *second* child instance and repeat its effects. It is refused at run time as
  `flow.suspension_inside_composition`; a `Detached` child has its own instance and is allowed
  to wait.

## When this rule is deleted

**This diagnostic is deleted, not fixed.** It describes a gap in the platform, and a rule that
outlives what it describes is noise — and noise is what teaches people to suppress a
catalogue.

| Event | Action | Status |
|---|---|---|
| Durable suspension: a suspended instance, resumption on signal, the author's duration in the plan | Narrow this rule off `AwaitSignal`; restore `FlowEmitter`'s `AwaitSignal` case with the **author's** declared duration, carried on the step model | **done** — 2026-08-01. `FlowEmitter`'s refusal is kept for the case that can no longer arise, and pinned by a test |
| Timers: a timer table, a scheduler, a `Delay` that waits and an `OnTimeout` branch that runs | Delete `FLOWX1031`, its analysis, this page and the release-tracking row | Outstanding |

`tests/Workflow.Tests/SuspensionTests` is the executable half of the first row: it runs
`offer.accept` — a flow of the reference sample's own, with a real wait in the middle — through
the real host and the real journal, and goes red the day a suspension point stops suspending.

---

**Back to:** [diagnostics index](README.md) · [FLOWX1017](FLOWX1017.md) ·
[FLOWX1028](FLOWX1028.md) ·
[Flow definition §3.5](../08-Flow-Definition.md#35-waiting) ·
[Execution engine §6](../06-Execution-Engine.md) · [Roadmap](../20-Roadmap.md)

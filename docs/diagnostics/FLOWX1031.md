# FLOWX1031 — Suspension construct is declared but not honoured by the compiler

> **Severity:** **Error** for `AwaitSignal`, **Warning** for `Delay` and `OnTimeout` ·
> **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** `.AwaitSignal<TSignal>(timeout)`, `.Delay(duration)` and `.OnTimeout(block)`
> on `IFlowBuilder`.
> **Scheduled for deletion:** when [WP-63](../20-Roadmap.md#3-increment-detail) lands durable
> suspension and timers — see [When this rule is deleted](#when-this-rule-is-deleted). This is
> scaffolding for a work package that has not started, not a rule about your code.

> [!NOTE]
> **This rule is the other half of [FLOWX1017](FLOWX1017.md).** That one refuses
> `AwaitSignal` under every profile except `Durable`, on the grounds that an in-memory wait
> does not survive a deployment. It has always been silent under `Durable` — which is
> where the author who read it, took its advice and applied its quick action ends up. This
> rule is what that author now hears: the wait does not happen under `Durable` either,
> because nothing implements it yet.

## What it means

Three methods on `IFlowBuilder` express a flow that waits — for a person, for an external
system, for a clock. All three compile. **None of them does anything**, and until this rule
none of them said so:

| What the DSL promises | What the compiler does today |
|---|---|
| `.AwaitSignal<T>(timeout)` — suspends until the signal arrives, holding no thread, no memory and no lease | Emits a step the dispatcher answers `StepOutcome.Success` for. `FlowEngine` has no case for `StepKind.AwaitSignal`, so it falls through to the ordinary capability path and returns. **The step after it runs in the same millisecond.** The declared `timeout` is discarded — see [below](#what-happened-to-the-timeout-you-wrote) |
| `.Delay(duration)` — a durable timer holding no resources while it waits | **No step at all.** `FlowAnalyzer`'s switch has no `case "Delay"`, so the call reaches the `default:` arm and is skipped |
| `.OnTimeout(block)` — what happens when the signal never arrives | **The block is discarded.** Same `default:` arm. Its steps are not in the plan, not in the generated dispatcher, not in `Descriptors`, and not in `flowx.manifest.json` |

The `default:` arm is deliberate and correct: it exists so that a generator does not error on
methods a later phase will teach it, which is what lets P1 work land on P0 code. What was
missing is that these three are not methods a later phase will teach it — they are methods
the DSL already publishes, [`08 §3.5`](../08-Flow-Definition.md#35-waiting) already documents, and no
diagnostic distinguished from a `Return` the analyzer legitimately skips.

**Why that is worth stopping a build.** A flow written to pause for a countersignature does
not pause. It runs to completion, journals a clean set of committed rows, returns a success,
and publishes a manifest naming a signal it never waits for. Every observable signal a team
has — the build log, the test suite, the trace, the manifest, the journal — agrees that it
worked. `samples/workflow/README.md §2` measures exactly that, and
`TheAbsentHalfTests.AnAwaitSignalStepDoesNotWaitForAnything` runs the shape on the real
engine against a real journal: three step boundaries, three committed rows, the instance
`Completed`, and the clock never moves.

## Example that triggers it

```csharp
[Flow("offer.accept", Profile = ExecutionProfile.Durable, Owner = "people-ops")]
[FlowDeadline("P30D")]
public sealed partial class AcceptOfferFlow : Flow<Offer, Acceptance>
{
    protected override void Define(IFlowBuilder<Offer, Acceptance> flow) => flow
        .Step<ValidateOffer>()
        .AwaitSignal<Countersigned>(TimeSpan.FromDays(7))     // FLOWX1031, error
            .OnTimeout(f => f.Step<WithdrawOffer>())          // FLOWX1031, warning
        .Delay(TimeSpan.FromDays(1))                          // FLOWX1031, warning
        .Step<SendWelcomePack>()
        .Return(ctx => ctx.Get<Acceptance>());
}
```

Before this rule that compiled with **0 errors and 0 warnings** and produced:

```csharp
StepGraph.Create(new StepNode[]
{
    StepNode.ForCapability(0, Descriptors.Step0),                       // ValidateOffer
    StepNode.ForAwaitSignal(1, "event.countersigned", TimeSpan.FromHours(1)),
    StepNode.ForCapability(2, Descriptors.Step2),                       // SendWelcomePack
});
```

`WithdrawOffer` is absent. The day-long delay is absent. The seven days became one hour.
The flow sends the welcome pack immediately and reports success.

A flow that declares none of the three is silent, and so is `[FlowDeadline]` — the flow's
absolute budget is the one timeout that does work, and it is not a suspension point.

## How to fix it

**Not by deleting the wait and shipping the rest.** A flow that needed to wait for a human
and now does not is not a fixed flow, it is the same defect with the evidence removed. The
three constructs are the only record that this process has a pause in it, and WP-63 will
need to find the flows that asked for one.

There is no fix inside FlowX in this release, in the same sense as
[FLOWX1024](FLOWX1024.md) and [FLOWX1028](FLOWX1028.md): the feature the code waits on does
not exist. Your options, in the order they should be considered:

- **Move the wait outside the flow.** This is the honest repair and it is real work, not a
  formality. Split the flow at the pause: the part before the wait is one flow, the part
  after it is a second flow triggered by the arriving signal, and the correlation between
  them is an identifier your own store owns. `[HttpTrigger]` or an event trigger starts the
  second half; a scheduled trigger replaces a `Delay`; the `OnTimeout` branch becomes a
  scheduled sweep over instances that have been waiting too long. You lose the single
  readable file, and you keep the behaviour.
- **Do not ship this flow on this release.** If the process genuinely cannot be split — the
  wait is inside a compensable region, or the state between the halves is too large to
  correlate through a store — then the flow cannot be expressed correctly here yet. Wait for
  [WP-63](../20-Roadmap.md#3-increment-detail).
- **Keep the declaration and accept that it does nothing**, if and only if you are writing
  against WP-63 ahead of time in a branch nothing deploys from. That is a suppression, and
  it has rules — below.

## When to suppress

**For `Delay` and `OnTimeout`:** when the flow is being written ahead of WP-63 and will not
run in production before it lands. Nothing else. There is no configuration, no deployment
and no profile that makes a discarded block correct.

Because this is a statement about a platform gap rather than about a line of code,
`.editorconfig` is the right place — one reviewable decision, scoped to the flows that took
it:

```ini
# FLOWX-DEBT(people-ops, 2026-12-31): these flows are written against WP-63's durable
#   suspension and are not deployed. Nothing in this directory ships until it lands.
[src/Flows/Onboarding/**.cs]
dotnet_diagnostic.FLOWX1031.severity = suggestion
```

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry — see
[21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A suppression without
one fails the build. Do not set it project-wide: the next flow that declares a wait is the
one that most needs to be told.

**For `AwaitSignal`: none, and downgrading it does not do what you want.** The error is not
only a message, it is the compiler declining to emit a plan for the flow — see
[the timeout](#what-happened-to-the-timeout-you-wrote). Setting
`dotnet_diagnostic.FLOWX1031.severity = none` hides the explanation and does not bring the
plan back; you get a flow with no generated plan and no diagnostic saying why, which is a
worse position than the one you started in. If you need the declaration to survive in a
branch, keep it and let the build stay red.

## Why `AwaitSignal` is an error and the other two are warnings

The split is not a compromise between the two answers. It is the line between a construct
the compiler **omits** and a construct the compiler **falsifies**, and it is the only line
in this rule that can be drawn from the source rather than from taste.

**`Delay` and `OnTimeout` are omissions.** The generated plan contains less than the author
wrote, and every statement in it is true: the steps that are there are the steps that run,
the indices are the indices they run at, and the manifest describes a flow that does exactly
what the plan says. That is precisely the category [FLOWX1027](FLOWX1027.md) occupies —
"code that has no effect rather than code that is wrong, which is exactly what C#'s own
`CS0162` is and exactly the severity C# gives it". A warning is what this catalogue already
gives that category, and giving it something else here would make the catalogue inconsistent
with itself.

**`AwaitSignal` is a falsification.** It reaches the plan, it reaches the dispatcher, it
reaches the manifest, and it reaches them carrying `TimeSpan.FromHours(1)` — a value the
author did not write, in place of one they did. The plan is not a smaller truth than the
source; it is a different claim. And the step it produces is not inert: the engine executes
it, succeeds, journals a row, and moves on. Nothing in this catalogue is a warning about a
plan that states something the source does not.

**Both halves point at the same remedy and the same work package.** They are one id because
they are one fact — *this release cannot honour a flow that waits* — and because two ids
would give a team two suppressions, two pages and two expiry dates for one gap. Choosing the
severity per report rather than per rule is what [FLOWX1011](FLOWX1011.md) and
[FLOWX1025](FLOWX1025.md) already do, and for the same reason both of them give: severity
follows what the compilation can prove about the consequence.

## Why the whole rule is not a warning, which is the obvious reading of `FLOWX1028`

[FLOWX1028](FLOWX1028.md) is the closest precedent in the catalogue and the title of this
page is deliberately its title with one word changed. It is a **warning**, on two arguments,
and it is worth saying exactly where each of them stops.

**"An error would erase the declaration, and the phase that fixes it would find no
inventory."** This is the argument that carries, and it is why `Delay` and `OnTimeout` are
warnings rather than errors even though they do nothing at all. `Profile = Streaming` is a
grep away from being P7's list of flows to make work, and `.Delay(...)` is the same list for
WP-63.

It does not carry for `AwaitSignal`, because the inventory it protects only exists if the
code survives — and an `AwaitSignal` flow must not. Which brings us to the argument that
does not transfer at all:

**"The source is not wrong; the flow is written correctly for a platform that has the
feature."** For `Streaming` this is true and is the whole case: a `Streaming` flow runs, and
runs *correctly*, for every flow whose invocations are genuinely independent. FLOWX1028's
first remedy is "confirm the flow is correct as a per-message execution, and record that",
and for a large class of flows that confirmation is honest.

**There is no corresponding confirmation here.** No flow is correct with a seven-day wait
compiled to no wait. There is no deployment, no configuration and no reviewer's signature
that makes "it did not wait" acceptable in a program that asked to wait — if the wait was
never needed, the construct should not be there. Every warning in this catalogue is a
warning because some legitimate program exists in which the reported code is correct:
`FLOWX1012`'s ephemeral saga compensates on every ordinary failure, `FLOWX1024`'s and
`FLOWX1025`'s findings are gaps between the manifest and the build rather than mistakes in
the source, `FLOWX1027` reports code that is merely unreachable. `AwaitSignal` has no such
program, and the index's opening sentence is the rule for that case: *if a rule is worth
having, it stops the build.*

### The objection this creates, stated rather than avoided

Making this an error leaves `AwaitSignal` **with no profile it can legally declare**:
`FLOWX1017` is an error below `Durable`, and this rule is an error at it. It also means
`AwaitSignalRequiresDurableCodeFixProvider` — the quick action that writes
`Profile = ExecutionProfile.Durable` — is a quick action whose result is a different
diagnostic.

That objection is not new. It is written down on
[FLOWX1028's page](FLOWX1028.md#why-this-is-a-warning-and-not-an-error) as a third argument
for *that* rule's severity, retired when WP-52 narrowed it to `Streaming`, and
`ExecutionProfileAnalyzerTests` states the principle plainly: *"a fix whose result is a
different diagnostic is a fix that is broken."*

**The answer is that the quick action's premise is false, and this rule is what makes that
visible.** Its own remarks say it does not guess because "the author wrote `AwaitSignal`, so
the flow suspends". The flow does not suspend. `Durable` was never the missing half of a
working suspension; it was the profile under which the same nothing happened without a
message. Weakening a rule so that a quick action's output looks clean would restore the
silence the quick action was walking the author into. `AwaitSignal` having no legal profile
is an accurate description of the platform — there is no suspension engine — and a rule that
left it one profile to declare would not be preserving an option, it would be preserving the
belief that an option exists.

`FLOWX1017` is unchanged and is not redundant. It asks *which profile*, and its answer will
be right the moment WP-63 lands; this rule asks *whether the construct works at all*, and
its answer is the same under every profile, which is why it does not read the profile.

**Info was not a candidate**, for the reason
[ADR-0003](../adr/ADR-0003-execution-profiles.md) has already been overruled on twice: an
`Info` diagnostic never appears in a build log, so it would ship a rule that does nothing —
which is a precise description of the state this rule was written to end.

## What happened to the timeout you wrote

**`FlowEmitter` emitted `TimeSpan.FromHours(1)` for every `AwaitSignal`, whatever the author
declared.** A flow written to wait seven days produced a plan that said one hour, and the
manifest published from that plan said one hour too. That is a defect on its own account,
independent of whether the construct works: a value the author wrote had been replaced by a
constant, and nothing anywhere said so.

There are two honest repairs, and this release takes the second:

1. **Carry the declared duration through to the plan.** The right answer, and it is WP-63's:
   the compiler's step model has no field for a signal timeout, so carrying it needs a field,
   a constructor parameter, a manifest column and an emitter argument — the same edit WP-63
   makes when it gives the step a meaning.
2. **Refuse to emit.** Between publishing a plan containing a duration nobody wrote and
   publishing no plan, the second is the only one that does not put a false fact into
   `flowx.manifest.json` and into the generated source a developer reads. So `AwaitSignal`'s
   report is an error, the generator emits no plan for the flow, and the fabricated constant
   is gone from `FlowEmitter` rather than left dead in it.

This is why the severity split above lands where it does. A warning on `AwaitSignal` would
have been a rule that reports the falsification in the build log and then commits it in the
generated source.

`Delay` is not affected: it fabricated nothing, because it produced nothing.

## When this rule is deleted

**This diagnostic is deleted, not fixed.** It describes a gap in the platform, and a rule
that outlives what it describes is noise — and noise is what teaches people to suppress a
catalogue.

| Event | Action | Status |
|---|---|---|
| [WP-63](../20-Roadmap.md#3-increment-detail) lands durable suspension and timers: a suspended instance, a timer table, resumption on signal | Delete `FLOWX1031`, its analysis, this page and the release-tracking row. Restore `FlowEmitter`'s `AwaitSignal` case with the **author's** declared duration, carried on the step model | Outstanding |

`TheAbsentHalfTests.AnAwaitSignalStepDoesNotWaitForAnything` in `tests/Workflow.Tests` is the
executable half of this reminder: it asserts the degenerate behaviour on the real engine and
goes red on the day a suspension point actually suspends.

---

**Back to:** [diagnostics index](README.md) · [FLOWX1017](FLOWX1017.md) ·
[FLOWX1028](FLOWX1028.md) ·
[Flow definition §3.5](../08-Flow-Definition.md#35-waiting) ·
[Execution engine §6](../06-Execution-Engine.md) · [Roadmap](../20-Roadmap.md)

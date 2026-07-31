# FLOWX1028 — Execution profile is declared but not honoured by the runtime

> **Severity:** Warning · **Category:** FlowX · **Since:** 0.1.0
> **Scheduled for deletion:** when `FlowX.Runtime` implements the profile — see
> [When this rule is deleted](#when-this-rule-is-deleted). This is scaffolding for a
> phase that has not happened, not a rule about your code.

## What it means

The flow declares `Profile = ExecutionProfile.Durable` (or `Streaming`), and
**`FlowX.Runtime` does not read `ExecutionProfile` anywhere.** The word does not
appear in the runtime assembly, in `FlowX.Hosting`, or in any transport plugin:

```
$ grep -rn "Profile" src/FlowX.Runtime src/FlowX.Hosting plugins/
$
```

So the flow runs on the ephemeral engine. Concretely, and in the terms
[06 §4](../06-Execution-Engine.md#4-execution-profiles--the-central-trade-off)
uses to sell the middle column:

| The `Durable` column promises | What actually happens today |
|---|---|
| journal, per step | nothing is written |
| resumes on another node after a crash | the instance is lost |
| suspend and resume on timers and signals | the wait is an in-memory wait |
| determinism required, because it replays | nothing replays |

What the declaration *does* reach is a validation in `ExecutionPlan` and the
`profile` field of `flowx.manifest.json`. That is the whole of it: a fact in a
published contract, and no behaviour.

**Why that is worth stopping a build.** An author who sets `Durable` on a payment
saga has made a deliberate, reviewed, expensive-looking decision, and believes
their flow survives a deploy. It does not. The failure is silent at compile time,
silent at run time, and discovered at the only moment it matters — when a node
goes away mid-flow and the money is somewhere in the middle. This diagnostic
exists so that no one can declare durability and be told nothing.

It also closes the gap the manifest opens. `flowx.manifest.json` publishes
`"profile": "Durable"` as a declared fact and consumers read it; this warning is
what ensures the person who put that fact there knew what it currently buys.

## Example that triggers it

```csharp
[Flow("payment.settle", Profile = ExecutionProfile.Durable, Owner = "payments")]
//                      ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ FLOWX1028
[FlowDeadline("P7D")]
public sealed partial class SettlePaymentFlow : Flow<SettlePayment, SettlementResult>
{
    protected override void Define(IFlowBuilder<SettlePayment, SettlementResult> flow) => flow
        .Step<ReserveFunds>().CompensateWith<ReleaseFunds>()
        .AwaitSignal<ClearingConfirmed>(TimeSpan.FromDays(2))
        .Step<PostLedgerEntry>()
        .Return(ctx => new SettlementResult("settled"));
}
```

The build succeeds today, the manifest says `"profile": "Durable"`, and a
`SIGTERM` during the two-day wait loses the flow.

A flow that declares nothing, or declares `Ephemeral`, is silent — that is the
profile the runtime implements.

## How to fix it

**Not by changing the profile.** `Profile = Ephemeral` would silence this warning
and change nothing about how the flow runs; it would only delete the record of
what this flow needs. [ADR-0003](../adr/ADR-0003-execution-profiles.md) calls the
profile the single most consequential decision a flow author makes and lists its
greppability as a positive consequence of the design — `Profile = Durable` visible
in the code, the manifest and the diagram is how P2 will find the flows it has to
make work. Erasing it to buy back a build is the one repair this page argues
against.

There is no fix in this release, in the same sense as
[FLOWX1024](FLOWX1024.md): the feature it waits on does not exist. Your options:

- **Confirm the flow is survivable without a journal, and record that.** This is
  the honest default and it is real work, not a formality. Losing an in-flight
  instance on a deploy has to be acceptable for *this* flow: the steps idempotent,
  the caller able to retry the whole operation, no state stranded between two
  systems. If that holds, keep `Durable` as the declaration of intent and
  downgrade this rule (below).
- **Do not ship this flow on this release**, if it is not survivable. A payment
  saga that must not double-charge across a deploy does not become safe because
  the compiler was persuaded to stop mentioning it. Wait for
  [P2](../20-Roadmap.md#3-increment-detail).
- **Move the guarantee outside FlowX** in the meantime — an idempotency key in
  your own store, a reconciliation job, an outbox owned by the capability —
  accepting that the flow's *orchestration* is still not crash-safe.

## When to suppress

When the flow's steps are idempotent and its caller retries, so losing an
in-flight instance costs a retry rather than correctness, and the `Durable`
declaration is there to state intent for P2.

Because this is a statement about a whole flow, and about a platform gap rather
than about a line of code, `.editorconfig` is the right place — one reviewable
decision, scoped to the flows that took it:

```ini
# FLOWX-DEBT(payments, 2026-12-31): these flows declare Durable for P2 and run
#   ephemerally today. Steps are idempotent and the caller retries, so a lost
#   instance costs a retry. Remove this when the journal ships.
[src/Flows/Settlement/**.cs]
dotnet_diagnostic.FLOWX1028.severity = suggestion
```

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

Do not set it project-wide. The next flow that declares `Durable` is the one that
most needs to be told.

## Why this is a warning and not an error

An error is the obvious reading of "make the gap loud", and it is wrong here for
three reasons that compound.

**It would erase the declaration.** The only edit that clears an error is
`Profile = Ephemeral`. Every flow that has declared `Durable` in good faith would
be rewritten to say it does not need durability — and P2 would land to find no
flow anywhere asking for the feature it just built. The diagnostic would have
destroyed the only inventory of its own remedy.

**It would deadlock a suspending flow.** [FLOWX1017](FLOWX1017.md) is an *error*
on a flow that uses `AwaitSignal` without `Durable`. If FLOWX1028 were also an
error, such a flow would have no profile it could legally declare: `Ephemeral`
fails 1017, `Durable` fails 1028. The rules would jointly make a documented,
supported construct unbuildable. Worse, `AwaitSignalRequiresDurableCodeFixProvider`
exists specifically to write `Profile = ExecutionProfile.Durable` — a quick action
whose result is a different error is a quick action that is broken.

**The source is not wrong.** This is the same category as
[FLOWX1024](FLOWX1024.md) and [FLOWX1025](FLOWX1025.md): a gap between what the
manifest promises and what the build can deliver, not a mistake in the code. The
flow is written correctly for a platform that has the feature.

Info was the other candidate, and it is the option
[ADR-0003](../adr/ADR-0003-execution-profiles.md) already rejected once — for
`FLOWX1011`, on the grounds that an Info diagnostic never reaches a build log and
would ship doing nothing. Shipping a rule that does nothing is the precise defect
this diagnostic was written to correct; repeating it here would be an unusually
direct self-contradiction.

Warning is the setting that is loud where it must be and negotiable where it must
be. FlowX's own build sets `TreatWarningsAsErrors`, so this stops the build
**here** and no sample, test or reference application can quietly declare
`Durable`. A consumer who has read this page and accepted the gap downgrades it in
`.editorconfig`, which puts the decision in the repository that made it, with an
owner and an expiry attached.

## Why the manifest still says `"profile": "Durable"`

A fair question, and the answer is deliberate: **the manifest records what was
declared, and `Durable` was declared.**

[ADR-0005](../adr/ADR-0005-manifest-as-build-artifact.md) makes the manifest a
document containing only declared facts — the same principle that stops
[FLOWX1025](FLOWX1025.md) inventing a trigger kind it cannot read. The `profile`
field is a faithful record of the attribute. What is untrue is not the field but
an inference a reader might draw from it, and the three plausible edits all make
things worse:

- **Emitting `"Ephemeral"` when `Durable` was declared** would put a fact in the
  manifest that no one wrote, and `flowx diff` would then report a breaking
  profile change on the day P2 lands and the field flips back — a change that
  never happened in any source file.
- **Omitting the field** loses the declaration entirely. That is exactly the
  failure [FLOWX1025](FLOWX1025.md) documents: `flowx diff` cannot tell an absence
  from a removal, so the gate silently loses its input.
- **Adding a `profileHonoured: false` field** encodes a repository-wide,
  temporary truth — *no* profile is honoured — as a per-flow one. It is a schema
  change and a `flowx diff` change, and it would have to be unwound in P2, for a
  statement that belongs in release notes.

So the manifest is unchanged, and the honesty is placed where the decision is
made rather than where it is published. This diagnostic is what guarantees that
the person who put `"profile": "Durable"` into the manifest was told what it
currently buys.

## When this rule is deleted

**This diagnostic is deleted, not fixed.** It describes a gap in the platform, and
when the gap closes the rule must go — a rule that outlives what it describes is
noise, and noise is what teaches people to suppress a catalogue.

| Event | Action |
|---|---|
| [P2](../20-Roadmap.md#3-increment-detail) lands the journal, leases and resumption, and the engine reads `ExecutionProfile` | Narrow the rule to `Streaming`, and land `FLOWX1007`–`FLOWX1009` with it, as [05 §11](../05-Architecture.md#11-risks-and-technical-debt) requires for risk R2 |
| [P7](../20-Roadmap.md#3-increment-detail) lands the stream engine | Delete `FLOWX1028`, `ExecutionProfileAnalyzer`, this page and the release-tracking row |

The reminder is executable rather than a comment. `RuntimeDoesNotReadTheExecutionProfile`
in `FlowX.Architecture.Tests` asserts that `ExecutionProfile` appears nowhere in
`src/FlowX.Runtime`, `src/FlowX.Hosting` or `plugins/`. The day someone makes the
runtime read the profile, that test fails and its message sends them here. A rule
whose removal depends on somebody remembering is a rule that never gets removed.

---

**Back to:** [diagnostics index](README.md) ·
[Execution engine §4](../06-Execution-Engine.md#4-execution-profiles--the-central-trade-off) ·
[ADR-0003](../adr/ADR-0003-execution-profiles.md) · [Roadmap](../20-Roadmap.md)

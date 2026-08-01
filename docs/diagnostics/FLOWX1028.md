# FLOWX1028 — Execution profile is declared but not honoured by the runtime

> **Severity:** Warning · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** `Profile = ExecutionProfile.Streaming` only.
> **Scheduled for deletion:** when P7 lands the stream engine — see
> [When this rule is deleted](#when-this-rule-is-deleted). This is scaffolding for a
> phase that has not happened, not a rule about your code.

> [!NOTE]
> **This rule used to cover `Durable`, and no longer does.** WP-52 made `FlowX.Runtime`
> read `ExecutionProfile`: a durable flow journals its step boundaries on
> `(instance, scope, step, attempt)` under a fencing token, resumption re-enters the
> same step loop from a cursor derived by replaying that journal, and a flow declaring
> `Durable` with no journal configured is **refused** rather than run ephemerally. The
> declaration is honoured, so warning about it would now be false. The rule was narrowed
> rather than deleted, because deleting it would have handed `Streaming` exactly the
> silence `Durable` was rescued from — the reasoning is
> [ADR-0015's](../adr/ADR-0015-journal-schema-and-durable-execution.md)#what-lands-with-this-and-what-is-deleted).

## What it means

The flow declares `Profile = ExecutionProfile.Streaming`, and **there is no stream
engine.** The flow runs on the ephemeral one. Concretely, and in the terms
[06 §4](../06-Execution-Engine.md#4-execution-profiles--the-central-trade-off)
uses to sell the third column:

| The `Streaming` column promises | What actually happens today |
|---|---|
| checkpointed offsets | nothing is checkpointed |
| windowing and watermarks | there is no window; each invocation is one flow |
| backpressure | the caller's own concurrency is the only limit |
| continuous ingestion | the flow runs once per trigger, like any other |

What the declaration *does* reach is a validation in `ExecutionPlan` and the
`profile` field of `flowx.manifest.json`. That is the whole of it: a fact in a
published contract, and no behaviour.

**Why that is worth stopping a build.** An author who sets `Streaming` on an
ingestion pipeline has made a deliberate, reviewed, expensive-looking decision,
and believes their flow checkpoints its progress. It does not. The failure is
silent at compile time, silent at run time, and discovered when a replica restarts
and the offsets it never wrote turn out not to exist. This diagnostic exists so
that no one can declare a profile and be told nothing.

It also closes the gap the manifest opens. `flowx.manifest.json` publishes
`"profile": "Streaming"` as a declared fact and consumers read it; this warning is
what ensures the person who put that fact there knew what it currently buys.

## Example that triggers it

```csharp
[Flow("telemetry.enrich", Profile = ExecutionProfile.Streaming, Owner = "platform")]
//                        ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ FLOWX1028
public sealed partial class EnrichTelemetryFlow : Flow<Reading, EnrichedReading>
{
    protected override void Define(IFlowBuilder<Reading, EnrichedReading> flow) => flow
        .Step<ResolveDevice>()
        .Step<AttachSiteMetadata>()
        .Return(ctx => ctx.Get<EnrichedReading>());
}
```

The build succeeds today, the manifest says `"profile": "Streaming"`, and the flow
is an ordinary request/response execution with no continuity between invocations.

A flow that declares nothing, `Ephemeral`, or `Durable` is silent — those are the
profiles the runtime implements.

## How to fix it

**Not by changing the profile.** `Profile = Ephemeral` would silence this warning
and change nothing about how the flow runs; it would only delete the record of
what this flow needs. [ADR-0003](../adr/ADR-0003-execution-profiles.md)) calls the
profile the single most consequential decision a flow author makes and lists its
greppability as a positive consequence of the design — `Profile = Streaming`
visible in the code, the manifest and the diagram is how P7 will find the flows it
has to make work. Erasing it to buy back a build is the one repair this page argues
against.

There is no fix in this release, in the same sense as
[FLOWX1024](FLOWX1024.md): the feature it waits on does not exist. Your options:

- **Confirm the flow is correct as a per-message execution, and record that.** This
  is the honest default and it is real work, not a formality. The flow has to be
  correct with no window, no watermark and no offset of its own: each invocation
  independent, ordering enforced by the broker or not needed, and duplicate
  delivery handled by the capabilities. If that holds, keep `Streaming` as the
  declaration of intent and downgrade this rule (below).
- **Do not ship this flow on this release**, if it is not correct that way. An
  aggregation that must not double-count across a restart does not become safe
  because the compiler was persuaded to stop mentioning it. Wait for
  [P7](../20-Roadmap.md#3-increment-detail).
- **Move the continuity outside FlowX** in the meantime — offsets committed by the
  consumer, windowing in the broker or in a store the capability owns — accepting
  that the flow's *orchestration* still has no notion of a stream.

## When to suppress

When the flow is correct as an independent per-message execution, and the
`Streaming` declaration is there to state intent for P7.

Because this is a statement about a whole flow, and about a platform gap rather
than about a line of code, `.editorconfig` is the right place — one reviewable
decision, scoped to the flows that took it:

```ini
# FLOWX-DEBT(platform, 2026-12-31): these flows declare Streaming for P7 and run
#   per message today. Each invocation is independent and the broker enforces
#   ordering, so there is nothing to checkpoint. Remove when the stream engine ships.
[src/Flows/Telemetry/**.cs]
dotnet_diagnostic.FLOWX1028.severity = suggestion
```

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

Do not set it project-wide. The next flow that declares `Streaming` is the one that
most needs to be told.

## Why this is a warning and not an error

An error is the obvious reading of "make the gap loud", and it is wrong here.

**It would erase the declaration.** The only edit that clears an error is a
different profile. Every flow that has declared `Streaming` in good faith would be
rewritten to say it does not need a stream engine — and P7 would land to find no
flow anywhere asking for the feature it just built. The diagnostic would have
destroyed the only inventory of its own remedy.

**The source is not wrong.** This is the same category as
[FLOWX1024](FLOWX1024.md) and [FLOWX1025](FLOWX1025.md): a gap between what the
manifest promises and what the build can deliver, not a mistake in the code. The
flow is written correctly for a platform that has the feature.

Info was the other candidate, and it is the option
[ADR-0003](../adr/ADR-0003-execution-profiles.md)) already rejected once — for
`FLOWX1011`, on the grounds that an Info diagnostic never reaches a build log and
would ship doing nothing. Shipping a rule that does nothing is the precise defect
this diagnostic was written to correct; repeating it here would be an unusually
direct self-contradiction.

Warning is the setting that is loud where it must be and negotiable where it must
be. FlowX's own build sets `TreatWarningsAsErrors`, so this stops the build
**here** and no sample, test or reference application can quietly declare
`Streaming`. A consumer who has read this page and accepted the gap downgrades it
in `.editorconfig`, which puts the decision in the repository that made it, with an
owner and an expiry attached.

> **A third argument used to live here and has been retired.** While this rule
> covered `Durable`, making it an error would have deadlocked with
> [FLOWX1017](FLOWX1017.md) — an *error* on a flow that uses `AwaitSignal` without
> `Durable` — leaving such a flow with no profile it could legally declare, and
> making `AwaitSignalRequiresDurableCodeFixProvider` a quick action whose result was
> a different error. Narrowing to `Streaming` retired that conflict outright rather
> than balancing it. The two reasons above stand on their own, which is why the
> severity did not change when the scope did.

## Why the manifest still says `"profile": "Streaming"`

A fair question, and the answer is deliberate: **the manifest records what was
declared, and `Streaming` was declared.**

[ADR-0005](../adr/ADR-0005-manifest-as-build-artifact.md)) makes the manifest a
document containing only declared facts — the same principle that stops
[FLOWX1025](FLOWX1025.md) inventing a trigger kind it cannot read. The `profile`
field is a faithful record of the attribute. What is untrue is not the field but
an inference a reader might draw from it, and the three plausible edits all make
things worse:

- **Emitting `"Ephemeral"` when `Streaming` was declared** would put a fact in the
  manifest that no one wrote, and `flowx diff` would then report a breaking
  profile change on the day P7 lands and the field flips back — a change that
  never happened in any source file.
- **Omitting the field** loses the declaration entirely. That is exactly the
  failure [FLOWX1025](FLOWX1025.md) documents: `flowx diff` cannot tell an absence
  from a removal, so the gate silently loses its input.
- **Adding a `profileHonoured: false` field** encodes a temporary truth as a
  per-flow one. It is a schema change and a `flowx diff` change, and it would have
  to be unwound in P7, for a statement that belongs in release notes.

So the manifest is unchanged, and the honesty is placed where the decision is
made rather than where it is published. **That is also what let P2 land quietly**:
`"profile": "Durable"` was published before the runtime honoured it and is
published unchanged now, because the field was always the declaration faithfully
recorded — what was untrue was the silence around it, not the field.

## When this rule is deleted

**This diagnostic is deleted, not fixed.** It describes a gap in the platform, and
when the gap closes the rule must go — a rule that outlives what it describes is
noise, and noise is what teaches people to suppress a catalogue.

| Event | Action | Status |
|---|---|---|
| [P2](../20-Roadmap.md#3-increment-detail) lands the journal, leases and resumption, and the engine reads `ExecutionProfile` | Narrow the rule to `Streaming` | **Done — WP-52.** The `Durable` half of the analyzer, the descriptor, the tests, the `AnalyzerReleases` row and this page went with it, and `ExecutionProfileHonestyTests` was deleted rather than skipped |
| [P7](../20-Roadmap.md#3-increment-detail) lands the stream engine | Delete `FLOWX1028`, `ExecutionProfileAnalyzer`, this page and the release-tracking row | Outstanding |

**The remaining row has no executable reminder, and that is worth saying plainly.**
The `Durable` half had one: `RuntimeDoesNotReadTheExecutionProfile` asserted that
`ExecutionProfile` appeared nowhere in `src/FlowX.Runtime`, `src/FlowX.Hosting` or
`plugins/`, and it went red on the day WP-52 made that false — which is what sent
whoever ran it here. No equivalent scaffold exists for `Streaming`, because "there
is no stream engine" is not a claim any file makes that a test could catch
changing; the profile is now read, and its being read is precisely what the old
reminder watched for. This table is the reminder instead, and P7's work package
names it.

---

**Back to:** [diagnostics index](README.md) ·
[Execution engine §4](../06-Execution-Engine.md#4-execution-profiles--the-central-trade-off) ·
[ADR-0003](../adr/ADR-0003-execution-profiles.md)) ·
[ADR-0015](../adr/ADR-0015-journal-schema-and-durable-execution.md)) · [Roadmap](../20-Roadmap.md)

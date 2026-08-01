# Architecture Decision Records

Every significant decision in FlowX has an ADR. One decision per record — never
bundled. Each record states the forces, the decision, the accepted trade-offs,
and the condition under which it must be revisited.

| ADR | Decision | Status | Revisit when |
|---|---|---|---|
| [0001](ADR-0001-flow-and-capability-as-primitives.md) | Flow + Capability are the only two user primitives | Accepted | a real use case cannot be expressed as either |
| [0002](ADR-0002-compile-time-orchestration.md) | Compile-time orchestration; no runtime reflection | Accepted | generator maintenance exceeds its benefit — measured as > 3 generator defects per phase (**counted by nothing**) or **build overhead > 8 % sustained, which has FIRED at +67.1 %** — or dynamic flows become a top-3 request |
| [0003](ADR-0003-execution-profiles.md) | Per-flow execution profiles instead of always-durable | Accepted | journal cost drops to ephemeral levels |
| [0004](ADR-0004-universal-trigger-model.md) | One trigger abstraction for all transports | Accepted | > 30 % of flows need transport-specific escape hatches |
| [0005](ADR-0005-manifest-as-build-artifact.md) | The manifest is a first-class build artifact | Accepted | never — this is foundational |
| [0006](ADR-0006-journal-and-leases.md) | Append-only journal + fenced leases | Accepted | a managed durable-execution primitive makes this commodity |
| [0007](ADR-0007-result-over-exceptions.md) | `Result<T>` for business outcomes | Accepted | the language gains first-class union types |
| [0008](ADR-0008-serialization-and-schema.md) | Source-generated STJ, versioned schemas | Accepted | journal serialisation becomes the measured bottleneck |
| [0009](ADR-0009-plugin-contracts.md) | Plugins depend only on `FlowX.Abstractions` | Accepted | never — extension integrity depends on it |
| [0010](ADR-0010-csharp-dsl-over-yaml.md) | C# DSL is the source of truth; YAML is export | Accepted | non-developer authoring becomes a primary requirement |
| [0011](ADR-0011-fixed-policy-stage-order.md) | Fixed policy stage order | Accepted | three documented legitimate counterexamples |
| [0012](ADR-0012-apache-2-license.md) | Apache-2.0 licence | Accepted | a governance model requires a different licence |
| [0013](ADR-0013-dsl-vocabulary-over-ca1716.md) | DSL vocabulary takes precedence over CA1716 | Accepted | a first-class VB.NET or F# consumer story is adopted |
| [0014](ADR-0014-derived-error-catalogue-vs-build-budget.md) | Keep the derived error catalogue; re-express the build-overhead budget | **Proposed** | four conditions, and **two are no longer open** ([§10](ADR-0014-derived-error-catalogue-vs-build-budget.md#10-which-of-the-four-revisit-triggers-have-fired)): the inner loop pays full derivation cost per edit — **FIRED**; the withheld rate exceeds 20 % — **crossed at 42 %**, on evidence its own author calls inadmissible. Still open: a real project measures the derivation at > 2× its recorded cost, or P8 approaches the manifest v1.0 freeze |
| [0015](ADR-0015-journal-schema-and-durable-execution.md) | Journal the step boundary, keyed on `(instance, scope, step, attempt)`; resume through the same step loop | Accepted (amended by 0016) | the derived resume position breaches B8 on a long `ForEach` or a deep `SubFlow` tree, or journal payloads outgrow the generated STJ context |
| [0016](ADR-0016-postgres-journal-adapter.md) | Journal payloads are `json`, not `jsonb`; a lease carries no foreign key to the instance it precedes | Accepted | a payload needs indexing inside the journal — at which point this and 0008 re-open together |
| [0017](ADR-0017-manifest-v1-freeze-criteria.md) | Eight checkable criteria that must hold before the manifest schema is frozen at v1.0 (amends 0005) | Accepted | a criterion is closed by a check that could not have failed; P8 starts with the outbox or the policy engine still absent; the schema gains a field; or the freeze happens |
| [0018](ADR-0018-outbox-publication-and-ordering.md) | Declare `IEventPublisher` in `FlowX.Abstractions`; publish per `partition_key` in staging order and offer no global order; retention refuses to purge an instance holding a pending event | Accepted | ~~a broker plugin exists and `PublisherConformance` can hold two implementations to the contract~~ (**met at WP-56b**: `RedisStreamEventPublisher`, held to the suite alongside the recording double — the prefix return survived), or a dead-letter path is needed |
| [0019](ADR-0019-redis-lease-store.md) | A lease's expiry is a value in a hash, never a Redis key TTL; `StepScope.Root` renders as `-` at the key space | Accepted | the key space must live under an `allkeys-*` eviction policy, or a lease store is asked to carry state a journal should own |
| [0020](ADR-0020-cli-reads-the-journal-as-rows.md) | `flowx replay --mode inspect` reads the journal as rows over the published migration contract and joins it against the manifest; no journal document is published and `CliLinksNoFlowXAssembly` is not amended | Accepted | a second journal store adapter ships; or a `replay` mode has to *execute* rather than render, which this record explicitly does not decide; or the manifest freeze (0017) closes with `replay` still reading columns nothing versions |
| [0021](ADR-0021-manifest-publishes-the-wait.md) | An `AwaitSignal` step publishes `signal` and a folded `timeout`, produced and diff-classified in the change that declares them (amends 0017) | Accepted | a timer arms the declared wait, at which point `FLOWX-DIFF-206` stops being Neutral; `.Delay` produces a step; the folding set proves too small; or the freeze closes with either field unclassified |
| [0022](ADR-0022-http-shape-of-a-suspending-flow.md) | A suspending flow answers `202` with where to continue it, and its signal endpoint is generated one per (flow, signal) (amends 0004) | Accepted | an instance-status resource is built; a second transport gains a signal path; authorisation execution lands; or a timer gives a wait a fourth outcome |
| [0023](ADR-0023-policy-stages-hook-through-the-plan.md) | A policy stage is reached through `ExecutionPlan.HasStepPolicies` and a resolved `StepNode.StepPolicy`, never by walking the declared chain per step (amends 0011) | Accepted | a stage has to run *between* steps rather than around one; or `EngineAllocationTests` records a non-zero figure for an unpoliced ephemeral plan — the B2 gate this record protects; or a third flag of this shape is proposed |
| [0024](ADR-0024-stage-four-is-a-fixed-nesting.md) | Within stage 4 the four kinds nest `Retry { CircuitBreaker { Bulkhead { Timeout { capability } } } }`, by kind and never by declaration order (amends 0011) | Accepted | three documented cases need a different nesting — deliberately 0011's own bar; a fifth stage-4 kind is implemented (`Hedge` and `Fallback` are catalogued and undeclarable); or [10 §2](../10-Policy-Framework.md#2-fixed-stage-order--the-core-decision)'s `order` value is built |
| [0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md) | Stage 4 executes in full; stage 1, stage 3, stage 5 and stage 7's `Audit` do not, and each skip is argued separately — the duplicate-charge row of 0011's table is held shut by FLOWX1014 and a stable idempotency key, not by the stage-3 policy (amends 0011) | Accepted | stage 1, 3, 5 or the `Audit` is implemented, at which point FLOWX1032 narrows again; `Hedge` or `Fallback` becomes declarable, which would falsify "stage 4 in full"; or FLOWX1014 is downgraded from an error, which removes the first of the two mechanisms |
| [0026](ADR-0026-an-occurrence-names-the-instance-it-starts.md) | A cron occurrence derives the instance id it starts, so the journal's primary key — not a leader election — is what makes a schedule fire once across every node (amends 0004) | Accepted | a schedule has to fire an instance whose id something else owns; a store weakens `StartAsync`'s duplicate refusal; a second transport needs occurrence-derived identity, at which point the derivation belongs to the abstraction rather than to the sweep; or `PerTenant` becomes declarable and one occurrence has to name many instances |
| [0027](ADR-0027-a-missed-schedule-fires-late.md) | A fire missed while every node was down happens **late**, not never, bounded by a declared catch-up horizon and narrowed by `MissedFire` (amends 0004) | Accepted | a deployment is found running with a horizon shorter than its longest outage; `RunAll` is declared on a schedule dense enough that one sweep's catch-up exceeds `MaxConcurrentRecoveries`; a durable per-schedule cursor is built, which would remove the horizon; or `Overlap` becomes executable and a late fire has to ask whether the previous one finished |
| [0028](ADR-0028-a-scheduled-flows-input-is-its-occurrence.md) | A `[CronTrigger]` flow's input contract is `ScheduledFire` — the occurrence, not the wall clock — enforced by `FLOWX1037` rather than by a silently skipped registration | Accepted | a scheduled flow needs data no occurrence carries, e.g. the tenant `PerTenant` would fan out over; `TriggerEnvelope` is populated for real, at which point `ScheduledFire` overlaps it; or a second trigger kind wants a platform-fixed input, which would make one rule out of two |
| [0029](ADR-0029-the-manifest-publishes-a-schedules-address.md) | The manifest publishes `cron` and `timeZone` and gains **no** field for `MissedFire`, `Overlap`, `Jitter` or `PerTenant`; F1's count of unproduced fields does not move (amends 0017) | Accepted | a consumer outside the application has to act on a firing policy; `PerTenant` becomes executable, which makes the fan-out an address rather than tuning; `flowx diff` gains a schedule rule these four would feed; or the freeze (0017) closes with `cron` or `timeZone` unclassified |

## Writing an ADR

```markdown
# ADR-NNNN: <Decision in active voice>

**Status:** Proposed | Accepted | Superseded by ADR-MMMM
**Date:** YYYY-MM-DD
**Deciders:** <roles>

## Context
Forces at play: constraints, quality goals affected, options considered
(rejected options: one line each with the rejection reason).

## Decision
We will <decision>, because <primary driver>.

## Consequences
**Positive:** …
**Negative / accepted trade-offs:** …
**Revisit when:** <condition> (mandatory)
```

An ADR with no "Negative" section has not been thought through. An ADR with no
"Revisit when" is a decision nobody can ever safely change.

*Two records broke that rule and were brought to it on 2026-07-31, in the way the rule
implies rather than by adding headings: **ADR-0013**'s "Reversal condition" was renamed
`Revisit when`, because it was already one, and **ADR-0016** gained the Positive / Negative
split its trade-offs had been argued under other headings all along. Both records say which
was done and why. A third obligation this table carries is the "Revisit when" column itself
— it is where a reader checks whether a decision needs reopening, so a condition summarised
here without its measurable half, as **0002**'s was, hides exactly what the column exists to
show.*

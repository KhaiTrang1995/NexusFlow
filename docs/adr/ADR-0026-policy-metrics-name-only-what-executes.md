# ADR-0026: A policy metric is created only for a policy that executes, and a breaker's state is published when it changes rather than when it is scraped

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture
**Amends:** [10 §9](../10-Policy-Framework.md#9-observing-policies--seven-of-seven-metrics-emit)

> **Two features shipped in the same release and never met.** The policy engine executes
> stage 4 ([ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md)) and the
> telemetry seam publishes eleven instruments
> ([12 §3](../12-Observability.md#3-metrics)) — and not one
> of them describes a policy. A circuit breaker that genuinely opened did so with no external
> evidence of any kind. This record is what closes that, and what it deliberately leaves open.

---

## 1. Context

[10 §9](../10-Policy-Framework.md#9-observing-policies--seven-of-seven-metrics-emit) specifies seven metric
rows. Its own warning box said none was emitted, and gave the reason: *"FlowX ships no metrics
or logging infrastructure at all."* That reason expired when
[`FlowXMetrics`](../../src/FlowX.Abstractions/Observability/FlowXMetrics.cs) landed. What
remained was that nobody had connected the two, and the gap had a shape worth stating: the four
events an operator is actually paged about — **a breaker opening, a retry attempting, a bulkhead
refusing, a timeout firing** — are all decisions the engine makes silently and the flow reports
only as an `Error` on a failed step. An `Error` reaches the caller. It does not reach a
dashboard, and a breaker that opens for every caller of a dependency is not a property of any
one call.

### 1.1 The rule this repository already applies to an unemitted metric

`FlowXMetrics`'s remarks are unambiguous about the two rows of §3 it declines to create:

> *"an instrument that exists and is never written to publishes an empty series, and an empty
> series is indistinguishable from a healthy one."*

That is the whole difficulty with §9's table, because **three of its seven rows describe
policies that do not execute.** `flowx_ratelimit_rejected_total`,
`flowx_cache_hits_total`/`_misses_total` and `flowx_idempotency_replays_total` belong to stage
1, stage 5 and stage 3 — the stages ADR-0025 §2 skips, each for its own stated reason. Creating
them would publish four series that are permanently zero, and a permanently-zero
`flowx_ratelimit_rejected_total` reads as *"nothing has ever been refused"* when the truth is
*"nothing ever refuses"*. That is a stronger lie than silence, because silence is visibly
missing and a zero is not.

### 1.2 A state gauge has an ownership problem an ordinary counter does not

`flowx_circuit_state` is specified as a gauge. The .NET idiom for a gauge is
`CreateObservableGauge`, whose callback is invoked per scrape — which requires the callback to
be able to *find* every live breaker at that moment. Breakers live in a
`ConcurrentDictionary` field on a `FlowEngine` instance, and an engine is an ordinary object a
host creates and drops. A static registry of live engines is the only way a scrape-time
callback could reach them, and it is a registry that either leaks every engine ever created or
needs weak references and a reaping policy — infrastructure with a failure mode
(a stale breaker publishing a stale state) that is worse than the gap it closes.

### 1.3 Rejected options

* **Create all seven instruments and leave three empty.** Rejected for §1.1's reason, which is
  this repository's own and is applied to `flowx_stream_lag_records` today.
* **An observable gauge over a static registry of engines.** Rejected for §1.2. It also breaks
  B6 in a way the guard pattern cannot fix: an observable callback is invoked by the meter, so
  it is not a call site that can check `Enabled` first.
* **Emit a span event per policy decision instead of a metric.** §9 asks for both — *"policy
  decisions also appear as span events"* — but a span is sampled and a page is not. An
  operator alerting on "this breaker is open" cannot do it from a signal that is dropped at
  99 % sampling. Spans are the diagnosis; the metric is the alert. Only the metric is built
  here.
* **Count a breaker refusal only, without the admissions.** Rejected: a refusal count with no
  denominator cannot answer *"what fraction of calls is this breaker refusing"*, and forty
  refusals means something different against forty-five calls than against forty-five thousand.

---

## 2. Decision

**Four instruments are created — the §9 rows whose policy executes — and the three whose policy
does not are left unnamed. `flowx_circuit_state` and `flowx_bulkhead_queue_depth` are
synchronous `Gauge<int>`s recorded at the instant the value changes.**

### 2.1 What is created, and what each call site is

| Metric | Instrument | Recorded when |
|---|---|---|
| `flowx_policy_invocations_total` | `Counter<long>` | every stage-4 policy application, refused **and** clean |
| `flowx_retry_attempts_total` | `Counter<long>` | a dispatch beyond the first, after the deadline check |
| `flowx_circuit_state` | `Gauge<int>` | a breaker transition, under the breaker's own lock |
| `flowx_bulkhead_queue_depth` | `Gauge<int>` | a caller joins or leaves the queue |

`PolicyMetrics` is a separate type from `FlowXMetrics` because §9 and
[12 §3](../12-Observability.md#3-metrics) are separate
frozen tables; they share `FlowXTelemetry.Meter`, because one meter name is that type's entire
purpose, and nothing else.

### 2.2 The three unnamed rows are omitted, not named-and-unemitted

This is the opposite of what was done for `TriggerAdmittedTotal` and `StreamLagRecords`, which
are named in `TelemetryNames` with no producer — and the difference is deliberate. Those two
describe a **subject that exists** and a seam that cannot reach it, so the name is a commitment
whose implementation is pending. A rate-limit rejection counter describes a **decision no code
makes**. There is nothing to freeze: the `scope` label §9 gives it presupposes a decision about
what a rate-limit scope *is*, and that decision belongs to whoever executes stage 1. Naming it
now would pre-commit a future author to a label shape chosen by someone who was not building
the thing.

### 2.3 A gauge recorded on transition, and a baseline so the series can recover

A breaker publishes its state when it changes and at the first call that finds it closed. The
baseline matters and is not decoration: an alert of the form `flowx_circuit_state == 2` needs a
`0` to recover to, and without an initial reading a dashboard cannot tell a closed breaker from
a breaker nothing has ever asked. `CircuitBreakerState` therefore starts at a sentinel that is
neither open nor closed, and the first `TryEnter` publishes the real value.

Publication happens **inside the breaker's existing lock**. Two threads opening the same breaker
at the same instant would otherwise both read a stale "last published" and emit the transition
twice, and a state gauge that double-reports is one an operator cannot count edges on.

### 2.4 Both halves of every counter, so a rate has a denominator

`flowx_policy_invocations_total` is incremented when a breaker admits as well as when it
refuses, when a bulkhead grants a permit as well as when it turns a caller away, and when a
timeout is armed and *not* reached as well as when it fires. The `outcome` label is what
separates them. This is the difference between a counter that can express "3 % of calls to this
dependency are being refused" and one that can only express "there have been 40 refusals".

`flowx_retry_attempts_total` is the exception and counts only attempts **beyond** the first,
because the first dispatch is not a retry — counting it would put every step that ever ran on a
retry dashboard.

### 2.5 B2 is untouched structurally, and B6 is guarded at every call site

Nothing in `PolicyMetrics` is reachable unless `ExecutionPlan.HasStepPolicies` is true *and* the
step's resolved `StepPolicy.IsActive` is true — the gate
[ADR-0023](ADR-0023-policy-stages-hook-through-the-plan.md) already placed in front of the whole
policy path. **An ephemeral flow that declares no policy does not reach a guard in this file; it
does not reach this file.** B2 is therefore a consequence of ADR-0023 rather than of care taken
here, which is the stronger of the two positions.

B6 is a property of the *caller*: every helper checks `Instrument.Enabled` before constructing
a single `KeyValuePair`, because a label that is not already a `string` boxes on its way in —
and two of these four take an `int` value.
`TelemetryCostTests.APolicyReportingADecisionToNobodyAllocatesNothing` measures the four helpers
with no listener attached and requires exactly zero bytes.

---

## 3. Consequences

**Positive:**

* **The four events an operator is paged about are now visible.** A breaker opening, a retry
  attempting, a bulkhead refusing and a timeout firing each produce a measurement, and
  `PolicyMetricsTests` asserts the measurement rather than the instrument — a test that only
  checked an instrument had been created would pass against exactly the silence being fixed.
* **§9's warning box is deleted rather than softened.** It said "no metric in this table is
  emitted"; four now are, and the table says which three are not and why.
* **The omission is checkable.** A future author implementing stage 1 finds no
  `RateLimitRejectedTotal` constant to fill in, which is a compile error rather than a silently
  wrong series.
* **No new lifetime to manage.** A synchronous gauge has no callback, so no engine is kept
  alive by the telemetry that describes it.

**Negative / accepted trade-offs:**

* **A transition-recorded gauge is not a scrape-time truth.** An exporter that starts after a
  breaker opened sees nothing until the next transition, where an observable gauge would have
  reported the current state on its first scrape. The mitigation is the baseline reading in
  §2.3, and it is only a partial one: a process whose breaker opened before the exporter
  attached publishes its `2` only when it closes. This is the cost of not holding a registry of
  live engines, and it is accepted rather than solved.
* **`capability` and `key` on `flowx_circuit_state` carry the same value.**
  [10 §6](../10-Policy-Framework.md)'s composite `BreakerKey` has four components and only
  `Capability` is expressible, so the two labels coincide. Emitting the duplicate is a bet that
  the key widens later; if it never does, every deployment pays one redundant label for ever.
* **`flowx_bulkhead_queue_depth` has no zero until a queue has formed.** It is recorded only on
  the path that actually queues — the uncontended `Wait(0)` returns before reaching it — so a
  bulkhead nobody has ever waited for publishes no series at all. That is consistent with §1.1
  (no empty series) and inconsistent with a dashboard that expects a flat zero.
* **`stage` is a constant label today.** Every kind that reaches the counter is stage 4, so the
  label distinguishes nothing and costs a string per measurement. It is emitted because §9
  froze it and a series an operator already graphs must not change shape when stage 1 lands.
* **The span-event half of §9 is still unbuilt.** *"Policy decisions also appear as span events
  on the step span, so a trace shows why a call took 3.2 s"* remains a specification. §9 is
  therefore half-implemented, and its text now has to say so rather than saying nothing is.

**Revisit when:** any of stage 1, stage 3 or stage 5 executes, at which point its §9 row gets a
name and §2.2's argument for omitting it becomes history; or a second execution engine, or an
out-of-process scraper, needs the breaker state — which a per-transition gauge cannot serve, and
which is the case §1.2's rejected registry would have to be reconsidered for; or the span-event
half of §9 is built, at which point the two halves should share one decision about what a
"policy decision" is called.

---

**See also:** [ADR-0023](ADR-0023-policy-stages-hook-through-the-plan.md) ·
[ADR-0024](ADR-0024-stage-four-is-a-fixed-nesting.md) ·
[ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md) ·
[10 §9](../10-Policy-Framework.md#9-observing-policies--seven-of-seven-metrics-emit) ·
[12 — Observability](../12-Observability.md)

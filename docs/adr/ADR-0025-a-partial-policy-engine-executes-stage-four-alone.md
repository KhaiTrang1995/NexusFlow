# ADR-0025: A partial policy engine may execute stage 4 alone, and the three stages it skips are each safe to skip

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture
**Amends:** [ADR-0011](ADR-0011-fixed-policy-stage-order.md)

> **ADR-0011 makes the stage order the safety property.** *"The order is not configurable…
> each of these is a real production incident, and each becomes unexpressible."* A policy
> engine that implements some stages and not others is, on its face, exactly what that record
> forbids: it runs stage 4 without stage 3, which is the row of ADR-0011's own table reading
> **"retry outside idempotency → duplicate charges"**. This record is the argument that the
> row does not apply, and the statement of what would make it apply.

---

## 1. Context

WP-57 shipped the compensation retry alone and justified it in one sentence: *"executing the
**last** stage cannot skip an earlier one, which is the property that makes a single-policy
slice safe to ship before the engine that runs the other fifteen."* That argument is exact,
and it is **not available** to any stage but the last. A package implementing stage 4 has to
make a different one.

### 1.1 What is declarable, which is narrower than the catalogue

[10 §3](../10-Policy-Framework.md#3-the-policy-catalogue) lists seventeen rows. `PolicySet`
offers nine builder methods, and there is no policy attribute of any kind in
`FlowX.Abstractions` — `[Timeout]`, `[CircuitBreaker]`, `[Audit]`, `[RateLimit]` and
`[Idempotency]` appear in [10 §4](../10-Policy-Framework.md#4-declaring-policies) and do not
exist. So **eight catalogue rows cannot be declared at all**: `Quota`, `Authorize`, `Consent`,
`Validate`, `Hedge`, `Fallback`, `Batch` and `Outbox`. A stage that has nothing declarable in
it is not skipped by this decision; it is empty.

That reduces the question to the nine kinds an author can write:

| Stage | Declarable kinds | This package |
|---|---|---|
| 1 · Admission | `RateLimit` | not executed |
| 2 · Identity | *(none declarable)* | empty |
| 3 · Integrity | `Idempotency` | not executed |
| 4 · Resilience | `Timeout`, `Retry`, `CircuitBreaker`, `Bulkhead` | **executed** |
| 5 · Efficiency | `Cache` | not executed |
| 6 · Execution | the capability | executed, always |
| 7 · Consistency | `Audit` | not executed |
| 7 · Consistency | `CompensationRetry` | executed since WP-57 |

### 1.2 Why stage 4 and not another

It is the only stage whose kinds need nothing FlowX does not already have. `Timeout`, `Retry`,
`CircuitBreaker` and `Bulkhead` need a clock and a counter; the engine holds an `IClock`
already, for the deadline and for the compensation backoff. `RateLimit`, `Idempotency` and
`Cache` each need a store, and a store is a plugin contract — [ADR-0009](ADR-0009-plugin-contracts.md)
territory, decided per contract rather than in passing. `Audit` needs a record schema and a
sink.

It is also the stage the complaint is about. Every published example of the gap —
[FLOWX1032](../diagnostics/FLOWX1032.md)'s, [10 §4](../10-Policy-Framework.md#4-declaring-policies)'s
`ExternalRead`, `samples/banking`'s — is a timeout, a retry and a breaker.

### 1.3 Rejected options

* **Ship nothing until all nine run.** Rejected: it is the position that produced
  FLOWX1032, a rule whose entire purpose is to apologise for the gap. Four working policies
  are worth more than a rule explaining why there are none.
* **Ship a partial version of each stage** — an idempotency in-flight guard with no replay, an
  audit record with no payload. Rejected: a half-executing policy is worse than an unexecuted
  one, because the declaration then looks satisfied. FLOWX1032's own remedy list says the same
  thing about deleting a declaration to silence it.
* **Ship stage 4 and delete FLOWX1032.** Rejected: three declarable kinds remain inert and an
  author declaring a `Cache` must still be told. The rule narrows, exactly as
  [FLOWX1028](../diagnostics/FLOWX1028.md) and `FLOWX1031` were narrowed before it.

---

## 2. Decision

**`PolicyStage.Resilience` is executed in full. Stages 1, 3 and 5 are not, and each skip is
argued individually below rather than covered by a single claim that partial is fine.**

### 2.1 Skipping stage 1 (`RateLimit`) is safe for stage 4

An unenforced rate limit admits *more* traffic than declared. It cannot corrupt anything, and
it cannot make a stage-4 policy wrong: a retry, a breaker and a bulkhead are all mechanisms
for behaving well under load, and they behave the same whether the load arrived past a limiter
or not. The failure mode is a dependency seeing more calls than the manifest promises — which
is what happens today, and what will keep happening until stage 1 lands.

### 2.2 Skipping stage 3 (`Idempotency`) does **not** re-open ADR-0011's duplicate-charge row

This is the load-bearing paragraph. ADR-0011's table says *"retry outside idempotency →
duplicate charges | prevented because Integrity (3) precedes Resilience (4)"*. The `Idempotency`
**policy** is not what prevents that, and [10 §5](../10-Policy-Framework.md#5-retry-safety)
says so in its own words. Two independent mechanisms do:

1. **[`FLOWX1014`](../diagnostics/FLOWX1014.md) refuses a `Retry` on a capability that does
   not declare `Idempotent = true`** — at build time, unconditionally, and it has always been
   enforced. `PolicyChain.Build` enforces the same rule a second time on any plan built any
   other way, so the engine's assumption holds for a hand-built plan too. A retry can only
   exist over a capability whose author has asserted that calling it twice is calling it once.
2. **A retry presents the same `ctx.IdempotencyKey`.** The key is seeded once per invocation
   and the engine does not touch it between attempts, which is
   [10 §5](../10-Policy-Framework.md#5-retry-safety)'s first explicit guarantee —
   *"attempt 2 presents the same key as attempt 1, which is what makes downstream
   deduplication work"* — and is pinned by
   `PolicyExecutionTests.EveryAttemptPresentsTheSameIdempotencyKey`.

What the stage-3 `Idempotency` **policy** does is a different thing: it deduplicates a
*caller's* repeated request across invocations, by recording a result against a key and
replaying it. Not having it means a caller who submits the same request twice runs the flow
twice — which is the behaviour today, is not caused by this package, and is not what
ADR-0011's row is about.

### 2.3 Skipping stage 5 (`Cache`) is safe for stage 4

An unconsulted cache means the call happens. Slower, never wrong, and strictly the more
conservative behaviour. The incident ADR-0011's table names for stage 5 — *"cache before
authorisation → tenant A served tenant B's cached data"* — needs a cache to occur, and there
is none. `FLOWX1018` continues to refuse a `Cache` on a capability with side effects, so the
declaration is still checked even though it is not applied.

### 2.4 Skipping stage 7 (`Audit`) is visible rather than safe

An unwritten audit record is a real loss, not a conservative default, and this record does not
pretend otherwise. It is unchanged from before this package, it remains reported by
FLOWX1032, and `samples/banking`'s README continues to say that no financial audit record is
written by a policy.

### 2.5 What must be true when a skipped stage lands

**A stage added later must not be able to run after stage 4.** The engine reaches stages in
one place — the step loop, in the nesting
[ADR-0024](ADR-0024-stage-four-is-a-fixed-nesting.md) fixes — so adding stage 3 means adding
it *outside* the retry loop, and adding stage 5 means adding it outside the dispatch and
inside stage 4. Neither is a rearrangement of what this package built; both are an
insertion at a point ADR-0011 already names.

---

## 3. Consequences

**Positive:**

* **`.WithPolicy(Policies.ExternalRead)` does what it says.** The timeout arms, the three
  retries happen, the breaker opens — which is the complaint FLOWX1032 was written to
  acknowledge and could not fix.
* **The remaining gap is smaller and is still reported.** FLOWX1032 narrows from eight kinds
  to three (`RateLimit`, `Idempotency`, `Cache`), so an author still hears about every
  declaration that is not applied, and hears about a shorter list.
* **The safety argument is per-stage and checkable.** Each of §2.1–§2.4 names the mechanism
  that makes the skip survivable, or says plainly that there is none. A future reader can
  falsify any one of them without having to re-derive the whole position.
* **The two mechanisms in §2.2 are tested rather than asserted.** FLOWX1014 has its own
  compiler tests; the stable key has a runtime test named in this record.

**Negative / accepted trade-offs:**

* **ADR-0011's "the order is the safety property" is now true with a footnote**, and the
  footnote is this record. A reader who takes the original at face value will believe stage 3
  runs before stage 4 in a shipped FlowX. It does not, because stage 3 does not run.
* **§2.2's argument depends on FLOWX1014 never being suppressed.** It is an error rather
  than a warning, and suppressing an error takes a deliberate `.editorconfig` entry with a
  `FLOWX-DEBT` marker — but a team that did so would have a retry over a non-idempotent
  capability and no second line of defence, and the engine would retry it.
* **A retry now amplifies load that stage 1 was declared to bound.** A step declaring both a
  `RateLimit` and a `Retry(3)` gets the retry and not the limit, so the declared worst case is
  three times what the author wrote down. FLOWX1032 reports the `RateLimit` on that step,
  which is the only warning available.
* **`Audit` is inert while the step it audits gains behaviour.** A financial reviewer reading
  `samples/banking` now sees a ledger post that is genuinely bounded by a timeout and
  genuinely not audited, which is a wider gap between the two halves of one policy set than
  existed before.

**Revisit when:** any of stages 1, 3 or 5 is implemented, at which point the corresponding
subsection of §2 stops being a justification and becomes history, and FLOWX1032 narrows again;
or `Hedge`, `Fallback` or any other stage-4 catalogue row becomes declarable, because this
record claims stage 4 is executed *in full* and a new declarable kind would falsify that
sentence; or FLOWX1014 is downgraded from an error, which would remove the first of §2.2's two
mechanisms and re-open ADR-0011's duplicate-charge row for real.

---

**See also:** [ADR-0011](ADR-0011-fixed-policy-stage-order.md) ·
[ADR-0023](ADR-0023-policy-stages-hook-through-the-plan.md) ·
[ADR-0024](ADR-0024-stage-four-is-a-fixed-nesting.md) ·
[FLOWX1032](../diagnostics/FLOWX1032.md) · [FLOWX1014](../diagnostics/FLOWX1014.md) ·
[10 — Policy Framework](../10-Policy-Framework.md)

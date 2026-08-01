# ADR-0024: Within stage 4 the four policies nest in a fixed order by kind, not in the order they were declared

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture
**Amends:** [ADR-0011](ADR-0011-fixed-policy-stage-order.md) ·
[10 §2](../10-Policy-Framework.md#2-fixed-stage-order--the-core-decision)

---

## 1. Context

[ADR-0011](ADR-0011-fixed-policy-stage-order.md) fixes the order of the seven *stages*.
`PolicyChain.Build` implements it as a stable sort on `(int)PolicyStage`, and the stability is
deliberate — its own comment says two policies in the same stage "must keep their declared
order, or the emitted plan differs between builds".

That is the right rule for a *manifest*, where the array is a published description and
reproducibility is the whole point. It is not an answer to the question the engine has to ask.
**`Timeout`, `Retry`, `CircuitBreaker` and `Bulkhead` are all stage 4**, so the chain hands
the engine four policies with nothing to separate them but the order somebody happened to type
them in — and the four are not commutative:

| Nesting | Consequence |
|---|---|
| `Timeout { Retry { … } }` | one timeout across all attempts; the last attempt gets whatever is left, usually nothing |
| `Retry { Timeout { … } }` | a timeout per attempt, which is what `attempts × timeout` means |
| `Bulkhead { CircuitBreaker { … } }` | an open breaker still consumes a permit to discover it is open |
| `CircuitBreaker { Bulkhead { … } }` | an open breaker refuses before taking a permit |

[10 §2](../10-Policy-Framework.md#2-fixed-stage-order--the-core-decision) says *"within a
stage, an `order` value breaks ties"*. **There is no such value.** `PolicyDescriptor` carries
`Kind`, `Stage` and `Parameters`, and no builder method on `PolicySet` accepts an order. So
the documented tie-break does not exist, and the tie is currently broken by declaration order
— which means `Policies.ExternalRead` and a set with the same three lines in a different order
would run differently while publishing an identical manifest.

### 1.1 The arithmetic is already committed to one of the answers

[`FLOWX1019`](../diagnostics/FLOWX1019.md) multiplies a set's `Timeout` by its
`Retry(attempts)` and checks the product against the flow's `[FlowDeadline]`. That check is
only meaningful if **the timeout bounds each attempt** — under `Timeout { Retry { … } }` the
product would be nonsense, because the whole retry sequence would fit inside a single timeout.
A build-time rule already asserts one of the two nestings; the runtime must not choose the
other.

### 1.2 Rejected options

* **Honour declaration order.** Rejected: it makes a resilience decision depend on typing
  order, produces two behaviours from one manifest, and contradicts FLOWX1019.
* **Add an `order` parameter, as [10 §2](../10-Policy-Framework.md#2-fixed-stage-order--the-core-decision)
  describes.** Rejected for now: it hands the author the exact four-way choice above, and
  three of the four orderings are the incidents ADR-0011 exists to make unexpressible. It also
  widens the DSL and the manifest schema in a package about the runtime.
* **Sort within the stage by kind name.** Rejected: alphabetical order is
  `Bulkhead, CircuitBreaker, Retry, Timeout` — which puts the bulkhead outside the retry, so a
  retried step holds one permit for the duration of all its attempts and its backoffs. Correct
  by accident is still by accident.

---

## 2. Decision

**Within `PolicyStage.Resilience` the four kinds nest in a fixed order by kind, outermost
first:**

```
Retry { CircuitBreaker { Bulkhead { Timeout { capability } } } }
```

Declaration order within the stage is ignored by the engine. `PolicyChain`'s stable sort is
unchanged — it still decides what the manifest publishes and in what order — and
`StepPolicy.From` reads the four kinds out of it into named fields, after which the array
order is not consulted again.

Each boundary, and why it is where it is:

**`Retry` outermost.** It is the only one of the four whose unit is *the whole attempt*; every
other kind describes something about a single call. Putting it outside is also what makes
FLOWX1019's `timeout × attempts` arithmetic true rather than an approximation, and what makes
[10 §5](../10-Policy-Framework.md#5-retry-safety)'s "the deadline allows another attempt
including its backoff" a question the engine can actually ask — it is asked between attempts,
which only exists as a place if retry is the outer loop.

**`CircuitBreaker` inside the retry.** So each attempt is counted. A breaker outside would see
one outcome per step rather than one per call, which is the wrong denominator for a failure
*ratio*: three attempts against a dead dependency would move the window by one, and the
breaker would need three times the traffic to notice an outage. It also makes the interaction
[10 §11](../10-Policy-Framework.md#11-anti-patterns) demands — *"retry without a breaker:
retries amplify an outage into a self-DDoS… always pair them"* — actually hold, because the
breaker can open *between* two attempts of the same step and refuse the rest.

**`Bulkhead` inside the breaker.** So an open breaker refuses without taking a permit. The
other way round, a dependency that is down would hold every permit in the pool for as long as
it takes each caller to be told the breaker is open — turning the isolation policy into the
thing that propagates the outage.

**`Timeout` innermost, around the call itself.** It is the only one of the four that is about
the duration of one invocation, and it is the only one that needs a `CancellationToken` to
reach the capability. Anything between it and the call would be inside the budget it grants.

---

## 3. Consequences

**Positive:**

* **One manifest means one behaviour.** Two sets with the same kinds in a different order
  publish the same `policies` array and now also run the same way, so `flowx diff` reporting
  "no change" is true rather than approximately true.
* **FLOWX1019 stops being a rule about a hypothetical.** Its arithmetic describes what the
  engine does, so a build that passes it describes a flow whose worst case really does fit the
  deadline.
* **The three orderings that are incidents are unexpressible**, in the same sense and for the
  same reason ADR-0011 makes cache-before-authorise unexpressible: there is no syntax for
  them.
* **`StepPolicy` needs no ordering data.** It carries parameters, and the nesting is in the
  engine's control flow where a reader can see it as code rather than as a sort key.

**Negative / accepted trade-offs:**

* **[10 §2](../10-Policy-Framework.md#2-fixed-stage-order--the-core-decision)'s "an `order`
  value breaks ties" is now wrong in a second way.** It was already wrong — no such value
  exists — and this record makes the sentence unimplementable rather than merely
  unimplemented, because an `order` that reordered stage 4 would reintroduce the four-way
  choice. The document is corrected in the same change; the sentence does not survive as
  aspiration.
* **A legitimate counterexample has no escape hatch.** A caller who genuinely wants one
  timeout across all attempts — a total budget rather than a per-attempt one — cannot express
  it. The honest substitute is the flow's `[FlowDeadline]`, which is exactly a total budget
  and which the timeout is already clamped to; but it is per-flow, not per-step.
* **The nesting is knowledge in the engine, not in the plan.** A second execution engine, or
  a consumer reading `flowx.manifest.json` to reason about behaviour, cannot derive it from
  the published artifact. It is recorded here and in `StepPolicy`'s remarks, and nowhere a
  machine reads.
* **`PolicyChain`'s stable sort now protects something narrower than it says.** Its comment
  argues stability matters so builds are reproducible; that is still true of the manifest, but
  a reader may take it to mean the order is behavioural. `StepPolicy`'s remarks say plainly
  that it is not.

**Revisit when:** three documented cases appear where an author needs a nesting other than
this one — the same bar ADR-0011 sets for itself, and deliberately the same number; or a
fifth stage-4 kind is implemented (`Hedge` and `Fallback` are catalogued and undeclarable
today), because a new kind has to be placed in this order and the placement may not be
obvious; or [10 §2](../10-Policy-Framework.md#2-fixed-stage-order--the-core-decision)'s
`order` value is built, at which point this record decides what it is allowed to reorder.

---

**See also:** [ADR-0011](ADR-0011-fixed-policy-stage-order.md) ·
[ADR-0023](ADR-0023-policy-stages-hook-through-the-plan.md) ·
[ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md) ·
[FLOWX1019](../diagnostics/FLOWX1019.md) · [10 §5](../10-Policy-Framework.md#5-retry-safety)

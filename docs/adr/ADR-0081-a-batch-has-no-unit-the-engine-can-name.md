# ADR-0081: `Batch` is refused and recorded — its unit is a set of *instances*, and nothing in the engine can name one

**Status:** Accepted
**Date:** 2026-08-15
**Deciders:** Repository owner · Platform architecture
**Amends:** [10 §3](../10-Policy-Framework.md#3-the-policy-catalogue) ·
[ADR-0023](ADR-0023-policy-stages-hook-through-the-plan.md)

> **WP-84, and it ends the way WP-80's first half did.**
> [ADR-0078 §3](ADR-0078-stage-four-nests-six-kinds.md) named four things a capability-valued
> fallback was blocked on; three were built and one was refuted, which is what makes that shape
> of record worth writing rather than a way of not deciding. This is the same shape over a row
> whose blockers are larger, and it does not expect the same ending.

---

## 1. Context

[10 §3](../10-Policy-Framework.md#3-the-policy-catalogue) catalogues `Batch` at stage 5 with
parameters `size` and `window` and one line of behaviour: *"coalesces N invocations into one"*.
`Consent` shipped in the same work package and left `Batch` and `Outbox` as the catalogue's last
two undeclarable rows — and `Outbox` is undeclarable for a reason that record already gives (it
is the emit step's own commit, real and not a policy anybody declares).

So `Batch` is the only row left where "undeclarable" might mean "not built yet". This record is
the answer to whether it does.

### 1.1 The question that decides it: whose invocations?

A policy is declared on a step, and a step runs once per flow instance. So *N invocations* can
only mean one of two things, and the row does not say which.

**N invocations of one instance.** A step inside a `ForEach` runs once per element, so ten
elements are ten invocations and coalescing them is expressible without leaving the instance.
This is real and it is **not what the row describes**: the elements are already in one flow, one
context and one journal scope, and an author who wants one call for ten elements writes a step
that takes the collection. `IterationSource` and `EnterIteration` exist precisely so a loop's
elements are addressable, and a policy that silently turned ten declared iterations into one
call would make the journal's per-iteration rows describe work that did not happen that way.

**N invocations across instances.** Order 4471's `payment.capture` waiting 50 ms for orders
4472 and 4473 so that all three are captured in one call to the gateway. This *is* what the row
describes, it is what makes a batch worth having, and every blocker below is a consequence of
it.

### 1.2 Why the second reading is the one the row means

The parameters settle it. A `window` is a duration to wait for *more work to arrive*, and inside
a single instance there is no more work to arrive — the iteration count is known when
`BeginIteration` returns. A `size` bounds how many separate arrivals may be held. Both are
meaningless within one instance and both are exactly what a cross-instance accumulator needs.

---

## 2. Decision

**`PolicySet` gains no `Batch` builder, `StepPolicy` gains no field, and the engine gains no
accumulator. The row stays *undeclarable* and this record is what it is blocked on.**

Five pieces are missing. Each is missing rather than merely unwritten, and each is named here so
that nobody re-derives the list.

### 2.1 There is no batched capability contract, and no dispatch seam for one

`ICapability<TIn, TOut>` takes one input and produces one output.
`IStepDispatcher.ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)` invokes the
capability at a step index **against one context**, and only generated code can name a
`JsonTypeInfo<T>`, read the step's input off the state bag or write its output back — the wall
that put `DescribeCacheEntry`, `DescribeAudit`, `RestoreState` and `Validate` on the dispatcher
rather than on the engine.

A batch needs to hand one call the inputs belonging to *N different contexts* and distribute N
answers back into them. There is no member for that and no type for it: this is a new capability
contract, a new dispatcher member and a new emitter path, which is
[ADR-0009](ADR-0009-plugin-contracts.md)'s question rather than this one's. It is
[ADR-0078 §3](ADR-0078-stage-four-nests-six-kinds.md)'s first blocker again, and strictly worse
— that one needed a seam for a second capability at one context, and this needs a seam for one
capability at N.

### 2.2 A policy cannot suspend an instance, and every wait in FlowX is a plan node

Waiting for a window to fill means the first instance to arrive stops, in the engine, for up to
`window`. Under `Durable` that is a suspension, and a suspension in FlowX is a **step node**:
`AwaitSignal`, `PollUntil` and `Delay` each occupy an index, carry a
`StepNode.Identity` the journal keys a row by
([ADR-0015](ADR-0015-journal-schema-and-durable-execution.md)), publish themselves in the
manifest ([ADR-0021](ADR-0021-manifest-publishes-the-wait.md)) and reach a resume frontier that
`ReadResumeFrontierAsync` can rebuild from. A poll's *second* ending needed a row of its own
before it was allowed to exist ([ADR-0066](ADR-0066-a-polls-second-ending-is-a-row.md)).

A stage-5 policy is none of those things. It is a resolved field on a node, read inside the step
loop — [ADR-0023](ADR-0023-policy-stages-hook-through-the-plan.md)'s whole shape — with no
index, no identity, no `wake_at` and no frontier entry. An instance parked in an accumulator is
an instance the journal says is *running* and no node is running; a lease sweep would find it,
fence it and start it again somewhere else, which is the batch coalescing an instance with
itself.

Under `Ephemeral` there is no journal to disagree with, and the cost lands on the caller
instead: the window holds N HTTP requests open, and a node that dies loses N of them rather than
one.

### 2.3 Stage 4's nesting is defined over one call, and a batch changes what the call is

Stage 5 runs **inside** stage 4's nesting — the cache sits between the innermost resilience
policy and the dispatch
([ADR-0025 §2.5](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md)). So a batched
call is wrapped by a `Timeout`, a `Bulkhead`, a `CircuitBreaker`, a `Hedge` and a `Retry` that
were each declared by, and are each measured against, **one** flow.

* **The timeout has no honest value.** A declared `Timeout` is clamped to what is left of *the
  flow's* deadline, which is what makes [10 §11](../10-Policy-Framework.md)'s *"a timeout longer
  than the deadline is a lie"* prevented rather than discouraged. N instances have N remaining
  budgets. Taking the shortest lets one nearly-expired caller cut a batch the others paid to
  wait for; taking the longest hands a caller a call that outlives its own deadline. There is no
  third answer, because the clamp is per-flow by construction.
* **The breaker and the bulkhead stop measuring the dependency.** Both count *calls*. A batch of
  ten is one call, so a `CircuitBreaker(failureRatio: 0.5)` over batched steps measures the
  health of batches, and a `Bulkhead(maxConcurrency: 64)` admits 64 batches — a bound whose
  effective value is a function of how full the windows happened to be.
* **The retry's unit fractures.** A batch returns N results and some fail. Re-batching only the
  failures is a retry whose unit is a subset of the call it is retrying, which the fixed nesting
  has no way to express; retrying the whole batch re-presents the successes, and their
  capabilities were never asked to be idempotent — `FLOWX1014` asked that of the step, once, on
  the author's behalf.

### 2.4 Coalescing across instances is coalescing across tenants

[10 §2](../10-Policy-Framework.md#2-fixed-stage-order--the-core-decision)'s first "why rigidity
is the feature" row is *cache before authorisation → tenant A served tenant B's cached data*,
and Efficiency (5) is placed after Identity (2) so that a cache **key** can carry the tenant and
the principal's permission set. A batch at the same stage has the same exposure and a worse
shape: it does not key a lookup, it puts N tenants' inputs into one request to a dependency,
which then sees a document no single tenant owns. Nothing in
[16](../16-Multi-Tenant.md) describes that boundary, and a batch keyed by tenant — the obvious
repair — is a batch that coalesces almost nothing in the deployments where tenants are many and
small, which is the shape the option exists for.

### 2.5 The accumulator is new machinery, and B2 is the smaller half of that objection

`FlowEngine` takes a clock and its optional plugin stores, and nothing else. A batch needs a
per-capability accumulator with a timer, shared across every instance on the node, and — because
a bound that is per-process is n × the declared figure across n nodes, which is
[ADR-0040](ADR-0040-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md)'s argument — arguably
shared across nodes too, which would make it a plugin store and a conformance suite.

Budget **B2**'s hard zero survives this easily: a plan declaring no batch would leave a flag
false and allocate nothing, exactly as `HasStepPolicies` already does. B2 is listed last here
because it is the piece that is *not* a blocker, and saying so is what keeps the other four
honest.

---

## 3. Consequences

**Positive:**

* **The catalogue stops implying a scope question.** `Batch` is annotated *undeclarable —
  blocked on ADR-0081's pieces* rather than left as a row a reader might cost as a work package.
  [10 §3](../10-Policy-Framework.md#3-the-policy-catalogue) has carried "specification with no
  surface" for four rows; three of them now have an argument attached and the fourth is
  `Outbox`, whose own row already carries one.
* **Nothing half-working ships.** A batch that is not journalled, whose timeout is one of N
  guesses and whose breaker measures batches, would be worst exactly under the load it was
  declared for. That is the reasoning ADR-0078 §3 used to keep a builder to one overload, and it
  applies here to keeping the builder from existing at all.
* **The distinction in §1.1 is now written down.** "Coalesce a loop's iterations" and "coalesce
  across instances" are different features with different costs, and the row's one line does not
  separate them. A future reader arguing for the first no longer has to discover that the second
  is what the parameters describe.

**Negative / accepted trade-offs:**

* **A real efficiency lever stays unavailable.** Batching is the standard answer to a
  per-request-priced or rate-limited dependency, and FlowX's answer is currently "write a flow
  whose step takes a collection" — which works only when one caller already holds the collection,
  and that is precisely the case §1.1 says is not the interesting one.
* **`PolicyStage.Efficiency`'s doc comment names three kinds and the platform offers one.**
  "Cache, batch, coalesce" — `Cache` executes, and the other two are this record. The comment
  stays as written, because the stage enum is a published contract and the catalogue is where a
  reader is told what executes.
* **This record could be wrong about §2.2 in one direction.** If a batch were expressed as a
  *step node* rather than as a policy — a plan node with an identity, a row and a frontier entry
  — several of the blockers weaken at once, and the stage-order objection in §2.3 disappears
  because a node is not inside stage 4's nesting. That is a different feature from the one the
  catalogue describes, it is not a `PolicySet` builder, and it is named here rather than left for
  somebody to think they invented.
* **A fifth blocker may be hiding behind the first.** §2.1 stops at "there is no contract",
  which means the questions a batched contract would raise — how a batch's inputs are redacted
  into one journal payload, what `ctx.IdempotencyKey` means for a call carrying N of them — have
  not been asked. They are downstream of a decision this record declines to make.

**Revisit when:** a batched capability contract exists in `FlowX.Abstractions` — a named
interface the generator can emit a dispatcher member for, taking N inputs and answering N
results — at which point §2.1 closes and §2.3's three sub-arguments become the whole of the
question; or a wait becomes expressible without a plan node, which would reopen
[ADR-0066](ADR-0066-a-polls-second-ending-is-a-row.md) and
[ADR-0021](ADR-0021-manifest-publishes-the-wait.md) together and is not a change this record
would make on its own; or somebody proposes `Batch` as a **step node** rather than a policy, at
which point §3's third negative is the starting point and this record is the wrong one to
amend; or a deployment demonstrates the dependency cost that makes §3's first negative
load-bearing, which is the evidence this record does not have.

---

**See also:** [ADR-0078 §3](ADR-0078-stage-four-nests-six-kinds.md) — the same shape of refusal,
three of whose four blockers were later built ·
[ADR-0079](ADR-0079-a-fallback-capability-is-a-dispatch-of-its-own.md) — what building them
looked like ·
[ADR-0023](ADR-0023-policy-stages-hook-through-the-plan.md) — the hook shape a policy has, and
that a batch cannot use ·
[ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md) — where stage 5 sits
inside stage 4 ·
[10 §3](../10-Policy-Framework.md#3-the-policy-catalogue) — the catalogue row

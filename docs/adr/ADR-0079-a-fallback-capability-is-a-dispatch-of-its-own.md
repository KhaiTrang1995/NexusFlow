# ADR-0079: A fallback capability is a dispatch of its own, and its journal row says who answered

**Status:** Accepted
**Date:** 2026-08-15
**Deciders:** Repository owner · Platform architecture
**Amends:** [ADR-0015](ADR-0015-journal-schema-and-durable-execution.md) ·
[ADR-0078](ADR-0078-stage-four-nests-six-kinds.md) ·
[10 §3](../10-Policy-Framework.md#3-the-policy-catalogue)

---

## 1. Context

[10 §3](../10-Policy-Framework.md#3-the-policy-catalogue) has catalogued `Fallback` as
*"capability or constant"* since the framework was written. WP-78 shipped the constant and
[ADR-0078](ADR-0078-stage-four-nests-six-kinds.md) §3 refused the capability — not vaguely, but
with four named blockers and the instruction *"so that nobody re-derives it"*. That refusal was
the right call at the time and this record is what it was written to make possible: the four are
addressed on their merits, three by building what was missing and one by showing the premise was
false.

The four, as 0078 §3 states them:

1. **No dispatch seam.** `IStepDispatcher.ExecuteAsync(stepIndex, …)` invokes *the capability at
   a step index*, and a fallback capability has no index, no generated `case` and no typed write
   into the state bag.
2. **No journal identity.** [ADR-0015](ADR-0015-journal-schema-and-durable-execution.md) keys a
   row on `(instance, scope, step, attempt)`, and *"two capabilities under one step index would
   either share a key — an append-only table cannot hold that — or need a fifth component, which
   is a schema change and a migration"*.
3. **No compensation story.** *"A step answered by its fallback capability really did produce an
   effect, so it must be compensable — by the fallback's undo, which the step does not declare
   and the stack has nowhere to record."*
4. **No manifest surface.** A second capability a step may invoke is a dependency `flowx diff`,
   the impact analysis and every other consumer of the manifest cannot see.

### 1.1 What this record found on the way

Blocker 3's premise is false, and the reason it is false also exposed a defect in the **constant**
fallback that had shipped: the forward path leaves a degraded step off the compensation stack and
the resume path could not, because it rebuilds that stack from committed rows and a success row
said nothing about which capability wrote it. One instance therefore had two behaviours, and
which one a deployment got depended on whether a node had died. §2.3 and §2.5 are the account.

---

## 2. Decision

**A fallback capability is dispatched by generated code under the step's own index, and its
journal row carries its own capability id.** The builder gains one overload beside the constant's:

```csharp
public PolicySet Fallback<TValue>(TValue value)   // WP-78
public PolicySet Fallback<TCapability>()          // this record
```

Everything else in ADR-0078 stands unchanged: the fallback is still outermost of the six, still
consulted once after the retry has stopped asking, still outside stages 1 and 3, and still not
pushed onto the unwind stack.

### 2.1 The seam: a member, not a step index

`IStepDispatcher` gains `ExecuteFallbackAsync(int stepIndex, FlowContext ctx, CancellationToken)`,
defaulted to a throw, and the generator emits it only for flows that declare a capability
fallback. The engine asks for *"step N's fallback"* the same way it asks for step N.

This is the same seam `DescribeCacheEntry`, `DescribeAudit` and `RestoreState` already use, and
0078 §3.1 identified it correctly as *"a new member on the dispatcher contract and a new emitter
path, not an engine change"*. The wall was never the engine's control flow; it was that only
generated code may name a `JsonTypeInfo<T>` or call `ctx.Set<T>`. A generated method does both.

**Rejected: giving the fallback a step index of its own.** It would put a node in the flat graph
that the layout says nothing reaches, and every walker over the step array — the manifest, the
impact analysis, `flowx diff`, the emitter's own switches — would have to learn to skip it. The
index a fallback has is the index of the step it answers for, and that is not a compromise: it is
what a fallback *is*.

**The declaration is bound, not reflected.** A `PolicySet` is a `static readonly` field built with
no step in sight, so `Fallback<TCapability>()` can hold a `Type` and nothing else; reading the
capability's id off that type at run time is what constraint C2 refuses. `PolicyChain.ForStep`
takes a third argument — the descriptor the generated plan already holds — and replaces the
declaration with it, which is the service that method already performs for the step's own
capability.

### 2.2 The identity: the column, not a fifth key component

**A degraded row is keyed exactly as ADR-0015 keys every row, and names the capability that
answered rather than the one that failed.**

```
(instance, scope, step, 1)  rating.primary    Failure
(instance, scope, step, 2)  rating.secondary  Success
```

0078 §3.2 read the problem as needing a fifth key component. It does not, because the component
was already there: **the key answers *which execution*, and `capability_id` answers *what
ran***. That column has been `NOT NULL` on every row since the initial schema, and the attempt
sequence keeps counting across both capabilities, so the rows are distinct without anything new.
**No schema change and no migration.**

The precedent is not new either. [06 §7](../06-Execution-Engine.md#7-compensation-semantics)
rule 6 already has a compensation row carry the *compensating* capability's id rather than the id
of the step it reverses, and for the identical reason: an operator reading the history has to be
able to see which capability the row is about. That rule was written after the engine got it
wrong once, and this is the same rule applied to the other capability a step can invoke.

**What the identity buys, and it is three things rather than one:**

* **Replay legibility.** Both facts survive on one step — what the dependency said, and what
  answered instead — without joining anything.
* **Resume.** A committed row under the fallback's id means the degraded path already owns this
  step, so the retry is skipped entirely and the fallback is asked again. Without it the
  derivation available is only *"this step has not succeeded"*, and a new node would spend the
  author's attempts on a dependency an earlier node had already given up on. The primary's own
  error died with that node — a row records an outcome and never an `Error` — so the loop carries
  `flow.step_already_degrading`, which names both capabilities and claims nothing the committed
  history does not support.
* **The compensation rebuild**, which is §2.3.

### 2.3 Compensation: blocker 3's premise is refused, not engineered around

**`FLOWX1053` now asks its question of the fallback capability as well as of the step: a
capability-valued fallback must itself declare no side effects.**

0078 §3.3 argued that a step answered by its fallback *"really did produce an effect, so it must
be compensable"*. Refused at build time, it does not — and so §2.7's rule that a degraded step
registers no compensation survives word for word, now for a stated reason rather than an
accidental one.

This is a refusal on the merits, not a scope cut:

* **The rule the fallback would break is the one that makes it sound.** The stack records
  *completed steps*, each carrying the compensation its own capability declared. A step answered
  by a second capability that wrote something would need the stack to record whose undo applies,
  and the entry for "step N completed" would mean two different things depending on which
  capability answered. That is ambiguity in the one structure a saga cannot afford it in.
* **The path is the wrong one to put a write on.** A fallback runs *because* a dependency has just
  failed. It is the least-exercised code in the system, running at the moment the system is
  already degraded. Making it the path that changes the world is backwards.
* **FlowX already treats `SideEffects = []` as the licence not to compensate.** `FLOWX1018` refuses
  a cache over a write on exactly that basis. A capability that declares no effects and yet needs
  an undo is a capability that is lying, and the platform does not model around a lying
  declaration anywhere else.
* **The case that would need it barely exists.** A side-effecting fallback is legal at all only
  over a side-effect-free primary, since `FLOWX1053`'s first half already refuses the other
  arrangement. A pure primary with an impure fallback is a modelling mistake, and where it is not,
  ADR-0078's existing consequence stands: *"a degraded mode for a write is expressible only as a
  branch in the flow, which is where a compensable effect belongs."*

**And the rule is now enforced on both paths.** The forward loop has excluded a degraded step from
the unwind stack since WP-78; the resume path now excludes it too, because a success row whose
capability is not the step's own is a degraded one. That divergence was live — see §2.5.

### 2.4 The manifest, and where `flowx diff` puts it

The step publishes `fallback: "<id>@<version>"` beside `compensation`, and the fallback capability
enters the top-level capability inventory in its own right, carrying its version, side effects,
authorisation stance and error catalogue. That is exactly how a compensation is published, and for
the reason that decision was taken: a capability listed only as a name on a step is one whose
breaking changes nothing downstream can see.

**`flowx diff` gains no new rule, and that is the deliberate placement.**
[ADR-0021](ADR-0021-manifest-publishes-the-wait.md) §2.4 keeps the diff out of a flow's `steps`
with one narrow exception — a declared wait, which is an *inbound address* — and a fallback is not
one. It is a dependency, so it is classified where every dependency is:

| change | finding | severity | why |
|---|---|---|---|
| a fallback naming a capability new to the build | `FLOWX-DIFF-101` | Additive | nothing that worked stops working; a step that used to fail now answers degraded |
| a fallback changed so its old capability leaves the build | `FLOWX-DIFF-010` | Breaking | a published capability is gone, which means the same thing whether it left a step or a fallback |
| a fallback swapped for a capability already in the build | none | — | the set of dependencies did not change |

Adding one is therefore never Breaking, which is the right answer for a gate people are expected
to obey.

### 2.5 What is **not** fixed: a constant fallback has no second identity

**A step answered by a *constant* is still indistinguishable on resume, and this record says so
rather than leaving it to be rediscovered.**

The identity in §2.2 works because a second capability supplies a second id. A constant supplies
nothing: its degraded row is a `Success` under the step's own capability, at an attempt after the
failures — which is byte-for-byte what a retry that eventually succeeded looks like. No derivation
from the committed history separates them.

The consequence is bounded and worth stating exactly. A flow whose step declares **both** a
constant fallback and a compensation, and which is **resumed** across the degradation, will put
that step on the unwind stack when the forward path would not have — running an undo for an
effect that never happened. It costs nothing where the step declares no compensation, which is the
overwhelming majority, and it is the pre-existing WP-78 behaviour rather than a regression.

Closing it needs what 0078 §3.2 anticipated and §2.2 avoided: a mark on the row that is not a
capability id, which is a `JournalOutcome` member, a relaxed `CHECK` constraint, a migration and
every adapter's parser. That is the right price for a real defect and the wrong price to pay
inside this package, which had four blockers to answer and did not need a fifth. It is this
record's first revisit condition.

### 2.6 Rejected options

* **An attempt-space band** — fallback rows numbered from a reserved base. Rejected: it needs a
  magic constant, and an identity that is only *probably* unique because retry counts are
  *usually* small is not an identity.
* **A scope suffix.** Rejected: `StepScope` means *which iteration*, its grammar is slash-separated
  digits and its `Depth` is derived from that. Overloading it would make a well-defined key
  component mean two things.
* **A fifth key component**, as 0078 §3.2 assumed. Rejected: it changes `IFlowJournal`, every
  adapter, the conformance suite and the schema, to record something a column already records.
* **Retrying, hedging or bulkheading the fallback.** Rejected: the declared chain wraps the step,
  and the fallback is the decision to *stop* asking. A fallback with its own retry is a second
  policy chain nobody declared.
* **Reporting the fallback's error as the step's** when the fallback also fails. Rejected: the
  degraded path failing does not change what went wrong. The dependency the author declared is
  what stopped answering, so that is the caller's error; what the fallback did is a second fact
  and lives where second facts live — a row under its own id, and an `exhausted` outcome on the
  counter that already means *"this policy was asked and could not help"*.
* **Allowing a side-effecting fallback that declares its own compensation.** Rejected on the
  merits in §2.3.

---

## 3. Consequences

**Positive:**

* **`docs/10 §3`'s `Fallback` row is whole.** The catalogue has said "capability or constant"
  since before either half existed; both halves now execute, ephemeral and durable.
* **A defect in the shipped constant fallback is closed** for the capability half and *named* for
  the constant half (§2.5). The forward path and the resume path now agree about which steps are
  compensable, which they did not.
* **No schema change, no migration, no new store contract.** The identity is a column ADR-0015
  already writes, so every existing journal — including third-party ones — carries a degraded row
  correctly with no change at all.
* **No new `flowx diff` rule and no new severity.** ADR-0021's line stays where it is.
* **B2 is untouched.** Everything here is behind `ExecutionPlan.HasStepPolicies` and
  `StepPolicy.IsActive`; a flow that declares no policy reaches none of it, and
  `EngineAllocationTests` still records zero.
* **`IStepDispatcher`'s new member is defaulted**, so no hand-written or third-party dispatcher has
  to change. Every *decorator* did, and a fitness test found the five that had not.

**Negative / accepted trade-offs:**

* **A constant fallback's degraded step is still not legible on resume** (§2.5). Named, bounded,
  and first on the revisit list.
* **`FLOWX1053` is stricter than "capability or constant" reads**, now in two places rather than
  one. A degraded mode that has to write remains a branch in the flow.
* **A fallback capability is a dependency that runs only during an outage**, which means it is the
  least-tested code path in any deployment that has one. The manifest publishing it is what makes
  that visible; nothing makes it exercised.
* **`FlowEngine.RunRangeAsync` moved from 160 to 163** on the complexity ratchet. The three lines
  are the resume rule, and they have to be where the retry is or they cannot skip it.
* **A second reading of the same capability now exists in the compiler** — the fallback type is
  resolved from the `PolicySet`'s tree rather than the flow's. It is the workaround `FLOWX1052`
  already makes for the constant, on the same licence: this runs in the generator, which holds the
  compilation.

**Revisit when:** a constant fallback's degraded row needs to be legible on resume, at which point
§2.5's `JournalOutcome` member and its migration are the shape; or a documented case needs a
side-effecting fallback badly enough to pay for a second compensation channel, at which point
§2.3's refusal re-opens with it; or a fallback is wanted over something other than a step — a
whole branch, a sub-flow — which is a different policy and not this one widened.

---

**See also:** [ADR-0015](ADR-0015-journal-schema-and-durable-execution.md) ·
[ADR-0021](ADR-0021-manifest-publishes-the-wait.md) ·
[ADR-0024](ADR-0024-stage-four-is-a-fixed-nesting.md) ·
[ADR-0078](ADR-0078-stage-four-nests-six-kinds.md) ·
[FLOWX1052](../diagnostics/FLOWX1052.md) · [FLOWX1053](../diagnostics/FLOWX1053.md) ·
[06 §7](../06-Execution-Engine.md#7-compensation-semantics) ·
[10 §3](../10-Policy-Framework.md#3-the-policy-catalogue)

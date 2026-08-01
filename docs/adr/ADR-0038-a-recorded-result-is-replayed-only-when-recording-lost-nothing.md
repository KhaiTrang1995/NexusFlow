# ADR-0038: A recorded result is replayed only when recording it lost nothing, and a flow that declares a `[Sensitive]` member may not declare an `Idempotency`

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture
**Amends:** [ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md) §2.2

> **This is the record for the one thing about stage 3 that is worse than not having it.**
> A replay that returns a *redacted* value as if it were the real one is not a degraded
> idempotency; it is a fabricated answer, returned to a caller who has no way to tell.

---

## 1. Context

### 1.1 The seam is not `Durable`-only, and the plan said otherwise

`PLAN` §6a and [ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md)
recorded that stage 3 was blocked because `IStepDispatcher` is type-erased. **That was wrong
when it was written and it is wrong now.** `DescribeStep(int, FlowContext)` and
`RestoreState(FlowContext, string)` have been on that interface since WP-59, the generator emits
both for every flow that journals a state bag, and between them they are a complete per-step
result seam: one turns the typed values into a document, the other turns the document back into
typed values, and only generated code can do either.

What was true is that `DescribeStep`'s own remarks say *"Called only for a `Durable` flow, at
the step boundary, before the commit."* That is a statement about its only caller, not a
constraint on the member, and this record makes it a statement about two callers. Nothing about
budget B2 changes: the seam is reached only when a step's resolved `StepPolicy` declares an
idempotency window, which is gated by `ExecutionPlan.HasStepPolicies` exactly as stage 4 is
([ADR-0036](ADR-0036-stage-one-and-stage-three-run-outside-the-retry.md)).

### 1.2 The blocker that is real

`RestoreState`'s contract says the document arrives *"sensitive members already redacted,
because there is no read path that could put them back"*, and `JournalPayload` is built so that
this cannot be worked around: there is **no accessor for the value**, the only exit is
`ToJson()`, and that exit redacts every member whose name appears in the flow's
`SensitiveMembers` — case-insensitively, at every depth. The type's own remarks say why the
shape is that severe: *"an opt-in redaction helper beside a public `Value` property is a control
that the first store under deadline pressure walks around."*

So a naive stage 3 does this, and `samples/banking` is the worked example:

1. `ValidateTransfer` produces a `ValidatedTransfer` carrying `DebtorIban`.
2. `ExecuteTransferFlow.SensitiveMembers` is `["CreditorIban", "DebtorIban"]`, read off the
   flow's input and output contracts.
3. The recorded document holds `"DebtorIban": "[redacted]"`.
4. A second caller presents the same key. The record is replayed. `ctx.Get<ValidatedTransfer>()`
   answers with an IBAN of `[redacted]`.
5. `PostDebit` posts a debit against account `[redacted]`, and the flow returns `200`.

The first caller's transfer settled. The second caller's transfer *looks* settled and moved
money to nowhere, and nothing anywhere reports a defect — every step succeeded.

### 1.3 Why the durable resume path is not a precedent

FlowX already restores a redacted state bag: `FlowEngine` calls `RestoreState` when it resumes a
journalled instance, and `JournalState`'s remarks accept the loss in as many words — *"what
comes back for a marked member is the placeholder, because that is what was stored."*

That does not license doing it again here, and the difference is not a matter of degree:

| | Durable resume | Idempotency replay |
|---|---|---|
| Whose request | **the same one**, continuing | **a different caller's**, being answered from a record |
| What the alternative is | strand the instance; its effects have already happened and the node that held the real values is gone | **dispatch the capability**, which is idempotent and returns the real value |
| What the placeholder is | a **loss** with no alternative, recorded and documented | a **fabrication**: an answer nobody computed, returned as if somebody had |

The durable path takes the lossy option because there is no other one. Stage 3 has a strictly
better option available at the moment of the choice — call the capability — so taking the lossy
one would be choosing corruption over cost.

### 1.4 Rejected options

* **Give `JournalPayload` an unredacted exit for this store.** Rejected, and it is the option
  this record exists to refuse. It would demolish the one structural control the repository has
  over `[Sensitive]`, and it would be worse here than for the journal: an idempotency store is
  read back into a **live context**, so the sink becomes a source and the value re-enters the
  program. The reason `RestoreState` can say "there is no read path that could put them back" is
  that nobody has built one; building one for a replay builds it for everything.
* **Record it redacted and replay it anyway, documenting the loss.** Rejected: §1.2. It is the
  half-executing policy ADR-0025 rejected, in the one form where the declaration does not merely
  look satisfied but produces a plausible wrong answer.
* **Record the completion and replay nothing** — an in-flight guard with no replay. Rejected:
  ADR-0025's rejected option 2, by name. The steps after the frontier would bind values no step
  produced, which `RestoreState`'s own contract calls out as the thing that must fail loudly.
* **Detect the collision per step, by walking the capability's output contract for member names
  that match the flow's `SensitiveMembers`.** Rejected, and it was the tempting one because it
  would let a flow declare a window on the steps whose results are clean. The matching is by
  name, case-insensitively, **at every depth**, over a contract graph that may reach types in
  referenced assemblies, through generics and collections. An analyzer that got that traversal
  wrong in the permissive direction would ship §1.2 silently. The conservative granularity is
  the one the mechanism itself has, and the mechanism's is the flow.

---

## 2. Decision

**A recorded result may be replayed only if recording it lost nothing. Two mechanisms enforce
that — one is the guarantee and one is the report — and they are deliberately not the same
mechanism.**

### 2.1 The guarantee: `JournalPayload.TryToReplayableJson`

`JournalPayload` gains one member, and it is **not** a widening:

```csharp
public bool TryToReplayableJson([NotNullWhen(true)] out string? json)
```

It returns the *same document* `ToJson()` returns, and returns `false` — yielding nothing at all
— when the redaction pass replaced anything. It cannot emit a byte `ToJson()` would not emit,
there is still no accessor for the value, and there is still exactly one place that decides what
a marked member is replaced with. What it adds is the answer to one question the payload is the
only thing that can answer: *did writing this lose anything?*

The engine records through it. A payload that would be redacted is never written to an
idempotency store, and the step **fails** with `policy.idempotency_not_replayable` rather than
recording a document it could not honestly replay. Loud, on the first execution, on the path
that would have created the bad record — not silently degraded, and not deferred to the second
caller who would have received the fabrication.

### 2.2 The report: `FLOWX1039`, at build time

[`FLOWX1039`](../diagnostics/FLOWX1039.md) is an **error** on a `.WithPolicy(...)` declaring an
`Idempotency` on a flow whose input or output contract declares a `[Sensitive]` member.

Flow-wide, per §1.4's last rejection: `SensitiveMembers` is read off the flow's two contracts
and applied to every document the flow writes, so "this flow redacts" is the exact granularity
the redaction has. A flow that declares one marked member cannot record any replayable result,
and the author is told which member and why.

**An error rather than FLOWX1032's warning**, for
[ADR-0030](ADR-0030-policy-stance-is-refused-at-build-time.md)'s reason: the alternative is not a
policy that does less, it is a step that fails at run time on the first execution. A build error
is the earliest honest moment to say so, and unlike FLOWX1032 there is a real fix — remove the
window, or move the marked member off the flow's contract.

### 2.3 Why both, when either alone would look sufficient

The build-time rule can be silent: `PolicySetReader` cannot read a set declared in a referenced
assembly, which is `FLOWX1036`'s whole subject, and a plan built by hand never meets an analyzer
at all. So the analyzer is not a guarantee and must not be treated as one.

The runtime refusal is the guarantee and is a bad *report*: it fires on a deployed flow, at the
first execution, as a failed step. So it must not be the only thing an author ever hears.

This is the same division `FLOWX1014` and `PolicyChain.Build` already make for the duplicate
charge — the rule reports, the constructor refuses — and it is made here for the same reason.

---

## 3. Consequences

**Positive:**

* **A replay returns the value the first execution returned, or there is no replay.** There is
  no third case, and the type that decides is the type that already owns the decision about what
  a store may see.
* **The `[Sensitive]` control is not weakened by a single byte.** `JournalPayload` still has no
  accessor for a value, still has one exit that emits a document, and that document is still the
  redacted one. A store cannot get at more than it could yesterday.
* **`samples/banking` demonstrates the decision rather than dodging it.** Its flow marks two
  IBANs, so it cannot declare a replayable window, and its `Policies.Admission` says so with
  FLOWX1039 named. That is a better thing for a banking sample to teach than a happy path.
* **ADR-0025 §2.2's argument is unaffected and is now load-bearing in a second way.** The stable
  key and `FLOWX1014` are still what hold the duplicate-charge row shut for the flows that
  cannot declare a window, and this record does not weaken either.

**Negative / accepted trade-offs:**

* **A flow that marks one member cannot use stage 3 anywhere, including on steps whose results
  are provably clean.** This is the cost of taking §1.4's conservative granularity, and it is a
  real one: `samples/banking`'s settlement register write would be a perfectly good place for a
  24-hour window and cannot have one. The alternative was an analyzer whose false negative is a
  silent wrong answer.
* **`JournalPayload` gains a member, and the type's design argument is that it has as few as
  possible.** The mitigation is that the member is strictly narrower than the one beside it —
  every document it emits, `ToJson` also emits — but "one more way out of `JournalPayload`" is
  the sentence that type's remarks are written against, and a future reader has to check the
  signature rather than trust the count.
* **The runtime refusal fails a step that would otherwise have succeeded.** A flow that reaches
  §2.1's guard has already dispatched its capability; the effect happened, and then the step is
  reported as failed and the completed compensable steps unwind. That is the correct direction —
  the alternative is a record that lies — but it is a failure caused by a policy rather than by
  the work.
* **`DescribeStep`'s "only for a `Durable` flow" is now false**, and it was a sentence people
  relied on when reasoning about the ephemeral path. It is corrected on the member rather than
  left to be discovered, and B2 is re-asserted rather than re-argued.

**Revisit when:** a read path for `[Sensitive]` values exists — an envelope encryption seam, a
key-management plugin — at which point `RestoreState`'s "there is no read path that could put
them back" stops being true and this whole record re-opens, along with the durable resume loss
§1.3 distinguishes itself from; or `SensitiveMembers` stops being flow-wide, at which point the
granularity in §2.2 is no longer the mechanism's own and §1.4's last rejection has to be
re-argued; or a second policy needs to record a result — stage 5's `Cache` is the obvious one —
because it will meet exactly this question and must not answer it differently.

---

**See also:** [ADR-0008](ADR-0008-serialization-and-schema.md) ·
[ADR-0015](ADR-0015-journal-schema-and-durable-execution.md) ·
[ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md) ·
[ADR-0030](ADR-0030-policy-stance-is-refused-at-build-time.md) ·
[ADR-0037](ADR-0037-an-idempotency-record-is-keyed-by-the-invocations-key.md) ·
[FLOWX1039](../diagnostics/FLOWX1039.md) ·
[10 — Policy Framework](../10-Policy-Framework.md)

# FLOWX1013 — Parallel branches must write disjoint context slots

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

The branches of a `Parallel` run **concurrently and share one context**. That context is a
bag keyed by contract type: the generated dispatcher ends every capability step with
`ctx.Set(result.Value)`, typed at the capability's declared output. So two branches whose
capabilities declare the **same output type** are two threads writing one key, and the step
after the join reads whichever branch happened to finish last.

[06 §9](../06-Execution-Engine.md#9-concurrency-and-parallel-steps) has stated the rule
since before anything could declare a fork — branches "write to **disjoint** context slots
— enforced at compile time (`FLOWX1013`) so parallel steps cannot race on shared state".
Until this analyzer existed, the id was reserved and the sentence was a promise the
compiler was not keeping.

### Why the runtime cannot catch this instead

Both writes are legal. Both succeed. The dictionary is guarded while a fork is in flight —
[06 §9](../06-Execution-Engine.md#9-concurrency-and-parallel-steps) — so there is no
corruption to detect and no exception to raise. What is left is a value, and deciding
whether the two branches' values *disagree* is a business question the engine has no
standing to answer.

That is the whole argument for making it a build error rather than a runtime one. This is
not a mistake that shows up in a test: with three branches and a fast local database the
same branch wins every time, and the flow is correct until the day one dependency is
slower than usual.

## Example that triggers it

```csharp
[Flow("loan.assess")]
public sealed partial class AssessLoanFlow : Flow<LoanApplication, Decision>
{
    protected override void Define(IFlowBuilder<LoanApplication, Decision> flow) => flow
        .Parallel(p => p
                .Branch<ScoreWithBureauA>()      // ICapability<LoanApplication, CreditScore>
                .Branch<ScoreWithBureauB>(),     // ICapability<LoanApplication, CreditScore>
            merge: MergeStrategy.AllMustSucceed)
        .Step<Decide>()
        .Return(ctx => new Decision(...));
}
```

```
error FLOWX1013: Branches 0 and 1 of the parallel step in flow 'AssessLoanFlow' both
                 produce 'Lending.CreditScore', so they race to write the same context slot
```

`Decide` reads `ctx.Get<CreditScore>()` and gets bureau A's score or bureau B's, depending
on which call returned first.

## How to fix it

**Give each branch its own contract.** This is usually the honest modelling anyway — two
bureaux do not produce interchangeable scores, and the flow almost certainly wants both:

```csharp
.Parallel(p => p
        .Branch<ScoreWithBureauA>()      // ICapability<LoanApplication, BureauAScore>
        .Branch<ScoreWithBureauB>(),     // ICapability<LoanApplication, BureauBScore>
    merge: MergeStrategy.AllMustSucceed)
.Step<ReconcileScores>()                 // reads both, and says what disagreement means
```

**Or run them in sequence.** If one really is meant to overwrite the other, say so in an
order a reader can see:

```csharp
.Step<ScoreWithBureauA>()
.Step<ScoreWithBureauB>()                // deliberately wins; no race, no ambiguity
```

**Or race them on purpose** — but only when the branches are genuinely interchangeable, in
which case they should still have distinct contracts and a step that picks:

```csharp
.Parallel(p => p.Branch<ScoreWithBureauA>().Branch<ScoreWithBureauB>(),
    merge: MergeStrategy.FirstSuccess)
```

`FirstSuccess` cancels the loser, but it does not make the slot safe: both branches may
already have written before the cancellation is observed. Distinct contracts are still
required.

## What it detects

`ParallelSlotAnalyzer` reads every `IFlowBuilder<,>.Parallel` invocation — resolved
semantically, so `System.Threading.Tasks.Parallel` is untouched — collects the `.Branch(...)`
calls belonging to *that* fork, and for each one gathers the output contract of every
capability it invokes at any nesting depth. Two branches naming the same
fully-qualified contract type is the conflict.

| Situation | Verdict |
|---|---|
| `.Branch<A>()` and `.Branch<B>()` where `A` and `B` declare the same `TOut` | **reported** |
| A capability nested inside `.Branch(b => b.Step<X>()...)`, at any depth | **reported** |
| A capability inside a `When` or `Switch` block inside a branch | **reported** — see below |
| A nested `Parallel` inside a branch | its own branches are checked as their own fork |
| Two branches producing `Quote` and `RetailQuote`, one deriving from the other | allowed — different keys |
| Two steps in the **same** branch producing the same contract | allowed — an ordered overwrite, not a race |
| Fewer than two branches | not checked; the generator lays such a declaration out inline |

**A conditional's arms are not treated as exclusive.** A branch whose `When` writes `Quote`
only in the `then` arm still races a sibling that writes `Quote` unconditionally, and the
race is real on the runs where the predicate holds. Reporting the possibility is right; a
rule that only fired when the collision was *certain* would be silent on every intermittent
version of this bug, which is the version that reaches production.

### What it cannot prove

Stated rather than implied, because a rule that stops a build has to be honest about its
edges — and because the gaps here are wider than they look:

- **A capability that calls `ctx.Set<T>()` from inside its own body is invisible.**
  `FlowContext.Set` is public, so a capability can write any slot it likes and this rule
  will not see it. **This is the largest gap**, and it means a green build is *not* a proof
  that the branches are disjoint — only that their *declared* contracts are. Closing it
  needs an effects attribute or a whole-program analysis, not a longer list. It is the same
  interprocedural limit [FLOWX1011](FLOWX1011.md) has with helper methods.
- **Only exact type identity is a conflict.** The state bag is keyed by `typeof(T)` at the
  static type the dispatcher writes, so a base and a derived contract occupy different keys
  and correctly do not collide. If two branches write `Quote` and `RetailQuote` and a later
  step reads `Quote`, it reads the first — which is confusing, and not a race.
- **A branch whose body is not a lambda is skipped.** A method group, or a variable holding
  an `Action<IFlowBuilder<,>>`, has no body at the call site — the same limit
  `FlowChainWalker` documents, for the same reason.
- **Anything that does not bind is skipped.** An unresolved capability means the file does
  not compile, and that message is better than this one.
- **A capability implementing `ICapability<,>` more than once is skipped.**
  [FLOWX1015](FLOWX1015.md) already reports it, and picking one of two contracts to guess
  with would be worse than saying nothing.
- **Sub-flows are not followed.** Nothing can declare one yet. When `SubFlow` lands, a
  branch invoking one writes whatever that flow writes, and this rule will have to be
  extended — or this paragraph rewritten to say it still does not.
- **Slots outside the context are not considered at all.** Two branches writing the same
  database row race in a way no compiler can see. This rule is about the flow's own state
  bag and nothing else.

## When to suppress

The honest case is a false positive from the conditional rule: two branches that each write
`Quote` under predicates the author knows to be mutually exclusive. The compiler cannot
know that, and the fix is usually to hoist the decision out of the fork — if only one of
them ever runs, they were not concurrent work.

Suppressing because "it has always worked" is the case to refuse. A race that has always
resolved the same way is a race whose outcome depends on a latency that has not changed
yet.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Execution engine §9](../06-Execution-Engine.md#9-concurrency-and-parallel-steps) · [Flow definition §3.3](../08-Flow-Definition.md#33-parallel)

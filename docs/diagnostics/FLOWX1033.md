# FLOWX1033 — CompensationRetry is declared on a step with no compensation

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** a `.WithPolicy(...)` set containing `CompensationRetry`, on a step that
> declares no `.CompensateWith<T>()`.

> [!NOTE]
> **This is the one policy that runs, dropped in silence.** [FLOWX1032](FLOWX1032.md)
> reports the eight kinds the runtime does not execute, and is a **warning** because P4 will
> execute them. This rule is its complement and is an **error**, because no release will ever
> execute a compensation retry that has no compensation to retry — there is nothing for it to
> wrap, in this version or any later one.

## What it means

`CompensationRetry` wraps a step's *undo*, not the step. `FlowEmitter` therefore emits it
only for a step that has one:

```csharp
// FlowEmitter.PolicyArguments
if (step.IsCompensable && step.PolicyKinds.Contains(CompensationRetryKind))
{
    arguments += ", compensationPolicies: PolicyChain.ForCompensation(" +
                 set + ", " + node + "Compensation)";
}
```

When the step is not compensable that branch is skipped, and **nothing else says so**. The
declaration reaches `flowx.manifest.json` — `ManifestWriter` publishes every kind it knows a
stage for, and it knows `CompensationRetry`'s — so the published contract says this step's
undo is retried five times while the compiled plan says the step has no undo at all.

**The runtime already refuses this shape; it just never sees it.**
`StepNode.ForCapability` throws:

> Step {index} declares a compensation policy set and no compensation. A policy chain that
> wraps nothing is a promise the unwind cannot keep, and it would read as though the step
> were recoverable when nothing would ever run.

That check cannot fire on a generated plan, because the emitter drops the argument before
constructing the node. This rule is what makes the same rule reachable from the DSL — the
identical relationship [FLOWX1014](FLOWX1014.md) and [FLOWX1018](FLOWX1018.md) have to
`PolicyChain`'s two rejections, and the identical failure FLOWX1014's own page records: *a
runtime check answering a question about something this compiler had invented rather than
about anything the author declared.*

## Example that triggers it

```csharp
public static class Policies
{
    // One set, applied to every ledger step. The CompensationRetry is right for two of
    // them and wraps nothing on the third.
    public static readonly PolicySet Ledger = PolicySet.Named("ledger")
        .Timeout(TimeSpan.FromSeconds(5))
        .CompensationRetry(attempts: 5);
}

flow
    .Step<PostDebit>()
        .CompensateWith<ReverseDebit>()
        .WithPolicy(Policies.Ledger)      // fine — the retry wraps ReverseDebit

    .Step<RecordSettlement>()
        .WithPolicy(Policies.Ledger)      // FLOWX1033 — nothing to retry
```

`RecordSettlement` is legitimately not compensable: once the register holds the transfer
there is nothing to take back. What is wrong is not the step, it is the five attempts
promised over an undo that does not exist. The report lands on the `WithPolicy` identifier.

**What stays silent:**

- A compensable step carrying `CompensationRetry`, whatever else is in the set.
- A non-compensable step carrying a set with no `CompensationRetry` — that is
  [FLOWX1032](FLOWX1032.md)'s business and nothing else's.
- A `.WithPolicy(...)` whose argument the compiler cannot resolve to a field or property
  initialiser declared in source. `PolicySetReader` returns nothing rather than guessing, so
  the rule is silent exactly where the emitter is — the same restriction FLOWX1014 works
  under, for the same reason: a diagnostic raised on a guess names a policy the author
  cannot find. That silence is [FLOWX1036](FLOWX1036.md)'s report, which says only that the
  set could not be read. `PolicySet.CompensationDefault` is not one of these: it resolves
  from metadata, so this rule *does* fire when it is applied to a step with no undo.
- A single attempt. `.CompensationRetry(attempts: 1)` on a step with no compensation reports
  this rule and not [FLOWX1035](FLOWX1035.md): the declaration reaches no plan node at all,
  which is the stronger statement, and two reports on one line would leave the author
  choosing which to act on.
- Either order of the two calls. `.CompensateWith<T>()` and `.WithPolicy(...)` both return
  `IStepBuilder<TIn, TOut>`, so both orders are legal C# and neither reports. A rule that
  depended on which the author wrote first would be a rule that fires on correct code half
  the time.

## How to fix it

Two edits, and which one is right is a question about the step rather than about the policy:

```csharp
// Either the step does have an inverse and it was not declared —
.Step<RecordSettlement>()
    .CompensateWith<VoidSettlement>()
    .WithPolicy(Policies.Ledger)

// — or it genuinely has none, and the set that names its policies should not
//   promise an undo. Split the set.
public static readonly PolicySet Ledger = PolicySet.Named("ledger")
    .Timeout(TimeSpan.FromSeconds(5));

public static readonly PolicySet LedgerCompensable = PolicySet.Named("ledger-compensable")
    .Timeout(TimeSpan.FromSeconds(5))
    .CompensationRetry(attempts: 5);
```

Splitting the set is the right answer for the shared-set case above.
`PolicySet.CompensationDefault` exists so that the common shape does not need a bespoke set
at all, and it is the whole set for a step whose only policy is the documented compensation
default:

```csharp
.Step<PostDebit>()
    .CompensateWith<ReverseDebit>()
    .WithPolicy(PolicySet.CompensationDefault)     // five attempts, and nothing else
```

> [!WARNING]
> **This page used to say "apply it alongside the step's own set", and that advice was wrong
> twice over.** `PolicySet.CompensationDefault` reached no plan at all until
> `PolicySetReader` learned to resolve a set declared in a referenced assembly — so the
> recommended way to retry an undo was the one way that could not work. And a step carries
> **one** policy set: `StepModel.WithPolicy` assigns rather than accumulates, so a second
> `.WithPolicy(...)` deletes the first from the compiled plan and from the manifest. Both
> halves are now diagnosed — the second is [FLOWX1034](FLOWX1034.md) — and the repair for a
> step that needs a timeout *and* a compensation retry is one set that declares both.

### The quick action, and the two cases it declines

**Offered for one case only**: when the set contains `CompensationRetry` and *nothing else*,
the whole `.WithPolicy(...)` call contributes nothing to the compiled plan —
`FlowEmitter.PolicyArguments` emits neither half, because the forward chain needs a kind that
is not the compensation retry and the compensation chain needs a compensation. Removing the
call is then provably behaviour-preserving, and the fix does exactly that.

It **does** change `flowx.manifest.json`: `ManifestWriter` publishes the kind today, so the
entry goes and `flowx diff` will report it. That entry is the promise of a retried undo for a
step with no undo, and removing a false claim from a published contract is the point of the
rule rather than a side effect. An author who wanted the entry to be *true* wants the other
repair, which is why the action is offered rather than applied.

**Declined when the set carries anything else.** Removing the call would then delete
declarations that do reach the plan and the manifest, and the two edits above are design
decisions the author owns. A quick action that guessed there would be one that deletes a
published contract entry which is true.

**Declined when a comment would go with it.** A comment written above the call attaches as
leading trivia of the `.` token — inside the node being removed, not beside it — and every
rule for re-placing it is right for one layout and wrong for the next. Declining costs two
keystrokes; guessing costs a sentence somebody wrote. A quick action may cost you a manifest
row; it may not lose your work.

## When to suppress

None. This is not a statement about a missing platform feature — it is a policy attached to
nothing, and no configuration, deployment or later release makes it run. Both fixes are in
the file the diagnostic points at.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry — see
[21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A suppression without
one fails the build.

**Suppressing this does not make the declaration work.** The emitter still drops it, so what
a suppression buys is a manifest that promises a retried undo, a plan with no undo, and no
message saying which one is true.

## Why this is an error where FLOWX1032 is a warning

They are adjacent ids about the same DSL call, so the split needs to be justified rather
than asserted. Every argument that makes [FLOWX1032](FLOWX1032.md) a warning fails here:

**"An error erases the inventory the fixing phase needs."** There is no fixing phase. P4
implements the eight kinds FLOWX1032 reports; it does not give a non-compensable step an
undo. A `CompensationRetry` here is not scaffolding for a feature that is coming — it is
attached to nothing, permanently, and preserving it preserves no information P4 wants.

**"The source is not wrong; it is written correctly for a platform that has the feature."**
There is no such platform. Every warning in this catalogue is a warning because some
legitimate program exists in which the reported code is correct, and this rule has none: a
retry over an undo that does not exist is not a design decision anyone would defend, in any
release.

**"Nothing is falsified."** Something is. The manifest publishes `CompensationRetry` on this
step, at stage `Consistency`, and the plan the engine walks carries no compensation for it —
so the published contract and the compiled artefact disagree about the same source line.
That is precisely the property that made the deleted `FLOWX1031`'s `AwaitSignal` half an
error rather than a warning.

**And the runtime already treats it as unrepresentable.** `StepNode.ForCapability` throws
`InvalidFlowPlanException` rather than dropping it, which is the strongest signal available:
the two other analyzer rules that mirror one of `PolicyChain`'s rejections — FLOWX1014 and
FLOWX1018 — are both errors, and `SafetyDiagnosticsAreErrorsRatherThanWarnings` pins them
there. An analyzer that warned about a shape the plan constructor refuses would be a warning
about a hard failure.

**`Info` was not a candidate**, for the reason the rest of the catalogue gives: it never
reaches a build log.

## This rule is not deleted

Unlike [FLOWX1028](FLOWX1028.md), [FLOWX1032](FLOWX1032.md) and the already-deleted
`FLOWX1031`, this one has no take-down row, because it does not describe a gap in the
platform. **That prediction has now half happened and held.** FLOWX1032 has narrowed from
eight kinds to four, and this rule became more load-bearing rather than less: a compensation
retry attached to nothing is still attached to nothing, and a set that also declares a
`Timeout` and a `Retry` now has two policies in it that visibly work — which makes it
likelier, not less likely, that a reader assumes the third does too. When the last four
kinds execute and FLOWX1032 is deleted, this rule stays.

---

**Back to:** [diagnostics index](README.md) · [FLOWX1032](FLOWX1032.md) ·
[FLOWX1014](FLOWX1014.md) · [FLOWX1012](FLOWX1012.md) ·
[Execution engine §7](../06-Execution-Engine.md) ·
[ADR-0011](../adr/ADR-0011-fixed-policy-stage-order.md)

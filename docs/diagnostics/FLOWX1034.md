# FLOWX1034 — Step declares more than one policy set

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** a step whose chain contains two or more `.WithPolicy(...)` calls.

> [!NOTE]
> **The other two calls in the family report a policy that is carried and not run.** This one
> reports a policy that is not carried at all: the second `.WithPolicy(...)` on a step
> *replaces* the first, and the first vanishes from the compiled plan and from
> `flowx.manifest.json` with no message. The deleted `FLOWX1032` at least left the
> declaration somewhere a reviewer can read it. Here there is nothing left to read.

## What it means

A step carries one policy set. `FlowAnalyzer.AttachPolicy` writes it with
`StepModel.WithPolicy`, which **assigns** rather than accumulates:

```csharp
// StepModel
public StepModel WithPolicy(string policySetName, string[]? policyKinds = null) => this with
{
    PolicySetName = policySetName,
    PolicyKinds = policyKinds ?? System.Array.Empty<string>(),
};
```

`IStepBuilder.WithPolicy` returns `IStepBuilder`, so writing two of them is legal C# and
reads as composition. It is not composition. The last call wins, and everything the earlier
sets declared is gone before the emitter or the manifest writer ever sees it — so a step that
declares a five-second `Timeout` and then a `CompensationRetry` compiles to a plan with no
timeout and publishes a contract with no timeout in it.

**This was already known one rule over and not reported.**
[FLOWX1019](FLOWX1019.md)'s page lists "a second `.WithPolicy(...)` on the same step" among
the cases it declines to put a number on, because "which set wins is a resolution question
this rule has no answer to". The answer is that the later one wins outright. That is a fact
about the compiler worth a diagnostic rather than a footnote in a rule about deadlines.

## Example that triggers it

```csharp
flow
    .Step<PostDebit>()
        .CompensateWith<ReverseDebit>()
        .WithPolicy(Policies.LedgerPost)          // FLOWX1034 — dropped, silently
        .WithPolicy(PolicySet.CompensationDefault)
```

The report lands on the **first** `WithPolicy` identifier of each call that is superseded,
naming the set that survives. Reporting on the survivor would put the message on the line
that is working.

**What stays silent:**

- One `.WithPolicy(...)` per step, in either order relative to `.CompensateWith<T>()`.
- Two `.WithPolicy(...)` calls on *different* steps, which is the ordinary case and the one
  `samples/banking` is built out of.
- A chain broken across statements — `var step = flow.Step<T>(); step.WithPolicy(a);` — where
  the analyzer cannot see which calls belong to one step. It reports on what it walked, never
  on what it could not.

## How to fix it

**Merge the sets into one, and name the merged set for the step.** A `PolicySet` is a fluent
chain, so the merge is textual and the result is one declaration a reviewer can read:

```csharp
public static readonly PolicySet LedgerPost = PolicySet.Named("ledger-post")
    .Timeout(TimeSpan.FromSeconds(5))
    .Audit("financial", "DebtorIban", "CreditorIban")
    .CompensationRetry(attempts: 5);
```

**Do not reach for `PolicySet.CompensationDefault` as a second application.** Until this rule
existed, [FLOWX1033](FLOWX1033.md) suggested exactly that — "apply
`PolicySet.CompensationDefault` alongside it on the steps that do have an undo" — and the
suggestion could not work: whichever of the two sets was written second deleted the other.
That page now says "split the set", which is the repair that does work, and
`PolicySet.CompensationDefault` is for a step whose *whole* policy set is the documented
compensation default.

## When to suppress

None. A suppression buys a build with no message and a plan that is still missing a control
the source declares. If two sets are genuinely wanted on one step, that is a request for
policy-set composition — a language feature this release does not have, and one that has to
answer what two `Timeout`s in one chain mean before it can be written. Until it exists,
merging is not a workaround; it is the only thing the compiler can represent.

## Related

- `FLOWX1032` (deleted) — declared policy is not executed by the runtime.
- [FLOWX1033](FLOWX1033.md) — `CompensationRetry` on a step with no compensation.
- [FLOWX1019](FLOWX1019.md) — flow deadline shorter than the step timeouts it must contain;
  its stated limits are where this gap was first written down.

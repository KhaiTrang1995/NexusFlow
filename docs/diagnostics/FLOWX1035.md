# FLOWX1035 — CompensationRetry declares a single attempt

> **Severity:** Warning · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** `.CompensationRetry(attempts: n)` with a literal `n` below 2, in a set
> applied to a step that declares a compensation.

> [!NOTE]
> **This was looked at once and left.** The judgement was that `attempts: 1` "is not a lie —
> one attempt is what one attempt means", and about the *number* that is right. What makes it
> a finding is what happens to the *kind*: `flowx.manifest.json` publishes
> `CompensationRetry` and no parameters, so the published contract cannot distinguish one
> attempt from five, and one attempt is precisely what a step with no declared chain at all
> already gets. The reasoning is recorded here rather than left to be rediscovered a third
> time.

## What it means

`CompensationPolicy.From` reads the declared attempt count and clamps it:

```csharp
// CompensationPolicy
public bool IsRetrying => Attempts > 1;
```

`ExecutionPlan.Create` ORs `IsRetrying` across every step into
`ExecutionPlan.HasCompensationPolicies`, and `FlowEngine.CompensateAsync` reads that one flag:

```csharp
var retrying = context.Plan?.HasCompensationPolicies ?? false;
…
var policy = retrying ? entry.Step.CompensationRetry : CompensationPolicy.None;
```

So `attempts: 1` leaves the flag false, the engine takes `CompensationPolicy.None`, and the
undo is dispatched exactly once — the same dispatch a step that declared no policy set gets,
because `CompensationPolicy.None` is itself one attempt. `attempts: 0` and any negative value
behave identically: `From` applies `Math.Max(1, …)`.

**The declaration cannot change behaviour in any direction.** It is not a weaker retry; it is
not a retry. What it does change is the published contract. `ManifestWriter.WritePolicies`
emits the kind and its stage:

```json
{ "kind": "CompensationRetry", "stage": "Consistency" }
```

and nothing else. A reviewer reading the manifest, a `flowx diff` comparing two of them and
an agent consuming one all see the same entry they would see for five attempts. The kind's
whole meaning is that the undo is retried, and here it is not.

**Is `attempts: 1` ever what someone means to write?** No — there is nothing it can express
that deleting the call does not express more clearly, and the method is named
`CompensationRetry`. The overwhelmingly likely intent is the ordinary off-by-one between
"attempts" and "retries": `attempts` is documented as *"how many times the undo may be
dispatched, including the first"*, so one retry is `attempts: 2`.

## Example that triggers it

```csharp
public static readonly PolicySet LedgerUndo = PolicySet.Named("ledger-undo")
    .CompensationRetry(attempts: 1);          // FLOWX1035

flow
    .Step<PostDebit>()
        .CompensateWith<ReverseDebit>()
        .WithPolicy(Policies.LedgerUndo)      // reported here
```

The report lands on the `WithPolicy` identifier, not on the set's declaration: the set may be
shared, and it is this application of it that publishes the promise.

**What stays silent:**

- `attempts: 2` and above — a real retry, and the one policy this runtime executes.
- An attempt count that is not a literal. A set whose count comes from configuration is
  exactly the case `FlowEmitter` copies verbatim rather than folding, and a rule that guessed
  would report on a value it cannot see. The same restriction [FLOWX1019](FLOWX1019.md)
  works under, for the same reason.
- A step with no compensation. That is [FLOWX1033](FLOWX1033.md)'s error, and it is the
  stronger statement: the declaration reaches no plan node at all, so how many attempts it
  asked for is not the interesting part.
- Any set the compiler cannot read — see [FLOWX1036](FLOWX1036.md).

## How to fix it

**Either raise the count, or delete the call.** Which one is right is a question about the
undo, and it has a default worth knowing: `docs/06-Execution-Engine.md` §7 rule 2 makes
compensation retry deliberately more aggressive than forward retry, at five attempts, and
`PolicySet.CompensationDefault` is that default as a named set.

```csharp
// The undo is worth insisting on — a reversal that gives up leaves money in one account only.
.CompensationRetry(attempts: 5)

// Or it genuinely is not, and one dispatch is the whole intention. Then say nothing:
// CompensationPolicy.None is one attempt, and the manifest stops promising a retry.
```

**Not by suppressing it and keeping the line.** Unlike the deleted `FLOWX1032` there is no
inventory to preserve: the declaration is not a record of a control a later release will
apply, because P4 will read the same `1` and dispatch once.

## When to suppress

None. There is no configuration, deployment or later release under which a single attempt
becomes a retry, and both repairs are one token in the file the diagnostic points at.

## Related

- [FLOWX1033](FLOWX1033.md) — `CompensationRetry` on a step with no compensation.
- `FLOWX1032` (deleted) — declared policy is not executed by the runtime.
- `docs/06-Execution-Engine.md` §7 rule 2 — the five-attempt default and why it is five.

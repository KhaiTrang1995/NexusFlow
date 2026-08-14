# FLOWX1052 — Fallback constant is not the step's output contract

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

`.Fallback(value)` captures the constant under `FlowContext.Set<TValue>`, which keys the state
bag by `typeof(TValue)`. A constant of any other type is filed under a key **no later step
binds** — so the flow survives the outage the fallback was declared for and then throws on the
first `ctx.Get<T>()` after it.

Reported at build time because a fallback fires exactly when a dependency is down, which is the
worst moment to discover that the degraded mode does not fit.

## Example that triggers it

```csharp
// rating.score produces a Rating
public sealed class ScoreRating : ICapability<RatingRequest, Rating> { … }

public static readonly PolicySet ExternalRead = PolicySet.Named("external-read")
    .Timeout(TimeSpan.FromSeconds(1))
    .Fallback(RatingScore.Unknown);          // a RatingScore, not a Rating

flow.Step<ScoreRating>().WithPolicy(Policies.ExternalRead)   // FLOWX1052
```

## How to fix it

```csharp
// Declare the constant as the capability's own output contract:
    .Fallback(new Rating(RatingScore.Unknown, Confidence: 0));
```

Or move the fallback to a set applied to the step that produces the type — one `PolicySet` can
be applied to several steps, and the constant only fits the ones whose output it is.

## When to suppress

None. The rule compares two types that are both written down in the source, so a false positive
would mean the step's capability was misread — in which case the step, not the policy, is what
needs looking at. There is no reading under which a degraded value the flow cannot read is what
the author meant.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

## What it does not report

**A set the compiler cannot read.** A `PolicySet` declared in a referenced assembly or built at
run time has no initialiser to walk, so the constant's type is not visible;
[FLOWX1036](FLOWX1036.md) reports the set itself, and this rule stays silent rather than
guessing.

**A capability-valued fallback**, because there is no syntax for one.
[ADR-0078](../adr/ADR-0078-stage-four-nests-six-kinds.md) §3 records what it is blocked on.

---

**Back to:** [diagnostics index](README.md) · [Policy framework §3](../10-Policy-Framework.md#3-the-policy-catalogue)

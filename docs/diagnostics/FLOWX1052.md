# FLOWX1052 — Fallback constant is not the step's output contract

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

`.Fallback(value)` captures the constant under `FlowContext.Set<TValue>`, which keys the state
bag by `typeof(TValue)`. A constant of any other type is filed under a key **no later step
binds** — so the flow survives the outage the fallback was declared for and then throws on the
first `ctx.Get<T>()` after it.

Reported at build time because a fallback fires exactly when a dependency is down, which is the
worst moment to discover that the degraded mode does not fit.

Since WP-80 the rule covers the other half of the row too. `.Fallback<TCapability>()` answers
with a second capability, and what that capability produces is filed under its own type by the
same `ctx.Set<T>` in the same generated dispatcher — so a fallback capability whose output
contract is not the step's is the identical defect, reported under the identical code. Only
where the type is read from differs: an argument's converted type for a constant, a declared
`ICapability<,>` output for a capability. The title says *constant* for the constant's sake;
the message names both contracts either way.

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

**A fallback capability whose output the reader could not resolve**, for the same reason: a
diagnostic raised on a guess names a type the author cannot find. *This paragraph read "a
capability-valued fallback, because there is no syntax for one" until WP-80 gave it one
([ADR-0079](../adr/ADR-0079-a-fallback-capability-is-a-dispatch-of-its-own.md)).*

---

**Back to:** [diagnostics index](README.md) · [Policy framework §3](../10-Policy-Framework.md#3-the-policy-catalogue)

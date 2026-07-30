# FLOWX1011 — Condition reads something outside the flow's state

> **Severity:** Warning in `Ephemeral` flows · **Error** in `Durable` flows · **Category:** FlowX · **Since:** 0.1.0

## What it means

A `When` condition is the only place a flow makes a decision, and the decision has to be a
function of what the flow knows. [08 §3.1](../08-Flow-Definition.md#31-conditional) states
the rule: a condition may read **only** `ctx.State`, `ctx.Input` and prior step results. A
condition that reads a clock, a static, a captured variable or an injected service is a
determinism violation — the same instance takes different paths on two runs, and in a
durable flow the replay diverges from the run it is replaying.

`FlowErrors.PredicateFailed` says the same thing at run time, and categorises it
`Internal` rather than transient, on the grounds that "a predicate is pure by
construction". This rule is what makes that sentence true.

**What the context is allowed to give you.** Everything reachable from the predicate's own
parameter is permitted — including `ctx.UtcNow`, `ctx.NewId()` and `ctx.Random`. That is
not a gap. [06 §5](../06-Execution-Engine.md#5-the-determinism-boundary) puts the clock,
identifiers and randomness in the *non-deterministic zone that is journaled and never
replayed*, and the context is the seam that makes them reproducible. Reporting `ctx.UtcNow`
would report the documented remedy.

### Why the severity depends on the profile

| Profile | Severity | Reason |
|---|---|---|
| `Durable` | **Error** | The flow is replayed. A branch that is not a function of flow state takes a different path on replay, and the journal and the execution disagree. |
| `Ephemeral` | **Warning** | Nothing is replayed, so nothing diverges — but the flow is one attribute away from being replayed, and the run-time error already treats an impure predicate as a defect under either profile. |

[ADR-0003](../adr/ADR-0003-execution-profiles.md) and [06 §5](../06-Execution-Engine.md#5-the-determinism-boundary)
originally said *informational* for the ephemeral case. It ships as a warning instead, for
one blunt reason: `Ephemeral` is the only profile the runtime executes today, and an Info
diagnostic never appears in a build log. Info would have shipped a rule that does nothing
anywhere — which is the state `FLOWX1011` was already in, and the state P1 exists to end.
The repository builds with `TreatWarningsAsErrors`, so in this codebase the warning does
stop the build; a consumer who disagrees can lower it in `.editorconfig`, which is a
decision written down rather than a default nobody chose.

## Example that triggers it

```csharp
[Flow("order.place", Profile = ExecutionProfile.Durable)]
public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
{
    private readonly IPricingService _pricing;   // injected

    protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
    {
        var cutoff = LoadCutoffFromConfig();

        flow.Step<ValidateOrder>()
            .When(ctx => DateTime.UtcNow.Hour < 17, day => day.Step<SameDayDispatch>())
            .When(ctx => ctx.Get<ValidatedOrder>().Total > cutoff, big => big.Step<RequireApproval>())
            .When(ctx => _pricing.IsPromotional(ctx.Input.Sku), promo => promo.Step<ApplyDiscount>())
            .Return(ctx => new OrderPlacedResult(...));
    }
}
```

```
error FLOWX1011: The condition in flow 'PlaceOrderFlow' reads 'DateTime.UtcNow', which is
                 the system clock; a condition may read only the flow context, the flow
                 input and prior step results
error FLOWX1011: The condition in flow 'PlaceOrderFlow' reads 'cutoff', which is a variable
                 captured from outside the condition; …
error FLOWX1011: The condition in flow 'PlaceOrderFlow' reads '_pricing', which is state
                 held on the flow instance rather than in the context; …
```

## How to fix it

```csharp
// A clock: read it through the context, which the journal reproduces on replay.
.When(ctx => ctx.UtcNow.Hour < 17, day => day.Step<SameDayDispatch>())
```

```csharp
// A captured value: make it a constant, so it is part of the flow's declared shape…
private const decimal ApprovalCutoff = 5_000m;
.When(ctx => ctx.Get<ValidatedOrder>().Total > ApprovalCutoff, big => big.Step<RequireApproval>())
```

```csharp
// …or, when it genuinely comes from outside, make a step fetch it and branch on the result.
// This is the fix for an injected service, always: a flow expresses order, condition and
// recovery — the lookup belongs in a capability, where it is named, testable and in the
// manifest.
.Step<LoadPricingRules>()
.When(ctx => ctx.Get<PricingRules>().IsPromotional(ctx.Input.Sku), promo => promo.Step<ApplyDiscount>())
```

Quick actions exist for the three mechanical cases — `DateTime.UtcNow`,
`DateTimeOffset.UtcNow`, `Guid.NewGuid()` and `Random.Shared` are rewritten onto the
context. Everything else needs a decision the compiler cannot make for you.

## What it detects

`PredicatePurityAnalyzer` reads the lambda passed as the first argument of
`IFlowBuilder<,>.When` — resolved semantically, so another library's `When` is not touched
— and traces every read to the root of its access chain. Two mechanisms, only one of which
is a proof:

**Scope, which is a proof.** The root of the chain is a symbol with a declaration, and
where that declaration sits decides the answer:

| Root | Verdict |
|---|---|
| The predicate's own context parameter, and anything reachable from it | allowed |
| A local, parameter or range variable declared inside the predicate — including inside a nested lambda such as `.Any(line => …)` | allowed |
| A `const` local, a `const` field, an enum member | allowed |
| A local or parameter declared **outside** the predicate | **reported** — captured variable |
| A field, property or event of the flow, through an implicit or explicit `this` | **reported** — flow instance state, and how an injected service gets in |
| A **static** field that is not `readonly`, or a static property with a setter | **reported** — mutable static state |

**A catalogue of known-impure statics, which is not a proof.** `DateTime.Now/UtcNow/Today`,
`DateTimeOffset.Now/UtcNow`, `Guid.NewGuid`, and every member of `Random`,
`RandomNumberGenerator`, `Environment`, `AppContext`, `AppDomain`, `Process`, `Thread`,
`Stopwatch`, `TimeProvider`, `Console`, `File`, `Directory`, `FileInfo`, `DirectoryInfo`,
`Dns` and `HttpClient` — reached statically, or constructed with `new`. This recognises
what teams actually reach for, in the same spirit and with the same admitted limit as
[FLOWX1003](FLOWX1003.md)'s transport list. **A clock that is not on this list is not
detected.**

### What it cannot catch

Stated rather than implied, because a rule that stops a build has to be honest about its
edges — and because a gate that fires on legitimate code is a gate people suppress:

- **Nothing is interprocedural.** `ctx.Get<Order>().IsStillOpen()` is accepted and its body
  may read a clock. This is by far the largest gap. Closing it needs a purity attribute or
  a whole-program analysis, not a longer list. A method of the flow itself — static or
  instance — is accepted for the same reason.
- **A predicate that is not a lambda is skipped.** A method group or a variable holding a
  `Func<FlowContext<TIn>, bool>` has no body at the call site.
- **`static readonly` is treated as constant, and is only shallowly so.** A
  `static readonly List<T>` is mutable state this rule permits.
- **A get-only static property is permitted.** A static helper's getter is far more often a
  constant than an ambient singleton, and reporting all of them would fire on valid code.
- **Reflection, `dynamic`, and anything that does not bind are skipped.** An unresolved
  symbol means the file does not compile, and that message is better than this one.
- **Only `When` is checked.** [06 §5](../06-Execution-Engine.md#5-the-determinism-boundary)
  puts the `Return` projection and the routing decisions in the same deterministic zone;
  `Return`, `Emit`, `EmitOnFailure`, `ForEach`'s selector and `Step<T, TIn>`'s mapping take
  context lambdas that are not analysed by this rule.

## When to suppress

The honest case is the interprocedural one in reverse: a helper the rule reports which is
in fact constant. A `static readonly` array that is genuinely never mutated is already
allowed, so what is left is narrow — most often a captured local that could simply be
`const`, in which case making it `const` is cheaper than the suppression.

If a value really does come from configuration and really cannot be a step, then the flow's
branch depends on something invisible to the manifest and to `flowx diff` as well as to
this rule. Suppress it only after deciding that is acceptable, and say so in the marker.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Flow definition §3.1](../08-Flow-Definition.md#31-conditional) · [Execution engine §5](../06-Execution-Engine.md#5-the-determinism-boundary)

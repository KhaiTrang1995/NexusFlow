# FLOWX1032 — Declared policy is not executed by the runtime

> **Severity:** Warning · **Category:** FlowX · **Since:** 0.1.0 · **Narrowed:** P4
> **Applies to:** the four policy kinds no code path applies — `RateLimit` (stage 1),
> `Idempotency` (stage 3), `Cache` (stage 5) and `Audit` (stage 7).
> **No longer applies to:** `Timeout`, `Retry`, `CircuitBreaker` and `Bulkhead`, which the
> policy engine now executes, or `CompensationRetry`, which the unwind has executed since
> WP-57.
> **Scheduled for deletion:** when the last four kinds execute — see
> [When this rule is deleted](#when-this-rule-is-deleted). This is scaffolding for the part
> of a phase that has not landed, not a rule about your code.

> [!NOTE]
> **This rule used to report eight kinds and now reports four.** `Timeout`, `Retry`,
> `CircuitBreaker` and `Bulkhead` left it when the policy engine landed
> `PolicyStage.Resilience`. If you are reading this because a `Retry` stopped being
> reported: it is not being ignored, it is being executed —
> [10 §5](../10-Policy-Framework.md#5-retry-safety) is now behaviour, and the `Idempotent = true`
> that [FLOWX1014](FLOWX1014.md) made you justify is now load-bearing rather than
> precautionary.
>
> **[FLOWX1014](FLOWX1014.md) and [FLOWX1018](FLOWX1018.md) are unaffected and are the ones
> that matter more than they did.** They check a declared policy's *safety precondition* and
> have always been real; the difference is that the duplicate charge FLOWX1014 prevents is
> now a thing that could actually happen.

## What it means

`.WithPolicy(Policies.PaymentGateway)` names a set of policies for a step. Four of the nine
kinds `PolicySet` offers reach the compiled plan and the manifest and are then read by no
code at run time.

`FlowEngine` reads four policy properties: `ExecutionPlan.HasStepPolicies` and
`StepNode.StepPolicy` in the step loop, and `ExecutionPlan.HasCompensationPolicies` and
`StepNode.CompensationRetry` on the failure path. Underneath them `PolicyChain.Ordered` is
read in exactly two places in the whole of `src/` — `StepPolicy.From` and
`CompensationPolicy.From` — and between them they read five kinds.

| What the DSL promises | What runs today |
|---|---|
| `.Timeout(d)` — caps how long one attempt may take | **Executes.** Armed per attempt, and clamped to what is left of the `[FlowDeadline]` when that is shorter |
| `.Retry(n)` — retries retryable failures | **Executes.** `n` attempts including the first, on the declared categories, full-jitter backoff, and never a wait that outlives the deadline |
| `.CircuitBreaker(...)` — stops calling a failing dependency | **Executes.** Keyed by capability id, per process. `minimumThroughput` is a constant rather than a parameter, and [10 §6](../10-Policy-Framework.md#6-circuit-breaker-scope)'s composite `BreakerKey` does not exist |
| `.Bulkhead(...)` — bounds concurrency | **Executes.** One pool per capability, refusing rather than queueing past `queueDepth` |
| `.CompensationRetry(n)` | **Executes.** See [FLOWX1033](FLOWX1033.md) for the one case where it does not |
| `.Cache(ttl)` — replays a recorded result | Nothing is cached or consulted |
| `.RateLimit(...)` — limits invocation rate | Nothing is counted |
| `.Idempotency(window)` — replays a recorded result for a repeated key | Nothing is recorded or replayed. `ctx.IdempotencyKey` is stable and is handed to the capability, but that is the engine's identity plumbing, not this policy |
| `.Audit(category, redact)` — writes an immutable audit record | No record is written by a policy |

**The line is a list of kinds, not a range of stages**, and that is the part that is easy to
get wrong — in both directions. `Audit` is a `PolicyStage.Consistency` policy, stage 7, the
same stage as `CompensationRetry`, which executes; and `RateLimit` at stage 1 is inert while
`Timeout` at stage 4 is not. No line drawn by stage number separates the two halves, so the
rule carries an enumerated set, `DeclaredPolicyAnalyzer.ExecutedKinds`, pinned against
`StepPolicy`'s and `CompensationPolicy`'s own constants by `PolicyStageFitnessTests`.

**Why stage 4 could be executed while stages 1, 3 and 5 were not** is
[ADR-0025](../adr/ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md), which
argues each skip separately rather than as one concession — including the one that looks
like a breach of [ADR-0011](../adr/ADR-0011-fixed-policy-stage-order.md)'s own
"retry outside idempotency → duplicate charges" row.

### What a declared policy still does

Not nothing — which is why the remedy below is *keep it*, and why this page is careful not
to say the declarations are worthless:

- **It is published.** Each kind and its fixed [ADR-0011](../adr/ADR-0011-fixed-policy-stage-order.md)
  stage reach the `policies` array of `flowx.manifest.json`, where a reviewer, a
  `flowx diff` and an agent can read what this step is *supposed* to be wrapped in.
- **It is carried into the plan.** `StepNode.Policies` holds the resolved, stage-ordered
  chain, which is the precondition for P4 executing it rather than P4 having to re-read the
  source.
- **It is validated.** `PolicyChain.ForStep` refuses a `Retry` on a capability that declares
  `Idempotent = false` and a `Cache` on one with side effects, throwing
  `InvalidFlowPlanException` when the plan is constructed. Those are FLOWX1014's and
  FLOWX1018's rules enforced a second time, at a second place.
- **It is read at build time by [FLOWX1019](FLOWX1019.md).** That rule multiplies a set's
  `Timeout` by its `Retry(attempts)` and checks the total against the flow's
  `[FlowDeadline]` — arithmetic that is now true of the running program rather than of a
  hypothetical one, because [ADR-0024](../adr/ADR-0024-stage-four-is-a-fixed-nesting.md) puts
  the retry outside the timeout.

What none of that amounts to, for the four kinds this rule still names, is **behaviour**. A
step declaring a one-hour cache calls the dependency every time.

## Example that triggers it

```csharp
public static class Policies
{
    public static readonly PolicySet Admission = PolicySet.Named("admission")
        .RateLimit(permits: 20, TimeSpan.FromSeconds(1))
        .Idempotency(TimeSpan.FromHours(24));

    // Silent: every kind in it is executed.
    public static readonly PolicySet ExternalRead = PolicySet.Named("external-read")
        .Timeout(TimeSpan.FromSeconds(3))
        .Retry(attempts: 3, Backoff.ExponentialJitter())
        .CircuitBreaker(failureRatio: 0.5, breakDuration: TimeSpan.FromSeconds(30));

    public static readonly PolicySet LedgerUndo = PolicySet.Named("ledger-undo")
        .CompensationRetry(attempts: 5);
}

[Flow("transfer.execute", Profile = ExecutionProfile.Durable, Owner = "payments")]
public sealed partial class ExecuteTransferFlow : Flow<ExecuteTransfer, TransferResult>
{
    protected override void Define(IFlowBuilder<ExecuteTransfer, TransferResult> flow) => flow
        .Step<ValidateTransfer>()
            .WithPolicy(Policies.Admission)       // FLOWX1032 — RateLimit, Idempotency
        .Step<ScreenSanctions>()
            .WithPolicy(Policies.ExternalRead)    // silent — all three kinds execute
        .Step<PostDebit>()
            .CompensateWith<ReverseDebit>()
            .WithPolicy(Policies.LedgerUndo)      // silent — CompensationRetry executes
        .Return(ctx => ctx.Get<TransferResult>());
}
```

One report per `.WithPolicy(...)` call, naming every inert kind in the set, on the
`WithPolicy` identifier itself. Not one report per policy: a set of eight would report eight
times on one line, which is how a catalogue gets suppressed wholesale.

**What stays silent**, so the rule's silence means something:

- A set whose every kind is executed — `ExternalRead` above, and any combination of
  `Timeout`, `Retry`, `CircuitBreaker`, `Bulkhead` and `CompensationRetry`.
- A set containing only `CompensationRetry`, like `LedgerUndo` above.
- A step with no `.WithPolicy(...)` at all.
- A `.WithPolicy(...)` whose argument the compiler cannot resolve to a field or property
  initialiser declared in source — a set built at run time, one returned by a method, or one
  arriving from a referenced assembly. `PolicySetReader` returns nothing rather than guessing,
  which is the same restriction FLOWX1014 and FLOWX1019 already work under, and it is exactly
  where `FlowEmitter.PolicyArguments` is silent too: a set whose kinds the compiler could not
  read produces no policy argument at all. A report naming policies the compiler inferred
  would name policies the author cannot find. **The silence itself is now reported, by
  [FLOWX1036](FLOWX1036.md)** — not as a claim about the kinds, which the compiler cannot
  make, but as the fact that none of them reaches the plan or the manifest.
- `PolicySet.CompensationDefault`, which is a referenced-assembly set and resolves anyway.
  *This page said the opposite for two releases, and it was true: the set named as the
  documented default for compensation reached no plan, so the one policy this runtime
  executes did not execute.* `PolicySetReader` now carries the composition of `PolicySet`'s
  own well-known sets, pinned by `PolicySetContentsAreThePinnedOnes`, so the set resolves to
  its `CompensationRetry` and this rule is silent on it for the ordinary reason: it is a
  compensation-only set.
- Every `.WithPolicy(...)` on a step that has a later one — see
  [FLOWX1034](FLOWX1034.md). The discarded set reaches neither the plan nor the manifest, and
  this rule's message says both carry the kinds it names.

## How to fix it

**Not by deleting the declaration.** That is the one edit which silences this rule, and it
would delete the record of what the step needs while changing nothing about how the step
runs — exactly the argument [FLOWX1028](FLOWX1028.md) makes for the execution profile. The
declarations are how P4 will find the flows that asked for a timeout.

In the order they should be considered:

1. **Confirm the flow is survivable with the policy unenforced, and record that.** For a
   large class of steps it is: a `RateLimit` whose real enforcement lives in the gateway in
   front of the process, an `Idempotency` window a caller already gets from an idempotent
   HTTP endpoint, a `Cache` on a read whose cost nobody is complaining about. If that is
   true here, keep the declaration and downgrade the rule — below.
2. **Move the control to where it is real.** A rate limit belongs in front of the process; a
   cache can be a dependency the capability holds; an audit record can be written by the
   capability itself. A control that exists in a manifest protects nothing.
3. **Do not ship this flow on this release**, if the step genuinely cannot run without the
   policy — a regulated write whose audit record is the reason it is allowed to happen.

There is no fix inside FlowX in this release, in the same sense as
[FLOWX1024](FLOWX1024.md) and [FLOWX1028](FLOWX1028.md), and in the sense `FLOWX1031` was in
until the feature it waited on arrived and it was deleted: the stage the declaration waits
on does not exist. **This rule ships with no code fix**, and
that is deliberate — every mechanical edit that clears it is the deletion the first
paragraph refuses.

## When to suppress

When you have taken option 1 above: the flow is survivable with these policies unenforced
until P4 lands, and somebody has said so.

Because this is a statement about a platform gap rather than about a line of code,
`.editorconfig` is the right place — one reviewable decision, scoped to the code that took
it:

```ini
# FLOWX-DEBT(payments, 2026-12-31): these flows declare a RateLimit and an Idempotency
#   window that no stage executes yet. The limit is enforced at the gateway and the
#   endpoint is already idempotent. Reviewed and accepted until stages 1 and 3 land.
[src/Flows/Payments/**.cs]
dotnet_diagnostic.FLOWX1032.severity = suggestion
```

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry — see
[21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A suppression without
one fails the build. Do not set it project-wide: the next step that declares a `Cache` it
believes in is the one that most needs to be told.

**Suppressing this rule does not suppress [FLOWX1014](FLOWX1014.md) or
[FLOWX1018](FLOWX1018.md).** Those are separate ids and separate rules, and they are the
ones that stop a duplicate charge. A `Retry` on a non-idempotent capability is still an
error whether or not you have accepted that nothing arms it.

## Why this is a warning and not an error

This catalogue's default is an error — *if a rule is worth having, it stops the build* — so
the warning is the thing that needs an argument. It is [FLOWX1028](FLOWX1028.md)'s, applied
to a declaration one level down from the profile, and both of its halves transfer:

**An error erases the inventory the fixing phase needs.** The only edit that silences an
error here is deleting the `.WithPolicy(...)` call or emptying the set. `.WithPolicy(...)`
visible in the code, in the manifest and in a rendered diagram is the list of the steps that
asked for a cache or a rate limit, and an error would systematically delete it — leaving the
work that implements stages 1, 3 and 5 with no flow declaring a policy to implement it for.
**This argument has now been paid off once**: the flows that declared a `Timeout` and a
`Retry` under this rule are the flows that got one, because the declarations were still
there.

**The source is not wrong; it is written correctly for a platform that has the feature.**
This is the half `FLOWX1031` explicitly could not use, and it is what puts this rule on
FLOWX1028's side of that line rather than on `AwaitSignal`'s. *No flow is
correct with a seven-day wait compiled to no wait* — but a great many flows are correct with
a `RateLimit` enforced at the gateway instead of in-process, or an `Idempotency` window the
transport already provides. FLOWX1028's first remedy — "confirm the flow is correct as it
is, and record that" — is an honest offer here, and option 1 above is it.

**Nothing is falsified, which is the line `FLOWX1031` drew for its error half.** That rule
was an error for `AwaitSignal` because the emitted plan carried `TimeSpan.FromHours(1)`, a
value no author wrote. Here the plan carries exactly the set the author declared, in exactly
ADR-0011's stage order. The plan says less about *behaviour* than a reader assumes and
nothing untrue about *declaration* — the category [FLOWX1027](FLOWX1027.md) occupies, at the
severity C# gives `CS0162`.

**`Warning` is not the lenient option.** This repository sets `TreatWarningsAsErrors`, so
the rule stops **this** build — its first finding was `samples/banking`, the reference saga,
seven times over. What the warning buys is the right unit of decision for a consumer: one
line in `.editorconfig`, in the repository that took the decision, instead of a rule nobody
can adopt incrementally.

**`Info` was not a candidate**, for the reason
[ADR-0003](../adr/ADR-0003-execution-profiles.md) has now been overruled on three times: an
`Info` diagnostic never appears in a build log, `dotnet build` does not print it and no gate
in [21-Quality-Gates](../21-Quality-Gates.md) notices it. Shipping this rule as `Info` would
ship a rule that does nothing — a precise description of the state it was written to end.

## Why this is not FLOWX1028, the deleted FLOWX1031, or FLOWX1014

Three neighbours, and the boundaries are worth stating because all four are about a gap
between what the source says and what the platform does.

| Rule | Asks |
|---|---|
| [FLOWX1028](FLOWX1028.md) | Is the flow's declared **execution profile** implemented? |
| `FLOWX1031` *(deleted — it can)* | Could the compiler compile a **suspension construct** into a plan at all? |
| **FLOWX1032** | Is the **stage** this policy runs in implemented? |
| [FLOWX1014](FLOWX1014.md) / [FLOWX1018](FLOWX1018.md) | Is a declared policy **safe** for the capability it wraps? |

FLOWX1032 presupposes the last row's answer is yes and asks the next question. The two are
not redundant and neither subsumes the other: a `Retry` on a non-idempotent capability is
FLOWX1014 *and* FLOWX1032, and it will still be FLOWX1014 on the day FLOWX1032 is deleted —
which is exactly the day the retry starts running and the duplicate charge becomes real.

## When this rule is deleted

**This diagnostic is deleted, not fixed.** It describes a gap in the platform, and a rule
that outlives what it describes is noise — and noise is what teaches people to suppress a
catalogue.

| Event | Action | Status |
|---|---|---|
| The policy engine lands `PolicyStage.Resilience` | Narrow the rule to the kinds still inert, rather than deleting it — the WP-52 / `FLOWX1028` precedent | **Done.** `Timeout`, `Retry`, `CircuitBreaker` and `Bulkhead` left the rule; `RateLimit`, `Idempotency`, `Cache` and `Audit` remain |
| Stage 1 executes | Drop `RateLimit` from `DeclaredPolicyAnalyzer.ExecutedKinds`' complement | Outstanding |
| Stage 3 executes | Drop `Idempotency` | Outstanding |
| Stage 5 executes | Drop `Cache` | Outstanding |
| Stage 7's `Audit` executes | Drop `Audit`, and then delete `FLOWX1032`, `DeclaredPolicyAnalyzer`, this page and the release-tracking row — nothing is left for it to report | Outstanding |

`PolicyExecutionTests` in `tests/FlowX.Runtime.Tests` is the executable half of this
reminder, and it works in both directions. `ACacheIsNotConsultedAndARateLimitCountsNothing`
and `AnAuditIsAStageSevenPolicyAndStillExecutesNowhere` assert on the real engine that the
four remaining kinds do nothing, and go red on the day one of them does;
`AZeroLengthTimeoutStopsTheStepBeforeItBegins` and the tests beside it assert that the four
that left really run, and go red if one silently stops. The narrowing above happened because
the first sort went red, which is what they are for.

---

**Back to:** [diagnostics index](README.md) · [FLOWX1033](FLOWX1033.md) ·
[FLOWX1014](FLOWX1014.md) · [FLOWX1028](FLOWX1028.md) ·
[Policy framework](../10-Policy-Framework.md) ·
[ADR-0011](../adr/ADR-0011-fixed-policy-stage-order.md)

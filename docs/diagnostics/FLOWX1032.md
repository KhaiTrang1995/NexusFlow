# FLOWX1032 — Declared policy is not executed by the runtime

> **Severity:** Warning · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** every policy kind a `.WithPolicy(...)` set declares **except**
> `CompensationRetry` — `RateLimit`, `Idempotency`, `Timeout`, `Retry`, `CircuitBreaker`,
> `Bulkhead`, `Cache` and `Audit`.
> **Scheduled for deletion:** when P4 lands the policy engine — see
> [When this rule is deleted](#when-this-rule-is-deleted). This is scaffolding for a phase
> that has not started, not a rule about your code.

> [!NOTE]
> **This is the other half of [FLOWX1014](FLOWX1014.md) and [FLOWX1018](FLOWX1018.md).**
> Those two check a declared policy's *safety precondition* and have always been real: they
> ran whether or not anything armed the policy, and they still do. This rule is what an
> author hears after taking their advice — the `Retry` you were made to justify against
> `Idempotent = true` is carried into the plan, published to the manifest, and armed by
> nothing.

## What it means

`.WithPolicy(Policies.PaymentGateway)` names a set of policies for a step. Every kind in
that set except one reaches the compiled plan and the manifest and is then read by no code
at run time.

**One policy executes, and it is `CompensationRetry`.** `FlowEngine` reads
`ExecutionPlan.HasCompensationPolicies` before it does any retry bookkeeping and then reads
`StepNode.CompensationRetry` on the failure path; those are the only two policy reads in the
engine. Underneath them, `PolicyChain.Ordered` is read in exactly one place in the whole of
`src/` — `CompensationPolicy.From`, which skips every descriptor whose kind is not
`CompensationRetry`. **`StepNode.Policies` — the chain that wraps the step itself — is read
nowhere at all.**

| What the DSL promises | What runs today |
|---|---|
| `.Timeout(d)` — caps how long one attempt may take | Nothing arms it. The flow's `[FlowDeadline]` is the only clock that bounds anything |
| `.Retry(n)` — retries retryable failures | Nothing retries. A failing step fails once and the flow unwinds |
| `.CircuitBreaker(...)` — stops calling a failing dependency | No breaker exists. Every call is dispatched |
| `.Bulkhead(...)` — bounds concurrency | Nothing counts. `ForEach`'s `MaxDegreeOfParallelism` is the only concurrency bound the runtime honours, and it is not a policy |
| `.Cache(ttl)` — replays a recorded result | Nothing is cached or consulted |
| `.RateLimit(...)` — limits invocation rate | Nothing is counted |
| `.Idempotency(window)` — replays a recorded result for a repeated key | Nothing is recorded or replayed. `ctx.IdempotencyKey` is stable and is handed to the capability, but that is the engine's identity plumbing, not this policy |
| `.Audit(category, redact)` — writes an immutable audit record | No record is written by a policy |
| `.CompensationRetry(n)` | **Executes.** See [FLOWX1033](FLOWX1033.md) for the one case where it does not |

**The line is by what a policy wraps, not by which stage it runs in**, and that is the part
that is easy to get wrong. `Audit` is a `PolicyStage.Consistency` policy — stage 7, the same
stage as `CompensationRetry` — and it is inert, because `PolicyChain.ForStep` moves only
`CompensationRetry` onto the compensation's chain and `CompensationPolicy.From` reads only
that kind. "Stages 1–6 do not run" is the wrong summary; "everything except
`CompensationRetry` does not run" is the right one.

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
  `[FlowDeadline]`. So a `Timeout` that arms nothing at run time can still fail your build,
  which is the one effect these declarations have today.

What none of that amounts to is **behaviour**. A step declaring a three-second timeout runs
for as long as the capability takes.

## Example that triggers it

```csharp
public static class Policies
{
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
        .Step<ScreenSanctions>()
            .WithPolicy(Policies.ExternalRead)   // FLOWX1032 — Timeout, Retry, CircuitBreaker
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

- A set containing only `CompensationRetry`, like `LedgerUndo` above, and
  `PolicySet.CompensationDefault`.
- A step with no `.WithPolicy(...)` at all.
- A `.WithPolicy(...)` whose argument the compiler cannot resolve to a field or property
  initialiser declared in source — a set built at run time, or one arriving from a
  referenced assembly. `PolicySetReader` returns nothing rather than guessing, which is the
  same restriction FLOWX1014 and FLOWX1019 already work under, and a report naming policies
  the compiler inferred would name policies the author cannot find.

## How to fix it

**Not by deleting the declaration.** That is the one edit which silences this rule, and it
would delete the record of what the step needs while changing nothing about how the step
runs — exactly the argument [FLOWX1028](FLOWX1028.md) makes for the execution profile. The
declarations are how P4 will find the flows that asked for a timeout.

In the order they should be considered:

1. **Confirm the flow is survivable with the policy unenforced, and record that.** For a
   large class of steps it is: a `Timeout` on a step inside a flow whose `[FlowDeadline]` is
   already tight, a `Retry` on a caller that retries the whole flow anyway, a `RateLimit`
   whose real enforcement lives in the gateway in front of the process. If that is true here,
   keep the declaration and downgrade the rule — below.
2. **Move the control to where it is real.** A rate limit belongs in front of the process; a
   timeout can be enforced inside the capability against the `CancellationToken` the engine
   passes; a cache can be a dependency the capability holds. A control that exists in a
   manifest protects nothing.
3. **Do not ship this flow on this release**, if the step genuinely cannot run without the
   policy — a capability that will hang forever without a timeout, in a flow whose deadline
   is long.

There is no fix inside FlowX in this release, in the same sense as
[FLOWX1024](FLOWX1024.md), [FLOWX1028](FLOWX1028.md) and [FLOWX1031](FLOWX1031.md): the
feature the declaration waits on does not exist. **This rule ships with no code fix**, and
that is deliberate — every mechanical edit that clears it is the deletion the first
paragraph refuses.

## When to suppress

When you have taken option 1 above: the flow is survivable with these policies unenforced
until P4 lands, and somebody has said so.

Because this is a statement about a platform gap rather than about a line of code,
`.editorconfig` is the right place — one reviewable decision, scoped to the code that took
it:

```ini
# FLOWX-DEBT(payments, 2026-12-31): these flows declare policies P4 will execute and
#   nothing executes today. The deadline bounds the flow; the rate limit is enforced at
#   the gateway. Reviewed and accepted until the policy engine lands.
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
visible in the code, in the manifest and in a rendered diagram is P4's list of the steps
that asked for a timeout, and an error would systematically delete it — leaving the phase
that implements the policy engine with no flow declaring a policy to implement it for.

**The source is not wrong; it is written correctly for a platform that has the feature.**
This is the half [FLOWX1031](FLOWX1031.md) explicitly could not use, and it is what puts
this rule on FLOWX1028's side of that line rather than on `AwaitSignal`'s. *No flow is
correct with a seven-day wait compiled to no wait* — but a great many flows are correct with
a `RateLimit` enforced at the gateway instead of in-process, or a `Timeout` subsumed by a
`[FlowDeadline]` that is already shorter. FLOWX1028's first remedy — "confirm the flow is
correct as it is, and record that" — is an honest offer here, and option 1 above is it.

**Nothing is falsified, which is the line FLOWX1031 drew for its error half.** That rule is
an error for `AwaitSignal` because the emitted plan carries `TimeSpan.FromHours(1)`, a value
no author wrote. Here the plan carries exactly the set the author declared, in exactly
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

## Why this is not FLOWX1028, FLOWX1031 or FLOWX1014

Three neighbours, and the boundaries are worth stating because all four are about a gap
between what the source says and what the platform does.

| Rule | Asks |
|---|---|
| [FLOWX1028](FLOWX1028.md) | Is the flow's declared **execution profile** implemented? |
| [FLOWX1031](FLOWX1031.md) | Can the compiler compile a **suspension construct** into a plan at all? |
| **FLOWX1032** | Will the runtime **apply** a policy the plan already carries correctly? |
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
| P4 lands the policy engine: stages 1–6 execute in ADR-0011's fixed order, over `StepNode.Policies` | Delete `FLOWX1032`, `DeclaredPolicyAnalyzer`, this page and the release-tracking row. Narrow it instead if only some kinds are implemented, exactly as WP-52 narrowed `FLOWX1028` rather than deleting it | Outstanding |

`PolicyExecutionTests.OnlyCompensationRetryIsExecutedAtRunTime` in
`tests/FlowX.Runtime.Tests` is the executable half of this reminder: it asserts on the real
engine that a step declaring `Timeout` and `Retry` is dispatched exactly once and takes as
long as the capability takes, and it goes red on the day a forward policy actually runs.

---

**Back to:** [diagnostics index](README.md) · [FLOWX1033](FLOWX1033.md) ·
[FLOWX1014](FLOWX1014.md) · [FLOWX1028](FLOWX1028.md) · [FLOWX1031](FLOWX1031.md) ·
[Policy framework](../10-Policy-Framework.md) ·
[ADR-0011](../adr/ADR-0011-fixed-policy-stage-order.md)

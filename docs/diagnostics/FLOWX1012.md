# FLOWX1012 — Compensation is declared on a flow that is not durable

> **Severity:** Warning · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** any flow containing `.CompensateWith<T>()` whose `Profile` is not
> `ExecutionProfile.Durable` — including a flow that names no profile, because
> `Ephemeral` is the default.
> **Code fix:** none, deliberately — see
> [Why there is no quick action](#why-there-is-no-quick-action).

> [!NOTE]
> **This id was reserved for two phases and deliberately left unraised.** Not because the
> check was hard — it is one predicate — but because its remedy was not true.
> `Profile = Durable` changed nothing at all while `FlowX.Runtime` read no profile; once
> WP-52 made the runtime read it, the remedy briefly changed something *worse* than
> nothing, because a durable flow with no journal is refused before its first step. WP-53
> and WP-55 gave a host a journal and a lease store to register. A rule whose fix is a lie
> is worse than an unraised id, which is why this one waited; the fix stopped being a lie,
> which is why it no longer does.

## What it means

The flow declares compensation — a `.CompensateWith<T>()` somewhere in its chain — and
runs under a profile that keeps the whole instance in the memory of one process.

Compensation on FlowX works by pushing each *successfully completed* compensable step onto
an unwind stack, and running that stack in reverse when a later step fails
([06 §7](../06-Execution-Engine.md)). On an `Ephemeral` flow that stack is a field of a
pooled in-memory context. It is not written anywhere. So:

| What happens | Ephemeral | Durable |
|---|---|---|
| A later step fails | the unwind runs | the unwind runs |
| The step's own attempt fails | the unwind runs | the unwind runs |
| The flow's deadline expires | the unwind runs | the unwind runs |
| **The process dies mid-flow — crash, deploy, scale-in, OOM kill** | **the instance is gone and the unwind never runs** | the recovery scan finds the instance, another node resumes it, and the completed compensable steps go back on the stack |

The last row is the whole rule. It is also the row that never shows up in a test, because
nothing in a test suite kills a process between two steps.

**What it costs in practice.** A compensation exists because a step took an effect that has
to be given back: a stock line reserved, a seat held, a card authorised, a quota consumed.
When the unwind is lost, that effect stays taken **and nothing anywhere records that it was
supposed to be released**. There is no dead letter, no failed-compensation metric, no
instance to inspect — the instance never existed outside one process's heap. The operator's
first evidence is inventory that does not add up.

### `flowx verify --cost` does not cover this, and several documents said it did

[ADR-0003](../adr/ADR-0003-execution-profiles.md) named `flowx verify --cost` as the
standing mitigation for "a wrong profile is a real bug class" and stated that it *"flags
exactly the accident `FLOWX1012` would have caught at build time — from the manifest rather
than the source, and after the build rather than during it"*. **That is not what the check
does.** Reading `src/FlowX.Cli/Verification/ProfileCostCheck.cs`:

```csharp
var durable = manifest.Flows
    .Where(flow => string.Equals(flow.Profile, DurableProfile, StringComparison.Ordinal))
```

It selects the flows whose manifest profile is `Durable` and reports the ones with no
compensation, no signal and no timer. **A compensable `Ephemeral` flow is never in the set
it examines**, and could not be: the finding it emits reads *"profile is Durable, with no
compensation, no signal and no timer"*, which is the opposite sentence.

So the verb catches the **expensive** half of a wrong profile — durability bought and not
used — and until this rule shipped, the **lossy** half had no check anywhere, at build time
or after it. The claim that a post-build report already covered it is what made the
reservation look cheaper to keep than it was.

The two are complementary rather than overlapping, which is why neither retires the other:

| | `flowx verify --cost` | `FLOWX1012` |
|---|---|---|
| Asks | is this flow paying for durability it does not use? | is this flow losing an unwind it declared? |
| Sees | one manifest, every flow in an application | one compilation, one flow's source |
| When | after the build, where somebody wired the command in | while the `.CompensateWith` is being typed |
| Could the other do it? | no — "nothing in this flow needs a journal" is a judgement across a whole application | no — the manifest records that a step *has* a compensation, but the decision being questioned is the profile the author defaulted into |

## Example that triggers it

```csharp
[Flow("order.place", Profile = ExecutionProfile.Ephemeral, Owner = "orders")]
//                   ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ FLOWX1012
public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
{
    protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) => flow
        .Step<ValidateOrder>()
        .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
        .Step<CapturePayment>()
        .Return(ctx => new OrderPlacedResult(/* … */));
}
```

The build succeeds today. `ReserveInventory` holds stock; if the node dies between the
reservation and the end of the flow, `ReleaseInventory` never runs and the hold stands
until someone notices.

**A flow that names no profile triggers it too**, and that is the common case:

```csharp
[Flow("order.place", Owner = "orders")]
// ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ FLOWX1012 — the default is Ephemeral
```

[ADR-0003](../adr/ADR-0003-execution-profiles.md) makes durability something you opt into,
so silence means `Ephemeral`. This is the one rule in the catalogue whose subject is the
default: every other profile rule is quiet about it and speaks up about a declaration.

**`Streaming` triggers it as well**, because the condition is *not `Durable`* rather than
*is `Ephemeral`*. `Streaming` has no engine and runs on the ephemeral one, so it loses a
pending compensation identically. Such a flow also gets [FLOWX1028](FLOWX1028.md); the two
say different things — that the profile buys nothing at all, and that this is one of the
specific things it costs.

**Silent:** a `Durable` flow with compensation; any flow without compensation, under any
profile; a `CompensateWith` on something that is not a FlowX builder (the call is resolved
semantically, not matched by name).

## How to fix it

### Declare the profile the saga needs

```csharp
[Flow("order.place", Profile = ExecutionProfile.Durable, Owner = "orders")]
```

and register the stores the profile requires, on the host:

```csharp
builder.Services.AddFlowX(options => options.ApplicationName = "Orders");
builder.Services.AddFlowXPostgres(connectionString);
```

**Both halves, or the fix is not one.** `FlowInvocation` refuses a `Durable` flow that was
started with no journal — `flow.durability_not_configured`, before its first step — rather
than running it ephemerally and pretending. That refusal is deliberate
([FLOWX1028](FLOWX1028.md) documents the silence it replaced), and it means the attribute
alone converts a lost compensation into a flow that does not start. Change the attribute
and the wiring in the same commit.

### What that buys, stated exactly

A durable flow commits one journal row per step boundary. When a node dies, the recovery
scan finds the instance, another node takes the lease and re-enters the same step loop; the
loop skips every step the journal shows completed, and **as it skips a completed compensable
step it puts that step back on the unwind stack**. A failure after the resume unwinds
everything the flow has done, including the parts a previous node did. That is the
guarantee this diagnostic is asking you to buy.

### What it does not buy — two limits that are still real

1. **The unwind is not itself journaled.** `06 §7` rule 4 says a crash *during* compensation
   resumes compensation; it does not, yet. `CompensateAsync` writes no journal row, so a
   process that dies halfway through an unwind still loses the rest of it. `Durable` moves
   the exposure window from "the whole flow" to "the unwind itself", which is much smaller
   and is not zero.
2. **A resumed parent does not rebuild a composed sub-flow's compensations.** The parent
   records a `SubFlow` step as one entry bound to the *child's* context, and that context
   died with the node. The engine skips the entry and deliberately does not approximate the
   child's stack, because a compensation stack that is silently short is the exact failure a
   saga exists to prevent. Rebuilding it from the child's own rows is WP-57.

Neither is a reason to stay ephemeral. Both are reasons not to read `Durable` as "solved".

### The other repair, and when it is the right one

Delete the compensation, and handle the effect some other way — a TTL on the reservation, a
nightly sweeper, an idempotent overwrite. This is a real answer for cheap, self-expiring
effects, and it is honest in a way that "keep the compensation and hope" is not: it says out
loud that nothing is going to undo the step.

It is the wrong answer whenever the effect is money, or is visible to a customer, or has no
natural expiry.

## Why there is no quick action

[FLOWX1017](FLOWX1017.md) has one — `AwaitSignalRequiresDurableCodeFixProvider` writes
`Profile = ExecutionProfile.Durable` — and the same edit would clear this rule. It is
deliberately not offered here, and the difference is the point.

For FLOWX1017 the source is **internally inconsistent**: an in-memory flow cannot suspend
for a signal at all, so the author's `AwaitSignal` and their profile cannot both be what
they meant, and the profile is the only one of the two a fix may change. The result compiles
*and works*.

Here the source is not inconsistent. A compensable ephemeral flow runs, and unwinds
correctly on every failure that is not a crash. What a one-click fix would produce is a flow
that **stops working**: durable, refused at run time with `flow.durability_not_configured`
until somebody registers a journal and a lease store, which is a change to a host the code
fix cannot see and may not even be in the same repository. Trading a build warning for a
start-up failure is the fix that silences the rule rather than the fix that is correct, and
that is the one thing a quick action may not be.

## When to suppress

When you have decided the flow stays ephemeral and know what that costs. Say which of the
two grounds it is:

- **The effect is cheap to leak, or something already reclaims it.** A hold with a TTL, a
  cache entry, a reservation a nightly job sweeps.
- **The crash window is acceptable for this operation** — a short flow, a low-value effect,
  and a documented manual recovery path.

Neither of those is technical debt. [`docs/DEBT.md`](../DEBT.md) draws exactly this line:
*"a documented trade-off recorded in an ADR is a decision, not debt… Ephemeral flows losing
state on a crash is ADR-0003."* So the suppression carries a **reason**, not a `FLOWX-DEBT`
id with an expiry — there is nothing to expire, because nobody intends to change it.

For one flow, at the declaration:

```csharp
#pragma warning disable FLOWX1012 // Deliberate: holds expire after 15 minutes; see ADR-0003
[Flow("cart.reserve", Profile = ExecutionProfile.Ephemeral, Owner = "checkout")]
#pragma warning restore FLOWX1012
```

The `SuppressionsAreAccountable` gate fails the build on a bare pragma with no same-line
reason. A sentence a reader can evaluate is the minimum.

Do not disable it project-wide. `Ephemeral` is the default profile, so a project-wide
downgrade is not a decision about your sagas — it is a decision to stop being told which of
your flows are sagas at all.

## Why this is a warning and not an error

Three reasons, and the first two are about truth rather than about adoption.

**The source is not wrong.** A compensable `Ephemeral` flow compensates correctly on every
failure that is not a crash. What it loses is one window, and losing it is a trade
[ADR-0003](../adr/ADR-0003-execution-profiles.md) ratified — the profile is a per-flow
decision precisely so that a flow may decline durability. An error would make a decision the
architecture record ratified inexpressible, which is not a compiler's job.

**The remedy has a prerequisite the compiler cannot see.** The fix is complete only when a
host registers a journal and a lease store. An error would stop a build until the author
made an edit whose correctness depends on a deployment fact no analyzer can check — and the
most likely response to that is `Profile = Ephemeral` again with the rule turned off
globally, which is strictly worse than the warning.

**`Ephemeral` is the default.** An error here breaks every compensable flow in every
codebase that has not already opted into durability, on the day it ships. A rule that cannot
be adopted incrementally is a rule that gets adopted by `.editorconfig` and then never read.

### Why not `Info`, which is what ADR-0003 originally implied for this family

An `Info` diagnostic never appears in a build log: `dotnet build` does not print it, MSBuild
does not fail on it, and no gate in [21-Quality-Gates](../21-Quality-Gates.md) notices it.
That argument sank `Info` for the determinism set at WP-58, and it is stronger here — those
rules at least escalate to errors on durable flows, whereas this one reports **only** on
flows that declined durability. An `Info` FLOWX1012 would be invisible in essentially every
build that could contain it, which is precisely the state the reserved id was already in.

### Why WP-58's escalation does not transfer

The determinism set ships *"Warning by default, Error where the compilation can prove the
code is on a durable flow's replay path"*. Read quickly, that looks like a house style this
rule should inherit. It cannot: **this rule reports because the flow is not durable.** Its
trigger and that escalation are mutually exclusive — there is no compilation in which
FLOWX1012 fires and that proof is available.

There is exactly one escalation a reader will propose: a `Durable` *parent* composing this
flow as a sub-flow, by the same transitive reasoning the determinism set uses for
capabilities. It is refused, and not for tidiness. A sub-flow's steps do run inside the
parent's instance — but the parent journals the composition as a single entry bound to the
child's context, and a resumed parent skips that entry **without rebuilding the child's
compensation stack** (see the second limit above; it is WP-57). Escalating on the parent's
profile would stop somebody's build on a guarantee the engine does not currently deliver.
`ADurableParentComposingThisFlowDoesNotEscalateIt` pins it.

### `Warning` is not the lenient reading

This repository sets `TreatWarningsAsErrors`, so the rule stops **this** build. It did, on
the first build after it was written, in the one place that mattered.

## The reference sample fires this rule

`samples/ecommerce/PlaceOrderFlow.cs` is a compensable saga on `Ephemeral`: it reserves
stock, compensates with a release, and then captures a payment. It is, almost word for word,
the flow ADR-0003's negative bullet describes when it says *"`Ephemeral` on a payment saga
loses work on deploy"*. It was the rule's first and only finding across `src/`, `plugins/`,
`samples/` and `tests/`, and it broke the build.

**The finding is a true positive and the sample keeps `Ephemeral` anyway.** The reasoning,
recorded here rather than left as a quiet pragma:

- The sample's value is that `dotnet run` serves an order with nothing behind it — CI
  publishes it *and then runs it*, because linking and serving a request are different
  facts. The only journal FlowX ships is PostgreSQL (`plugins/FlowX.Postgres`); there is no
  in-process store in `src/`.
- So `Profile = Durable` in the sample means the reference application cannot place an order
  without a database, and seven of its forty tests need one to pass. That is a real cost
  paid by every reader.
- And it would buy a crash-safe unwind for an inventory store that is a `Dictionary` and a
  payment gateway that always approves. The durability would be real and the thing being
  made durable would not.

A future in-process journal in `src/FlowX.Testing` would change this answer, and it is the
thing to watch for: at that point the sample can be durable and still start with no
infrastructure, and the pragma should go.

**What did change is the sample's account of itself.** Its README used to say the flow is
ephemeral because *"durable execution … is P2"* — a reason that expired when P2 landed. It
now says the flow is ephemeral because the sample chose to be runnable with nothing behind
it, and states what that costs. The claim
[06 §7](../06-Execution-Engine.md) made — *"no analyzer warns: FLOWX1012 does not exist and
never has"* — is likewise no longer true and has been rewritten.

---

**Back to:** [diagnostics index](README.md) ·
[Execution engine §7](../06-Execution-Engine.md) ·
[ADR-0003](../adr/ADR-0003-execution-profiles.md) ·
[FLOWX1017](FLOWX1017.md) · [FLOWX1028](FLOWX1028.md) · [DEBT register](../DEBT.md)

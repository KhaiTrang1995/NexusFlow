# FLOWX1007 — Time is read from the ambient clock rather than the context

> **Severity:** Warning · **Error** where the compilation shows the code on a durable flow's replay path · **Category:** FlowX · **Since:** 0.1.0

## What it means

[06 §5](../06-Execution-Engine.md#5-the-determinism-boundary) draws a line through a flow.
On one side is the deterministic zone, which a durable flow **replays**; on the other are
the clock, identifiers and randomness, which are **journaled and never replayed**.
`CapabilityContext` is the seam between them, and `ctx.UtcNow` is how a step crosses it:
the engine captures the value the first time a step reads it and writes it into that step's
journal row (`NondeterminismCapture.UtcNow`), so a replay is handed the same instant.

`DateTime.UtcNow` does not go through the seam. It is read afresh every time it is
evaluated — on the first run, on a retried attempt, and on a replay — so a value derived
from it differs between the run and the run that is meant to reproduce it.
[07 §3](../07-Capability-Model.md#3-rules) states the rule as capability rule 7,
"time/ID/randomness only via `ctx`", and its **Enforced by** cell said "—" until this rule
existed.

### Where it applies

| Subject | Covered by |
|---|---|
| A capability's members — `ExecuteAsync`, its helpers, its field initialisers | **this rule** |
| A flow's members that are not builder delegates — the statements in `Define`, a helper method, a field initialiser | **this rule** |
| The lambda passed to `When`, `Switch`, `ForEach`, `Return`, `Emit`, `EmitOnFailure`, `Step<,>` or `SubFlow<,>` | [FLOWX1011](FLOWX1011.md) |
| Any other type — an adapter, a repository, a background service | nothing, deliberately |

The last two rows are the point of the split. FLOWX1011 applies a **stronger** rule inside a
builder delegate — only the context, the flow input and prior step results — so a clock
there is already reported, with a message about conditions and projections. Reporting the
same span under two ids would make a reader choose which diagnostic to believe, so this
analyzer skips any node inside a delegate handed to a FlowX builder. Between them the two
rules partition the flow class rather than overlapping on it.

And a capability *is* in scope even though [06 §5](../06-Execution-Engine.md#5-the-determinism-boundary)
puts capability bodies in the non-deterministic zone. That zone is non-deterministic because
it is **journaled** — and what the journal records is exactly `ctx.UtcNow`, the ids
`ctx.NewId()` produced and the seed `ctx.Random` was built from. A value taken ambiently is
in none of those fields, which makes it precisely the part of the step a replay cannot
reconstruct.

### Why the severity is what it is

| Situation | Severity |
|---|---|
| A `Durable` flow, or a capability a `Durable` flow in this compilation steps through — directly or through a sub-flow it composes | **Error** |
| Anything else | **Warning** |

The reasoning is the determinism set's, not this row's, and it is written once on the
[index](README.md#the-severity-of-the-determinism-set). In short: `Info` is what
[ADR-0003](../adr/ADR-0003-execution-profiles.md)) asked for and never reaches a build log;
a `Warning` still stops the build in a repository that sets `TreatWarningsAsErrors`, which
this one does, while staying one `.editorconfig` line for a consumer who has decided
otherwise; and the escalation is a *proof* obligation rather than a guess, because a
capability has no profile of its own and a false error is worse than a missed one.

## Example that triggers it

```csharp
[Capability("order.validate", Version = "1.0.0", Authorization = Authorization.Internal)]
public sealed class ValidateOrder : ICapability<PlaceOrder, ValidatedOrder>
{
    public ValueTask<Result<ValidatedOrder>> ExecuteAsync(
        PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
    {
        var placed = DateTime.UtcNow;                       // the machine clock
        var deadline = placed.AddDays(1);

        return ValueTask.FromResult(Result.Ok(
            new ValidatedOrder(input.Sku, input.Quantity, placed, deadline)));
    }
}
```

```
warning FLOWX1007: 'ValidateOrder' reads 'DateTime.UtcNow', which is the ambient clock; a
                   replay reproduces only what the journal captured, and what it captures
                   is ctx.UtcNow
```

The same read inside a flow declared `Profile = ExecutionProfile.Durable`, or inside a
capability that flow steps through, is reported as an **error** instead.

## How to fix it

```csharp
// Read the clock through the context. The engine captures it into the step's journal row
// on first read and hands the same instant back on the way through again.
var placed = ctx.UtcNow;
var deadline = placed.AddDays(1);
```

```csharp
// In a flow, the same seam is on FlowContext<TIn>.
.When(ctx => ctx.UtcNow.Hour < 17, day => day.Step<SameDayDispatch>())
```

If what you need is a **duration** rather than an instant — how long a gateway call took —
measure it inside the capability and put it in telemetry, not in a value the flow carries.
A duration that never reaches the flow's state is not part of anything a replay has to
reproduce.

If the value genuinely has to come from outside the process — a business calendar, a
regulator's cut-off — that is a step, not a clock read: fetch it in a capability and let the
flow read the result, where it is named, testable and in the manifest.

## What it detects

`DeterminismAnalyzer` walks each member of a capability or flow declaration and classifies
every access chain rooted at a type name. The clocks it recognises are a catalogue shared
with [FLOWX1011](FLOWX1011.md), in `AmbientReads`:

`DateTime.Now`, `DateTime.UtcNow`, `DateTime.Today`, `DateTimeOffset.Now`,
`DateTimeOffset.UtcNow`, `Environment.TickCount`, `Environment.TickCount64`, and every
member of `Stopwatch` and `TimeProvider` — reached statically or constructed with `new`.

### What it cannot prove

Stated rather than implied, because a rule that stops a build has to be honest about its
edges, and because a gate that fires on legitimate code is a gate people suppress:

- **Nothing is interprocedural.** A capability that calls `_calendar.Today()` is accepted,
  and that method may read the machine clock. This is by far the largest gap, and closing it
  needs a purity attribute or a whole-program analysis, not a longer list.
- **The catalogue is a list, not a proof.** A clock that is not named above is not detected
  — the same admitted limit as [FLOWX1003](FLOWX1003.md)'s transport list. **The silence of
  this rule is never a statement that code is deterministic.**
- **An injected `TimeProvider` is not reported.** Only the static entry points are, because
  a `TimeProvider` handed to a capability may well be a test's fake — and reporting a
  constructor parameter would report the recommended way to make a clock substitutable in
  every other .NET codebase.
- **Only this compilation is visible.** A capability whose durable caller lives in a
  referenced assembly is reported as a warning rather than an error, because nothing here
  can see the flow that reaches it.
- **Reflection, `dynamic`, and anything that does not bind are skipped.** An unresolved
  symbol means the file does not compile, and that message is better than this one.

## When to suppress

The honest case is a capability that is genuinely never on a replay path and genuinely needs
wall time — a metrics sink, a health probe. The cheaper answer is usually still `ctx.UtcNow`,
which costs nothing and is testable; suppress only when the value must be the real clock
*and* must not be the journaled one, and say which in the marker.

`Stopwatch` in a capability that measures its own latency is the other legitimate case, and
it is worth asking first whether that measurement belongs in the platform's telemetry rather
than in the capability.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A suppression
without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Execution engine §5](../06-Execution-Engine.md#5-the-determinism-boundary) · [Capability model §3](../07-Capability-Model.md#3-rules)

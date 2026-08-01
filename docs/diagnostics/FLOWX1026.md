# FLOWX1026 — Sub-flow cannot be composed

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

A `.SubFlow(...)` call the compiler will not turn into a step. There are three causes and
they share one id, because from the author's point of view they are one sentence: *this
composition cannot become a step, and here is why.*

| Cause | Why |
|---|---|
| The target carries no `[Flow]` attribute | Nothing generates a plan for it, so there is no compiled flow to run |
| `SubFlowMode.AwaitCompletion` | It suspends the parent, and there is no suspension point to suspend into. *This cell said "no journal"; WP-52 built one, and a durable flow still runs to completion inside a single invocation — WP-63 is what adds suspension* |
| A mode the compiler cannot read | The mode decides the flow's semantics; it may not be guessed |

**An error rather than a warning, and that is the point.** All three would otherwise be
silent: the step would simply not be emitted, and the flow would ship missing the
composition its author wrote — absent from the plan, absent from the manifest, absent from
the diagram. A dropped step is the one outcome that needs no severity argument.

### Why `AwaitCompletion` does not ship

[08 §3.7](../08-Flow-Definition.md#37-sub-flows) documents three modes. Two are
implemented; this is the third.

`AwaitCompletion` means *the parent suspends until the child completes*. A suspension point
is durable by definition — the parent stops holding a thread, a lease and a context, and
something else resumes it later. That requires the journal, which is P2 work
([ADR-0003](../adr/ADR-0003-execution-profiles.md)), the same thing
[FLOWX1017](FLOWX1017.md) blocks `AwaitSignal` on.

The difference from `AwaitSignal` is why this is refused under **every** profile rather than
only under `Ephemeral`. An `AwaitSignal` step has an honest degenerate form: it is a step
that completes, and the plan carries it so the manifest tells the truth about the flow's
shape. A sub-flow has no such form. The two candidates are both lies:

- **Run it inline instead.** The parent would then hold its resources for the child's whole
  duration and share its deadline — the opposite of what `AwaitCompletion` was chosen for —
  and a child that outran the parent's budget would fail the parent, which
  `AwaitCompletion` explicitly does not do.
- **Skip it.** Business logic disappears silently.

So the mode stays in `SubFlowMode` — a diagnostic that names the reason is more use than a
member that vanished, and the enum is public surface (constraint C7) — and the compiler
refuses it until there is something real behind it.

### Why an unreadable mode is refused rather than copied

`Parallel`'s `merge:` and `ForEach`'s `options:` arguments are copied into the generated
plan **verbatim**, so a strategy chosen through a constant or a helper still executes
exactly as written; only the manifest's label is lost, and it is omitted rather than
guessed.

A mode is not that kind of argument. It is a two-member choice that decides the flow's
semantics: whether the parent waits, whether the child's failure is the parent's, and whose
deadline applies. A mode the compiler cannot read is a mode this rule cannot check, the
manifest cannot publish and a reviewer cannot see. So it is reported, and the fix is to
write `SubFlowMode.Inline` or `SubFlowMode.Detached` at the call site — which is where a
reader looks for it anyway.

## Example that triggers it

```csharp
[Flow("order.place")]
public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlaced>
{
    protected override void Define(IFlowBuilder<PlaceOrder, OrderPlaced> flow) => flow
        .Step<ValidateOrder>()
        .SubFlow<FulfilOrderFlow, FulfilOrder>(
            ctx => new FulfilOrder(ctx.Get<OrderId>()),
            SubFlowMode.AwaitCompletion)
        .Return(ctx => new OrderPlaced(...));
}
```

```
error FLOWX1026: The sub-flow step in flow 'PlaceOrderFlow' cannot be composed:
                 AwaitCompletion suspends the parent until the child completes, which needs
                 a durable suspension point, and there is no journal to suspend into in
                 this release
```

The second cause needs a type that derives from `Flow` but was never given an identity:

```csharp
public sealed partial class DraftFlow : Flow<Draft, Saved>   // no [Flow("…")]
{
    protected override void Define(IFlowBuilder<Draft, Saved> flow) => flow.Step<SaveDraft>();
}
```

```
error FLOWX1026: The sub-flow step in flow 'PlaceOrderFlow' cannot be composed:
                 'DraftFlow' carries no [Flow] attribute, so nothing generates a plan for it
                 and there is no compiled flow to run
```

**Composing something that is not a flow at all is not this rule's job** — it is an ordinary
C# error, because `SubFlow<TFlow, TSubIn>` constrains `TFlow` to `Flow`. `.SubFlow<CapturePayment, …>()`
fails to compile on the author's own line, which is a better diagnostic than anything this
catalogue could produce.

## How to fix it

**For `AwaitCompletion`, pick the mode you actually meant.** The choice is nearly always
one of these two, and they differ in exactly one respect — whether the parent's failure can
still undo the child's work:

```csharp
// Synchronous. The parent waits, shares its correlation and remaining budget, and the
// child's failure is the parent's. Work the child completed is undone when the parent
// later fails, in strict reverse, through the child's own compensations.
.SubFlow<FulfilOrderFlow, FulfilOrder>(ctx => new FulfilOrder(ctx.Get<OrderId>()))

// Fire-and-forget. The child gets its own context, its own deadline and its own lifecycle.
// The parent does not wait, does not hear about its failure, and does not undo it.
.SubFlow<NotifyPartnersFlow, PartnerNotice>(
    ctx => new PartnerNotice(ctx.Get<OrderId>()),
    SubFlowMode.Detached)
```

If what you needed was genuinely *"do not hold this thread for four hours"*, that is
`Detached` plus an event the parent can react to later — not a suspension the runtime
cannot honour.

**For a missing `[Flow]`, add one.** A flow's identity is what the manifest, `flowx diff`
and every trace key on:

```csharp
[Flow("order.draft")]
public sealed partial class DraftFlow : Flow<Draft, Saved> { … }
```

**For an unreadable mode, write the member.** Not a constant, not a helper, not a
conditional — the literal `SubFlowMode.Inline` or `SubFlowMode.Detached`.

## When to suppress

**Never, and suppressing it does not get you the step.** This is not a rule that refuses a
construct the compiler could have emitted anyway: in all three cases there is nothing to
emit. Suppressing the diagnostic leaves the flow compiling cleanly with the composition
missing from the plan, the manifest and the diagram — which is the failure the error
severity exists to prevent.

If `AwaitCompletion` is the semantics your flow genuinely needs, the thing to track is the
journal, not this rule. `docs/DEBT.md` is where that belongs.

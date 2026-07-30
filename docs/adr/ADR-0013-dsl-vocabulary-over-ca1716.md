# ADR-0013 — DSL vocabulary takes precedence over CA1716

> **Status:** Accepted · **Date:** 2026-07-30
> **Supersedes:** none · **Superseded by:** none

---

## Context

The first compilation of `FlowX.Abstractions` produced 29 analyzer errors. Fourteen
of them were **CA1716 — "Identifiers should not match keywords"**, firing on:

| Identifier | Where | Conflicts with |
|---|---|---|
| `Step` | `IFlowBuilder.Step<TCapability>()` | VB `Step` |
| `Return` | `IFlowBuilder.Return(...)` | VB `Return` |
| `When` | `IFlowBuilder.When(...)` | VB `When` |
| `then` | parameter of `When` | VB `Then` |
| `Error` | the `Error` record, `FlowContext.Error`, parameter of `Fail` | VB `Error` |
| `Get` / `Set` | `FlowContext.Get<T>()` / `Set<T>()` | VB `Get` / `Set` |

CA1716 exists so that a type authored in one .NET language can be consumed
comfortably from another — principally VB.NET, where these are statement
keywords and a consumer would need escaping syntax.

The list is not a coincidence. It is, almost exactly, **the FlowX DSL's entire
vocabulary**. These names appear in every sample, in all twenty specification
documents, and in the one code block on the README that has to make the platform
make sense in ten seconds:

```csharp
flow.Step<ValidateOrder>()
    .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
    .When(ctx => ctx.Input.IsExpress, then => then.Step<PriorityRouting>())
    .Return(ctx => new OrderPlacedResult(ctx.Get<OrderId>()));
```

## Decision

**Disable CA1716 repository-wide**, in `.editorconfig`, with a comment pointing
here.

## Rationale

The rule and this codebase optimise for different things, and the conflict is
not resolvable by compromise.

1. **The audience the rule protects does not exist here.** FlowX is C#-first by
   [constraint C1](../05-Architecture.md). No VB.NET or F# consumer story is
   designed, tested, sampled or documented. Paying a permanent readability cost
   for a hypothetical consumer is the wrong trade.
2. **The alternative names are all worse.** `ExecuteStep`, `ReturnValue`,
   `WhenCondition`, `GetValue` — each is longer, less precise, and reads as
   though it were named by a linter rather than by a designer. A fluent DSL's
   whole value is that it reads like the domain; `flow.ExecuteStep<T>()` does not.
3. **The names are already public contract.** They appear across the whole
   specification set. Renaming them here would make the code and its
   documentation disagree, which
   [constraint C8](../05-Architecture.md) forbids and which is a far more
   expensive defect than a cross-language ergonomics warning.
4. **Suppressing narrowly would be worse than suppressing globally.** Fourteen
   inline `[SuppressMessage]` attributes on the most-read type in the codebase
   would be visual noise on exactly the surface that must be pristine. One
   documented decision beats fourteen scattered apologies.

## Consequences

**Accepted:**
- A VB.NET consumer of `FlowX.Abstractions` must use bracket escaping
  (`[Step]`, `[Error]`). This is a documented, accepted limitation.
- The repository loses CA1716's protection everywhere, not just on the DSL. The
  risk is small: the rule's remaining value is for identifiers nobody would
  choose accidentally.

**Gained:**
- The DSL reads as designed, in code and in documentation, identically.

**Reversal condition:** if a first-class VB.NET or F# consumer story is ever
adopted as a goal, this ADR is superseded and the rule is re-enabled with
per-member suppressions on the DSL surface only.

## Why this is not technical debt

Per [21-Quality-Gates §6.3](../21-Quality-Gates.md#63-what-is-not-debt), a
documented trade-off recorded in an ADR is a **decision**, not debt. Nothing here
would be "fixed given time" — given infinite time, the same choice is made again.
It therefore carries no `DEBT-####` id and no expiry date, and it does not count
against the debt budget.

The distinction matters: if decisions were logged as debt, the register would
fill with things nobody intends to change, and the budget that makes the register
enforceable would become meaningless.

## Related

- The other 15 errors from the same build were **IDE0040** on interface members,
  caused by this repository's own `.editorconfig` setting
  `dotnet_style_require_accessibility_modifiers = always`. Interface members are
  implicitly public and no C# codebase writes the modifier out. That was a
  configuration defect, not a design question, and was fixed by changing the
  setting to `for_non_interface_members` — no ADR required.

---

**Back to:** [ADR index](README.md) · [Quality gates](../21-Quality-Gates.md)

# FLOWX1036 — Policy set cannot be read at compile time

> **Severity:** Warning · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** a `.WithPolicy(...)` argument the compiler cannot resolve to a set whose
> contents it can see — a set declared in a referenced assembly, returned by a method, or
> assembled at run time.

> [!NOTE]
> **This is the silence the other policy rules are built on, said out loud.**
> [FLOWX1032](FLOWX1032.md), [FLOWX1033](FLOWX1033.md), [FLOWX1014](FLOWX1014.md),
> [FLOWX1018](FLOWX1018.md) and [FLOWX1019](FLOWX1019.md) all ask a question about what is
> *in* a set, and all five stay quiet when the compiler cannot read one. So does
> `FlowEmitter`, and so does `ManifestWriter`. Five rules and two artifacts being quiet
> together is not a policy that is unchecked; it is a policy that does not exist.

## What it means

`PolicySetReader` resolves the argument to its declaration and walks the fluent chain that
built it. That needs the chain, and a chain is syntax:

```csharp
public static readonly PolicySet Ledger = PolicySet.Named("ledger")
    .Timeout(TimeSpan.FromSeconds(5));
```

A symbol from a **referenced assembly** has no `DeclaringSyntaxReferences` — the initialiser
was compiled to IL in another build, and Roslyn does not read IL. A set returned by a method,
held in a local, or chosen by a conditional has no single initialiser to read either. In all
of those cases the reader returns nothing rather than guessing, and everything downstream
follows:

- `FlowEmitter.PolicyArguments` emits no `PolicyChain` at all, so the plan node carries none
  and `ExecutionPlan.HasCompensationPolicies` stays false. **A `CompensationRetry` in such a
  set — the one policy this runtime executes — does not run.**
- `ManifestWriter.WritePolicies` writes no `policies` array, so the published contract shows
  a step with no policies while the source declares some, and `flowx diff` compares nothing.
- FLOWX1014 does not ask whether a `Retry` in the set is safe for the capability it wraps.
  That rule is documented as what prevents a duplicate charge.

Until this rule existed, every one of those was silent together.

## The one referenced-assembly set that does resolve

`PolicySet.CompensationDefault` is declared by FlowX itself, in `FlowX.Abstractions`, and it
reaches every consuming compilation as metadata like any other. It resolves anyway:
`PolicySetReader` carries the composition of `PolicySet`'s own well-known sets, because the
compiler ships alongside the assembly that declares them and their composition is part of
FlowX's published surface rather than something to infer. `PolicySetContentsAreThePinnedOnes`
reads the real `PolicySet.CompensationDefault.Policies` by reflection and fails the build if
the two ever disagree, and it also fails if `PolicySet` grows a well-known set the compiler
has not been told about — the same arrangement `ManifestWriter.KnownPolicyStages` and
`PolicyStagesMatchTheAbstraction` already use for the stage table.

That is the whole of the exception. It does not extend to a policy library of your own,
because nothing pins your set's composition to the compiler that has to read it.

## Example that triggers it

```csharp
// In a shared library, referenced by the flow's project:
namespace Shared;
public static class Policies
{
    public static readonly PolicySet Ledger = PolicySet.Named("ledger")
        .Timeout(TimeSpan.FromSeconds(5))
        .CompensationRetry(attempts: 5);
}

// In the flow:
flow
    .Step<PostDebit>()
        .CompensateWith<ReverseDebit>()
        .WithPolicy(Shared.Policies.Ledger)   // FLOWX1036 — the retry does not run

    .Step<ScreenSanctions>()
        .WithPolicy(Policies.Build())         // FLOWX1036 — built at run time
```

The report lands on the `WithPolicy` identifier and names the expression as written.

**What stays silent:**

- A set declared as a field or property initialiser anywhere in the compilation being built,
  including in another file — that is the ordinary case and the one `samples/banking` uses.
- `PolicySet.CompensationDefault`, and any other well-known set `PolicySet` itself declares.
- An argument that does not bind to a `PolicySet` at all. The compiler already reports that,
  and a second message about a half-typed expression is noise.

## How to fix it

In the order they should be considered:

1. **Move the declaration into the assembly that declares the flow.** A `PolicySet` is a
   static field and a fluent chain; the cost of a copy is a few lines, and it buys the plan,
   the manifest and all five rules back. If several projects need one set, the honest shape
   today is a shared *source* file — a linked `.cs`, or a source-only package — rather than a
   compiled reference.
2. **Use `PolicySet.CompensationDefault`** where the only policy that has to run today is the
   compensation retry. It resolves from metadata, it is the default
   `docs/06-Execution-Engine.md` §7 rule 2 documents, and it is five attempts.
3. **Do not assemble a set at run time and hand it to `.WithPolicy(...)`.** Composition is
   resolved at compile time by design — `PolicySet`'s own summary says configuration can
   change a timeout from 2 s to 3 s and can never add, remove or reorder a policy, because
   that would change the graph. A set built by a method is asking the DSL for something it
   does not offer, and gets nothing.

## When to suppress

**Only with the first repair ruled out and written down.** A suppression here does not make
the policies apply; it makes a step whose declared controls reach no plan, no manifest and no
safety rule look like a step with no controls declared. If the set contains nothing but kinds
FLOWX1032 would report as inert anyway, that is a defensible position for this release and an
indefensible one on the day P4 lands — record it with a `FLOWX-DEBT` marker rather than a
bare `#pragma`.

## Related

- [FLOWX1032](FLOWX1032.md) — declared policy is not executed by the runtime.
- [FLOWX1014](FLOWX1014.md) — retry requires an idempotent capability; the rule this silence
  disables.
- [FLOWX1019](FLOWX1019.md) — flow deadline shorter than the step timeouts it must contain.

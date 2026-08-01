# FLOWX1006 — State-bag contract is outside every generated JSON context

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

A `Durable` flow journals its state bag and every step result. A value reaches the journal
only as a `JournalPayload`, and `JournalPayload.Of<T>` requires the source-generated
`JsonTypeInfo<T>` for the contract — there is no overload that reflects over a type. That
requirement is what keeps the write path NativeAOT- and trim-safe (constraint **C2**) and
what [ADR-0008](../adr/ADR-0008-serialization-and-schema.md) chose.

So a contract that no `JsonSerializerContext` in the compilation declares is a contract the
generated payload writer cannot name metadata for. This rule reports that at build time,
naming the member, rather than letting the flow ship with a journal that silently records
nothing for it — which is the outcome ADR-0008's Negative consequences describe as *"friction
at the right moment"*.

**What counts as a state-bag member:** the flow's input contract, which the engine puts in
the bag before the first step, and the output contract of every capability step, which the
dispatcher writes back with `ctx.Set`. Those are exactly the types a resumed instance has to
rehydrate, and a bag it cannot rehydrate is a flow that resumes running the rest of itself
against values no step produced.

**Two contexts declaring the same contract is the same answer as none.** Picking the first of
several would make the stored shape depend on file order. That is the rule
`EndpointEmitter` and `FLOWX1024` already follow, for the same reason.

**It reports on `Durable` flows only.** An `Ephemeral` flow keeps no journal, so nothing
serialises its bag and there is nothing for the rule to protect. That also makes this the one
member of the determinism set whose severity is not split: the set's rule is *"Warning by
default, Error where the compilation can prove the code is on a durable flow's replay path"*,
and this rule's trigger **is** that proof, so it is an Error uniformly. See
[the severity section](README.md#the-severity-of-the-determinism-set).

## Example that triggers it

```csharp
[Flow("transfer.execute", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
public sealed partial class ExecuteTransferFlow : Flow<ExecuteTransfer, TransferResult>
{
    protected override void Define(IFlowBuilder<ExecuteTransfer, TransferResult> flow) => flow
        .Step<ValidateTransfer>()        // produces ValidatedTransfer
        .Step<PostDebit, DebitInstruction>(/* … */);
}

[JsonSerializable(typeof(ExecuteTransfer))]
[JsonSerializable(typeof(TransferResult))]
public partial class BankingJsonContext : JsonSerializerContext;   // no ValidatedTransfer
```

```
error FLOWX1006: 'ValidatedTransfer' is written into the state bag of durable flow
'transfer.execute' and no single source-generated JsonSerializerContext in this compilation
declares [JsonSerializable(typeof(Banking.ValidatedTransfer))], so the journal cannot record
it without reflection — add it to one
```

## How to fix it

Declare the contract on the context the rest of the flow already uses:

```csharp
[JsonSerializable(typeof(ExecuteTransfer))]
[JsonSerializable(typeof(TransferResult))]
[JsonSerializable(typeof(ValidatedTransfer))]      // the fix
public partial class BankingJsonContext : JsonSerializerContext;
```

One attribute per contract, and the compiler names the missing one in the message, so the
list is read off the build rather than derived by hand.

If **two** contexts declare it, remove it from one. The rule is about there being exactly one
answer to "which metadata writes this", not about there being at least one.

Three alternatives, and why each is worse:

- **Make the flow `Ephemeral`.** It silences the rule by removing the journal, which is the
  fix that silences rather than the fix that is correct — the same trade
  [FLOWX1012](FLOWX1012.md) refuses to offer as a code fix.
- **Serialise by reflection.** There is no such overload, deliberately: it would break
  NativeAOT and trimming, which constraint C2 makes non-negotiable.
- **Journal nothing for that member.** That is what the build did before WP-59, and it is why
  a resumed instance re-entered with an empty bag.

## When to suppress

**Effectively never, and a suppression does not do what it looks like.** Silencing this rule
does not give the flow a serialiser; it gives it a journal with a hole where one contract's
values should be. The instance still commits its step boundaries, so a resume still happens —
against a bag missing that member, which is the failure this rule exists to move from run time
to build time.

The one honest use is a temporary one, on a flow being converted to `Durable` in stages, with
the reason written down:

```csharp
#pragma warning disable FLOWX1006 // Converting to Durable in stages; ValidatedTransfer joins
                                  // BankingJsonContext in the same PR as the resume test.
```

There is no `.editorconfig` stance worth taking here. Turning the rule off repository-wide
would mean every durable flow's journal is allowed to be incomplete, which is a decision about
recovery rather than about a diagnostic.

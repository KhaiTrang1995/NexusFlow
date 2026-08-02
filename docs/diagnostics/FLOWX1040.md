# FLOWX1040 — `Idempotency` is declared on a flow whose result cannot be recorded without redaction

> **Severity:** Error · **Category:** FlowX · **Since:** P4
> **Applies to:** a `.WithPolicy(...)` whose set declares an `Idempotency`, on a flow whose
> input or output contract declares a `[Sensitive]` member.
> **Scheduled for deletion:** when a read path for `[Sensitive]` values exists — see
> [When this rule is deleted](#when-this-rule-is-deleted).

> [!IMPORTANT]
> **This rule exists because the alternative is a wrong answer that looks like a right one.**
> Not a slower flow, not an unenforced policy — a `200` carrying a value nobody computed. Read
> [the worked example](#what-would-happen-without-this-rule) before deciding this is
> bureaucracy.

## What it means

Stage 3 executes. A step declaring `.Idempotency(window)` records what it produced, and a later
caller presenting the same `ctx.IdempotencyKey` gets that record back instead of a second
dispatch.

**What is recorded goes through `JournalPayload`, and `JournalPayload` has exactly one exit.**
`ToJson()` replaces every member whose name appears in the flow's `SensitiveMembers` with the
literal string `[redacted]` — case-insensitively, at every depth — and there is no accessor for
the value, deliberately, so no store can reach around it. `SensitiveMembers` is generated from
the `[Sensitive]` markers on the flow's **input and output contracts**, and applies to every
document that flow writes.

So on a flow that marks any member, the recorded result is not what the flow produced. Replaying
it would hand the values back with the placeholder in place of the real ones, and
`IStepDispatcher.RestoreState` says why nothing can undo that: *"sensitive members already
redacted, because there is no read path that could put them back."*

## What would happen without this rule

`samples/banking` is the worked example, and it is the reason this rule is an error.

```csharp
public sealed record ExecuteTransfer(
    [property: Sensitive] string DebtorIban,      // ← marked
    [property: Sensitive] string CreditorIban,    // ← marked
    decimal Amount, string Currency, TransferChannel Channel);

public sealed record ValidatedTransfer(
    string DebtorIban, string CreditorIban,       // ← same names, one level down
    decimal Amount, string Currency, TransferChannel Channel, decimal AvailableBalance);
```

1. `ValidateTransfer` produces a `ValidatedTransfer` holding the debtor's real IBAN.
2. `ExecuteTransferFlow.SensitiveMembers` is `["CreditorIban", "DebtorIban"]`.
3. The recorded document holds `"DebtorIban": "[redacted]"` — matched **by name**, so a contract
   that never carried the attribute is redacted anyway.
4. A second caller presents the same idempotency key. The record is replayed.
   `ctx.Get<ValidatedTransfer>().DebtorIban` answers `"[redacted]"`.
5. `PostDebit` posts a debit against account `[redacted]`. The flow returns `200`.

The first caller's transfer settled. The second caller's transfer reports success, moved money
to a placeholder, and **every step succeeded**, so nothing is logged, nothing is counted and no
alert fires. That is worse than having no idempotency at all: without it, step 5 would have
called an idempotent capability with the real IBAN and got the right answer for the second time.

## Example that triggers it

```csharp
public static class Policies
{
    public static readonly PolicySet Admission = PolicySet.Named("transfer-admission")
        .RateLimit(permits: 20, TimeSpan.FromSeconds(1), RateLimitScope.Principal)
        .Idempotency(TimeSpan.FromHours(24));    // ← FLOWX1040
}

[Flow("transfer.execute", Profile = ExecutionProfile.Durable)]
public sealed partial class ExecuteTransferFlow : Flow<ExecuteTransfer, TransferResult>
{
    protected override void Define(IFlowBuilder<ExecuteTransfer, TransferResult> flow) => flow
        .Step<ValidateTransfer>().WithPolicy(Policies.Admission)   // ← reported here
        .Return(ctx => ctx.Get<TransferResult>());
}
```

One report per `.WithPolicy(...)` call, on the `WithPolicy` identifier itself, naming the marked
member that makes the flow unrecordable — so the message points at both halves of the conflict.

**What stays silent**, so the rule's silence means something:

- A flow whose input and output contracts mark nothing. Marks on *intermediate* contracts do not
  count, because `SensitiveMembers` is not read off them —
  `samples/banking`'s `TransferCompleted` is the documented case.
- A set declaring a `RateLimit` and no `Idempotency`. Stage 1 records nothing, so redaction
  cannot reach it.
- A `.WithPolicy(...)` the compiler cannot resolve — a set in a referenced assembly, one built
  at run time. That is [FLOWX1036](FLOWX1036.md)'s subject and this rule inherits its silence,
  which is exactly why the runtime guard below is not optional.
- Every `.WithPolicy(...)` on a step that has a later one, per [FLOWX1034](FLOWX1034.md): the
  discarded set reaches neither the plan nor the manifest.

## How to fix it

In the order they should be considered:

1. **Remove the `Idempotency` from the set, and say why.** For most flows this is right: the
   window was aspirational, the transport already demands an idempotency key, and the duplicate
   the author was worried about is the one [FLOWX1014](FLOWX1014.md) and the stable
   `ctx.IdempotencyKey` already hold shut —
   [ADR-0025 §2.2](../adr/ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md)
   is that argument in full, and it is unchanged by stage 3 landing.
2. **Take the `[Sensitive]` marker off, if the member is not actually sensitive.** Check what
   else the marker is doing first: it strips the member from RFC 7807 error bodies, from journal
   rows and from emitted events, and those are usually the reasons it is there.
3. **Move the marked member off the flow's input and output contracts.** A flow whose input is
   an opaque reference — a token the first step exchanges for the real values — records nothing
   that has to be redacted, and the marked member then lives on an intermediate contract where
   `SensitiveMembers` does not read it. This is a real design and it is more work than the other
   two.

**There is no suppression that makes this safe.** Suppressing the rule does not remove the
guard: `JournalPayload.TryToReplayableJson` refuses to emit a document the redaction pass
touched, so the step fails at run time with `policy.idempotency_not_replayable` — after its
capability has been dispatched, on the flow's first execution, with the completed compensable
steps unwinding behind it. The rule is the early warning; the refusal is the guarantee.

## Why this is an error and not a warning

The deleted `FLOWX1032` was a warning because *"the source is not wrong; it is written
correctly for a platform that has the feature"* — a `RateLimit` enforced at the gateway is a
correct program. That argument does not transfer, and the reason is
[ADR-0030](../adr/ADR-0030-policy-stance-is-refused-at-build-time.md)'s:

- **Nothing about this flow becomes correct later.** The platform has the feature. The
  declaration cannot be served *for this flow*, and no release changes that except one that
  gives `[Sensitive]` a read path — at which point the rule is deleted rather than downgraded.
- **The alternative is not a policy that does less.** It is a step that fails at run time on its
  first execution. A build error is the earliest honest moment to say the same thing.
- **There is a fix.** Unlike FLOWX1032, whose only silencing edit was the deletion its own page
  refuses, every remedy above leaves the program working.

## Why this is not FLOWX1014, FLOWX1018 or the retired FLOWX1032

| Rule | Asks |
|---|---|
| `FLOWX1032` (deleted) | Was the **stage** this policy runs in implemented? |
| [FLOWX1014](FLOWX1014.md) | Does the **capability** tolerate being called twice? |
| [FLOWX1018](FLOWX1018.md) | Does the **capability** have side effects a cache would corrupt? |
| **FLOWX1040** | Can the **platform record** what this flow produced, without changing it? |

The three neighbours are all questions about the declaration's subject. This one is a question
about the platform's ability to serve it, and it is the only one of the four whose answer
depends on a contract the policy does not mention.

A step could be FLOWX1040 and FLOWX1032 at once — an `Idempotency` and a `Cache` in one set — and
the two report separately, because they are different facts and have different expiry dates.

## When this rule is deleted

| Event | Action | Status |
|---|---|---|
| A read path for `[Sensitive]` values ships — envelope encryption, a key-management plugin — so a recorded value can be restored as itself | Delete this rule, `JournalPayload.TryToReplayableJson`, the runtime guard and this page. [ADR-0042](../adr/ADR-0042-a-recorded-result-is-replayed-only-when-recording-lost-nothing.md) re-opens, along with the durable-resume loss its §1.3 distinguishes itself from | Outstanding |
| `SensitiveMembers` stops being flow-wide | Narrow the rule to the steps whose results a marked name can reach. ADR-0042 §1.4's last rejection has to be re-argued first — a traversal that is wrong in the permissive direction ships the worked example above, silently | Outstanding |

`IdempotencyReplayTests.ARedactedResultIsNeverRecorded` in `tests/FlowX.Runtime.Tests` is the
executable half of this page: it drives a flow whose state bag carries a marked member through a
real engine and a real store, and asserts that the step fails rather than recording. It goes red
on the day the guard is removed, whether or not this rule is still raised.

---

**Back to:** [diagnostics index](README.md) ·
[FLOWX1014](FLOWX1014.md) · [FLOWX1036](FLOWX1036.md) ·
[Policy framework](../10-Policy-Framework.md) ·
[ADR-0042](../adr/ADR-0042-a-recorded-result-is-replayed-only-when-recording-lost-nothing.md)

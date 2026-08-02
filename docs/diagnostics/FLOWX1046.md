# FLOWX1046 — Agent tool declares no confirmation over declared side effects

> **Severity:** Warning · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** a flow carrying `[AgentTrigger(Confirmation = ConfirmationMode.Never)]`.

## What it means

A tool descriptor's `confirmationRequired` is not a declaration. It is computed, and the rule is
one line:

| Declared `ConfirmationMode` | `confirmationRequired` |
|---|---|
| `RequiredForSideEffects` *(the attribute's default)* | true exactly while some capability the flow's steps reach declares a `SideEffects` entry |
| `Always` | true |
| `Never` | **false, whatever the flow does** |

So a flow that reaches `payment.refund` and declares `Never` publishes a tool descriptor saying no
human is needed. Every client that trusts the annotation — which is what the annotation is for —
will call it without prompting, and a server configured with `ConfirmationPolicy.Elicit`
([ADR-0060](../adr/ADR-0060-the-server-asks-the-caller-for-what-it-does-not-have.md)) elicits
nothing, because it elicits for the tools whose descriptor asks for it.

The declaration is legal, the build succeeds, and the consequence is invisible in the file that
contains it: the trigger says how much consent is wanted and the *capabilities* say what there is
to consent to, and those are two files written by two people. That is what this rule is for.

## Example that triggers it

```csharp
[Flow("ticket.refund", Version = "1.0.0")]
[AgentTrigger(
    Description = "Issue a refund against a support ticket.",
    Confirmation = ConfirmationMode.Never)]
//  ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ reported here
public sealed partial class IssueRefundFlow : Flow<IssueRefund, RefundIssued>
{
    protected override void Define(IFlowBuilder<IssueRefund, RefundIssued> flow) => flow
        .Step<LoadTicket>()
        .Step<RefundPayment>()   // [Capability(SideEffects = ["payment-gateway", "ledger"])]
        .Return(ctx => new RefundIssued(/* … */));
}
```

## How to fix it

Delete the argument.

```csharp
[AgentTrigger(Description = "Issue a refund against a support ticket.")]
```

`RequiredForSideEffects` is the attribute's default and is the answer that stays correct: it is
true exactly while the flow has a declared consequence, and it becomes false on its own the day the
consequence is removed. Writing `Never` freezes the answer against a flow that will keep changing.

## When to suppress

**When the declared effects genuinely do not warrant a human**, and this is a real case rather than
a courtesy. A side effect is any declared consequence outside the process: a cache write, a
search-index update and a metrics push are all side effects, and prompting a human before each one
is how a consent gate is trained away. Name which effect and why, because that is a decision a
reviewer should be able to read:

```csharp
#pragma warning disable FLOWX1046
//   search-index writes only; nothing here is irreversible and nothing costs money.
[AgentTrigger(Description = "Reindex a ticket.", Confirmation = ConfirmationMode.Never)]
#pragma warning restore FLOWX1046
```

**Not** because the client is trusted to prompt anyway. That is the reasoning the rule exists to
interrupt: the descriptor is the only thing the client reads, and a client told `false` has nothing
to prompt about.

## Why a warning, where FLOWX1042 is an error

Because the source is not wrong. Every reason FLOWX1042 reports is a declaration that produces
*nothing at all* — a subscription no host reads — and every fix produces a flow the engine runs
today. Here the flow runs, the tool works, and what is missing is a judgement about consequences
that this analyzer cannot make: it can see that an effect was declared and cannot see whether it
matters. An error would make a legitimate design inexpressible, which is
`CompensationDurabilityAnalyzer`'s reason for the same severity.

This repository sets `TreatWarningsAsErrors`, so it stops the build here regardless. A consumer who
has decided otherwise writes one `.editorconfig` line in the repository that took the decision.

## What it does not report

- **A flow that declares no `Confirmation` at all.** The default is the safe answer, and reporting
  on it would fire on every agent tool with a consequence and mean nothing.
- **A flow whose capabilities declare no side effect.** `Never` is then exactly the accurate
  declaration — `ticket.search` in [`samples/ai-agent`](../../samples/ai-agent) is that flow, and
  it must stay silent or the rule has taught its readers to suppress it.
- **A `.CompensateWith<T>` whose capability declares an effect.** The set counted is the set
  `McpToolCatalog` projects into the descriptor, which reads the manifest's `step.capability` and
  not its `step.compensation`. Counting more than the descriptor publishes would report a tool
  whose `confirmationRequired` no edit to the trigger can change.
- **A capability whose attribute this compilation cannot read** — one in a referenced assembly
  built by another compiler. It contributes no effect, and the rule then says nothing rather than
  reporting "no side effects" as though that were a fact. The rule fires on evidence of a
  consequence, never on its absence.

## The same condition, reported from a manifest

`FlowX.Ai`'s `ManifestReview` raises `ai.agent_tool_declares_no_confirmation` for the same pair of
declarations, read out of `flowx.manifest.json` instead of out of source. Both are worth having and
they see different things: this one stops a build in the repository that owns the flow, and that
one reads a document from any build — including one whose source the reader does not have.

---

**See also:** [ADR-0060](../adr/ADR-0060-the-server-asks-the-caller-for-what-it-does-not-have.md) ·
[13 §6 — Capabilities as agent tools](../13-AI-Native.md#6-capabilities-as-agent-tools) ·
[Diagnostics index](README.md)

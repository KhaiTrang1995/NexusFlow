# Diagnostics

Every `FLOWX####` the compiler can report, with what it means, an example that
triggers it, and the fix. The `helpLinkUri` on each descriptor points here, and
`EveryDiagnosticIsHelpful` fails the build if a descriptor lacks one.

## Why these are errors, not warnings

Each rule below encodes something that is either structurally impossible to recover
from at run time, or expensive enough that discovering it in production is the wrong
place. A warning is a rule nobody has to obey; if a rule is worth having, it stops
the build.

Three entries below are not errors, and each says why on its own page.
[FLOWX1024](FLOWX1024.md) and [FLOWX1025](FLOWX1025.md) report gaps between the
manifest and what the build can actually deliver, rather than mistakes in the source
— and `FLOWX1025` is additionally about an attribute the developer usually does not
own, which an error would make unusable. [FLOWX1011](FLOWX1011.md) is an error in `Durable`
flows and a warning in `Ephemeral` ones, which is the asymmetry
[ADR-0003](../adr/ADR-0003-execution-profiles.md) ratified for the determinism rules:
a durable flow is replayed and must take the branch it took the first time, an
ephemeral one is not replayed at all.

## Catalogue

| Id | Rule | Prevents |
|---|---|---|
| [FLOWX1001](FLOWX1001.md) | Flow must be partial | The generated plan has nowhere to live |
| [FLOWX1002](FLOWX1002.md) | Step type is not a capability | A step the engine cannot invoke |
| [FLOWX1003](FLOWX1003.md) | Capability references a transport | Losing quality goal Q4 — the same flow behind any transport |
| [FLOWX1004](FLOWX1004.md) | Capability invokes another capability | Turning the capability set back into a call graph |
| [FLOWX1005](FLOWX1005.md) | Flow inherits from another flow | Control flow invisible to the graph and the manifest |
| [FLOWX1010](FLOWX1010.md) | Capability declares no authorisation stance | A permissive default nobody chose |
| [FLOWX1011](FLOWX1011.md) | Condition, selector or projection reads something outside the flow's state | A branch that takes a different path on replay, or a step input that is not the journaled one |
| [FLOWX1013](FLOWX1013.md) | Parallel branches must write disjoint context slots | **Two concurrent branches racing on one context slot** |
| [FLOWX1014](FLOWX1014.md) | Retry requires an idempotent capability | **A duplicate charge** |
| [FLOWX1015](FLOWX1015.md) | Capability implements more than one contract | Ambiguous dispatch, meaningless manifest entry |
| [FLOWX1016](FLOWX1016.md) | Expected failures are values, not exceptions | A business outcome arriving as a defect alert, missing from the error catalogue and unclassifiable by retry |
| [FLOWX1017](FLOWX1017.md) | AwaitSignal requires the Durable profile | A waiting flow vanishing with its node |
| [FLOWX1018](FLOWX1018.md) | Cache requires no side effects | Reporting a write that never happened |
| [FLOWX1019](FLOWX1019.md) | Flow deadline is shorter than the step timeouts it must contain | A flow that runs out of budget mid-way, reported as a timeout several steps from its cause |
| [FLOWX1020](FLOWX1020.md) | Step consumes a contract no earlier step produces | A flow that throws on its first request |
| [FLOWX1021](FLOWX1021.md) | Sub-flow composition forms a cycle | **A stack overflow, or a deadline breach several flows from its cause** |
| [FLOWX1026](FLOWX1026.md) | Sub-flow cannot be composed | A composition silently missing from the plan, the manifest and the diagram |
| [FLOWX1023](FLOWX1023.md) | Flow declares no steps | A flow that silently does nothing |
| [FLOWX1024](FLOWX1024.md) | Emit step is recorded but not published | A consumer waiting for an event the manifest promised |
| [FLOWX1025](FLOWX1025.md) | Trigger attribute cannot be read by the compiler | A trigger missing from the manifest, and `flowx diff` unable to tell |

> **Every id above is raised and covered by a test.** Four of them were not, until
> WP-13: `FLOWX1014` and `FLOWX1018` ask what is in a policy set, and nothing resolved
> one; `FLOWX1003` and `FLOWX1004` read a capability's dependencies, which the flow
> generator never looks at and which needed a separate `DiagnosticAnalyzer`. All four
> were documented as compile errors the whole time.
>
> `FLOWX1003` has a stated limit worth reading before relying on it: it matches a
> **list** of transport namespaces, not a proof. `FLOWX1020` has stated limits for the
> opposite reason: it is silent wherever it cannot resolve the chain, because a rule
> about step order that fires on a valid flow would be suppressed and then protect
> nothing. `FLOWX1011` has both kinds at once: its scope rules are a proof, its
> catalogue of impure statics is a list, and it is not interprocedural — a helper
> method called from a condition can read a clock and it will not notice. It covers
> every `IFlowBuilder` delegate that takes the flow context, not only `When`; the
> constructs are a table on its page.
>
> The two added in WP-39 state theirs the same way. `FLOWX1016` proves containment — a
> `throw` inside `ExecuteAsync` is a `throw` the engine catches — and *lists* the
> exception types that mean a defect rather than an outcome; it is not interprocedural.
> `FLOWX1019` reports a **floor**: everything it cannot read counts as zero, so its
> silence is never a statement that a flow's deadline is adequate.

## Ids reserved but not yet raised

The catalogue is deliberately smaller than the numbering suggests. Codes appear here
only once the compiler actually reports them — a documented diagnostic that nothing
raises is a promise the compiler is not keeping. Reserved for later phases:
`FLOWX1006` (state must be serialisable), `FLOWX1007`–`FLOWX1009` (determinism in
durable flows), `FLOWX1012` (compensable-and-ephemeral) and
`FLOWX1022` (contract compatibility **across versions** — the analyzer counterpart
of `flowx diff`, distinct from `FLOWX1020`, which checks one flow's steps against
each other). `FLOWX1021` left this list when sub-flows landed; `FLOWX1016` and
`FLOWX1019` left it in WP-39.

**What each remaining reservation is blocked on**, so that "reserved" does not
quietly become "forgotten":

| Id | Blocked on |
|---|---|
| `FLOWX1006` | The generated `System.Text.Json` context [ADR-0008](../adr/ADR-0008-serialization-and-schema.md) chose. Nothing generates one, `IPayloadSerializer` does not exist, and `ctx.State` is serialised nowhere — so there is no membership the rule could check a contract against. Checking "is this type serialisable in principle" instead would be a different, weaker rule under a number already spoken for |
| `FLOWX1007`–`FLOWX1009` | Not the analysis — `PredicatePurityAnalyzer` already performs it for flow delegates, and extending it to capability bodies is mechanical. The severity: [ADR-0003](../adr/ADR-0003-execution-profiles.md) makes these errors in `Durable` and informational in `Ephemeral`, the runtime executes only `Ephemeral`, and an Info diagnostic never reaches a build log. They would ship doing nothing anywhere. [06 §5](../06-Execution-Engine.md#5-the-determinism-boundary) says the stance is to be revisited **as a set** once the journal exists, and not one row at a time |
| `FLOWX1012` | Its remedy. The check itself is easy — `.CompensateWith` under `Profile = Ephemeral` — and it would fire on every compensable flow in this release, including the reference sample. But the fix it would recommend, `Profile = Durable`, changes nothing: there is no journal, and the engine runs every profile in memory. It would silence the warning while leaving compensation exactly as best-effort as before, which is worse than not raising it |
| `FLOWX1022` | `flowx diff`'s question, asked of two manifests. An analyzer sees one compilation and cannot see the previous version's contracts at all |

**A new rule takes the next id above the catalogue, never a reserved one.** Each
reservation above already has a meaning written down in at least one other document,
and reusing one would leave two rules describing themselves with the same number —
a mistake this project has already made once, when a check was built as `FLOWX1022`
while three documents described it as `FLOWX1020`. `FLOWX1026` took the next free id
for exactly that reason: `FLOWX1022` is spoken for, and "sub-flow cannot be
composed" is not contract compatibility. The next is `FLOWX1027`. The range is
`FLOWX1001`–`FLOWX1099`.

## Adding a diagnostic

1. Add the descriptor to `FlowXDiagnostics`, with a message naming the offending
   symbol, a description saying what to do instead, and a help URI.
2. Add the id to `AnalyzerReleases.Unshipped.md`. The build fails without it —
   RS2008 — which is intentional: a diagnostic id is public surface, because teams
   write suppressions against it.
3. Write the page in this directory.
4. Add the test that proves it fires, and the test that proves it does not fire on
   valid code. The second one matters more; a rule with false positives gets
   suppressed everywhere and then protects nothing.

---

**Back to:** [Quality gates](../21-Quality-Gates.md) · [Architecture](../05-Architecture.md)

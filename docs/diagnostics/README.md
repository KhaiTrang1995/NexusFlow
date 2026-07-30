# Diagnostics

Every `FLOWX####` the compiler can report, with what it means, an example that
triggers it, and the fix. The `helpLinkUri` on each descriptor points here, and
`EveryDiagnosticIsHelpful` fails the build if a descriptor lacks one.

## Why these are errors, not warnings

Each rule below encodes something that is either structurally impossible to recover
from at run time, or expensive enough that discovering it in production is the wrong
place. A warning is a rule nobody has to obey; if a rule is worth having, it stops
the build.

## Catalogue

| Id | Rule | Prevents |
|---|---|---|
| [FLOWX1001](FLOWX1001.md) | Flow must be partial | The generated plan has nowhere to live |
| [FLOWX1002](FLOWX1002.md) | Step type is not a capability | A step the engine cannot invoke |
| [FLOWX1003](FLOWX1003.md) | Capability references a transport | Losing quality goal Q4 — the same flow behind any transport |
| [FLOWX1004](FLOWX1004.md) | Capability invokes another capability | Turning the capability set back into a call graph |
| [FLOWX1005](FLOWX1005.md) | Flow inherits from another flow | Control flow invisible to the graph and the manifest |
| [FLOWX1010](FLOWX1010.md) | Capability declares no authorisation stance | A permissive default nobody chose |
| [FLOWX1014](FLOWX1014.md) | Retry requires an idempotent capability | **A duplicate charge** |
| [FLOWX1015](FLOWX1015.md) | Capability implements more than one contract | Ambiguous dispatch, meaningless manifest entry |
| [FLOWX1017](FLOWX1017.md) | AwaitSignal requires the Durable profile | A waiting flow vanishing with its node |
| [FLOWX1018](FLOWX1018.md) | Cache requires no side effects | Reporting a write that never happened |
| [FLOWX1023](FLOWX1023.md) | Flow declares no steps | A flow that silently does nothing |
| [FLOWX1024](FLOWX1024.md) | Emit step is recorded but not published | A consumer waiting for an event the manifest promised |

## Ids reserved but not yet raised

The catalogue is deliberately smaller than the numbering suggests. Codes appear here
only once the compiler actually reports them — a documented diagnostic that nothing
raises is a promise the compiler is not keeping. Reserved for later phases:
`FLOWX1006` (state must be serialisable), `FLOWX1007`–`FLOWX1009` (determinism in
durable flows), `FLOWX1011`–`FLOWX1013` (predicate purity, parallel branch
disjointness), `FLOWX1016` (expected failures are values), `FLOWX1019`–`FLOWX1022`
(deadline coherence, step binding, sub-flow cycles, contract compatibility).

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

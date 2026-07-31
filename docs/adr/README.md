# Architecture Decision Records

Every significant decision in FlowX has an ADR. One decision per record — never
bundled. Each record states the forces, the decision, the accepted trade-offs,
and the condition under which it must be revisited.

| ADR | Decision | Status | Revisit when |
|---|---|---|---|
| [0001](ADR-0001-flow-and-capability-as-primitives.md) | Flow + Capability are the only two user primitives | Accepted | a real use case cannot be expressed as either |
| [0002](ADR-0002-compile-time-orchestration.md) | Compile-time orchestration; no runtime reflection | Accepted | generator maintenance exceeds its benefit, or dynamic flows become a top-3 request |
| [0003](ADR-0003-execution-profiles.md) | Per-flow execution profiles instead of always-durable | Accepted | journal cost drops to ephemeral levels |
| [0004](ADR-0004-universal-trigger-model.md) | One trigger abstraction for all transports | Accepted | > 30 % of flows need transport-specific escape hatches |
| [0005](ADR-0005-manifest-as-build-artifact.md) | The manifest is a first-class build artifact | Accepted | never — this is foundational |
| [0006](ADR-0006-journal-and-leases.md) | Append-only journal + fenced leases | Accepted | a managed durable-execution primitive makes this commodity |
| [0007](ADR-0007-result-over-exceptions.md) | `Result<T>` for business outcomes | Accepted | the language gains first-class union types |
| [0008](ADR-0008-serialization-and-schema.md) | Source-generated STJ, versioned schemas | Accepted | journal serialisation becomes the measured bottleneck |
| [0009](ADR-0009-plugin-contracts.md) | Plugins depend only on `FlowX.Abstractions` | Accepted | never — extension integrity depends on it |
| [0010](ADR-0010-csharp-dsl-over-yaml.md) | C# DSL is the source of truth; YAML is export | Accepted | non-developer authoring becomes a primary requirement |
| [0011](ADR-0011-fixed-policy-stage-order.md) | Fixed policy stage order | Accepted | three documented legitimate counterexamples |
| [0012](ADR-0012-apache-2-license.md) | Apache-2.0 licence | Accepted | a governance model requires a different licence |
| [0013](ADR-0013-dsl-vocabulary-over-ca1716.md) | DSL vocabulary takes precedence over CA1716 | Accepted | a first-class VB.NET or F# consumer story is adopted |
| [0014](ADR-0014-derived-error-catalogue-vs-build-budget.md) | Keep the derived error catalogue; re-express the build-overhead budget | **Proposed** | a real project measures the derivation at > 2× its recorded cost, or P8 approaches the manifest v1.0 freeze |

## Writing an ADR

```markdown
# ADR-NNNN: <Decision in active voice>

**Status:** Proposed | Accepted | Superseded by ADR-MMMM
**Date:** YYYY-MM-DD
**Deciders:** <roles>

## Context
Forces at play: constraints, quality goals affected, options considered
(rejected options: one line each with the rejection reason).

## Decision
We will <decision>, because <primary driver>.

## Consequences
**Positive:** …
**Negative / accepted trade-offs:** …
**Revisit when:** <condition> (mandatory)
```

An ADR with no "Negative" section has not been thought through. An ADR with no
"Revisit when" is a decision nobody can ever safely change.

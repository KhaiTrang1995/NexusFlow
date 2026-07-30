# ADR-0010: Make the C# fluent DSL the source of truth; YAML is export only

**Status:** Accepted
**Date:** 2026-07-30
**Deciders:** Platform architecture

## Context

Workflow platforms typically define flows in YAML or JSON (Argo, Step Functions,
Camunda, GitHub Actions). The attraction is real: non-developers can read them,
they can be changed without a deployment, and they are trivially visualisable.

The costs are equally real and are paid in production: no type checking between
steps, no refactoring support, no go-to-definition, no static analysis, and
errors that surface at run time in the environment where they are most expensive.

Quality goals affected: Q3 (static knowability), and the determinism analysis
that ADR-0003 depends on.

Options considered:

- **A. YAML/JSON as the source of truth.** *Rejected:* a step whose input type
  does not match the previous step's output is a production incident instead of
  a red squiggle. Determinism analysis, contract compatibility checking and
  compile-time policy validation all become impossible.
- **B. YAML with a schema and a linter.** *Rejected:* recovers some validation
  but still cannot check that `CaptureRequest` can be constructed from what the
  context holds, and still cannot analyse determinism.
- **C. Visual editor as the source of truth.** *Rejected:* unreviewable diffs,
  merge conflicts that humans cannot resolve, and a hard dependency on one tool.
- **D. C# fluent DSL as the source of truth; YAML/JSON as a generated export.**
  Chosen.

## Decision

We will make the **C# fluent DSL authoritative** and treat YAML/JSON as a
*generated export* (`flowx graph --format yaml`) consumed by Studio,
documentation and external tooling — because the compile-time guarantees that
distinguish FlowX from every other flow engine are only available in a compiled
language.

Corollary, from the manifesto: **configuration selects adapters; it never
redefines business meaning.**

## Consequences

**Positive**
- Type mismatches, unresolvable steps, cycles, unsafe retries, determinism
  violations and missing authorisation are all **compile errors** with precise
  locations and suggested fixes.
- Refactoring is IDE-wide and safe. Renaming a contract updates every flow.
- Go-to-definition, find-references and CodeLens work on business flows.
- Debugging means breakpoints in ordinary generated C#.
- Analyzers can enforce architecture rules that no YAML linter can express.

**Negative / accepted trade-offs**
- **Non-developers cannot author flows.** This is a genuine capability we are
  giving up, and it is the main reason teams choose YAML engines. Partial
  mitigation: Studio renders flows visually and can *scaffold* C# from a
  designed graph — read-and-scaffold, not round-trip edit.
- **No hot-reload of business logic.** Changing a flow requires a build and a
  deployment. For a platform whose value is compile-time guarantees, this is
  inherent, not incidental.
- **C# is a barrier for polyglot organisations.** A non-.NET service cannot
  author FlowX flows; it can only interact through triggers and events.
- Business analysts must read generated diagrams rather than the source. In
  practice this is an improvement over reading YAML, but it is a dependency on
  the tooling being good.

**Revisit when:** authoring by non-developers becomes a primary product
requirement — at which point the answer is likely a *second*, interpreted profile
with explicitly weaker guarantees, not a change to this one.

# ADR-0002: Resolve orchestration at compile time with Roslyn generators

**Status:** Accepted
**Date:** 2026-07-30
**Deciders:** Platform architecture, Runtime team

## Context

Existing .NET mediators and message frameworks build their dispatch tables at
run time by scanning assemblies (`Assembly.GetTypes()`), then dispatch through
reflection or expression trees. This costs:

- **Latency and allocation** on every invocation (Q1: p99 ≤ 5 µs, 0 alloc/step).
- **Cold start** — assembly scanning dominates startup (Q7: ≤ 200 ms).
- **NativeAOT incompatibility** — reflection over unreferenced types cannot be
  trimmed (constraint C2).
- **Knowability** — the application's graph exists only after DI resolution, so
  nothing outside the running process can inspect it (Q3).

Options considered:

- **A. Runtime scanning + reflection.** Status quo elsewhere. *Rejected:* fails
  Q1, Q3, Q7 and C2 simultaneously.
- **B. Runtime scanning + cached compiled expressions.** *Rejected:* fixes
  steady-state latency only; cold start, AOT and knowability remain broken.
- **C. Explicit manual registration** (`services.AddFlow<PlaceOrderFlow>()`).
  *Rejected:* fixes AOT but not knowability; and hand-maintained registration
  drifts — a forgotten registration becomes a runtime 404.
- **D. Roslyn incremental source generators emitting a static plan, dispatch
  switch, trigger bindings and the manifest.** Chosen.

## Decision

We will resolve the entire orchestration graph at **compile time** using Roslyn
incremental generators and analyzers, emitting static execution plans, a
reflection-free dispatch switch, trigger bindings, serialiser contexts and
`flowx.manifest.json` — because the manifest (Q3) is only achievable at build
time, and it is simultaneously what makes Q1, Q7 and C2 achievable.

Corollary rule: `FlowX.Runtime` contains **no** `System.Reflection` usage on any
execution path (fitness function `NoReflectionOnHotPath`).

## Consequences

**Positive**
- Q1, Q3, Q7 and C2 are met by construction rather than by optimisation.
- Errors move from run time to build time: an unresolvable step, an incompatible
  contract, a cycle, a determinism violation, an unsafe retry — all become
  compiler diagnostics.
- The manifest exists as a build output, enabling everything in
  [13-AI-Native](../13-AI-Native.md).
- No service-locator; the dependency graph is visible in generated code.

**Negative / accepted trade-offs**
- **Generator complexity becomes our own legacy risk** (risk R1). Source
  generators are hard to debug and can slow builds. Mitigations, all mandatory:
  emit readable C# to `obj/generated`; snapshot-test every emitted file; keep the
  generator's logic in a pure, unit-testable model layer separate from Roslyn
  plumbing; gate build overhead at ≤ 8 % (budget B12).
- **No dynamic, user-authored flows at run time.** This is a real capability we
  are giving up. When the need is proven, it will be served by a *separate*
  interpreted profile with an explicitly worse performance contract — never by
  degrading the compiled path.
- Build-time coupling: upgrading the analyzer package can surface new diagnostics
  on unchanged code. Mitigated by SemVer and a 2-minor deprecation window (C7).
- Debugging requires understanding that some code is generated. Mitigated by
  emitting it to disk with SourceLink, breakpoint-able.

**Revisit when:** generator maintenance cost exceeds its benefit (measured as
> 3 generator defects per delivery phase, or build overhead > 8 % sustained), or
dynamic run-time flow authoring becomes a top-3 user request.

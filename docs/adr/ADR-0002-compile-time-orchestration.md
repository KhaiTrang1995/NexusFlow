# ADR-0002: Resolve orchestration at compile time with Roslyn generators

**Status:** Accepted
**Date:** 2026-07-30
**Deciders:** Platform architecture, Runtime team

> [!IMPORTANT]
> **This record's own revisit trigger has fired, and the record did not say so.** The
> condition at the bottom is *"more than 3 generator defects per delivery phase, **or build
> overhead over 8 % sustained**"*. Build overhead at 200 flows — the size P1's exit criterion
> names — is **+67.1 %**, 95 % CI **[+61.9, +73.6]**, re-measured at WP-43 and recorded in
> [B12-scale §5.4](../benchmarks/B12-scale.md). *Sustained* is not one reading: §5.1
> recorded **+77.1 %** [+72.0, +80.6] on other hardware before the duplicated-bind fix, and
> the pre-catalogue tree measured **+18.4 %** [+16.3, +19.9] with the feature that dominates
> the figure nowhere in the build. Every campaign that has measured this clause has found it
> true.
>
> **What has fired is the obligation to review, not the decision.** Nothing here is
> withdrawn. Q1, Q3, Q7 and C2 are still met by construction, the kill criterion B1 and B2
> define was passed, and [B12-scale §4](../benchmarks/B12-scale.md) finds the generator
> linear in flows — `flows^0.95`, CI [0.87, 1.06], with no meaningful fixed term — which is
> an optimisation backlog rather than an architectural defect. The other half of the
> condition — *more than three generator defects per delivery phase* — is counted by
> nothing: it can neither fire nor be shown not to have.
>
> **Where the review is.** [PLAN §1](../../PLAN.md#1-what-p0-exists-to-prove)'s kill
> criterion says "Revisit ADR-0002 first", and it is the only place in the repository that
> connects the number to the record it is meant to reopen.
> [ADR-0014](ADR-0014-derived-error-catalogue-vs-build-budget.md)) is the record that exists
> to resolve it — the choice between the derived error catalogue and the ≤ 8 % budget, put
> in front of a decider — and it is still **Proposed**.
>
> **Two records now disagree about whether one of the mitigations below binds, and this note
> does not settle it.** The mitigation list in the first negative is declared *"all
> mandatory"* and its fourth item is *"gate build overhead at ≤ 8 % (budget B12)"*.
> [ADR-0014 §4(4)](ADR-0014-derived-error-catalogue-vs-build-budget.md)#4-decision) has since
> committed the opposite: the `scale-overhead` job that measures it *"stays advisory until a
> pass is recorded"*, and `.github/workflows/performance.yml` carries `continue-on-error:
> true` on that job, with the reasoning attached. So a mitigation this record calls mandatory
> is measured by a job that cannot fail a pull request. The gate that does block —
> `generator-cost` — is *relative*: bytes allocated against a committed baseline, which
> answers "did this change make it worse", not "is the build inside the budget". **Which
> record gives way is the repository owner's decision**, and on 2026-07-31 that owner
> **removed it from [PLAN §9](../../PLAN.md#9-open-items-blocking-the-plan)'s open items** —
> a decision to leave the choice unmade rather than an oversight. So the disagreement stands
> and is no longer queued for resolution: this note records it because a conflict nobody is
> tracking is worse than one nobody has settled.

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
  plumbing; gate build overhead at ≤ 8 % (budget B12). *The fourth is the one the
  measurement has broken and the one ADR-0014 §4(4) has since made advisory — see the note
  at the top. "Mandatory" is therefore doing no work on this line as things stand.*
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

*The build-overhead clause has **fired** — +67.1 % at 200 flows, sustained across every
campaign that has measured it. See the note at the top of this record for what that does and
does not mean, and for the record that carries the resulting choice.*

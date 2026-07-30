# Implementation Plan

> **Scope:** work-package detail for the phases in
> [docs/20-Roadmap.md](docs/20-Roadmap.md). The roadmap says what each phase must
> prove and when it is done; this document is what a contributor picks work from.
>
> **Companion:** [CHECKLIST.md](CHECKLIST.md) carries live status and is updated
> with every change. This document changes only when the *plan* changes.
>
> **Where we are:** **P0 is complete and passed its kill criterion** (B1 172 ns
> against 5 µs, B2 zero — [P0.md](docs/benchmarks/P0.md)). **P1 — Compiler
> hardening** is in progress; §4 below carries its work packages, derived from the
> roadmap's P1 scope.

---

## 1. What P0 exists to prove

P0 is not "the foundation". It is a **falsification attempt** against the
platform's central bet, stated in
[ADR-0002](docs/adr/ADR-0002-compile-time-orchestration.md):

> A Roslyn source generator can emit an execution plan that is correct,
> debuggable, and fast enough that compile-time orchestration beats a
> reflection-based mediator by an order of magnitude.

Everything else in FlowX is downstream of that sentence. If it is false, the
right outcome for P0 is to **discover it in three weeks**, not to discover it in
month nine with eight phases built on top.

**Kill criterion (unchanged from the roadmap):** if generated dispatch cannot
reach **B1 ≤ 5 µs p99** and **B2 = 0 allocations**, stop. Do not proceed to P1.
Revisit ADR-0002 first.

---

## 2. Sequencing

```mermaid
flowchart TD
    WP0["WP-0 · Quality gates<br/>CI, SAST, DAST, Sonar"]
    WP1["WP-1 · Abstractions<br/>contract surface ✔"]
    WP2["WP-2 · Core model<br/>StepGraph, ExecutionPlan"]
    WP3["WP-3 · Benchmark harness<br/>B1–B3 measurable"]
    WP4["WP-4 · Engine<br/>step loop, context pooling"]
    WP5["WP-5 · Generator<br/>FlowPlanGenerator, linear"]
    WP6["WP-6 · Manifest<br/>emit + schema validate"]
    WP7["WP-7 · Hosting<br/>DI, options, health"]
    WP8["WP-8 · HTTP plugin<br/>one endpoint, RFC 7807"]
    WP9["WP-9 · CLI<br/>flowx graph"]
    WP10["WP-10 · Sample<br/>ecommerce, 3 steps"]
    WP11["WP-11 · Gate<br/>run kill criterion"]
    WP12["WP-12 · Testing<br/>supported test context"]
    WP12a["WP-12a · Sensitive<br/>read + manifest"]
    WP13["WP-13 · Diagnostics<br/>FLOWX1014 · FLOWX1018"]
    WP14["WP-14 · B12<br/>build overhead"]

    WP15["WP-15 · Branching DSL<br/>When · Switch · Parallel"]
    WP16["WP-16 · Step binding<br/>FLOWX1020"]
    WP17["WP-17 · flowx diff<br/>breaking-change gate"]
    WP18["WP-18 · Scale<br/>200 flows"]
    WP19["WP-19 · Code fixes<br/>IDE quick actions"]

    WP0 --> WP1 --> WP2 --> WP3
    WP2 --> WP4
    WP3 --> WP4
    WP4 --> WP5 --> WP6
    WP5 --> WP7 --> WP8 --> WP10
    WP6 --> WP9 --> WP10
    WP10 --> WP11
    WP10 --> WP12
    WP5 --> WP12a
    WP5 --> WP13
    WP5 --> WP14

    WP5 --> WP15
    WP5 --> WP16
    WP9 --> WP17
    WP6 --> WP17
    WP14 --> WP18
    WP13 --> WP19

    style WP3 fill:#fff3cd,stroke:#856404
    style WP11 fill:#f8d7da,stroke:#721c24
    style WP1 fill:#d4edda,stroke:#155724
    style WP15 fill:#cfe2ff,stroke:#084298
    style WP16 fill:#cfe2ff,stroke:#084298
    style WP17 fill:#cfe2ff,stroke:#084298
    style WP18 fill:#cfe2ff,stroke:#084298
    style WP19 fill:#cfe2ff,stroke:#084298
```

WP-0 through WP-14 are P0 (complete); WP-15 onward are **P1**, shown in blue.
WP-16, WP-17 and WP-18 have disjoint dependencies and no shared files, so they
run concurrently; WP-15 touches the whole stack and does not.

**WP-3 is scheduled before the engine on purpose.** A performance budget that
becomes measurable only after the thing it constrains is built is a budget that
gets renegotiated instead of met. The benchmark harness measures an empty step
loop first, so every subsequent commit is measured against a number that already
exists.

---

## 3. P0 — Walking skeleton · work packages

**Complete.** The kill criterion passed at WP-11 and the phase's exit criteria are
met; see [CHECKLIST.md](CHECKLIST.md) for live status. WP-12 through WP-14 are
overruns — work P0 turned out to need once the reference sample was written, kept
here with the phase that produced them rather than renumbered into P1.

Each package states its goal, the tests written **first**, the deliverable, and
an exit criterion that is mechanically checkable.

### WP-0 — Quality gates and CI

| | |
|---|---|
| **Goal** | Every gate in [21-Quality-Gates](docs/21-Quality-Gates.md) runs before there is code to violate it |
| **Tests first** | n/a — this *is* the test infrastructure |
| **Deliverable** | `.github/workflows/ci.yml` (build, fitness, AOT, docs, attribution) · `security.yml` (CodeQL, Semgrep, Gitleaks, Trivy, SCA) · `quality.yml` (Sonar, coverage, Stryker) · `.github/dependabot.yml` · PR template with the Definition of Done |
| **Exit** | A deliberately introduced violation of each gate class is caught. Verified by pushing a throwaway branch per gate. |
| **Depends on** | — |

### WP-1 — Contract surface

| | |
|---|---|
| **Goal** | `FlowX.Abstractions` — what all user code and every plugin reference |
| **Tests first** | `AbstractionsHasNoDependencies`, `LayersPointInward`, `ContractSurfaceTests` |
| **Deliverable** | `Result<T>`, `Error`, `ErrorCategory`, `ICapability<,>`, `Flow<,>`, `IFlowBuilder<,>`, contexts, trigger attributes, `PolicySet` |
| **Exit** | Solution compiles with zero warnings; fitness functions green; zero package references |
| **Status** | **Done.** 0 warnings, 30/30 fitness tests, 47 behavioural tests |

### WP-2 — Core execution model

| | |
|---|---|
| **Goal** | The immutable data model an execution plan is made of. No I/O, no engine, no generator — just the shapes. |
| **Tests first** | `StepGraphTests` (construction, invariants) · `ExecutionPlanTests` (ordering, compensation stack) · `NoCyclicDependencies` |
| **Deliverable** | `FlowX.Core`: `StepGraph`, `StepNode`, `ExecutionPlan`, `CompensationStack`, `FlowDescriptor`, `CapabilityDescriptor`, `PolicyChain` |
| **Exit** | A three-step linear plan with one compensation is constructible, immutable, and asserts its own invariants. Mutation score ≥ 70 %. |
| **Depends on** | WP-1 |
| **Status** | **Done.** Built red → green; 58 tests; 98.5 % line / 95.6 % branch coverage. Mutation score not yet measured — Stryker is wired but unrun. |

### WP-3 — Benchmark harness *(before the engine — see §2)*

| | |
|---|---|
| **Goal** | B1, B2 and B3 are measurable and gated in CI against a committed baseline |
| **Tests first** | The benchmarks are the tests |
| **Deliverable** | `tests/FlowX.Benchmarks` with BenchmarkDotNet · `MemoryDiagnoser` with a hard-zero assertion for B2 · baseline JSON committed · CI job failing on > 5 % regression |
| **Exit** | `dotnet run -c Release --project tests/FlowX.Benchmarks` reports B1–B3; CI fails on an injected 10 % regression |
| **Depends on** | WP-2 |
| **Status** | **Done.** Gate verified by injecting a 64 B allocation regression — rejected, exit 1. Results: [docs/benchmarks](docs/benchmarks/README.md) |

### WP-4 — Flow engine

| | |
|---|---|
| **Goal** | The step loop: execute an `ExecutionPlan`, thread a pooled context, honour deadlines, unwind compensation on failure |
| **Tests first** | `FlowEngineTests` — happy path, failure path, compensation ordering (**strict reverse**), deadline expiry, cancellation propagation · `ContextPoolingTests` — zero allocation across N executions |
| **Deliverable** | `FlowX.Runtime`: `FlowEngine`, `CapabilityEngine`, pooled `FlowContext`/`CapabilityContext`, `IClock` |
| **Exit** | B2 = 0 allocations on a 4-step flow; compensation ordering proven by test, not by inspection |
| **Depends on** | WP-2, WP-3 |
| **Status** | **Done.** 0 B on a 4-step flow (592 B → 0 B after three fixes found by measurement); 28 tests including concurrency; B1 = 169 ns against a 5 000 ns budget |

### WP-5 — Source generator *(the risk)*

| | |
|---|---|
| **Goal** | Emit a compile-time `ExecutionPlan` from a `Define` method — linear steps only |
| **Tests first** | Generator **snapshot** tests (Verify) · `EmittedCodeIsDebuggable` (line directives present) · `EveryDiagnosticIsHelpful` · a golden-file test per DSL shape |
| **Deliverable** | `FlowX.Compiler`: `FlowPlanGenerator` (incremental), a syntax→model layer kept separate from emission, diagnostics FLOWX1001–1010 |
| **Exit** | The sample flow's plan is generated, readable, breakpoint-able; B1 ≤ 5 µs; build overhead ≤ 8 % on a 20-flow solution |
| **Risk** | **R1.** If the generator's model layer and emission layer blur together here, P1 becomes unmaintainable. Keep them separate from the first commit. |
| **Depends on** | WP-4 |
| **Status** | **Partial.** Generating end to end against a real compilation, exercised by the sample at WP-10. The diagnostics landed at WP-13 and budget B12 at WP-14. **Remaining: the branching DSL** — `When` / `Switch` / `Parallel` / `ForEach` / `SubFlow`. |

### WP-6 — Manifest emission

| | |
|---|---|
| **Goal** | `flowx.manifest.json` v0 emitted at build, validating against the committed schema |
| **Tests first** | `ManifestValidatesAgainstSchema` · `ManifestContainsNoSecrets` · `ManifestIsDeterministic` (same input → byte-identical output) |
| **Deliverable** | Manifest writer in `FlowX.Compiler`; schema already committed at `schemas/flowx.manifest.schema.json` |
| **Exit** | Sample build emits a manifest that validates; two consecutive builds are byte-identical |
| **Depends on** | WP-5 |
| **Status** | **Done.** Schema-valid against the committed schema with negative controls; byte-identical across runs and independent of flow discovery order. The on-disk file waits for the CLI at WP-9, because a generator must not do file IO. |

### WP-7 — Hosting and composition

| | |
|---|---|
| **Goal** | `AddFlowX()` wires generated registrations; configuration validated at **startup**, not first use |
| **Tests first** | `StartupValidationRejectsMisconfiguration` (A05) · `HealthCheckReportsReadiness` |
| **Deliverable** | `FlowX.Hosting`: DI extensions, options with validation, health checks, graceful shutdown draining |
| **Exit** | A misconfigured host refuses to start with a message naming the setting; in-flight flows drain on SIGTERM |
| **Status** | **Done.** 19 tests. Validation runs at startup rather than first use, reports every problem at once, and names each setting. Drain refuses new work, is bounded, and reports whether it succeeded. |
| **Depends on** | WP-5 |

### WP-8 — HTTP trigger plugin

| | |
|---|---|
| **Goal** | Generated endpoint, generated binder, generated OpenAPI, RFC 7807 errors |
| **Tests first** | `ErrorCategoryMapsToProblemDetails` (all six) · `IdempotencyKeyIsEnforced` · `TenantComesFromClaimsOnly` (A07) · `EgressIsAllowListed` (A10) |
| **Deliverable** | `plugins/FlowX.Http`: endpoint generation, model binding, Problem Details mapping, OpenAPI document |
| **Exit** | Sample serves `POST /api/v1/orders`; ZAP baseline scan clean; B9 measured |
| **Depends on** | WP-7 |
| **Status** | **Mostly done.** The endpoint serves over a real TestServer with RFC 7807 mapping and claims-only tenant resolution; 59 tests. Generated endpoints, OpenAPI, ZAP and B9 all wait for the sample at WP-10. |

### WP-9 — CLI

| | |
|---|---|
| **Goal** | `flowx graph` renders the manifest as Mermaid |
| **Tests first** | `GraphOutputIsValidMermaid` · `GraphIsDeterministic` |
| **Deliverable** | `FlowX.Cli` with the `graph` verb |
| **Exit** | Rendered graph of the sample parses with `mmdc` in CI |
| **Depends on** | WP-6 |
| **Status** | **Done.** Verified: `mmdc` renders the diagram to a 58 KB SVG, and the check is now a CI step. Also delivered `flowx manifest`, the on-disk artifact WP-6 deferred. |

### WP-10 — Reference sample

| | |
|---|---|
| **Goal** | `samples/ecommerce` runs a real 3-step ephemeral flow over HTTP |
| **Tests first** | End-to-end test hitting the endpoint · `CapabilityTestedWithoutHost` (proves quality goal Q2) |
| **Deliverable** | Four capabilities, one flow, one endpoint, 20 tests, README |
| **Exit** | `dotnet run` serves the endpoint; ZAP baseline clean; `flowx graph` renders it |
| **Depends on** | WP-8, WP-9 |
| **Status** | **Done**, bar the ZAP baseline. The endpoint serves the flow's declared output over HTTP and as a NativeAOT binary; `flowx graph` renders the sample's real manifest; 20 tests. |

**What the first consumer found.** The sample was the first code written against the
platform from outside it, and it found six defects that no test inside the platform
could have:

| Found | Was |
|---|---|
| `Result<T>` had no implicit conversions | The documented capability style did not compile. Restored; the `T = Error` collision is real but is a loud `CS0457`, not a silent mis-resolution. |
| Every step's `#line` directive pointed at the same line | A fluent chain nests its receiver, so each invocation's span starts at the head of the chain. A breakpoint on step three landed on step one. |
| `.Return(...)` was silently dropped | The flow declared an output type that nothing produced. The endpoint returned a step count. Now generated as a static projection. |
| `MapFlow` never read a request body | A flow whose first step binds to a contract had nothing to bind to. |
| `AddFlowX` registered the health-check **type**, not the check | `MapHealthChecks` threw at startup; with `AddHealthChecks` it returned a probe that never ran. |
| The manifest embedded an absolute source path | Broke the determinism ADR-0005 requires of it, and shipped the build agent's directory layout. |

Two more surfaced while getting the suite green:

- `.Emit<T>()` compiles into the plan and the manifest but publishes nothing. Now
  **FLOWX1024**, a warning — the only non-error diagnostic in the set — because the
  manifest promises consumers an event that does not arrive.
- The engine's allocation budgets are Release-only assertions that silently measured
  376 B of Debug scaffolding. CI runs Release and never saw it; every contributor
  running `dotnet test` did. Now skipped in Debug with the reason.

### WP-11 — P0 gate: run the kill criterion

| | |
|---|---|
| **Goal** | Answer the question P0 was built to answer |
| **Deliverable** | A benchmark report committed to `docs/benchmarks/P0.md` with the measured numbers, the hardware, and an explicit **pass/fail against ADR-0002** |
| **Exit** | B1 ≤ 5 µs **and** B2 = 0 → proceed to P1. Otherwise → stop, write the ADR that supersedes ADR-0002, and re-plan. |
| **Depends on** | WP-10 |
| **Status** | **Done. PASS.** B1 = **172.3 ns** against 5 000 ns (29× margin); B2 = **0 B** exactly. Report at [docs/benchmarks/P0.md](docs/benchmarks/P0.md); baseline re-recorded at 10 warmups / 30 iterations. |

**P0 proceeds to P1.** ADR-0002 stands: the compiled path delivers the budget it was
chosen for.

The hardware is still shared, which was open item 5 against this work package. The
report answers that head-on rather than deferring: the measured margin is 29×, the
worst run-to-run variance ever observed on this container is a factor of 2.6, and 2.6
does not close 29. Where the hardware genuinely is not good enough — the 10–30 %
ratio comparisons in `DispatchBenchmarks` — the report says so and does not lean on
them. Open item 5 is closed on that reasoning, not on new hardware.

Two things the report explicitly does **not** claim:

- **Not a p99 in the strict sense.** BenchmarkDotNet's percentiles are over iteration
  means, not individual operations, and at ~170 ns a single operation cannot be timed
  without the timer costing more than the work. The worst iteration mean was 184.8 ns;
  a true operation-level p99 would have to be 27× the mean to breach the budget, and
  the usual cause of a tail that shape is a GC pause, which a zero-allocation path
  does not create.
- **Not a retirement of risk R1.** This measures runtime performance; R1 is generator
  maintenance cost. Build overhead (**budget B12**) was measured at WP-14 and
  **passes at +0.4 %** — see [B12.md](docs/benchmarks/B12.md). The defect-count half of
  the clause is still not tracked.

### WP-12 — A supported test context

| | |
|---|---|
| **Goal** | Constructing a `CapabilityContext` in a test costs one line, not nine |
| **Why** | Quality goal Q2 says a capability is testable by constructing it and calling it. It is — but `CapabilityContext` is abstract with nine members, so every consumer hand-writes the same stub. `tests/Ecommerce.Tests/CapabilityTests.cs` carries one; so will everybody else's first test file. Ceremony that every user pays is a platform defect, not a user problem. |
| **Tests first** | The sample's own capability tests, rewritten against it — if they do not get shorter, it is not worth shipping |
| **Deliverable** | `FlowX.Testing` with a context builder: fixed clock, fixed ids, seeded `Random`, overridable per test |
| **Exit** | `CapabilityTests` constructs its context in one expression and still pins every value it pins today |
| **Depends on** | WP-10 |
| **Status** | **Done.** `TestCapabilityContext` and `TestFlowContext` ship from `src/FlowX.Testing`; 30 tests. The sample's `CapabilityTests` builds its context in one expression and lost 27 lines of stub, pinning everything it pinned before. |

`TestFlowContext` was not in the original deliverable and is the more useful half. A
generated step dispatcher and a `.Return(...)` projection are ordinary methods that take
a `FlowContext`, so with a working typed bag they can be called directly — no engine, no
plan, no host. `Fail(error)` puts the context into the state a compensation actually
meets, which is otherwise unreachable.

**Also found and fixed while here:** `LayersPointInward` enumerates its projects in
hand-written `[InlineData]` rows, so adding `FlowX.Testing` created a `src/` project that
no fitness function checked — it could have referenced anything at all and the theory
would have passed without looking at it. `EverySourceProjectIsCoveredByTheLayeringRule`
now fails on any unlisted project; it was verified by deleting the row and watching it
fail. A rule with a hand-maintained subject list needs a rule about the list.

**Not shipped, and now said so in the docs:** [19-SDK §6](docs/19-SDK.md) described a
`FlowTestHost` with capability substitution, virtual time, crash simulation and a Kafka
integration harness. None of it exists. The section now separates what ships from what
is intended, rather than reading as a description of the current package.

---

### WP-12a — `[Sensitive]` is declared and unread

| | |
|---|---|
| **Goal** | The attribute does something |
| **Why** | `[Sensitive]` exists on the contract surface and the sample applies it to `PlaceOrder.PaymentToken`. The compiler never reads it: it is absent from the manifest, and no redaction is generated. An attribute that looks like a control and is not one is worse than no attribute — a reviewer sees the token marked and concludes it is handled. Found while checking the OWASP A02 row in `CHECKLIST.md`, which claimed it reached the manifest. It does not. |
| **Tests first** | A test asserting a sensitive member is absent from any emitted log or error payload · a manifest test asserting the field is marked |
| **Deliverable** | `CapabilityReader` reads `[Sensitive]`; the manifest records it; the generator emits redaction for it |
| **Exit** | A flow whose input carries a sensitive member cannot emit that member's value into a log record, a `Problem Details` extension, or a trace attribute |
| **Depends on** | WP-5 |
| **Status** | **Done for the one path that exists.** The compiler reads the attribute in both spellings, the manifest carries a `sensitive` array per contract, and the flow's partial class carries `SensitiveMembers`. The HTTP endpoint replaces matching structured error detail with `[redacted]` before writing the body — proven end to end with a dispatcher that deliberately attaches a secret. |

The attribute previously documented itself as "applied by the generated serialiser… with
no code path able to bypass it", while nothing read it at all. An attribute that reads as
a control while doing nothing is worse than no attribute: a reviewer sees the field
marked and concludes it is handled.

**The exit criterion named three sinks — a log record, a Problem Details extension, and a
trace attribute. Only the second exists.** There is no logging scope, no journal and no
replay view in this release, so there is nothing else to redact from. The criterion is
met for the sink that exists and cannot be met for the two that do not; both the
attribute's remarks and [12-Observability §4](docs/12-Observability.md) say so in those
words rather than implying coverage the release does not have. The remaining sinks arrive
with the observability work in P3 and re-open this.

Redaction matches by member name, case-insensitively, because the wire contract is
camelCase and the member is PascalCase — a capability writing `.With("paymentToken", …)`
is naming `PlaceOrder.PaymentToken`, and a case-sensitive match would let through exactly
the spelling people write. The value is replaced with `[redacted]` rather than dropped: a
key that silently vanishes reads as a field the server never received.

**Also found and fixed while here:** the manifest listed a compensation only as a name
on the step it undoes. `inventory.release` had no entry in `capabilities`, so its
authorisation stance (`Internal`), its side effects and its idempotency reached
nothing, and `flowx diff` could not have seen a breaking change to one. A compensation
is a capability that happens to run backwards; `StepModel.Compensation` is now a whole
`StepModel` rather than three loose strings, and the manifest lists it.

### WP-13 — The diagnostics that were documented and never raised

| | |
|---|---|
| **Goal** | Every diagnostic the docs call a compile error is one |
| **Why** | `FLOWX1014` — "a retry policy on a non-idempotent capability is a compile error" — is the safety property this repository advertises most loudly. `07-Capability-Model.md` said *"FlowX will not let you retry something that is unsafe to retry"*; the diagnostics index listed it as preventing **a duplicate charge**; the sample README told the reader to try it. Nothing raised it. `.WithPolicy(...)` stored the argument's source text, so no rule could ask what was in the set. `FLOWX1018` was in the same state. |
| **Tests first** | A generator test per rule, both directions · the sample itself, built with a Retry attached to `payment.capture` |
| **Deliverable** | `PolicySetReader` resolves a named set to the policies it declares; `FLOWX1014` and `FLOWX1018` raised; policies reach the manifest |
| **Exit** | Adding `.WithPolicy(retry)` to the sample's `CapturePayment` fails the build with `FLOWX1014` |
| **Depends on** | WP-5 |
| **Status** | **Done.** All four are raised, tested in both directions, and verified against the real sample rather than only the harness. Every diagnostic the docs call a compile error now is one. |

A policy set is declared as a fluent chain, so reading one is the same problem as
reading a `Define` body and reuses the same `FlowChainWalker`. A set that is not a field
or property initialiser — one built by a method call — cannot be inspected at compile
time, and the reader returns nothing rather than guessing: a diagnostic derived from a
guess is one nobody can act on.

The manifest now carries each step's policies, which the schema had declared and the
writer had never emitted. That required duplicating the kind-to-stage mapping into the
compiler, because it targets netstandard2.0 and cannot reference `FlowX.Abstractions`.
An unpinned copy of a safety ordering is exactly what drifts silently, so
`PolicyStagesMatchTheAbstraction` reads the real mapping by reflection and fails on any
disagreement, in both directions. It was verified by changing one entry and watching it
fail.

`FLOWX1003` and `FLOWX1004` needed a `DiagnosticAnalyzer` rather than more generator
work, because they read a capability's dependencies and no flow mentions those. Being an
analyzer also means they apply to every capability in the compilation, including one no
flow has a step for yet — a capability that violates Q4 is wrong whether or not anything
calls it. Generic wrappers are unwrapped, so a single `Lazy<>` cannot defeat either rule,
and the generated dispatcher is exempt because it legitimately holds every capability its
flow invokes.

**`FLOWX1003` has a limit, and the page says so.** It matches a list of transport
namespaces; there is no general way to recognise a transport. The sound alternative is an
attribute applied by transport authors, which is worth nothing until they adopt it. A
clean build means "no *known* transport", not proof, and the documentation says that
rather than implying coverage it does not have.

### WP-14 — Budget B12: build overhead

| | |
|---|---|
| **Goal** | Measure the number ADR-0002's revisit clause depends on |
| **Why** | ADR-0002 says to revisit the whole compile-time decision when *"build overhead > 8 % sustained"*. Nothing measured build overhead, so the clause could never have fired. The budget was declared in [14-Performance §1](docs/14-Performance.md) and left unmeasured through P0. |
| **Tests first** | The benchmark itself is the test; committed to the baseline like every other budget |
| **Deliverable** | `CompilerBenchmarks` (the file [14-Performance §7](docs/14-Performance.md) already named for B12) and a report |
| **Exit** | A number for build overhead, with an explicit pass or fail against 8 % |
| **Depends on** | WP-5 |
| **Status** | **Done. PASS at +0.4 % against a +8 % budget.** Report at [docs/benchmarks/B12.md](docs/benchmarks/B12.md). |

**It took two measurements, and the first one was the wrong shape.**

`CompilerBenchmarks` prices the generator in isolation at **~2.9 ms** per compilation
containing one flow. That number is real and committed to the baseline, but it is not a
build-overhead ratio: its control compiled a file with no plan and no dispatcher in it,
so most of the difference was binding code the control did not contain — work an
application written without FlowX would have hand-written and paid for anyway. Reporting
that ratio as build overhead would have overstated the cost by more than an order of
magnitude, which is the same class of claim WP-10 through WP-13 spent their time
removing. It was written up as *not settling the budget*, with what would settle it
spelled out.

`scripts/measure-build-overhead.sh` then did that: two builds of the reference sample
producing the **same final compilation**, differing only in whether the generator ran.
`Ecommerce.csproj` carries an MSBuild condition (`FlowXGeneratorDisabled`) that drops the
analyzer and compiles the previously generated sources as ordinary files, so the sample
genuinely builds both ways.

Over 15 alternating rounds: **2 613 ms with, 2 603 ms without — +0.4 % against a +8 %
budget.** The result is legible from the isolated number: ~3 ms of generator against a
~2.6 s project build is about a tenth of a percent.

**The report states what the figure cannot support.** The two arms' ranges overlap and
the within-arm spread is 14 %, so this cannot distinguish +0.4 % from −0.4 %. It is
evidence that the overhead is nowhere near 8 %, not evidence that it is exactly 0.4 %.
Sharpening it needs dedicated hardware, and no decision waits on the difference between
0.4 % and 2 %.

## 4. P1 — Compiler hardening

The roadmap's [P1 scope](docs/20-Roadmap.md#3-increment-detail), item by item, with
what is already done from P0's overruns marked. P1 exists to mitigate **risk R1** —
generator complexity becoming our own legacy — so its exit criteria are about
maintainability and scale, not features.

| Roadmap item | Where it stands |
|---|---|
| Full DSL: `When`/`Otherwise`, `Switch`, `Parallel`, `ForEach`, `SubFlow` | **WP-15**, part done — `When`/`Otherwise` ship end to end; `Switch`, `Parallel`, `ForEach`, `SubFlow` remain |
| Contract-compatibility checking | **WP-16**, done — as `FLOWX1020`, *step binding* |
| Diagnostics FLOWX1001–1023 with help URIs | **Done** at WP-13. All raised, all tested, all with help links |
| Generator snapshot tests | **Done** at WP-5 and extended since |
| Readable emitted code | **Done** — on disk under `obj/generated`, with per-step `#line` directives (fixed at WP-10) |
| Build-overhead budget B12 | **Done** at WP-14. **+0.4 %** against +8 % |
| *Should:* `flowx diff` v1 | **WP-17**, done |
| *Should:* IDE code fixes | **WP-19**, done |

**Exit criteria, from the roadmap:**

- a 200-flow synthetic solution builds with ≤ 8 % overhead → **WP-18**, measured and
  **FAILING at +23 %**. The harness exists and the number is real; the budget is not met.
  P1 cannot exit on this criterion until it is
- every diagnostic passes `EveryDiagnosticIsHelpful` → **already green**
- emitted code is breakpoint-able → **already true**, and pinned by
  `EachStepGetsItsOwnLineDirective`

### WP-15 — The branching DSL

| | |
|---|---|
| **Goal** | A flow can express a condition, a fan-out and a loop, not only a straight line |
| **Why** | P0 shipped linear flows only, and said so. Every real saga branches; a platform that cannot express `When` sends its users back to writing the control flow by hand, which is the thing it exists to replace. [ADR-0010](docs/adr/ADR-0010-csharp-dsl-over-yaml.md) chose a C# DSL precisely so branching stays type-checked. |
| **Tests first** | A walker test per shape · a golden emitted file per shape · a runtime test proving each shape executes · `StepGraph` invariant tests for a non-linear graph |
| **Deliverable** | `When`/`Otherwise`, `Switch`, `Parallel`, `ForEach`, `SubFlow` through the whole stack: builder surface, model, analysis, emission, `StepGraph`, engine |
| **Exit** | A flow using every shape compiles, runs, appears correctly in the manifest, and renders in `flowx graph` |
| **Depends on** | WP-5 |
| **Status** | **`When` / `Otherwise` done**, through the whole stack. `Switch`, `Parallel`, `ForEach` and `SubFlow` are still open; the exit criterion above is not met until they land. |

The engine's step loop walked an array by index, and **budget B2 is a hard zero** — so
the shape of the change was constrained before it was designed: no allocation per step,
no iterator, no closure per branch.

**Branching did not make the graph a graph.** A conditional compiles into the *same flat
step array* as everything else — a `StepKind.Branch` carrying the false target, and a
`StepKind.Jump` closing the `then` block. The engine gained no branch stack and no
recursion; the only change to the loop is that the index sometimes moves by more than
one. A tree of nested plan objects would have read more naturally and would have cost an
enumerator per level on the hot path. `TakingEitherBranchOfAConditionalAllocatesNothing`
asserts **0 B on both directions** in Release, so B2 survived the DSL's most-used shape.

Termination is not an assumption: `StepGraph` rejects any target that is out of range or
points backwards, which is why that check exists and why the `while` loop is safe.

**The manifest does not carry the predicate.** This package proposed adding a
`condition` string to `$defs/step` holding the predicate's source text, so `flowx graph`
could label the branches. **Rejected.** Predicate text carries business values —
`order.Total > 80` — and the manifest's rule is *structure only, never values*. That rule
is what `ManifestContainsNoSecrets` asserts and what makes the file safe to publish to
consumers who are not entitled to the thresholds inside it. A renderer wanting labels can
read them from source, where the reader is already trusted.

Newly surfaced by this package, and open:

- **`FLOWX1011` (predicate purity) is unimplemented.** It was reserved when nothing could
  declare a predicate. Something can now, and `FlowErrors.PredicateFailed` documents the
  rule at run time that no analyzer enforces at build time. **Closed by WP-21.**
- **The `.Step<TCapability, TStepIn>(map)` overload is not honoured** by `FlowAnalyzer`
  or `FlowEmitter` — it parses and is then ignored, which is worse than not existing.
- **Triggers and capability `errors` are in the manifest schema but never emitted.**

### WP-16 — Step binding

| | |
|---|---|
| **Goal** | A flow whose steps cannot pass values to each other fails the build |
| **Why** | The generated dispatcher binds by type: `ctx.Get<TInput>()`. If no earlier step produced that type the flow compiles and throws on the first request — exactly the class of failure this platform exists to move to build time. |
| **Tests first** | Both directions per rule, plus the reference sample's real shape as a case that must stay silent |
| **Deliverable** | `FLOWX1020` raised by a `DiagnosticAnalyzer`, with its documentation page |
| **Exit** | Reordering the sample's steps fails its build; the unmodified sample still builds clean |
| **Depends on** | WP-5 |
| **Status** | **Done.** Verified by reordering the real sample's steps: `FLOWX1020` fires at the offending `.Step<>` type argument, naming the missing type and what the context can supply. |

**The id is 1020, not the 1022 this package was originally opened against.** The
reserved list assigns 1019–1022 to deadline coherence, **step binding**, sub-flow
cycles and contract compatibility, in that order — and `08-Flow-Definition.md` already
documented this exact check as `FLOWX1020`, with an example message nearly identical to
the one now emitted, as did `FlowContext.Get<T>` and `FlowExecutionContext.Get<T>`.
Shipping it as 1022 would have left three places pointing at a number nothing raised.
`FLOWX1022` stays reserved for contract compatibility **across versions** — the
analyzer counterpart of `flowx diff`, which is a different question.

The analyzer states its own limits rather than implying coverage it lacks: it checks by
**exact declared type**, because the context is a dictionary keyed on `typeof(T)` and a
base-class match would miss at run time; it stops at the first chain method it does not
understand, because a hidden branch may produce the next step's type; and it stays
silent on the explicit-mapping overload, which is the fix it recommends.

### WP-17 — `flowx diff` v1

| | |
|---|---|
| **Goal** | The manifest earns its keep: a breaking change is caught in CI, not by a consumer |
| **Why** | [ADR-0005](docs/adr/ADR-0005-manifest-as-build-artifact.md) makes the manifest a build artifact so it can be *compared*. Until something compares two of them, the artifact is a description nobody acts on. |
| **Tests first** | One test per classification rule, in both directions |
| **Deliverable** | `flowx diff --old --new`, text and JSON output, non-zero exit on a breaking change |
| **Exit** | Removing a capability, narrowing a contract, or loosening an authorisation stance each fail; a line-number change does not |
| **Depends on** | WP-6, WP-9 |
| **Status** | **Done.** 29 rules; wired into CI against a committed baseline. |

**The compatibility unit is `id@major`, not `id@version`.** Exact-version keying reports
a patch bump as a removal plus an addition; identity-only keying lets two side-by-side
majors collide and hides a real removal. Keying on the major gets both right, and
removes any "was the version bumped?" waiver — bumping the major *is* publishing a new
contract, and deleting the old one is what breaks people.

Three classifications worth recording, because each could reasonably have gone the other
way:

- **`[Sensitive]` is asymmetric.** Marking a member is *additive* — a gate that failed
  the build when an engineer marks a password teaches engineers not to mark passwords.
  Un-marking is *breaking*, and the more serious half: the value then reaches logs,
  traces and a journal retained for the replay window, with no signature change to catch
  it.
- **Both directions of an authorisation change are breaking**, under separate codes.
  Relaxing is a security regression. Tightening is the right change and still denies
  callers that worked yesterday — the gate is not saying it is wrong, it is saying that
  shipping it unannounced turns a security improvement into an outage.
- **Adding a side effect is breaking.** Nothing about the call changes, but
  `sideEffects` is what blast-radius review reads and what decides whether an agent
  confirms before invoking a tool. Every assessment made against the baseline is stale.

Deliberately never reported: `source` file:line, `application.version`, a flow's steps,
and array order. A gate that fires on every build is a gate people delete.

### WP-18 — Scale: 200 flows

| | |
|---|---|
| **Goal** | The roadmap's P1 exit criterion, measured rather than assumed |
| **Why** | B12 passed at **+0.4 %** on a sample with *one* flow. The generator's cost scales with flows; the budget was written for a realistic solution, and one flow does not test it. Superlinear behaviour would be a far more important finding than the ratio. |
| **Deliverable** | A synthetic-project generator, a measurement script, and a report |
| **Exit** | 200 flows build within the 8 % budget, and the cost is shown to scale linearly |
| **Depends on** | WP-14 |
| **Status** | **Harness done, budget FAILED at +23 %.** Reported as a failure rather than rounded off — the measurement is the deliverable, and the number it produced is the honest one. See [B12-scale](docs/benchmarks/B12-scale.md). |

**The +23 % is not yet a verdict.** It was measured on a machine under load average
2–34, with an 85 % spread *within* a single arm — wide enough that the arms overlap and
the ratio is not separable from the noise. The finding that matters is therefore
provisional in magnitude but not in direction: at 200 flows the generator costs
materially more than at one, where B12 measured +0.4 %. Two things must happen before
this criterion is closed either way:

1. **Re-measure on a quiet machine.** Until the within-arm spread is small relative to
   the difference between arms, neither a pass nor a fail is trustworthy.
2. **Establish the shape, not just the ratio.** The roadmap's real question is whether
   cost grows linearly with flow count. Superlinear growth at 200 flows would be a far
   more important finding than any single percentage, and would change what gets fixed.

The CI job added here is **advisory** (`continue-on-error: true`): it publishes the
number on every run without failing the build on a measurement whose noise floor is
larger than its budget. Gating on it while it cannot separate signal from load would
teach people to re-run CI until it passes, which is worse than not gating at all. It
becomes a gate when (1) above is satisfied.

### WP-19 — IDE code fixes

| | |
|---|---|
| **Goal** | Every diagnostic that has one obvious fix offers it |
| **Why** | A diagnostic tells you that you are wrong; a code fix tells you what right looks like. `FLOWX1001` (add `partial`) and `FLOWX1010` (declare an authorisation stance) are mechanical, and leaving them manual is friction on every new flow. |
| **Deliverable** | A `CodeFixProvider` for the mechanically fixable diagnostics |
| **Exit** | The fix applies cleanly in a test harness and produces compiling code |
| **Depends on** | WP-13 |
| **Status** | **Done.** `FLOWX1001`, `FLOWX1010` and `FLOWX1017`. Tests apply each fix to the reference sample's own files and assert byte equality with what is on disk. |

**The fixes ship in their own assembly, and it must not reference the compiler.** The
original instruction for this package said to add a `ProjectReference` from
`FlowX.Compiler.CodeFixes` to `FlowX.Compiler`. That was wrong: both are
`DevelopmentDependency` analyzer assets, a development dependency does not flow
transitively, and the host would be handed an assembly whose reference it cannot
resolve. A compiler extension that fails to load is dropped **in silence** — it would
have surfaced as "the quick actions do not appear on my machine". The diagnostic ids are
string literals instead, pinned against `FlowXDiagnostics.All` by a fitness test in the
test project, which may reference both; a second fitness test asserts the seam itself.

**What the `FLOWX1010` fix refuses is the substance of it.** It offers `Authenticated`
and `Internal` only. `Public` would clear a security error with one keystroke and make
the capability world-readable — the outcome the rule exists to prevent. `Permission` and
`Policy` each need a name nothing in the source implies, and nothing rejects the stance
without it, so a fix emitting one would produce a declaration that compiles, reads as
enforced, and reaches the manifest as a claim about access control that nothing backs.
There is no Fix All for it either.

Not fixed, deliberately: `FLOWX1014` (the only mechanical repairs are asserting an
idempotency the tool cannot verify, or deleting the retry — and the diagnostic is what
prevents a duplicate charge), `FLOWX1018` (the repair is splitting a capability in two),
and `FLOWX1024` (suppression needs a `FLOWX-DEBT` owner and expiry a tool cannot
invent).

---

## 5. Definition of Ready

A work package may start only when all are true. This prevents the most common
failure mode in a spec-heavy project: building something the spec describes but
nobody can verify.

- [ ] Its exit criterion is mechanically checkable (a command, not a judgement)
- [ ] Its tests-first artifacts are named
- [ ] Its dependencies are complete
- [ ] The documentation section it implements is identified

---

## 6. Estimation and staffing

Deliberately absent. This is a specification-driven project with one
contributor's throughput unknown; a date column here would be fiction, and
fiction in a plan is worse than a blank. Sequencing and exit criteria are the
real content — they hold regardless of pace.

The [roadmap gantt](docs/20-Roadmap.md) carries indicative durations for
phase-level planning only.

---

## 7. Open items blocking the plan

| # | Item | Blocks | Owner |
|---|---|---|---|
| 1 | Three infographic PNGs must be committed to `docs/assets/` — see [the asset manifest](docs/assets/README.md) | CI `docs` job | repository owner |
| 2 | ~~Never compiled~~ **Resolved.** SDK 10.0.110 installs from the Ubuntu archive; the official installer hosts are proxy-blocked but `packages.microsoft.com` is not | — | — |
| 3 | ~~Stray `claude/` branch on the remote~~ **Resolved.** Deleted | — | — |
| 4 | `SONAR_TOKEN` repository secret not configured; the `sonar` job no-ops without it | Sonar gate | repository owner |
| 5 | ~~Benchmarks recorded on shared container hardware with 10 iterations~~ **Resolved at WP-11**, without dedicated hardware. Re-recorded at 30 iterations; the 29× margin is ~11× clear of the worst observed noise factor (2.6×), and [P0.md §5](docs/benchmarks/P0.md) argues the case rather than assuming it. Timing figures remain advisory in the baseline | — | — |

Item 1 is the only one blocking a green CI run.

---

**Back to:** [README](README.md) · [Checklist](CHECKLIST.md) · [Roadmap](docs/20-Roadmap.md) · [Quality gates](docs/21-Quality-Gates.md)

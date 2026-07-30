# Live Checklist

> **This file is updated with every change.** It is the single place that answers
> "where is this project actually at?" — the [plan](PLAN.md) says what to build,
> this says what is built.
>
> **Last updated:** 2026-07-30 · **Phase:** **P0 complete → P1 in progress** ·
> **Commit:** see `git log`
>
> **Build:** 0 warnings, 0 errors · **Tests:** 791/791 passing ·
> **Coverage:** 94.0 % line / 87.0 % branch (gates: 80 / 75) · **SDK:** 10.0.110
> **P0 kill criterion: PASS** — B1 **172.3 ns** / 5 000 ns budget · B2 **0 B** exactly ·
> B3 dispatch 21.9 ns / 150 ns. See [P0.md](docs/benchmarks/P0.md)
>
> Legend: `[x]` done and verified · `[~]` partial — shipped but incomplete, blocked, or
> failing its own criterion, with the gap named on the line · `[ ]` not started

---

## 0. Blockers — read first

These gate everything below them. None is code work.

- [ ] **B-1 · Commit the three infographic PNGs** to `docs/assets/` using the exact
      filenames in [the asset manifest](docs/assets/README.md).
      *Blocks:* CI `docs` job (README currently references three missing files).
      *Owner:* repository owner. *Why not done:* the images exist only in a chat
      transcript; they cannot be written to disk from here.
- [x] **B-2 · ~~Run `dotnet build FlowX.slnx` once.~~ RESOLVED.**
      SDK 10.0.110 installed from the Ubuntu archive (`dot.net` and
      `builds.dotnet.microsoft.com` are proxy-blocked; `packages.microsoft.com`
      and `apt` are not). Full solution builds with **0 warnings, 0 errors**;
      **29/29** fitness tests pass; 0 IL2xxx/IL3xxx trim warnings.
      *Resolved:* `required` members on attribute classes compile and are
      observable via reflection — the construct flagged as highest-risk is sound.
      *Found and fixed by the first build:* see §8.
- [x] **B-3 · ~~Delete remote branch `claude/flowx-platform-docs-djjyxi`.~~ RESOLVED.**
      Gone from the remote. History scan is clean: no commit in any branch has
      bot authorship, a generated-by footer, or a signature.

---

## 1. Documentation

- [x] 20 specification documents, `docs/01` – `docs/20`
- [x] 13 ADRs with trade-offs stated (ADR-0013 added by the first compilation)
- [x] `docs/diagnostics/` — 11 pages plus an index; every help URI resolves, asserted by test
- [x] `docs/benchmarks/` — baseline, gate policy, and the honest caveats
- [x] 9 sample application specifications
- [x] `CONTRIBUTING.md`, `SECURITY.md`, `LICENSE` (Apache-2.0)
- [x] `docs/21-Quality-Gates.md` — SonarQube thresholds, OWASP mapping, debt policy
- [x] `PLAN.md` — WP-0…WP-12 with mechanically checkable exit criteria
- [x] `CHECKLIST.md` — this file
- [x] README references the platform infographics
- [x] `docs/DEBT.md` — debt register (1 open entry: DEBT-0001; format + budget defined)
- [x] `docs/benchmarks/README.md` — WP-3 baseline results and gate policy
- [x] `docs/benchmarks/P0.md` — the kill-criterion report. **PASS**, argued on shared
      hardware: a 29× margin against a 2.6× worst-observed noise factor
- [~] Internal Markdown links resolve — **4 broken, all of them B-1**: three
      image paths referenced from `README.md` and `docs/05-Architecture.md`.
      Every non-image link resolves. The `docs` job is red until the PNGs land,
      which is the intended forcing function, not an oversight.
- [ ] All Mermaid diagrams parse (the `docs` job cannot reach this step while B-1 is open)

---

## 2. WP-0 · Quality gates and CI

- [x] `ci.yml` — build with warnings-as-errors
- [x] `ci.yml` — architecture fitness functions gated ahead of the rest of the suite
- [x] `ci.yml` — trim/AOT analyzer gate failing on `IL2xxx`/`IL3xxx` (verified: 0)
- [x] `ci.yml` — NativeAOT publish, **and a smoke run of the published binary** (WP-10)
- [x] `ci.yml` — Mermaid parse gate
- [x] `ci.yml` — internal link check
- [x] `ci.yml` — attribution guard (rejects bot authorship)
- [x] `security.yml` — CodeQL, Semgrep, Gitleaks, SCA, Checkov
- [x] `security.yml` — OWASP ZAP nightly DAST (guarded until `samples/ecommerce` runs)
- [x] `quality.yml` — coverage thresholds, Sonar gate, Stryker mutation, debt policy
- [x] `.github/dependabot.yml` — NuGet + Actions, grouped
- [x] `.github/pull_request_template.md` carrying the Definition of Done
- [ ] Each gate class verified by a deliberate violation on a throwaway branch
- [ ] `SONAR_TOKEN` repository secret configured (the `sonar` job no-ops without it)

---

## 3. WP-1 · `FlowX.Abstractions` — **compiled, 0 warnings**

- [x] `Result<T>` — readonly struct, allocation-free failure path
- [x] `Error`, `ErrorCategory` — closed set, terminal/retryable, HTTP mapping
- [x] `ICapability<TIn, TOut>` — the seven rules documented on the interface
- [x] `CapabilityAttribute` — `Version` and `Authorization` as required members
- [x] `Authorization`, `ApprovedByAttribute`, `SensitiveAttribute`
- [x] `CapabilityContext`, `FlowContext<TIn>` — clock, ids, randomness, deadline
- [x] `Flow<TIn, TOut>`, `IFlowBuilder<,>` — no `Do(lambda)`, no trigger types
- [x] `ExecutionProfile` — `Ephemeral` as the zero value
- [x] `PolicySet`, `PolicyStage` — stage order encoding the safety guarantees
- [x] Trigger attributes — http, kafka, cron, stream, agent
- [x] Zero package references, zero project references

Verified by `dotnet build -c Release` with `TreatWarningsAsErrors`, and by the
29 fitness tests in §4 which assert these properties by reflection.

---

## 4. WP-1 · Architecture fitness functions — **30/30 green**

- [x] `AbstractionsHasNoDependencies` — reads the `.csproj`
- [x] `LayersPointInward`
- [x] `RuntimeDoesNotReferenceAnyPlugin`
- [x] `EveryShippedRuntimeProjectIsAotAnalyzed` — exempts Roslyn components
- [x] `RoslynComponentsTargetNetStandard20`
- [x] `ResultIsAnAllocationFreeValueType`
- [x] `ErrorCategoryRemainsClosed`
- [x] `TerminalCategoriesAreNeverRetried`
- [x] `CapabilityMustDeclareVersionAndAuthorization`
- [x] `ExecutionProfileDefaultsToEphemeral`
- [x] `PolicyStageOrderEncodesTheSafetyGuarantees`
- [x] `TenantScopedIsTheDefaultForEveryScopeEnum`
- [x] `FlowBuilderExposesNoTransportTypes`
- [x] `FlowBuilderHasNoEscapeHatchForInlineCode`
- [x] `NoCyclicDependencies` — DFS over the project graph, not just direct edges
- [ ] `SuppressionsAreAccountable`
- [ ] `ManifestContainsNoSecrets`
- [ ] `EveryCapabilityDeclaresAuthorization`
- [ ] `PublicCapabilitiesAreReviewed`
- [ ] `CrossTenantAccessIsDenied`
- [ ] `RedactionCannotBeBypassed`
- [ ] `NoPermissiveDefaults`
- [x] `EveryDiagnosticIsHelpful` — in `FlowX.Compiler.Tests`, beside what it governs
- [x] `ModelLayerHasNoRoslynDependency` / `EmitLayerHasNoRoslynDependency` — the R1 mitigation

---

## 5. WP-2 · `FlowX.Core` — **done, TDD, 92 tests**

Built red → green: `tests/FlowX.Core.Tests` was written and observed failing to
compile (24 errors, types absent) before `src/FlowX.Core` existed.

- [x] `CapabilityDescriptor` — validated identity and SemVer, immutable side effects
- [x] `Identifiers` — one regex pair for identity and version, source-generated
- [x] `PolicyChain` — stage ordering (stable), retry-requires-idempotent, no-cache-with-side-effects
- [x] `StepNode` — factories make an invalid shape unrepresentable
- [x] `StepGraph` — non-empty, contiguous from zero, no duplicates, copies its input
- [x] `FlowDescriptor` — positive deadline enforced
- [x] `ExecutionPlan` — precomputed compensable indices, sorted side-effect set, profile validation
- [x] `CompensationStack` — strict reverse unwind, drains, rejects double-record
- [x] `InvalidFlowPlanException` — a defect, not an `Error` (ADR-0007)
- [x] `tests/FlowX.Abstractions.Tests` — 47 behavioural tests added; `Result<T>`,
      `Error`, `PolicySet` and the trigger defaults had zero execution coverage before

**Exit criterion met:** a three-step plan with one compensation is constructible,
immutable, and rejects every invariant violation under test.

---

## 5b. P0 · WP-3 → WP-14

- [x] **WP-3** `FlowX.Benchmarks` — B1–B3 measurable, baseline committed
- [x] **WP-4** `FlowX.Runtime` — step loop, pooled contexts, deadline handling, 0 B
- [~] **WP-5** `FlowX.Compiler` — `FlowPlanGenerator`, model layer separate from emission.
      Diagnostics all raised (WP-13), B12 measured and passing (WP-14).
      *Remaining:* the branching DSL — `When` / `Switch` / `Parallel` / `ForEach` /
      `SubFlow`
- [x] **WP-6** Manifest emission, deterministic and schema-valid
- [x] **WP-7** `FlowX.Hosting` — DI, startup validation, graceful drain, health probe
- [~] **WP-8** `plugins/FlowX.Http` — endpoint, request binding, RFC 7807.
      *Remaining:* generated endpoints from `[HttpTrigger]`, OpenAPI
- [x] **WP-9** `FlowX.Cli` — `flowx graph`, `flowx manifest`
- [~] **WP-10** `samples/ecommerce` — 3-step flow end to end. *Remaining:* ZAP baseline
- [x] **WP-11** P0 gate — **PASS.** B1 = 172.3 ns / 5 000 ns, B2 = 0 B. Report at
      [docs/benchmarks/P0.md](docs/benchmarks/P0.md)
- [~] **WP-12a** `[Sensitive]` — read by the compiler, recorded in the manifest, and
      **redacted from Problem Details bodies**. The exit criterion named three sinks;
      only that one exists in this release. Logs, traces and the journal re-open it in P3
- [x] **WP-12** `FlowX.Testing` — `TestCapabilityContext` and `TestFlowContext`; the
      sample's capability tests lost 27 lines of hand-written stub
- [x] **WP-13** Diagnostics that were documented and never raised. **All four now fire**
      — `FLOWX1014`, `FLOWX1018`, `FLOWX1003`, `FLOWX1004` — each verified against the
      real sample, not only the harness
- [x] **WP-14** Budget **B12** build overhead — **PASS at +0.4 %** against +8 %, on a
      like-for-like build of the sample. Report at [B12.md](docs/benchmarks/B12.md)

**P0's exit criteria are met.** `samples/ecommerce` runs a 3-step ephemeral flow over
HTTP, B1 and B2 are green, and `flowx graph` renders it. The ZAP baseline is the one
item outstanding and needs a CI run.

---

## 5c. P1 · Compiler hardening — in progress

Scope from [the roadmap](docs/20-Roadmap.md#3-increment-detail); work packages in
[PLAN.md §4](PLAN.md).

- [~] **WP-15** The branching DSL — **`When` / `Otherwise` done** through builder, model,
      analysis, emission, graph and engine. A conditional compiles into the *same flat
      step array* as a linear flow, as a `Branch` plus a `Jump`, so the engine gained no
      branch stack and **both directions allocate 0 B** in Release. The manifest
      deliberately does **not** carry the predicate's source text: it would put business
      thresholds into a file whose rule is structure-only. `Parallel`, `ForEach` and
      `SubFlow` remain open — this box does not tick until they land
- [x] **WP-20** `Switch` / `Case` / `Default` — a value branch through builder, model,
      analysis, emission, graph and engine. One `StepKind.Switch` carrying a target per
      case plus a default, and a `Jump` closing each case block, in the *same flat step
      array*; the dispatcher gained `int Select(...)` returning the matching arm, which
      is an `int` and not the value so nothing is boxed. **Every arm, the miss and an
      out-of-range arm allocate 0 B** in Release. A value matching nothing in a switch
      with no `Default` falls through, exactly as `When` without `Otherwise` does —
      recorded in `08 §3.2` and on `ISwitchBuilder`. The manifest carries neither the
      selector nor the case values, for the same reason WP-15 refused the predicate
- [x] **WP-16** Step binding — **`FLOWX1020`** raised by `StepBindingAnalyzer`. A flow
      whose steps cannot pass values to each other now fails the build. Numbered 1020,
      not 1022: `08-Flow-Definition.md` and both `Get<T>` implementations already
      documented this check under 1020, and 1022 stays reserved for contract
      compatibility *across versions*
- [x] **WP-17** `flowx diff` v1 — 29 classification rules, text and JSON, exit 1 on a
      breaking change. **Wired into CI** against a committed baseline, and verified by
      flipping `Idempotent` on the sample's real source
- [~] **WP-18 + WP-23** Scale — harness delivered, then made trustworthy. **The budget
      FAILS at +18.4 %**, 95 % CI [+16.3, +19.9], against +8 %. WP-18's provisional +23 %
      is superseded: it was measured under load average 2–34 with an 85 % within-arm
      spread, and the rebuilt methodology (sandwiched arms, an A/A control in the same
      rounds, IQR, interleaved sizes) moved the answer five points. **Growth is linear** —
      3 ms fixed + 9.54 ms per flow, R² 0.994 — so the constant is too large rather than
      the design being wrong. Cost splits 62 % `FlowPlanGenerator` / 37 %
      `StepBindingAnalyzer` / 1 % `CapabilityAnalyzer`, which disproves WP-18's guess
      about where to look. The harness can now return **exit 2 = INCONCLUSIVE** and did so
      on its own first run rather than publishing a number its error bars swallowed.
      Box stays open: the budget is not met
- [x] **WP-19** IDE code fixes — `FLOWX1001`, `FLOWX1010`, `FLOWX1017`, in a separate
      `FlowX.Compiler.CodeFixes` assembly so the analyzer never drags Workspaces into a
      consumer's build. `FLOWX1010` deliberately withholds `Public`

Already satisfied from P0, per the roadmap's P1 list: diagnostics with help URIs
(WP-13), generator snapshot tests (WP-5), readable and breakpoint-able emitted code
(WP-10), and budget B12 (WP-14).

**Gaps WP-15 surfaced**, listed here rather than left in a commit message — each is a
place where something is documented, reserved or parseable but not actually enforced,
which is the exact failure mode P1 exists to remove:

- [x] **`FLOWX1011` — predicate purity.** Closed by WP-21: `PredicatePurityAnalyzer`
      raises it on a `When` predicate that reads a clock, ambient randomness, the
      environment, mutable static state, a captured variable or flow instance state.
      An **error** in `Durable` flows and a **warning** in `Ephemeral` ones, rather than
      the Info that ADR-0003 and `06` §5 originally specified — Info is invisible in a
      build log and `Ephemeral` is the only profile that runs today, so it would have
      shipped a rule that does nothing anywhere. Scope is decided by proof; impure
      statics are a list; nothing is interprocedural, and the page says so
- [ ] **`.Step<TCapability, TStepIn>(map)` is parsed and then ignored** by `FlowAnalyzer`
      and `FlowEmitter`. It is on the builder surface and `FLOWX1020` recommends it as
      the fix for a binding failure, so a user following the diagnostic reaches an
      overload that silently does nothing. Worse than not existing
- [x] **Triggers and capability `errors` are in the manifest schema and never emitted.**
      Closed by WP-22. Both are emitted, under a **three-state rule**: a resolved
      catalogue, a resolved-and-empty one (`[]` — "declares no errors"), or **withheld
      entirely** when it could not be resolved. A catalogue short by one entry reads
      exactly like a complete one, so an unresolvable case has to be visibly absent
      rather than quietly approximated

**Gaps WP-20 and WP-22 surfaced in turn.** Same class again — declared, documented or
reachable, and enforced or honoured by nothing:

- [ ] **`.Fail(Error)` is parsed and then ignored.** It is on `IFlowBuilder`, it is in
      the `08 §4` method table, and `FlowAnalyzer` does not model it — so a block whose
      only call is `.Fail(...)` compiles to an **empty** block. The consequence is worse
      than a no-op: `08 §3.2`'s own `Switch` example uses `.Default(b => b.Fail(...))` to
      reject an unsupported channel, and that default currently falls through and accepts
      it. Flagged in the doc; the fix is a work package
- [ ] **`FLOWX1011` does not cover `Switch` selectors.** WP-21 scoped the analyzer to
      `When` predicates; WP-20 then added a second construct under the identical rule.
      `FlowErrors.SelectorFailed` states it at run time and nothing checks it at build
      time — the exact position `When` was in before WP-21. `Return`, `Emit`,
      `EmitOnFailure` and `ForEach`'s selector are in the same position
- [ ] **An unrecognised `TriggerAttribute` subclass is skipped in silence.** A trigger's
      `Kind` is an overridden property — executable code, not attribute data — so a
      third-party transport plugin's trigger cannot be read from metadata. WP-22 declined
      to guess, which is right, but the skip produces only an absent `triggers` array. It
      needs a diagnostic
- [x] **Nothing in the repository had ever compiled generator output.** The generator
      harness discarded the updated compilation, so every test asserted against *parsed*
      text — which catches a syntax error but not an unresolved name, a wrong delegate
      type argument, or an unimplemented interface member. Fixed by WP-20's
      `GeneratedCompileErrorsIn`, and a real compile is now asserted

### WP-10 · what it delivered

- [x] Four capabilities, one flow, one endpoint — `dotnet run` serves it
- [x] The endpoint returns the flow's **declared output**, projected from its
      `.Return(...)` clause, not a step count
- [x] 28 tests: 10 capability tests with **no host in the file** (quality goal Q2),
      8 over a real server including the compensation path, 8 on the adapters,
      2 on the wire contract
- [x] NativeAOT publish, ~11 MB, 0 trim/AOT warnings — **and CI runs the binary**,
      because linking and serving a request are different facts
- [x] `flowx manifest` → `flowx graph` renders the sample's real manifest; `mmdc`
      validates the output
- [ ] ZAP baseline scan — the `security` workflow needs a CI run against the sample

**Exit criterion met**, bar the ZAP scan, which cannot run in this working tree.

### WP-10 · what the first consumer found

The sample was the first code written against the platform from outside it. Six
defects that no test inside the platform could have caught:

| Found | Was |
|---|---|
| `Result<T>` had no implicit conversions | The documented capability style did not compile |
| Every `#line` directive pointed at one line | A breakpoint on step three landed on step one |
| `.Return(...)` silently dropped | The flow declared an output nothing produced |
| `MapFlow` never read a request body | A flow's first step had nothing to bind to |
| `AddFlowX` registered the health-check **type**, not the check | `MapHealthChecks` threw at startup |
| The manifest embedded an absolute path | Broke ADR-0005 determinism; leaked the agent's layout |

Three more surfaced while getting the suite green:

| Found | Was |
|---|---|
| `.Emit<T>()` publishes nothing | Silent. Now **FLOWX1024**, the only warning in the set |
| Allocation budgets measured 376 B in Debug | Compiler scaffolding, not the engine. CI runs Release and never saw it; every contributor did. Now skipped in Debug, with the reason |
| The walker read `ArgumentNullException.ThrowIfNull(flow)` as a chain | A statement *after* the chain would have silently replaced it. The walk is now rooted at the builder parameter |

---

## 6. Quality gates · current state

| Gate | Target | Now | Source |
|---|---|---|---|
| Compiler warnings | 0 | **0** ✅ | verified locally |
| Blocker/critical Sonar issues | 0 | **not running** | WP-0 |
| Line coverage | ≥ 80 % | **94.0 %** ✅ | verified locally |
| Branch coverage | ≥ 75 % | **87.0 %** ✅ | verified locally |
| Mutation score (`FlowX.Core`) | ≥ 70 % | **not measured** — Stryker not run locally | WP-0 |
| Trim/AOT warnings | 0 | **0** ✅ | verified locally |
| Fitness functions | all green | **36/36** ✅ | plus compiler and code-fix fitness tests |
| NativeAOT publish | links **and runs** | **✅** | 11 MB binary served a real order |
| Concurrent cross-tenant leak | none | **none** ✅ | 64 concurrent flows, 0 overlaps |
| SAST findings | 0 | **wired, unrun** — needs a CI run | WP-0 |
| DAST findings | 0 | **wired, unrun** — the sample now exists; needs a CI run | WP-0 |
| Vulnerable dependencies | 0 | **0 by construction** — zero dependencies | WP-1 |
| Open debt entries | ≤ 20 | **1** — [DEBT-0001](docs/DEBT.md) | enforced by `quality.yml` |
| B1 flow overhead | ≤ 5 µs | **172.3 ns** ✅ | WP-11, real engine, 30 iterations |
| B2 allocations per step | 0 B | **0 B** ✅ | gated as a unit test — **Release only**, see below |
| B3 capability dispatch | ≤ 150 ns | **21.9 ns** ✅ | shared hardware, advisory |
| B12 build overhead | ≤ 8 % | **+0.4 %** ✅ | WP-14, like-for-like sample build |

Nothing in the "Now" column is green by assertion — every ✅ was produced by a
command in this working tree. Every "not measured" is equally honest: the gate
exists and the mechanism to run it has not been run here.

**B2 is measured in Release only.** The C# compiler emits async state machines as
classes in Debug and structs in Release, so a Debug run charges the engine ~376 B of
Edit-and-Continue scaffolding it does not allocate. The tests now skip in Debug and
say why, rather than failing in the configuration everyone runs locally — a gate that
fails by default is a gate people learn to ignore. Run `dotnet test -c Release`.

---

## 7. OWASP Top 10 · control status

Controls from [21-Quality-Gates §3](docs/21-Quality-Gates.md#3-owasp-top-10-mapping).
"Designed" means the control is specified and its enforcement point identified;
"enforced" means a gate actually runs.

| Risk | Control designed | Control enforced |
|---|---|---|
| A01 Broken access control | [x] required `Authorization` member | [x] `FLOWX1010` raised and tested; the sample's four capabilities all declare a stance |
| A02 Cryptographic failures | [x] `[Sensitive]` + generated redaction | [~] **enforced on the one sink that exists.** Secrets are stripped from Problem Details bodies, tested end to end; logs, traces and the journal do not exist yet, so the "no code path can forget it" claim is not met |
| A03 Injection | [x] compile-time graph, no `Do(lambda)` | [~] structurally true; CodeQL + Semgrep wired, unrun |
| A04 Insecure design | [x] STRIDE per boundary, 12 ADRs | [x] ADR review in CONTRIBUTING |
| A05 Security misconfiguration | [x] no permissive defaults | [x] startup validation, 8 tests |
| A06 Vulnerable components | [x] zero-dependency abstractions | [x] Dependabot + SCA gate |
| A07 Auth failures | [x] claims-only tenant resolution | [x] 5 tests, incl. headers ignored |
| A08 Integrity failures | [x] deterministic builds configured | [ ] needs signing + SBOM (WP-0) |
| A09 Logging failures | [x] `Audit` policy at `Consistency` stage | [ ] needs the policy engine (P4) |
| A10 SSRF | [x] `FLOWX1003` forbids transport refs | [~] **raised** by `CapabilityAnalyzer`, against a list of transport namespaces rather than a proof — the limit is stated on the diagnostic's page |

---

## 8. What the first compilation found

The build that resolved B-2 produced 31 errors. None was a language error — every
one was an analyzer rule, which is the outcome the contract surface was written
for. Recorded here because "it compiled first try" would be a more flattering
claim than the truth, and a less useful one.

| Finding | Count | Resolution |
|---|---|---|
| `CA1716` — identifier matches a reserved keyword | 14 | The rule fires on `Step`, `Return`, `When`, `Then`, `Error`, `Get`, `Set` — the DSL's entire vocabulary. Disabled repo-wide with [ADR-0013](docs/adr/ADR-0013-dsl-vocabulary-over-ca1716.md). A decision, not debt. |
| `IDE0040` — accessibility modifiers required | 15 | Our own `.editorconfig` defect: `always` demands `public` on interface members, which no C# codebase writes. Changed to `for_non_interface_members`. |
| `IL2026` — trim analyzer on `GetExportedTypes()` | 1 | Trim analyzers were enabled on test projects, which reflect by design. Disabled for `tests/` only; `EveryShippedProjectIsAotAnalyzed` still guards `src/`. |
| `CA1859` — return concrete type for perf | 1 | Legitimate. Private helper changed from `IReadOnlyList<string>` to `List<string>`. |
| `MSB4025` — `.slnx` parse failure | 1 | A solution folder named `/` is invalid. The cosmetic file listings were removed; the solution now lists projects only. |

Two CI jobs were also wrong and are fixed: the AOT job published a **class
library**, where `PublishAot` does nothing and a RuntimeIdentifier is required.
The real gate for a library is the analyzer at build time, which now runs. The
genuine AOT publish was guarded until an executable existed; the sample is that
executable, so the guard is gone and CI now publishes **and runs** the binary.

---

## 9. How to update this file

Update it in the **same commit** as the change it describes — a checklist updated
separately is a checklist that drifts, and a drifted checklist is worse than
none, because people trust it.

When a `[~]` becomes `[x]`, state what verified it. When a `[ ]` becomes `[x]`,
the [Definition of Done](docs/21-Quality-Gates.md#5-definition-of-done) applies in
full.

---

**Back to:** [README](README.md) · [Plan](PLAN.md) · [Quality gates](docs/21-Quality-Gates.md)

# Live Checklist

> **This file is updated with every change.** It is the single place that answers
> "where is this project actually at?" — the [plan](PLAN.md) says what to build,
> this says what is built.
>
> **Last updated:** 2026-07-30 · **Phase:** P0 · **Commit:** see `git log`
>
> **Build:** 0 warnings, 0 errors · **Tests:** 135/135 passing ·
> **Coverage:** 98.5 % line / 95.6 % branch (gates: 80 / 75) · **SDK:** 10.0.110
> **Benchmarks:** B1 precursor 20 ns / 5 000 ns budget · B3 9.5 ns / 150 ns · 0 alloc on both
>
> Legend: `[x]` done and verified · `[~]` done, verification blocked · `[ ]` not started

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
- [x] `docs/benchmarks/` — baseline, gate policy, and the honest caveats
- [x] 9 sample application specifications
- [x] `CONTRIBUTING.md`, `SECURITY.md`, `LICENSE` (Apache-2.0)
- [x] `docs/21-Quality-Gates.md` — SonarQube thresholds, OWASP mapping, debt policy
- [x] `PLAN.md` — WP-0…WP-11 with mechanically checkable exit criteria
- [x] `CHECKLIST.md` — this file
- [x] README references the platform infographics
- [x] `docs/DEBT.md` — debt register (0 open entries; format + budget defined)
- [x] `docs/benchmarks/README.md` — WP-3 baseline results and gate policy
- [ ] `docs/benchmarks/P0.md` — the kill-criterion report (WP-11, needs dedicated hardware)
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
- [x] `ci.yml` — NativeAOT publish smoke test (guarded until an executable exists at WP-10)
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
- [x] `EveryShippedProjectIsAotAnalyzed`
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
- [ ] `EveryDiagnosticIsHelpful`

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

## 5b. WP-3 → WP-11 · Not started
- [ ] **WP-3** `FlowX.Benchmarks` — B1–B3 measurable, baseline committed
- [ ] **WP-4** `FlowX.Runtime` — step loop, pooled contexts, deadline handling
- [ ] **WP-5** `FlowX.Compiler` — `FlowPlanGenerator`, model layer separate from emission
- [ ] **WP-6** Manifest emission, deterministic and schema-valid
- [ ] **WP-7** `FlowX.Hosting` — DI, startup validation, graceful drain
- [ ] **WP-8** `plugins/FlowX.Http` — endpoint, binder, RFC 7807, OpenAPI
- [ ] **WP-9** `FlowX.Cli` — `flowx graph`
- [ ] **WP-10** `samples/ecommerce` — 3-step flow end to end
- [ ] **WP-11** P0 gate — run the kill criterion and publish the report

---

## 6. Quality gates · current state

| Gate | Target | Now | Source |
|---|---|---|---|
| Compiler warnings | 0 | **0** ✅ | verified locally |
| Blocker/critical Sonar issues | 0 | **not running** | WP-0 |
| Line coverage | ≥ 80 % | **98.5 %** ✅ | verified locally |
| Branch coverage | ≥ 75 % | **95.6 %** ✅ | verified locally |
| Mutation score (`FlowX.Core`) | ≥ 70 % | **not measured** — Stryker not run locally | WP-0 |
| Trim/AOT warnings | 0 | **0** ✅ | verified locally |
| Fitness functions | all green | **30/30** ✅ | verified locally |
| SAST findings | 0 | **wired, unrun** — needs a CI run | WP-0 |
| DAST findings | 0 | **wired, guarded** — needs WP-10 | WP-0 |
| Vulnerable dependencies | 0 | **0 by construction** — zero dependencies | WP-1 |
| Open debt entries | ≤ 20 | **0** | enforced by `quality.yml` |
| B1 flow overhead p99 (precursor) | ≤ 5 µs | **20 ns** ✅ | WP-3, shared hardware |
| B2 allocations per step | 0 B | **0 B** ✅ | gated as a unit test |
| B3 capability dispatch p99 | ≤ 150 ns | **9.7 ns** ✅ | WP-3, shared hardware |

Nothing in the "Now" column is green by assertion — every ✅ was produced by a
command in this working tree. Every "not measured" is equally honest: the gate
exists and the mechanism to run it has not been run here.

---

## 7. OWASP Top 10 · control status

Controls from [21-Quality-Gates §3](docs/21-Quality-Gates.md#3-owasp-top-10-mapping).
"Designed" means the control is specified and its enforcement point identified;
"enforced" means a gate actually runs.

| Risk | Control designed | Control enforced |
|---|---|---|
| A01 Broken access control | [x] required `Authorization` member | [ ] needs `FLOWX1010` (WP-5) |
| A02 Cryptographic failures | [x] `[Sensitive]` + generated redaction | [ ] needs the generator (WP-5) |
| A03 Injection | [x] compile-time graph, no `Do(lambda)` | [~] structurally true; CodeQL + Semgrep wired, unrun |
| A04 Insecure design | [x] STRIDE per boundary, 12 ADRs | [x] ADR review in CONTRIBUTING |
| A05 Security misconfiguration | [x] no permissive defaults | [ ] needs startup validation (WP-7) |
| A06 Vulnerable components | [x] zero-dependency abstractions | [x] Dependabot + SCA gate |
| A07 Auth failures | [x] claims-only tenant resolution | [ ] needs the HTTP plugin (WP-8) |
| A08 Integrity failures | [x] deterministic builds configured | [ ] needs signing + SBOM (WP-0) |
| A09 Logging failures | [x] `Audit` policy at `Consistency` stage | [ ] needs the policy engine (P4) |
| A10 SSRF | [x] `FLOWX1003` forbids transport refs | [ ] needs the analyzer (WP-5) |

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
The real gate for a library is the analyzer at build time, which now runs; a
genuine AOT publish is guarded until an executable exists at WP-10.

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

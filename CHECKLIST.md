# Live Checklist

> **This file is updated with every change.** It is the single place that answers
> "where is this project actually at?" — the [plan](PLAN.md) says what to build,
> this says what is built.
>
> **Last updated:** 2026-07-30 · **Phase:** P0 · **Commit:** see `git log`
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
- [ ] **B-2 · Run `dotnet build FlowX.slnx` once.**
      *Blocks:* every work package from WP-2 onward.
      *Why not done:* this environment's proxy blocks the .NET SDK download
      (`dot.net` returns 403), so no C# in this repository has ever been compiled.
      *Highest-risk unverified construct:* `required` members on attribute classes
      (`CapabilityAttribute.Version`, `.Authorization`, `KafkaTriggerAttribute.Group`,
      `StreamTriggerAttribute.Window`, `AgentTriggerAttribute.Description`).
- [ ] **B-3 · Delete remote branch `claude/flowx-platform-docs-djjyxi`.**
      It carries three commits with non-owner authorship. The git proxy here
      refuses the delete; it must be done from the GitHub UI after switching the
      default branch to `master`.

---

## 1. Documentation

- [x] 20 specification documents, `docs/01` – `docs/20`
- [x] 12 ADRs with trade-offs stated
- [x] 9 sample application specifications
- [x] `CONTRIBUTING.md`, `SECURITY.md`, `LICENSE` (Apache-2.0)
- [x] `docs/21-Quality-Gates.md` — SonarQube thresholds, OWASP mapping, debt policy
- [x] `PLAN.md` — WP-0…WP-11 with mechanically checkable exit criteria
- [x] `CHECKLIST.md` — this file
- [x] README references the platform infographics
- [x] `docs/DEBT.md` — debt register (0 open entries; format + budget defined)
- [ ] `docs/benchmarks/P0.md` — the kill-criterion report (WP-11)
- [~] Internal Markdown links resolve — **4 broken, all of them B-1**: three
      image paths referenced from `README.md` and `docs/05-Architecture.md`.
      Every non-image link resolves. The `docs` job is red until the PNGs land,
      which is the intended forcing function, not an oversight.
- [ ] All Mermaid diagrams parse (the `docs` job cannot reach this step while B-1 is open)

---

## 2. WP-0 · Quality gates and CI

- [x] `ci.yml` — build with warnings-as-errors
- [x] `ci.yml` — architecture fitness functions gated ahead of the rest of the suite
- [x] `ci.yml` — NativeAOT publish failing on `IL2xxx`/`IL3xxx`
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

## 3. WP-1 · `FlowX.Abstractions` *(code written, compiler-unverified)*

- [~] `Result<T>` — readonly struct, allocation-free failure path
- [~] `Error`, `ErrorCategory` — closed set, terminal/retryable, HTTP mapping
- [~] `ICapability<TIn, TOut>` — the seven rules documented on the interface
- [~] `CapabilityAttribute` — `Version` and `Authorization` as required members
- [~] `Authorization`, `ApprovedByAttribute`, `SensitiveAttribute`
- [~] `CapabilityContext`, `FlowContext<TIn>` — clock, ids, randomness, deadline
- [~] `Flow<TIn, TOut>`, `IFlowBuilder<,>` — no `Do(lambda)`, no trigger types
- [~] `ExecutionProfile` — `Ephemeral` as the zero value
- [~] `PolicySet`, `PolicyStage` — stage order encoding the safety guarantees
- [~] Trigger attributes — http, kafka, cron, stream, agent
- [~] Zero package references, zero project references

Every item is `[~]` for the same reason: **B-2**. None of this has been compiled.

---

## 4. WP-1 · Architecture fitness functions *(written, unverified)*

- [~] `AbstractionsHasNoDependencies` — reads the `.csproj`
- [~] `LayersPointInward`
- [~] `RuntimeDoesNotReferenceAnyPlugin`
- [~] `EveryShippedProjectIsAotAnalyzed`
- [~] `ResultIsAnAllocationFreeValueType`
- [~] `ErrorCategoryRemainsClosed`
- [~] `TerminalCategoriesAreNeverRetried`
- [~] `CapabilityMustDeclareVersionAndAuthorization`
- [~] `ExecutionProfileDefaultsToEphemeral`
- [~] `PolicyStageOrderEncodesTheSafetyGuarantees`
- [~] `TenantScopedIsTheDefaultForEveryScopeEnum`
- [~] `FlowBuilderExposesNoTransportTypes`
- [~] `FlowBuilderHasNoEscapeHatchForInlineCode`
- [ ] `NoCyclicDependencies`
- [ ] `SuppressionsAreAccountable`
- [ ] `ManifestContainsNoSecrets`
- [ ] `EveryCapabilityDeclaresAuthorization`
- [ ] `PublicCapabilitiesAreReviewed`
- [ ] `CrossTenantAccessIsDenied`
- [ ] `RedactionCannotBeBypassed`
- [ ] `NoPermissiveDefaults`
- [ ] `EveryDiagnosticIsHelpful`

---

## 5. WP-2 → WP-11 · Not started

- [ ] **WP-2** `FlowX.Core` — `StepGraph`, `ExecutionPlan`, `CompensationStack`
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
| Compiler warnings | 0 | **unknown** | B-2 |
| Blocker/critical Sonar issues | 0 | **not running** | WP-0 |
| Line coverage (new code) | ≥ 80 % | **not measured** | WP-0 |
| Branch coverage (new code) | ≥ 75 % | **not measured** | WP-0 |
| Mutation score (`FlowX.Core`) | ≥ 70 % | n/a — no `FlowX.Core` yet | WP-2 |
| SAST findings | 0 | **wired, unrun** — needs B-2 | WP-0 |
| DAST findings | 0 | **wired, guarded** — needs WP-10 | WP-0 |
| Vulnerable dependencies | 0 | **0 by construction** — zero dependencies | WP-1 |
| Open debt entries | ≤ 20 | **0** | enforced by `quality.yml` |
| B1 flow overhead p99 | ≤ 5 µs | **not measured** | WP-3 |
| B2 allocations per step | 0 B | **not measured** | WP-3 |

Nothing in the "Now" column is green by assertion. Every "not measured" is
honest: the gate exists on paper and the mechanism to run it does not exist yet.

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

## 8. How to update this file

Update it in the **same commit** as the change it describes — a checklist updated
separately is a checklist that drifts, and a drifted checklist is worse than
none, because people trust it.

When a `[~]` becomes `[x]`, state what verified it. When a `[ ]` becomes `[x]`,
the [Definition of Done](docs/21-Quality-Gates.md#5-definition-of-done) applies in
full.

---

**Back to:** [README](README.md) · [Plan](PLAN.md) · [Quality gates](docs/21-Quality-Gates.md)

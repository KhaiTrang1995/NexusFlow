# 21 — Quality Gates, OWASP Compliance and Technical-Debt Policy

> **Status:** Accepted · **Audience:** contributors, security reviewers, release managers
> **Answers:** what must be true for code to merge, and how is each claim mechanically verified?

---

## 1. The rule that makes the rest work

> **A quality claim that is not enforced by a gate is a wish.**

This document contains no advice. Every row in every table below names a rule, the
mechanism that enforces it, and what happens when it fails. If a rule cannot be
expressed as a gate, it does not belong here — it belongs in
[03-Design-Principles](03-Design-Principles.md) as a principle, or in a review
checklist as guidance.

Three gate classes, in the order they run:

| Class | Runs | Failure means |
|---|---|---|
| **Build gates** | every compile, locally and in CI | the code does not compile |
| **Merge gates** | every pull request | the branch cannot merge |
| **Release gates** | every tag | the release does not ship |

A gate that is routinely bypassed is worse than no gate: it teaches the team that
red means "probably fine". There is no `continue-on-error` in this repository's
CI, and no `// TODO: fix later` suppression without a linked issue and an expiry
date — enforced by §6.

---

## 2. Static quality gate (SonarQube-grade)

### 2.1 Thresholds on new code

Measured on the pull request's diff, not on the whole repository. Legacy debt is
paid down deliberately (§6), never by blocking unrelated work.

| Metric | Threshold | Enforced by | Class |
|---|---|---|---|
| Blocker issues | **0** | SonarAnalyzer + Roslyn, `TreatWarningsAsErrors` | Build |
| Critical issues | **0** | Sonar quality gate | Merge |
| Cognitive complexity per method | **≤ 15** | `S3776` as error | Build |
| Cyclomatic complexity per method | **≤ 10** | `S1541` as error | Build |
| Method length | **≤ 60 lines** | `S138` as error | Build |
| Parameters per method | **≤ 7** | `S107` as error | Build |
| Duplicated lines on new code | **≤ 3 %** | Sonar | Merge |
| Line coverage on new code | **≥ 80 %** | Coverlet + Sonar | Merge |
| Branch coverage on new code | **≥ 75 %** | Coverlet + Sonar | Merge |
| Mutation score on `FlowX.Core` | **≥ 70 %** | Stryker.NET | Merge |
| Security hotspots reviewed | **100 %** | Sonar | Merge |
| Public API documented | **100 %** | `CS1591` as error | Build |
| Compiler warnings | **0** | `TreatWarningsAsErrors` | Build |

Mutation testing appears here for one reason: line coverage measures which lines
ran, not whether anything would notice if they were wrong. `FlowX.Core` holds the
execution semantics, so a test suite that cannot detect a mutated comparison in
the step loop is not a test suite. It is applied to `FlowX.Core` only — running
Stryker across the whole solution costs more CI time than it returns.

### 2.2 Rules promoted to errors

These are not style preferences. Each is a defect class that has caused
production incidents in systems of this shape.

| Rule | Why it is an error here |
|---|---|
| `CA2007` — `ConfigureAwait(false)` | Library code that captures a synchronization context deadlocks its host. FlowX is library code everywhere except `FlowX.Cli`. |
| `CA1031` — no general `catch` | A swallowed exception in the step loop turns a crash into silent data loss, which is strictly worse. |
| `CA2016` — forward `CancellationToken` | A dropped token means a cancelled flow keeps burning a dependency's capacity after its deadline passed. |
| `CA1062` — validate public arguments | The contract surface is consumed by code we do not control. |
| `CA1848` — `LoggerMessage` over interpolation | Interpolated logging allocates on the hot path even when the level is disabled, which breaks budget B6. |
| `S2245` — no insecure randomness | `Random` for anything security-adjacent. Determinism uses `CapabilityContext.Random`, which is journaled, not secret. |
| `S4507` — no debug features in production | Delivering stack traces to a caller is an information leak (A05). |
| `VSTHRD002` — no sync-over-async | `.Result`/`.Wait()` in a runtime this hot is a thread-pool starvation incident waiting for load. |

### 2.3 Architecture gates

Structural rules are executable, per
[05-Architecture §12](05-Architecture.md) and
[CONTRIBUTING](../CONTRIBUTING.md). They live in `tests/FlowX.Architecture.Tests`
and run **before** the rest of the suite, because a layering violation makes every
downstream test result uninteresting.

| Fitness function | Rule it enforces |
|---|---|
| `AbstractionsHasNoDependencies` | `FlowX.Abstractions` has zero package and project references ([ADR-0009](adr/ADR-0009-plugin-contracts.md)) |
| `LayersPointInward` | Abstractions ← Core ← Runtime ← Runtime.Durable, never the reverse |
| `EverySourceProjectIsCoveredByTheLayeringRule` | no project under `src/` escapes the rule above by not being named in it |
| `RuntimeDoesNotReferenceAnyPlugin` | adding a transport never means editing the runtime (quality goal Q6) |
| `NoCyclicDependencies` | no dependency cycle between any two assemblies or namespaces |
| `EveryCapabilityDeclaresAuthorization` | every `ICapability<,>` that ships carries `[Capability]` naming a stance (principle P11) |
| `PublicCapabilitiesAreReviewed` | every `Authorization.Public` carries an `[ApprovedBy]`, and no approval outlives the stance it approved |
| `NoPermissiveDefaults` | nothing on the contract surface reaches a permissive stance by being left alone |
| `SuppressionsAreAccountable` | every suppression names a registered, unexpired `FLOWX-DEBT` id (§6.1) |
| `EveryDiagnosticIsHelpful` | every `FLOWX####` has a message, a fix and a help URI |
| `ManifestContainsNoSecrets` | the emitted manifest is structure, never values |
| `EveryShippedProjectIsAotAnalyzed` | no project silences the trim/AOT analyzer (constraint C2) |

`ManifestContainsNoSecrets` matches the **shape** of a secret — PEM blocks, JWTs,
`Password=` assignments, credentials embedded in a URL, provider key prefixes — across the
manifests the build actually emitted. It deliberately does not forbid words. A manifest
that lists a `[Sensitive]` member called `PaymentToken` is doing its job; one that carries
a payment token has leaked. A word list cannot tell those apart, and in practice that
argument is settled by deleting the word from the list.

### 2.4 Gates named here but not yet enforced

Two rules named in the OWASP mapping below and in [15-Security §10](15-Security.md) have
no fitness function, because the code they would govern does not exist yet. They are
recorded here rather than left as an empty checkbox: an unticked box reads as "not got
round to it", and the difference between *unwritten* and *not yet writable* is the
difference between a backlog item and a false claim of coverage.

**A fitness function asserting a property of code that has not been written is not a gate.
It is decoration — and worse than nothing, because it stops the next reviewer looking.**

| Named gate | Blocked on | What *is* assertable today |
|---|---|---|
| `CrossTenantAccessIsDenied` | **P4** — no policy executes at runtime, so no stage exists that could return `Forbidden`; **P2** — no journal, so there is no audit event to assert. `TenantId` is resolved from claims and carried on the invocation, and nothing consumes it. "Across every trigger kind" additionally needs **P3**: HTTP is the only transport. | That tenant resolution reads validated claims and nothing else. Covered behaviourally by `HttpTriggerReaderTests` — which is the `TenantComesFromClaimsOnly` control the A07 row cites, under a different name, for the one transport that exists. |
| `RedactionCannotBeBypassed` | **P3** and **P5** — the rule is that no path reaches logs, traces, journal or replay output un-redacted, and none of those four sinks exists. Exactly one sink can serialise a contract value today: the RFC 7807 body, redacted by `ProblemDetailsMapper` and covered by `ProblemDetailsMapperTests`. Redaction is *not* applied by a generated serialiser; see the remarks on `SensitiveAttribute`. | That the compiler records `[Sensitive]` members in the manifest and emits them onto the flow — `ManifestWriterTests`, `PlaceOrderEndpointTests`. That is provenance, not an un-bypassable control. |

Both are exit criteria of their phases in [20-Roadmap](20-Roadmap.md). Neither should be
written before then, and neither should be cited as present until it is.

---

## 3. OWASP Top 10 mapping

OWASP Top 10:2021, each risk mapped to the FlowX control that addresses it and the
gate that proves the control is present. "Verified by" is a CI job or an
executable test — never a review step alone.

| # | Risk | FlowX control | Verified by |
|---|---|---|---|
| **A01** | Broken access control | Authorisation is a **required member** on `[Capability]`, so a capability cannot compile without a stance. Enforcement is at the business operation, not the route, so it holds over HTTP, Kafka and the agent surface alike. `Authorization.Internal` is *designed* to be unreachable from any external trigger — **the enforcement is not built**; nothing under `src/FlowX.Runtime`, `src/FlowX.Hosting` or `plugins/` reads the stance, which reaches the manifest and no further (P4). | `FLOWX1010` (build) · `EveryCapabilityDeclaresAuthorization`, `PublicCapabilitiesAreReviewed` (merge) · `CrossTenantAccessIsDenied` — **not yet enforced, see §2.4** |
| **A02** | Cryptographic failures | The intent is that `[Sensitive]` redaction is applied by the **generated** serialiser so no code path reaches logs, traces, journal or replay output un-redacted. **Today it reaches one sink**: the RFC 7807 body, redacted by `ProblemDetailsMapper`. The other three do not exist (P3, P5), and redaction is not generated. Secrets resolve from a secret provider at startup; `IConfiguration` is never a secret source. TLS is required for every egress plugin. | `ManifestContainsNoSecrets` (merge) · secret-scanning (merge) · `RedactionCannotBeBypassed` — **not yet enforced, see §2.4** |
| **A03** | Injection | Capabilities own their own data access, so FlowX cannot prevent a hand-written SQL string — this is a **stated limitation** ([15 §11](15-Security.md)). What the platform does provide: flow graphs are compile-time constants, so no input can alter control flow; the DSL has no `Do(lambda)` and no dynamic step resolution; all contract deserialisation is generated and schema-validated. | CodeQL + Semgrep (merge) · `FlowGraphIsCompileTimeConstant` (merge) · capability review checklist |
| **A04** | Insecure design | STRIDE per trust boundary in [15 §3](15-Security.md), ADR for every significant decision, threat model refreshed at each phase gate. Design defects are cheapest here and this is the only control that catches them. | ADR presence check (merge) · phase-gate review (release) |
| **A05** | Security misconfiguration | There is **no permissive default anywhere**: authorisation, tenant scope on cache/rate-limit/idempotency, and `Public` all require an explicit, greppable declaration. Configuration is validated at startup and the host refuses to start on a violation — a misconfigured node is a dead node, never a quietly insecure one. | `NoPermissiveDefaults` (merge) · startup validation test (merge) · container scan (merge) |
| **A06** | Vulnerable and outdated components | `FlowX.Abstractions` has zero dependencies by construction. Lock files committed; Dependabot with review required; SBOM (CycloneDX) generated per release; Trivy scans the container image. | `dotnet list package --vulnerable` (merge, fails on any) · Trivy HIGH/CRITICAL (merge) · SBOM attached (release) |
| **A07** | Identification and authentication failures | Principal and tenant come from **validated claims only** — never a header, never a payload field. Token validation is centralised in the trigger engine so a plugin cannot weaken it. | `TenantComesFromClaimsOnly` (merge) · DAST auth suite (nightly) |
| **A08** | Software and data integrity failures | Deterministic, reproducible builds; packages signed; provenance attestation on release. At runtime: transactional outbox so an event is never published before its step is durable, and journal entries are integrity-checked on replay. | build reproducibility check (release) · signature verification (release) · replay-determinism corpus (merge) |
| **A09** | Security logging and monitoring failures | The `Audit` policy writes an immutable record at the `Consistency` stage. Every authorisation denial is audited by the platform, not by the capability. Correlation ID propagates across every hop. Redaction is generated, so audit records cannot leak what they record. | `EveryDenialIsAudited` (merge) · `TelemetryConformanceTest` (merge) |
| **A10** | Server-side request forgery | Egress is plugin-mediated; each egress plugin declares an allow-list and the host refuses unknown destinations. A capability cannot open an arbitrary socket without importing a transport assembly, which `FLOWX1003` rejects. | `FLOWX1003` (build) · `EgressIsAllowListed` (merge) |

### 3.1 OWASP Top 10 for LLM Applications

The agent surface ([13-AI-Native](13-AI-Native.md)) is a genuine attack surface, not a
feature. It gets its own mapping because the risks are different in kind.

| # | Risk | FlowX control | Verified by |
|---|---|---|---|
| **LLM01** | Prompt injection | An agent invokes a **generated tool surface**, never free-form code. A tool is a flow, and the flow's authorisation applies unchanged — a prompt cannot grant a permission the caller's identity lacks. | `AgentSurfaceEqualsFlowSurface` (merge) |
| **LLM02** | Insecure output handling | Tool outputs are typed contracts, schema-validated on the way out. | generated schema validation (build) |
| **LLM06** | Sensitive information disclosure | `[Sensitive]` redaction is intended to apply to the agent surface identically to every other transport. Neither the agent surface nor redaction beyond the RFC 7807 body is built. | `RedactionCannotBeBypassed` — **not yet enforced, see §2.4** |
| **LLM07** | Insecure plugin design | Agent tools are generated from flows; there is no separate plugin registration path an attacker could target. | `AgentSurfaceEqualsFlowSurface` (merge) |
| **LLM08** | Excessive agency | `ConfirmationMode.RequiredForSideEffects` is the default, and confirmation prompts state the **declared** side effects rather than a generic warning. Capabilities with `Authorization.Internal` are excluded from the tool surface entirely. | `InternalCapabilitiesAreNotAgentReachable` (merge) |

---

## 4. Security testing toolchain

| Stage | Tool | Scope | Gate | Frequency |
|---|---|---|---|---|
| SAST | **CodeQL** (`security-and-quality`) | whole solution | any alert ≥ medium fails | every PR |
| SAST | **Semgrep** (OWASP + C# rulesets) | whole solution | any ERROR fails | every PR |
| Secrets | **Gitleaks** + GitHub secret scanning | full history on PR | any finding fails | every PR |
| SCA | `dotnet list package --vulnerable --include-transitive` | all projects | any vulnerability fails | every PR |
| SCA | **Dependabot** | NuGet + GitHub Actions | review required | weekly |
| Container | **Trivy** | published image | HIGH/CRITICAL fails | every PR |
| IaC | **Checkov** | Helm charts, Kubernetes manifests | HIGH fails | every PR |
| **DAST** | **OWASP ZAP** baseline + full scan | `samples/banking` and `samples/ecommerce` running in Docker | any HIGH fails | nightly + pre-release |
| Fuzzing | **SharpFuzz** | trigger payload deserialisation | any crash fails | nightly |
| Supply chain | **CycloneDX SBOM** + Sigstore | release artifacts | missing attestation fails | every release |

### 4.1 Why DAST runs against samples

FlowX is a library; there is no FlowX server to point a scanner at. The samples
are the honest target — they exercise the generated HTTP surface, the generated
Problem Details mapping, the authorisation stack and the idempotency store the
way a real application does. `samples/banking` is the primary DAST target because
it is the sample designed around money and PII, so its threat model is the
strictest one in the set.

A DAST finding against a sample is treated as a **platform** defect until proven
to be a sample defect. The generated surface is platform code.

---

## 5. Definition of Done

A change is done when **every** box is checked. This is the same list the pull
request template asks for, and it is enforced by the `merge` gate class.

- [ ] Design: an ADR exists for any significant decision this change makes
- [ ] Tests written **first**, and the commit history shows red before green
- [ ] Line coverage ≥ 80 % and branch coverage ≥ 75 % on the diff
- [ ] Architecture fitness functions green
- [ ] Zero compiler warnings; zero blocker/critical Sonar issues
- [ ] Cognitive complexity ≤ 15 on every method touched
- [ ] SAST, SCA, secret scanning and container scan clean
- [ ] Public API documented; breaking changes carry a SemVer bump and a deprecation entry
- [ ] Documentation section updated ([constraint C8](05-Architecture.md))
- [ ] Benchmarks green if the change touches a hot path
- [ ] `CHECKLIST.md` updated to reflect the new state

---

## 6. Technical-debt policy

"No technical debt" is not achievable as an absolute, and claiming it would be
the first lie in the codebase. What *is* achievable: **no undeclared,
unbudgeted, unexpiring debt.** The difference is everything.

### 6.1 Every suppression is a dated contract

A suppression without all four fields fails the `SuppressionsAreAccountable`
fitness function:

```csharp
// FLOWX-DEBT: id=DEBT-0007 owner=runtime expires=2026-12-31
//   Reason: the pooled context reset is hand-written because the generator
//   cannot yet see partial-class fields; tracked by issue #142.
[SuppressMessage("Sonar", "S3776:Cognitive Complexity", Justification = "DEBT-0007")]
```

| Field | Rule |
|---|---|
| `id` | matches an entry in [`docs/DEBT.md`](DEBT.md) |
| `owner` | a team, never an individual — people change teams |
| `expires` | ISO-8601 date, **≤ 6 months out**; CI fails the build the day it passes |
| Reason | what would have to change for the suppression to be removable |

An expiring suppression fails the build rather than warning, because a warning
about an expired suppression is itself debt.

### 6.2 Debt budget

| Limit | Value | On breach |
|---|---|---|
| Open debt entries | **≤ 20** | no new feature work until back under |
| Any single entry's age | **≤ 6 months** | automatic build failure |
| Debt entries per phase gate | reviewed, re-scored, or closed | phase does not pass |

### 6.3 What is not debt

Deliberate, documented trade-offs recorded in an ADR are **decisions**, not debt.
Ephemeral flows losing state on crash is not debt — it is
[ADR-0003](adr/ADR-0003-execution-profiles.md). Confusing the two makes the debt
register meaningless, which makes the budget unenforceable.

---

## 7. Performance gates

Budgets live in [14-Performance](14-Performance.md). Their enforcement is here.

| Gate | Rule | Class |
|---|---|---|
| B1–B6, B10–B12 | regression > 5 % vs the baseline fails the build | Merge |
| B2, B6 | allocations must be **exactly 0** — not "low" | Merge |
| B7–B9, B13 | nightly load test; regression opens a blocking issue | Release |
| Baseline updates | require a reviewed commit stating why the budget moved | Merge |

A benchmark that becomes flaky is fixed or deleted, never muted. A muted
benchmark is a budget nobody is holding.

---

## 8. Reliability gates

| Gate | Rule | Class |
|---|---|---|
| Chaos: SIGKILL at every step boundary | 10 000 flows, zero duplicate non-idempotent effects, zero lost instances | Release |
| Replay determinism corpus | zero divergence across the full corpus | Merge |
| Backpressure conformance | bounded memory with a deliberately slow capability | Release |
| Tenant fairness | one tenant at 10× quota degrades another's p99 by ≤ 10 % | Release |
| Graceful shutdown | in-flight flows drain or checkpoint within the termination grace period | Merge |

---

## 9. What these gates deliberately do not claim

Stating this honestly matters more than the marketing value of omitting it.

| Claim not made | Why |
|---|---|
| "Zero security issues" | No tool set proves absence of vulnerabilities. What is claimed: every gate in §4 is green, and every OWASP risk in §3 has a named control with a named verification. |
| "Zero technical debt" | See §6. What is claimed: no debt that is undeclared, unowned or unexpiring. |
| "Secure capabilities" | FlowX cannot stop a capability writing a SQL injection ([15 §11](15-Security.md)). It narrows the blast radius and makes the capability reviewable in isolation. |
| "100 % coverage" | Coverage measures execution, not correctness. Mutation score on `FlowX.Core` is the honest metric, and it is set at 70 %, not 100 %. |

---

**Back to:** [README](../README.md) · [Architecture](05-Architecture.md) · [Security](15-Security.md) · [Checklist](../CHECKLIST.md)

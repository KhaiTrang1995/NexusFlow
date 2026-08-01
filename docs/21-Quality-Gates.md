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

| Metric | Threshold | Enforced by | Class | Runs today |
|---|---|---|---|---|
| Blocker issues | **0** | `SonarAnalyzer.CSharp` default profile + `TreatWarningsAsErrors` | Build | **yes** |
| Critical issues | **0** | Sonar quality gate | Merge | no — needs `SONAR_TOKEN` (§2.6) |
| Cognitive complexity per method | **≤ 15** | `S3776` as error | Build | **no** — 12 methods over (§2.6) |
| Cyclomatic complexity per method | **≤ 10** | `S1541` as error | Build | **no** — 24 methods over (§2.6) |
| Method length | **≤ 60 lines** | `S138` as error | Build | **no** — 4 methods over (§2.6) |
| Parameters per method | **≤ 7** | `S107` as error | Build | **no** — 14 members over (§2.6) |
| Duplicated lines on new code | **≤ 3 %** | Sonar | Merge | no — needs `SONAR_TOKEN` |
| Line coverage on new code | **≥ 80 %** | Coverlet + Sonar | Merge | Coverlet half only, whole-assembly |
| Branch coverage on new code | **≥ 75 %** | Coverlet + Sonar | Merge | Coverlet half only, whole-assembly |
| Mutation score on `FlowX.Core` | **≥ 70 %** | Stryker.NET | Merge | **yes** |
| Security hotspots reviewed | **100 %** | Sonar | Merge | no — needs `SONAR_TOKEN` |
| Public API documented | **100 %** | `CS1591` as error | Build | **yes** |
| Compiler warnings | **0** | `TreatWarningsAsErrors` | Build | **yes** |

Mutation testing appears here for one reason: line coverage measures which lines
ran, not whether anything would notice if they were wrong. `FlowX.Core` holds the
execution semantics, so a test suite that cannot detect a mutated comparison in
the step loop is not a test suite. It is applied to `FlowX.Core` only — running
Stryker across the whole solution costs more CI time than it returns.

> [!IMPORTANT]
> **`SonarAnalyzer.CSharp` is now referenced** (`Directory.Build.props`, pinned,
> `PrivateAssets="all"`), so the `S####` rules are real analyzers running on
> every compile. Its default profile — 329 of the package's 471 rules — gates
> the build, which is what makes the *Blocker issues* row true. Every deviation
> from that profile is a named rule with a written reason in `.editorconfig`,
> and §2.6 lists all of them.
>
> **Four rows above still do not run, and the reason is not the one this note
> used to give.** A previous version of this note claimed that adding the
> package "would make the complexity and method-size rows real without any
> other change". That was wrong twice over, and both errors are worth keeping
> on the record because they are the kind that survive review:
>
> 1. `S3776`, `S1541`, `S138` and `S107` ship **disabled by default**. Merely
>    referencing the package leaves them off, and a build with the package
>    installed and those rules silent looks exactly like a build that passes
>    them. They have to be named explicitly to run at all.
> 2. When they *are* named, the repository does not pass them: **54 findings**,
>    listed in §2.6. So the rows were never one dependency away from being true
>    — they describe a bar this code has not met.
>
> They are therefore set to `none` explicitly rather than left at their default,
> so that the file records a decision instead of an accident. The thresholds
> §2.1 names are pinned in `SonarLint.xml` (severities live in `.editorconfig`;
> **thresholds cannot** — a threshold written there is silently ignored), so
> turning any of these rows on after the debt is paid is a one-word edit.
>
> `VSTHRD002` in §2.2 **now runs** everywhere except `tests/`, from
> `Microsoft.VisualStudio.Threading.Analyzers` — it is set on `[*.cs]`, so it covers
> `samples/` and `scripts/` as well as `src/` and `plugins/`. It has one file-scoped
> exception; §2.6 says why.
>
> The three merge-class Sonar rows — critical issues, duplicated lines, security
> hotspots — still depend on the *Sonar quality gate* job in `quality.yml`,
> which **exits 0 when `SONAR_TOKEN` is absent**, and `CHECKLIST.md` records the
> token as not configured. The job no longer does so quietly: it now emits a
> warning annotation and a step summary naming the three rows that were not
> evaluated, so a green tick on that job cannot be mistaken for a gate that
> passed. It is still green, deliberately — see the comment on the job's `if:`.
>
> **What runs on every pull request**, for completeness: `TreatWarningsAsErrors`
> (so any warning is a build failure), `CS1591` as an error, `CA2007`, `CA1031`,
> `CA2016`, `CA1062` as errors and `CA1848` as a warning-that-is-an-error, the
> Sonar default profile, `S2245`, `S4507` and `VSTHRD002` as errors — all set in
> `.editorconfig` — plus the coverage thresholds (80 / 75, enforced on the whole
> assembly by the *Coverage thresholds* job) and Stryker at `--threshold-break
> 70` on `FlowX.Core`.
>
> Until the four complexity rows are paid down, "cognitive complexity ≤ 15 on
> every method touched" in §5's Definition of Done remains a review instruction,
> not a gate.

### 2.2 Rules promoted to errors

These are not style preferences. Each is a defect class that has caused
production incidents in systems of this shape.

| Rule | Why it is an error here | Scope |
|---|---|---|
| `CA2007` — `ConfigureAwait(false)` | Library code that captures a synchronization context deadlocks its host. FlowX is library code everywhere except `FlowX.Cli`. | all but `tests/` |
| `CA1031` — no general `catch` | A swallowed exception in the step loop turns a crash into silent data loss, which is strictly worse. | all but `tests/` |
| `CA2016` — forward `CancellationToken` | A dropped token means a cancelled flow keeps burning a dependency's capacity after its deadline passed. | everywhere |
| `CA1062` — validate public arguments | The contract surface is consumed by code we do not control. | everywhere |
| `CA1848` — `LoggerMessage` over interpolation | Interpolated logging allocates on the hot path even when the level is disabled, which breaks budget B6. | everywhere |
| `S2245` — no insecure randomness | `Random` for anything security-adjacent. Determinism uses `CapabilityContext.Random`, which is journaled, not secret. | everywhere except the two files that *are* that determinism source (§2.6) |
| `S4507` — no debug features in production | Delivering stack traces to a caller is an information leak (A05). | everywhere, but it only has anything to bind to in `FlowX.Http` — the rule keys on ASP.NET Core APIs, and that is the only project with a `Microsoft.AspNetCore.App` framework reference |
| `VSTHRD002` — no sync-over-async | `.Result`/`.Wait()` in a runtime this hot is a thread-pool starvation incident waiting for load. | Everywhere except `tests/`, minus `FlowEngine.cs` (§2.6). Set on `[*.cs]`, so `samples/` and `scripts/` are covered too — this row used to say "`src/` and `plugins/`", which understated it. Not `tests/`: the rule is about deadlocking on a captured synchronization context, and xUnit does not install one. |

Every rule in this table is an error at build time. The three `S`/`VSTHRD` rows
were added by the change that introduced the analyzer packages; the five `CA`
rows predate it.

### 2.3 Architecture gates

Structural rules are executable, per
[05-Architecture §12](05-Architecture.md) and
[CONTRIBUTING](../CONTRIBUTING.md). They live in `tests/FlowX.Architecture.Tests`
and run **before** the rest of the suite, because a layering violation makes every
downstream test result uninteresting.

| Fitness function | Rule it enforces |
|---|---|
| `AbstractionsHasNoDependencies` | `FlowX.Abstractions` has zero package and project references ([ADR-0009](adr/ADR-0009-plugin-contracts.md)) |
| `LayersPointInward` | Abstractions ← Core ← Runtime ← Hosting / Runtime.Durable, never the reverse. *The `Runtime.Durable` row of the theory passes vacuously — that project is **P2** and does not exist yet* |
| `EverySourceProjectIsCoveredByTheLayeringRule` | no project under `src/` escapes the rule above by not being named in it |
| `RuntimeDoesNotReferenceAnyPlugin` | adding a transport never means editing the runtime (quality goal Q6) |
| `NoCyclicDependencies` | no dependency cycle between any two assemblies or namespaces |
| `EveryCapabilityDeclaresAuthorization` | every `ICapability<,>` that ships carries `[Capability]` naming a stance (principle P11) |
| `NoReflectionOnHotPath` | no `System.Reflection`, `Activator` or runtime binder in Abstractions, Core or Runtime — IL scan (principle P4) |
| `RuntimeHasNoMutableStatics` | every static field in `FlowX.Runtime` is `readonly` or `const` — IL scan (principle P7) |
| `FlowsAreTransportFree` | nothing in a flow's transitive closure names a transport or a plugin namespace (principle P3) |
| `CapabilitiesDoNotCallCapabilities` | no capability reaches another, from a dependency or from a method body (wider than FLOWX1004) |
| `EveryPublicContractIsVersioned` | every flow, capability, event, manifest and shipped package carries a SemVer version (constraint C7) |
| `ManifestIsComplete` | every declared flow and capability appears in the emitted manifest, and every step names one it describes (quality goal Q3) |
| `PublicCapabilitiesAreReviewed` | every `Authorization.Public` carries an `[ApprovedBy]`, and no approval outlives the stance it approved |
| `NoPermissiveDefaults` | nothing on the contract surface reaches a permissive stance by being left alone |
| `SuppressionsAreAccountable` | every suppression names a registered, unexpired `FLOWX-DEBT` id (§6.1) |
| `EveryDiagnosticIsHelpful` | every `FLOWX####` has a message, a fix and a help URI — *lives in `tests/FlowX.Compiler.Tests/CompilerFitnessTests.cs`, not in `FlowX.Architecture.Tests`, because it reads the descriptor catalogue rather than an assembly* |
| `ManifestContainsNoSecrets` | the emitted manifest is structure, never values |
| `EveryShippedRuntimeProjectIsAotAnalyzed` | no project under `src/` silences the trim/AOT analyzer (constraint C2). *This row read `EveryShippedProjectIsAotAnalyzed`, which is not the test's name* |

`NoReflectionOnHotPath`, `RuntimeHasNoMutableStatics`, `FlowsAreTransportFree`,
`CapabilitiesDoNotCallCapabilities`, `EveryPublicContractIsVersioned` and
`ManifestIsComplete` inspect the built assemblies rather than the source, because
[P4](03-Design-Principles.md#p4--compile-time-everything) and
[P7](03-Design-Principles.md#p7--cloud-native) state them that way and because reflection or
a transport reference can arrive through a generator, an extension method or an `async`
state machine — none of which a source scan sees. There is one exemption, `MemberInfo.Name`:
`typeof(T).Name` is how the runtime names the contract a step failed to produce, and it
discovers nothing. Everything that looks a member up is still caught.

`ManifestContainsNoSecrets` matches the **shape** of a secret — PEM blocks, JWTs,
`Password=` assignments, credentials embedded in a URL, provider key prefixes — across the
manifests the build actually emitted. It deliberately does not forbid words. A manifest
that lists a `[Sensitive]` member called `PaymentToken` is doing its job; one that carries
a payment token has leaked. A word list cannot tell those apart, and in practice that
argument is settled by deleting the word from the list.

### 2.4 Gates named here but not yet enforced

Three rules named elsewhere in the documentation have no fitness function, because the
code they would govern does not exist yet. They are recorded here rather than left as an
empty checkbox: an unticked box reads as "not got round to it", and the difference between
*unwritten* and *not yet writable* is the difference between a backlog item and a false
claim of coverage.

**A fitness function asserting a property of code that has not been written is not a gate.
It is decoration — and worse than nothing, because it stops the next reviewer looking.**

| Named gate | Blocked on | What *is* assertable today |
|---|---|---|
| `CrossTenantAccessIsDenied` | **P4** — no policy executes at runtime, so no stage exists that could return `Forbidden`. `TenantId` is resolved from claims and carried on the invocation, and nothing consumes it. "Across every trigger kind" additionally needs **P3**: HTTP is the only transport. *This cell also read "**P2** — no journal, so there is no audit event to assert". That half expired on 2026-07-31: WP-52 journals a `Durable` flow's step boundaries and stamps `tenant_id` on the instance row. It is an execution record rather than an audit event, and it changes nothing about this gate, which is blocked on the `Forbidden` that cannot happen.* | That tenant resolution reads validated claims and nothing else. Covered behaviourally by `HttpTriggerReaderTests` — which is the `TenantComesFromClaimsOnly` control the A07 row cites, under a different name, for the one transport that exists. |
| `RedactionCannotBeBypassed` | **P3** and **P5** — the rule is that no path reaches logs, traces, journal or replay output un-redacted. *This cell said none of the four sinks exists; **the journal does, since WP-52 (2026-07-31)**, and it is the first sink where redaction is structural rather than remembered:* a payload enters only through `JournalPayload`, whose sole way out is `ToJson()`, which redacts — there is no accessor a store could use to reach the graph. Two sinks now exist: that one, and the RFC 7807 body redacted by `ProblemDetailsMapper` (`ProblemDetailsMapperTests`). Logs, traces and replay output still do not. Redaction is still *not* applied by a generated serialiser; see the remarks on `SensitiveAttribute`. | That the compiler records `[Sensitive]` members in the manifest and emits them onto the flow — `ManifestWriterTests`, `PlaceOrderEndpointTests`. That is provenance, not an un-bypassable control. |
| `PluginsPassConformance` | *This cell said "the conformance suite does not exist". It does now, and the gate is still blocked.* `tests/FlowX.Conformance.Tests` (WP-51) holds **three of seven** suites — `JournalConformance`, `LeaseStoreConformance`, `RecoveryIndexConformance` — and **no `TriggerSourceConformance`**, which is the one this gate would run; `ITriggerSource` is still not declared in `src/`. The project is **not packable**, so nothing outside this repository can run it. *This cell also said "nothing has ever run against a real database"; WP-53 ended that* — `tests/FlowX.Postgres.Tests` inherits all three unmodified, from a different assembly, and runs them against PostgreSQL 16.13, which is the arrangement a third party would use ([ADR-0016](adr/ADR-0016-postgres-journal-adapter.md)). What that proves is that the *mechanism* travels across an assembly boundary, not that the gate exists. [05-Architecture §11](05-Architecture.md#11-risks-and-technical-debt) names publishing a suite as the mitigation for both R3 and R8, and that has not happened. There is still one **transport** plugin — `FlowX.Http` — so "every plugin agrees on the minimum semantics" has one data point and no comparison for the extension point this gate names. | That the one transport that exists normalises HTTP into a `TriggerEnvelope` and maps every `ErrorCategory` to its documented status. `FlowX.Http.Tests` covers it. Writing the named gate against a single plugin would restate those tests under a name claiming ecosystem coverage. Separately assertable, and asserted: that the conformance *mechanism* rejects a wrong store by name — `TheSuiteRejectsAStoreThatIsWrongTests`, now across three contracts rather than two. That is the suite proving itself, not a plugin passing it. |

All three are exit criteria of their phases in [20-Roadmap](20-Roadmap.md). None should be
written before then, and none should be cited as present until it is.

### 2.5 The "Verified by" columns in §3 name eight more gates that do not exist

§2.4 says "three rules", and that was true of the fitness-function tables. It was not true
of the OWASP mapping below, which was written earlier and to a different standard: its
**Verified by** column reads as a list of running checks, and eight of the names in it have
never been written. Recorded here rather than struck through in the table, because each one
is still the right control — it is the *tense* that was wrong.

| Named in §3 as verification | State | Blocked on |
|---|---|---|
| `FlowGraphIsCompileTimeConstant` (A03) | not written. The property holds — the DSL has no `Do(lambda)`, and `FlowBuilderHasNoEscapeHatchForInlineCode` in `ContractSurfaceTests` asserts that much of it | — could be written now against the builder surface |
| `TenantComesFromClaimsOnly` (A07) | not written under that name. `HttpTriggerReaderTests` covers the behaviour for the one transport that exists | **P3** for "every transport" |
| `EveryDenialIsAudited` (A09) | not written. No authorisation decision is made at run time and no audit record is written | **P4** |
| `TelemetryConformanceTest` (A09) | **runs** — `tests/FlowX.Hosting.Tests/TelemetryConformanceTests.cs` (WP-90). It asserts the frozen span and metric names as literals, asserts that they are emitted rather than merely declared, and gates [12 §3](12-Observability.md#3-metrics)'s cardinality rule. *It does not yet cover A09's own concern:* there is no `ILogger` and no audit record, so the denial-is-audited claim in the row below has nothing to assert against | partly **P4** (the `Audit` policy), partly **P5** (logs) |
| `EgressIsAllowListed` (A10) | not written. No egress plugin exists, so nothing declares an allow-list | **P3** |
| `AgentSurfaceEqualsFlowSurface` (LLM01, LLM07) | not written. There is no agent surface; `[AgentTrigger]` reaches the manifest and nothing serves it | **P8** |
| `InternalCapabilitiesAreNotAgentReachable` (LLM08) | not written, and vacuous today for the same reason | **P8** |
| replay-determinism corpus (A08) | **written — WP-61, 2026-07-31.** *This row said there was no corpus and that nothing replayed a capture back into execution.* `ReplayDeterminismTests` replays eight shapes — linear, `When`, `Switch`, `ForEach`, `Parallel`, `SubFlow` inline and `Detached`, a failure with its unwind, and a flow reading all three ambient sources — each against its own journal, on a clock a hundred days from the original's so a fresh read cannot pass for a replayed one. Six further tests exist only to prove the comparison can go red. Three fidelity limits are measured and pinned rather than assumed | **P2** |
| startup validation test (A05) | **exists** — `FlowXOptionsValidator` runs under `ValidateOnStart`, covered by `StartupValidationTests` in `FlowX.Hosting.Tests` | — |

The A01 and A02 rows already carry an inline "the enforcement is not built" correction. The
rest of §3 should be read as: **the control column is the design, and the verification
column is a mixture of gates that run and gates that are scheduled.** Where a row says
"(merge)" against a name in the table above, no merge is currently blocked by it.

**The same is true of four §3 entries that name a tool rather than a fitness function**,
and they were missed when the list above was written because they do not look like test
names. `Trivy HIGH/CRITICAL` (A06) and `container scan` (A05) do not run — there is no
`Dockerfile` in this repository and no image is built, so there is nothing to scan.
`ADR presence check` (A04) does not exist: no workflow inspects `docs/adr/`, and the ADR
requirement is carried by review and by the pull-request template. `build reproducibility
check`, `signature verification` and `SBOM attached` (A08, A06) are all marked "(release)"
and there is **no release workflow at all**. §4 carries the same correction against the
toolchain table.

### 2.6 What the analyzers found, and what was done about each

Turning the packages on is a one-line change. Deciding what to do with what they
say is the rest of the work, and it is recorded here rather than in a commit
message because a suppression whose reason lives in history is a suppression
nobody can check.

The rule that governs every row below: **a finding is either fixed, or it is
switched off by name with a reason.** There is no blanket `NoWarn` in this
repository, and a rule is never demoted to make a build pass — a threshold
lowered until today's code fits is a description of the past, not a limit.

#### The four §2.1 rows that cannot be turned on yet

These are the complexity and size gates. All four are off. Together they produce
**54 findings**, every one of them in code that predates the analyzer.

| Rule | Limit | Over the limit | Worst offender |
|---|---|---|---|
| `S1541` cyclomatic complexity | ≤ 10 | 24 methods | 20 — `DeadlineCoherenceAnalyzer` |
| `S107` parameters | ≤ 7 | 14 members | 15 — `FlowModel`'s constructor |
| `S3776` cognitive complexity | ≤ 15 | 12 methods | 47 — `FlowEngine.RunRangeAsync` |
| `S138` method length | ≤ 60 lines | 4 methods | 118 — `FlowEngine.RunRangeAsync` |

Most of the `S1541` and `S3776` findings are marginal (11–18 against limits of 10
and 15) and concentrated in the compiler's syntax-dispatch code, where a flat
`switch` over syntax kinds scores as complexity without being hard to read.
`FlowEngine.RunRangeAsync` is not marginal on any of the four measures and is the
one place where all four agree.

One of the four should probably be retired rather than paid down: **`S1541` is
deprecated by Sonar** in favour of `S3776`, which measures the same property
without counting a flat dispatch as complexity. Keeping both means paying the
same debt twice against two numbers that disagree about what complexity is.

#### Open findings: closed

Three rules found genuine problems in files outside the scope of the change that
introduced them. **All three are now fixed, and every suppression has been deleted, so
each rule guards its own fix.** The block is kept rather than removed, because "a gate
was switched off and later switched back on" is the part a reader needs to be able to
check.

| Rule | Was | Now |
|---|---|---|
| `S8949` | Four Roslyn semantic-model calls not forwarding `context.CancellationToken` — the class `CA2016` is promoted to an error for, and a real cost in an editor | **Fixed** (WP-46). Token threaded; suppression deleted; rule live at default severity |
| `S2365` | `FlowModel.ComposedFlows` and `ReferencedCapabilities` allocated a `List` on every read while the emitter read each three times per flow | **Fixed** (WP-46). Computed once in the constructor — not cached lazily, because an incremental generator caches the model and hands it across threads. Suppression deleted |
| `S6966` / `VSTHRD103` | Three `Cancel()` calls in the engine where `CancelAsync()` exists, running callbacks synchronously on the engine's own thread | **Fixed** (WP-45), both rules re-enabled. Each site was checked against the fork's drain guarantee before the swap, not changed mechanically |

**One entry bundled two unrelated defects**, and only surfaced when the fix did not turn
the build green: the row cited `samples/ecommerce/Program.cs:43` alongside the three
engine lines, but that site is `app.Run()`, not a `Cancel()` call — `S6966` wants
`await app.RunAsync()`. It was then suppressed a second time, scoped to that one file,
on the grounds that blocking the main thread until shutdown is what a host entry point
does and `app.Run()` is the shape every template teaches.

**That argument was sound and the suppression is still gone** (the same unnumbered CI-gate
round as §4's DAST repair — *not* WP-50, which has not started). The sample's last
line is now `await app.RunAsync().ConfigureAwait(false)`, which under top-level statements
compiles to an async entry point and blocks until shutdown exactly as before — so nothing
was traded away to remove it. `S6966` now runs at its default severity across the whole
repository with no file-scoped exception, and the sample was re-run against `/health` and
`POST /api/v1/orders` to confirm the change is behavioural nothing. The `ConfigureAwait`
is `CA2007`, which applies everywhere but `tests/` and which the previous `Run()` form
never had to satisfy; adding it kept the fix from trading one suppression for another.

#### Rules switched off because they are wrong about this codebase

Each fired, was read, and was judged wrong here rather than merely inconvenient.

| Rule | Why not |
|---|---|
| `S3267` — use `Where`/`Select` | Trades a non-allocating loop for an iterator plus a closure, on `ForEachOutcome`, `ParallelOutcome` and the emitter's step scans. Budget **B2** forbids exactly that and `EngineAllocationTests` fails if it happens, so the two gates would contradict each other. |
| `S3236` — do not pass `[CallerArgumentExpression]` arguments | In `Identifiers.RequireIdentity`/`RequireSemanticVersion` the explicit `paramName` **is** the point: letting the compiler fill it in would name the helper's own local (`value`) in every `ArgumentException` instead of the caller's parameter. Following the rule would introduce the defect the rule exists to prevent. |
| `S2094` — no empty classes | `IsExternalInit` and friends in `FlowX.Compiler/Polyfills.cs` exist only so the netstandard2.0 compiler can bind records and `init`. An empty type is the whole design. |
| `S127` — do not advance the loop variable in the body | `FlowEngine`'s step loop, the CLI's argument parser and `FlowAnalyzer`'s step scan each do it deliberately — a branch advances the index to a jump target rather than by one — and each carries a comment saying so. |
| `S1075`, `S5332` — hardcoded / insecure URI | The two constants are URIs that *identify* rather than *address*: a WS-Federation-shaped claim type whose scheme is part of its identity, and the RFC 9457 `type` prefix, which the spec requires to be stable. Neither is ever dereferenced. |
| `S125` — commented-out code | The comment above `FlowEngine`'s step loop quotes the `for` loop it is deliberately not using, in order to explain why. |
| `VSTHRD200`, `VSTHRD003` | Naming and context rules the document does not ask for, firing only on benchmark entry points (whose names are the labels `docs/benchmarks/` compares runs by) and one test helper. |
| `S8969`, `S3358`, `S6618`, `S4136` | Stylistic, no behavioural difference, and disagreeing with the house style: redundant null-forgiving operators after an assertion the compiler cannot see through, nested ternaries in expression-bodied mapping code, `string.Create` over `FormattableString` in test helpers, and overload adjacency. |

#### Rules switched off for `tests/` only

`S1215` (`GC.Collect` **is** the measurement in the allocation tests), `S2699`
(the assertion is that `StartAsync` does not throw), `S2326` (phantom type
parameters are what is under test), `S3218`, `S5034` (reading
`ValueTask.IsCompleted` and then `GetAwaiter().GetResult()` is the documented
synchronous-completion pattern), `S3241`, `S3878`, and `VSTHRD002` — whose
rationale is deadlock on a captured synchronization context, which xUnit does not
install.

#### Two file-scoped exceptions

`S2245` stays an error everywhere except `FlowExecutionContext.cs` and
`ContextValues.cs`. Those two files *are* the determinism source §2.2 already
names: `CapabilityContext.Random` is journaled so that a replay reproduces the
run, and journalled and secret are opposites. A CSPRNG there would break replay
and secure nothing.

`VSTHRD002` stays an error across `src/` and `plugins/` except `FlowEngine.cs`,
where `Observe` reads `finished.Result` inside `if (finished.IsCompletedSuccessfully)`.
That is a read of an already-completed task, not a wait; the guard is what keeps
the parallel fast path allocation-free, and the rule does not model it.

#### One thing the analyzer could not tell us

`FlowExecutionContext.Random` was documented as journaling its seed on first use,
but it was constructed as `new Random()` — which chooses a seed that nothing can
read back, so whatever journaled the seed could not have been reading it from
there. This was outside the analyzers' reach and outside that change's scope; it is
kept here because it was noticed while reading an `S2245` finding, and a replay
guarantee that cannot hold is worth more attention than the finding that led to it.
*It has since been fixed rather than merely noted:*
[ADR-0015 commitment 4](adr/ADR-0015-journal-schema-and-durable-execution.md) made the
seed a journaled value, and WP-52 built it — the field is `new Random(seed)` drawn from
`Random.Shared`, the seed is exposed as `RandomSeed`, and a `Durable` flow writes it into
the step's `NondeterminismCapture`. The guarantee is now *possible*; nothing replays a
capture back into execution, so it is still not *proven* (WP-61).

---

## 3. OWASP Top 10 mapping

OWASP Top 10:2021, each risk mapped to the FlowX control that addresses it and the
gate that proves the control is present. "Verified by" is a CI job or an
executable test — never a review step alone.

| # | Risk | FlowX control | Verified by |
|---|---|---|---|
| **A01** | Broken access control | Authorisation is a **required member** on `[Capability]`, so a capability cannot compile without a stance — nor, since [`FLOWX1030`](diagnostics/FLOWX1030.md), with a `Permission` or `Policy` stance that names no permission or policy. Enforcement is at the business operation, not the route, so it holds over HTTP, Kafka and the agent surface alike. `Authorization.Internal` is *designed* to be unreachable from any external trigger — **the enforcement is not built**; nothing under `src/FlowX.Runtime`, `src/FlowX.Hosting` or `plugins/` reads the stance, which reaches the manifest and no further (P4). | `FLOWX1010`, `FLOWX1030` (build) · `EveryCapabilityDeclaresAuthorization`, `PublicCapabilitiesAreReviewed` (merge) · `CrossTenantAccessIsDenied` — **not yet enforced, see §2.4** |
| **A02** | Cryptographic failures | The intent is that `[Sensitive]` redaction is applied by the **generated** serialiser so no code path reaches logs, traces, journal or replay output un-redacted. **Today it reaches two sinks**: the RFC 7807 body, redacted by `ProblemDetailsMapper`, and — since WP-52 — the journal, where `JournalPayload` makes redaction the only exit rather than an opt-in helper. Logs, traces and replay output do not exist (P3, P5), and redaction is still not generated. Secrets resolve from a secret provider at startup; `IConfiguration` is never a secret source. TLS is required for every egress plugin. | `ManifestContainsNoSecrets` (merge) · secret-scanning (merge) · `RedactionCannotBeBypassed` — **not yet enforced, see §2.4** |
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

| Stage | Tool | Scope | Gate | Frequency | Runs today |
|---|---|---|---|---|---|
| SAST | **CodeQL** (`security-and-quality`) | whole solution | any alert ≥ medium fails | every PR | **partly** — the scan runs and uploads results; `codeql-action/analyze` does not fail a job on findings, so "fails" is branch-protection configuration, not this workflow |
| SAST | **Semgrep** (OWASP + C# rulesets) | whole solution | any ERROR fails | every PR | **yes** — `--error` |
| Secrets | **Gitleaks** + GitHub secret scanning | full history on PR | any finding fails | every PR | **yes** |
| SCA | `dotnet list package --vulnerable --include-transitive` | all projects | any vulnerability fails | every PR | **yes** |
| SCA | **Dependabot** | NuGet + GitHub Actions | review required | weekly | **yes** — `.github/dependabot.yml` |
| Container | **Trivy** | published image | HIGH/CRITICAL fails | every PR | **no** — not in any workflow. There is no `Dockerfile` and no image is built anywhere, so there is nothing to scan |
| IaC | **Checkov** | Helm charts, Kubernetes manifests | HIGH fails | every PR | **no-op** — the step exists and exits cleanly because neither `deploy/` nor `charts/` exists. Deliberate, and stated in the job |
| **DAST** | **OWASP ZAP** baseline + full scan | `samples/ecommerce` run by `dotnet run` | any HIGH fails | nightly | **newly** — the job's guard tested for two paths that never existed, so it skipped every night since WP-10; repaired, and not yet observed on a real scheduled run. *(This cell credited the repair to **WP-50**. WP-50 is the B7/B8 benchmarks and the QR2 chaos rig, and the gate repairs were unnumbered work; attributing them to another package made it look more delivered than it was. **The correction itself has since half expired and is corrected in turn:** it read "none of it has been built — `JournalBenchmarks` and `scripts/chaos-qr2.sh` do not exist". The rig shipped on 2026-08-01 as `tests/FlowX.Chaos` with `scripts/run-chaos-qr2.sh`, and `.github/workflows/chaos.yml` schedules it since WP-62 — see [§8](#8-reliability-gates). `JournalBenchmarks` still does not exist and B7 and B8 are still unmeasured, which is the half that stands. Neither has anything to do with this row, which is about DAST.)* `samples/banking` is a README, not a project, and nothing runs in Docker |
| Fuzzing | **SharpFuzz** | trigger payload deserialisation | any crash fails | nightly | **no** — not in any workflow |
| Supply chain | **CycloneDX SBOM** + Sigstore | release artifacts | missing attestation fails | every release | **no** — there is no release workflow and no tag-triggered workflow at all |

**The *Runs today* column is new, and four rows of this table were false without it.**
Trivy, SharpFuzz, SBOM and Sigstore appear nowhere in `.github/`; Checkov is wired but
has nothing to scan; DAST was guarded off by a condition that could never become true.
The *Gate* and *Frequency* columns are kept as written because they are the design and
the design is not in dispute — but read on their own they claimed nine running security
gates where five run, one is newly unblocked and three do not exist. Every "(release)"
verification named in §3 — build reproducibility, signature verification, SBOM attached
— is in the last category: **this repository has no release pipeline**, so no release
gate of any kind currently runs.

### 4.1 Why DAST runs against samples

FlowX is a library; there is no FlowX server to point a scanner at. The samples
are the honest target — they exercise the generated HTTP surface, the generated
Problem Details mapping, the authorisation stack and the idempotency store the
way a real application does.

This section said `samples/banking` **is** the primary DAST target, "the sample designed
around money and PII, so its threat model is the strictest one in the set". That is the
intent and it is not built: `samples/banking` is a `README.md` and nothing else. The one
sample that exists as a project is `samples/ecommerce`, and it is what the scheduled scan
points at. Banking becomes the primary target when it becomes a project.

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

| Gate | Rule | Class | State |
|---|---|---|---|
| B1, B3, B12 | allocation change vs `baseline.json`, or p95 over the documented ceiling, fails the build | Merge | **runs, and is currently red on `dev`** — *Benchmark budgets* job. Blocking and failing since at least run #41 on 2026-07-31, on three allocation entries that are not the engine's; see [benchmarks/README §5.2](benchmarks/README.md#52-the-gate-above-has-been-red-on-dev-and-that-is-why-16-bytes-got-in) for which, and for the sixteen bytes that crossed `dev` because a red gate is a gate nobody reads. Timing drift is measured and printed but **advisory**, see below |
| B2 | allocations must be **exactly 0** — not "low" | Merge | **runs** — `AllocationBudgetTests`, `EngineAllocationTests` |
| Generator cost | > 2 % more bytes allocated by the generator than the committed baseline fails the build | Merge | **runs** — [generator-cost-gate.md](benchmarks/generator-cost-gate.md) |
| B12 against its **+8 %** budget | — | — | **failing.** +46.6 % at 50 flows, +77 % at 200. The relative gate above stops it getting worse; it does not make the budget met |
| B4, B5, B6, B10, B11 | regression > 5 % vs the baseline | Merge | **no harness**, and one exception. Policy chain (P4) and start-up/RSS (P9) have nothing to measure. Telemetry now emits (WP-90), so **B5** — telemetry cost *with* an exporter attached — has a subject and no harness, which is the ordinary state of this row. **B6 is different: it is a hard zero, and a hard zero needs no harness.** `TelemetryCostTests` asserts it as a unit test the way `AllocationBudgetTests` asserts B2, on every pull request, and the nanosecond half of B6 is deliberately not measured rather than measured badly |
| B7–B9, B13 | nightly load test; regression opens a blocking issue | Release | **no harness.** Journal (P2 — the step-commit path exists since WP-52 and `plugins/FlowX.Postgres` backs it since WP-53; *"no store backs it" has stopped being the reason* — nothing measures it, WP-50), HTTP end-to-end (P3), streaming (P7). ***WP-50 is "B7, B8 and the QR2 chaos rig" and shipped the rig only***, so this row is unchanged by it: [§8](#8-reliability-gates)'s chaos row now runs and these budgets still have nothing measuring them |
| Baseline updates | require a reviewed commit stating why the budget moved | Merge | convention |

**The old version of this table said B1–B6 and B10–B12 were gated on merge and
B7–B9 and B13 nightly. Nine of the thirteen budgets have no benchmark at all** —
see [14 §8](14-Performance.md#8-benchmark-suite-and-ci-gating), where the file
listing that implied otherwise is corrected too. A budget stated in advance is
[rule zero](#1-the-rule-that-makes-the-rest-work) working as designed; a budget
listed as *gated* when nothing measures it is the failure this document exists to
prevent.

**The B1/B3/B12 row was itself an instance of that failure, and is corrected above.**
It read "regression > 5 % vs `baseline.json` fails the build". Nothing in this
repository enforces 5 %, and drift does not fail anything:
`scripts/check-benchmark-budgets.py` puts drift in `blocking` only under `--strict`,
the *Benchmark budgets* job does not pass `--strict`, and the tolerances committed in
`docs/benchmarks/baseline.json` are **40 %** absolute and **15 %** ratio — not 5 %.
What the job actually blocks on is the two machine-independent checks: allocation
counts, compared exactly (or against a declared `allocationTolerancePercent` band),
and p95 against the ceiling written down in [14-Performance](14-Performance.md).
The split is deliberate — two runs of the same commit disagreed by 63 % on ratio and
159 % on absolute time on a shared runner — and `--strict` is scheduled for when the
baseline is recorded on dedicated hardware (WP-11). [CONTRIBUTING](../CONTRIBUTING.md)
already described this correctly; this table was the copy that had gone stale.

A benchmark that becomes flaky is fixed or deleted, never muted. A muted
benchmark is a budget nobody is holding.

---

## 8. Reliability gates

***This section opened "none of the first four runs, and none can" — the first row now
does.*** The rest are the exit criteria of the phases that build the subsystems they test,
listed here so the criteria are agreed before the code is written. *WP-52 landed a durable
seam without moving any of them; WP-53 supplied the store, WP-55 the lease and the recovery
scan, and the first two rows still did not move — because what they were waiting on turned
out to be the rig.* **WP-50 built the rig**, and the row below is what it measured rather
than what it hopes.

| Gate | Rule | Class | State |
|---|---|---|---|
| Chaos: SIGKILL at every step boundary | 10 000 flows, zero duplicate non-idempotent effects, zero lost instances | Release · nightly | **runs, and since WP-62 runs on a schedule.** *This row said "not written … nothing kills a node, nothing crosses a process boundary, and nothing has run 10 000 of anything". All three expired.* `tests/FlowX.Chaos` spawns worker processes against a shared PostgreSQL and **SIGKILLs them at a step boundary chosen so the effect has happened and the commit has not** — 97 kills per arm, every one observed to exit 137. At **10 000 flows** per arm: **zero duplicate effects against the guarantee, zero lost instances**, zero orphan effects, zero instances run by two live nodes. The duplicates it *does* find — 260 and 186 — are all [ADR-0006](adr/ADR-0006-journal-and-leases.md)'s documented window, and at concurrency 1 that is exactly one per kill and exactly zero when the kill moves after the commit. **It does not run in the ordinary suite and must not**: it is opt-in on `FLOWX_CHAOS`, skipping with a reason when unset and failing rather than skipping when set with no database. Record: [benchmarks/QR2-chaos.md](benchmarks/QR2-chaos.md). **WP-62 put it on a schedule (2026-08-01)**: [`.github/workflows/chaos.yml`](../.github/workflows/chaos.yml) runs it at 03:41 UTC against a PostgreSQL 16 service container and takes `scripts/check-chaos-qr2.py`'s exit code as the verdict — 0 PASS, 1 FAIL, **2 INCONCLUSIVE, which is a third state and is not folded into either of the others**. *That row previously read "**No CI job runs it**", and it is what changed.* **What a red night costs, exactly**: the job is red **and** an issue labelled `qr2-nightly` is opened and assigned to the repository owner, commented on rather than duplicated if the next night is red too, and closed automatically by the next green one. **This is not a merge gate and this row does not claim one** — it runs on a schedule, against no pull request, and [§8.1](#81-why-the-chaos-nightly-is-not-described-as-blocking) is why that phrasing is chosen deliberately. **P2** (QR2) |
| Chaos: resume p99 ≤ 45 s | the latency half of the same criterion | Release | **measured, not gated — WP-50.** **32.9 s** and **32.6 s** at 10 000 flows. The rig prints it against 45 s on every run and its checker does not fail on it, because B7 and B8 are latency budgets set aside for this phase (WP-50 shipped the rig only). **Two other runs of the same rig missed the budget** — 48.1 s at the rig's own defaults, 69.9 s in a 500-flow pilot — with every correctness row still zero, so **this is not a figure to quote on its own**. It is ~30 s of lease TTL plus however long a backlog takes to drain through `MaxConcurrentRecoveries`, which means a deployment that raises `LeaseTtl` above 45 s fails QR2 by configuration and one whose recovery capacity is below its crash rate fails it by queueing. [benchmarks/QR2-chaos.md §4.4](benchmarks/QR2-chaos.md). **WP-62 scheduled the row above and deliberately left this one where it is.** The nightly job passes the checker no budget argument, so nothing in it can fail on the p99; and because "not gated" decays into "gated" the moment somebody adds one line, that is now asserted rather than intended — [`scripts/selftest-chaos-verdict.py`](../scripts/selftest-chaos-verdict.py) feeds the checker a run with a **999 s** resume p99 and every correctness row zero and requires a **PASS**. It runs on every pull request, so the change that turned this into a gate would be red on the pull request that made it. **P2** |
| Replay determinism corpus | zero divergence across the full corpus | Merge | **runs — WP-61, 2026-07-31.** *This row said the corpus was not written and that nothing replayed a capture back into execution.* `ReplayDeterminismTests` in `tests/FlowX.Runtime.Tests` is the corpus, and it runs in the ordinary test job rather than needing one of its own. Zero divergence across eight shapes; the three shapes that cannot replay — an overlapping fork, a compensation's ambient reads, the engine's own deadline check — are asserted to diverge and named, so closing any of them turns the gate red until the note is deleted |
| Backpressure conformance | bounded memory with a deliberately slow capability | Release | **not written** — no Stream Engine. **P7** |
| Tenant fairness | one tenant at 10× quota degrades another's p99 by ≤ 10 % | Release | **not written** — no quota, no admission control. **P6** |
| Graceful shutdown | in-flight flows drain within the termination grace period | Merge | **runs** — `DrainTests` in `FlowX.Hosting.Tests`. *Drain only, and the caveat has narrowed: this said "there is no checkpoint, so a flow still running at the end of the grace period is lost rather than resumed". Since WP-52/WP-53 a `Durable` flow has a committed prefix and since WP-55 the host releases its lease on the way out, so another node's recovery scan can pick it up. An `Ephemeral` one — the default — is still simply lost, and the drain test asserts the drain, not the takeover* |

### 8.1 Why the chaos nightly is not described as blocking

The word matters here more than usual, because [§7](#7-performance-gates) was corrected for
getting it wrong. That table described the *Benchmark budgets* job as one that "runs", and
[CHECKLIST **B-4**](../CHECKLIST.md) and the unnumbered package above `WP-31` in
[PLAN](../PLAN.md) then established what "runs" was hiding: the job is blocking, it has been
red on `dev` since 2026-07-31, sixty-odd pushes merged over it, sixteen bytes of allocation
regression crossed underneath it — and nothing told anyone, because no branch protection
required it and no notification fired. Their sentence for that state is the design
constraint on the chaos job, not a footnote to it:

> A merge-class gate whose failure has no consequence is a nightly report with a red icon.

A nightly chaos run cannot be made merge-blocking. It runs at 03:41 against no pull request,
it takes minutes, it kills operating-system processes, and there is no author to hold
responsible for a `SIGKILL` that duplicated an effect. So the consequence is not a merge
check and this section does not pretend otherwise. **Three things are true instead:**

1. **A non-PASS reaches a person.** The job is red *and*
   [`scripts/publish-chaos-verdict.py`](../scripts/publish-chaos-verdict.py) opens a labelled
   issue, assigned to the repository owner, carrying the failing clause, the arm, the exact
   command that reproduces the run, and the results document as an artifact. §7's own
   Release-class convention — *"nightly load test; regression opens a blocking issue"* — is
   what that implements. A second red night comments on the same issue rather than opening a
   second one, and a green night closes it, because a tracker that fills up and a red that
   nobody closes are the same failure one step later.
2. **The judgement is merge-gated even though the run is not.** `verdict-self-test` in
   [`chaos.yml`](../.github/workflows/chaos.yml) runs on every push and pull request, needs
   no database, and asserts that `check-chaos-qr2.py` still returns each of its three
   verdicts — including that the recorded run's 260 and 186 duplicates inside ADR-0006's
   documented window are **not** a failure, and that a 999 s resume p99 **is** still a pass.
   The chaos run is where a regression shows up; the checker is where a softening would.
3. **`INCONCLUSIVE` is not converted into a pass.** The checker refuses to grade a run in
   which nothing was killed, no worker exited 137, nothing was recovered, or an arm ran
   fewer flows than it registered. The job carries that through as its own state, with its
   own issue and its own wording, and is red for it. Folding it into PASS would tick the
   chaos row above on evidence that does not exist, which is the outcome that exit code was
   written to prevent; folding it into FAIL would report a defect nobody observed.

**What has not happened yet:** no scheduled run has occurred, because the schedule takes
effect when this lands on the default branch. Everything above is what the workflow says;
none of it is an observation of a real nightly, and the first one is also the first time the
10 000-flow configuration has been asked of a hosted runner.

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

# Live Checklist

> **This file is updated with every change.** It is the single place that answers
> "where is this project actually at?" — the [plan](PLAN.md) says what to build,
> this says what is built.
>
> **Last updated:** 2026-07-31 · **Phase:** **P0 complete · P1 closed with one accepted
> exception → P2 in progress: WP-51, WP-52, WP-53, WP-55 and WP-58 landed, WP-50 still not
> started** · **Commit:** see `git log`
>
> **Durable execution runs against a real database, and is not yet end to end.** WP-52 made
> `FlowX.Runtime` read `ExecutionProfile`: a `Durable` flow journals one row per
> `(instance, scope, step, attempt)` and resumes through the same step loop. WP-55 added
> lease acquisition with background renewal and a recovery scan that claims instances whose
> lease expired. WP-53 added `plugins/FlowX.Postgres`, which passes the WP-51 conformance
> suite unmodified from a different assembly against PostgreSQL 16.13 — and found three
> clauses of ADR-0015 that a dictionary could not have,
> [ADR-0016](docs/adr/ADR-0016-postgres-journal-adapter.md).
>
> **What is still missing is not small:** the transactional outbox (WP-56), `AwaitSignal`
> and durable suspension (WP-63), Redis (WP-54), and — the one that matters most for a
> claim about durability — **both durability budgets are unreported rather than passed**,
> because WP-50, the benchmark harness, has not started. A journal has been made correct
> without being made fast. See
> [§5d](#5d-p2--durable-execution--correct-against-a-real-database-and-unmeasured).
>
> **Build:** 0 warnings, 0 errors · **Tests:** 1490/1490 passing (86 of them against a live
> PostgreSQL 16.13; 0 skipped). Without `FLOWX_POSTGRES_CONNECTION` the adapter suite skips
> 79 with reasons; set to an unreachable server it **fails 80 and skips none**, on purpose ·
> **Coverage:** **83.9 % line / 77.6 % branch** over `src/` and `plugins/`, measured
> 2026-07-31 with a live PostgreSQL. *This line read 94.0 / 87.0 for several phases. That
> figure was not re-measured as the codebase grew and was overstated by about ten points;
> it is replaced rather than annotated.* Both are above the stated 80 / 75 — **and nothing
> enforces them**: CI collects coverage and no step compares it to a threshold, so the
> "gate" has never once failed a build. Two further caveats a reader needs: CI runs
> without `FLOWX_POSTGRES_CONNECTION`, so its number is lower than this one, and the
> figure excludes generated code · **SDK:** 10.0.110
> **P0 kill criterion: PASS** — B1 **172.3 ns** / 5 000 ns budget · B2 **0 B** exactly ·
> B3 dispatch 21.9 ns / 150 ns. See [P0.md](docs/benchmarks/P0.md)
>
> ### ⚠ P1 closed over an unmet exit criterion, on purpose
>
> **Build overhead at 200 flows is +67.1 %** [+61.9, +73.6] against a **≤ 8 %** exit
> criterion — failed by 59 points. The repository owner set performance aside and closed
> the phase on 2026-07-31; the criterion is carried into P2 as a **named, accepted
> exception**. Its box below stays `[~]`, not `[x]`, and the reason it is stated up here
> rather than only at line 500 is that a phase closed over a failing criterion a reader
> has to go looking for is the exact drift this project spent P1 removing. Detail and the
> open decision: [PLAN §4](PLAN.md#4-p1--compiler-hardening).
>
> Legend: `[x]` done and verified · `[~]` partial — shipped but incomplete, blocked, or
> failing its own criterion, with the gap named on the line · `[ ]` not started
>
> **`[~]` is not a softer `[x]`.** Nothing in this file is ticked because a phase closed;
> the P1 exception is the reason the legend exists.

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
- [x] **B-3 · ~~Delete a stray tooling-prefixed branch from the remote.~~ RESOLVED.**
      Gone from the remote. History scan is clean: no commit in any branch has
      bot authorship, a generated-by footer, or a signature. *The branch name itself
      is no longer written here: the guard forbids that string in tracked files, and a
      resolved blocker is not a licence to keep the thing it was about.*

---

## 1. Documentation

- [x] 20 specification documents, `docs/01` – `docs/20`
- [~] **16 ADRs** with trade-offs stated. *This line said "15 ADRs … ADR-0014 and ADR-0015
      are **Proposed**" and was wrong twice: ADR-0016 was uncounted, and ADR-0015 became
      **Accepted** at WP-53 — which this file records correctly 800 lines further down. A
      count and a status, both wrong, both ticked `[x]`.* **[ADR-0014](docs/adr/ADR-0014-derived-error-catalogue-vs-build-budget.md)
      is the only one still Proposed**, and it is `[~]` rather than `[x]` because three of
      the sixteen do not meet the [index's own template rule](docs/adr/README.md):
      **ADR-0013** has no `Revisit when`, **ADR-0016** has no `Negative` section, and
      **ADR-0014** has no `Context` heading. See [PLAN open item 11](PLAN.md#9-open-items-blocking-the-plan)
- [x] `docs/diagnostics/` — 23 pages plus an index, one per raised diagnostic; every help
      URI resolves, asserted by test
- [x] `docs/benchmarks/` — baseline, gate policy, and the honest caveats
- [x] 9 sample application specifications
- [x] `CONTRIBUTING.md`, `SECURITY.md`, `LICENSE` (Apache-2.0)
- [x] `docs/21-Quality-Gates.md` — SonarQube thresholds, OWASP mapping, debt policy
- [x] `PLAN.md` — every work package from WP-0 to WP-76 with a mechanically checkable
      exit criterion: P0 and P1 as executed, P2 in full, P3 lighter and said to be lighter
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
- [x] `quality.yml` — coverage thresholds, Sonar gate, Stryker mutation, unaccountable-TODO
      rule. The suppression rule is `SuppressionsAreAccountable` in the fitness functions,
      and only there (WP-35)
- [x] `.github/dependabot.yml` — NuGet + Actions, grouped
- [x] `.github/pull_request_template.md` carrying the Definition of Done
- [~] Each gate class verified by a deliberate violation — **five done, two found broken**.
      Link check (probe linking nowhere), Mermaid parse (unclosed `subgraph`; baseline
      **70/70 diagrams parse**), trim/AOT (`Type.GetType` + `Activator.CreateInstance` →
      `IL2057`), attribution guard (bot-authored commits in throwaway repos), and the
      debt/TODO rule all fire on their violation and stay silent otherwise.
      **The attribution guard had been failing on every push** — `git log --all` with
      `fetch-depth: 0` reaches the six open `dependabot/*` branches this repo asks for in
      its own config. Verified: 6 bot commits via `--all`, **0 from `HEAD`**. Scoped to
      `HEAD`, which narrows blast radius and not the rule.
      **The DAST job had never run once** — its guard tested for paths that never existed,
      so it skipped every scheduled night while printing that it was not yet runnable.
      Fixed, and **still never observed to pass**, which is why this box is `[~]`.
      Needing a real CI run: Sonar, coverage, Stryker, CodeQL, Semgrep, Gitleaks, ZAP, and
      the benchmark jobs
- [~] `SONAR_TOKEN` repository secret configured — still unset, but the no-op is now
      **loud**: the job emits a `::warning::` and a step-summary table naming the rows it
      did not evaluate. It still exits 0, because failing would punish fork contributors
      for a secret they cannot have — but a green tick can no longer be read as a pass
- [x] **SonarQube rules actually run** (WP-44). `SonarAnalyzer.CSharp` is referenced
      `PrivateAssets="all"`, and the default profile — 329 of 471 rules — gates every
      compile, with `S2245`, `S4507` and `VSTHRD002` as errors, each proved to bite.
      **The document was wrong about itself**: `S3776`, `S1541`, `S138` and `S107` ship
      `IsEnabledByDefault=false`, so referencing the package leaves them silent — and a
      build with the package installed and those rules off looks exactly like one that
      passes them. Named explicitly they produce **54 findings**, `FlowEngine.RunRangeAsync`
      failing all four; they are `none` with counts and reasons, thresholds pinned in
      `SonarLint.xml` because `.editorconfig` silently ignores them
- [ ] **Three real defects Sonar found in files WP-44 did not own.** Four analyzer
      semantic-model calls drop `context.CancellationToken` (the class `CA2016` is promoted
      to error for); `FlowModel.ComposedFlows` and `ReferencedCapabilities` allocate a
      `List` per read while the emitter reads them repeatedly; and three `Cancel()` calls
      should be `CancelAsync()` — flagged independently by two analyzers on the same lines
- [ ] **Two gates in this repository contradict each other.** `S3267` would rewrite the
      engine's loops into LINQ, which breaks budget B2 — `EngineAllocationTests` is the
      arbiter and the rule is off. Worth knowing that the quality bar and the performance
      bar disagree, rather than discovering it at the next upgrade
- [x] **~~`FlowExecutionContext.Random` documents a journaled seed it cannot produce.~~
      Closed by WP-52.** It was built as `new Random()`, whose seed nothing can read back,
      so the replay guarantee the remarks described could not hold — found incidentally
      while reading an `S2245` finding. The seed is now drawn from `Random.Shared.Next()`,
      exposed as `RandomSeed`, and written into the step's `NondeterminismCapture`, which is
      the only construction under which those remarks are true. ADR-0015 commitment 4 named
      this defect and fixing it as the same act

---

## 3. WP-1 · `FlowX.Abstractions` — **compiled, 0 warnings**

- [x] `Result<T>` — readonly struct, allocation-free failure path
- [x] `Error`, `ErrorCategory` — closed set, terminal/retryable, HTTP mapping
- [x] `ICapability<TIn, TOut>` — the eight rules documented on the interface, **seven of
      them enforced at build time** since WP-58 raised `FLOWX1007`–`FLOWX1009`. The one
      that is not is contract immutability (`FLOWX1006`, WP-59), and the doc comment says
      which is which rather than claiming the list is compiler-enforced, as it once did
- [x] `CapabilityAttribute` — `Version` and `Authorization` as required members
- [x] `Authorization`, `ApprovedByAttribute`, `SensitiveAttribute`
- [x] `CapabilityContext`, `FlowContext<TIn>` — clock, ids, randomness, deadline
- [x] `Flow<TIn, TOut>`, `IFlowBuilder<,>` — no `Do(lambda)`, no trigger types
- [x] `ExecutionProfile` — `Ephemeral` as the zero value
- [x] `PolicySet`, `PolicyStage` — stage order encoding the safety guarantees
- [x] Trigger attributes — http, kafka, cron, stream, agent
- [x] Zero package references, zero project references

Verified by `dotnet build -c Release` with `TreatWarningsAsErrors`, and by the fitness
functions in §4, which assert these properties by reflection over the built contract
surface and by reading the repository's own source.

---

## 4. WP-1 · Architecture fitness functions — **29 enforced, 3 blocked and named**

The heading here used to read *30/30 green* with seven boxes below it empty, and the line
above §4 spoke of *29 fitness tests*. Neither number was reachable from the list. What
follows is the list as it is — including five functions that were already running and had
never been written down.

[05-Architecture §12](docs/05-Architecture.md) names fourteen gates; **seven of them
existed nowhere** when WP-30 audited this section, and two more existed under other names.
WP-35 closed six of the seven and named the last as blocked. The §12 table now carries a
*Lives in* column, so a name with nothing behind it is visible in the table itself rather
than only in a footnote.

### Structure

- [x] `AbstractionsHasNoDependencies` — reads the `.csproj`
- [x] `LayersPointInward`
- [x] `EverySourceProjectIsCoveredByTheLayeringRule` — a rule with a hand-maintained
      subject list needs a rule about the list
- [x] `RuntimeDoesNotReferenceAnyPlugin`
- [x] `CliDependsOnNothingButTheManifest`
- [x] `RoslynComponentsReferenceNoRuntimeAssemblies`
- [x] `EveryShippedRuntimeProjectIsAotAnalyzed` — exempts Roslyn components
- [x] `RoslynComponentsTargetNetStandard20`
- [x] `NoCyclicDependencies` — DFS over the project graph, not just direct edges
- [x] `ModelLayerHasNoRoslynDependency` / `EmitLayerHasNoRoslynDependency` — the R1 mitigation

### Contract surface

- [x] `ResultIsAnAllocationFreeValueType`
- [x] `ErrorCategoryRemainsClosed`
- [x] `TerminalCategoriesAreNeverRetried`
- [x] `ErrorCategoryMapsToTheDocumentedHttpStatus`
- [x] `CapabilityMustDeclareVersionAndAuthorization`
- [x] `ExecutionProfileDefaultsToEphemeral`
- [x] `PolicyStageOrderEncodesTheSafetyGuarantees`
- [x] `TenantScopedIsTheDefaultForEveryScopeEnum`
- [x] `FlowBuilderExposesNoTransportTypes`
- [x] `FlowBuilderHasNoEscapeHatchForInlineCode`
- [x] `EveryPublicTypeIsInTheFlowXNamespace`
- [x] `EveryDiagnosticIsHelpful` — in `FlowX.Compiler.Tests`, beside what it governs

### Security · WP-30

Four of these were listed here, cited in the OWASP mapping in
[21-Quality-Gates §3](docs/21-Quality-Gates.md) and in
[15-Security §10](docs/15-Security.md), and existed nowhere in the repository. A control
that is claimed and absent is worse than one never claimed: the claim is what stops
anyone looking.

- [x] `SuppressionsAreAccountable` — every suppression carries a `FLOWX-DEBT` marker whose
      id has a row in `docs/DEBT.md`, unexpired and at most six months out, with the marker
      within six lines of the suppression. **Now the only implementation** (WP-35): the
      shell copy in `quality.yml` asked whether the *file* contained a marker anywhere, so
      one accountable suppression licensed every unaccountable one below it, and it never
      checked the id was registered. Demonstrated on a file the shell step exited 0 on and
      this one reports by line. Deleted rather than repaired — two implementations of one
      rule disagree eventually, and the weaker one is what a developer meets first. The
      fitness function's trees gained `scripts/`, which the shell walk covered and the
      named trees did not
- [x] `ManifestContainsNoSecrets` — pattern scan over the manifests the build **actually
      emitted**, matching the shape of a secret rather than the word. See the note below
- [x] `EveryCapabilityDeclaresAuthorization` — every `ICapability<,>` under `src/`,
      `plugins/` and `samples/` carries `[Capability]` naming a stance. Distinct from
      `CapabilityMustDeclareVersionAndAuthorization`, which only says the stance cannot be
      omitted from an attribute that is already present
- [x] `PublicCapabilitiesAreReviewed` — `Authorization.Public` requires `[ApprovedBy]` with
      a reviewer and an ISO-8601 date, and an approval left behind after the stance narrowed
      is also a failure. Nothing declares `Public` today, so it currently rejects nothing
- [x] `NoPermissiveDefaults` — no property or optional parameter on the contract surface
      reaches a permissive stance by being left alone. `Authorization.Public` is the *zero
      value* of its enum, so `required` is the only thing between `default(Authorization)`
      and "anyone may invoke it"
- [ ] `CrossTenantAccessIsDenied` — **blocked, not overlooked, and it lost half its
      blocker at WP-52 without becoming writable.** Nothing consumes `TenantId`: no policy
      executes at runtime, so no stage can return `Forbidden`; one transport exists, so
      "every trigger kind" cannot be exercised. *The third clause — "there is no journal,
      so there is no audit event to assert" — stopped being true on 2026-07-31: a `Durable`
      flow journals its step boundaries and the instance row carries `tenant_id`. That is a
      record, not an audit event, and the `Forbidden` this test asserts still cannot
      happen.* Needs **P4** (policy execution, audit) and **P3** (a second transport).
      [21-Quality-Gates §2.4](docs/21-Quality-Gates.md)
- [ ] `RedactionCannotBeBypassed` — **blocked, not overlooked.** *This entry said exactly
      one sink can serialise a contract value; since WP-52 (2026-07-31) there are two.* The
      RFC 7807 body, covered by `ProblemDetailsMapperTests`, and the **journal**, where
      redaction is structural rather than remembered — a payload enters through
      `JournalPayload` and its only exit is `ToJson()`, which redacts, so a store has no
      route to the object graph. Proven by a durable flow whose input, state bag and every
      step result carry a marked member, read back from all six stored strings
      (`DurableSeamTests`). Logs, traces and replay output — the remaining two of the four
      sinks the rule is about — do not exist. Needs P3 and P5. Same section

### Runtime, transport and published contract · WP-35

The six of §12's seven absent gates that were writable. Each was proved able to fail by
introducing the violation it targets and observing red; the violation used is named beside
it, because a gate nobody has seen fail is a gate nobody has tested.

- [x] `NoReflectionOnHotPath` — IL scan of `FlowX.Abstractions`, `FlowX.Core` and
      `FlowX.Runtime` for `System.Reflection`, `System.Runtime.Loader`, `Activator`,
      `AppDomain` and the C# runtime binder. IL rather than source because
      [P4](docs/03-Design-Principles.md) says so and because
      `x.GetType().GetMethod(…)` needs no `using` to compile. One exemption,
      `MemberInfo.Name`: `typeof(T).Name` is how the runtime names the contract a step
      failed to produce, it discovers nothing, and a namespace-only rule would report
      every diagnostic message in the engine. *Proved by* `typeof(ContextPool).Assembly`
      in `ContextPool`
- [x] `RuntimeHasNoMutableStatics` — every static field in `FlowX.Runtime` is `readonly`
      or `const`. Compiler-generated fields — lambda caches, async state machines — are
      exempt, because a rule whose only fix is to stop using `async` is not a rule.
      *Proved by* a `private static int _rentCount` incremented in `ContextPool.Rent`
- [x] `FlowsAreTransportFree` — the transitive closure of every `Flow<,>` in a shipping
      assembly, through the types that assembly declares, reaches no transport namespace
      and no plugin. Includes the generated half of the flow and the state machines the
      compiler nested inside it. *Proved by* an `HttpContext` parameter on `PlaceOrderFlow`
- [x] `CapabilitiesDoNotCallCapabilities` — wider than FLOWX1004, which reads declared
      dependencies. This reads method bodies, so a capability that constructs and awaits
      another inside `ExecuteAsync` is caught. *Proved by* `ValidateOrder` awaiting
      `new ReleaseInventory(store)` — which the analyzer passes
- [x] `EveryPublicContractIsVersioned` — every flow, capability, event, emitted manifest
      and packable assembly carries a SemVer 2.0 version. *Proved by* `payment.capture`
      declaring `Version = "2.1"`
- [x] `ManifestIsComplete` — every flow and capability an assembly declares appears in the
      manifest its build emitted, every step's capability and compensation has a full entry
      rather than a mention, and every emitted event is in the event catalogue. *Proved by*
      adding an `order.archive` capability no flow uses
- [ ] `PluginsPassConformance` — **blocked, not overlooked, and the reason has narrowed.**
      This line said there is no conformance suite. There is one now,
      `tests/FlowX.Conformance.Tests` (WP-51), and it holds `JournalConformance` and
      `LeaseStoreConformance` — **not** the `TriggerSourceConformance` this gate would run,
      and `ITriggerSource` is still undeclared. The project is not packable, so nothing
      outside this repository can run it. *This entry also said "nothing has ever run
      against a real database" and "there is one plugin". Both expired at WP-53:
      `plugins/FlowX.Postgres` runs both suites unmodified from another assembly against
      PostgreSQL 16.13, and there are two plugins.* **What has not changed is the claim
      that matters here:** there is still **one implementation per abstraction**, so
      "every plugin agrees" still has one data point — the second store is WP-54, and the
      suite this gate would actually run does not exist.
      [05-Architecture §11](docs/05-Architecture.md) names publishing a suite as the
      mitigation for R3 and R8 and that has not happened.
      [21-Quality-Gates §2.4](docs/21-Quality-Gates.md)

`ManifestIsComplete` does **not** check policies, although the §12 row is written as
though it did: nothing declares a policy, so there is nothing to be missing. Recorded in
§2.4 rather than written as an assertion that cannot fail.

Written alongside the above so none of them can pass by finding nothing:
`TheReflectionScanFindsReflectionWhereItIsExpected`,
`TheClosureWalkFindsATransportWhenThereIsOne`, `TheCapabilityScanFindsTheShippedCapabilities`,
`TheManifestReaderFindsTheSamplesManifest`. Two more keep copied definitions honest:
`TheTransportListMatchesTheAnalyzers` and `TheVersionPatternMatchesTheRuntimes` compare this
project's copy of the transport list and the SemVer pattern against the analyzer and
`FlowX.Core` that define them — neither can be referenced from here, so equality is
asserted rather than assumed.

Written alongside the security family so it cannot pass by finding nothing:
`TheCapabilitySurveyFindsTheShippedCapabilities`,
`EveryDeclaredStanceIsAKnownAuthorizationMember`,
`ApprovalsDoNotOutliveTheStanceTheyApproved`, `EveryScopeEnumDefaultsToTenant`,
`TheDebtRegisterIsWellFormed`.

> **On `ManifestContainsNoSecrets`.** The rule *was* asserted — in
> `FlowX.Compiler.Tests/ManifestWriterTests.ContainsStructureButNoValues`, under a doc
> comment citing this exact name — but over a hand-built model, and by forbidden **word**.
> Run that same word list against the manifest `samples/ecommerce` actually emits and it
> fails: the manifest correctly lists a `[Sensitive]` member named `PaymentToken`, and the
> list forbids `token`. Naming a sensitive field is the manifest doing its job; carrying a
> value is the leak. The architecture gate therefore scans emitted manifests for the
> *shape* of a secret — PEM blocks, JWTs, `Password=` assignments, credentials in a URL,
> provider key prefixes. The writer test keeps its own name and its own job.

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
- [x] **WP-5** `FlowX.Compiler` — `FlowPlanGenerator`, model layer separate from emission.
      Diagnostics all raised (WP-13), B12 measured and passing (WP-14).
      The branching DSL is complete: `When`, `Switch`, `Parallel`, `ForEach` and
      `SubFlow` all ship (WP-15, WP-20, WP-24, WP-29, WP-33)
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
      only that one exists in this release. **The phase names here were wrong and are
      corrected:** the journal is a sink and arrives in **P2**; logs, traces and replay
      output arrive with observability in **P5**, not P3. So P2 creates a sink for
      sensitive values three phases before `RedactionCannotBeBypassed` can be written —
      WP-52 owns not journaling a marked member in the clear. **Discharged 2026-07-31:**
      the second sink exists and redacts by construction, so two of the four sinks are
      covered and the exit criterion's third is still P5
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

## 5c. P1 · Compiler hardening — **closed 2026-07-31, one criterion unmet**

Scope from [the roadmap](docs/20-Roadmap.md#3-increment-detail); work packages in
[PLAN.md §4](PLAN.md#4-p1--compiler-hardening).

**The three exit criteria, each re-run rather than remembered:**

| Criterion | Verdict |
|---|---|
| 200-flow solution builds with ≤ 8 % overhead | **FAIL at +67.1 %** [+61.9, +73.6]; 50 flows +46.5 %. **Accepted as an exception; the phase closed over it.** `FlowPlanGenerator` is 90.5 % of the marginal cost and would need an ~8× cut. [ADR-0014](docs/adr/ADR-0014-derived-error-catalogue-vs-build-budget.md) is the open decision and is still **Proposed** |
| every diagnostic passes `EveryDiagnosticIsHelpful` | **PASS** — `FlowX.Compiler.Tests.CompilerFitnessTests.EveryDiagnosticIsHelpful`, green in this working tree |
| emitted code is breakpoint-able | **PASS** — `FlowPlanGeneratorTests.EachStepGetsItsOwnLineDirective` plus five further line-directive tests across the emitter, `Fail` and step-input mapping, all green |

**`FlowTestHost` shipped at WP-49**, closing P0's other unshipped *Should* after three
documents had described a host that ran flows while `FlowX.Testing` contained only context
doubles. It runs the real engine over the real compiled plan in-process, with capabilities
substituted by capability id. `For<TFlow>()` is deliberately **not** offered: discovering
the generated dispatcher needs reflection over generated members, which constraint C2
forbids, so the documented shape was corrected rather than faked. `AwaitSignal` and
`AwaitCompletion` are unhandled, because both need a journal.

**Carried into P2, in three named piles** — five reserved diagnostics (`FLOWX1006`,
`FLOWX1007`–`FLOWX1009`, `FLOWX1012`, four of them blocked on *severity* and not on
analysis), three blocked fitness functions (`CrossTenantAccessIsDenied`,
`RedactionCannotBeBypassed`, `PluginsPassConformance`), and the build-overhead exception.
`dotnet new flowx`, unshipped since P0 and carried twice, **is being attempted in the
current round** — until it lands, `docs/19-SDK.md` and `docs/03 §12` still describe a
command that does not run. See
[§5d](#5d-p2--durable-execution--correct-against-a-real-database-and-unmeasured) and [PLAN §5](PLAN.md#5-p2--durable-execution).

- [x] **WP-15** The branching DSL — **`When` / `Otherwise` done** through builder, model,
      analysis, emission, graph and engine. A conditional compiles into the *same flat
      step array* as a linear flow, as a `Branch` plus a `Jump`, so the engine gained no
      branch stack and **both directions allocate 0 B** in Release. The manifest
      deliberately does **not** carry the predicate's source text: it would put business
      thresholds into a file whose rule is structure-only. `Parallel` followed at
      **WP-24**, `ForEach` at **WP-29** and `SubFlow` at **WP-33**, which closes the
      shape
- [x] **WP-20** `Switch` / `Case` / `Default` — a value branch through builder, model,
      analysis, emission, graph and engine. One `StepKind.Switch` carrying a target per
      case plus a default, and a `Jump` closing each case block, in the *same flat step
      array*; the dispatcher gained `int Select(...)` returning the matching arm, which
      is an `int` and not the value so nothing is boxed. **Every arm, the miss and an
      out-of-range arm allocate 0 B** in Release. A value matching nothing in a switch
      with no `Default` falls through, exactly as `When` without `Otherwise` does —
      recorded in `08 §3.2` and on `ISwitchBuilder`. The manifest carries neither the
      selector nor the case values, for the same reason WP-15 refused the predicate
- [x] **WP-24** `Parallel` / `Branch` / `MergeStrategy` — concurrent branches through
      builder, model, analysis, emission, graph and engine, plus **`FLOWX1013`**. The flat
      array survives: one `StepKind.Parallel` carrying a target per branch and a join, each
      branch a contiguous sub-range of the *same* step array, and **no closing jumps** —
      a branch's range ends where the next branch begins, so a jump would only restate the
      bound. Every target still points strictly forward and `StepGraph` still rejects one
      out of range, so termination is unchanged. What is no longer true is that "the loop
      index" describes execution: between a fork and its join there are several, on several
      threads, and the engine recurses once per fork into the same range-walking method.
      **`MergeStrategy` became a struct** so `Quorum(n)` can carry its number — the
      documented surface listed four strategies and an enum could name only three of them.
      All four are implemented. `AllSettled` publishes a `ParallelOutcome` at the join.
      **B2 is unchanged and still a hard zero** for the linear, conditional and switch
      paths; a fork allocates **792 B for three branches, 552 B for two** — about 240 B per
      branch — recorded as a ceiling, with a second test pinning that the cost tracks
      branches and not steps. Concurrent context writes are made safe by a lock taken
      **only when the plan contains a fork** (`ExecutionPlan.HasParallel`), not by a
      concurrent collection, which would have cost every linear flow an allocation per
      write. Cancelled siblings' completed work is still compensated, and every branch is
      drained before the fork returns — the context is pooled, so a branch outliving its
      flow would write into the next tenant's.
      **One caveat found in review, not by the package:** the race test that covers those
      concurrent writes does **not** fail when the lock is disabled. It was run eight times
      unguarded — including a variant forcing dictionary resizes — and passed every time.
      Branches genuinely do overlap (`BranchesOverlapWhenTheirStepsActuallyYield` proves
      `PeakConcurrency > 1`), but overlapping is not the same as colliding inside one
      dictionary operation. The lock stays — concurrent `Dictionary` mutation is unsafe by
      contract, and a race this hard to provoke reaches production instead of CI — but it
      is currently guarded by reasoning, not by a test that can fail
- [x] **WP-29** `ForEach` — a bounded body **re-entered per element**, the first shape
      where one range of the flat array executes more than once. The body appears once and
      the engine re-enters the span `[index+1, Target)`, so the plan, the manifest and the
      diagram stay independent of data size. **The per-iteration item is not in the state
      bag**: `IterationScope<TItem>` wraps the enclosing context and shadows `TryGet<T>`
      when `T` is the element type, via `Unsafe.As` so a struct element is not boxed per
      read; writes fall through, and scopes chain so nested loops each resolve their own
      element. **32 B per element and nothing per step** — asserted as a ceiling *and* as
      an exact tracks-elements-not-steps test. `CompensationStack`'s duplicate check moved
      from index to `(index, scope)`: keying on index alone threw on the second element,
      which would have made the documented per-line compensation example impossible to
      write. `MaxDegreeOfParallelism > 1` reuses **all** of `Parallel`'s machinery and adds
      only a sliding window; the bound is clamped against a constant rather than
      `ProcessorCount`, so a plan does not depend on the machine that built it.
      **The new cost gate fired on this merge** at +2.20 % against its +2.0 % threshold,
      with a 0.01 % spread — real signal. Re-recorded deliberately: `IStepDispatcher`
      gained two required members, so every flow emits two throwing stubs for iteration it
      does not use, exactly as it already does for `Evaluate` and `Select`. That is the
      gate working — a cost increase arriving as a reviewable diff instead of unseen.
      **Worth watching:** this is the fifth required member on that interface
- [x] **WP-33** `SubFlow` — composition, and the shape where **one array stops describing
      one execution**. The flat array itself survives untouched: a `StepKind.SubFlow`
      occupies one index, carries no target and moves nothing around it, so the graph
      validation and the termination proof for the parent are unchanged. What no longer
      holds is that the array in front of the loop contains every step that runs — the
      child has its own plan, dispatcher, pooled context and compensation stack, and the
      engine recurses into a second, independent execution. Splicing the child's steps in
      at compile time was rejected: it would make the parent's manifest claim the child's
      capabilities, discard the child's deadline and profile, and be impossible across an
      assembly boundary.
      **Two modes of three ship.** `Inline` and `Detached` are real; **`AwaitCompletion`
      is refused under every profile by `FLOWX1026`** — it needs a durable suspension
      point, and WP-52's journal is not one: a durable flow still runs to completion inside
      one invocation (suspension is WP-63). Unlike `AwaitSignal` it has no honest degenerate
      form (running it inline changes the parent's deadline and failure semantics;
      skipping it drops business logic). It is also unrepresentable in `StepNode`.
      **Compensation crosses the boundary upwards.** A child that succeeded is undone when
      the *parent* later fails — anything else would mean `FLOWX1005`'s own advice, to
      extract shared steps into a sub-flow, silently weakened the saga. Strict reverse
      survives: the parent records the composition as **one entry** in its own stack, so
      `A · child(X, Y) · B` unwinds `B, Y, X, A`, exactly as the steps would have inline.
      The child's context stays rented until the parent finishes, because the undo binds
      to what the child's steps produced.
      **`Detached` is drained.** It starts inside a step, so `FlowHost` never counted it;
      `DrainAsync` now waits on the engine's detached count as well, because a drain that
      reported success while a fire-and-forget saga was reserving inventory would leave it
      reserved. The child never touches the parent's pooled context — the mapping runs on
      the parent's thread *before* the child starts — so the cross-tenant hazard is
      structural rather than guarded.
      **0 B, and that is not "sub-flows are free".** Every part was made pooled or a
      struct on purpose; the first version walked the compensation stack to find retained
      children and cost **96 B per nesting level on the success path**, which is why the
      context now owns the list.
      **`FLOWX1021`** proves the DAG for every edge in one compilation and **cannot see
      across an assembly boundary** — its page says so, and `FlowEngine.MaxSubFlowDepth`
      bounds at run time what it cannot bound at build time.
      **Stated limit:** the child's result does not reach the parent's context. Both ways
      to pass it up were refused for reasons written down in `08 §3.7`
- [ ] **A test that can actually fail on the parallel context race.** Needs deterministic
      interleaving, not more iterations. Until then the lock above rests on the language
      contract alone
- [x] **A test that can actually fail on the parallel context race.** Closed by WP-34, and
      the diagnosis is the useful part: the window was not narrow, it did not exist. The old
      test wrote three keys through one reused engine, so from the second execution every
      `Set` was an *overwrite* — assigning an already-allocated slot cannot move an entry,
      relink a bucket or grow an array. Adding more keys did not help either, because the
      pooled `Dictionary` keeps its buckets across `Reset` (`Clear` does not release
      capacity), so those became overwrites after iteration 0. That is why the earlier
      twelve-extra-keys stress attempt also passed unguarded. Now: every branch **inserts**
      unseen keys, a fresh engine per iteration keeps them inserts, and branches rendezvous
      at a **spin** gate — a `Barrier` wakes participants microseconds apart, long enough
      for one branch to finish its whole loop first. Verified by mutation in review:
      **fails 4 of 4 unguarded, passes guarded**
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
- [ ] **⚠ REGRESSION — the generator has tripled since that measurement.**
      `FlowPlanGenerator` was 8.07 ms per flow at `a75c1f0`; it is now **≈ 29**. Measured
      twice independently — **28.7 ms/flow** at 200 flows by WP-27, **+29.70 ms/flow** at
      50 flows in review on a quiet machine (load 2.52), verdict **FAIL** — superseded by
      WP-43's **+67.1 %** at 200 flows and **+46.5 %** at 50, see below
      against the +8 % budget. The other two components reproduced to within 7 %, which is
      what makes the third reading believable. **It slipped in because the scale job is
      advisory** — correct while the measurement could not beat the noise, but the cost is
      now concrete: a 3× regression merged across four packages in silence. The job cannot
      just be made blocking while the criterion fails, so this wanted a *relative* gate
      against the committed figure instead of an absolute one against the budget —
      **built at WP-31, and it catches this one at fifty times its threshold**.
      **Bisected at WP-28 to one commit:** `c7ae70a`, WP-22's error-catalogue emission,
      **5.60 → 27.28 ms/flow (×4.9)**. `Switch`, `Parallel` and the sort-key fix cost
      nothing detectable; `FLOWX1011` never appeared in the generator's number, being an
      analyzer. Stubbing the catalogue read returns `dev` to 7.36 — the control. Of the
      reader's cost, **91 % was `GetTypeInfo`**, and **half of those binds were a duplicate
      question** asked once in a `DescendantNodes` predicate and again in the `Where` after
      it. Fixed: **25.4 → 20.4 ms/flow**, faster in 6 of 6 paired rounds, manifest
      byte-identical. **The remaining ~80 % is what the feature costs** — deriving `errors`
      from code binds every capability body, so generator cost now tracks how much
      capability *implementation* exists rather than how many flows do. Whether that
      catalogue is worth two thirds of the compile-time budget is a product decision
- [x] **WP-31** A **relative** cost gate, blocking, on a **deterministic proxy** — bytes
      allocated by one `RunGeneratorsAndUpdateCompilation` call, against a committed
      baseline, threshold **+2 %**. **Validated against `c7ae70a`: FAIL at +102 %**, fifty
      times the threshold, on the commit that caused the incident. **Wall clock provably
      cannot do this**: gating the same probe on elapsed time, a no-op commit produces a
      false signal of up to **+166 %** while the real 4.9× regression produces **+39 – 77 %**
      — a threshold wide enough not to fire on nothing is 2–4× too wide to fire on the
      incident, and more rounds cannot fix a signal smaller than its noise. Confirmed in
      review under **load average 38.6**, where the gated metric moved **+0.01 %**. The
      absolute criterion is reprinted as `ABSOLUTE CRITERION — FAIL` on every run, passing
      ones included, so a green relative gate cannot be read as a met budget
- [ ] **WP-27** cut `StepBindingAnalyzer` 89 % — 4.70 → 0.53 ms per flow — by binding a
      step's type argument outside the `Define` body. 96 % of its cost was one
      `GetSymbolInfo` call: a node inside a statement cannot be bound without binding the
      whole chain, so the *first* `.Step<T>()` of a flow cost 4.13 ms and every later one
      0.06 ms. All 800 resolved symbols identical before and after. It does **not** close
      the criterion and is not claimed to — end to end the difference sits inside the
      noise. What it buys is the IDE, where the rule re-runs per keystroke
- [~] **WP-43** Re-measured P1's exit criterion. **FAIL at +67.1 %** [+61.9, +73.6] at 200
      flows and **+46.5 %** at 50, against +8 %, on a quiet machine with a **10.6 % A/A
      noise floor**. The first attempt returned **INCONCLUSIVE** under load 6.8–39, with the
      A/A control reporting ±77 % and identical builds swinging four-fold — the exit code
      firing legitimately, and no verdict manufactured from it.
      **The attribution has moved decisively:** `FlowPlanGenerator` is **90.5 %** of the
      marginal cost; `StepBindingAnalyzer`, once 37 %, is **1.2 %** — WP-27's 89 % cut is
      visible in the split and invisible in the criterion, exactly as predicted.
      **The number that ends the profiling conversation: optimising all eight other
      components perfectly still leaves the criterion failing by 54 points.** Reaching +8 %
      needs the generator at ~2.8 ms/flow against today's 23.25 — an ~8× cut, where the bulk
      is what the derived error catalogue costs. That is ADR-0014's question.
      Honest about attribution: 50 flows reproduces the previous figure to **0.1 points**
      across a dozen merged changes, so the saving and the features cancelled; the 200-flow
      improvement was **not bisected**, and the earlier runs were on a 2.80 GHz CPU where
      this container reports 2.10.
      **Box stays `[~]`, and it stays `[~]` permanently.** The phase closed over this on
      2026-07-31 by decision, not by measurement changing. Ticking it because P1 is closed
      would make this file say the budget is met, which is the one thing it must not say.
      Risk **R1's trigger has fired and its named action — freeze features, invest in the
      generator's model layer — was not taken**; what was done instead is WP-31's relative
      gate, which stops it worsening and does not close it
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
      statics are a list; nothing is interprocedural, and the page says so. Scope was
      `When` only until WP-25 widened it to every context delegate — see below
- [x] **`.Step<TCapability, TStepIn>(map)` is parsed and then ignored** by `FlowAnalyzer`
      and `FlowEmitter`. It is on the builder surface and `FLOWX1020` recommends it as
      the fix for a binding failure, so a user following the diagnostic reaches an
      overload that silently does nothing. Worse than not existing.
      Closed by WP-41. The mapping is modelled, emitted as a cached static
      `Func<FlowContext<TIn>, TStepIn>` and called at the step; its result is the step's
      input and is **not** written into the state bag — the bag is keyed on `typeof(T)`
      and a mapping exists precisely because nothing put a `TStepIn` there, so storing one
      would invent a producer `FLOWX1020` cannot see and would make two mapped steps of
      the same contract overwrite each other. A compensation on a mapped step re-runs the
      mapping, which is sound because it is pure by `FLOWX1011` and is the same guarantee
      an unmapped compensation has. 0 B on the mapped path. `FLOWX1020`'s silence on this
      overload is now justified rather than self-defeating, and `FLOWX1028` was added
      because C# constrains `TStepIn` to nothing — a mapping the capability cannot accept
      used to be a `CS1503` inside generated source
- [x] **Triggers and capability `errors` are in the manifest schema and never emitted.**
      Closed by WP-22. Both are emitted, under a **three-state rule**: a resolved
      catalogue, a resolved-and-empty one (`[]` — "declares no errors"), or **withheld
      entirely** when it could not be resolved. A catalogue short by one entry reads
      exactly like a complete one, so an unresolvable case has to be visibly absent
      rather than quietly approximated
- [ ] **⚠ The three-state rule has a fourth state nobody designed: positively wrong.**
      Found by WP-36. The rule above assumes the reader either resolves a catalogue or
      knows it could not. There is a third outcome: **it finds nothing, finds nothing it
      *could not* follow, and publishes `errors: []`** — which the schema defines as the
      positive claim *"this capability returns no declared error"*. It happens whenever a
      failure stays inside `Result<T>` for its whole journey and never takes the shape of
      an `Error`, and it is reachable through the **first-party**
      `Result.Fail<T>(code, message, category)` overload — whose sibling
      `Fail<T>(Error)` resolves correctly. Same intent, one publishes the truth and the
      other a confident falsehood. Also hit by a one-line capability delegating to a
      service that returns `Result<T>`, which is a very common shape. **`flowx diff`
      treats the field as authoritative**, so a wrong catalogue is worse than a slow
      build — and it contradicts ADR-0014's stated premise that a *derived* list cannot
      be wrong where a declared one can. Fixing it moves the measured corpus from 39 % to
      47 % withheld: **correctness costs coverage, and someone has to choose**
- [ ] **`07-Capability-Model` §4 prescribes a layout the reader cannot follow.** It
      mandates the static error class in the same breath as putting contracts in a
      dedicated assembly — across an assembly boundary, which is exactly where the scan
      stops. A team following the documentation exactly gets **no catalogue at all**.
      `samples/ecommerce` misses this only because it is a single project

**Closed this round, and one opened.**

- [x] **The false-complete catalogue.** Fixed by WP-37 by changing the question: the scan
      roots at `ICapability<,>.ExecuteAsync` and follows the *value*, and `Result.Ok` is the
      only expression that entitles a capability to `errors: []`. Anything the resolver does
      not understand marks the catalogue incomplete — that is the safety property.
      Reachability came free. Better than B13 forecast: **wrong 4 → 0**, correct **47 % →
      55 %**, withheld only **39 % → 42 %**, because two under-reports became *correct*
      catalogues rather than withholds. The load-bearing assumption is now written where it
      can be checked: `Result<T>` is enterable only from `T` and from `Error`
- [x] **`.Fail(Error)` parsed and ignored** — fixed by WP-38. It compiles to a
      `StepKind.Fail` whose dispatcher returns `StepOutcome.Failed`, so the engine needed no
      change. Completed compensable steps unwind, because the documented workaround while it
      was unimplemented was to spell the rejection as a capability returning `Result.Fail`,
      and a feature that replaced that workaround without unwinding would silently weaken
      every saga that took the advice
- [x] **`ctx.Input` did not compile** — fixed by WP-38, and the root cause was deeper than
      the emitter: **`FlowContext<TIn>` was an abstract class nothing derived from**. There
      was no such object at run time and there could not be, since the pooled context is
      shared by every flow. It is now a `readonly struct` view over whatever context is
      current, which is what makes it work inside a `ForEach` body and a sub-flow where a
      cast would have thrown. One reference wide, so B2 stays a hard zero
- [x] **⚠ `FlowX.Runtime` never reads `ExecutionProfile`** — **closed 2026-07-31 by WP-52.**
      The runtime reads the profile; a `Durable` flow journals one row per
      `(instance, scope, step, attempt)` and resumes through the same step loop;
      `ExecutionProfileHonestyTests` was observed failing and is deleted; `FLOWX1028` is
      narrowed to `Streaming`; the warning boxes in `06 §4`, `06 §5`, `11`, ADR-0003 and
      ADR-0006 are corrected at WP-54. **Risk R2 went from unreachable to live and
      unmitigated** — its analyzers (WP-58) and its replay test (WP-61) did not land with
      it. **What this box did *not* buy:** no lease is acquired, no recovery scan exists, no
      store implements `IFlowJournal` outside an in-memory reference, and a `Durable` flow
      with no journal wired is now *refused* rather than silently run ephemerally. The
      original entry follows, unedited.
      Found by WP-40 while auditing
      risk R2. A `Durable` flow runs the ephemeral path; the profile affects only a plan
      validation and a manifest field. So R2 is not *mitigated* — it is **unreachable**, and
      it goes live the moment P2 lands. This also explains why several determinism
      diagnostics are blocked on severity rather than analysis.
      **Still open, and deliberately: WP-42 made it loud, not fixed.** Durability is a
      phase, not a package. What shipped is `FLOWX1028` — a warning on any flow declaring a
      profile the runtime does not implement — so the platform no longer accepts a
      declaration it does not honour in silence. Warning rather than error because the only
      repair for an error is `Profile = Ephemeral`, which deletes the design decision P2
      must find, and because `FLOWX1017` is an error on the opposite condition: two errors
      would leave a suspending flow with no profile it could legally declare. The manifest
      still publishes `"profile": "Durable"`, which is the declaration faithfully recorded;
      the untruth was the silence around it, not the field. `RuntimeDoesNotReadTheExecutionProfile`
      in `FlowX.Architecture.Tests` fails on the day the runtime reads a profile, so the
      scaffold gets taken down rather than left to rot. This box is ticked by P2 —
      specifically by **WP-52**, whose design is
      [ADR-0015](docs/adr/ADR-0015-journal-schema-and-durable-execution.md) and whose exit
      criterion is that every row of that record's take-down table is discharged in the same
      package: the fitness test **deleted** rather than skipped, `FLOWX1028` narrowed to
      `Streaming`, and the warning boxes in `06 §4`, `06 §5`, `11`, ADR-0003 and ADR-0006
      corrected. The same package unblocks four of the five reserved diagnostics, which is
      an argument for landing the seam early in P2 rather than after the store adapters
- [ ] **The cost gate measured a subject that did not compile** — the probe parsed a project
      with `ImplicitUsings=enable` without supplying them, so `ValueTask` and friends never
      bound. Harmless for relative comparisons of syntax-matching code, which is why it still
      caught the 4.9× regression; not harmless for anything that resolves a signature. Fixed
      and the baseline re-recorded. **Left open as a reminder**: a benchmark's subject needs
      a gate of its own, and this one had none

**What building `dotnet new flowx` found out about the platform.** A template is the
first thing a new user runs, so it is also the cheapest test of our own ergonomics.
Eleven findings; these are the ones that are ours to fix:

- [x] **The repository could not produce its own packages.** `dotnet pack` failed on both
      analyzer projects with `NU5017` — `IncludeSymbols` is set repo-wide, those two ship
      only an analyzer asset, so the symbols package has no content and pack exits 1
      *after* writing the `.nupkg` correctly. Nothing here ran `pack` until a template
      needed a local feed, so it would have gone undiscovered until the first release.
      Fixed; the solution now packs nine packages, exit 0
- [ ] **`Program.cs` restates the flow — the highest-value missing piece.** `AddFlowX`
      registers no transport and generates no endpoint, so a generated project
      hand-writes the method and route `[HttpTrigger]` already declares, plus `Plan`,
      `Dispatcher`, `Projection`, `SensitiveMembers` and two `JsonTypeInfo`s. Ten lines
      every consumer writes identically for every flow, and the one place a generated
      project can silently drift from its own flow. `19-SDK §6`'s own standard is that
      ceremony every user pays is a platform defect
- [ ] **`FLOWX1028`'s Warning is defeated by warnings-as-errors.** Its page argues at
      length that an Error would be *actively harmful* — and this repository mandates
      `TreatWarningsAsErrors`, so any consumer at the platform's own bar who declares
      `Durable` gets a hard error anyway. Same shape for `FLOWX1024`. Either they become
      `Info` or the platform publishes a `WarningsNotAsErrors` recommendation
- [ ] **`EmitCompilerGeneratedFiles` is documented as on by default and is not.** It is on
      only via this repo's `Directory.Build.props`, so a consumer writing their own
      `.csproj` silently loses the ADR-0002 / risk-R1 mitigation. It belongs in
      `FlowX.Compiler.props`, which every package consumer imports
- [ ] **`FLOWX1010` fires second and in the wrong file.** Omitting `Authorization`
      produces `CS9035` first, with no help link; `FLOWX1010` then fires at the *flow's*
      `.Step<T>()` type argument rather than at the `[Capability]` attribute, so its
      documented quick action is offered somewhere other than the edit it describes
- [ ] **No fitness function sees `templates/`.** `SourceSurvey.ShippingTrees` covers
      `src`, `plugins` and `samples`. The stated reason for including samples — a
      permissive declaration there is one people copy — applies harder to a template,
      which is copied by definition

**Gaps WP-20 and WP-22 surfaced in turn.** Same class again — declared, documented or
reachable, and enforced or honoured by nothing:

- [x] **`.Fail(Error)` was parsed and then ignored.** It was on `IFlowBuilder`, it was in
      the `08 §4` method table, and `FlowAnalyzer` did not model it — so a block whose
      only call was `.Fail(...)` compiled to an **empty** block, and `08 §3.2`'s own
      `Switch` example, which uses `.Default(b => b.Fail(...))` to reject an unsupported
      channel, fell through and accepted it. Closed by WP-38. `Fail` is a terminal step:
      the error is a `static readonly Error` in the generated dispatcher, delivered
      through the same `ExecuteAsync` a declined payment comes back on, so **the completed
      compensable steps unwind exactly as they would on a capability failure** — anything
      else would mean the arm that rejects a request leaves the stock it reserved. The
      error's *value* never reaches `flowx.manifest.json`, which publishes
      `"kind": "Fail"` and nothing more; steps written after one are unreachable and
      reported as `FLOWX1027`
- [x] **`FLOWX1013` — parallel branch disjointness.** Closed by WP-24:
      `ParallelSlotAnalyzer` reports two branches of a fork whose capabilities declare the
      same output contract, which is the slot the generated dispatcher writes. Its limit is
      stated on the page and worth repeating here, because a green build reads as a proof
      and is not one: it compares **declared contracts**, so a capability calling
      `ctx.Set<T>()` from inside its own body writes a slot the rule never sees
- [x] **`FLOWX1011` did not cover `Switch` selectors.** Closed by WP-25. The analyzer is
      now driven by a table of every `IFlowBuilder` method taking a
      `Func<FlowContext<TIn>, …>` — `When`, `Switch`, `ForEach`, `Return`, `Emit`,
      `EmitOnFailure`, `Step<TCapability, TStepIn>` and `SubFlow` — and the message names
      the construct it found, because a `Return` projection reported as "the condition" is
      a diagnostic a reader stops believing. The next DSL shape is a row in that table
      rather than a second code path, which is the mistake WP-21 made once and this
      package exists to undo. Three of the eight are not yet executed by the emitter and
      are checked anyway; the page says which
- [x] **An unrecognised `TriggerAttribute` subclass is skipped in silence.** A trigger's
      `Kind` is an overridden property — executable code, not attribute data — so a
      third-party transport plugin's trigger cannot be read from metadata. WP-22 declined
      to guess, which is right, but the skip produced only an absent `triggers` array —
      which `flowx diff` cannot tell apart from a flow that declares no trigger, so the
      gate that calls a removed trigger breaking lost its input without saying so. Closed
      by `FLOWX1025` (WP-26), a warning: the manifest still refuses to guess, and the
      refusal is now audible. ~~**What remains open is the cause** — the abstractions give a
      plugin author no way to declare a kind the compiler can read, so the only fix
      offered is "use a built-in attribute instead"~~ **The cause is now closed.**
      `[TriggerKind(TriggerKind.Bus)]` carries the kind as an enum constructor argument,
      which survives to metadata where an overridden property does not, and `TriggerReader`
      walks the base chain because Roslyn does not honour `Inherited = true` for
      `ISymbol.GetAttributes`. Severity follows who can apply the fix: Error when the
      attribute is declared in the compilation being built, Warning when it arrives as a
      reference. Pulled forward from P3's WP-70 because it is an abstraction change and
      three plugins were about to be written against the old shape.
      **One hole is stated rather than buried:** for an attribute arriving from metadata,
      nothing checks that the marker and the `Kind` property agree — reading `Kind` means
      running a getter, and a generator does not run what it compiles.
      *The rest of WP-70 — `TriggerSourceConformance`, `ITriggerSource`, the published
      package — has not shipped, and `PluginsPassConformance` is still blocked (§4).*
- [x] **Nothing in the repository had ever compiled generator output.** The generator
      harness discarded the updated compilation, so every test asserted against *parsed*
      text — which catches a syntax error but not an unresolved name, a wrong delegate
      type argument, or an unimplemented interface member. Fixed by WP-20's
      `GeneratedCompileErrorsIn`, and a real compile is now asserted
- [x] **`ctx.Input` did not compile in any predicate or projection.** Found by WP-25,
      confirmed against a real build, closed by WP-38. The DSL signature is
      `Func<FlowContext<TIn>, …>`, but `FlowEmitter` wrote every emitted delegate as
      `Func<FlowContext, …>` — the **non-generic base**, on which `Input` is not
      declared — so the lambda source was copied into a field whose parameter type had
      lost the member and the build failed with **CS1061**, in generated code, against
      three documents that all said it worked. The cause was that `FlowContext<TIn>` was
      an abstract class **nothing anywhere derived from**: the engine's context is pooled
      and shared by every flow, so no such object could exist. It is now a `readonly
      struct` view over any `FlowContext`, which costs a register, allocates nothing, and
      works inside a `ForEach` body and a sub-flow where a cast would have thrown

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
| `.Emit<T>()` publishes nothing | Silent. Now **FLOWX1024**, the first warning in the set — since joined by `FLOWX1011` and `FLOWX1025` |
| Allocation budgets measured 376 B in Debug | Compiler scaffolding, not the engine. CI runs Release and never saw it; every contributor did. Now skipped in Debug, with the reason |
| The walker read `ArgumentNullException.ThrowIfNull(flow)` as a chain | A statement *after* the chain would have silently replaced it. The walk is now rooted at the builder parameter |

---

## 5d. P2 · Durable execution — **correct against a real database, and unmeasured**

Work packages in [PLAN.md §5](PLAN.md#5-p2--durable-execution); the design they are held
to is [ADR-0015](docs/adr/ADR-0015-journal-schema-and-durable-execution.md), **Accepted at
WP-53** and [amended by ADR-0016](docs/adr/ADR-0016-postgres-journal-adapter.md). It is
listed in full because P1 handed each item over with a named blocker, and an inventory that
exists only in a closing summary is one nobody reads.

> **Where durability actually is, in one paragraph, because the rest of this file depends
> on it.** `FlowX.Runtime` reads `ExecutionProfile` (WP-52). A `Durable` flow commits one
> journal row per `(instance, scope, step, attempt)`, captures `ctx.UtcNow`, `ctx.NewId()`
> and `Random`'s seed per step, gives a composed sub-flow its own instance row, and resumes
> by replaying its committed rows into the *same* step loop. A host acquires a lease,
> renews it in the background at TTL/3, and runs a recovery scan that claims instances
> whose lease expired (WP-55); a node that loses its lease stops **without compensating**,
> because the work belongs to another node now. `plugins/FlowX.Postgres` is a real store
> (WP-53), and the conformance suite passes against PostgreSQL 16.13 unmodified, from a
> different assembly. **The outbox, `AwaitSignal` and Redis are not built**, and neither
> durability budget has been measured. A reader must not conclude durability works end to
> end — but "nothing has run against a store", which this section said until 2026-07-31, is
> no longer one of the reasons why.

> **ADR-0015 is Accepted, and the caveat matters more than the status.** Its condition was
> that the conformance suite hold a *real* implementation to the schema — not the in-memory
> dictionary that could not disagree with a transaction boundary, an index, a unique
> constraint or a migration. WP-53 supplied one, and **all five Decision commitments held**.
> Three clauses did not: `jsonb` reorders object keys and so breaks commitment 5 outright
> (payload columns are `json`); `flow_lease.instance_id` as `PK,FK` is inverted in time,
> because the lease is taken before the instance row exists; and the state-bag snapshot
> ADR-0015 names as B8's mitigation had no column saying which commit it came from. Two of
> the three were in an ERD ADR-0015 already declared superseded; the third was the record
> contradicting itself. **B7 and B8 are unreported rather than passed** — WP-50 has not
> started — so a journal has been made correct without being made fast, and that is what
> Accepted does and does not mean here.

**Roadmap Must:**

- [ ] **WP-50** B7, B8 and the QR2 chaos rig — **before** the journal. `JournalBenchmarks`
      does not exist and neither does a chaos rig; both are named in P2's Must as outcomes
      and by nothing as tooling. This is B12's lesson applied on time rather than late.
      **It was not applied on time.** WP-51 landed first, so the baseline WP-53 is to be
      judged against still does not exist and will be written by someone who already knows
      what the journal looks like. It must land before **WP-53**, which is a weaker claim
      than the plan made. **WP-52 has now landed too, so the inversion is complete:** the
      journal has a step-commit path and nothing measures B7 or B8 against it. The two
      entries this package was to remove *first* — `JournalBenchmarks` in `14 §8` and the
      chaos row in `21 §8` — are now the **last** entries left, and their stated reason
      ("there is no journal") has stopped being true. *(The attribution-guard and DAST repairs were credited to WP-50
      in `docs/21`; they were not this package's work and the credit is withdrawn.)*
- [x] **WP-51** `IFlowJournal`, `ILeaseStore` and the shared conformance suite ADR-0006
      promises. **Shipped, with two deviations from its own row, both recorded rather than
      absorbed.** The contracts are in `src/FlowX.Abstractions/Durability/` — where
      ADR-0009 requires them, and not where `docs/05 §5.3` drew them; that document has
      been corrected. `FenceAsync` was added to `IFlowJournal` to close a gap ADR-0015
      left: the ADR never says *when* the fence rises, and a journal that learned tokens
      only from writes accepts a stale token during the window between acquisition and the
      new owner's first commit. Pinned by
      `JournalConformance.TheFenceRisesOnAcquisitionNotOnTheFirstWrite`; the ADR should be
      amended when next opened.
      **The suite is two of six and is not packable.** `TriggerSourceConformance`,
      `PublisherConformance`, `SerializerConformance` and `PolicyHandlerConformance` are
      unwritten. The shape exists and is proved to reject a wrong store by name
      (`TheSuiteRejectsAStoreThatIsWrongTests`); nothing real has met it. It packs at
      WP-53/WP-54, when a second and third store exist to push back on it
- [x] **WP-52** The seam — **`FlowX.Runtime` reads `ExecutionProfile`**. **Shipped
      2026-07-31.** `RuntimeDoesNotReadTheExecutionProfile` was observed failing and
      `ExecutionProfileHonestyTests.cs` is deleted, not skipped; `FLOWX1028` is narrowed to
      `Streaming`. B2 re-measured at **0 B** on the ephemeral path, and `Durable` costs
      192 B per step, recorded as a ceiling. A `[Sensitive]` member reaches the journal
      redacted, proved by reading all six stored strings back — structural, not remembered,
      because `JournalPayload.ToJson()` is the only exit. **Three limits recorded rather
      than absorbed:** non-determinism attribution inside a `Parallel` is best-effort (one
      pooled context is shared by the branches, so a sibling's id can land on the wrong row
      — harmless while nothing replays a capture; WP-61 needs a per-branch context); a
      skipped sub-flow's compensations are **not** rebuilt on resume, because the parent's
      entry binds to the child's context and that died with the node (WP-57); and ADR-0015
      was amended in two places its own first implementation found wrong. The take-down list
      is worked row by row in WP-54's commits
- [~] **WP-53** Postgres journal + lease adapter; B7 and B8 reported with an explicit
      verdict. **The adapter shipped 2026-07-31; the verdict did not, and the row stays
      `[~]` for exactly that half.** `plugins/FlowX.Postgres` implements `IFlowJournal`,
      `ILeaseStore`, migrations and retention, depending on `FlowX.Abstractions` and nothing
      else. The WP-51 suite was inherited **unmodified from a different assembly** — the
      arrangement `17 §5` describes for a third party claiming conformance — and 45
      conformance assertions plus 18 adapter tests are green against PostgreSQL 16.13.
      **Three ADR-0015 clauses failed contact** and are amended in
      [ADR-0016](docs/adr/ADR-0016-postgres-journal-adapter.md): payload columns are `json`
      because `jsonb` reorders keys and breaks commitment 5; `flow_lease` carries no foreign
      key to `flow_instance`, because the lease precedes the instance row; and
      `flow_instance.state_bag_sequence` was added in migration `0002` to give B8's
      mitigation the position nothing had given it. Four further things the record does not
      say are documented rather than absorbed — `JournalStep.Sequence` needs an
      instance-local column, the lease row must be `UPDATE`d and never `DELETE`d or the
      token counter resets under a returning zombie, `duration_ms` overflows as `int` at
      24.8 days, and `CompleteAsync` on an already-terminal instance is a **contract gap**
      the suite does not specify. **B7 and B8 are unreported**, because WP-50 has not
      started; the exit criterion asks for an explicit pass or fail and the honest answer is
      neither yet. Skip behaviour is deliberately three-way: no connection string → every
      adapter case skips with a reason; a connection string and no server → **59 failures,
      0 skips**, because a skip would report the suite green against a database never
      reached; and three always-on tests gate the skip logic itself.
      **Three gaps found after the merge, by reading the adapter against the documents
      rather than by a test.** One is closed, two stand.
      *Closed:* the adapter implemented **no `IRecoveryIndex`**, so a Postgres-backed host
      resolved the scan's query to `null` and silently swept nothing — it fenced correctly
      and picked up no dead node's work, which made P2's Done-when unreachable.
      `PostgresRecoveryIndex` closes it; see the entry below.
      *Standing:* `state_bag_sequence` is written and **never read** — the frontier query is
      `WHERE instance_id = @instance ORDER BY sequence` with no lower bound, so B8's
      mitigation is a column and not yet a shorter scan; and the deliverable row's
      **group-commit batching and `tenant_id` partition key are not built**, which is
      defensible only because WP-50's absent numbers are the sole rational basis for either
- [x] **`IRecoveryIndex` for Postgres** — the class that connects WP-53's store to WP-55's
      scan. Recorded as its own line because it belongs to neither package: both shipped
      complete against their own exit criteria, and the gap was *between* them. A separate
      class rather than a second interface on `PostgresFlowJournal`, because a scan is not
      part of executing an instance and the type every durable write passes through should
      not carry a member no write uses ([ADR-0016 decision 4](docs/adr/ADR-0016-postgres-journal-adapter.md)).
      **Migration `0003` adds the index the query needs, and `0002`'s was the wrong shape:**
      with `state` leading, `ORDER BY updated_at` inherits no ordering — 1 748 buffers and a
      top-N sort on 200 000 rows, against 4 with `(updated_at)` partial. `0002`'s index is
      left in place, because superseded is not unused. The plan is **asserted, not assumed**:
      a test EXPLAINs the statement read from the class rather than transcribed, and fails on
      `Seq Scan` or `Sort`. 19 tests, including a real death and recovery over real stores.
      **Two gaps named and not closed:** there is no `RecoveryIndexConformance`, so which
      states count as abandoned is agreed between the two implementations by reading rather
      than by an assertion; and `AbandonedInstanceQuery.TenantId` is a filter, not a second
      index, because nothing sets the parameter yet
- [ ] **WP-54** Redis lease store — concurrent with WP-53, same suite unmodified. **Now the
      only demonstration left of the split-store arrangement** `ILeaseStore`'s remarks
      describe: a Redis lease store and a Postgres journal sharing no transaction. WP-53
      made that arrangement *possible* — the lease has no foreign key into the journal's
      instance table, which is what a shared-nothing pair requires — and *unproved*
- [x] **WP-55** Resume: lease acquisition, recovery scan, re-entry into the same step loop.
      **Shipped 2026-07-31.** `DurableLease` acquires and renews in the background at TTL/3;
      `LeasePolicy` carries the TTL and renewal stance. `FlowRecoveryScan` and
      `FlowRecoveryService` find instances whose lease has expired and hand each to the same
      `ExecuteAsync`, so there is still no second recovery code path to rot. **A fenced-out
      node stops without compensating** (`CompensationOutcome.Abandoned`) — the alternative,
      compensating work another node now owns, is a double-undo, and the decision is
      recorded rather than inferred from the code. This discharges the WP-52 consequence
      that a `Durable` flow was "rejected at its first invocation unless the caller builds
      the session": a host wires it now
- [ ] **WP-56** Transactional outbox and publisher. Retires **`FLOWX1024`**, the warning
      that says `.Emit<T>()` publishes nothing
- [ ] **WP-57** Compensation with its own policies. **Has a dependency the roadmap does not
      show:** no policy executes at run time; the policy engine is P4
- [x] **WP-58** `FLOWX1007`–`FLOWX1009`, with the determinism severity stance re-decided
      **as a set**, `FLOWX1011`'s deliberate deviation included. **Shipped 2026-07-31.**
      `DeterminismAnalyzer` raises all three: ambient time (1007), ambient identifiers and
      randomness (1008), and mutable state at the declaration site (1009). **The stance was
      re-decided rather than inherited, and `Info` was rejected outright** — Info never
      reaches a build log and `Ephemeral` is the *default* profile, so the informational
      severity ADR-0003 originally specified is precisely what kept all four ids unraised
      through two phases. They ship **Warning by default, and Error where the compilation
      can prove the code is on a durable flow's replay path**; a capability has no profile
      of its own, so escalation is a proof — a `Durable` flow *in this compilation* naming
      it as a step — rather than a guess. `FLOWX1011`'s deviation stops being an exception
      and becomes the rule. ADR-0003's bullet is amended to say so
- [ ] **WP-59** `FLOWX1006` and the generated STJ payload context. **Now the only
      capability rule left unenforced** — WP-58 built the other three, and
      `ICapability`'s doc comment says so
- [ ] **WP-60** `FLOWX1012` — the check was always easy, and its *fix* became true at
      WP-52. **The blocker is discharged; the rule is not written.** Left unticked, because
      an unblocked rule is not a raised one. WP-58 discharged the *severity* half of the
      same blocker for its own three ids and shipped them; this one did not follow
- [ ] **WP-61** `ReplayDeterminismTest` and its corpus. Risk **R2**'s actual mitigation,
      currently cited in `05 §11` as though it existed
- [ ] **WP-62** QR2 — 10 000 flows, `SIGKILL` at every step boundary, zero duplicate
      non-idempotent effects, zero lost instances, resume p99 ≤ 45 s. P2's Done-when

**Roadmap Should:**

- [ ] **WP-63** `AwaitSignal`, `Delay`, timers. Un-blocks `SubFlowMode.AwaitCompletion`
      and makes `FLOWX1017`'s existing code fix buy something
- [ ] **WP-64** `flowx replay --mode inspect`. Its exit criterion collides with the green
      fitness function `CliDependsOnNothingButTheManifest`, which is stated in the plan
      rather than left to be discovered by whoever writes it

**Fitness functions P2 changes, and one it does not:**

- [x] `RuntimeDoesNotReadTheExecutionProfile` — **deleted** at WP-52, not skipped.
      Observed failing first; `ExecutionProfileHonestyTests.cs` no longer exists
- [ ] `ReplayDeterminismTest` — created at WP-61
- [~] `CrossTenantAccessIsDenied` — lost **half** its blocker at WP-52 (the missing
      journal) and stays blocked on P4's policy execution and, for "every trigger kind",
      P3's second transport. It did not become green, and the row in §4 says so

---

## 5e. P3 · Transport breadth — **one package, out of phase and unnumbered**

Nothing in [PLAN §6](PLAN.md#6-p3--transport-breadth)'s table has started. One package
adjacent to it shipped early and is recorded here rather than left to be rediscovered.

- [x] **Endpoint generation** — a flow that declares an HTTP route no longer needs a
      hand-written `MapPost`. `FlowXEndpoints.g.cs` is emitted into the *user's* assembly
      and registers every routed flow from one call; `samples/ecommerce/Program.cs` drops
      from 12 lines of registration to 2. **`RuntimeDoesNotReferenceAnyPlugin` stays green**
      because the compiler emits the string `"FlowX.Http.FlowEndpointExtensions"` and
      resolves it through `Compilation.GetTypeByMetadataName` — it knows the transport's
      name, not its assembly. Manifest output is byte-identical; the generator cost gate
      reports −0.50 % allocations and −0.97 % elapsed. **Deliberately unnumbered:** it was
      executed as "WP-74", which `PLAN.md` reserves for Azure Service Bus — the second
      work-package number collision this project has had, and the first to happen *after*
      the warning against it was written
- [x] **A silent staleness bug in `templates/local-feed.sh`**, found by the same package
      rather than by a test. NuGet caches by id **and version**, so a rebuild at an
      unchanged version left `verify.sh` restoring the previous run's assemblies — the
      template verification was passing against stale output. Fixed, and `verify.sh` now
      carries the check
- [ ] **WP-70** through **WP-76** — not started. The abstraction half of WP-70
      (`[TriggerKind]`) shipped in P1; `PluginsPassConformance` is still blocked on the rest

---

## 5f. The vision's success criteria · current state

[PLAN §1.1](PLAN.md#11-what-this-plan-is-held-to) carries the static mapping — which package
or phase each criterion is owed to. This carries the state. **P9 closes when all eight are
met *and gated in CI*** ([20-Roadmap](docs/20-Roadmap.md)), so "satisfied" and "closed" are
different columns on purpose.

*Neither this file nor the plan mentioned `V1`–`V8` before 2026-07-31. The criteria that
define whether the project succeeded were tracked nowhere in the two documents that track
everything else.*

| # | Criterion | Satisfied? | Gated by a check that can fail? |
|---|---|---|---|
| **V1** | ≤ 3 files, ≤ 60 lines for a 4-step flow | **yes** | **no** — a review. Endpoint generation cut the sample from 12 lines to 2 and no assertion noticed the number move |
| **V2** | HTTP → Kafka, zero logic edits | **no** — one transport | no — WP-71 writes the assertion |
| **V3** | p99 ≤ 5 µs, ≤ 1 alloc/step | **yes** — 172.3 ns / 0 B | **yes.** The only one of the eight |
| **V4** | durable checkpoint p99 ≤ 15 ms @ 5 000 flows/s | **unknown** — a journal exists since WP-53; nothing times it | no — WP-50 |
| **V5** | cold start ≤ 200 ms, NativeAOT | **unknown** — the binary links and serves; nothing times it | no — P9 |
| **V6** | build overhead ≤ 8 % | **no** — +67.1 % | **no, and deliberately.** The job that measures it is advisory by an ADR-0014 commitment; the blocking gate is relative |
| **V7** | 100 % of flows, capabilities, **policies and events** in the manifest | **partly** — flows and capabilities yes; policies and events neither exist nor are checked | partly — `ManifestIsComplete` covers the half that exists |
| **V8** | mid-level engineer ships a flow in ≤ 2 h, n ≥ 10 | **not run** | no — P9 |

**One of eight is gated.** Three more are satisfied or partly satisfied and enforced by
nothing, which is the state that decays silently — V1 already moved without anything
noticing.

---

## 6. Quality gates · current state

| Gate | Target | Now | Source |
|---|---|---|---|
| Compiler warnings | 0 | **0** ✅ | verified locally |
| Blocker/critical Sonar issues | 0 | **not running** | WP-0 |
| Line coverage | ≥ 80 % | **83.9 %** — *not enforced* | measured 2026-07-31 over `src/` + `plugins/`, live Postgres |
| Branch coverage | ≥ 75 % | **77.6 %** — *not enforced* | same run; no CI step compares either figure to its target |
| Mutation score (`FlowX.Core`) | ≥ 70 % | **not measured** — Stryker not run locally | WP-0 |
| Trim/AOT warnings | 0 | **0** ✅ | verified locally |
| Fitness functions | all green | **58/58** ✅ | `dotnet test tests/FlowX.Architecture.Tests -c Release`, plus compiler and code-fix fitness tests |
| NativeAOT publish | links **and runs** | **✅** | 11 MB binary served a real order |
| Concurrent cross-tenant leak | none | **none** ✅ | 64 concurrent flows, 0 overlaps |
| SAST findings | 0 | **wired, unrun** — needs a CI run | WP-0 |
| DAST findings | 0 | **wired, unrun** — the sample now exists; needs a CI run | WP-0 |
| Vulnerable dependencies | 0 | **0 by construction** — zero dependencies | WP-1 |
| Open debt entries | ≤ 20 | **1** — [DEBT-0001](docs/DEBT.md) | enforced by `SuppressionsAreAccountable` |
| B1 flow overhead | ≤ 5 µs | **172.3 ns** ✅ | WP-11, real engine, 30 iterations |
| B2 allocations per step | 0 B | **0 B** ✅ | gated as a unit test — **Release only**, see below |
| B3 capability dispatch | ≤ 150 ns | **21.9 ns** ✅ | shared hardware, advisory |
| B12 build overhead · **1 flow** | ≤ 8 % | **+0.4 %** ✅ | WP-14, like-for-like sample build |
| B12 build overhead · **200 flows** — P1's exit criterion | ≤ 8 % | **+67.1 %** ❌ | WP-43. **Accepted exception; P1 closed over it.** 50 flows: +46.5 %. Growth linear, R² 0.994 |
| Generator cost regression (relative, blocking) | ≤ +2 % | **green** ✅ | WP-31, deterministic allocation proxy. Prints `ABSOLUTE CRITERION — FAIL` on every run, so a pass here is not a met budget |
| B7 durable step commit | ≤ 15 ms p99 @ 5 000/s | **no harness** | P2 · WP-50 |
| B8 journal rehydration | ≤ 8 ms p99 | **no harness** | P2 · WP-50 |
| QR2 chaos: 10 000 flows, `SIGKILL` | 0 duplicate effects, 0 lost | **no rig** | P2 · WP-50 builds it, WP-62 runs it |

### The architecture's quality goals — [05 §1.2](docs/05-Architecture.md#12-quality-goals-measurable--arc42-12)

The gates above are mechanisms. These are the eight things the mechanisms exist to protect,
and until 2026-07-31 they were named nowhere in this file. Q1–Q3 are *architecture-defining*:
05 §1.2 requires an ADR wherever a design choice trades one away.

| # | Quality goal | Enforced by |
|---|---|---|
| **Q1** | predictable low latency | `EngineAllocationTests` (hard zero) + B1/B2. **The only quality goal whose gate has ever failed a build** |
| **Q2** | durable correctness | conformance suite vs real Postgres, lease, recovery scan. **The measure — p99 ≤ 15 ms — is unmeasured, and the *scenario* has never happened:** nothing has killed a process |
| **Q3** | static knowability | `ManifestIsComplete`, `flowx diff`, the error catalogue. Same half-gap as V7 — policies and events are unchecked |
| **Q4** | transport portability | **nothing.** One transport |
| **Q5** | operational uniformity | **nothing.** No `ActivitySource`, no `Meter`, no exporter (P5) |
| **Q6** | extensibility | `RuntimeDoesNotReferenceAnyPlugin` ✅; `PluginsPassConformance` **blocked**. `plugins/FlowX.Postgres` is the first outside implementation to push back on a contract |
| **Q7** | startup and footprint | **nothing.** Same gap as V5 |
| **Q8** | multi-tenant isolation | **nothing.** `CrossTenantAccessIsDenied` blocked on P4 and P3 |

### ADR inventory — 16 records, and which carry undischarged obligations

| ADR | Status | Revisit trigger | Obligation this file or the plan is missing |
|---|---|---|---|
| 0001 primitives · 0004 triggers · 0010 C# DSL · 0012 licence | Accepted | not fired | **0012:** the licence scan it calls for — [open item 9](PLAN.md#9-open-items-blocking-the-plan) |
| **0002** compile-time orchestration | Accepted | **FIRED** — "build overhead > 8 % sustained"; measured +67.1 % | The record does not say so. Its mitigation list, called *"all mandatory"*, includes a ≤ 8 % gate that ADR-0014 has since made advisory — **two ADRs disagree on whether the gate binds** |
| 0003 execution profiles | Accepted | not fired | `flowx verify --cost` is load-bearing in this ADR twice and **appears in neither planning file** |
| 0005 manifest · 0007 `Result` | Accepted | not fired | **0007:** `Result.Try` is named in the ADR and does not exist |
| 0006 journal + leases | Accepted | not fired | ceiling still a literature figure — tracked |
| **0008** serialization | Accepted | **cannot fire** — keyed on B7, which has no harness | Its warning box is **false since WP-53**: says "no journal to serialise into". `schemaVersion` and `IPayloadSerializer` are in neither planning file |
| **0009** plugin contracts | Accepted | reviewed "each phase gate" — **no record of a review at P1's gate** | Its warning box is **false since WP-53**: still says "no store has ever run against a real database". This is the record a plugin author reads |
| **0011** policy stage order | Accepted | needs three counterexamples collected — **nothing collects them**, so it cannot be revisited | the counterexample register does not exist |
| **0013** DSL vocabulary | Accepted | **has no `Revisit when`** | violates the index's own rule |
| **0014** catalogue vs budget | **Proposed** | **one of four FIRED** — the inner loop pays full derivation per edit, by construction. A second is **crossed, not fired**: withheld 42 % vs a 20 % trigger, on a corpus [B13 §2](docs/benchmarks/B13-error-catalogue-resolution.md) argues is inadmissible. *This cell said "two of four" and overstated it* | **corrected 2026-07-31.** The record headlined **+77.1 %** and claimed 200 flows had not been re-measured; [ADR-0014 §10](docs/adr/ADR-0014-derived-error-catalogue-vs-build-budget.md) now states +67.1 % and which triggers fired |
| 0015 journal schema | Accepted | cannot fire — keyed on B8, no harness | tracked well |
| **0016** Postgres adapter | Accepted | not fired | **has no `Negative` section.** Its WP-56 purge-guard note and its Oracle `Root`-scope portability rule are in neither planning file |

---

### The constraints — [05 §2](docs/05-Architecture.md#2-constraints)

| # | Constraint | Enforced by |
|---|---|---|
| **C1** .NET 10+/C# 14 | the SDK pin | ✅ |
| **C2** NativeAOT | AOT job + `IsAotCompatible` analyzers | ✅ **the only constraint with a failing gate** |
| **C3** hosts in ASP.NET Core | nothing explicit — held by construction | — |
| **C4** no 2-phase commit | nothing — held by design; the outbox that makes it correct is WP-56 | — |
| **C5** OpenTelemetry only | vacuous: nothing emits telemetry (P5) | — |
| **C6** Apache-2.0, no copyleft | **nothing.** `DependencyLicencesAreCompatible` specified in [15 §10](docs/15-Security.md) and ADR-0012, never written; no workflow scans licences; `Npgsql` arrived unvetted at WP-53 | ❌ [open item 9](PLAN.md#9-open-items-blocking-the-plan) |
| **C7** SemVer + 2-minor deprecation | `flowx diff` catches breaking changes; **nothing tracks the deprecation window** | partly |
| **C8** documentation-first | convention. Held well; no gate | — |

Nothing in the "Now" column is green by assertion — every ✅ was produced by a
command in this working tree. Every "not measured" is equally honest: the gate
exists and the mechanism to run it has not been run here. **"No harness" is a third
state and the worst of them**: the budget is stated, nothing can run, and P2's WP-50
exists to make sure that state does not survive into the phase that depends on it, the
way it survived into P1.

**The two B12 rows are one budget at two scales, and splitting them is the point.** A
single row reading +0.4 % ✅ was true of a one-flow sample and hid a criterion failing by
59 points on the solution shape the budget was actually written for.

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
| A02 Cryptographic failures | [x] `[Sensitive]` + generated redaction | [~] **enforced on both sinks that exist.** Secrets are stripped from Problem Details bodies, tested end to end, and from journal payloads since WP-52 — structurally, since `JournalPayload.ToJson()` is the only exit and it redacts. *This row said the journal does not exist yet.* Logs, traces and replay output still do not, and redaction is not applied by a **generated** serialiser anywhere, so the "no code path can forget it" claim is still not met |
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

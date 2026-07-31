# Live Checklist

> **This file is updated with every change.** It is the single place that answers
> "where is this project actually at?" — the [plan](PLAN.md) says what to build,
> this says what is built.
>
> **Last updated:** 2026-07-30 · **Phase:** **P0 complete → P1 in progress** ·
> **Commit:** see `git log`
>
> **Build:** 0 warnings, 0 errors · **Tests:** 1111/1111 passing ·
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
- [x] `docs/diagnostics/` — 21 pages plus an index, one per raised diagnostic; every help
      URI resolves, asserted by test
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
- [x] `quality.yml` — coverage thresholds, Sonar gate, Stryker mutation, unaccountable-TODO
      rule. The suppression rule is `SuppressionsAreAccountable` in the fitness functions,
      and only there (WP-35)
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
- [ ] `CrossTenantAccessIsDenied` — **blocked, not overlooked.** Nothing consumes
      `TenantId`: no policy executes at runtime, so no stage can return `Forbidden`; there
      is no journal, so there is no audit event to assert; one transport exists, so "every
      trigger kind" cannot be exercised. Needs P4 (policy execution, audit) and P2
      (journal). [21-Quality-Gates §2.4](docs/21-Quality-Gates.md)
- [ ] `RedactionCannotBeBypassed` — **blocked, not overlooked.** Exactly one sink can
      serialise a contract value today (the RFC 7807 body), and `ProblemDetailsMapperTests`
      already covers it. Logs, traces, the journal and replay output — the four sinks the
      rule is about — do not exist. Needs P3 and P5. Same section

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
- [ ] `PluginsPassConformance` — **blocked, not overlooked.** There is no conformance
      suite; [05-Architecture §11](docs/05-Architecture.md) names publishing one as the
      mitigation for R3 and R8 and it has not been written. There is also one plugin, so
      "every plugin agrees" has one data point.
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
      point and there is no journal, and unlike `AwaitSignal` it has no honest degenerate
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
      50 flows in review on a quiet machine (load 2.52), verdict **FAIL at +47.8 – +55.2 %**
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
- [ ] **⚠ `FlowX.Runtime` never reads `ExecutionProfile`.** Found by WP-40 while auditing
      risk R2. A `Durable` flow runs the ephemeral path; the profile affects only a plan
      validation and a manifest field. So R2 is not *mitigated* — it is **unreachable**, and
      it goes live the moment P2 lands. This also explains why several determinism
      diagnostics are blocked on severity rather than analysis
- [ ] **The cost gate measured a subject that did not compile** — the probe parsed a project
      with `ImplicitUsings=enable` without supplying them, so `ValueTask` and friends never
      bound. Harmless for relative comparisons of syntax-matching code, which is why it still
      caught the 4.9× regression; not harmless for anything that resolves a signature. Fixed
      and the baseline re-recorded. **Left open as a reminder**: a benchmark's subject needs
      a gate of its own, and this one had none

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
      refusal is now audible. **What remains open is the cause** — the abstractions give a
      plugin author no way to declare a kind the compiler can read, so the only fix
      offered is "use a built-in attribute instead"
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

## 6. Quality gates · current state

| Gate | Target | Now | Source |
|---|---|---|---|
| Compiler warnings | 0 | **0** ✅ | verified locally |
| Blocker/critical Sonar issues | 0 | **not running** | WP-0 |
| Line coverage | ≥ 80 % | **94.0 %** ✅ | verified locally |
| Branch coverage | ≥ 75 % | **87.0 %** ✅ | verified locally |
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

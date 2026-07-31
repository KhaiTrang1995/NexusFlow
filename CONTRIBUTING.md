# Contributing to FlowX

FlowX is **specification-first**. `docs/` is normative: code that contradicts it
is either a bug in the code, or an ADR that has not been written yet
(constraint C8). That makes contributing here a little different from most
repositories, so please read this before opening a pull request.

## The three kinds of contribution

| Kind | Start with | Requires |
|---|---|---|
| **Design change** — new concept, changed semantics, new extension point | an ADR pull request | discussion and acceptance **before** any code |
| **Implementation** — build something already specified | the issue for that spec section | tests first, then code, then doc updates |
| **Documentation** — clarify, correct, add examples | a pull request | consistency with the specification |

If your change alters what FlowX *is*, it is a design change. Open the ADR first;
a rejected ADR costs an hour, a rejected implementation costs a week.

## Definition of Done

A pull request is complete when **all** of these hold:

- [ ] Tests written **before** the implementation (red → green → refactor), and
      the pull request description says which behaviour they pin down
- [ ] Architecture fitness functions pass (`tests/FlowX.Architecture.Tests`)
- [ ] Benchmarks pass their budgets — zero allocations where the budget says
      zero, and no p95 over a documented ceiling
      ([14-Performance](docs/14-Performance.md))
- [ ] `flowx diff` is clean, or a major version bump plus an ADR is included
- [ ] The relevant `docs/` section is updated in the same pull request
- [ ] An ADR is included for every significant decision, with a
      "Revisit when" clause
- [ ] Every new diagnostic has a title, a message naming the symbol, a suggested
      fix and a help URI
- [ ] Public API changes respect SemVer and the 2-minor deprecation window
- [ ] `PublishAot=true` still succeeds with no trim warnings
- [ ] Commits are signed off (`git commit -s`) — DCO

*Two notes on that list. **"No regression > 5 %" was removed because nothing
enforces it**: `scripts/check-benchmark-budgets.py` treats absolute and ratio
drift as **advisory** and blocks only on allocation changes and a p95 over a
documented ceiling. WP-3's assumption that ratios are machine-independent was
withdrawn after two runs of the same commit disagreed by 63 % on ratio and 159 %
on absolute time; drift becomes a gate (`--strict`) once the baseline is recorded
on dedicated hardware. And **the DCO line is a convention, not a check** — no
workflow verifies `Signed-off-by`. Both are asked for in review.*

## Code standards

These follow from [03-Design-Principles](docs/03-Design-Principles.md). The third
column says whether a mechanism actually enforces the row today, because a rule
printed next to a gate reads as a gate:

| Rule | Enforced by | Runs today |
|---|---|---|
| Cognitive complexity ≤ 15 per method | `S3776` | **no** — set to `none`, see below |
| Line ≥ 80 % / branch ≥ 75 % coverage | *Coverage thresholds* job, `quality.yml` | yes, but **whole assembly**, not new code |
| Zero blocker Sonar issues | Sonar default profile + `TreatWarningsAsErrors` | yes, on every compile |
| Zero critical Sonar issues | *Sonar quality gate* job, `quality.yml` | **no** — needs `SONAR_TOKEN` |
| No reflection in `FlowX.Runtime` | `NoReflectionOnHotPath` | yes |
| No mutable statics in `FlowX.Runtime` | `RuntimeHasNoMutableStatics` | yes |
| No cyclic dependencies | `NoCyclicDependencies` | yes |
| `FlowX.Abstractions` has zero package references | `AbstractionsHasNoDependencies` | yes |
| Dependencies point inward | `LayersPointInward` | yes |
| Structured logging only — data as fields | review | **no analyzer** — nothing logs yet, see below |
| No swallowed exceptions, no magic numbers, no boolean behaviour switches | review | — |
| Names describe intent, not mechanism | [04 §11](docs/04-Core-Concepts.md#11-naming-rules) | review |

*Four rows of this table were wrong, and each is corrected rather than removed:
they are the bar, and three of the four are one edit or one secret away from
being enforced.*

- **"Cognitive complexity ≤ 15 per method — SonarAnalyzer, CI gate" was false.**
  `SonarAnalyzer.CSharp` *is* referenced and its default profile does gate the
  build, but `S3776` ships `IsEnabledByDefault=false`, and `.editorconfig` sets
  it to `none` explicitly. Enabled, it reports **12 methods over the threshold,
  worst 47** (`FlowEngine.RunRangeAsync`) — part of the **54 findings** across
  the four complexity and size rules listed in
  [21 §2.6](docs/21-Quality-Gates.md#26-what-the-analyzers-found-and-what-was-done-about-each).
  The threshold is pinned in `SonarLint.xml`, so turning the row on once that
  debt is paid is a one-word edit. Until then it is a **review instruction**.
- **"≥ 80 % coverage on new code" overstated the scope.** The Coverlet gate that
  runs measures the whole assembly; the diff-scoped half is Sonar's and does not
  run.
- **"Zero blocker/critical Sonar issues" merged a gate that runs with one that
  does not.** Blocker is real, on every compile. Critical belongs to the *Sonar
  quality gate* job, which exits 0 when `SONAR_TOKEN` is absent — and the token
  is not configured. The job emits a warning annotation naming the rows it did
  not evaluate, so its green tick is not mistakeable for a pass.
- **"Structured logging only — analyzer + review" named an analyzer that is not
  there.** Nothing under `src/` constructs an `ILogger`, `ActivitySource` or
  `Meter` at all ([12-Observability](docs/12-Observability.md), **P5**), so this
  is a commitment about code that has not been written. `CA1848` is on as a
  warning and will apply the day something logs.

Folder-by-feature inside layers. Tests mirror source 1:1. One composition root.
Namespace equals folder path.

## Test-first, concretely

```csharp
// 1 — RED: state the behaviour, watch it fail for the right reason
[Fact]
public async Task Retry_does_not_exceed_the_flow_deadline()
{
    var host = FlowTestHost.For<SlowFlow>().WithVirtualTime()
        .WithDeadline(TimeSpan.FromSeconds(5)).Build();

    var outcome = await host.RunAsync(AnInput());

    outcome.Should().HaveFailedWith("flow.deadline_exceeded");
    host.Trace.RetryAttempts.Should().BeLessThan(3);   // the policy stopped early
}
// 2 — GREEN: the smallest change that passes
// 3 — REFACTOR: with the test green
```

Resilience and timing behaviour must be tested with `WithVirtualTime()`. A test
that sleeps in real time will be asked to change.

> [!IMPORTANT]
> **`FlowTestHost` does not exist yet, so the block above is the shape to aim at
> rather than code you can run today.** `FlowX.Testing` ships
> `TestCapabilityContext` and `TestFlowContext` — context doubles — and nothing
> that runs a flow. `WithVirtualTime()`, `WithDeadline()` and `host.Trace` are
> unwritten, and the retry the test asserts on could not happen anyway because no
> policy executes. It is carried as unstarted P1 scope in
> [20-Roadmap §P1](docs/20-Roadmap.md), and [19 §3](docs/19-SDK.md) says the same.
> Write capability tests as plain unit tests against the doubles until it lands.

## Performance changes

Any pull request touching `FlowX.Runtime` or `FlowX.Compiler` must include
benchmark output before and after:

```bash
./scripts/run-benchmarks.sh              # run everything, then gate
./scripts/run-benchmarks.sh '*Dispatch*' # a subset — runs, does not gate
```

*This said `flowx bench --compare origin/main`. **The CLI has no `bench` verb** —
it has `graph`, `manifest` and `diff` ([22-CLI](docs/22-CLI.md)) — and there is
no `--compare`. The script above is what runs the harness and gates it against
`docs/benchmarks/baseline.json`.*

A regression needs either a fix or an ADR explaining the trade that was
deliberately accepted. "It's only 3 %" is not an argument in a platform whose
overhead is multiplied across an entire estate — but note that a 3 % drift is
**not** something CI currently fails on, for the reason given under Definition of
Done. Bringing it is the author's job, not the gate's.

## Plugins

Third-party plugins live in their own repositories. To be listed as compatible:

- depend on `FlowX.Abstractions` only
- pass the full `FlowX.Conformance.Tests` suite for every extension point
- publish the conformance result, an SBOM, a licence and a sample
- follow the checklist in [17 §9](docs/17-Plugin-System.md#9-plugin-quality-checklist)

*`FlowX.Conformance.Tests` **does not exist**, so the second bullet is a
requirement nobody can meet yet. It is a **P3** deliverable
([09 §8](docs/09-Trigger-Model.md), [ADR-0009](docs/adr/ADR-0009-plugin-contracts.md)),
and the fitness function that would check it — `PluginsPassConformance` — is
recorded as blocked rather than overlooked in
[21 §2.4](docs/21-Quality-Gates.md#24-gates-named-here-but-not-yet-enforced).
The bullet stays because it is the bar; it is not a bar that can be cleared
today.*

## Reporting security issues

Do **not** open a public issue. See `SECURITY.md` for the private disclosure
process. Response SLA: critical 48 hours, high 7 days.

## Reviewing

Reviewers are asked to check, in this order:

1. Does it match the specification? If not, where is the ADR?
2. Were the tests written first, and do they pin down behaviour rather than
   implementation?
3. Does it hold the budgets?
4. Would a mid-level engineer maintain this in six months?
5. Is the failure path designed, not just the happy path?

A pull request that is correct but undocumented is not done. A pull request that
is documented but untested is not done either.

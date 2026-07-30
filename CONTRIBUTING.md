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
- [ ] Benchmarks pass their budgets — no regression > 5 %, zero allocations where
      the budget says zero ([14-Performance](docs/14-Performance.md))
- [ ] `flowx diff` is clean, or a major version bump plus an ADR is included
- [ ] The relevant `docs/` section is updated in the same pull request
- [ ] An ADR is included for every significant decision, with a
      "Revisit when" clause
- [ ] Every new diagnostic has a title, a message naming the symbol, a suggested
      fix and a help URI
- [ ] Public API changes respect SemVer and the 2-minor deprecation window
- [ ] `PublishAot=true` still succeeds with no trim warnings
- [ ] Commits are signed off (`git commit -s`) — DCO

## Code standards

These follow from [03-Design-Principles](docs/03-Design-Principles.md) and are
enforced by analyzers, fitness functions and CI:

| Rule | Enforced by |
|---|---|
| Cognitive complexity ≤ 15 per method | SonarAnalyzer, CI gate |
| ≥ 80 % coverage on new code | coverage gate |
| Zero blocker/critical Sonar issues | quality gate |
| No reflection in `FlowX.Runtime` | `NoReflectionOnHotPath` |
| No mutable statics in `FlowX.Runtime` | `RuntimeHasNoMutableStatics` |
| No cyclic dependencies | `NoCyclicDependencies` |
| `FlowX.Abstractions` has zero package references | `AbstractionsHasNoDependencies` |
| Dependencies point inward | `LayersPointInward` |
| Structured logging only — data as fields | analyzer + review |
| No swallowed exceptions, no magic numbers, no boolean behaviour switches | review |
| Names describe intent, not mechanism | [04 §11](docs/04-Core-Concepts.md#11-naming-rules) |

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

## Performance changes

Any pull request touching `FlowX.Runtime`, `FlowX.Compiler` or a journal adapter
must include benchmark output before and after:

```bash
flowx bench --compare origin/main
```

A regression needs either a fix or an ADR explaining the trade that was
deliberately accepted. "It's only 3 %" is not an argument in a platform whose
overhead is multiplied across an entire estate.

## Plugins

Third-party plugins live in their own repositories. To be listed as compatible:

- depend on `FlowX.Abstractions` only
- pass the full `FlowX.Conformance.Tests` suite for every extension point
- publish the conformance result, an SBOM, a licence and a sample
- follow the checklist in [17 §9](docs/17-Plugin-System.md#9-plugin-quality-checklist)

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

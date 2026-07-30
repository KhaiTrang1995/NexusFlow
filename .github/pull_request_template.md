## What this changes

<!-- One paragraph. What behaviour is different after this merges? -->

## Why

<!-- The problem, not the solution. Link the issue or the documentation section. -->

## Documentation section this implements

<!-- e.g. docs/06-Execution-Engine.md §4, or PLAN.md WP-2. Constraint C8: a change
     without its documentation is not done. -->

## Definition of Done

From [docs/21-Quality-Gates.md §5](../docs/21-Quality-Gates.md#5-definition-of-done).
Tick honestly — an unticked box with a reason is far more useful than a ticked box that is not true.

- [ ] An ADR exists for any significant decision this change makes
- [ ] Tests were written **first**; the commit history shows red before green
- [ ] Line coverage ≥ 80 % and branch coverage ≥ 75 % on the diff
- [ ] Architecture fitness functions green
- [ ] Zero compiler warnings; zero blocker/critical Sonar issues
- [ ] Cognitive complexity ≤ 15 on every method touched
- [ ] SAST, SCA, secret scanning and container scan clean
- [ ] Public API documented; breaking changes carry a SemVer bump and a deprecation entry
- [ ] Benchmarks green if this touches a hot path
- [ ] **`CHECKLIST.md` updated in this same commit**

## Trade-offs

<!-- What did you decide against, and what would make you revisit it? A PR with
     no trade-off section is usually a PR where the alternatives were not considered. -->

## Risk

<!-- What breaks if this is wrong, and how would we find out? -->

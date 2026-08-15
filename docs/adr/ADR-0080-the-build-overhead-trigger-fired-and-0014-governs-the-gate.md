# ADR-0080: The build-overhead trigger fired, the budget was re-expressed rather than the bet withdrawn, and ADR-0014 §4(4) governs the gate

**Status:** Accepted
**Date:** 2026-08-15
**Deciders:** Repository owner · Platform architecture

This record reconciles two Accepted records. It decides nothing new and re-argues nothing —
every figure below is cited to the record or the benchmark that produced it. It exists for
two reasons. A reader following [PLAN §1](../../PLAN.md#1-what-p0-exists-to-prove)'s kill
criterion — *"Revisit ADR-0002 first"* — arrives at
[ADR-0002](ADR-0002-compile-time-orchestration.md) and finds a fired trigger with no verdict
attached to it. And 0002 and
[ADR-0014](ADR-0014-derived-error-catalogue-vs-build-budget.md) disagree in plain text about
whether one of 0002's mandatory mitigations binds, with nothing saying which record gives
way.

## 1. The trigger fired

ADR-0002's exit condition is *"> 3 generator defects per delivery phase, or build overhead
> 8 % sustained"*.

**Build overhead at 200 flows — the size P1's exit criterion names — is +67.1 %, 95 % CI
[+61.9, +73.6]**, verdict `FAIL`, recorded at WP-43 in
[B12-scale §5.4](../benchmarks/B12-scale.md#54-wp-43--the-criterion-re-measured-and-a-split-with-a-different-shape).
*Sustained* rests on more than that one reading: §5.1 recorded **+77.1 %** [+72.0, +80.6] on
different hardware before the duplicated-bind fix, and the pre-catalogue tree measured
**+18.4 %** [+16.3, +19.9] with the feature that dominates the figure nowhere in the build.
Every campaign that has measured this clause has found it true.

The clause's other half — more than three generator defects per delivery phase — is counted
by nothing, so it can neither fire nor be shown not to have. Nothing below depends on it.

## 2. The forced revisit happened, and its outcome was re-expression

The revisit the trigger obliged is
[ADR-0014](ADR-0014-derived-error-catalogue-vs-build-budget.md), **decided 2026-08-10**. It
kept compile-time orchestration and the derived error catalogue, and changed what the budget
says instead.

**Why re-expression and not withdrawal.** The number was never evidence against the
architecture, and 0014 is where that is argued rather than here:

- Deleting the feature that dominates the figure moves 200 flows from roughly +67 % back to
  roughly +18 % — **still a fail** — and no option 0014 priced reaches +8 %
  ([§2](ADR-0014-derived-error-catalogue-vs-build-budget.md#2-the-dilemma-as-posed-is-false-and-that-is-the-most-important-thing-here)).
- A ratio cannot be passed or failed in the first place.
  [§3 A](ADR-0014-derived-error-catalogue-vs-build-budget.md#a-keep-it-as-is-re-express-the-budget--recommended):
  a per-unit budget *"is testable at any size; a ratio is not, because its denominator is the
  user's code."* The gate's own baseline states it as a measurement — the same generator
  reads **+0.4 %** on the one-flow reference sample and **+67.1 %** at 200 flows, so *"a
  ratio whose bottom half moves with the subject cannot be passed or failed, only
  re-argued"* ([generator-cost-baseline.json](../benchmarks/generator-cost-baseline.json)).
- The architectural question ADR-0002 actually asked is answered separately, and
  favourably: the generator is **linear in flows** — `flows^0.95`, CI [0.87, 1.06], with no
  meaningful fixed term, on the same WP-43 run — which is an optimisation backlog rather
  than an architectural defect.

**What binds in its place**, per 0014's decision as corrected on the day it was taken (the
unit is *bytes allocated*, not the milliseconds the record recommended, because wall clock
moved 139 % across twelve identical runs where allocation moved 0.069 %): the generator
allocates **≤ 800,000 bytes per flow and ≤ 160,000 bytes per capability, at any subject
size**, set from 772,522 / 769,272 B per flow and 148,562 / 146,808 B per capability.
Milliseconds stay reported and advisory. That criterion is P1's `Done when` in
[20-Roadmap](../20-Roadmap.md), and `check-generator-cost.py` fails the run on a breach.

So ADR-0002's decision stands unwithdrawn and this record does not reopen it. What the
trigger obliged was a review; the review happened; this is where its outcome is written down.

## 3. The gate contradiction, resolved

ADR-0002's first negative declares its mitigation list *"all mandatory"*, and the fourth item
on that list is *"gate build overhead at ≤ 8 % (budget B12)"*.
[ADR-0014 §4(4)](ADR-0014-derived-error-catalogue-vs-build-budget.md#4-decision) commits the
opposite: the `scale-overhead` job *"stays advisory until a pass is recorded"*, and
`.github/workflows/performance.yml` carries `continue-on-error: true` on it.

**ADR-0014 §4(4) governs.** Concretely:

1. **The ≤ 8 % clause of ADR-0002's mitigation list is superseded** by §2's allocation
   ceilings — which are absolute, gateable on a shared runner, and blocking. The *mitigation*
   is not withdrawn; what changed is the unit it is stated in, and therefore which job
   enforces it. "All mandatory" continues to mean what it says about a gate that can fail.
2. **`scale-overhead` stays advisory**, and stays red. It measures P1's wall-clock question,
   which is real and unmet, and
   [generator-cost-gate §7](../benchmarks/generator-cost-gate.md#7-keeping-the-absolute-criterion-visible)
   keeps it visible by reprinting the criterion on every run, including passing ones.
3. **The other three mitigations stand, individually and unchanged** — emit readable C# to
   `obj/generated`; snapshot-test every emitted file; keep the generator's logic in a pure,
   unit-testable model layer separate from Roslyn plumbing. Superseding the fourth supersedes
   only the fourth.

One date does the work, and a reader needs it. ADR-0002's note says the resolving record *"is
still **Proposed**"* and that the disagreement *"stands and is no longer queued for
resolution"*. Both were true when written and stopped being true on 2026-08-10. ADR-0002 is
not edited, because this repository does not edit accepted records — so this is where the
reader learns it, and the index row is amended to point here.

## Consequences

- A reader sent to ADR-0002 by PLAN §1's kill criterion reaches a verdict in one hop, from
  the index row.
- Exactly one record now answers *does the ≤ 8 % gate bind* — no — and names the criterion
  that binds instead.
- **This record adds no gate and moves no ceiling.** If §2's ceilings are wrong they are
  wrong in ADR-0014 and its baseline, which is where they are argued and where a correction
  belongs.
- Recorded and deliberately not repaired: 0014's own
  [§10](ADR-0014-derived-error-catalogue-vs-build-budget.md#10-which-of-the-four-revisit-triggers-have-fired)
  still closes *"this record stays **Proposed**"*, which its own header contradicts. It is an
  accepted record's text; the index row now reads Accepted.

**Revisit when:** a wall-clock overhead measurement at a stated size records a **pass**, at
which point `scale-overhead` stops being advisory and ADR-0002's fourth mitigation can be
restated in its original unit rather than superseded; or one of ADR-0014's four revisit
triggers fires on admissible evidence and re-opens the budget this record says governs; or
the generator stops being linear in flows — §5.4's `flows^0.95` acquiring a superlinear
segment, or a fixed term large enough to matter — because §2's answer to *why not withdraw
the bet* rests on that fit and on nothing else.

---

**Back to:** [ADR index](README.md) · [ADR-0002](ADR-0002-compile-time-orchestration.md) ·
[ADR-0014](ADR-0014-derived-error-catalogue-vs-build-budget.md) ·
[B12 at scale](../benchmarks/B12-scale.md)

# B12 at scale — the 200-flow synthetic solution

> **Verdict: FAIL.** A 200-flow synthetic solution builds with **+18.4 %** overhead
> (95 % CI **+16.3 % to +19.9 %**) against a **+8 %** budget.
>
> **The cost is linear in flow count** — `flows^0.91`, 95 % CI **[0.82, 1.08]** — and is
> better read as a straight line: **≈ 9.5 ms of build time per flow, with no fixed term**,
> replicated to within 1 % by a second run in a different configuration. That is the
> finding that matters, and it is good news. The generator does not degrade as a solution
> grows; it is uniformly too expensive per flow.
>
> **Of that per-flow cost, ~62 % is `FlowPlanGenerator` and ~37 % is `StepBindingAnalyzer`
> (FLOWX1020).** `CapabilityAnalyzer`, which the first version of this document named as
> the likely suspect, is **1 %**.
>
> **Supersedes the provisional +23 %** recorded by WP-18, which was measured on a machine
> under load average 2–34 and could not be separated from that load. §9 keeps it, and what
> was wrong with it, on the record.
>
> **Recorded:** 2026-07-30 · **P1** · `./scripts/measure-scale-overhead.sh --rounds 15`

---

## 1. What this measures, and why it is a separate document

[B12.md](B12.md) settled build overhead for the reference sample and got **+0.4 %**. It
also named the mechanism that decides whether that figure travels:

> The generator's cost scales with the number of **flows**; a compilation's cost scales
> with the number of **files**. A one-file project maximises the generator's share, so
> that measurement is biased in a known direction rather than merely imprecise.

Half of that is right and the conclusion drawn from it is backwards. The first sentence is
the correct model, and it says the numerator is a function of *flows*. The reference sample
has **one** flow — the minimum of the numerator — so a small file count does not make it a
worst case. It makes it a best case, and +0.4 % is the friendliest number this budget will
ever produce.

[20-Roadmap §3](../20-Roadmap.md) names the missing measurement as P1's exit criterion:

> **Done when:** a 200-flow synthetic solution builds with ≤ 8 % overhead

Nothing in the repository is that shape, so `scripts/generate-scale-project.py` emits one.

---

## 2. The harness, and why the first version of it could not be believed

The comparison itself is unchanged and is B12's: two builds of the same project reaching
the **same final compilation**, differing only in whether the generator ran.

| Arm | How |
|---|---|
| **with** | the analyzer referenced, generator and diagnostic analyzers running |
| **without** | the analyzer dropped, the generated `.g.cs` compiled as ordinary source |

Both arms are verified to produce an identically sized assembly before any round is timed,
and the generated file count is checked against the flow count — a flow that silently
failed to analyse would make the control arm compile a smaller program, and the difference
between the arms would then be partly missing code.

What changed is everything around that comparison. WP-18's run reported a difference of
15 points between arms whose own ranges were 85 % of their medians. **When the spread
inside one arm is five times the difference between arms, the ratio is not a measurement.**
Five changes, none of which move the number in a chosen direction:

**Rounds are sandwiched, not merely alternated.** Each size in each round runs three
builds — `A B A`, or `B A B` on alternate rounds so neither arm is systematically favoured
— and the paired ratio compares the middle build against the *mean of the two outside it*.
A machine that drifts linearly across the round then contributes exactly nothing to the
ratio. Plain alternation leaves that drift in the number with a sign set by which arm
happened to run first, which is why WP-18's two statistics disagreed by a factor of two at
50 flows.

**The sandwich's outer pair is an A/A control.** Two builds of the *same* arm, the same
distance apart in time as the real comparison, whose true difference is zero by
construction. Whatever ratio they produce is this harness's noise floor, measured in the
same rounds under the same load as the thing being judged. It gets its own column, and
nothing else here is as useful: at 200 flows it says this harness reports ±8.7 % of
apparent overhead when there is none.

**Spread is an interquartile range, not a min-to-max range.** A max-minus-min range grows
with the sample count by construction and is set entirely by the two worst scheduling
accidents in the run. WP-18's "85 %" is that statistic. The equivalent IQR figures below
are 3–8 %.

**Sizes are interleaved, not run in blocks.** Every project is generated, warmed and
verified before any round starts, and then every round visits every size, alternating the
direction of travel. WP-18 measured each size to completion before starting the next, so
its growth curve was partly a record of how the machine's load changed between blocks —
which is how a fit lands on `flows^0.99` while the two segments it averages read 0.61 and
1.94.

**The harness can refuse to answer.** `scripts/analyse-scale-samples.py` returns
`INCONCLUSIVE` — exit code 2, distinct from PASS's 0 and FAIL's 1 — when the within-arm IQR
exceeds 25 % of the median, when the measured overhead is smaller than the A/A scatter, or
when the confidence interval on the overhead contains the budget. **A measurement that says
"this machine cannot resolve this" is a correct output.** It fired on the first attempt at
this verdict; see §3.

The statistics live in `scripts/analyse-scale-samples.py` rather than inside the shell
harness, and the raw per-build samples are written to JSON with `--json`. A run costs half
an hour; re-reading its arithmetic must not mean re-running it.

---

## 3. The measurement

```bash
./scripts/measure-scale-overhead.sh --rounds 15 --sizes 1,25,50,100,200
```

15 rounds after one discarded warm-up round, three builds per size per round — 240 timed
builds. Wall clock, with the shared compiler server on, which is `dotnet build`'s default
and B12's configuration exactly.

```
 flows       with    without   overhead             95 % CI   A/A scatter
     1     1714 ms     1718 ms      -2.4 %        [-4.6, -0.5]        10.8 %
    25     2970 ms     2726 ms     +10.8 %       [+6.2, +12.1]         8.4 %
    50     4168 ms     3705 ms     +13.1 %      [+10.4, +16.0]         7.3 %
   100     6588 ms     5674 ms     +15.1 %      [+13.1, +17.2]         9.3 %
   200    12085 ms    10249 ms     +18.4 %      [+16.3, +19.9]         8.7 %

VERDICT: FAIL — 95 % CI [+16.3, +19.9] % lies above the +8 % budget at 200 flows
```

| Flows | with-arm IQR | without-arm IQR | FlowX's cost | Per flow |
|---:|---:|---:|---:|---:|
| 1 | 4.2 % | 7.2 % | −42 ms *(below the noise floor)* | — |
| 25 | 7.8 % | 3.9 % | +292 ms | 11.68 ms |
| 50 | 2.7 % | 5.4 % | +474 ms | 9.49 ms |
| 100 | 5.5 % | 4.9 % | +874 ms | 8.74 ms |
| 200 | 3.7 % | 5.9 % | **+1 946 ms** | **9.73 ms** |

Spreads of 3–8 % of the median against a difference of 18 %, and a confidence interval
3.6 points wide that clears the budget by more than eight points. This is a verdict.
WP-18's was not.

### The machine, which decides whether any of this is true

Same shared container the [P0 report](P0.md#5-is-shared-hardware-good-enough-to-decide-on)
documents: 4 logical cores, Intel Xeon @ 2.80 GHz, Ubuntu 24.04.4, .NET SDK 10.0.110. The
harness samples `/proc/loadavg` before every timed build and records all of them.

| Run | Load: min / median / max | Verdict |
|---|---|---|
| First attempt, 13 rounds | 2.25 / **16.77** / 46.66 | **INCONCLUSIVE** — within-arm IQR 56 %, over the 25 % limit |
| **Recorded, 15 rounds** | 2.63 / **4.07** / 12.60 | **FAIL** at +18.4 % |
| Cross-check, 8 rounds, server off (§4) | 1.49 / **2.16** / 3.92 | **FAIL** at +18.0 % |

A 1-minute load average of 4 is a fully busy machine at 4 cores, and the recorded run's own
builds account for most of its 4.07. The first attempt ran while three sibling branches
were compiling and testing, and **the harness refused to render a verdict on it** — which
is the behaviour this package exists to add. Its point estimate at 200 flows was +23.3 %
with a confidence interval of [+7.6, +42.6]: the same direction, a third of it noise, and
by its own gate not reportable.

All three runs are in the record, because a harness that only publishes the run it liked is
not a harness.

---

## 4. The scaling question, which matters more than the ratio — and this time it is answered

A generator whose cost grew faster than the flow count would fail at 2 000 flows however it
behaved at 200, and would mean the cost could never be paid down by a constant-factor
optimisation. That is the outcome that would change the architecture rather than the
schedule. **It is not what is happening.**

| Fit | Result |
|---|---|
| Power law | **`flows^0.91`**, 95 % CI **[0.82, 1.08]**, R² 0.988 |
| Straight line | **≈ 3 ms fixed + 9.54 ms per flow**, R² **0.994** |
| Segment 25 → 50 | `flows^0.70` |
| Segment 50 → 100 | `flows^0.88` |
| Segment 100 → 200 | `flows^1.15` |

The interval on the exponent is bootstrapped by resampling rounds within each size and
refitting, so it carries each size's own uncertainty into the exponent rather than fitting
a line through five point estimates and reporting the slope as though the points were
exact. It **contains 1.0 and excludes everything above 1.1**. The three segments agree with
each other and with the fit, which is exactly what WP-18's did not do.

**The straight line is the better description**, and its intercept is the reason: 3 ms of
fixed cost against a 12-second build is nothing, so cost is essentially *proportional* to
flow count from the origin. The per-flow column in §3 says the same thing without a fit —
11.7, 9.5, 8.7, 9.7 ms per flow across an eight-fold range of sizes.

**Confidence in this: high, with two stated limits.** Four sizes over an 8× range, fifteen
rounds each, interleaved, with an A/A control at every size and every within-arm IQR under
8 %.

The first limit is that 200 flows is the largest size measured, so the claim is *linear up
to 200 flows*, not *linear forever*. The mechanism §5 finds is per-flow work rather than a
whole-compilation pass, which is the reason to expect it to continue — but 400 and 2 000
flows have not been measured here, and WP-18's 400-flow point is deliberately not carried
forward: it was taken at load average 25–34 and was the least trustworthy number in this
document's history.

The second is that **the 100 → 200 segment is the steepest in every run** — 1.15 here,
1.18 on CPU in the cross-check — while the fit's interval tops out at 1.08. Three segments
with no interval each cannot distinguish "the curve is bending upward at the top" from
"the last segment drew the high side of the noise", and the affine fit's R² of 0.994 leans
firmly towards the second. **It is the number to watch when 400 flows is eventually
measured**, and it is not, on this evidence, a reason to withhold the linear reading.

**The 1-flow row is not a data point about scaling.** Its measured cost is −42 ms, negative
because the true value (~10 ms) is an order of magnitude below the harness's noise floor.
It is excluded from both fits automatically, because its confidence interval contains zero
and a point with no position on a log axis cannot be given one. It is reported because it
is the independent confirmation that this harness reproduces B12: at one flow, overhead is
indistinguishable from zero.

### A second run, in a different configuration, on a quieter machine

The verdict above is wall clock, which on a shared box is partly a measurement of the other
tenants. `--no-compiler-server` trades that for CPU time — user + sys across the whole build
process tree, which barely moves with load and can only be attributed when the compile is
not happening inside a compiler-server process shared with every other build on the machine.
Eight rounds, four sizes, load average median **2.16** (the quietest window of the day):

```bash
./scripts/measure-scale-overhead.sh --no-compiler-server --rounds 8 --sizes 25,50,100,200
```

| At 200 flows | §3, server on, wall | This run, wall | This run, **CPU** |
|---|---:|---:|---:|
| Overhead | +18.4 % [+16.3, +19.9] | **+18.0 %** [+16.0, +19.9] | +9.0 % [+8.4, +11.0] |
| FlowX's cost | +1 946 ms | +2 536 ms | +2 529 ms |
| Per flow | 9.73 ms | 12.68 ms | 12.65 ms |
| **Marginal cost per flow, affine fit** | **9.54 ms** (R² 0.994) | **9.44 ms** (R² 0.980) | **11.20 ms** (R² 0.972) |
| Verdict | FAIL | FAIL | FAIL |

Two things to take from it.

**The verdict replicates.** +18.4 % and +18.0 %, from two runs hours apart in different
compiler configurations, with intervals that all but coincide. The CPU figure is lower
because that ratio is distorted — with the server off, both arms carry a cold Roslyn start
that inflates the denominator, while the generator pays its own JIT every build and
inflates the numerator, by amounts nobody measured. The harness knows this and will not
turn a pass in that configuration into a PASS; it will report a fail, because an effect
this size survives the distortion. **The costs in milliseconds are not distorted**, because
a constant present in both arms cancels in the difference between them.

**And the marginal cost per flow replicates to within 1 %**: 9.54 ms and 9.44 ms of wall
clock, 11.20 ms of CPU. That is the number §4 rests on, arrived at three ways.

**The power-law exponents from the two runs do *not* agree, and the reason is instructive.**
This run reads `flows^0.49` [0.38, 0.61] on wall clock against §4's `flows^0.91`. Nothing
changed about the generator; what changed is that turning the compiler server off adds a
**585 ms fixed term** to the measured cost, and a cost of the form `fixed + k × flows`
reads as sublinear under a power law — the more so the smaller the smallest size measured.
The affine fits, which separate the constant from the slope, agree to within 1 %. **This is
why both fits are reported**, and it is a caution about quoting a single exponent for a cost
that has a fixed part. Neither run's interval reaches 1.2 in any metric, which is the claim
§4 actually makes.

---

## 5. Where the cost actually goes

The measurement above prices FlowX's whole compile-time cost, because the control arm drops
`FlowX.Compiler` entirely. That is the right total for a budget phrased as "overhead vs
identical non-FlowX code" — a non-FlowX codebase runs neither the generator nor the
analyzers — and the wrong number for deciding what to optimise. Roslyn will split it on
request:

```bash
dotnet build ScaleSynthetic.csproj -c Release --no-restore --no-dependencies \
  --no-incremental /p:ReportAnalyzer=true /p:UseSharedCompilation=false -v d
```

Medians of three builds per size. These are Roslyn's own per-component execution times,
summed across threads, so they total more than the wall-clock difference in §3: the
components run concurrently with each other and with the rest of the compile.

| Flows | `FlowPlanGenerator` | `StepBindingAnalyzer` (FLOWX1020) | `CapabilityAnalyzer` (FLOWX1003/4) |
|---:|---:|---:|---:|
| 25 | 1.259 s | 0.137 s | 0.024 s |
| 50 | 1.553 s | 0.196 s | 0.019 s |
| 100 | 1.883 s | 0.440 s | 0.032 s |
| 200 | **2.672 s** | **0.972 s** | 0.051 s |
| **Marginal cost per flow, 25 → 200** | **8.07 ms** | **4.77 ms** | **0.15 ms** |
| **Share of the marginal cost** | **62 %** | **37 %** | **1 %** |

Four things follow.

**Every component is linear in flow count too.** The split is not hiding a superlinear term
inside a linear total, which is the reassurance §4's fit on its own cannot give. The three
marginal costs sum to **13.0 ms per flow**, against the 11.2 ms per flow of CPU time §4
measured from the outside — close enough for two instruments that count overlapping work
differently, and a fourth independent confirmation of the same slope.

**`CapabilityAnalyzer` is not the problem, and the previous version of this document
guessed that it was.** Its §4 named the analyzer that "visits every named type in the
compilation, of which there are about 2 500" as the first thing to look at. It costs 51 ms
at 200 flows — 1 % of the marginal cost, and less at 200 flows than `StepBindingAnalyzer`
costs at 25. The guess was reasonable and it was wrong, which is the argument for measuring
the split rather than reasoning about it.

**`StepBindingAnalyzer` is a third of the bill and is not a generator problem.** FLOWX1020
is contract-compatibility checking between adjacent steps, and the synthetic project has
800 capability steps. Roughly 4.8 ms per flow — about 1.2 ms per step — is spent proving
that each step's input type is something a prior step produced. That is a diagnostic doing
real work, and it is a separate optimisation target with a separate cost/benefit: the
generator's cost buys emitted code, the analyzer's buys an error message.

**The `FlowPlanGenerator` fixed term is mostly cold JIT, not a scaling cost.** The ~1.06 s
intercept implied by that column is an artefact of `UseSharedCompilation=false`, which this
diagnostic needs in order to see per-component timings at all; a warm compiler server pays
it once per server lifetime rather than once per build. The *marginal* row is the part that
scales, and it is the part that matters.

Not diagnosed here: whether the generator's 8 ms per flow is dominated by semantic-model
queries, syntax walking, or string formatting of the emitted source. That needs a profiler
rather than a stopwatch, and it is the first step of whatever package fixes this.

---

## 6. What this does not claim

**It does not claim +18.4 % is the number on your machine.** It claims the overhead at 200
flows is between roughly +16 % and +20 % on this hardware and configuration. A machine with
more cores would move it in a direction this measurement cannot predict, because the
generator and the rest of the compile parallelise differently.

**It does not claim anything about 400 or 2 000 flows** beyond the extrapolation §4
licenses and the caveat §4 attaches to it.

**It does not measure incremental builds.** The recorded figures are full `dotnet build`
wall clock on `--no-incremental`, with restore and dependencies excluded because they are
identical in both arms. A build that touches one flow in an existing solution is a
different measurement and is not made here. Source generators re-run on every compilation,
so it is unlikely to be more flattering per unit of work — but that is an expectation, not
a result.

**It does not claim the synthetic project resembles a real one** in any respect except the
ones that were chosen. The reasoning behind each choice — why the flows differ from one
another, why the capability bodies are deliberately minimal, why the project inherits none
of the repository's analyzer settings — is documented at the top of
`scripts/generate-scale-project.py`. Each is made in the direction that makes the overhead
ratio *larger*, so a pass would have been a conservative pass. This is a fail, and those
choices are the reason to read it as an upper bound rather than as a prediction of what a
real 200-flow solution would see.

---

## 7. Why this and B12 do not contradict each other

Both are correct measurements of different projects.

| | Reference sample | 200-flow synthetic |
|---|---:|---:|
| Flows | 1 | 200 |
| Compilation, generator off | ~2.6 s | ~10.2 s |
| FlowX's compile-time cost | ~3 ms | ~1 946 ms |
| Overhead | +0.4 % | **+18.4 %** |

The compilation grew about 4×; FlowX's compile-time cost grew about 650×. Overhead is a
ratio of two quantities that scale with different things, so a single sample can only ever
report the ratio at its own size. B12's +0.4 % was never a property of the generator; it
was a property of a project with one flow in it, and it remains true of that project. This
document's own 1-flow row reproduces it.

What this changes is what the +0.4 % may be used to argue. It may not be used to argue that
build overhead is under control at scale, and
[14-Performance §1](../14-Performance.md)'s unqualified "+8 %" is not currently met by any
project big enough to notice.

---

## 8. Consequences

[ADR-0002](../adr/ADR-0002-compile-time-orchestration.md)'s revisit clause names this
threshold:

> **Revisit when:** generator maintenance cost exceeds its benefit (measured as > 3
> generator defects per delivery phase, or **build overhead > 8 % sustained**).

The measured figure is over that threshold at every size from 25 flows upward. **What §4
changes is what the clause should do about it.** A superlinear generator would have meant
the compile-time-orchestration thesis does not survive contact with a real solution, and
would have put ADR-0002 itself in question. A linear generator at 9.5 ms per flow is a
constant factor, and constant factors are an optimisation backlog rather than an
architectural defect. The clause is live; on this evidence it is not a reason to reopen
ADR-0002.

Two consequences are concrete. Neither is done here — this package measured, it did not
fix:

1. **Profile `FlowPlanGenerator`** (62 % of the marginal cost, ~8 ms per flow) and
   `StepBindingAnalyzer` (37 %, ~4.8 ms per flow). Roughly a 2.3× improvement across the
   two would bring 200 flows inside the budget. Nothing about the shape of the curve says
   that is out of reach, and nothing here says which part of either is hot.
2. **Decide what the budget means at scale.** +8 % is declared against no stated project
   size, which is how the same generator can pass at one flow and fail at 25. A budget
   phrased per flow — the measurement here says ~9.5 ms, and a plausible target is a small
   fraction of it — would be falsifiable at any size.

The CI job (`.github/workflows/performance.yml`, `scale-overhead`) **stays advisory**
(`continue-on-error: true`). This is P1's *exit* criterion; making it blocking today would
red every pull request for the length of the phase over a defect none of them introduced —
including the pull requests that fix it — and a red gate people learn to ignore is worse
than no gate. The script's exit code is real, `pipefail` is set, and the step is reported as
failed. **Remove `continue-on-error` the moment this document records a pass**, and when
that happens keep exit code 2 non-blocking: `INCONCLUSIVE` means the runner could not
resolve the question, and failing a pull request for that fails it for the weather. The
workflow comment spells out the exact blocking form.

---

## 9. History: the provisional +23 %, and why it is superseded rather than deleted

WP-18 built this harness and recorded **+23 %** at 200 flows, with the growth exponent
explicitly unresolved. That figure is superseded. It is kept here because a benchmark
report that quietly changes its numbers is not a benchmark report.

| | WP-18, provisional | This document |
|---|---|---|
| 200-flow overhead | +23.3 % / +20.7 % (two statistics) | **+18.4 %**, CI [+16.3, +19.9] |
| Within-arm spread | **85 %** of the median (min-to-max) | **3.7 %** of the median (IQR) |
| Load average | 2 – 34 | 2.63 – 12.60, median 4.07 |
| Arms | alternated | sandwiched, `A B A` / `B A B` |
| Noise floor | not measured | A/A control, ±8.7 % at 200 flows |
| Sizes | 50, 200, 400, in blocks | 1, 25, 50, 100, 200, interleaved |
| Growth exponent | `flows^0.99` from segments reading 0.61 and 1.94 — reported as unresolved | **`flows^0.91`**, CI [0.82, 1.08], segments 0.70 / 0.88 / 1.15 |
| Verdict | FAIL, with the caveat that it was not separable from noise | **FAIL**, separable from noise |

**What WP-18 got right.** It reported the ratio as provisional rather than as a result; it
printed the segment slopes next to the fit so that a fit averaging away a disagreement was
visible rather than convincing; it refused to drop the 50-flow row after seeing that it
disagreed; and it said in as many words that the exponent was unresolved and that settling
it needed a quiet machine rather than more rounds. Every one of those judgements survived
contact with the better measurement. **Its direction was right and its magnitude was about
25 % too high** — roughly what an unsandwiched comparison on a drifting machine would be
expected to add.

**What was wrong with it.** The four methodological faults §2 lists, and one substantive
conclusion: its §4 named `CapabilityAnalyzer` as the first place to look for the cost. §5
measures it at 1 % of the marginal cost.

The superseded run's own numbers remain in this document's history in git, and its
400-flow row is deliberately not carried forward.

---

## 10. Reproducing this

```bash
# The recorded verdict and the curve. ~25 minutes on a machine at load < 5;
# it took closer to an hour under the contention §3 describes.
./scripts/measure-scale-overhead.sh --rounds 15 --sizes 1,25,50,100,200

# The criterion alone, faster.
./scripts/measure-scale-overhead.sh --rounds 10 --sizes 200

# Keep the raw per-build samples, and re-read the statistics without re-measuring.
./scripts/measure-scale-overhead.sh --rounds 15 --json /tmp/scale.json
./scripts/analyse-scale-samples.py /tmp/scale.json --budget 8

# The load-robust cross-check in section 4: CPU time rather than wall clock. Its RATIO
# is distorted by the cold Roslyn start it forces on both arms, so it may report a fail
# but the harness will not let it report a pass. Its per-size cost in milliseconds is
# correct, because a constant in both arms cancels in the difference.
./scripts/measure-scale-overhead.sh --no-compiler-server --rounds 8 --sizes 25,50,100,200

# Where the cost goes (§5).
./scripts/generate-scale-project.py --flows 200 --out /tmp/scale-200
dotnet build /tmp/scale-200/ScaleSynthetic.csproj -c Release --no-incremental \
  /p:ReportAnalyzer=true /p:UseSharedCompilation=false -v d | grep -A20 'Total generator'
```

Exit codes: **0** PASS, **1** FAIL, **2** INCONCLUSIVE, **3** the measurement itself broke.
The third one is the point of this package.

---

**See also:** [B12 at one flow](B12.md) · [ADR-0002](../adr/ADR-0002-compile-time-orchestration.md) ·
[Benchmark harness](README.md) · [Roadmap P1](../20-Roadmap.md) · [Plan](../../PLAN.md)

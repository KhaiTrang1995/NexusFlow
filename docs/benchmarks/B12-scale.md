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

> [!IMPORTANT]
> **§5.1 supersedes the split above, and the headline with it.** Re-running the same
> diagnostic on the same machine reproduces `StepBindingAnalyzer` at 4.70 ms per flow and
> `CapabilityAnalyzer` at 0.14 — both within 1.5 % of the figures recorded here — and finds
> `FlowPlanGenerator` at **28.7 ms per flow against the 8.07 below**. The generator became
> ~3.6× more expensive between this record and today's `dev`; nothing else moved. The
> 200-flow criterion now measures **+77 %**, not +18.4 %, and the split is **85 %
> `FlowPlanGenerator` / 14 % `StepBindingAnalyzer`**, not 62/37.
>
> §5.1 also records WP-27's optimisation of `StepBindingAnalyzer` — 4.70 → **0.53 ms per
> flow**, a 89 % cut — and is candid that it does not move the end-to-end number, because
> after the generator's growth it was no longer large enough to.
>
> §3 to §5 are left exactly as recorded. They were correct for the tree they were taken on,
> and the fact that two of their three components still reproduce is what makes the third's
> change readable at all.

> [!IMPORTANT]
> **§5.2 answers §5.1's closing question by bisect.** The generator's growth is **one
> commit** — `c7ae70a`, WP-22's capability error catalogues — which takes it from 5.60 to
> **27.28 ms per flow**, 4.9×. `Switch`/`Case`, `Parallel`/FLOWX1013 and the manifest
> sort-key fix cost nothing detectable; FLOWX1011 is an analyzer and never appeared here.
> Stubbing out the catalogue read returns the generator to 7.36 ms per flow, which is the
> control that closes it.
>
> **It is not a defect.** 93 % of the reader is `SemanticModel.GetTypeInfo`, asked of every
> expression in every capability body, because deriving `errors` from the code means binding
> the code. §5.2 removed a duplicated bind worth **20 %** and argues that the rest is what
> the feature costs. **The criterion is not closer**: 50 flows measures +46.6 %
> [+42.8, +51.3] against a +8 % budget.

> [!IMPORTANT]
> **§5.3 answers the question §5.2 leaves behind — why nothing stopped it — and closes it
> with a blocking gate.** The reason a 4.9× regression merged in silence is not that the CI
> job was advisory. It is that the job was **absolute**: it compared the build against a
> +8 % budget the project was already failing by ten points, so it said the same thing
> before the regression as after it.
>
> [**generator-cost-gate.md**](generator-cost-gate.md) records the replacement — a
> *relative* gate against a committed baseline, blocking on every pull request, which fails
> `c7ae70a` at **+102 %** against a **+2 %** threshold. It does not gate wall clock,
> because wall clock cannot do this: twelve identical runs of the same tree disagreed with
> each other by **+139 %** while the real 4.9× regression showed as **+77 %**. It gates
> bytes allocated by the generator, which over those same twelve runs moved by **0.069 %**.
>
> **This document's criterion is unchanged and still failing**, and the new gate reprints
> it on every run so that a green relative gate cannot be read as a budget that is met.

> [!IMPORTANT]
> **§5.4 is the current measurement, and it supersedes every figure above it.** The
> criterion at 200 flows is **+67.1 %**, 95 % CI **[+61.9, +73.6]**, against a **+8 %**
> budget. **FAIL.** A/A noise floor 10.6 %, within-arm IQR 7.3 % and 7.5 %, load average
> 1.87 / 2.88 / 3.76 — the quietest run in this document's history.
>
> **At 50 flows it is +46.5 % [+42.4, +51.0], against §5.2's +46.6 % [+42.8, +51.3].**
> Everything that landed between those two runs — WP-37's rewrite of the error-catalogue
> reader, `Switch`, `Parallel`, `ForEach`, `SubFlow`, `Fail`, the `FlowContext<TIn>` view
> and five new analyzers — **nets to no change the harness can see.** The savings and the
> features cancelled.
>
> **The split is now `FlowPlanGenerator` 90.5 %.** `StepBindingAnalyzer`, which §5 recorded
> at 37 %, is **1.2 %**, and the three analyzers above it are all new. Full table in §5.4.

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
exact. It **contains 1.0 and excludes everything above 1.1**. The three segments span 0.70
to 1.15 and straddle the fit rather than contradicting it — WP-18's read 0.61 and 1.94 and
its fit landed at 0.99 by cancellation, which is the failure mode printing the segments
exists to expose.

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
clock, 11.20 ms of CPU. That is the number the linear reading rests on, arrived at three
independent ways.

**The power-law exponents from the two runs do *not* agree, and the reason is instructive.**
This run reads `flows^0.49` [0.38, 0.61] on wall clock against the `flows^0.91` above. Nothing
changed about the generator; what changed is that turning the compiler server off adds a
**585 ms fixed term** to the measured cost, and a cost of the form `fixed + k × flows`
reads as sublinear under a power law — the more so the smaller the smallest size measured.
The affine fits, which separate the constant from the slope, agree to within 1 %. **This is
why both fits are reported**, and it is a caution about quoting a single exponent for a cost
that has a fixed part. What survives every configuration is that **no interval in any run,
on either metric, reaches 1.2** — which is the claim this section actually makes.

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

### 5.1 WP-27 — the analyzer profiled and cut, and a generator that grew while nobody looked

Recorded **2026-07-30**, later the same day, on the same container. Two findings, and the
second is the one that matters more.

#### The split re-measured, with two of three components as the control

The §5 command, re-run at 25 and 200 flows, three builds each, medians. Load average 2.6 to
3.5 throughout — quieter than the run §5 was taken on.

| Component | §5, ms per flow | Re-measured, ms per flow | |
|---|---:|---:|---|
| `StepBindingAnalyzer` (FLOWX1020) | 4.77 | **4.70** | reproduces to 1.5 % |
| `CapabilityAnalyzer` | 0.15 | **0.14** | reproduces to 7 % |
| `PredicatePurityAnalyzer` (FLOWX1011) | not yet present | 0.03 | — |
| **`FlowPlanGenerator`** | **8.07** | **28.7** | **3.6× the recorded figure** |

**Two components reproducing to within 1.5 % is what makes the third readable.** A slower
machine, a different SDK or a changed synthetic project would have moved all four. Only the
generator moved, and `git log a75c1f0..dev -- src/FlowX.Compiler` names four non-merge
commits that landed between the §5 record and the tree measured here: FLOWX1011, the
trigger and error-catalogue manifest work, a manifest sort-key fix, and `Switch`/`Case`.
**Which of them is responsible is not diagnosed here**, and attributing it needs the same
bisect-and-measure this section did for the analyzer.

The end-to-end criterion moved with it. Twelve rounds at 200 flows, wall clock, on the
quiet machine:

```
 flows       with    without   overhead             95 % CI   A/A scatter
   200    13064 ms     7408 ms     +77.1 %      [+72.0, +80.6]         5.0 %
```

Within-arm IQR 2.1 % and 4.9 % — the best-conditioned run in this document's history. The
control arm is *faster* than §3's (7 408 ms against 10 249) because the machine was quieter;
the treatment arm is slower (13 064 against 12 085) because the generator is dearer. Both
push the ratio the same way.

#### Where `StepBindingAnalyzer`'s 4.7 ms per flow actually went

`/reportanalyzer` stops at the analyzer boundary, so the analyzer was instrumented with
timestamp counters around each phase. Three builds at 200 flows; milliseconds summed across
threads:

| Phase | build 1 | build 2 | build 3 |
|---|---:|---:|---:|
| class declarations visited | 1 251 | 1 251 | 1 251 |
| flows analysed / steps checked | 200 / 800 | 200 / 800 | 200 / 800 |
| `GetDeclaredSymbol` | 3.3 | 3.0 | 2.9 |
| the `[Flow]` filter (`ToDisplayString` per attribute) | 11.4 | 27.3 | 37.4 |
| `FlowInputContract` | 10.4 | 14.8 | 1.7 |
| `Define` lookup | 1.1 | 1.3 | 1.1 |
| `FlowChainWalker` | 2.7 | 2.8 | 2.9 |
| the chain walk | 816.6 | 1 849.2 | 1 297.7 |
| — of which **resolving each step's capability type** | **787.4** | **1 752.1** | **1 234.5** |
| — of which reading its `ICapability<,>` contract | 14.6 | 15.3 | 48.2 |

**One call, 96 % of the bill**, in all three builds: `GetSymbolInfo` on the `T` of a
`.Step<T>()`. Resolving a type name is not expensive. What is expensive is that the name
sits inside a statement, and a statement is the smallest thing Roslyn will bind — so asking
what one node of it means binds the entire `Define` chain, overload resolution and generic
inference at every link, lambdas and all.

Splitting the same call by position proves that is the mechanism rather than a guess about
it:

| | calls | total |
|---|---:|---:|
| first `.Step<T>()` of a flow | 200 | 826.9 ms — **4.13 ms each** |
| every later `.Step<T>()` | 600 | 37.6 ms — **0.06 ms each** |

One body bound per flow and then cached, not a slow lookup repeated 800 times. **So §5's
reading of this number — "roughly 1.2 ms per step … proving that each step's input type is
something a prior step produced" — is wrong on both counts.** It is 4.1 ms per *flow*, and
it is not spent on the proof; it is spent binding a method body the compiler binds again
anyway when it emits, and does not share.

#### The fix, and the evidence it returns the same answers

Bind the name where binding costs nothing: speculatively, at a position inside the flow
class but outside any member body — just past its opening brace. That binder sees what a
type name written in the class sees (the file's usings and aliases, the enclosing
namespaces, the class's own members and type parameters). What it does not see are a
method's type parameters and its locals, and neither can name a type at a `.Step<T>()`:
`Define` is an override with a fixed non-generic signature, and C# has no local types.

Both candidates were run **inside the same build as the original and before it**, so that
anything they warmed was available to it, and all 800 answers were compared for symbol
identity:

| How a step's type argument is resolved | build 1 | build 2 | disagreements |
|---|---:|---:|---:|
| speculative, at the class's opening brace | **36.9 ms** | **64.7 ms** | **0 of 800** |
| speculative, at the call site | 100.1 ms | 151.4 ms | 0 of 800 |
| `GetSymbolInfo` on the node in place — the original, run last | 926.0 ms | 930.2 ms | — |

The call-site position is 6–9× cheaper because speculative binding does not bind the body;
the class position is cheaper again because it does not build a member model at all. **The
original stayed at ~930 ms even running last**, which is what rules out "the first caller
pays and the rest read its cache". The same pass dropped `ToDisplayString()` from the
`[Flow]` filter, which ran on every attribute of every class in the compilation.

Result, from the §5 command with the two analyzer builds alternated:

| `StepBindingAnalyzer` | 25 flows | 200 flows | marginal, 25 → 200 |
|---|---:|---:|---:|
| before | 120 ms | 942 ms | **4.70 ms per flow** |
| after | 52 ms | 144 ms | **0.53 ms per flow** |

**An 89 % cut**, and the analyzer no longer binds a `Define` body at all.

#### What it does not buy

**The end-to-end criterion did not move detectably.** Three 12-round runs at 200 flows,
back to back on the quiet machine, the baseline analyzer bracketed by the optimised one so
that drift across the hour would show:

| Run | Order | with | without | overhead | 95 % CI | A/A scatter | FlowX's cost |
|---|---|---:|---:|---:|---|---:|---:|
| after | 1st | 13 478 ms | 7 744 ms | +74.3 % | [+72.2, +76.2] | 3.9 % | +5 734 ms |
| **before** | 2nd | 13 064 ms | 7 408 ms | **+77.1 %** | [+72.0, +80.6] | 5.0 % | +5 656 ms |
| after | 3rd | 13 113 ms | 7 627 ms | +71.8 % | [+67.0, +73.4] | 5.4 % | +5 486 ms |

The two *identical* runs differ by 248 ms; before and after differ by 46 ms. **The effect is
smaller than the harness's own scatter, and this table is not evidence that the change
helped end to end.** Two reasons, both foreseeable in hindsight:

1. **It was never big enough.** After the generator's growth, 4.7 of 33.6 ms per flow is
   14 % of FlowX's compile-time cost, not the 37 % §5 recorded. Removing 89 % of 14 % is
   ~4 ms per flow against a total of ~28 ms of wall clock per flow.
2. **Analyzer time is not wall-clock time.** These are per-analyzer execution times summed
   across threads. Analyzers run concurrently with each other and with the compile; the
   generator, which is the critical path, does not. Thread-time removed from a path that is
   not critical need not shorten the build at all.

**What it does buy** is the IDE, where FLOWX1020 re-runs on the keystroke that reorders two
steps and where 4.1 ms per flow of redundant binding is paid per edit rather than per build
— and a rule that no longer duplicates the compiler's work. That is worth having on its own
terms. It is not worth reporting as progress against the +8 % budget.

**Where the budget actually stands.** With the analyzer at 0.53 ms per flow, the split is
`FlowPlanGenerator` **97.6 %**, `StepBindingAnalyzer` 1.8 %, `CapabilityAnalyzer` 0.5 %,
`PredicatePurityAnalyzer` 0.1 %. Every remaining route to the criterion runs through the
generator, and the first question for it is not "how do we make it faster" but **"what made
it 3.6× slower, and was that intended?"**

### 5.2 WP-28 — the bisect, and the answer to §5.1's question

Recorded **2026-07-31**, same container. §5.1 ended by asking what made the generator 3.6×
dearer and whether it was intended. **One commit did, and it was intended — it is what the
`errors` catalogue costs.**

#### The method

`git log a75c1f0..dev --first-parent` is thirteen commits: seven merges carrying code
(WP-20 through WP-26), five documentation commits, and one analyzer performance change.
Every merge was probed, then the winning merge was opened and its two underlying commits
probed individually. A linear scan rather than a binary search: eight probes cost about ten
minutes, and a scan gives the per-commit contribution a bisect throws away.

**The probe is Roslyn's own `FlowPlanGenerator` execution time**, from §5's `/reportanalyzer`
command, at 25 and 50 flows, three builds per size, medians, marginal cost taken across the
pair. Not the harness: a full harness run per bisect step is half an hour, and a 3× effect
does not need that resolution. `scripts/generate-scale-project.py` is byte-identical across
the whole range, so every probe compiles the *same* synthetic project and only the compiler
under it changes.

**The probe was validated against the harness at both endpoints**, which is the check that
makes it usable. Eight rounds at 50 flows, wall clock, on a machine at load average 2.7–4.2:

| Tree | Harness, FlowX's total cost | Probe, `FlowPlanGenerator` alone |
|---|---:|---:|
| `a75c1f0` (§3's tree) | **+12.03 ms per flow**, +17.5 % [+11.7, +25.8] | 7.04 ms per flow |
| `dev` | **+33.26 ms per flow**, +52.9 % [+49.8, +55.9] | 22.9–25.4 ms per flow |

The harness's baseline row reproduces §3's 50-flow record (+13.1 % there, +17.5 % here,
overlapping intervals). The probe sits about 25 % below the harness at both ends, which is
expected — it times one component and the harness prices the whole of FlowX — and it tracks
the ratio, which is all a bisect needs.

#### The per-commit numbers

Marginal `FlowPlanGenerator` cost, 25 → 50 flows, median of three builds per size, with the
1-minute load average observed across each probe:

| Commit | | ms per flow | load |
|---|---|---:|---|
| `a75c1f0` | §3 and §5's tree | 7.04 | 1.9–2.4 |
| `1687072` | WP-22's merge base | 5.60 | 2.1–2.6 |
| **`c7ae70a`** | **WP-22 — triggers and capability error catalogues** | **27.28** | 2.3–2.6 |
| `2d108c7` | WP-22 merged, with the manifest sort-key fix | 27.52 | 3.5–4.2 |
| `eb19b31` | WP-21 merged — FLOWX1011 predicate purity | 25.84 | 3.1–4.1 |
| `4acbb04` | WP-20 merged — `Switch` / `Case` / `Default` | 26.16 | 2.3–2.9 |
| `e7042c3` | WP-24 merged — `Parallel` and FLOWX1013 | 26.00 | 1.6–2.2 |
| `ae35cd8` | `dev` | 22.92 | 2.2–2.5 |
| — | `dev`, with the catalogue read stubbed out | **7.36** | 2.5–3.0 |

**It is one commit, not a spread.** `c7ae70a` takes the generator from 5.60 to 27.28 ms per
flow — **+21.7, or 4.9×** — and nothing after it moves. The 22.9 to 27.5 range across the
post-WP-22 rows is session-to-session drift on this machine, not a trend: `dev` itself
measured 22.92 in one session and 25.4 in another, so nothing inside a ±15 % band is
readable, and every one of those rows is inside it.

**Three of the four suspects are exonerated by measurement.** `Switch`/`Case`,
`Parallel`/FLOWX1013 and the manifest sort-key fix each cost nothing detectable in the
generator. FLOWX1011 is an analyzer and, as expected, does not appear in the generator's
number at all — §5.1 already prices it at 0.03 ms per flow.

**And the last row is the control that closes it.** Stubbing `ErrorCatalogueReader.Read` to
return `null` — nothing else changed — returns the generator to **7.36 ms per flow**, the
pre-WP-22 figure to within the probe's noise. The regression is not merely correlated with
the error catalogue; it *is* the error catalogue.

#### The mechanism, instrumented rather than reasoned about

`ErrorCatalogueReader` was instrumented with counters around each phase and one build taken
at 50 flows — 262 capability types, which is what the synthetic project has:

| | calls | ms |
|---|---:|---:|
| `ErrorCatalogueReader.Read` | 262 | **1 229** |
| — `Compilation.GetSemanticModel` | 524 | 2 |
| — `Roots` (the walk) | 524 | 1 196 |
| — — `SemanticModel.GetTypeInfo` | 39 964 | **1 118** |
| — — `SemanticModel.GetSymbolInfo` | 1 572 | 13 |

Against a generator total of ~2 500 ms for that build. **The reader is half the generator,
and 93 % of the reader is one call.** `GetSymbolInfo` costs 13 ms for the same reason
§5.1's analyzer's later `.Step<T>()` lookups were free: by the time it is asked, the type
query has already bound everything it needs.

`Roots` walks every node of the capability's class declaration and asks each expression its
type, because an expression's type is the only thing that identifies a failure path.
55 790 nodes are visited to find **1 572** that are `Error`-typed — a hit rate of 4 %. Split
by syntax kind, the bill is not spread evenly:

| Node kind | `GetTypeInfo` calls | ms | of which `Error`-typed |
|---|---:|---:|---:|
| `InvocationExpression` | 4 268 | **717.6** | 1 048 |
| `IdentifierName` | 21 868 | 200.8 | 524 |
| `SimpleMemberAccessExpression` | 5 916 | 115.9 | 0 |
| `GenericName` | 1 796 | 63.1 | 0 |
| everything else (11 kinds) | 6 116 | 20.5 | 0 |

**64 % of the cost is asking an invocation its type**, at 0.17 ms each, because answering
that means resolving the overload and inferring its type arguments. There are about eight
invocations in a capability body — `ArgumentNullException.ThrowIfNull`, `ValueTask.FromResult`,
`Result.Fail<T>`, the store call, `ConfigureAwait`, the error factory — and every one of
them is bound to discover that six of them are not errors.

**This is not a bug, and there is no cheap way out of it.** The manifest's `errors` is
derived from the code rather than from a second hand-maintained declaration, which is the
right call and is argued at length in `c7ae70a`'s message. Deriving it means reading every
capability in the compilation, and reading it means binding it. **The generator's cost is
now a function of how much capability *implementation* exists, not of how many flows there
are** — 262 capability types at ~3 ms each, which the 50-flow project happens to express as
~22 ms per flow. The synthetic project's capability bodies are deliberately minimal
(`generate-scale-project.py` explains why); a real capability with real business logic in it
costs more, not less.

#### What was fixed, and what was left alone

One thing in that walk was waste rather than cost. `Roots` was spelled

```csharp
scope.DescendantNodes(node => !IsErrorExpression(node, model))
     .OfType<ExpressionSyntax>()
     .Where(node => IsErrorExpression(node, model))
```

which asks the same question about the same node twice — once to decide whether to descend
into it, once to decide whether to keep it. **20 762 of the 39 964 binds above are that
duplicate.** It is replaced by an explicit stack walk that visits the same nodes in the same
document order, yields the same list, and asks once.

Measured by alternating the two compiler builds against the same project — A/B/A/B, six
rounds, so drift across the run shows as scatter rather than as a difference between arms:

| | 25 flows | 50 flows | marginal |
|---|---:|---:|---:|
| before | 2.034 s | 2.670 s | **25.4 ms per flow** |
| after | 2.002 s | 2.511 s | **20.4 ms per flow** |

**A 20 % cut**, and the optimised build was faster in all six paired rounds at 50 flows.
All 905 tests pass unmodified and the build stays at 0 warnings; among those tests is the
ecommerce manifest baseline gate, so the document this reader produces is byte-identical.
**No emission behaviour changed.**

End to end it is at the edge of what the harness can see. Three 8-round runs at 50 flows,
back to back, the unchanged side bracketed:

| Run | Order | with | without | overhead | 95 % CI | A/A scatter | per flow |
|---|---|---:|---:|---:|---|---:|---:|
| before | 1st | 4 852 ms | 3 158 ms | +52.9 % | [+49.8, +55.9] | 6.1 % | +33.26 ms |
| **after** | 2nd | 4 806 ms | 3 248 ms | **+46.6 %** | [+42.8, +51.3] | 6.6 % | **+30.86 ms** |
| before | 3rd | 4 910 ms | 3 328 ms | +49.6 % | [+43.3, +54.4] | 8.4 % | +32.73 ms |

The two identical runs differ by 0.53 ms per flow and the change moves it by 2.14 — four
times the drift, in the right direction, with intervals that still overlap. **Believe the
paired A/B's 20 %, and read this table as consistent with it rather than as independent
confirmation of it.**

**Three larger cuts were considered and not made**, because each trades a fact for speed:

* **Skip binding in syntactic type-only positions** (base lists, attribute arguments). Worth
  perhaps 6 %, and the reasoning is delicate in the direction that fails silently: a
  parameter default of `default` on an `Error`-typed parameter *is* an `Error`-typed
  expression that the reader currently finds and correctly refuses to understand. Get the
  list of positions wrong and the catalogue starts claiming to be complete when it is not,
  which is the one failure mode the whole design of this reader exists to prevent.
* **Cache a factory's catalogue across capabilities.** The 262 capabilities here follow into
  the same two factory methods 262 times. A cache keyed on the compilation would fix that
  and is exactly the "caching something that can go stale" a generator must not do.
* **Make `errors` opt-in.** This would work and it changes what the generator emits, which
  makes it a product decision about ADR-0007's promise, not a performance fix.

#### Where the budget stands after this

At 50 flows on this machine, `dev` costs **+33.26 ms per flow** end to end and the fixed
tree **+30.86 ms**, against `a75c1f0`'s **+12.03 ms**. The criterion is **FAIL** either way:
+46.6 % [+42.8, +51.3] at 50 flows against a +8 % budget.

**P1's criterion is not closer.** A 20 % cut against a 4.9× regression leaves the generator
about 2.8× its pre-WP-22 cost, and that remainder is what the `errors` catalogue costs
rather than something left to optimise. The decision in front of P1 is therefore not a
profiling one. It is whether a manifest that enumerates every capability's failure modes is
worth roughly two thirds of FlowX's compile-time budget — and if it is, whether §8's
consequences should be rewritten around a budget that was set before that feature existed.

### 5.3 WP-31 — why nothing caught it, and the gate that now does

Recorded **2026-07-31**, same container. §5.2 found what the regression was. This is the
answer to a different question it raises and does not ask: **four working packages went by
and CI said nothing.** Full record in
[**generator-cost-gate.md**](generator-cost-gate.md); this is the summary and the part
that revises §8.

**The diagnosis is not "the job was advisory".** That was the first answer and it is wrong.
`scale-overhead` is advisory, but its exit code is real, its output is in the log, and its
step is shown as failed. Somebody reading it would have seen the same thing before
`c7ae70a` and after it: **FAIL, over budget.** The job compares the build against +8 % and
the build was at +18 % before the regression and +77 % after it. **A gate that is already
red carries no information about the commit under test.** Making it blocking would not have
helped either; it would have blocked every pull request in P1 equally, including the ones
that made things better.

**What was missing was a relative gate** — one that asks *did this commit make it worse
than the figure we last agreed on*, which has an answer on every commit whatever the
absolute number is. Two gates in this repository already work that way and say so in their
comments: the benchmark baseline, and the ecommerce manifest baseline. This is the third.

**It cannot be built on wall clock, and that is a measurement rather than an opinion.**
Twelve independent runs of an in-process probe on an *unchanged* tree — no MSBuild, no
restore, no compiler server, on a container at load 5.8 to 21.1:

| | 25 flows | 50 flows |
|---|---:|---:|
| Elapsed ms, worst disagreement between two identical runs | **+139 %** | **+166 %** |
| Elapsed ms, what `1687072` → `c7ae70a` actually produces | +77 % | +39 % |
| **Bytes allocated, worst disagreement between two identical runs** | **0.014 %** | **0.071 %** |
| **Bytes allocated, what `1687072` → `c7ae70a` produces** | **+102.1 %** | **+103.6 %** |

**The timing rows are the finding.** A threshold wide enough not to fire on an unchanged
tree is two to four times too wide to fire on the incident, with every source of noise a CI
runner adds already removed. No number of rounds fixes a signal smaller than the noise;
this harness already spends fifteen sandwiched rounds and an A/A control to reach a 3.9–8.7 %
floor, and that is still the wrong order of magnitude for a per-commit gate.

**So the gate counts allocations instead**, which is the split
[README.md §5](README.md#5-gate-design--and-a-claim-wp-3-got-wrong) already argues for at
run time — allocations are exact on shared hardware, timings are not — applied to compile
time. It is close to a direct measure of the thing §5.2 identified: the generator's cost is
semantic-model queries, answering one binds a statement, and binding allocates.

**The bisect replays through it, and reads better in two places.**

| Commit | §5.2, ms per flow | Bytes per flow | × vs `1687072` |
|---|---:|---:|---:|
| `a75c1f0` | 7.04 | 346 323 | 1.000× |
| `1687072` | 5.60 | 346 319 | 1.000× |
| **`c7ae70a`** | **27.28** | **710 013** | **2.050×** |
| `2d108c7` | 27.52 | 710 029 | 2.050× |
| `eb19b31` | 25.84 | 710 058 | 2.050× |
| `4acbb04` | 26.16 | 713 000 | 2.059× |
| `e7042c3` | 26.00 | 713 787 | 2.061× |
| `ae35cd8` | 22.92 | 713 789 | 2.061× |

Flat, one step at `c7ae70a`, flat again — §5.2's structure exactly. The two places it reads
better are both places §5.2 was careful to claim nothing:

* **The first two rows differ by 26 % there and by nothing here.** `1687072..a75c1f0` is
  four documentation and CI commits with **no change to `src/FlowX.Compiler` at all**, so
  the true difference is zero. §5.2 correctly declined to interpret 5.60 against 7.04,
  writing that nothing inside a ±15 % band is readable. This instrument reads them as equal
  to one part in 87 000.
* **"Cost nothing detectable" becomes a number.** `Switch`/`Case` costs **+0.41 %** and
  `Parallel`/FLOWX1013 costs **+0.11 %** — the same conclusion, stated sharply. That those
  are what real feature work in this generator costs is also the argument for where the
  threshold sits: **+2 %** is fifty times below the regression and five times above the
  dearest feature in the window.

**What it does not do.** It is a proxy: 2.05× in bytes where the clock says 4.87×, because
part of `c7ae70a`'s time is repeated binds that hit Roslyn's caches and allocate little. It
does not run MSBuild, the analyzers, or a real build, so it cannot see a regression that
lives in any of those. And **it says nothing whatever about the +8 % criterion**, which is
still measured by this document's harness in wall clock and is still failing.

**§8's third consequence — "re-measure the split whenever the criterion is quoted" — is
now partly mechanical.** It was written after §5.1 found a tripled generator by hand, and
its cost was six builds and somebody remembering. The relative gate is that check run
automatically on every pull request, for the generator. It does not cover
`StepBindingAnalyzer` or the other analyzers, which is the part of that consequence still
carried by a human.

### 5.4 WP-43 — the criterion re-measured, and a split with a different shape

Recorded **2026-07-31**, same container, **different hardware** — see below. §5.1 through
§5.3 were taken across two days during which the generator tripled, was bisected and was
gated. Since the last end-to-end number was recorded, WP-28 removed a duplicated semantic
bind, **WP-37 rewrote the error-catalogue reader outright**, WP-27's analyzer cut landed,
and `Switch`, `Parallel`, `ForEach`, `SubFlow`, `Fail`, the `FlowContext<TIn>` view and
five new analyzers all shipped. Nobody knew the current figure, and
[ADR-0014](../adr/ADR-0014-derived-error-catalogue-vs-build-budget.md) was quoting numbers
taken before two of the three optimisations.

#### The criterion

```bash
./scripts/measure-scale-overhead.sh --rounds 12 --sizes 50,200
```

```
 flows       with    without   overhead             95 % CI   A/A scatter
    50     3986 ms     2758 ms     +46.5 %      [+42.4, +51.0]        14.3 %
   200    11980 ms     7236 ms     +67.1 %      [+61.9, +73.6]        10.6 %

VERDICT: FAIL — 95 % CI [+61.9, +73.6] % lies above the 8 % budget
```

| Flows | with-arm IQR | without-arm IQR | FlowX's cost | Per flow |
|---:|---:|---:|---:|---:|
| 50 | 8.3 % | 10.5 % | +1 296 ms (CI [+1 138, +1 401]) | 25.91 ms |
| 200 | 7.3 % | 7.5 % | **+4 821 ms** (CI [+4 482, +5 199]) | **24.11 ms** |

Growth `flows^0.95`, 95 % CI [0.87, 1.06]; affine fit **121 ms fixed + 23.50 ms per flow**,
R² 1.000. **Still linear, still no meaningful fixed term** — §4's finding survives four more
working packages and a change of machine, which is the one piece of good news here.

**The criterion is failed by 59 points.** It is not close. Nothing in this section should be
read as progress towards +8 %.

#### The machine, and why this run is worth more than its predecessors

| Run | Load: min / median / max | Within-arm IQR at 200 | Verdict |
|---|---|---:|---|
| First attempt, 12 rounds | 6.81 / **15.52** / 38.96 | 44.8 % / 57.5 % | **INCONCLUSIVE** — over the 25 % limit |
| Cross-check, 8 rounds, server off | 7.31 / **17.13** / 37.08 | 6.6 % / 6.0 % *(CPU)* | **FAIL** at +23.4 % CPU [+15.2, +24.9] |
| **Recorded, 12 rounds** | 1.87 / **2.88** / 3.76 | **7.3 % / 7.5 %** | **FAIL** at +67.1 % |

Three other agents were building in sibling worktrees for most of this session, and the
first attempt is what that costs: a 4× swing between identical 200-flow builds (7 144 ms to
29 088 ms) and an A/A control reporting **±77 %** of apparent overhead where the true
difference is zero. **The harness refused, which is the correct output and is why it has an
exit code for it.** The recorded run was taken after those agents went idle.

The middle row is §4's load-robust configuration, run while the machine was still loud. Its
wall-clock half was inconclusive and its CPU half was not: **CPU time is the instrument that
survives a contended box**, with 6 % IQRs against wall clock's 45–58 % in the same
conditions. It may report a fail and the harness will not let it report a pass; it reported
a fail. Its per-flow cost — **22.95 ms per flow** from the affine fit — agrees with the
quiet run's 23.50 to within 2.4 %, which is the strongest cross-check in this document.

**The hardware is not the hardware §3 to §5.3 were taken on.** `/proc/cpuinfo` reads
`Intel(R) Xeon(R) Processor @ 2.10GHz`; every earlier section in this document was taken at
**2.80 GHz**. Ratios are comparatively portable because both arms move together; absolute
milliseconds are not. **Any comparison below between a figure recorded here and one recorded
earlier is a comparison of ratios, and where a per-flow millisecond figure is compared the
change of machine is named.**

#### What moved, and what cannot be attributed

| | Recorded in | Then | Now | |
|---|---|---:|---:|---|
| 50 flows | §5.2, post-WP-28 | +46.6 % [+42.8, +51.3] | **+46.5 % [+42.4, +51.0]** | unchanged |
| 200 flows | §5.1, pre-WP-28 | +77.1 % [+72.0, +80.6] | **+67.1 % [+61.9, +73.6]** | −10 points |

**The 50-flow row is the finding.** It reproduces §5.2 to **0.1 points** — on different
hardware, four working packages later. Between those two runs the error-catalogue reader was
rewritten from scratch (WP-37), five analyzers were added, and four of the five DSL shapes
landed. **The net effect on the criterion is zero to the resolution of this harness.** Read
plainly: WP-37's saving is real and is roughly the size of what the new features cost, and
the two cancelled. B13 §6.5 measures WP-37 alone at **−5.7 %** of total generator
allocation at 50 flows, which is the right order of magnitude for a cancellation of this
kind and is the only figure here anyone should quote for that change on its own.

**The 200-flow row spans WP-28 as well**, so its 10 points are consistent with WP-28's
documented 20 % cut of the reader plus WP-37, less what the features added. **That is a
consistency argument, not an attribution. This package did not bisect**, and the two runs
being compared were taken on different CPUs, so a per-commit reading of those 10 points is
not available from this evidence. §5.2's method — generate the subject once, probe each
commit — is what would produce one.

**Neither row is progress towards the budget.** −10 points against a 59-point miss does not
change what has to happen, and §5.2's conclusion stands unaltered: the remaining cost is
what the derived `errors` catalogue costs, and the decision in front of P1 is a product
decision rather than a profiling one.

#### The split, re-measured

The §5 command at 25 and 200 flows, three builds each, medians, load 1.46 to 1.94.

| Component | ms per flow | Share | §5 recorded |
|---|---:|---:|---|
| **`FlowPlanGenerator`** | **23.25** | **90.5 %** | 8.07 ms, 62 % |
| `PredicatePurityAnalyzer` (FLOWX1011) | 0.90 | 3.5 % | not yet present |
| `DeadlineCoherenceAnalyzer` (FLOWX1019) | 0.70 | 2.7 % | not yet present |
| `CapabilityThrowAnalyzer` (FLOWX1016) | 0.46 | 1.8 % | not yet present |
| `StepBindingAnalyzer` (FLOWX1020) | 0.30 | 1.2 % | 4.77 ms, **37 %** |
| `CapabilityAnalyzer` (FLOWX1003/4) | 0.06 | 0.2 % | 0.15 ms, 1 % |
| `SubFlowCycleAnalyzer` (FLOWX1021) | 0.02 | 0.1 % | not yet present |
| `ParallelSlotAnalyzer` (FLOWX1013) | 0.01 | < 0.1 % | not yet present |
| `TriggerDeclarationAnalyzer` (FLOWX1025) | ~0 | ~0 % | not yet present |
| **Total** | **25.70** | | |

**25.70 ms per flow against the wall clock's 23.50.** §5 recorded a 16 % gap between these
two instruments and explained it by concurrency; the gap is now 9 %. Two instruments that
count overlapping work differently agreeing this closely is the reason to believe either.

Three things follow, and the first is the only one that is firm.

**`FlowPlanGenerator` is now 90.5 % of the marginal cost, and every route to the budget runs
through it.** This is not a close call and does not depend on the arithmetic below it: in
all six builds, at both sizes, the generator was 74–82 % of FlowX's *total* execution time
before any marginal is taken. §5.1's 97.6 % was measured when three of these analyzers did
not exist; the generator's share has fallen slightly because analyzers were added, not
because the generator got cheaper.

**`StepBindingAnalyzer` is no longer the second component, and WP-27 is why.** §5 put it at
37 % of the marginal cost and 4.77 ms per flow; it is now 1.2 % and 0.30 ms per flow, on a
machine 25 % slower in nominal clock. That is WP-27's 89 % cut showing up end to end in the
split, four working packages after it landed — and it remains, as §5.1 said at the time,
invisible in the criterion, because 4 ms per flow off a 26 ms bill does not move a 59-point
miss.

**The analyzers that replaced it at the top are new, and their individual figures are soft.**
`PredicatePurityAnalyzer`, `DeadlineCoherenceAnalyzer` and `CapabilityThrowAnalyzer` are
collectively **8 %** of the marginal cost. Their per-component build-to-build spread is
comparable to the marginal being extracted from it — `DeadlineCoherenceAnalyzer` read 0.156,
0.192 and 0.487 s across three builds at 200 flows — so **their ordering among themselves is
not established here** and should not be quoted as a ranking. The 8 % total is safe; the
2.7-versus-3.5 is not. Separating them needs the per-phase instrumentation §5.1 used on
`StepBindingAnalyzer`, not `/reportanalyzer`.

#### Does the wall-clock harness measure a subject that binds?

`scripts/generator-cost-probe/Program.cs` was, until `31e876f`, building its own
`CSharpCompilation` out of parsed syntax trees and never supplying the global usings that
`<ImplicitUsings>enable</ImplicitUsings>` produces, so the subject it measured did not bind
and anything in the generator that resolves a signature walked away early. **This harness
does not have that defect, and the reason is structural rather than lucky.**

It does not construct a compilation. It runs `dotnet build` on a real project, so the SDK
generates `GlobalUsings.g.cs` from that same property and passes it to `csc` like any other
source file. On top of that it cannot silently proceed on an unbindable subject, because
three checks run before any round is timed and each of them fails on one:

1. the untimed setup build must succeed, or the run aborts with exit 3;
2. the generated file count must be **exactly `flows + 1`** — one plan per flow plus the
   manifest — and a flow that failed to analyse emits nothing;
3. both arms must produce a **byte-identical-sized assembly**, which a control arm compiling
   fewer generated sources cannot.

All three passed at both sizes in all three runs of this section: **51 files and a 1 358 848
byte assembly at 50 flows, 201 files and 5 551 104 bytes at 200.** Check (2) is the one that
would have caught the probe's defect, and it is the check the probe had no equivalent of.

**The two instruments do not disagree.** Run on the same tree in the same session, the
relative gate reports the generator within **+0.20 %** of its committed baseline — unchanged
— and this harness reports the 50-flow criterion within **0.1 points** of §5.2 — unchanged.
They agree, by different means, that nothing since WP-28 has moved the generator's cost. The
probe's *elapsed* column disagreed with itself by **51.5 %** and **56.0 %** between its own
repeats in that same run, which is §5.3's argument for gating allocations rather than time,
restated by accident.

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

**§5.1 discharges half of (1) and rewrites the other half.** `StepBindingAnalyzer` is
profiled and cut from 4.70 to 0.53 ms per flow, and the profiling method — instrument the
component, then cross-check every answer the faster route gives against the slower one —
transfers directly. What it rewrites is the arithmetic: "roughly a 2.3× improvement across
the two" was computed against a generator costing 8 ms per flow, and it now costs 28.7.
A 2.3× improvement no longer reaches the budget from anywhere, and the analyzer half of
that sum is spent. Consequence (1) is now **one** item — the generator — and its first
question is why it grew, not how to shrink it.

**A third consequence, which §5.1 adds.** *Re-measure the split whenever the criterion is
quoted.* This document's headline survived four working packages and stopped being true
during them, and nothing noticed, because the split was recorded once and cited thereafter.
The re-measurement that caught it costs six builds.

The CI job (`.github/workflows/performance.yml`, `scale-overhead`) **stays advisory**
(`continue-on-error: true`). This is P1's *exit* criterion; making it blocking today would
red every pull request for the length of the phase over a defect none of them introduced —
including the pull requests that fix it — and a red gate people learn to ignore is worse
than no gate. The script's exit code is real, `pipefail` is set, and the step is reported as
failed. **Remove `continue-on-error` the moment this document records a pass**, and when
that happens keep exit code 2 non-blocking: `INCONCLUSIVE` means the runner could not
resolve the question, and failing a pull request for that fails it for the weather. The
workflow comment spells out the exact blocking form.

**§5.3 adds a second job next to it, `generator-cost`, and that one is blocking.** The two
are not alternatives and neither substitutes for the other:

| Job | Asks | Metric | Class |
|---|---|---|---|
| `generator-cost` | did *this commit* make the generator dearer than the committed figure | bytes allocated | **blocking** |
| `scale-overhead` | is the build within the +8 % budget | wall clock, end to end | advisory, until it can pass |

The reasoning above for keeping `scale-overhead` advisory is unchanged and is *why* the
relative gate had to exist: a criterion that cannot be enforced for the length of a phase
leaves that phase with no gate at all unless something else is enforceable in the meantime.
**What is no longer true is the implication that nothing could be blocking until the budget
is met.** Something could, and now is.

**§5.4 discharges the third consequence for the second time, and narrows the first to one
component.** Re-measuring the split found what it was written to find: `StepBindingAnalyzer`
has fallen from 37 % to 1.2 % and three analyzers that did not exist when §5 was written now
sit above it. Consequence (1) is now a single item with no ambiguity about which — the
generator is **90.5 %** of the marginal cost, and the other eight components together are
9.5 %, so **optimising all of them perfectly would leave the criterion failing by 54
points.** §5.4 also confirms the criterion is unchanged since §5.2 at 50 flows, which means
`scale-overhead` stays advisory on the same reasoning as before: it has not recorded a pass,
and the condition for removing `continue-on-error` is unmet.

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

# The criterion alone, faster. This is what section 5.4 ran.
./scripts/measure-scale-overhead.sh --rounds 12 --sizes 50,200

# The RELATIVE gate (section 5.3). About a minute, and it is the one that runs on every
# pull request. It does not measure the criterion above and does not claim to.
./scripts/measure-generator-cost.py --json /tmp/cost.json
./scripts/check-generator-cost.py /tmp/cost.json

# Keep the raw per-build samples, and re-read the statistics without re-measuring.
./scripts/measure-scale-overhead.sh --rounds 15 --json /tmp/scale.json
./scripts/analyse-scale-samples.py /tmp/scale.json --budget 8

# The load-robust cross-check in section 4: CPU time rather than wall clock. Its RATIO
# is distorted by the cold Roslyn start it forces on both arms, so it may report a fail
# but the harness will not let it report a pass. Its per-size cost in milliseconds is
# correct, because a constant in both arms cancels in the difference.
#
# Section 5.4 adds a second use for it: when the machine is loud — three concurrent builds
# elsewhere on the box will do it — wall clock returns INCONCLUSIVE and this still answers,
# because CPU time holds a 6 % IQR where wall clock spreads to 58 % in the same rounds.
./scripts/measure-scale-overhead.sh --no-compiler-server --rounds 8 --sizes 25,50,100,200

# Where the cost goes (§5). Run this before quoting the split — §5.1 is the record of
# what happens when it is not: two of its three components moved by under 1.5 % over four
# working packages and the third tripled.
./scripts/generate-scale-project.py --flows 200 --out /tmp/scale-200
dotnet build /tmp/scale-200/ScaleSynthetic.csproj -c Release --no-incremental \
  /p:ReportAnalyzer=true /p:UseSharedCompilation=false -v d | grep -A20 'Total generator'

# The criterion alone against a specific change (§5.1): run the harness once per side of
# it, back to back, with the unchanged side bracketed so drift across the hour shows.
# Comparing a run made today against a figure recorded in this document is not a
# before-and-after, as §5.1's control arm — 2 841 ms faster than §3's for no reason but a
# quieter machine — demonstrates.
./scripts/measure-scale-overhead.sh --rounds 12 --sizes 200 --json /tmp/after.json

# Bisecting the generator across a range of commits (§5.2). The generated project is the
# subject and must not move with the commit under test, so generate it ONCE, from a fixed
# checkout, and point it at the worktree the commits are checked out in. Then per commit:
# build the four projects the synthetic one consumes, and read the generator's own time.
# Three builds per size, median, marginal across 25 -> 50. Validate the probe against the
# harness at both ends of the range before trusting a single step of it.
./scripts/generate-scale-project.py --flows 25 --out /tmp/scale-25 --repo /path/to/worktree
./scripts/generate-scale-project.py --flows 50 --out /tmp/scale-50 --repo /path/to/worktree
dotnet build /tmp/scale-50/ScaleSynthetic.csproj -c Release --no-restore --no-dependencies \
  --no-incremental /p:ReportAnalyzer=true /p:UseSharedCompilation=false -v d \
  | grep -A20 'Total generator'
```

Exit codes: **0** PASS, **1** FAIL, **2** INCONCLUSIVE, **3** the measurement itself broke.
The third one is the point of this package.

---

**See also:** [B12 at one flow](B12.md) · [ADR-0002](../adr/ADR-0002-compile-time-orchestration.md) ·
[Benchmark harness](README.md) · [Roadmap P1](../20-Roadmap.md) · [Plan](../../PLAN.md)

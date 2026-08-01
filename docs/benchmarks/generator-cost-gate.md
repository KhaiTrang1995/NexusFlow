# The generator cost gate — a relative gate that works while the absolute one is failing

> **Verdict: a blocking gate exists, and it would have caught the incident.** Replaying
> [B12-scale.md §5.2](B12-scale.md#52-wp-28--the-bisect-and-the-answer-to-51s-question)'s
> bisect through it, the commit that took `FlowPlanGenerator` from 5.60 to 27.28 ms per
> flow fails the gate at **+102.1 %** and **+103.6 %** against a **+2 %** threshold.
>
> **It does not gate wall clock, because wall clock cannot do this.** On the same machine,
> in the same process, with MSBuild taken out of the picture entirely, twelve identical
> runs disagreed with each other by **+139 %** while the real 4.9× regression showed up as
> **+77 %**. A timing gate wide enough not to fire on nothing would not have fired on this.
>
> It gates **bytes allocated by one run of the generator**, which over those same twelve
> runs moved by **0.069 %** — a signal-to-noise ratio of about **1 500 : 1**.
>
> **P1's absolute criterion is untouched and still failing**, and
> `scripts/check-generator-cost.py` reprints it on every run, including passing ones.
>
> **Recorded:** 2026-07-31 · **WP-31** · `./scripts/measure-generator-cost.py`

---

## 1. Why an absolute gate was not enough, and this is not hypothetical

[20-Roadmap §3](../20-Roadmap.md) sets P1's exit criterion: *a 200-flow synthetic solution
builds with ≤ 8 % overhead*. [B12-scale.md](B12-scale.md) measures it, and it has been
failing since it was first measured. The CI job that measures it,
`scale-overhead` in [`.github/workflows/performance.yml`](../../.github/workflows/performance.yml),
is therefore advisory, and the reasoning written into it at the time was sound:

> Making the job blocking today would red every pull request for the whole of P1 over a
> defect none of them introduced, which trains people to ignore a red gate.

Then [§5.2](B12-scale.md#52-wp-28--the-bisect-and-the-answer-to-51s-question) happened.
One commit — `c7ae70a`, WP-22's capability error catalogues — took the generator from
**5.60 to 27.28 ms per flow, 4.9×**. It merged. Three further working packages merged on
top of it. It was found days later, by accident, when someone re-measured the split for an
unrelated reason.

**The gate was not silent because it was advisory. It was silent because it was
absolute.** An absolute gate compares a measurement to a budget. When the measurement is
+18 % and the budget is +8 %, that gate says "over budget" before the regression and "over
budget" after it. It carries no information about the commit under test. Multiplying the
generator's cost by five did not change a single character of its output.

**What was needed was a relative gate**: not *is this good enough*, but *did this change
make it worse than the last figure we agreed on*. That question has an answer on every
commit, including every commit made while the absolute criterion is failing — and
especially those, because a phase spent optimising is exactly when a regression is easiest
to mistake for the status quo.

Two gates in this repository already work that way, and both give the same reason for it:

* `docs/benchmarks/baseline.json` with `scripts/check-benchmark-budgets.py`, for run time.
* `samples/ecommerce/flowx.manifest.baseline.json` with `flowx diff`, for the manifest
  contract. Its comment in [`ci.yml`](../../.github/workflows/ci.yml) puts the argument
  plainly: *"The baseline is committed on purpose. A contract change then arrives as a
  reviewable diff … rather than as a number in a log."*

This is the third instance of that idiom, not a fourth idiom.

---

## 2. What is measured, and why it is not milliseconds

**The gated quantity is the number of bytes allocated by one call to
`RunGeneratorsAndUpdateCompilation`, with `FlowPlanGenerator` as the only generator, over a
freshly created `CSharpCompilation` of a synthetic project of N flows.**

That is not an indirect measure of the generator's work; it is close to a direct one.
§5.2 established that the generator's cost *is* semantic-model queries — 93 % of the
error-catalogue reader is `SemanticModel.GetTypeInfo`, and what makes that call expensive
is that answering it binds a statement. Binding allocates. A generator that asks Roslyn to
bind twice as much code allocates about twice as much memory, and it does so identically
on a busy machine and an idle one.

This follows the split [`README.md` §5](README.md#5-gate-design--and-a-claim-wp-3-got-wrong)
already argues for and applies to run time, where WP-3's claim that timing ratios were
machine-independent was withdrawn against evidence:

| Check | Class | Why |
|---|---|---|
| **Allocations** | **blocking** | Deterministic and machine-independent. This is the real gate. |
| Absolute timing drift | advisory | Reported for a human. Not reliable here. |

The same sentence is true of compile time, and this gate is that sentence applied to it.
Elapsed milliseconds are measured and printed by the probe anyway — so that a human can
see whether the two metrics agree in direction, and so that a hypothetical regression
which costs time without costing allocations is at least visible — and they are advisory.

### 2.1 What the gate is *not* allowed to conclude

**Bytes are a proxy for cost, not a conversion of it.** Across the bisect below, the byte
figure moves by **2.05×** where §5.2's `/reportanalyzer` timing moves by **4.87×**. The
proxy is monotone with the real cost, it is enormously above its own noise, and it fires
decisively — but a +2 % byte threshold is not a +2 % millisecond threshold, and this
document does not claim it is. Part of the time cost of `c7ae70a` is repeated binds that
hit Roslyn's caches and allocate very little; that part shows up in the clock and not
here.

**Nothing about this gate says the build is fast enough.** It answers one question, and
the question it answers is not P1's criterion. See §7.

---

## 3. The noise floor, measured rather than assumed

Twelve independent runs of `./scripts/measure-generator-cost.py` on an unchanged tree —
the A/A control this measurement's ancestor
([§2 of B12-scale.md](B12-scale.md#2-the-harness-and-why-the-first-version-of-it-could-not-be-believed))
argues nothing else here is as useful as. Each run is a separate process; each reports the
median of 5 measured runs after 2 discarded warm-ups.

The machine is the container [P0.md §5](P0.md#5-is-shared-hardware-good-enough-to-decide-on)
documents: 4 logical cores, Intel Xeon, Ubuntu 24.04.4, .NET SDK 10.0.110. **Three sibling
worktrees were building and testing throughout**, which is why the 1-minute load average
spans 5.8 to 21.1 — considerably worse than the 2.6 to 12.6 B12-scale.md's recorded verdict
was taken under, and worse than a hosted runner is likely to be.

| run | load | 25 flows, bytes | 50 flows, bytes | 25 flows, ms | 50 flows, ms |
|---:|---:|---:|---:|---:|---:|
| 1 | 8.15 | 17 179 680 | 34 350 888 | 399 | 993 |
| 2 | 9.27 | 17 178 712 | 34 327 480 | 435 | 712 |
| 3 | 9.91 | 17 178 688 | 34 351 280 | 372 | 593 |
| 4 | 8.25 | 17 179 288 | 34 351 760 | 449 | 646 |
| 5 | 6.80 | 17 179 512 | 34 351 392 | 369 | 823 |
| 6 | 5.83 | 17 178 856 | 34 351 368 | 523 | 620 |
| 7 | 7.36 | 17 179 232 | 34 351 192 | 469 | 789 |
| 8 | 10.50 | 17 179 552 | 34 351 744 | 462 | 661 |
| 9 | 13.00 | 17 177 296 | 34 349 560 | 546 | 931 |
| 10 | 16.75 | 17 178 648 | 34 350 224 | 397 | 1 027 |
| 11 | 17.79 | 17 179 136 | 34 349 720 | 476 | 1 103 |
| 12 | 21.06 | 17 178 480 | 34 351 144 | 881 | 1 580 |

|  | 25 flows | 50 flows |
|---|---:|---:|
| **Bytes — full range across the twelve** | **0.014 %** | **0.071 %** |
| Bytes — worst deviation from the median | 0.010 % | 0.069 % |
| Bytes — standard deviation | 0.004 % | 0.019 % |
| Bytes — worst single un-medianed sample | 0.576 % | 0.293 % |
| **Elapsed ms — full range across the twelve** | **+139 %** | **+166 %** |
| Correlation with 1-minute load, bytes | −0.40 | +0.07 |
| Correlation with 1-minute load, milliseconds | **+0.61** | **+0.83** |

**These are the same twelve runs.** The two rows in bold are the whole design argument:
under identical conditions, at the same instant, on the same machine, the clock disagreed
with itself by a factor of 2.7 and the byte count agreed with itself to seven parts in ten
thousand. The load correlations say why — wall clock tracks how busy the box is, and the
byte count has no relationship with it worth reporting in either direction.

### 3.1 The counterfactual, which is the part worth reading

The obvious cheaper design is to keep this probe — in-process, no MSBuild, no restore, no
compiler server, no second arm — and gate its **elapsed time** rather than its
allocations. That design does not work, and the table above already contains the proof:

| | 25 flows | 50 flows |
|---|---:|---:|
| Worst false signal a no-op commit could produce (A/A range) | **+139 %** | **+166 %** |
| What the real 4.9× regression produces (`1687072` → `c7ae70a`) | **+77 %** | **+39 %** |

**A timing threshold wide enough not to fire on an unchanged tree is too wide to fire on
the incident this package exists to catch.** Not marginally — by a factor of two to four.
This is with every source of noise a CI runner adds already removed. Adding MSBuild, a
cold restore and a shared runner back in makes it worse, not better.

So the honest form of the finding the brief asked for is: **wall-clock timing cannot gate
generator cost per commit, on this hardware or on shared CI, and no amount of rounds fixes
it** — the harness in `measure-scale-overhead.sh` already spends fifteen sandwiched rounds
and an A/A control to get its floor down to 3.9–8.7 %, and that is still the wrong order of
magnitude for a per-commit gate that ought to notice a 20 % change. The deterministic
proxy is not a compromise forced by a lack of time to tune a threshold. It is the only one
of the two that works.

The same three metrics for the bytes column, for comparison:

| | 25 flows | 50 flows |
|---|---:|---:|
| Worst false signal (A/A range) | 0.014 % | 0.071 % |
| The real regression | +102.1 % | +103.6 % |
| **Signal to noise** | **7 360 : 1** | **1 464 : 1** |

---

## 4. The threshold, and where each end of it comes from

**+2 %**, recorded in `tolerances.regressionPercent` in
[`generator-cost-baseline.json`](generator-cost-baseline.json) so that changing it is
itself a reviewable diff.

**The lower bound — it must survive the noise.** The gated statistic is the median of five
runs, whose worst observed deviation is 0.069 %. 2 % is **29×** that. It is also **3.5×**
the worst deviation of any *single* un-medianed sample (0.576 %), so a degenerate run whose
median lands on a worst-case sample still passes. Confirmation rather than argument: all
twelve A/A runs above were put through `check-generator-cost.py` against the committed
baseline and all twelve returned PASS.

**The upper bound — it must catch a 4.9×.** The bisected commit produces +102 %. The
threshold is **1/50** of that, so the gate catches a regression fifty times smaller than
the one that got through.

**The middle — it must not fire on ordinary work.** This is the constraint a threshold
picked from the noise floor alone would get wrong, and the bisect happens to price it.
Two substantial compiler features landed in the same window as the regression:

| Change | Compiler diff | Cost, bytes per flow |
|---|---|---:|
| WP-20 — `Switch` / `Case` / `Default` (`eb19b31` → `4acbb04`) | 635 insertions | **+0.41 %** |
| WP-24 — `Parallel` and FLOWX1013 (`4acbb04` → `e7042c3`) | 1 362 insertions | **+0.11 %** |
| WP-22 — capability error catalogues (`1687072` → `c7ae70a`) | — | **+105 %** |

**Real feature work in this generator costs a few tenths of a percent.** 2 % leaves room
for several such features between re-recordings while sitting fifty times below the thing
it is there to stop. Both numbers above are also below what §5.2's timing probe could
resolve at all — it recorded both as "nothing detectable" inside a ±15 % band — which is a
second, smaller demonstration of the same point as §3.1.

**What is *not* a justification for the threshold**: any argument from the noise of a
hosted runner, because that has not been measured. See §6.

---

## 5. Validating it against the actual regressing commit

§5.2's bisect, replayed through this instrument. Each commit was extracted with
`git archive`, its `FlowX.Compiler` and `FlowX.Runtime` built, and the probe pointed at the
result; the **subject is byte-identical across every row**, because
`scripts/generate-scale-project.py` is unchanged over the whole range and its output is
hashed into every measurement.

| Commit | | §5.2, ms per flow | Here, bytes per flow | × vs `1687072` |
|---|---|---:|---:|---:|
| `a75c1f0` | §3 and §5's tree | 7.04 | 346 323 | 1.000× |
| `1687072` | WP-22's merge base | 5.60 | 346 319 | 1.000× |
| **`c7ae70a`** | **WP-22 — error catalogues** | **27.28** | **710 013** | **2.050×** |
| `2d108c7` | manifest sort-key fix | 27.52 | 710 029 | 2.050× |
| `eb19b31` | WP-21 — FLOWX1011 | 25.84 | 710 058 | 2.050× |
| `4acbb04` | WP-20 — `Switch`/`Case` | 26.16 | 713 000 | 2.059× |
| `e7042c3` | WP-24 — `Parallel` | 26.00 | 713 787 | 2.061× |
| `ae35cd8` | `dev` at §5.1 | 22.92 | 713 789 | 2.061× |

**The structure of §5.2's finding reproduces exactly**: flat, one step at `c7ae70a`, flat
again. Nothing else moves.

**And two rows read better here than they did there.** §5.2's first two rows differ by
26 % — 5.60 against 7.04 — and §5.2 correctly refused to interpret that, writing that
"nothing inside a ±15 % band is readable" on that instrument. `1687072..a75c1f0` is **four
documentation and CI commits with no change to `src/FlowX.Compiler` at all**, so the true
difference is zero. This instrument reads 346 319 against 346 323: **one part in 87 000**,
which is zero. The 26 % was the instrument, and this one does not have it.

The same resolution shows at the other end. §5.2 could say only that `Switch`/`Case` and
`Parallel` "cost nothing detectable"; here they cost +0.41 % and +0.11 %, which is both
a sharper statement and the same conclusion.

### The gate, run on the incident

Baseline recorded at `1687072`, candidate measured at `c7ae70a` — the pull request as it
actually was:

```
  flows      allocated B       baseline B     drift    spread    elapsed
------------------------------------------------------------------------
     25       17,751,720        8,781,592  +102.15%    0.018%       403ms
     50       35,502,040       17,439,576  +103.57%    0.123%       656ms

::notice::advisory — 25 flows: emitted source changed by +19,078 characters
::notice::advisory — 50 flows: emitted source changed by +38,408 characters

::error::25 flows: FlowPlanGenerator allocated 17,751,720 B, +102.15% against the
         committed 8,781,592 B (threshold +2.0%). 2.02x the baseline.
::error::50 flows: FlowPlanGenerator allocated 35,502,040 B, +103.57% against the
         committed 17,439,576 B (threshold +2.0%). 2.04x the baseline.

VERDICT: FAIL — 2 blocking gate failure(s).
```

**So the answer to "would this have caught it" is yes, demonstrated rather than argued**,
at fifty times the threshold and roughly fifteen hundred times the noise.

The advisory line about emitted characters is worth keeping in view. `c7ae70a` is not a
defect — §5.2 argues at length that deriving `errors` from the code is the right design and
that this is what it costs — and the gate is not built to say otherwise. Its job is to make
the cost arrive *on the commit that spends it*, attached to the diff that explains it,
where a reviewer can price it against what it buys. The response to a legitimate failure
is a one-line re-record of the baseline in the same pull request, and that line is then the
record that somebody decided.

---

## 6. What has not been established, and what would settle it

**The baseline is recorded on this container and has never been recorded on a hosted
runner.** Everything in §3 says the number does not depend on how *busy* a machine is.
Nothing in §3 says it does not depend on *which* machine, and there are mechanisms by which
it could: Roslyn sizes some pools from `Environment.ProcessorCount`, and a different .NET
patch level could shift a few allocations. This container and `ubuntu-latest` are both
4-core, so the most likely such mechanism is neutralised by luck rather than by design.

**This is the one assumption in the design that is untested, and it is stated rather than
buried.** Two things make it safe to ship anyway:

1. **The failure mode is loud and immediate.** A systematic cross-machine offset shows up
   on the very first CI run of the gate, as a drift figure identical on both sizes and
   unrelated to any code change. It cannot degrade quietly.
2. **The remedy is already wired in.** The `generator-cost` job uploads a
   `generator-cost-baseline.candidate.json` artifact on every run, recorded from that
   runner. If the runner disagrees, the fix is to download that file and commit it — **the
   baseline is re-recorded where the gate runs**.

**Widening the threshold is the wrong response and is called out here so that nobody
reaches for it.** A cross-machine offset is a constant, and the correct treatment of a
constant is to record it, not to hide it inside a tolerance that then has to absorb real
regressions too.

Two further things are deliberately not claimed:

* **The probe is not the build.** It runs the generator in-process against a compilation it
  constructs itself. It does not run the analyzers, MSBuild, restore, or the compiler
  server, and it therefore cannot see a regression that lives in any of those. Nothing here
  supersedes `measure-scale-overhead.sh`; the two answer different questions and both are
  in CI.
* **Two sizes, 25 and 50 flows.** Enough to take a marginal and confirm the cost is
  proportional to flow count (687 178 and 687 032 bytes per flow at the two sizes, marginal
  686 886 — agreeing to 0.05 %). Not enough to say anything about 200 or 2 000, and it is
  not asked to: this gate compares a tree against a committed figure at fixed sizes, and
  the shape question belongs to B12-scale.md §4.

---

## 7. Keeping the absolute criterion visible

A relative gate has one characteristic failure mode: it goes green, and people read green
as *fine*. It is not fine. **P1's exit criterion is a 200-flow solution building within
+8 % overhead, and the last recorded measurement is +46.6 % [+42.8, +51.3] at 50 flows.**

Three things are arranged so that this cannot be forgotten again:

1. **`check-generator-cost.py` prints the absolute criterion on every run, including
   passing ones**, from the `absoluteCriterion` block of the committed baseline — the
   criterion, the last measured figure, the instrument that measured it, and where it is
   recorded. A green gate is followed immediately by `ABSOLUTE CRITERION — FAIL. This gate
   does not measure it.`
2. **`scale-overhead` stays.** It still runs `measure-scale-overhead.sh` in wall clock at
   50 and 200 flows, it still exits non-zero, and it stays advisory under the reasoning
   already written into it. This gate does not discharge it and the workflow says so.
3. **The `absoluteCriterion` block is data in a committed file**, so when the criterion is
   eventually met, updating it is a diff someone has to write — and the same commit is the
   one that removes `continue-on-error` from `scale-overhead`.

**The two gates measure different things and neither substitutes for the other.** This one
asks *did this commit make the generator more expensive*, has an answer today, and is
blocking. That one asks *is the build within budget*, does not have a passing answer today,
and is advisory until it does.

---

## 8. Running it

```bash
# The gate, as CI runs it. About a minute, most of it building FlowX.Compiler.
./scripts/measure-generator-cost.py --json /tmp/cost.json
./scripts/check-generator-cost.py /tmp/cost.json

# Re-record the baseline. A deliberate act; commit the diff with the reason.
./scripts/check-generator-cost.py /tmp/cost.json --record

# Price a different tree — a merge base, or a commit under bisect. Build its
# FlowX.Compiler and FlowX.Runtime first; the subject is generated fresh either way
# and its hash is checked, so the comparison cannot silently change what it measures.
./scripts/measure-generator-cost.py --no-build \
  --compiler /path/to/tree/src/FlowX.Compiler/bin/Release/netstandard2.0/FlowX.Compiler.dll \
  --refs     /path/to/tree/src/FlowX.Runtime/bin/Release/net10.0 \
  --json /tmp/other.json

# More sizes, if the question is about shape rather than about a regression.
./scripts/measure-generator-cost.py --sizes 25,50,100,200
```

Exit codes, the same four `analyse-scale-samples.py` already uses:
**0** PASS · **1** FAIL · **2** INCONCLUSIVE · **3** the measurement itself broke.

**INCONCLUSIVE means something different here, and CI treats it differently.** In
`scale-overhead` it means the runner was too busy to separate the generator's cost from
scheduling noise — the weather, which no author caused and no author can fix. Here the
measurement does not care how busy the runner is, so exit 2 can only mean that the same
generator gave different byte counts for the same sources: **a broken instrument, not a
busy one**. The CI job therefore retries once, and on a second INCONCLUSIVE raises a
warning annotation that says the gate did not run and leaves the merge unblocked. A gate
that cannot judge must not pass quietly, and must not fail a pull request for something its
author did not do.

Exit 3 — a flow that failed to analyse, or a build that failed — **is** blocking. That is a
defect rather than a measurement.

---

**See also:** [B12 at scale](B12-scale.md) · [B12 at one flow](B12.md) ·
[Benchmark harness](README.md) · [ADR-0002](../adr/ADR-0002-compile-time-orchestration.md) ·
[Roadmap P1](../20-Roadmap.md)

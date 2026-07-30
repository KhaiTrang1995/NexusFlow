# B12 at scale — the 200-flow synthetic solution

> **Verdict: FAIL.** A 200-flow synthetic solution builds with **+23 %** overhead against
> a **+8 %** budget — roughly **11 ms of generator per flow** against a compilation of
> about 10 s.
>
> **Whether that cost is linear in flow count is *not* settled here**, and §3 says so
> rather than quoting the fit. That question matters more than the ratio and this
> measurement could not answer it on shared hardware.
>
> **Recorded:** 2026-07-30 · **P1**

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

## 2. The measurement

Identical in method to B12: two builds of the same project producing the **same final
compilation**, differing only in whether the generator ran.

| Arm | How |
|---|---|
| **with** | the analyzer referenced, generator and diagnostic analyzers running |
| **without** | the analyzer dropped, the generated `.g.cs` compiled as ordinary source |

```bash
./scripts/measure-scale-overhead.sh 7 50 200 400
```

```
 flows       with    without   overhead     paired   verdict
------  ---------  ---------  ---------  ---------  --------
    50     9248 ms     6087 ms     +51.9 %     +23.3 %      FAIL
   200    12380 ms    10043 ms     +23.3 %     +20.7 %      FAIL
   400    34578 ms    29919 ms     +15.6 %     +27.0 %      FAIL

FAIL against the +8 % budget
```

*overhead* is B12's statistic — the ratio of the two arms' medians. *paired* is the median
of each round's own with/without ratio. Both are printed because the arms of a round run
seconds apart and the ratio of medians does not know that; on a machine whose speed drifts
mid-run they compare builds that met different machines. Where the two disagree, as they
do badly at 50 flows, the disagreement is the finding: it means drift dominates and
neither figure should be quoted alone.

**The recorded number is the 200-flow row**, because that is the size the criterion names
and the size where the two statistics agree: **+23.3 % and +20.7 %**, against +8 %.

An independent earlier run of 5 rounds at the same size put it at **+27.1 % / +34.6 %**.
Across both runs the answer is between roughly +20 % and +35 % — somewhere near **three
times the budget at the low end**, which is the resolution this measurement supports and
all it needs to.

### Absolute build time at 200 flows

The synthetic project is 200 flows, 800 capability steps, 250 compensations, 1 050
capability types and about 2 500 types in total, in 201 files.

| | |
|---|---|
| Compile the project alone, generator running | **≈ 12.4 s** |
| Compile the project alone, generator not running | ≈ 10.0 s |
| From cold, including restore and the FlowX projects | ≈ 24 s |

The timed figures exclude restore and dependencies, which are identical in both arms;
rebuilding them every round would pad the denominator with work the generator has nothing
to do with.

---

## 3. The scaling question, which matters more than the ratio — and is not answered

A generator whose cost grew faster than the flow count would fail at 400 flows however it
behaved at 200, and no single size can tell the two cases apart. Three sizes were measured
to settle it. They did not.

| Flows | Generator cost | Per flow |
|---:|---:|---:|
| 50 | +970 ms | 19.4 ms |
| 200 | +2 275 ms | 11.4 ms |
| 400 | +8 717 ms | 21.8 ms |

A least-squares fit through those three points reads **flows^0.99**, which is exactly
linear and would be a reassuring headline. It is not one, because the two segments the fit
averages disagree with each other and with it:

| Segment | Slope |
|---|---:|
| 50 → 200 flows | flows^**0.61** |
| 200 → 400 flows | flows^**1.94** |

Sublinear then quadratic is not a curve. It is two noisy points and a third, and the fit
through them lands near 1.0 by cancellation. **The honest reading is that this measurement
resolved the ratio and did not resolve the exponent.** The 400-flow rounds in particular
ran while the container's load average was between 25 and 34; the +8 717 ms at that size is
the least trustworthy number in this document.

`measure-scale-overhead.sh` prints the segment slopes next to the fit for exactly this
reason: a fit that averages away a disagreement reads far more convincing than the data
underneath it, and this one would have been quoted as "linear" by anybody who saw only the
one number.

**Settling it needs a quiet machine, not more rounds here.** Until then, the 200-flow
figure predicts nothing about 2 000 flows in either direction, and the possibility that
the generator is superlinear above 200 remains open — the outcome that would matter far
more than the ratio, since it would mean the cost cannot be paid down by a constant-factor
optimisation.

---

## 4. What this does not claim

**The hardware was not quiet, and it is not close.** This is the same shared container the
[P0 report](P0.md#5-is-shared-hardware-good-enough-to-decide-on) documents — 4 logical
cores, Intel Xeon @ 2.80 GHz, Ubuntu 24.04.4, .NET SDK 10.0.110 — and during this run it
was also compiling and testing other branches. The load average moved between 2 and 34
over the course of the measurement, and the with-arm's own range is 85 % of its median at
200 flows and 122 % at 400. **The two arms' ranges overlap at every size.**

So this is not evidence that the overhead is 23 %. It is evidence that the overhead is
**not near 8 %** — the same shape of claim B12 made in the opposite direction, and the only
shape either measurement can support here.

**The 50-flow row should not be quoted on its own.** Its two statistics differ by more than
a factor of two (+51.9 % against +23.3 %) because the with-arm spread there is 126 % of its
median. It stays in the table anyway: dropping a point after seeing that it disagrees is
how a curve gets fitted to numbers that were not there, and the disagreement is part of
what §3 has to report.

**Nothing here separates the generator from the diagnostic analyzers.** The control arm
drops `FlowX.Compiler` entirely, so the measured cost is `FlowPlanGenerator` *plus*
`CapabilityAnalyzer` — which visits every named type in the compilation, of which there
are about 2 500. That is the correct total for a budget phrased as "overhead vs identical
non-FlowX code", since a non-FlowX codebase would run neither. It is the wrong number for
deciding what to optimise, and the split has not been measured. The first diagnostic step
is to keep the generator and drop only the analyzers:

```bash
dotnet build ScaleSynthetic.csproj -c Release --no-restore --no-dependencies \
  --no-incremental /p:RunAnalyzers=false
```

**The `CompilerBenchmarks` figure and this one are not comparable, and the gap is not
explained.** B12 §3 prices the generator at ~2.9 ms for a compilation containing one flow.
This measures 11.4 ms per flow at 200. Some of that gap is the analyzers, which
`CompilerBenchmarks` does not run at all; some is per-compilation work that a one-flow
benchmark charges once; how much of each is unknown.

---

## 5. Why this and B12 do not contradict each other

Both are correct measurements of different projects.

| | Reference sample | 200-flow synthetic |
|---|---:|---:|
| Flows | 1 | 200 |
| Compilation, generator off | ~2.6 s | ~10.0 s |
| Generator + analyzer cost | ~3 ms | ~2 275 ms |
| Overhead | +0.4 % | +23 % |

The compilation grew about 4×; FlowX's compile-time cost grew about 750×. Overhead is a
ratio of two quantities that scale with different things, so a single sample can only ever
report the ratio at its own size. B12's +0.4 % was never a property of the generator; it
was a property of a project with one flow in it, and it remains true of that project.

What this changes is what the +0.4 % may be used to argue. It may not be used to argue that
build overhead is under control at scale, and
[14-Performance §1](../14-Performance.md)'s unqualified "+8 %" is not currently met by any
project big enough to notice.

---

## 6. Consequences

[ADR-0002](../adr/ADR-0002-compile-time-orchestration.md)'s revisit clause names this
threshold:

> **Revisit when:** generator maintenance cost exceeds its benefit (measured as > 3
> generator defects per delivery phase, or **build overhead > 8 % sustained**).

The measured figure is over that threshold by a wide margin at every size tested. It is
**not yet "sustained"**: this is one afternoon on one noisy shared container, and the
clause reasonably asks for more than that before a foundational ADR is reopened. What it
does mean is that the clause is now live rather than theoretical, and that P1 cannot be
declared done on the current generator.

The CI job (`.github/workflows/performance.yml`, `scale-overhead`) is wired as
**advisory**, and that is a deliberate and temporary choice with a condition attached. This
is P1's *exit* criterion. Making it blocking today would turn every pull request red for
the length of the phase over a defect none of them introduced — including the pull requests
that fix it — and a red gate people learn to ignore is worse than no gate. The script's
exit code is real, `pipefail` is set, and the step is reported as failed; only the job's
effect on the pull request is suppressed. **Remove `continue-on-error` the moment this
document records a pass.**

---

## 7. Reproducing this

```bash
# The recorded verdict: 7 rounds at three sizes. Budget ~25 minutes on an idle 4-core
# box; it took closer to an hour under the contention §4 describes.
./scripts/measure-scale-overhead.sh 7 50 200 400

# The criterion alone, faster.
./scripts/measure-scale-overhead.sh 5 200

# Just the subject, to read or to profile against.
./scripts/generate-scale-project.py --flows 200 --out /tmp/scale-200
```

The synthetic project's shape, and the reasoning behind each choice in it — why the flows
differ from one another, why the capability bodies are deliberately minimal, why the
project inherits none of the repository's analyzer settings — is documented at the top of
`scripts/generate-scale-project.py`. Each choice is made in the direction that makes the
overhead ratio *larger*, so a pass would be a conservative pass. This is a fail.

---

**See also:** [B12 at one flow](B12.md) · [ADR-0002](../adr/ADR-0002-compile-time-orchestration.md) ·
[Benchmark harness](README.md) · [Roadmap P1](../20-Roadmap.md) · [Plan](../../PLAN.md)

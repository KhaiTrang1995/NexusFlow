#!/usr/bin/env python3
"""Turn raw build timings from measure-scale-overhead.sh into a verdict, or into a
refusal to render one.

    ./scripts/analyse-scale-samples.py samples.json
    ./scripts/analyse-scale-samples.py samples.json --budget 8 --markdown

Kept separate from the shell harness for two reasons. A run costs tens of minutes and
the statistics are the part most likely to need changing, so re-analysing a recorded
sample file must not mean re-measuring. And a verdict whose arithmetic can only be seen
by running an hour-long build is a verdict nobody audits.


Why the earlier version of this measurement could not be trusted
----------------------------------------------------------------

The first pass at the P1 scale criterion reported +23 % overhead against a +8 % budget
while the within-arm range was 85 % of its own median and the machine's load average
moved between 2 and 34. A difference of 15 points read off arms that each wander by 85 %
is not a measurement of anything. Four things are done differently here.

**The arms are sandwiched, not merely alternated.** Each round at each size runs three
builds, A B A or B A B, alternating which arm gets the outside slots. The paired
comparison is then made against the *mean of the two outside builds*, so a machine that
drifts linearly during the round contributes nothing to the ratio. Plain alternation
leaves that drift in the number with a sign that depends on which arm happened to run
first.

**The middle of the sandwich is also the noise floor.** The two outside builds are the
same arm, so their ratio is an A/A comparison: what this harness reports as "overhead"
when the true overhead is zero by construction. That distribution is measured in the
same rounds, on the same machine, under the same load as the real comparison, and it is
the only honest yardstick for whether an effect exists at all. It costs one extra build
per size per round and it is the single most valuable number here.

**Spread is reported as an interquartile range, not a min-to-max range.** A max-minus-min
range over n samples grows with n by construction and is set entirely by the two worst
scheduling accidents in the run. The IQR is not.

**A verdict is refused when the spread makes it meaningless.** Three separate gates can
return INCONCLUSIVE, and the exit code distinguishes it from both PASS and FAIL. A
measurement that says "this machine cannot resolve this" is a correct output; one that
rounds an unresolvable difference to whichever side of the budget it happens to land on
is not.


The two metrics, and which one is allowed to decide
---------------------------------------------------

Every build is timed twice over, in wall-clock milliseconds and in CPU milliseconds
(user + sys of the whole build process tree).

*Wall clock* is what the budget means -- a developer waits for wall clock -- and it is
what B12 measured. It is also, on a machine running four other builds, largely a
measurement of the other tenants: a compile takes 14 s on an idle box and 29 s on a
loaded one with nothing about the generator changed.

*CPU time* measures work done rather than time waited and barely moves with load, which
is exactly what a contended machine needs. It is only attributable when the shared
compiler server is off, because otherwise the compile runs inside a VBCSCompiler process
that outlives the build -- and on a shared machine that server is shared with every other
build on the box, so its CPU cannot be charged to this one either.

That leaves a genuine trade, and it is resolved by letting each configuration decide only
what it is entitled to decide:

**Compiler server on** (the default, and B12's configuration). Wall clock is the verdict.
The CPU column counts MSBuild alone, not the compile, so it is reported greyed out and
excluded from the verdict and from the growth fit.

**Compiler server off.** Every build pays a cold Roslyn start of several seconds, in both
arms. That inflates the *denominator* and therefore makes the overhead ratio smaller than
a developer would see -- so a ratio measured this way is a LOWER BOUND. A FAIL under a
lower bound is a real fail. A PASS under one is not a pass, and is reported as
INCONCLUSIVE with that reason. What this configuration is good for is the growth curve:
the fixed cold-start cost is identical in both arms and cancels in the difference, so the
per-size generator cost in milliseconds is both correct and load-robust.

When two metrics are in play and they disagree, the disagreement is the finding and the
verdict is INCONCLUSIVE.
"""

from __future__ import annotations

import argparse
import json
import math
import random
import statistics
import sys

PASS = "PASS"
FAIL = "FAIL"
INCONCLUSIVE = "INCONCLUSIVE"

EXIT = {PASS: 0, FAIL: 1, INCONCLUSIVE: 2}

BOOTSTRAP_DRAWS = 20000
METRICS = ("wall", "cpu")


def percentile(values: list[float], p: float) -> float:
    """Linear-interpolated percentile. `statistics.quantiles` refuses n < 2 and takes a
    method argument that has to be spelled the same way in three places; this does not."""
    if not values:
        return float("nan")

    ordered = sorted(values)

    if len(ordered) == 1:
        return ordered[0]

    position = (len(ordered) - 1) * p
    lower = math.floor(position)
    upper = math.ceil(position)

    if lower == upper:
        return ordered[int(position)]

    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def iqr(values: list[float]) -> float:
    """Interquartile range. The spread statistic that does not grow with sample count."""
    return percentile(values, 0.75) - percentile(values, 0.25)


def bootstrap_median(values: list[float], rng: random.Random, draws: int = BOOTSTRAP_DRAWS
                     ) -> tuple[float, float]:
    """Percentile bootstrap confidence interval for a median.

    Coarse at the round counts this harness can afford -- with seven rounds the interval
    can only land on values the sample already contains -- and it is reported anyway,
    because the alternative is quoting a median with no interval at all and letting the
    reader assume it is tighter than it is.
    """
    if len(values) < 2:
        return (float("nan"), float("nan"))

    n = len(values)
    medians = [
        statistics.median([values[rng.randrange(n)] for _ in range(n)])
        for _ in range(draws)
    ]

    return (percentile(medians, 0.025), percentile(medians, 0.975))


class SizeResult:
    """Everything measured at one flow count, for one metric."""

    def __init__(self, size: int, metric: str, rounds: list[dict], rng: random.Random):
        self.size = size
        self.metric = metric
        self.rounds = len(rounds)

        with_all: list[float] = []
        without_all: list[float] = []

        # The paired estimate. Each round is A B A or B A B; the outer pair is averaged
        # so that a machine drifting linearly across the round cancels out instead of
        # landing in the ratio with a sign set by which arm happened to run first.
        self.paired_overhead: list[float] = []
        self.paired_delta: list[float] = []

        # The same rounds' A/A (or B/B) comparison: two builds of the SAME arm, the same
        # distance apart in time as the real pair. Its spread is what this harness
        # reports as overhead when the true overhead is zero.
        self.self_ratio: list[float] = []

        for entry in rounds:
            builds = entry["builds"]
            arms = [b["arm"] for b in builds]
            times = [float(b[f"{metric}_ms"]) for b in builds]

            with_all.extend(t for a, t in zip(arms, times) if a == "with")
            without_all.extend(t for a, t in zip(arms, times) if a == "without")

            if len(builds) != 3 or arms[0] != arms[2] or arms[1] == arms[0]:
                continue

            outer = (times[0] + times[2]) / 2
            inner = times[1]

            if arms[0] == "with":
                w, o = outer, inner
            else:
                w, o = inner, outer

            if o > 0:
                self.paired_overhead.append((w - o) / o * 100)
                self.paired_delta.append(w - o)

            if times[0] > 0:
                self.self_ratio.append((times[2] - times[0]) / times[0] * 100)

        self.with_samples = with_all
        self.without_samples = without_all

        self.with_median = statistics.median(with_all) if with_all else float("nan")
        self.without_median = statistics.median(without_all) if without_all else float("nan")

        self.with_iqr = iqr(with_all)
        self.without_iqr = iqr(without_all)

        self.with_spread = self.with_iqr / self.with_median * 100 if self.with_median else float("nan")
        self.without_spread = (
            self.without_iqr / self.without_median * 100 if self.without_median else float("nan")
        )

        # Kept only so this report can be read next to the previous one, which quoted a
        # max-minus-min range. It is not used in any gate.
        self.with_range = (min(with_all), max(with_all)) if with_all else (0.0, 0.0)
        self.without_range = (min(without_all), max(without_all)) if without_all else (0.0, 0.0)

        self.overhead = (
            statistics.median(self.paired_overhead) if self.paired_overhead else float("nan")
        )
        self.overhead_ci = bootstrap_median(self.paired_overhead, rng)

        self.delta = statistics.median(self.paired_delta) if self.paired_delta else float("nan")
        self.delta_ci = bootstrap_median(self.paired_delta, rng)

        # The A/A control, split into the two things it contains.
        #
        # Its median is systematic drift within a round -- the machine being reliably
        # slower (or faster) by the third build than the first. The sandwich removes that
        # from the real comparison by construction, so it is reported as a property of the
        # harness rather than folded into the effect's uncertainty.
        #
        # Its scatter about that median is what the sandwich cannot remove, and is the
        # scale below which an effect is not distinguishable from the harness measuring
        # itself. Comparing a median-over-rounds against a per-round scatter is stricter
        # than it needs to be; erring towards INCONCLUSIVE is the intended direction.
        if self.self_ratio:
            self.noise_drift = statistics.median(self.self_ratio)
            self.noise_half = (
                percentile(self.self_ratio, 0.975) - percentile(self.self_ratio, 0.025)
            ) / 2
        else:
            self.noise_drift = float("nan")
            self.noise_half = float("nan")

        self.noise_lo = self.noise_drift - self.noise_half
        self.noise_hi = self.noise_drift + self.noise_half

    @property
    def per_flow(self) -> float:
        return self.delta / self.size

    @property
    def effect_detected(self) -> bool:
        """Is the measured overhead larger than the harness's own scatter against itself?"""
        if not self.self_ratio or math.isnan(self.noise_half):
            return True

        return abs(self.overhead) > self.noise_half

    def verdict(self, budget: float, max_spread: float) -> tuple[str, str]:
        """PASS, FAIL or INCONCLUSIVE, and the reason in one line.

        Three ways to end up inconclusive, in the order they are checked:

        1. The arms are so scattered that no ratio read off them means anything.
        2. The measured overhead is inside the band an A/A comparison produced, so there
           is no evidence of an effect -- and the band itself straddles the budget, so
           "no evidence of an effect" does not clear the budget either.
        3. The confidence interval on the overhead contains the budget, so the same data
           supports a pass and a fail.
        """
        worst_spread = max(self.with_spread, self.without_spread)

        if worst_spread > max_spread:
            return (
                INCONCLUSIVE,
                f"within-arm spread {worst_spread:.1f} % of the median exceeds the "
                f"{max_spread:.0f} % limit; no ratio read off these arms is meaningful",
            )

        lo, hi = self.overhead_ci

        if not self.effect_detected:
            if self.noise_half <= budget:
                return (
                    PASS,
                    f"overhead {self.overhead:+.1f} % is smaller than the harness's own "
                    f"A/A scatter of ±{self.noise_half:.1f} %, and that scatter is itself "
                    f"inside the {budget:.0f} % budget",
                )

            return (
                INCONCLUSIVE,
                f"overhead {self.overhead:+.1f} % is smaller than the harness's own A/A "
                f"scatter of ±{self.noise_half:.1f} %, which is wider than the "
                f"{budget:.0f} % budget; an effect this size is not visible here",
            )

        if math.isnan(lo):
            return (INCONCLUSIVE, "too few rounds to form a confidence interval")

        if hi <= budget:
            return (PASS, f"95 % CI [{lo:+.1f}, {hi:+.1f}] % lies below the {budget:.0f} % budget")

        if lo > budget:
            return (FAIL, f"95 % CI [{lo:+.1f}, {hi:+.1f}] % lies above the {budget:.0f} % budget")

        return (
            INCONCLUSIVE,
            f"95 % CI [{lo:+.1f}, {hi:+.1f}] % contains the {budget:.0f} % budget; "
            "the same data supports a pass and a fail",
        )


def fit_exponent(sizes: list[int], deltas: list[float]) -> tuple[float, float]:
    """Least-squares slope of log(cost) against log(flows), and the fit's R^2.

    An exponent of 1.0 is linear: doubling the flows doubles the generator's cost. Below
    1.0 the generator amortises across flows; above it, it does not, and a pass at 200
    flows says nothing about 2 000.
    """
    xs = [math.log(s) for s in sizes]
    ys = [math.log(d) for d in deltas]

    mean_x = sum(xs) / len(xs)
    mean_y = sum(ys) / len(ys)

    variance = sum((x - mean_x) ** 2 for x in xs)

    if variance == 0:
        return (float("nan"), float("nan"))

    slope = sum((x - mean_x) * (y - mean_y) for x, y in zip(xs, ys)) / variance
    intercept = mean_y - slope * mean_x

    residual = sum((y - (slope * x + intercept)) ** 2 for x, y in zip(xs, ys))
    total = sum((y - mean_y) ** 2 for y in ys)

    r2 = 1 - residual / total if total > 0 else float("nan")

    return (slope, r2)


def fit_affine(sizes: list[int], deltas: list[float]) -> tuple[float, float, float]:
    """Ordinary least squares of cost against flow count: cost = fixed + marginal * flows.

    A power law through a cost that is genuinely `fixed + k * flows` reads as sublinear,
    and reads more sublinear the smaller the smallest size measured -- because at one flow
    the fixed part is the whole number. That is an artefact of the model, not a property
    of the generator, and it is why both fits are reported. The affine fit separates the
    two: `fixed` is what the generator costs to start at all, `marginal` is what each
    additional flow costs, and it is `marginal` that decides whether a real solution is
    affordable.
    """
    n = len(sizes)
    mean_x = sum(sizes) / n
    mean_y = sum(deltas) / n

    variance = sum((x - mean_x) ** 2 for x in sizes)

    if variance == 0:
        return (float("nan"), float("nan"), float("nan"))

    slope = sum((x - mean_x) * (y - mean_y) for x, y in zip(sizes, deltas)) / variance
    intercept = mean_y - slope * mean_x

    residual = sum((y - (slope * x + intercept)) ** 2 for x, y in zip(sizes, deltas))
    total = sum((y - mean_y) ** 2 for y in deltas)

    r2 = 1 - residual / total if total > 0 else float("nan")

    return (intercept, slope, r2)


def bootstrap_exponent(results: dict[int, SizeResult], usable: list[int], rng: random.Random,
                       draws: int = 4000) -> tuple[float, float]:
    """Confidence interval for the growth exponent.

    Rounds are resampled within each size, each size's cost is recomputed from its own
    resample, and the fit is redone. This propagates the per-size uncertainty into the
    exponent instead of fitting a line through five point estimates and reporting the
    slope as though the points were exact -- which is how a fit reads 0.99 while the
    segments it averages read 0.61 and 1.94.
    """
    if len(usable) < 2:
        return (float("nan"), float("nan"))

    slopes: list[float] = []

    for _ in range(draws):
        sizes: list[int] = []
        deltas: list[float] = []

        for size in usable:
            sample = results[size].paired_delta
            n = len(sample)
            resampled = statistics.median([sample[rng.randrange(n)] for _ in range(n)])

            if resampled > 0:
                sizes.append(size)
                deltas.append(resampled)

        if len(sizes) >= 2:
            slope, _ = fit_exponent(sizes, deltas)

            if not math.isnan(slope):
                slopes.append(slope)

    if len(slopes) < 100:
        return (float("nan"), float("nan"))

    return (percentile(slopes, 0.025), percentile(slopes, 0.975))


def describe_shape(exponent: float, lo: float, hi: float) -> str:
    """Name the curve, and say how much the interval permits."""
    if math.isnan(exponent):
        return "not determinable from these sizes"

    if math.isnan(lo):
        return f"flows^{exponent:.2f}, with no interval -- too few rounds to bootstrap"

    if hi < 0.9:
        shape = "sublinear"
    elif lo > 1.1:
        shape = "superlinear"
    elif lo >= 0.9 and hi <= 1.1:
        shape = "linear"
    elif lo > 0.9:
        shape = "linear or mildly superlinear"
    elif hi < 1.1:
        shape = "linear or mildly sublinear"
    else:
        shape = "not resolved -- the interval spans sublinear to superlinear"

    return f"flows^{exponent:.2f}, 95 % CI [{lo:.2f}, {hi:.2f}] -- {shape}"


def analyse(document: dict, budget: float, max_spread: float, seed: int) -> dict:
    meta = document["meta"]
    rng = random.Random(seed)

    by_size: dict[int, list[dict]] = {}

    for entry in document["rounds"]:
        by_size.setdefault(int(entry["size"]), []).append(entry)

    sizes = sorted(by_size)

    results = {
        metric: {size: SizeResult(size, metric, by_size[size], rng) for size in sizes}
        for metric in METRICS
    }

    criterion = 200 if 200 in sizes else max(sizes)

    server_on = bool(meta.get("compiler_server", True))

    # With the compiler server on, the CPU column counts MSBuild and not the compile, so
    # it is not allowed to decide anything. With it off, both metrics are attributable
    # but both ratios are diluted by a cold Roslyn start that lands in both arms.
    decisive = ("wall",) if server_on else ("wall", "cpu")

    verdicts = {
        metric: results[metric][criterion].verdict(budget, max_spread) for metric in METRICS
    }

    if not server_on:
        # A ratio whose denominator carries seconds of cold-start work that a real build
        # would not pay is a lower bound on the true overhead. Below the budget it proves
        # nothing; above it, it proves the budget is missed.
        for metric in decisive:
            verdict, reason = verdicts[metric]

            if verdict == PASS:
                verdicts[metric] = (
                    INCONCLUSIVE,
                    reason + " — but the compiler server was off, so both arms carry a "
                    "cold Roslyn start and this ratio is a lower bound; a pass under a "
                    "lower bound is not a pass",
                )

    taken = [verdicts[metric][0] for metric in decisive]

    if len(set(taken)) == 1:
        overall = taken[0]
        why = (
            f"{verdicts[decisive[0]][1]}"
            if len(decisive) == 1
            else f"wall clock and CPU time agree: {verdicts[decisive[0]][1]}"
        )
    elif {PASS, FAIL} <= set(taken):
        overall = INCONCLUSIVE
        why = (
            f"wall clock says {verdicts['wall'][0]} and CPU time says {verdicts['cpu'][0]}; "
            "a measurement whose two metrics contradict each other has not measured anything"
        )
    elif FAIL in taken:
        overall = FAIL
        metric = "wall" if verdicts["wall"][0] == FAIL else "cpu"
        why = f"one metric fails and the other is inconclusive: {verdicts[metric][1]}"
    else:
        overall = INCONCLUSIVE
        metric = "wall" if verdicts["wall"][0] == INCONCLUSIVE else "cpu"
        why = f"one metric passes and the other is inconclusive: {verdicts[metric][1]}"

    shapes = {}

    for metric in METRICS:
        per_size = results[metric]

        # A size whose measured cost could be zero contributes a log of noise to a
        # log-log fit. Dropping it is not cherry-picking: a point whose interval contains
        # zero has no position on a log axis at all.
        usable = [
            s for s in sizes
            if per_size[s].delta > 0 and not (per_size[s].delta_ci[0] <= 0 <= per_size[s].delta_ci[1])
        ]

        dropped = [s for s in sizes if s not in usable]

        if len(usable) >= 2:
            exponent, r2 = fit_exponent(usable, [per_size[s].delta for s in usable])
            lo, hi = bootstrap_exponent(per_size, usable, rng)
            fixed, marginal, affine_r2 = fit_affine(usable, [per_size[s].delta for s in usable])
        else:
            exponent = r2 = lo = hi = fixed = marginal = affine_r2 = float("nan")

        segments = []

        for small, large in zip(usable, usable[1:]):
            slope = math.log(per_size[large].delta / per_size[small].delta) / math.log(large / small)
            segments.append((small, large, slope))

        shapes[metric] = {
            "usable": usable,
            "dropped": dropped,
            "exponent": exponent,
            "r2": r2,
            "ci": (lo, hi),
            "segments": segments,
            "fixed": fixed,
            "marginal": marginal,
            "affine_r2": affine_r2,
        }

    return {
        "meta": meta,
        "sizes": sizes,
        "criterion": criterion,
        "results": results,
        "verdicts": verdicts,
        "overall": overall,
        "why": why,
        "shapes": shapes,
        "budget": budget,
        "max_spread": max_spread,
        "server_on": server_on,
        "decisive": decisive,
    }


def render(report: dict, out=sys.stdout) -> None:
    meta = report["meta"]
    budget = report["budget"]

    def line(text: str = "") -> None:
        print(text, file=out)

    line()
    line("  P1 scale criterion — build overhead vs identical non-FlowX code")
    line("  " + "=" * 68)
    line()

    line(f"  machine        {meta.get('nproc', '?')} logical cores, {meta.get('cpu_model', 'unknown')}")
    line(f"  toolchain      .NET SDK {meta.get('dotnet', '?')}")

    loads = meta.get("load_samples") or []

    if loads:
        line(
            f"  load average   {min(loads):.2f} min, {statistics.median(loads):.2f} median, "
            f"{max(loads):.2f} max over {len(loads)} samples during the run"
        )
        line(f"                 (a 1-minute load average of {meta.get('nproc', 4)} is a fully busy machine)")

    line(
        f"  rounds         {meta.get('rounds')} measured, {meta.get('warmup')} discarded as warm-up, "
        f"3 builds per size per round"
    )
    if report["server_on"]:
        line("  compiler server on — B12's configuration; CPU time is NOT attributable")
    else:
        line("  compiler server off — CPU time attributable; ratios are a LOWER BOUND")

    if int(meta.get("rounds", 0)) < 5:
        line()
        line("  NOTE: fewer than 5 rounds. Every interval below is formed from a handful")
        line("        of points and is decorative rather than informative. Treat any")
        line("        verdict from this run as provisional.")

    line()

    for metric, unit in (("wall", "wall clock"), ("cpu", "CPU time")):
        per_size = report["results"][metric]
        role = (
            "decides the verdict"
            if metric in report["decisive"]
            else "MSBuild only, not the compile — reported, not used"
        )

        line(f"  {unit}  ({role})")
        line("  " + "-" * 68)
        line(
            f"  {'flows':>6}  {'with':>9}  {'without':>9}  {'overhead':>9}  "
            f"{'95 % CI':>18}  {'A/A scatter':>12}"
        )

        for size in report["sizes"]:
            row = per_size[size]
            lo, hi = row.overhead_ci
            ci = f"[{lo:+.1f}, {hi:+.1f}]" if not math.isnan(lo) else "n/a"

            line(
                f"  {size:>6}  {row.with_median:>7.0f} ms  {row.without_median:>7.0f} ms  "
                f"{row.overhead:>+8.1f} %  {ci:>18}  {row.noise_half:>10.1f} %"
            )

        line()

        for size in report["sizes"]:
            row = per_size[size]

            line(
                f"  {size:>4} flows   spread (IQR) with {row.with_spread:.1f} %, "
                f"without {row.without_spread:.1f} % of the median"
            )
            line(
                f"              full range with {row.with_range[0]:.0f}–{row.with_range[1]:.0f} ms, "
                f"A/A drift {row.noise_drift:+.1f} % ± {row.noise_half:.1f} %"
            )

            dlo, dhi = row.delta_ci
            detected = "" if row.effect_detected else "   BELOW THE A/A SCATTER"

            line(
                f"              generator cost {row.delta:+.0f} ms "
                f"(CI [{dlo:+.0f}, {dhi:+.0f}]), {row.per_flow:+.2f} ms per flow{detected}"
            )

        line()

        shape = report["shapes"][metric]
        line(f"  growth in flow count: {describe_shape(shape['exponent'], *shape['ci'])}")

        if not math.isnan(shape["r2"]):
            line(f"  log-log fit R² {shape['r2']:.3f} over sizes {', '.join(str(s) for s in shape['usable'])}")

        if not math.isnan(shape["marginal"]):
            line(
                f"  affine fit  cost ≈ {shape['fixed']:.0f} ms fixed + "
                f"{shape['marginal']:.2f} ms per flow   (R² {shape['affine_r2']:.3f})"
            )

        if shape["segments"]:
            line("  segment slopes, which the fit above averages:")

            for small, large, slope in shape["segments"]:
                line(f"    {small:>4} -> {large:<5} flows^{slope:.2f}")

        if shape["dropped"]:
            line(
                "  excluded from the fit, cost not separable from zero: "
                + ", ".join(str(s) for s in shape["dropped"])
            )

        verdict, reason = report["verdicts"][metric]
        line()

        if metric in report["decisive"]:
            line(f"  {unit} at {report['criterion']} flows: {verdict} — {reason}")
        else:
            line(f"  {unit} is not attributable in this configuration and decides nothing.")

        line()

    line("  " + "=" * 68)
    line(f"  VERDICT: {report['overall']} against the +{budget:.0f} % budget "
         f"at {report['criterion']} flows")
    line(f"  {report['why']}")
    line("  " + "=" * 68)
    line()

    if report["overall"] == INCONCLUSIVE:
        line("  An inconclusive result is a result. It says this machine could not")
        line("  separate the generator's cost from its own scheduling noise at this")
        line("  size, and it does NOT mean the budget is met. Re-run on a machine")
        line("  with a load average near zero, or raise the round count until the")
        line("  confidence interval clears the budget in one direction.")
        line()


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Render a verdict — PASS, FAIL or INCONCLUSIVE — from build timings "
                    "recorded by measure-scale-overhead.sh, and fit the growth curve."
    )
    parser.add_argument("samples", help="JSON written by measure-scale-overhead.sh")
    parser.add_argument("--budget", type=float, default=8.0,
                        help="overhead budget in percent (default 8, from 14-Performance §1)")
    parser.add_argument("--max-spread", type=float, default=25.0,
                        help="refuse a verdict above this within-arm IQR, as %% of the median")
    parser.add_argument("--seed", type=int, default=20260730,
                        help="bootstrap seed, fixed so the same samples give the same interval")

    args = parser.parse_args()

    with open(args.samples, encoding="utf-8") as handle:
        document = json.load(handle)

    report = analyse(document, args.budget, args.max_spread, args.seed)
    render(report)

    return EXIT[report["overall"]]


if __name__ == "__main__":
    sys.exit(main())

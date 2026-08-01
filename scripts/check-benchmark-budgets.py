#!/usr/bin/env python3
"""Gate benchmark results against the committed baseline and the budget table.

Two classes of check, because only one of them is trustworthy on shared hardware:

  BLOCKING
    allocations     exact match, no tolerance
    budget ceiling  p95 must stay under the documented figure in docs/14-Performance.md

  ADVISORY (reported, does not fail the build)
    absolute drift  mean vs the committed baseline
    ratio drift     mean/fastest vs the committed baseline

The split is not a convenience. WP-3 asserted that ratios between benchmarks in the
same run are machine-independent and could therefore be gated tightly. Two runs of
the identical commit on the identical container then disagreed by up to 63 % on
ratio and 159 % on absolute time — because the fastest benchmark, which is the
ratio's denominator, sits at ~10 ns, right on the measurement noise floor. A
denominator that moves ±100 % moves every ratio with it.

So the claim was wrong and is withdrawn. Allocation counts are exact and
reproducible; timing on this hardware is not, in either form. Until the benchmarks
run on dedicated hardware (WP-11), drift is information for a human, not a gate.

What still catches a real regression: the budget ceilings. A four-step flow is
measured at ~170 ns against a 5 000 ns budget, so anything that costs an order of
magnitude fails loudly. A 2x regression would not — that is the honest limitation
of measuring here, and the reason WP-11 re-records elsewhere.

Usage:
    check-benchmark-budgets.py <artifacts-dir> [--baseline docs/benchmarks/baseline.json]
                                               [--strict]

    --strict promotes drift from advisory to blocking. Use it once the baseline has
    been recorded on dedicated hardware.
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import sys

EXIT_OK = 0
EXIT_REGRESSION = 1
EXIT_USAGE = 2


def load_results(artifacts_dir: str) -> dict[str, dict]:
    """Flatten every *-report-full.json into {Type.Method: measurements}."""
    reports = sorted(glob.glob(os.path.join(artifacts_dir, "**", "*-report-full.json"), recursive=True))

    if not reports:
        print(f"::error::No benchmark reports found under {artifacts_dir}")
        sys.exit(EXIT_USAGE)

    results: dict[str, dict] = {}

    for path in reports:
        with open(path, encoding="utf-8") as handle:
            document = json.load(handle)

        # A benchmark BenchmarkDotNet could not run is exported with a null Statistics
        # rather than omitted, so reading b["Statistics"]["Mean"] straight out crashes
        # with "'NoneType' object is not subscriptable" and a traceback that names the
        # dict comprehension instead of the run. The gate did fail — it has never been
        # able to pass a run that measured nothing — but it failed as a Python error,
        # and an operator reading the log learned the script broke rather than that
        # the benchmarks did not execute. Dropping the unmeasured ones here hands them
        # to check(), which reports each one as "in the baseline but absent from this
        # run" and blocks. Same verdict, stated in the vocabulary of the gate.
        benchmarks = [b for b in document.get("Benchmarks", []) if b.get("Statistics")]
        unmeasured = len(document.get("Benchmarks", [])) - len(benchmarks)

        if unmeasured:
            print(
                f"::warning::{os.path.basename(path)}: {unmeasured} benchmark(s) produced "
                "no measurement. BenchmarkDotNet failed to run them; see the run log for "
                "the build or toolchain error."
            )

        means = {b["Method"]: b["Statistics"]["Mean"] for b in benchmarks}
        fastest = min(means.values()) if means else 1.0

        for benchmark in benchmarks:
            key = f"{benchmark['Type']}.{benchmark['Method']}"
            memory = benchmark.get("Memory") or {}
            results[key] = {
                "mean_ns": benchmark["Statistics"]["Mean"],
                "p95_ns": benchmark["Statistics"].get("Percentiles", {}).get("P95", 0.0),
                "allocated": memory.get("BytesAllocatedPerOperation", 0),
                "ratio": benchmark["Statistics"]["Mean"] / fastest if fastest else 0.0,
            }

    return results


def check(results: dict[str, dict], baseline: dict, strict: bool) -> tuple[list[str], list[str]]:
    """Return (blocking failures, advisory notes)."""
    tolerances = baseline["tolerances"]
    ratio_tolerance = tolerances["ratioPercent"] / 100.0
    absolute_tolerance = tolerances["absolutePercent"] / 100.0

    blocking: list[str] = []
    advisory: list[str] = []
    drift_bucket = blocking if strict else advisory

    for name, expected in baseline["benchmarks"].items():
        actual = results.get(name)

        if actual is None:
            blocking.append(f"{name}: in the baseline but absent from this run")
            continue

        # BLOCKING 1 — allocations. Exact, machine-independent, and B2/B6 are hard zeros.
        #
        # Exact only for code WE wrote. A benchmark that drives Roslyn measures Roslyn's
        # allocations too, and those move by a few hundred bytes between runs of the same
        # commit — so an exact gate there fails on noise and teaches people to ignore it.
        # Such entries declare allocationTolerancePercent and are checked as a band.
        tolerance = expected.get("allocationTolerancePercent")

        if tolerance is None:
            if actual["allocated"] != expected["allocatedBytes"]:
                blocking.append(
                    f"{name}: allocated {actual['allocated']} B, baseline "
                    f"{expected['allocatedBytes']} B (allocation counts are exact)"
                )
        else:
            ceiling = expected["allocatedBytes"] * (1 + tolerance / 100)

            if actual["allocated"] > ceiling:
                blocking.append(
                    f"{name}: allocated {actual['allocated']} B, more than "
                    f"{tolerance}% above the baseline {expected['allocatedBytes']} B"
                )

        # BLOCKING 2 — the documented ceiling from docs/14-Performance.md.
        budget_ns = expected.get("budgetNs")
        if budget_ns and actual["p95_ns"] > budget_ns:
            blocking.append(
                f"{name}: p95 {actual['p95_ns']:.1f} ns exceeds budget "
                f"{expected['budget']} of {budget_ns} ns"
            )

        # ADVISORY — timing drift. See the module docstring for why this is not blocking.
        expected_ratio = expected.get("ratioToBaseline")
        if expected_ratio and expected_ratio > 0:
            drift = abs(actual["ratio"] - expected_ratio) / expected_ratio
            if drift > ratio_tolerance:
                drift_bucket.append(
                    f"{name}: ratio {actual['ratio']:.2f} vs baseline {expected_ratio:.2f} "
                    f"({drift * 100:.0f}% drift)"
                )

        expected_ns = expected.get("absoluteNs")
        if expected_ns and expected_ns > 0:
            drift = abs(actual["mean_ns"] - expected_ns) / expected_ns
            if drift > absolute_tolerance:
                drift_bucket.append(
                    f"{name}: mean {actual['mean_ns']:.1f} ns vs baseline {expected_ns:.1f} ns "
                    f"({drift * 100:.0f}% drift)"
                )

    for name in sorted(set(results) - set(baseline["benchmarks"])):
        advisory.append(f"{name} is new and has no baseline entry. Add one.")

    return blocking, advisory


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifacts", help="BenchmarkDotNet artifacts directory")
    parser.add_argument("--baseline", default="docs/benchmarks/baseline.json")
    parser.add_argument(
        "--strict",
        action="store_true",
        help="Promote timing drift from advisory to blocking. For dedicated hardware only.",
    )
    args = parser.parse_args()

    with open(args.baseline, encoding="utf-8") as handle:
        baseline = json.load(handle)

    results = load_results(args.artifacts)

    print(f"{'benchmark':50s} {'mean ns':>10s} {'p95 ns':>10s} {'ratio':>7s} {'alloc':>7s}")
    print("-" * 88)
    for name in sorted(results):
        r = results[name]
        print(
            f"{name:50s} {r['mean_ns']:10.2f} {r['p95_ns']:10.2f} "
            f"{r['ratio']:7.2f} {r['allocated']:6d}B"
        )

    blocking, advisory = check(results, baseline, args.strict)

    print()
    for note in advisory:
        print(f"::notice::advisory — {note}")

    if blocking:
        print()
        for failure in blocking:
            print(f"::error::{failure}")
        print(f"\n{len(blocking)} blocking gate failure(s).")
        return EXIT_REGRESSION

    print(f"\nAll blocking gates pass ({len(advisory)} advisory note(s)).")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())

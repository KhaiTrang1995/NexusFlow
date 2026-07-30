#!/usr/bin/env python3
"""Gate benchmark results against the committed baseline and the budget table.

Reads BenchmarkDotNet's JSON export and compares each benchmark on three axes:

  allocations   exact match, no tolerance      — machine-independent, and B2/B6 are hard zeros
  ratio         +/- tolerances.ratioPercent    — machine-independent, catches a real regression
  absolute ns   +/- tolerances.absolutePercent — machine-dependent, a catastrophe detector only

The split matters. A shared CI runner cannot reproduce an absolute nanosecond
figure from a developer's laptop, so gating tightly on absolute time produces a
job that fails for reasons nobody can act on — and a job people learn to ignore.
Ratios between benchmarks in the same run, and allocation counts, reproduce
exactly. Those carry the contract; the absolute number only has to stay in the
same order of magnitude and under its documented budget.

Usage:
    check-benchmark-budgets.py <artifacts-dir> [--baseline docs/benchmarks/baseline.json]
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
    pattern = os.path.join(artifacts_dir, "**", "*-report-full.json")
    reports = sorted(glob.glob(pattern, recursive=True))

    if not reports:
        print(f"::error::No benchmark reports found under {artifacts_dir}")
        sys.exit(EXIT_USAGE)

    results: dict[str, dict] = {}

    for path in reports:
        with open(path, encoding="utf-8") as handle:
            document = json.load(handle)

        # Ratios are computed here rather than read from the report: BenchmarkDotNet
        # only emits a Ratio column when a [Benchmark(Baseline = true)] exists, and
        # recomputing keeps this script working if that attribute ever moves.
        benchmarks = document.get("Benchmarks", [])
        means = {b["Method"]: b["Statistics"]["Mean"] for b in benchmarks}
        smallest = min(means.values()) if means else 1.0

        for benchmark in benchmarks:
            key = f"{benchmark['Type']}.{benchmark['Method']}"
            memory = benchmark.get("Memory") or {}
            results[key] = {
                "mean_ns": benchmark["Statistics"]["Mean"],
                "p95_ns": benchmark["Statistics"].get("Percentiles", {}).get("P95", 0.0),
                "allocated": memory.get("BytesAllocatedPerOperation", 0),
                "ratio": benchmark["Statistics"]["Mean"] / smallest if smallest else 0.0,
            }

    return results


def check(results: dict[str, dict], baseline: dict) -> list[str]:
    """Return a list of failure messages; empty means the gate passes."""
    tolerances = baseline["tolerances"]
    ratio_tolerance = tolerances["ratioPercent"] / 100.0
    absolute_tolerance = tolerances["absolutePercent"] / 100.0

    failures: list[str] = []

    for name, expected in baseline["benchmarks"].items():
        actual = results.get(name)

        if actual is None:
            failures.append(f"{name}: present in the baseline but absent from this run")
            continue

        # 1. Allocations — exact. A hard zero cannot be eroded a field at a time.
        if actual["allocated"] != expected["allocatedBytes"]:
            failures.append(
                f"{name}: allocated {actual['allocated']} B, baseline "
                f"{expected['allocatedBytes']} B (allocation counts are exact)"
            )

        # 2. Absolute budget — the documented ceiling from docs/14-Performance.md.
        budget_ns = expected.get("budgetNs")
        if budget_ns and actual["p95_ns"] > budget_ns:
            failures.append(
                f"{name}: p95 {actual['p95_ns']:.1f} ns exceeds budget "
                f"{expected['budget']} of {budget_ns} ns"
            )

        # 3. Ratio drift — machine-independent, so this is the sensitive check.
        expected_ratio = expected.get("ratioToBaseline")
        if expected_ratio and expected_ratio > 0:
            drift = abs(actual["ratio"] - expected_ratio) / expected_ratio
            if drift > ratio_tolerance:
                failures.append(
                    f"{name}: ratio {actual['ratio']:.2f} drifted {drift * 100:.0f}% from "
                    f"baseline {expected_ratio:.2f} (tolerance {tolerances['ratioPercent']}%)"
                )

        # 4. Absolute drift — loose. Only catches an order-of-magnitude change.
        expected_ns = expected.get("absoluteNs")
        if expected_ns and expected_ns > 0:
            drift = abs(actual["mean_ns"] - expected_ns) / expected_ns
            if drift > absolute_tolerance:
                failures.append(
                    f"{name}: mean {actual['mean_ns']:.1f} ns drifted {drift * 100:.0f}% from "
                    f"baseline {expected_ns:.1f} ns (tolerance {tolerances['absolutePercent']}%) "
                    f"— may be runner noise; confirm before updating the baseline"
                )

    unexpected = sorted(set(results) - set(baseline["benchmarks"]))
    for name in unexpected:
        print(f"::notice::{name} is new and has no baseline entry. Add one.")

    return failures


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifacts", help="BenchmarkDotNet artifacts directory")
    parser.add_argument("--baseline", default="docs/benchmarks/baseline.json")
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

    failures = check(results, baseline)

    print()
    if failures:
        for failure in failures:
            print(f"::error::{failure}")
        print(f"\n{len(failures)} benchmark gate failure(s).")
        return EXIT_REGRESSION

    print("All benchmark gates pass.")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())

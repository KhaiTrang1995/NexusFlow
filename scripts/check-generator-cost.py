#!/usr/bin/env python3
"""Gate the generator's cost against the committed baseline, and report where the
absolute P1 criterion stands whatever the gate says.

    ./scripts/measure-generator-cost.py --json /tmp/cost.json
    ./scripts/check-generator-cost.py /tmp/cost.json

    ./scripts/check-generator-cost.py /tmp/cost.json --record   # re-record deliberately

Exit codes:  0 PASS   1 FAIL   2 INCONCLUSIVE   3 the measurement itself broke.
The same four this repository already uses for scripts/analyse-scale-samples.py.


Why a committed baseline rather than a budget
---------------------------------------------

Two gates in this repository already work this way, and both give the same reason.

  * docs/benchmarks/baseline.json holds a recorded figure per benchmark, and
    scripts/check-benchmark-budgets.py compares against it.
  * samples/ecommerce/flowx.manifest.baseline.json holds a recorded contract, and the
    `manifest` job in .github/workflows/ci.yml diffs against it. Its comment says it
    plainly: "The baseline is committed on purpose. A contract change then arrives as a
    reviewable diff ... rather than as a number in a log."

The same reasoning applies to compile-time cost, and docs/benchmarks/B12-scale.md §5.2 is
the case for it. A commit took FlowPlanGenerator from 5.60 to 27.28 ms per flow — 4.9x —
and merged silently along with three further working packages, because the only gate was
absolute, against a +8 % budget the project was already failing by a wide margin. An
absolute gate that is already red is not a gate. A relative one would have fired on the
commit that did it.

Regenerating this baseline is therefore meant to be a deliberate act with a reviewable
diff attached, exactly like regenerating the manifest baseline. `--record` does it.


What is blocking here, and what is not
--------------------------------------

BLOCKING — allocated bytes against the committed figure, per size. The quantity is a count
of work rather than a measurement of the machine; see the header of
scripts/measure-generator-cost.py and docs/benchmarks/generator-cost-gate.md for the
distribution that justifies the threshold.

BLOCKING — a subject or toolchain that no longer matches the baseline. If
generate-scale-project.py or the pinned Roslyn version changes, the committed number stops
describing anything and the correct response is to re-record it in the same pull request,
not to compare across the change and call the difference a regression.

ADVISORY — elapsed milliseconds, emitted character counts, and a baseline that has become
pessimistic because the generator got faster. None of these fails a build; all are printed.

NOT A FAILURE — INCONCLUSIVE. Exit 2 means the run could not resolve the question. The
absolute harness reserves that code for a machine too busy to judge, and the comment in
.github/workflows/performance.yml is explicit that failing a pull request for it "fails it
for the weather". Here it means something narrower and stranger, because this measurement
does not care how busy the machine is: it means the same generator, run repeatedly against
the same sources in the same process, produced different byte counts. That is not weather,
it is a broken instrument — so the CI job retries once and then raises a warning that the
gate did not run, rather than passing quietly or failing loudly.


What this does NOT say
----------------------

It does not say the build is fast enough. P1's exit criterion is a 200-flow solution
building within +8 % overhead, that is a wall-clock question,
scripts/measure-scale-overhead.sh is the instrument for it, and the last recorded answer
is a fail by a wide margin. The `absoluteCriterion` block in the baseline is reprinted on
every run, passing or failing, so that a green relative gate cannot be read as a budget
that is met.
"""

from __future__ import annotations

import argparse
import datetime
import json
import pathlib
import subprocess
import sys

PASS = "PASS"
FAIL = "FAIL"
INCONCLUSIVE = "INCONCLUSIVE"

EXIT = {PASS: 0, FAIL: 1, INCONCLUSIVE: 2}
EXIT_BROKEN = 3

REPO = pathlib.Path(__file__).resolve().parent.parent
DEFAULT_BASELINE = REPO / "docs" / "benchmarks" / "generator-cost-baseline.json"


def die(message: str) -> None:
    print(f"::error::{message}", file=sys.stderr)
    sys.exit(EXIT_BROKEN)


def shell(*command: str) -> str:
    try:
        result = subprocess.run(command, capture_output=True, text=True, check=False)
        return result.stdout.strip()
    except OSError:
        return ""


def record(samples: dict, baseline: dict, path: pathlib.Path) -> None:
    """Rewrite the baseline from a measured run, keeping everything a human wrote."""
    environment = samples["meta"]["environment"]

    baseline["recordedOn"] = {
        "commit": shell("git", "-C", str(REPO), "rev-parse", "--short", "HEAD") or "unknown",
        "recordedAt": datetime.datetime.now(datetime.UTC).strftime("%Y-%m-%d"),
        "roslyn": environment["roslyn_product_version"],
        "roslynAssemblyVersion": environment["roslyn_assembly_version"],
        "framework": environment["framework"],
        "sdk": shell("dotnet", "--version") or "unknown",
        "nproc": samples["meta"]["nproc"],
        "load1DuringRun": samples["meta"]["load1_after"],
        "runsPerSize": samples["meta"]["runs"],
        "warmupPerSize": samples["meta"]["warmup"],
    }

    baseline["sizes"] = {
        str(entry["flows"]): {
            "allocatedBytes": entry["allocated_bytes"],
            "allocatedBytesPerFlow": entry["allocated_bytes_per_flow"],
            "sourcesSha256": entry["sources_sha256"],
            "generatedTrees": entry["generated_trees"],
            "generatedChars": entry["generated_chars"],
            "diagnostics": entry["diagnostics"],
            "observedSpreadPercent": entry["spread_percent"],
        }
        for entry in samples["sizes"]
    }

    # ensure_ascii=False so that re-recording does not turn every non-ASCII character in
    # the hand-written commentary into an escape sequence. A baseline whose prose churns
    # on every regeneration is a baseline whose diff nobody reads.
    path.write_text(json.dumps(baseline, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Baseline re-recorded at {path}. Commit the diff.")


def check(samples: dict, baseline: dict, threshold: float, stale: float, max_spread: float):
    """Return (verdict, blocking failures, advisory notes)."""
    blocking: list[str] = []
    advisory: list[str] = []
    unresolved: list[str] = []

    environment = samples["meta"]["environment"]
    recorded = baseline["recordedOn"]

    # The number is a property of (generator, subject, Roslyn). Comparing across a change
    # to any of the three is not a measurement of the generator, so the gate refuses the
    # comparison rather than attributing the difference to code.
    if environment["roslyn_product_version"] != recorded["roslyn"]:
        blocking.append(
            f"Roslyn moved: baseline recorded against {recorded['roslyn']}, this run used "
            f"{environment['roslyn_product_version']}. The committed byte counts describe "
            "the old pair. Re-record with --record in the same pull request that moved the "
            "pin in scripts/generator-cost-probe/GeneratorCostProbe.csproj.")

    # The runtime's patch level is not pinnable across a hosted runner and a container, and
    # nothing measured so far suggests it moves this number. It is reported, not gated.
    if environment["framework"] != recorded["framework"]:
        advisory.append(
            f"runtime differs from the baseline's: {environment['framework']} here, "
            f"{recorded['framework']} recorded. Not gated — but if this run is the odd one "
            "out by a few tenths of a percent, this is the first thing to suspect.")

    for entry in samples["sizes"]:
        size = str(entry["flows"])
        expected = baseline["sizes"].get(size)

        if expected is None:
            blocking.append(
                f"{size} flows was measured but has no baseline entry. Add one with --record.")
            continue

        if entry["sources_sha256"] != expected["sourcesSha256"]:
            blocking.append(
                f"{size} flows: the synthetic project changed "
                f"({entry['sources_sha256'][:12]} against the recorded "
                f"{expected['sourcesSha256'][:12]}). scripts/generate-scale-project.py "
                "decides what is measured, so a change to it makes the committed number "
                "describe a different program. Re-record with --record.")
            continue

        if entry["spread_percent"] > max_spread:
            unresolved.append(
                f"{size} flows: allocation spread {entry['spread_percent']:.3f}% over the "
                f"{max_spread}% limit. The same generator gave different answers about the "
                "same sources; the run cannot be compared to anything.")
            continue

        measured = entry["allocated_bytes"]
        drift = 100 * (measured - expected["allocatedBytes"]) / expected["allocatedBytes"]

        if drift > threshold:
            blocking.append(
                f"{size} flows: FlowPlanGenerator allocated {measured:,} B, "
                f"{drift:+.2f}% against the committed {expected['allocatedBytes']:,} B "
                f"(threshold +{threshold}%). "
                f"{measured / expected['allocatedBytes']:.2f}x the baseline.")
        elif drift < -stale:
            advisory.append(
                f"{size} flows: {drift:+.2f}% — the generator got cheaper and the committed "
                "baseline is now pessimistic. Re-record it with --record so the next "
                "regression is measured against what the generator actually costs.")

        if entry["generated_chars"] != expected["generatedChars"]:
            delta = entry["generated_chars"] - expected["generatedChars"]
            advisory.append(
                f"{size} flows: emitted source changed by {delta:+,} characters "
                f"({entry['generated_chars']:,} against {expected['generatedChars']:,}). "
                "Cost that arrives with more emitted code is a different fact from cost "
                "that arrives with none.")

        if entry["diagnostics"] != expected["diagnostics"]:
            advisory.append(
                f"{size} flows: the generator produced {entry['diagnostics']} diagnostics, "
                f"baseline {expected['diagnostics']}.")

    if blocking:
        return FAIL, blocking, advisory
    if unresolved:
        return INCONCLUSIVE, unresolved, advisory
    return PASS, [], advisory


def report(samples: dict, baseline: dict) -> None:
    print(f"{'flows':>7} {'allocated B':>16} {'baseline B':>16} {'drift':>9} "
          f"{'spread':>9} {'elapsed':>10}")
    print("-" * 72)

    for entry in samples["sizes"]:
        expected = baseline["sizes"].get(str(entry["flows"]))
        if expected is None:
            print(f"{entry['flows']:7d} {entry['allocated_bytes']:16,d} {'—':>16} {'—':>9} "
                  f"{entry['spread_percent']:8.3f}% {entry['elapsed_ms']:9.0f}ms")
            continue

        drift = 100 * (entry["allocated_bytes"] - expected["allocatedBytes"]) / expected["allocatedBytes"]
        print(f"{entry['flows']:7d} {entry['allocated_bytes']:16,d} "
              f"{expected['allocatedBytes']:16,d} {drift:+8.2f}% "
              f"{entry['spread_percent']:8.3f}% {entry['elapsed_ms']:9.0f}ms")


def absolute_criterion(baseline: dict) -> None:
    """Reprint P1's actual exit criterion, whatever the relative gate decided.

    A relative gate answers "did this change make it worse". It cannot answer "is it good
    enough", and the whole reason this package exists is that a criterion nobody could act
    on stopped being watched. Printing it on every run costs nothing and keeps it read.
    """
    criterion = baseline.get("absoluteCriterion")
    if not criterion:
        return

    print()
    print("=" * 72)
    print(f"ABSOLUTE CRITERION — {criterion['status']}. This gate does not measure it.")
    print("=" * 72)
    print(f"    P1 exit criterion : {criterion['criterion']}")
    print(f"    last measured     : {criterion['lastMeasured']}")
    print(f"    measured by       : {criterion['measuredBy']}")
    print(f"    recorded in       : {criterion['recordedIn']}")
    print()
    print("    A pass above means this change did not make the generator more expensive.")
    print("    It does not mean the build overhead budget is met. It is not.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("samples", help="JSON written by measure-generator-cost.py --json")
    parser.add_argument("--baseline", default=str(DEFAULT_BASELINE))
    parser.add_argument("--record", action="store_true",
                        help="Rewrite the baseline from these samples. Deliberate act; "
                             "commit the diff.")
    parser.add_argument("--threshold", type=float,
                        help="Override the baseline's regression threshold, in percent.")
    args = parser.parse_args()

    baseline_path = pathlib.Path(args.baseline)

    try:
        samples = json.loads(pathlib.Path(args.samples).read_text(encoding="utf-8"))
        baseline = json.loads(baseline_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        die(f"Could not read the inputs: {error}")

    if args.record:
        record(samples, baseline, baseline_path)
        return EXIT[PASS]

    tolerances = baseline["tolerances"]
    threshold = args.threshold if args.threshold is not None else tolerances["regressionPercent"]

    report(samples, baseline)

    verdict, failures, advisory = check(
        samples, baseline, threshold,
        tolerances["staleBaselinePercent"], tolerances["maxSpreadPercent"])

    print()
    for note in advisory:
        print(f"::notice::advisory — {note}")

    if verdict == FAIL:
        print()
        for failure in failures:
            print(f"::error::{failure}")
        print(f"\nVERDICT: FAIL — {len(failures)} blocking gate failure(s).")
    elif verdict == INCONCLUSIVE:
        print()
        for reason in failures:
            print(f"::warning::{reason}")
        print("\nVERDICT: INCONCLUSIVE — the gate did not run. This is not a regression "
              "and it is not a pass.")
    else:
        print(f"\nVERDICT: PASS — generator cost within +{threshold}% of the committed "
              f"baseline ({len(advisory)} advisory note(s)).")

    absolute_criterion(baseline)

    return EXIT[verdict]


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Judge a cold-start run against V5.

    dotnet run --project tests/FlowX.ColdStart.Bench -c Release -- --no-publish
    ./scripts/check-cold-start.py .artifacts/cold-start.json

Exit codes:  0 PASS   1 FAIL   2 INCONCLUSIVE   3 the results document is unreadable.
The four scripts/check-durability-latency.py and scripts/check-generator-cost.py use.

The rig's JSON is the input, never its console output: the rig prints a per-run line for
every start and a human summary at the end, and a gate that greps a summary line is a gate
that changes meaning when somebody rewords a sentence.


What is gated, and on which percentile
--------------------------------------

  V5   cold start, NativeAOT   ≤ 200 ms   —   judged on flow.firstResponse p50

The ceiling is the criterion's own number. The percentile is not: V5 says "≤ 200 ms" and
does not name a rank, so the gate has to pick one, and docs/benchmarks/V5-cold-start.md
argues the median. Two runs of the same commit on the same container reported p50 63.0 and
64.2 ms — 2 % apart — while their p99, which with n = 30 IS the worst of thirty starts,
moved 102.2 → 128.5 ms, 26 %. A gate on the tail would fire on the container's scheduler
rather than on the code. The margin at the median is 3.2x, so a gate there is decidable on
shared hardware, which is the only hardware CI has.

The tail is REPORTED and not gated: p95, p99, min, max, the port-open phase, resident set
and the binary's size are all printed on every run, passing or failing. A number nobody
prints is a number nobody watches, and the rig existed for two working packages before
anything compared it to anything.


What makes a run INCONCLUSIVE rather than a FAIL
------------------------------------------------

  * the rig published ReadyToRun. V5 names NativeAOT; a ReadyToRun start is a real number
    and a different fact, and the rig labels its own verdict `NOT V5` for the same reason.
  * fewer than MIN_RUNS measured starts. A median over a handful of starts is one start.

Neither is a regression and neither is a pass, which is the distinction
docs/benchmarks/QR2-chaos.md is about.
"""

import argparse
import json
import sys

PASS, FAIL, INCONCLUSIVE, BROKEN = "PASS", "FAIL", "INCONCLUSIVE", "BROKEN"
EXIT = {PASS: 0, FAIL: 1, INCONCLUSIVE: 2, BROKEN: 3}

# docs/01-Vision.md §7, criterion V5.
CEILING_MS = 200.0

# The phase the ceiling is about: process start to a served flow response, not to an open
# port. docs/benchmarks/V5-cold-start.md §"What was measured" is why the rig records both.
GATED_PHASE = "flow.firstResponse"
GATED_PERCENTILE = "p50"

# Below this the median is not a median. Thirty is what the rig defaults to.
MIN_RUNS = 10


def load(path):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def phase(document, name):
    for entry in document.get("phases", []):
        if entry.get("name") == name:
            return entry
    return None


def judge(document, budget):
    """Return (verdict, blocking, refusals) for one results document."""
    blocking, refusals = [], []

    served = phase(document, GATED_PHASE)

    if served is None:
        return BROKEN, [f"The results document carries no '{GATED_PHASE}' phase."], []

    mode = document.get("subject", {}).get("publishMode", "unknown")

    if mode != "NativeAOT":
        refusals.append(
            f"The rig published {mode}. V5 is stated over NativeAOT, so this run measured a "
            "different binary's start-up and cannot decide the criterion either way.")

    runs = served.get("runs", 0)

    if runs < MIN_RUNS:
        refusals.append(
            f"{runs} measured start(s). A {GATED_PERCENTILE} over fewer than {MIN_RUNS} is "
            "one or two starts wearing a percentile's name.")

    measured = served.get(GATED_PERCENTILE)

    if measured is None:
        return BROKEN, [f"'{GATED_PHASE}' carries no {GATED_PERCENTILE}."], refusals

    # The gate. Only reached with a NativeAOT run of enough starts — a refusal above already
    # means the number describes something other than V5, and comparing it would attribute
    # that difference to the code.
    if not refusals and measured > budget:
        blocking.append(
            f"V5: cold start {measured:.1f} ms at {GATED_PERCENTILE} against a "
            f"{budget:.0f} ms ceiling ({measured / budget:.2f}x). The tail is reported "
            "above and is not what this fails on.")

    if blocking:
        return FAIL, blocking, refusals
    if refusals:
        return INCONCLUSIVE, blocking, refusals
    return PASS, blocking, refusals


def report(document, verdict, blocking, refusals, budget):
    subject = document.get("subject", {})
    machine = document.get("machine", {})
    run = document.get("run", {})

    print(f"## Cold start (V5) — {verdict}")
    print()
    print(f"{subject.get('publishMode', 'unknown')} publish of "
          f"{subject.get('sample', 'unknown')}, {subject.get('endpoint', 'unknown')}")
    print(f"{machine.get('processorCount', '?')} cores, "
          f"{machine.get('dotnet', 'unknown runtime')}, "
          f"{run.get('measured', '?')} measured starts after "
          f"{run.get('discardedWarmups', '?')} discarded")
    print()
    print(f"{'phase':<24} {'n':>4} {'min':>9} {'p50':>9} {'p95':>9} {'p99':>9} "
          f"{'max':>9}  unit")
    print("-" * 84)

    for entry in document.get("phases", []):
        gated = " <- gated" if entry.get("name") == GATED_PHASE else ""
        print(f"{entry.get('name', '?'):<24} {entry.get('runs', 0):>4} "
              f"{entry.get('min', 0):>9.1f} {entry.get('p50', 0):>9.1f} "
              f"{entry.get('p95', 0):>9.1f} {entry.get('p99', 0):>9.1f} "
              f"{entry.get('max', 0):>9.1f}  {entry.get('unit', '')}{gated}")

    print()
    binary_bytes = subject.get("binaryBytes", 0)
    directory_bytes = subject.get("publishDirectoryBytes", 0)
    print(f"binary {binary_bytes:,} B ({binary_bytes / 1024 / 1024:.1f} MiB); "
          f"whole publish directory {directory_bytes / 1024 / 1024:.1f} MiB — reported, "
          "not gated")
    print(f"first start after the publish {run.get('firstStartAfterPublishMs', 0):.1f} ms "
          "— paid once per deployment, so it is discarded rather than sampled")
    print()

    for failure in blocking:
        print(f"::error::{failure}")
    for reason in refusals:
        print(f"::warning::{reason}")

    print()
    if verdict == PASS:
        served = phase(document, GATED_PHASE) or {}
        print(f"VERDICT: PASS — {served.get(GATED_PERCENTILE, 0):.1f} ms at "
              f"{GATED_PERCENTILE} against the {budget:.0f} ms ceiling.")
    elif verdict == FAIL:
        print(f"VERDICT: FAIL — {len(blocking)} blocking failure(s).")
    elif verdict == BROKEN:
        print("VERDICT: the results document does not answer the question it is shaped like.")
    else:
        print("VERDICT: INCONCLUSIVE — the run did not measure V5. This is not a regression "
              "and it is not a pass.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", help="The rig's JSON output.")
    parser.add_argument("--budget", type=float, default=CEILING_MS,
                        help=f"The ceiling in milliseconds. Default {CEILING_MS:.0f}, which "
                             "is V5's own. Lower it to prove the gate can fail.")
    arguments = parser.parse_args()

    try:
        document = load(arguments.results)
    except (OSError, json.JSONDecodeError, TypeError) as failure:
        print(f"::error::{arguments.results} could not be read: {failure}")
        return EXIT[BROKEN]

    verdict, blocking, refusals = judge(document, arguments.budget)

    report(document, verdict, blocking, refusals, arguments.budget)

    return EXIT[verdict]


if __name__ == "__main__":
    sys.exit(main())

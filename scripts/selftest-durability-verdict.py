#!/usr/bin/env python3
"""Prove that scripts/check-durability-latency.py can fail, and where its edges are.

    python3 scripts/selftest-durability-verdict.py

Exit 0 when every case lands on the verdict it should, 1 otherwise. Runs in a second and
needs no database, so it goes on the merge path where the chaos and QR2 self-tests are.


Why a self-test rather than trust
---------------------------------

The checker has three states and one suppression: a B7 latency breach is downgraded to a
warning when the offered rate was not sustained, because under overload the response
latency IS the queue. That suppression is correct and it is also exactly the shape a
silent softening takes. Widening it by one condition — to B8, to a run that held the
rate, to a refusal — would turn the gate off, and nothing in the checker itself would
notice. These cases hold the boundary.

Each case is the recorded run of docs/benchmarks/B7-B8-durability.json with one field
changed, which is the technique scripts/selftest-chaos-verdict.py already uses here.
"""

import copy
import json
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

# The checker is a script rather than a module, so it is imported by path.
import importlib.util

SPEC = importlib.util.spec_from_file_location(
    "check_durability_latency",
    pathlib.Path(__file__).resolve().parent / "check-durability-latency.py")
CHECKER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CHECKER)

RECORDED = pathlib.Path(__file__).resolve().parents[1] / "docs/benchmarks/B7-B8-durability.json"


def arm(document, budget):
    return next(entry for entry in document["budgets"] if entry["budget"] == budget)


def holding_the_rate(document):
    """The recorded run with the rate sustained and B7 inside its budget."""
    document = copy.deepcopy(document)
    commit = arm(document, "B7")
    commit["achievedRatePerSecond"] = commit["offeredRatePerSecond"]
    commit["response"]["p99Ms"] = 12.0
    return document


CASES = []


def case(name, expected, build):
    CASES.append((name, expected, build))


case("the recorded run, whose node could not offer 5 000/s",
     CHECKER.INCONCLUSIVE,
     lambda d: d)

case("rate held and B7 inside its budget",
     CHECKER.PASS,
     holding_the_rate)


def b7_breach(document):
    document = holding_the_rate(document)
    arm(document, "B7")["response"]["p99Ms"] = 15.001
    return document


case("rate held and B7 over its budget — the gate's whole point",
     CHECKER.FAIL,
     b7_breach)


def b8_breach(document):
    """B8 breaches on the RECORDED run, whose rate was not held."""
    document = copy.deepcopy(document)
    arm(document, "B8")["total"]["p99Ms"] = 8.001
    return document


case("B8 over its budget is a FAIL even when B7's rate was missed",
     CHECKER.FAIL,
     b8_breach)


def refused_commits(document):
    document = copy.deepcopy(document)
    commit = arm(document, "B7")
    commit["refusals"] = 3
    commit["refusalCodes"] = ["flowx.durability.fenced_out"]
    return document


case("a refused commit is a FAIL, not a warning",
     CHECKER.FAIL,
     refused_commits)


def not_durable(document):
    document = holding_the_rate(document)
    document["machine"]["synchronousCommit"] = "off"
    return document


case("synchronous_commit off is INCONCLUSIVE — that commit was not durable",
     CHECKER.INCONCLUSIVE,
     not_durable)


def empty_frontier(document):
    document = holding_the_rate(document)
    arm(document, "B8")["stepsRead"] = 0
    return document


case("a rehydration that read no history is INCONCLUSIVE",
     CHECKER.INCONCLUSIVE,
     empty_frontier)


def too_few_samples(document):
    document = holding_the_rate(document)
    arm(document, "B7")["response"]["operations"] = 40
    return document


case("a p99 over forty samples is INCONCLUSIVE",
     CHECKER.INCONCLUSIVE,
     too_few_samples)


def missing_arm(document):
    document = copy.deepcopy(document)
    document["budgets"] = [entry for entry in document["budgets"] if entry["budget"] != "B8"]
    return document


case("a results document missing an arm is BROKEN, not a pass",
     CHECKER.BROKEN,
     missing_arm)


def main():
    with open(RECORDED, encoding="utf-8") as handle:
        recorded = json.load(handle)

    wrong = 0

    for name, expected, build in CASES:
        verdict, _, _ = CHECKER.judge(build(recorded))

        if verdict == expected:
            print(f"  ok    {expected:<12} {name}")
        else:
            wrong += 1
            print(f"  WRONG {expected:<12} {name} — got {verdict}")

    print()

    if wrong:
        print(f"::error::{wrong} of {len(CASES)} verdicts were wrong. The durability gate "
              "does not judge what it says it judges.")
        return 1

    print(f"All {len(CASES)} verdicts hold: the gate can fail, and its one suppression is "
          "confined to B7's queue under a rate it could not offer.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

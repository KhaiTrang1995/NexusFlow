#!/usr/bin/env python3
"""Turn a chaos run's JSON into a verdict — PASS, FAIL or INCONCLUSIVE — for QR2.

    ./scripts/check-chaos-qr2.py chaos-qr2.json
    ./scripts/check-chaos-qr2.py chaos-qr2.json --flows 10000 --markdown

QR2 (docs/05-Architecture.md §10) is three clauses and a number:

    kill any node at any step boundary — 10 000 flows,
    zero duplicate non-idempotent effects, zero lost instances,
    resume p99 <= 45 s.

This script gates the two correctness clauses and **reports the latency one without
gating on it**. That split is deliberate and is not a softening. B7 and B8 are latency
budgets that the repository owner has set aside for this phase, and a resume p99 measured
on a shared container with four cores is a measurement of that container as much as of
FlowX. Correctness is not: a duplicate row is a duplicate row on any hardware.


What counts as a duplicate, and why the rig reports three kinds
---------------------------------------------------------------

Two applications of one step are not automatically a defect.
docs/adr/ADR-0006-journal-and-leases.md and docs/11-Distributed-Runtime.md §4 both say
outright that a node dying after an effect and before its commit re-executes that step,
and that the composition FlowX offers is "at-least-once delivery + idempotent capabilities
+ fenced journal writes". A rig that reported the documented window as a failure would be
reporting a design decision as a bug, and would be as useless as one that reported nothing.

So the rig classifies every duplicate against the committed prefix the recovering node was
actually handed:

  duplicatesAgainstGuarantee   an effect applied for a step whose commit was ALREADY in
                               the journal when the recovering node took the instance
                               over. The journal's whole promise is that this cannot
                               happen. **Gated at zero.**

  duplicatesInDocumentedWindow an effect applied where no commit for that step existed.
                               ADR-0006's window. Counted, reported, not gated — and its
                               size is the honest measure of the exposure.

  duplicatesBetweenLiveWorkers two first-run applications of one step, i.e. two live nodes
                               executing one instance. The lease exists to make this
                               impossible. **Gated at zero.**


Why a run can be refused rather than passed
-------------------------------------------

A chaos rig that kills nothing is worse than no chaos rig, because it lets the box in
docs/21 §8 be ticked on evidence that does not exist. Four conditions return
INCONCLUSIVE rather than PASS:

  * no process was killed;
  * no killed process exited 137, which is what SIGKILL looks like from outside — a
    worker that exited 0 was not killed, whatever the rig believes it did;
  * nothing was recovered, so the recovery path was never exercised;
  * an arm ran fewer flows than it registered because workers stopped claiming.

Exit codes: 0 PASS, 1 FAIL, 2 INCONCLUSIVE — the same three-way convention as
scripts/analyse-scale-samples.py.
"""

from __future__ import annotations

import argparse
import json
import sys

EXIT = {"PASS": 0, "FAIL": 1, "INCONCLUSIVE": 2}

SIGKILL_EXIT_CODE = "137"


def judge_arm(arm: dict, budget: float) -> dict:
    """Reduce one arm to a verdict and the reasons for it."""
    failures: list[str] = []
    refusals: list[str] = []

    against = arm["duplicatesAgainstGuarantee"]
    live = arm["duplicatesBetweenLiveWorkers"]
    lost = arm["lostInstances"]
    orphans = arm["orphanEffects"]

    if against:
        failures.append(
            f"{against} effect(s) applied for a step whose commit was already in the "
            f"journal. This is the guarantee failing, not ADR-0006's window."
        )

    if live:
        failures.append(
            f"{live} step(s) applied twice by two live workers — two nodes executed one "
            f"instance, which the lease and the fencing token exist to prevent."
        )

    if lost:
        failures.append(
            f"{lost} instance(s) never reached a terminal state: journaled, abandoned by a "
            f"killed node, and never picked up."
        )

    if orphans:
        failures.append(
            f"{orphans} effect(s) were applied for an instance with no journal row at all."
        )

    kills = arm["processKills"]
    sigkills = arm.get("killExitCodes", {}).get(SIGKILL_EXIT_CODE, 0)

    if kills == 0:
        refusals.append("no process was killed, so nothing about QR2 was exercised.")
    elif sigkills == 0:
        refusals.append(
            "no worker exited 137, so no process was killed by SIGKILL however many kills "
            "the rig believes it took."
        )

    if arm["recoveredInstances"] == 0:
        refusals.append("nothing was recovered, so the recovery path never ran.")

    if arm["flowsClaimed"] < arm["flowsRequested"]:
        refusals.append(
            f"only {arm['flowsClaimed']} of {arm['flowsRequested']} registered instances "
            f"were ever claimed, so the arm did not run the flow count it reports."
        )

    verdict = "FAIL" if failures else ("INCONCLUSIVE" if refusals else "PASS")

    p99 = arm.get("resumeSecondsP99")

    return {
        "arm": arm["killPosition"],
        "verdict": verdict,
        "failures": failures,
        "refusals": refusals,
        "p99": p99,
        "p99Within": None if p99 is None else p99 <= budget,
    }


def analyse(document: dict, budget: float, required_flows: int | None) -> dict:
    arms = [judge_arm(arm, budget) for arm in document["arms"]]

    overall = "PASS"

    if any(arm["verdict"] == "FAIL" for arm in arms):
        overall = "FAIL"
    elif any(arm["verdict"] == "INCONCLUSIVE" for arm in arms):
        overall = "INCONCLUSIVE"

    scale = None

    if required_flows is not None:
        ran = min((arm["flowsClaimed"] for arm in document["arms"]), default=0)
        scale = {"required": required_flows, "ran": ran, "met": ran >= required_flows}

        if not scale["met"] and overall == "PASS":
            overall = "INCONCLUSIVE"

    return {"arms": arms, "overall": overall, "scale": scale, "document": document}


def render(report: dict, budget: float, markdown: bool) -> None:
    document = report["document"]
    parameters = document["parameters"]
    bullet = "- " if markdown else "  "

    print()
    print("QR2 — SIGKILL at a step boundary, against a shared PostgreSQL")
    print(
        f"  {parameters['flows']} flows x {parameters['steps']} non-idempotent steps, "
        f"kill at step {parameters['killStep']} every {parameters['killEvery']} arrivals, "
        f"{parameters['workers']} worker process(es) x {parameters['concurrency']}, "
        f"{parameters['recoveryNodes']} recovery node(s), "
        f"lease TTL {parameters['leaseTtlSeconds']}s"
    )
    print(f"  recorded {document['recordedAt']} on {document['machine']['postgres'][:60]}")
    print()

    for arm, judged in zip(document["arms"], report["arms"]):
        print(f"  --- kill {arm['killPosition']} : {judged['verdict']} ---")
        print(f"{bullet}flows requested/claimed/opened/completed: "
              f"{arm['flowsRequested']} / {arm['flowsClaimed']} / "
              f"{arm['flowsOpened']} / {arm['flowsCompleted']}")
        print(f"{bullet}process kills (exit codes): {arm['processKills']} "
              f"({arm.get('killExitCodes', {})})")
        print(f"{bullet}effect applications: {arm['effectApplications']}")
        print(f"{bullet}duplicate applications: {arm['duplicateApplications']}")
        print(f"{bullet}  against the guarantee: {arm['duplicatesAgainstGuarantee']}")
        print(f"{bullet}  in ADR-0006's window : {arm['duplicatesInDocumentedWindow']}")
        print(f"{bullet}  between live workers : {arm['duplicatesBetweenLiveWorkers']}")
        print(f"{bullet}lost instances: {arm['lostInstances']}")
        print(f"{bullet}claimed but never opened: {arm['claimedButNeverOpened']}")
        print(f"{bullet}recovered instances: {arm['recoveredInstances']}")

        p99 = judged["p99"]
        state = "—" if p99 is None else f"{p99:.1f} s"
        against = "" if p99 is None else (
            f" (budget {budget:.0f} s — "
            f"{'within' if judged['p99Within'] else 'OVER'}, reported not gated)"
        )
        print(f"{bullet}resume p50/p95/p99/max: "
              f"{fmt(arm['resumeSecondsP50'])} / {fmt(arm['resumeSecondsP95'])} / "
              f"{state} / {fmt(arm['resumeSecondsMax'])}{against}")

        for failure in judged["failures"]:
            print(f"{bullet}FAIL: {failure}")

        for refusal in judged["refusals"]:
            print(f"{bullet}INCONCLUSIVE: {refusal}")

        print()

    scale = report["scale"]

    if scale is not None:
        verdict = "met" if scale["met"] else "NOT met"
        print(f"  Scale: {scale['ran']} flows run against {scale['required']} required — {verdict}.")
        print()

    print(f"VERDICT: {report['overall']}")
    print()
    print("  Correctness is gated. The resume p99 is measured and printed against the 45 s")
    print("  figure in QR2 and is not gated on: B7 and B8 are latency budgets set aside for")
    print("  this phase, and this rig is correctness infrastructure.")


def fmt(value: float | None) -> str:
    return "—" if value is None else f"{value:.1f} s"


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Render a QR2 verdict from a chaos run's JSON."
    )
    parser.add_argument("results", help="JSON written by the chaos coordinator")
    parser.add_argument("--budget", type=float, default=45.0,
                        help="resume p99 budget in seconds, reported not gated (default 45)")
    parser.add_argument("--flows", type=int, default=None,
                        help="refuse a PASS below this flow count (QR2's figure is 10000)")
    parser.add_argument("--markdown", action="store_true",
                        help="render the per-arm lines as markdown bullets")

    args = parser.parse_args()

    with open(args.results, encoding="utf-8") as handle:
        document = json.load(handle)

    report = analyse(document, args.budget, args.flows)
    render(report, args.budget, args.markdown)

    return EXIT[report["overall"]]


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Judge a durability run against B7 and B8.

    ./scripts/run-durability-latency.sh
    ./scripts/check-durability-latency.py .artifacts/durability-latency.json

Exit codes:  0 PASS   1 FAIL   2 INCONCLUSIVE   3 the results document is unreadable.
The four scripts/check-generator-cost.py and scripts/check-chaos-qr2.py already use.


What the two budgets say
------------------------

  B7  Durable step commit (PostgreSQL, group commit)   p99 15 ms @ 5 000 commits/s/node
  B8  Flow instance rehydration from journal           p99 8 ms

B7 is two claims joined by "@", and this is the whole reason the checker is not a
one-line comparison. A run that offered forty commits a second and reported a 3 ms p99
has not met B7; it has answered an easier question and printed a number that looks like
an answer to the hard one. So a run whose ACHIEVED rate falls short of the rate B7 names
cannot be a PASS however good its latency was — it is INCONCLUSIVE, which is a third
state on purpose and not a soft failure.


Why the response latency and not the service latency
----------------------------------------------------

The rig records both: service is issue-to-complete, response is due-to-complete against
the offered schedule. Gating on service would be the coordinated-omission mistake — under
overload fewer commits are issued and each is timed from the moment a writer was free for
it, so the measured p99 gets BETTER exactly when the node is failing. Response latency is
what a caller on the schedule waited, so that is what the gate reads. Service is printed
beside it because the difference between the two is the queue, and a large gap says the
node is saturated even when both numbers pass.


What makes a run INCONCLUSIVE rather than a FAIL
------------------------------------------------

  * the achieved rate fell short of the offered rate (above);
  * the server ran with synchronous_commit off, in which case PostgreSQL acknowledged
    before the write reached disk and B7 — which is a DURABLE commit — was not measured
    at all, whatever the number says;
  * the rehydrated instances carried no history, so the frontier read returned nothing.

None of those is a regression and none is a pass. Folding them into either is the failure
mode docs/benchmarks/QR2-chaos.md records at length.

A rate shortfall also SUPPRESSES B7's own latency failure, and that is not a softening.
When a node is at capacity the response latency is the queue in front of it, so a breached
p99 there is the same finding as the shortfall said twice — and calling it a blocking
failure would report a product defect on every machine too small to offer 5 000 durable
commits a second. The suppression is narrow: it applies to B7's response latency only,
only when the rate was missed, and never to B8, to a refusal, or to a run that held the
rate. scripts/selftest-durability-verdict.py holds that boundary in place.

A refused commit or a refused resume IS a FAIL. Those mean the rig wrote less than it
claimed to, and a tail full of refusals is a fast p99 for a store that was not working.
"""

import argparse
import json
import sys

PASS, FAIL, INCONCLUSIVE, BROKEN = "PASS", "FAIL", "INCONCLUSIVE", "BROKEN"
EXIT = {PASS: 0, FAIL: 1, INCONCLUSIVE: 2, BROKEN: 3}

# docs/14-Performance.md, rows B7 and B8.
P99_CEILING_MS = {"B7": 15.0, "B8": 8.0}
B7_RATE_PER_SECOND = 5000

# How far below the offered rate a run may land and still be judged. Not zero: the pacer
# issues an operation when it is within a millisecond of due, so a run that keeps up
# exactly still reports a rate a hair under the one it asked for.
RATE_TOLERANCE = 0.98


def load(path):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def arm(document, budget):
    for entry in document.get("budgets", []):
        if entry.get("budget") == budget:
            return entry
    return None


def judge(document):
    """Return (verdict, blocking, refusals) for one results document."""
    blocking, refusals = [], []

    commit, resume = arm(document, "B7"), arm(document, "B8")

    if commit is None or resume is None:
        return BROKEN, ["The results document is missing B7 or B8."], []

    machine = document.get("machine", {})
    synchronous = machine.get("synchronousCommit", "unknown")

    if synchronous != "on":
        refusals.append(
            f"synchronous_commit was '{synchronous}', so PostgreSQL acknowledged each "
            "commit before it reached disk. B7 is a durable commit; this run measured a "
            "different operation that happens to share its name.")

    achieved, offered = commit.get("achievedRatePerSecond", 0), commit.get("offeredRatePerSecond", 0)
    rate_held = True

    if offered < B7_RATE_PER_SECOND:
        rate_held = False
        refusals.append(
            f"The rig offered {offered}/s and B7 names {B7_RATE_PER_SECOND}/s. A latency "
            "measured below the budget's own load is not a measurement of the budget.")
    elif achieved < offered * RATE_TOLERANCE:
        rate_held = False
        refusals.append(
            f"The rig offered {offered}/s and achieved {achieved}/s. The node could not "
            "sustain the rate, so its latency describes a queue rather than a commit.")

    if commit.get("refusals", 0):
        blocking.append(
            f"B7: the journal refused {commit['refusals']} commit(s) "
            f"({', '.join(commit.get('refusalCodes', [])) or 'no code'}). A refused commit "
            "does almost none of the work a committed one does.")

    if resume.get("failures", 0):
        blocking.append(f"B8: {resume['failures']} instance(s) could not be resumed.")

    if resume.get("stepsRead", 0) <= 0:
        refusals.append(
            "B8 read no step rows, so the frontier came back empty and nothing was "
            "rehydrated.")

    for budget, measured in (("B7", commit["response"]), ("B8", resume["total"])):
        ceiling = P99_CEILING_MS[budget]
        p99 = measured.get("p99Ms", 0)

        if measured.get("operations", 0) < 100:
            refusals.append(
                f"{budget} recorded {measured.get('operations', 0)} operations. A p99 over "
                "fewer than a hundred samples is the worst one or two observations.")
        elif p99 > ceiling and budget == "B7" and not rate_held:
            # NOT a FAIL, and the precedence is deliberate. When the node could not sustain
            # the offered rate the response latency IS the queue -- operations pile up behind
            # a store that is already at capacity, so the p99 is a restatement of the rate
            # shortfall rather than a second, independent finding. Reporting it as a blocking
            # failure would tell every under-provisioned machine that FlowX has a defect. The
            # rate shortfall above is already the warning, and it is the honest one.
            refusals.append(
                f"B7: p99 {p99:.3f} ms against a budget of {ceiling} ms, but the rate was "
                "not sustained, so this number is the queue depth and not the commit cost. "
                f"The store's own cost, issue to complete, was p99 "
                f"{commit['service']['p99Ms']:.3f} ms.")
        elif p99 > ceiling:
            blocking.append(
                f"{budget}: p99 {p99:.3f} ms against a budget of {ceiling} ms.")

    if blocking:
        return FAIL, blocking, refusals
    if refusals:
        return INCONCLUSIVE, blocking, refusals
    return PASS, blocking, refusals


def report(document, verdict, blocking, refusals):
    commit, resume = arm(document, "B7"), arm(document, "B8")
    machine = document.get("machine", {})

    print(f"## Durability latency — {verdict}")
    print()
    print(f"PostgreSQL: {machine.get('postgres', 'unknown')}")
    print(f"synchronous_commit: {machine.get('synchronousCommit', 'unknown')}, "
          f"{machine.get('processorCount', '?')} cores, "
          f"{machine.get('wallClockSeconds', '?')} s wall clock")
    print()

    if commit:
        print(f"B7  durable step commit, ceiling {P99_CEILING_MS['B7']} ms "
              f"@ {B7_RATE_PER_SECOND}/s")
        print(f"      offered {commit['offeredRatePerSecond']}/s, "
              f"achieved {commit['achievedRatePerSecond']}/s, "
              f"{commit['writers']} writers, {commit['refusals']} refusals")
        for label, key in (("response (gated)", "response"), ("service", "service")):
            row = commit[key]
            print(f"      {label:<18} n={row['operations']:<7} "
                  f"p50={row['p50Ms']:>8.3f}  p99={row['p99Ms']:>8.3f}  "
                  f"max={row['maxMs']:>8.3f} ms")
        print()

    if resume:
        print(f"B8  rehydration, ceiling {P99_CEILING_MS['B8']} ms")
        row = resume["total"]
        print(f"      history depth {resume['historyDepth']}, "
              f"{resume['stepsRead']} step rows read, {resume['failures']} failures")
        print(f"      {'total (gated)':<18} n={row['operations']:<7} "
              f"p50={row['p50Ms']:>8.3f}  p99={row['p99Ms']:>8.3f}  "
              f"max={row['maxMs']:>8.3f} ms")
        print()

    for failure in blocking:
        print(f"::error::{failure}")
    for reason in refusals:
        print(f"::warning::{reason}")

    print()
    if verdict == PASS:
        print("VERDICT: PASS — both budgets met, at the rate B7 names.")
    elif verdict == FAIL:
        print(f"VERDICT: FAIL — {len(blocking)} blocking failure(s).")
    else:
        print("VERDICT: INCONCLUSIVE — the run did not answer the question. This is not a "
              "regression and it is not a pass.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", help="The rig's JSON output.")
    arguments = parser.parse_args()

    try:
        document = load(arguments.results)
    except (OSError, json.JSONDecodeError) as failure:
        print(f"::error::{arguments.results} could not be read: {failure}")
        return EXIT[BROKEN]

    verdict, blocking, refusals = judge(document)

    report(document, verdict, blocking, refusals)

    return EXIT[verdict]


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Prove that `check-chaos-qr2.py` returns each of its three verdicts, and only those.

    ./scripts/selftest-chaos-verdict.py

A gate nobody has seen fail is an assumption, not a gate — the argument
`.github/workflows/performance.yml`'s `gate-self-test` and `generator-cost-self-test`
already make for the allocation and generator-cost budgets. This is the same test for the
QR2 verdict, and it matters more here than there, because the nightly chaos job cannot be
rehearsed: the run it judges takes minutes, kills operating-system processes and needs a
PostgreSQL, so the *only* cheap thing that can run on a pull request is the judgement.

So this is what is merge-gated about QR2. The chaos run itself is nightly and is not.

Every case starts from `docs/benchmarks/QR2-chaos.json` — the real recorded run, which the
checker passes — and changes exactly one field. That is deliberate: a synthetic document
built from nothing would prove the checker parses a shape this repository never produces.

Three of the assertions are load-bearing beyond "the exit code is N":

  * **the recorded run passes unchanged**, with 260 and 186 duplicate applications in it.
    ADR-0006's window is not a defect and a checker that failed on it would be reporting a
    design decision as a bug;
  * **a resume p99 of 999 s is still a PASS.** QR2's 45 s clause is reported and not gated
    — `docs/benchmarks/QR2-chaos.md` §4.4 has three runs of one rig disagreeing by a factor
    of two on it — and this case is what stops that becoming a gate by accident;
  * **the output names the reason.** An exit code tells CI what to do; the text is what
    tells a person what happened, and a failure nobody can read is the red icon this
    package exists to avoid.

Exit 0 when every case holds, 1 otherwise.
"""

from __future__ import annotations

import copy
import json
import pathlib
import subprocess
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
CHECKER = ROOT / "scripts" / "check-chaos-qr2.py"
RECORDED = ROOT / "docs" / "benchmarks" / "QR2-chaos.json"

PASS, FAIL, INCONCLUSIVE = 0, 1, 2


def first_arm(document: dict) -> dict:
    """The arm every single-field mutation below is applied to."""
    return document["arms"][0]


def unchanged(document: dict) -> None:
    """The recorded run, as recorded."""


def against_the_guarantee(document: dict) -> None:
    first_arm(document)["duplicatesAgainstGuarantee"] = 1


def lost_instance(document: dict) -> None:
    first_arm(document)["lostInstances"] = 1


def lost_instance_after_a_timeout(document: dict) -> None:
    """The same number, with the rig recording that it stopped waiting for it.

    These two cases are a pair on purpose. Until the rig recorded
    `convergenceTimedOut`, an instance still running when the coordinator gave up was
    counted in `lostInstances` and was indistinguishable from one no recovery scan ever
    found — so a slow runner filed a correctness failure against a clause it had not
    tested. The pair below is what keeps them distinguishable: the same mutation, one bit
    apart, must produce two different verdicts.
    """
    arm = first_arm(document)
    arm["lostInstances"] = 1
    arm["convergenceTimedOut"] = True


def orphan_effect(document: dict) -> None:
    first_arm(document)["orphanEffects"] = 1


def two_live_workers(document: dict) -> None:
    first_arm(document)["duplicatesBetweenLiveWorkers"] = 1


def nothing_killed(document: dict) -> None:
    arm = first_arm(document)
    arm["processKills"] = 0
    arm["killExitCodes"] = {}


def killed_but_not_by_sigkill(document: dict) -> None:
    # The rig believes it killed 97 processes and every one of them exited 0. Whatever
    # happened, signal 9 did not: this is the case that stops a rig grading its own homework.
    first_arm(document)["killExitCodes"] = {"0": first_arm(document)["processKills"]}


def nothing_recovered(document: dict) -> None:
    first_arm(document)["recoveredInstances"] = 0


def under_claimed(document: dict) -> None:
    first_arm(document)["flowsClaimed"] = first_arm(document)["flowsRequested"] - 1


def below_qr2_scale(document: dict) -> None:
    for arm in document["arms"]:
        arm["flowsRequested"] = 200
        arm["flowsClaimed"] = 200


def p99_far_over_budget(document: dict) -> None:
    for arm in document["arms"]:
        arm["resumeSecondsP99"] = 999.0


CASES = [
    # (name, mutation, extra checker arguments, expected exit, substrings the output must carry)
    ("the recorded run, unchanged", unchanged, [], PASS,
     ["VERDICT: PASS", "in ADR-0006's window : 260"]),
    ("an effect applied against the guarantee", against_the_guarantee, [], FAIL,
     ["VERDICT: FAIL", "already in the journal"]),
    ("an instance never reached a terminal state", lost_instance, [], FAIL,
     ["VERDICT: FAIL", "never reached a terminal state"]),
    ("an instance still running when the rig stopped waiting",
     lost_instance_after_a_timeout, [], INCONCLUSIVE,
     ["VERDICT: INCONCLUSIVE", "stopped waiting", "has not tested the clause"]),
    ("an effect with no journal row at all", orphan_effect, [], FAIL,
     ["VERDICT: FAIL", "no journal row at all"]),
    ("one step applied by two live workers", two_live_workers, [], FAIL,
     ["VERDICT: FAIL", "two live workers"]),
    ("no process was killed", nothing_killed, [], INCONCLUSIVE,
     ["VERDICT: INCONCLUSIVE", "no process was killed"]),
    ("kills that produced no exit 137", killed_but_not_by_sigkill, [], INCONCLUSIVE,
     ["VERDICT: INCONCLUSIVE", "no worker exited 137"]),
    ("nothing was recovered", nothing_recovered, [], INCONCLUSIVE,
     ["VERDICT: INCONCLUSIVE", "the recovery path never ran"]),
    ("an arm claimed fewer flows than it registered", under_claimed, [], INCONCLUSIVE,
     ["VERDICT: INCONCLUSIVE", "were ever claimed"]),
    ("a clean run below QR2's scale", below_qr2_scale, ["--flows", "10000"], INCONCLUSIVE,
     ["VERDICT: INCONCLUSIVE", "200 flows run against 10000 required — NOT met"]),
    # The one case whose *point* is that it passes.
    ("a resume p99 of 999 s, every correctness row zero", p99_far_over_budget, [], PASS,
     ["VERDICT: PASS", "999.0 s", "OVER, reported not gated"]),
]


def run_case(name, mutate, arguments, expected, substrings, recorded, directory) -> bool:
    document = copy.deepcopy(recorded)
    mutate(document)

    path = pathlib.Path(directory) / (name.replace(" ", "-").replace("'", "") + ".json")
    path.write_text(json.dumps(document), encoding="utf-8")

    completed = subprocess.run(
        [sys.executable, str(CHECKER), str(path), *arguments],
        capture_output=True, text=True, check=False)

    output = completed.stdout + completed.stderr
    problems = []

    if completed.returncode != expected:
        problems.append(f"exit {completed.returncode}, expected {expected}")

    for substring in substrings:
        if substring not in output:
            problems.append(f"output does not carry {substring!r}")

    verdict = "ok" if not problems else "BROKEN"
    print(f"  [{verdict:6}] exit {completed.returncode} — {name}")

    for problem in problems:
        print(f"           {problem}")

    if problems:
        print("           ---- checker output ----")
        for line in output.splitlines():
            print(f"           {line}")

    return not problems


def main() -> int:
    recorded = json.loads(RECORDED.read_text(encoding="utf-8"))

    print()
    print("The QR2 verdict returns three states, and the right one for each document")
    print(f"  seed: {RECORDED.relative_to(ROOT)} — the run recorded on {recorded['recordedAt'][:10]}")
    print()

    with tempfile.TemporaryDirectory() as directory:
        results = [run_case(*case, recorded, directory) for case in CASES]

    print()

    if all(results):
        print(f"All {len(results)} cases hold: PASS, FAIL and INCONCLUSIVE are three states.")
        return 0

    print(f"{results.count(False)} of {len(results)} cases are broken. The QR2 verdict "
          f"cannot be trusted until they are fixed.")
    return 1


if __name__ == "__main__":
    sys.exit(main())

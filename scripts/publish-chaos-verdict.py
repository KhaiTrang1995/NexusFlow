#!/usr/bin/env python3
"""Give the QR2 verdict a consequence, and make a failing one readable without a log dive.

    ./scripts/publish-chaos-verdict.py --code 1 --results .artifacts/chaos-qr2.json \\
        --verdict verdict.txt --log run.log            # dry run: prints what it would file
    ./scripts/publish-chaos-verdict.py ... --notify    # actually files it, needs gh + GH_TOKEN

WHY THIS EXISTS, AND WHY IT IS NOT JUST `exit $?`
--------------------------------------------------
`CHECKLIST.md` blocker **B-4** and the unnumbered package above `WP-31` in `PLAN.md` record
the same finding at length: the *Benchmark budgets* job has been blocking and red on `dev`
since 2026-07-31, through sixty-odd pushes, and sixteen bytes of allocation regression
crossed underneath it, because **nothing tells anyone**. Their words for the state a job
ends up in are worth quoting exactly, because this script exists to not reproduce it:

    A merge-class gate whose failure has no consequence is a nightly report with a
    red icon.

A nightly chaos job is *already* a nightly report. It cannot be made merge-blocking — it
runs on a schedule, against no pull request, and there is no author to hold responsible for
a `SIGKILL` that duplicated an effect. So the consequence has to be something other than a
merge check, and it has to reach a person rather than a dashboard:

  * the job goes red — necessary, and on its own exactly the red icon B-4 is about;
  * **an issue is opened, labelled, and assigned to the repository owner**, which is the
    part that notifies somebody. `docs/21-Quality-Gates.md` §7 already names this as the
    Release-class convention — *"nightly load test; regression opens a blocking issue"*;
  * the issue is **updated rather than duplicated** on a second red night, so the tracker
    does not fill with one issue per night and become its own thing nobody reads;
  * and it is **closed automatically on the next green night**, so an open issue always
    means the rig is unhappy *now*. A stale red that nobody closes decays into background
    noise, which is the same failure one step later.

WHAT IS AND IS NOT CLAIMED
--------------------------
This does not make QR2 "gated" on a merge, and no document in this repository should say it
does — `docs/21` §7 was corrected once for exactly that word. What is true: the run happens
every night, its verdict has three states, a non-PASS reaches a named human, and the
evidence is attached to the run.

THE THIRD STATE IS A THIRD STATE
--------------------------------
`scripts/check-chaos-qr2.py` returns 0 PASS, 1 FAIL, 2 INCONCLUSIVE, and INCONCLUSIVE means
*the run produced no evidence* — nothing was killed, or no worker exited 137, or nothing was
recovered, or an arm did not run the flows it registered. It is neither of the other two and
is not folded into either here:

  * folding it into PASS would tick `docs/21` §8's chaos row on evidence that does not
    exist, which is the one outcome the rig's own INCONCLUSIVE exit was written to prevent;
  * folding it into FAIL would report a correctness defect that was never observed.

So it gets its own issue, with its own title and its own text, and the job is red. Red for
both, distinguished everywhere a human reads: the annotation, the step summary and the
issue. GitHub gives a job two conclusions and QR2 has three verdicts; the third one lives in
the issue tracker, not in the icon.

`performance.yml`'s `generator-cost` job treats its INCONCLUSIVE as non-blocking, and that
is not a contradiction: that job runs on pull requests, where an unresolvable measurement
would fail somebody's change for the weather. This one runs at 03:41 against nobody's
change, so there is no such cost, and the cost of a false green is the whole of B-4.
"""

from __future__ import annotations

import argparse
import json
import os
import pathlib
import shutil
import subprocess
import sys

LABEL = "qr2-nightly"

# Anything not in this table is the checker having broken rather than judged: a missing
# results file, a traceback, a python that would not start. That is a fourth outcome and it
# is not silently a pass either — see `unexpected` below.
STATES = {
    0: {
        "verdict": "PASS",
        "headline": "QR2's correctness clauses held on this run.",
        "annotation": None,
        "job": 0,
    },
    1: {
        "verdict": "FAIL",
        "headline": "A correctness clause of QR2 did not hold.",
        "title": "QR2 nightly: FAIL — a correctness clause of QR2 is not holding",
        "annotation": "error",
        "job": 1,
    },
    2: {
        "verdict": "INCONCLUSIVE",
        "headline": "The chaos run produced no verdict — it is neither a pass nor a failure.",
        "title": "QR2 nightly: INCONCLUSIVE — the chaos run produced no verdict",
        "annotation": "error",
        "job": 1,
    },
}

UNEXPECTED = {
    "verdict": "BROKEN",
    "headline": "The verdict could not be produced at all.",
    "title": "QR2 nightly: the verdict could not be produced",
    "annotation": "error",
    "job": 1,
}


def read(path: str | None) -> str:
    if not path:
        return ""

    file = pathlib.Path(path)

    return file.read_text(encoding="utf-8", errors="replace") if file.is_file() else ""


def load(path: str | None) -> dict | None:
    text = read(path)

    if not text:
        return None

    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return None


def repro(document: dict | None) -> str | None:
    """The exact command line that produced this run, rebuilt from what it recorded."""
    if not document or "parameters" not in document:
        return None

    p = document["parameters"]

    return (
        "FLOWX_CHAOS=1 FLOWX_POSTGRES_CONNECTION=... ./scripts/run-chaos-qr2.sh \\\n"
        f"    --flows {p['flows']} --kill-every {p['killEvery']} --kill-step {p['killStep']} \\\n"
        f"    --workers {p['workers']} --concurrency {p['concurrency']} \\\n"
        f"    --recovery-nodes {p['recoveryNodes']} "
        f"--max-concurrent-recoveries {p['maxConcurrentRecoveries']} \\\n"
        f"    --lease-ttl {p['leaseTtlSeconds']} --scan-interval {p['scanIntervalSeconds']}"
    )


def diagnostics(document: dict | None, verdict_text: str, log: str, green: bool) -> list[str]:
    """The two or three lines that decide whether a red night is read or skimmed past.

    Everything here is derived from what the run recorded. None of it re-judges the run:
    the verdict is `check-chaos-qr2.py`'s and a second opinion computed here would be a
    second implementation of the same rule, which is the mistake `quality.yml`'s `debt` job
    documents at length. These say *where to look*, not *what the answer is*.
    """
    found: list[str] = []

    # First, because it changes how every other number below should be read. The coordinator
    # counts an instance still running when it gives up waiting as LOST — the same field a
    # genuinely lost instance lands in — so a run that simply ran out of time reads as a
    # correctness failure. The distinguishing evidence is this line in the log and nothing
    # in the JSON, which is why it is a grep and why the follow-up is to record the timeout
    # in the results document so the checker can tell the two apart itself.
    if "convergence timed out" in log:
        found.append(
            "**The coordinator's convergence wait expired.** Instances still running at the "
            "deadline are counted as `lostInstances`, so a non-zero lost count here may be "
            "the runner being too slow rather than an instance being lost. Re-read the "
            "verdict with that in mind, and raise `--converge-timeout` before treating it "
            "as a defect.")

    for arm in (document or {}).get("arms", []):
        codes = arm.get("killExitCodes", {})
        other = {code: count for code, count in codes.items() if code != "137"}

        if other and not codes.get("137"):
            found.append(
                f"**No worker in the `{arm['killPosition']}` arm exited 137.** Exit codes "
                f"were {other}. Whatever ended those processes, `SIGKILL` did not, and the "
                f"kill is the whole experiment.")
        elif other and not green:
            # Only when something is already wrong. A handful of workers exiting 0 is normal
            # — they ran out of instances to claim before their kill was due, and the
            # recorded run in docs/benchmarks/QR2-chaos.md has two and three of them — so
            # saying it on a green night would be noise in the one place that must not have
            # any. It is worth saying beside a failure, where the question is what was
            # different about this run.
            found.append(
                f"In the `{arm['killPosition']}` arm {sum(other.values())} worker(s) ended "
                f"with exit code(s) {sorted(other)} rather than 137. A few of those are "
                f"normal — a worker that ran out of instances to claim before its kill was "
                f"due — but a lot of them means the arm killed less than it reports.")

    # The checker's own reasons, lifted out of the middle of its report so they are the
    # first thing in the issue rather than the twentieth line of it.
    reasons = [
        line.strip().lstrip("- ")
        for line in verdict_text.splitlines()
        if line.strip().lstrip("- ").startswith(("FAIL:", "INCONCLUSIVE:"))
    ]

    # De-duplicated in order: the checker prints its reasons per arm and the text does not
    # name the arm, so two identical lines read as a rendering bug rather than as two arms
    # agreeing. Which arms they came from is in the verdict block directly below.
    for reason in reasons:
        if reason not in found:
            found.append(reason)

    return found


def render(state: dict, document: dict | None, verdict_text: str,
           diagnosed: list[str], run_url: str | None) -> str:
    lines = [
        f"## QR2 chaos — {state['verdict']}",
        "",
        state["headline"],
        "",
    ]

    if run_url:
        lines += [f"Run: {run_url}", ""]

    if document:
        p = document["parameters"]
        lines += [
            f"`{p['flows']}` flows per arm x `{p['steps']}` non-idempotent steps, kill at "
            f"step `{p['killStep']}` every `{p['killEvery']}` arrivals, "
            f"`{p['workers']}` worker process(es) x `{p['concurrency']}`, "
            f"`{p['recoveryNodes']}` recovery node(s), lease TTL `{p['leaseTtlSeconds']}`s. "
            f"Wall clock `{document.get('elapsedSeconds', '?')}`s.",
            "",
        ]

    if diagnosed:
        lines += ["### Start here", ""]
        lines += [f"- {line}" for line in diagnosed]
        lines += [""]

    lines += ["### The verdict, in full", "", "```", verdict_text.strip(), "```", ""]

    command = repro(document)

    if command:
        lines += ["### Reproducing this exact run", "", "```bash", command, "```", ""]

    lines += [
        "### What this verdict does and does not cover",
        "",
        "- **Gated:** zero duplicate effects against the journal's guarantee, zero effects "
        "applied by two live workers, zero lost instances, zero orphan effects, zero "
        "instances taken over by more than one recovery node. A duplicate "
        "inside [ADR-0006](docs/adr/ADR-0006-journal-and-leases.md)'s documented window is "
        "counted and reported and is **not** a failure — it is the design, and a rig that "
        "failed on it would be reporting a decision as a bug.",
        "- **Reported, not gated:** the resume p99, against QR2's 45 s. Three runs of this "
        "one rig disagree on it by a factor of two with every correctness row still zero "
        "([QR2-chaos.md §4.4](docs/benchmarks/QR2-chaos.md#44-the-resume-p99-which-is-measured-and-not-gated)) "
        "because it is ~30 s of lease TTL plus however long a backlog takes to drain. "
        "**Nothing in this job fails on it**, and a p99 over 45 s here is not a defect and "
        "must not be filed as one.",
        "",
        "The full results document is attached to the run as the `chaos-qr2` artifact.",
    ]

    return "\n".join(lines)


def annotate(state: dict, diagnosed: list[str]) -> None:
    if not state["annotation"]:
        return

    # The checker's own reason first, not whichever diagnostic happens to sort first: the
    # annotation is one line on the run page and it has to say what the verdict was about.
    # The rest of the context is in the step summary and the issue, which is where somebody
    # who has decided to look reads it.
    reasons = [line for line in diagnosed
               if line.startswith(("FAIL:", "INCONCLUSIVE:"))]

    first = (reasons or diagnosed or [state["headline"]])[0]
    first = first.replace("**", "").replace("\n", " ")

    print(f"::{state['annotation']} title=QR2 {state['verdict']}::{first}")


def gh(arguments: list[str], notify: bool) -> str:
    """Run a `gh` command, or print it when this is a dry run."""
    printable = "gh " + " ".join(
        argument if " " not in argument else f'"{argument}"' for argument in arguments)

    if not notify:
        print(f"    would run: {printable[:400]}")
        return ""

    completed = subprocess.run(["gh", *arguments], capture_output=True, text=True, check=False)

    if completed.returncode != 0:
        print(f"::warning::`{printable[:200]}` failed: {completed.stderr.strip()[:400]}")
        return ""

    return completed.stdout


def open_issues(notify: bool) -> list[dict]:
    if not notify:
        return []

    output = gh(["issue", "list", "--state", "open", "--label", LABEL,
                 "--limit", "50", "--json", "number,title"], notify)

    try:
        return json.loads(output) if output.strip() else []
    except json.JSONDecodeError:
        return []


def file_it(state: dict, body: str, run_url: str | None, notify: bool, owner: str) -> None:
    """Open, update or close the tracking issue — the part that reaches a person."""
    existing = open_issues(notify)

    if state["verdict"] == "PASS":
        # Close whatever is open, whichever of the non-PASS states opened it. An issue that
        # outlives the condition it describes is the next thing nobody reads.
        titles = [candidate["title"] for candidate in STATES.values() if "title" in candidate]
        titles.append(UNEXPECTED["title"])

        for issue in existing:
            if issue["title"] in titles:
                gh(["issue", "close", str(issue["number"]), "--comment",
                    f"QR2 is green again.{f' {run_url}' if run_url else ''}"], notify)

        if not existing:
            print("    nothing open to close.")

        return

    match = next((issue for issue in existing if issue["title"] == state["title"]), None)

    if match:
        # Comment rather than open a second one: one issue per red night is a tracker
        # nobody reads, which is the failure this whole mechanism is about.
        gh(["issue", "comment", str(match["number"]), "--body", body], notify)
        print(f"    updated issue #{match['number']}.")
        return

    gh(["label", "create", LABEL, "--color", "B60205",
        "--description", "The nightly QR2 chaos run is not green", "--force"], notify)

    gh(["issue", "create", "--title", state["title"], "--label", LABEL, "--body", body], notify)

    # Separately, and tolerantly: an assignee that cannot be assigned must not stop the
    # issue existing. The assignment is what turns a tracker entry into a notification, so
    # it is worth attempting and not worth failing over.
    if owner:
        latest = open_issues(notify)
        match = next((issue for issue in latest if issue["title"] == state["title"]), None)

        if match:
            gh(["issue", "edit", str(match["number"]), "--add-assignee", owner], notify)
        elif notify:
            print("::warning::The issue was created but could not be found again to assign, "
                  "so nobody was notified by it.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--code", type=int, required=True,
                        help="the exit code check-chaos-qr2.py returned")
    parser.add_argument("--results", help="the chaos run's JSON")
    parser.add_argument("--verdict", help="the checker's rendered output")
    parser.add_argument("--log", help="the whole run's log, for diagnostics")
    parser.add_argument("--run-url", help="the CI run this verdict came from")
    parser.add_argument("--owner", default=os.environ.get("GITHUB_REPOSITORY_OWNER", ""),
                        help="who the issue is assigned to")
    parser.add_argument("--notify", action="store_true",
                        help="actually file the issue; without it, print what would be filed")

    args = parser.parse_args()

    document = load(args.results)
    verdict_text = read(args.verdict) or f"(the checker produced no output; exit {args.code})"

    # An unhandled exception leaves Python with exit code 1, which is also FAIL. A rig that
    # died mid-run and left half a JSON document behind would therefore be reported as a
    # duplicate effect, and the issue would name a correctness defect that nobody observed.
    # `gate-self-test` in performance.yml makes the same distinction for the benchmark gate,
    # in its own words: the gate must reject a bad run "by reporting it rather than by
    # crashing". A traceback is the checker breaking, not judging.
    crashed = "Traceback (most recent call last)" in verdict_text

    state = UNEXPECTED if crashed else STATES.get(args.code, UNEXPECTED)
    diagnosed = diagnostics(document, verdict_text, read(args.log), green=args.code == 0)

    if crashed:
        diagnosed.insert(0, (
            "**`check-chaos-qr2.py` raised rather than returned a verdict.** It did not "
            "judge this run and this is not a FAIL, whatever the exit code says. The usual "
            "cause is a results document the rig did not finish writing, so start with the "
            "run's log and with whether the chaos coordinator reached the end."))
    elif args.code not in STATES:
        diagnosed.insert(0, (
            f"**`check-chaos-qr2.py` exited {args.code}**, which is not one of its three "
            f"verdicts. The checker did not judge this run; it broke. Nothing about QR2 was "
            f"established either way."))

    body = render(state, document, verdict_text, diagnosed, args.run_url)

    summary = os.environ.get("GITHUB_STEP_SUMMARY")

    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(body + "\n")
    else:
        print(body)
        print()

    annotate(state, diagnosed)

    if args.notify and not shutil.which("gh"):
        print("::warning::--notify was asked for and `gh` is not on PATH, so no issue was "
              "filed. The verdict below is the only record of this run.")
        args.notify = False

    print(f"    verdict {state['verdict']} (exit {args.code}); "
          f"{'filing' if args.notify else 'dry run —'} the tracking issue:")

    file_it(state, body, args.run_url, args.notify, args.owner)

    return state["job"]


if __name__ == "__main__":
    sys.exit(main())

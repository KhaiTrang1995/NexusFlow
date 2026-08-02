#!/usr/bin/env python3
"""Turn a red CI run on a shared branch into something that reaches a person.

WHY THIS EXISTS
---------------
`CHECKLIST.md` blocker **B-4** and the unnumbered package above `WP-31` in `PLAN.md`
both describe the same defect, found twice, about the *Benchmark budgets* job: it was
"blocking, and blocking nothing: no branch protection, no notification". Sixty-odd
pushes merged over it while a benchmark walked 40 B -> 56 B underneath, because the
only thing a red run produced was a red icon on a page nobody had open.

On 2026-08-01 the same shape reached the *build and test* job, which is not a nightly
measurement job but the one that compiles the product and runs the suite. Five of six
jobs in `ci.yml` were red on `dev` for days. Nothing had a consequence:

  * `dev` is not a protected branch, so a red check blocks no merge and no push;
  * no workflow in `.github/workflows/` triggers on a failed run — there is no
    `on: workflow_run`, and no `if: failure()` step that notifies anybody;
  * a local `dotnet test` passes, so the people most likely to notice were the least
    likely to be shown.

`chaos.yml` already solved this for the nightly QR2 run, and its comment on the
`issues: write` permission names B-4 by number: "Without this the job can only be a
red icon, which is the thing B-4 is about; with it, a non-PASS reaches a named
person's notifications." This script is that same mechanism for `ci.yml`, and is
deliberately written to behave the same way so there is one idea here and not two.

WHAT IT DOES
------------
One issue, not one per run. On a failure it opens an issue titled after the branch,
labels it, and assigns the repository owner — the assignment is the part that becomes
a notification. While the branch stays red it comments on that same issue instead of
opening another, because a tracker with forty entries for one outage is the next thing
nobody reads. On the first green run it closes the issue, because an issue that
outlives the condition it describes trains people to ignore the label.

WHAT IT DOES NOT DO
-------------------
It does not decide whether the run failed; it is told. It does not fail the run — the
jobs that broke already do that, and a notifier that also goes red would just be a
second red icon. Its own exit code answers a different question: whether the report was
filed. That way "CI is red" and "nobody was told CI is red" are distinguishable, which
is the whole distinction B-4 turns on.

Nothing is written without `--notify`; without it every `gh` call is printed instead,
which is what makes this testable outside CI.
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys

LABEL = "ci-red"


def gh(arguments: list[str], notify: bool) -> str:
    """Run a `gh` command, or print it when this is a dry run."""
    printable = "gh " + " ".join(
        argument if " " not in argument else f'"{argument}"' for argument in arguments)

    if not notify:
        print(f"    would run: {printable[:400]}")
        return ""

    completed = subprocess.run(["gh", *arguments], capture_output=True, text=True, check=False)

    if completed.returncode != 0:
        # A warning, not a failure. Being unable to file the report is worth surfacing and
        # is not worth turning into a second, more confusing failure on top of the real one.
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


def failed_jobs(needs: dict) -> list[str]:
    """The jobs that did not succeed, by name, in the order the workflow declares them."""
    return [name for name, job in needs.items()
            if isinstance(job, dict) and job.get("result") not in ("success", "skipped")]


def body(failed: list[str], branch: str, commit: str, run_url: str) -> str:
    """The issue text: what broke, on what, and where to look."""
    lines = [
        f"`{branch}` is red.",
        "",
        f"* **Commit** `{commit}`",
        f"* **Run** {run_url}",
        "",
        f"**{len(failed)} job(s) did not pass:**",
        "",
    ]

    lines.extend(f"* `{name}`" for name in failed)
    lines.extend([
        "",
        "---",
        "",
        "This issue is opened by `scripts/report-red-run.py` and closes itself on the "
        "next green run of the same branch. While the branch stays red it is commented "
        "on rather than duplicated.",
        "",
        "It exists because a red run on this repository has historically had no other "
        "consequence: `dev` is not protected, so nothing is blocked, and until this "
        "existed nothing was notified. See `CHECKLIST.md` **B-4**.",
    ])

    return "\n".join(lines)


def file_it(failed: list[str], text: str, branch: str, run_url: str,
            owner: str, notify: bool) -> None:
    """Open, update or close the tracking issue — the part that reaches a person."""
    title = f"CI is red on `{branch}`"
    existing = open_issues(notify)
    match = next((issue for issue in existing if issue["title"] == title), None)

    if not failed:
        if match:
            gh(["issue", "close", str(match["number"]), "--comment",
                f"`{branch}` is green again. {run_url}"], notify)
            print(f"    closed issue #{match['number']}.")
        else:
            print("    green, and nothing open to close.")

        return

    if match:
        gh(["issue", "comment", str(match["number"]), "--body", text], notify)
        print(f"    updated issue #{match['number']}.")
        return

    gh(["label", "create", LABEL, "--color", "B60205",
        "--description", "A shared branch is failing CI", "--force"], notify)

    gh(["issue", "create", "--title", title, "--label", LABEL, "--body", text], notify)
    print("    opened an issue.")

    # Separately, and tolerantly: an assignee that cannot be assigned must not stop the
    # issue existing. The assignment is what turns a tracker entry into a notification, so
    # it is worth attempting and not worth failing over.
    if owner:
        latest = open_issues(notify)
        match = next((issue for issue in latest if issue["title"] == title), None)

        if match:
            gh(["issue", "edit", str(match["number"]), "--add-assignee", owner], notify)
        elif notify:
            print("::warning::The issue was created but could not be found again to assign, "
                  "so nobody was notified by it.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--branch", required=True, help="The branch the run was for.")
    parser.add_argument("--commit", default="", help="The commit the run was for.")
    parser.add_argument("--run-url", default="", help="Link back to the run.")
    parser.add_argument("--owner", default="", help="Who to assign, so it is a notification.")
    parser.add_argument("--notify", action="store_true",
                        help="Actually file it. Without this every gh call is printed.")
    arguments = parser.parse_args()

    # Read from the environment rather than a command-line value interpolated by the
    # workflow. `${{ toJSON(needs) }}` spliced into a shell line is a script-injection
    # shape whatever it happens to hold today; chaos.yml's "Settle the run's parameters"
    # step makes the same argument about its dispatch inputs.
    raw = os.environ.get("FLOWX_NEEDS", "").strip()

    if not raw:
        print("::error::FLOWX_NEEDS is empty, so this script cannot tell what failed.")
        return 1

    try:
        needs = json.loads(raw)
    except json.JSONDecodeError as error:
        print(f"::error::FLOWX_NEEDS is not JSON: {error}")
        return 1

    failed = failed_jobs(needs)

    if failed:
        print(f"{len(failed)} job(s) did not pass: {', '.join(failed)}")
    else:
        print("Every job passed.")

    file_it(
        failed,
        body(failed, arguments.branch, arguments.commit, arguments.run_url),
        arguments.branch,
        arguments.run_url,
        arguments.owner,
        arguments.notify)

    # Zero on a filed report, red or green. This job answers "was anybody told?", and the
    # jobs that actually broke are what make the run red.
    return 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Price one run of FlowPlanGenerator, in bytes, against a synthetic project of N flows.

    ./scripts/measure-generator-cost.py                          # 25 and 50 flows
    ./scripts/measure-generator-cost.py --sizes 25,50,100
    ./scripts/measure-generator-cost.py --json /tmp/cost.json    # keep the samples
    ./scripts/measure-generator-cost.py --compiler some/other/FlowX.Compiler.dll

Exit codes:  0 measured   2 too noisy to report   3 the measurement itself broke.
(No 1: this script does not render a verdict. scripts/check-generator-cost.py does.)


What this is for
----------------

docs/benchmarks/B12-scale.md §5.2 records a commit that made the generator 4.9x more
expensive and merged unnoticed, along with three more working packages, because the only
gate on generator cost was an *absolute* one against a budget the project was already
failing. A gate that is already red says nothing when a change makes things worse.

This is the missing *relative* measurement: a number small enough to take on every pull
request, stable enough to compare against a committed figure, and about the generator
rather than about the machine.


Why it is not scripts/measure-scale-overhead.sh with fewer rounds
-----------------------------------------------------------------

That harness measures the right thing — end-to-end build overhead in wall clock, which is
what the +8 % budget is phrased against — and it cannot be made cheap or quiet enough for
a per-commit gate. Its own A/A control puts its noise floor at 3.9 % to 8.7 % on a quiet
container, a full run costs half an hour, and B12-scale.md §5.1 contains a table where two
*identical* runs differ by more than the change being measured. Tightening a threshold
towards that floor on a hosted runner produces a gate that fails for the weather; loosening
it past that floor produces a gate that would not have caught a 4.9x regression until it
was a 10x one.

So this measures a different quantity: **how many bytes the generator allocates**, running
in-process against the same synthetic project. Every SemanticModel query the generator
issues binds something, and binding allocates, so the count tracks the work rather than the
wait. It is reproducible to about one part in a thousand — see
docs/benchmarks/generator-cost-gate.md for the A/A distribution behind that claim — which
is what makes a tight relative threshold defensible.

This is the same split scripts/check-benchmark-budgets.py already argues for and applies to
run time: allocation counts are exact and gateable on shared hardware, timings are not.
Elapsed milliseconds are measured and reported here too, and they are advisory for exactly
that reason.

**It does not replace the absolute criterion, and nothing here should be read as
progress against it.** P1 still has to build a 200-flow solution within +8 %, that is
still measured by measure-scale-overhead.sh in wall clock, and it is still failing.
check-generator-cost.py reprints where the absolute criterion stands on every run so that
a green relative gate cannot be mistaken for a passing budget.
"""

from __future__ import annotations

import argparse
import json
import os
import pathlib
import shutil
import statistics
import subprocess
import sys
import tempfile

EXIT_OK = 0
EXIT_INCONCLUSIVE = 2
EXIT_BROKEN = 3

REPO = pathlib.Path(__file__).resolve().parent.parent

PROBE = REPO / "scripts" / "generator-cost-probe" / "GeneratorCostProbe.csproj"
PROBE_DLL = REPO / "scripts" / "generator-cost-probe" / "bin" / "Release" / "net10.0" / "GeneratorCostProbe.dll"
GENERATE = REPO / "scripts" / "generate-scale-project.py"

DEFAULT_COMPILER = REPO / "src" / "FlowX.Compiler" / "bin" / "Release" / "netstandard2.0" / "FlowX.Compiler.dll"
DEFAULT_REFS = REPO / "src" / "FlowX.Runtime" / "bin" / "Release" / "net10.0"


def die(message: str) -> None:
    print(f"::error::{message}", file=sys.stderr)
    sys.exit(EXIT_BROKEN)


def run(command: list[str], what: str) -> None:
    result = subprocess.run(command, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        print(result.stdout, file=sys.stderr)
        print(result.stderr, file=sys.stderr)
        die(f"{what} failed.")


def build(args: argparse.Namespace) -> None:
    """Build the three things the probe needs, unless the caller says they are ready."""
    if args.no_build:
        return

    env = dict(os.environ, DOTNET_NOLOGO="1", DOTNET_CLI_TELEMETRY_OPTOUT="1")

    for project, what in (
        (REPO / "src" / "FlowX.Compiler" / "FlowX.Compiler.csproj", "Building FlowX.Compiler"),
        # FlowX.Runtime pulls FlowX.Abstractions and FlowX.Core with it, and its output
        # directory is where the probe finds all three as metadata references.
        (REPO / "src" / "FlowX.Runtime" / "FlowX.Runtime.csproj", "Building FlowX.Runtime"),
        (PROBE, "Building the probe"),
    ):
        result = subprocess.run(
            ["dotnet", "build", str(project), "-c", "Release", "--nologo", "-v", "q"],
            capture_output=True, text=True, check=False, env=env,
        )
        if result.returncode != 0:
            print(result.stdout, file=sys.stderr)
            die(f"{what} failed.")


def measure(size: int, work: pathlib.Path, args: argparse.Namespace) -> dict:
    """Generate the subject at `size` flows and run the probe over it."""
    project = work / f"scale-{size}"

    run([sys.executable, str(GENERATE), "--flows", str(size), "--out", str(project), "--repo", str(REPO)],
        f"Generating a {size}-flow synthetic project")

    out = work / f"probe-{size}.json"

    result = subprocess.run(
        ["dotnet", str(PROBE_DLL),
         "--sources", str(project),
         "--refs", str(args.refs),
         "--compiler", str(args.compiler),
         "--runs", str(args.runs),
         "--warmup", str(args.warmup),
         "--json", str(out)],
        capture_output=True, text=True, check=False,
        env=dict(os.environ, DOTNET_NOLOGO="1", DOTNET_CLI_TELEMETRY_OPTOUT="1"),
    )

    if result.returncode != 0:
        print(result.stdout, file=sys.stderr)
        print(result.stderr, file=sys.stderr)
        die(f"The probe failed at {size} flows.")

    report = json.loads(out.read_text(encoding="utf-8"))

    # One plan per flow, plus the manifest. This is measure-scale-overhead.sh's check,
    # for its reason: a flow that silently failed to analyse would make the generator do
    # less work and report a cost that looks like an improvement. The failure mode that
    # produces a number which looks fine and means nothing is the one worth an assertion.
    expected = size + 1
    if report["subject"]["generated_trees"] != expected:
        die(f"Expected {expected} generated trees at {size} flows, got "
            f"{report['subject']['generated_trees']}. Some flows did not analyse.")

    # THE UNIT THE CATALOGUE COST ACTUALLY SCALES WITH. Bytes per flow is what this harness
    # has always reported, and it is the right unit for the plan emitter — one plan per flow.
    # The error catalogue is derived per CAPABILITY, and the synthetic project declares
    # several per flow, so a per-flow figure mixes two rates and hides which one moved. Both
    # are reported; neither is derived from the other.
    capabilities = sum(
        source.read_text(encoding="utf-8").count("[Capability(")
        for source in project.rglob("*.cs"))

    if capabilities == 0:
        die(f"No [Capability(...)] was found in the {size}-flow subject, so a per-capability "
            "figure would be a division by nothing. The generator script's layout changed.")

    allocated = [sample["allocated_bytes"] for sample in report["samples"]]
    elapsed = [sample["elapsed_ms"] for sample in report["samples"]]

    median = statistics.median(allocated)

    return {
        "flows": size,
        "allocated_bytes": int(median),
        "allocated_bytes_per_flow": round(median / size, 1),
        "capabilities": capabilities,
        "allocated_bytes_per_capability": round(median / capabilities, 1),
        "spread_percent": round(100 * (max(allocated) - min(allocated)) / median, 4),
        "elapsed_ms": round(statistics.median(elapsed), 2),
        "elapsed_spread_percent": round(
            100 * (max(elapsed) - min(elapsed)) / statistics.median(elapsed), 2),
        "generated_trees": report["subject"]["generated_trees"],
        "generated_chars": report["subject"]["generated_chars"],
        "diagnostics": report["subject"]["diagnostics"],
        "sources_sha256": report["subject"]["sources_sha256"],
        "samples": report["samples"],
        "environment": report["environment"],
    }


def load_average() -> float:
    try:
        return os.getloadavg()[0]
    except OSError:
        return float("nan")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--sizes", default="25,50",
                        help="Comma-separated flow counts. Default 25,50 — two sizes so the "
                             "per-flow cost can be taken as a marginal and the fixed term "
                             "does not dilute a regression.")
    parser.add_argument("--runs", type=int, default=5, help="Measured runs per size (default 5).")
    parser.add_argument("--warmup", type=int, default=2,
                        help="Discarded runs per size (default 2). The first run in a "
                             "process measures the JIT.")
    parser.add_argument("--compiler", default=str(DEFAULT_COMPILER),
                        help="The FlowX.Compiler.dll to measure. Point this at another "
                             "tree's build to compare two commits.")
    parser.add_argument("--refs", default=str(DEFAULT_REFS),
                        help="Directory holding FlowX.Abstractions/Core/Runtime.dll.")
    parser.add_argument("--json", help="Write the samples here.")
    parser.add_argument("--max-spread", type=float, default=2.0,
                        help="Refuse to report if a size's allocation spread exceeds this "
                             "percentage of its median (default 2). Measured spread on a "
                             "quiet container is under 0.2 %%.")
    parser.add_argument("--no-build", action="store_true",
                        help="Assume FlowX.Compiler, FlowX.Runtime and the probe are built.")
    args = parser.parse_args()

    try:
        sizes = [int(value) for value in args.sizes.replace(" ", ",").split(",") if value]
    except ValueError:
        die(f"Flow counts must be whole numbers, got '{args.sizes}'.")

    if not sizes:
        die("No sizes given.")

    if shutil.which("dotnet") is None:
        die("dotnet is not on PATH. Nothing to measure.")

    compiler = pathlib.Path(args.compiler)
    build(args)

    if not compiler.exists():
        die(f"No compiler assembly at {compiler}.")

    print("==> Machine")
    print(f"    {os.cpu_count()} logical cores, load average now {load_average():.2f}")
    print(f"==> Measuring {compiler}")
    print()

    work = pathlib.Path(tempfile.mkdtemp(prefix="flowx-generator-cost-"))
    load_before = load_average()

    try:
        sizes_report = [measure(size, work, args) for size in sorted(sizes)]
    finally:
        shutil.rmtree(work, ignore_errors=True)

    document = {
        "meta": {
            "runs": args.runs,
            "warmup": args.warmup,
            "compiler": str(compiler),
            "nproc": os.cpu_count(),
            "load1_before": round(load_before, 2),
            "load1_after": round(load_average(), 2),
            "environment": sizes_report[0]["environment"],
        },
        "sizes": sizes_report,
    }

    # The marginal cost strips the fixed term the way B12-scale.md §5.2's bisect probe
    # does. On the recorded runs the generator's allocation is essentially proportional to
    # flow count with no fixed part, so the marginal and the per-flow total agree closely
    # — and when they stop agreeing, that disagreement is itself worth seeing.
    if len(sizes_report) >= 2:
        low, high = sizes_report[0], sizes_report[-1]
        document["marginal"] = {
            "from_flows": low["flows"],
            "to_flows": high["flows"],
            "allocated_bytes_per_flow": round(
                (high["allocated_bytes"] - low["allocated_bytes"]) / (high["flows"] - low["flows"]), 1),
        }

    print(f"{'flows':>7} {'allocated':>16} {'per flow':>12} {'caps':>6} {'per cap':>10} "
          f"{'spread':>9} {'elapsed':>10} {'emitted':>10}")
    print("-" * 92)
    for entry in sizes_report:
        print(f"{entry['flows']:7d} {entry['allocated_bytes']:16,d} "
              f"{entry['allocated_bytes_per_flow']:12,.0f} {entry['capabilities']:6d} "
              f"{entry['allocated_bytes_per_capability']:10,.0f} "
              f"{entry['spread_percent']:8.3f}% {entry['elapsed_ms']:9.0f}ms "
              f"{entry['generated_chars']:10,d}")

    if "marginal" in document:
        print()
        print(f"    marginal {document['marginal']['from_flows']} -> "
              f"{document['marginal']['to_flows']} flows: "
              f"{document['marginal']['allocated_bytes_per_flow']:,.0f} bytes per flow")

    print()
    print("    allocated bytes are the gated metric; elapsed ms is advisory and is here "
          "only so a\n    reader can see whether the two agree in direction.")

    if args.json:
        pathlib.Path(args.json).write_text(json.dumps(document, indent=1), encoding="utf-8")
        print(f"\n==> Samples written to {args.json}")

    # The refusal to report, in the same shape measure-scale-overhead.sh already has. It
    # is very hard to trip — the spread this measures is a hundredth of the limit — and
    # that is the point: if it fires, something about the run is wrong in a way that a
    # number would hide rather than a machine that is merely busy.
    noisy = [entry for entry in sizes_report if entry["spread_percent"] > args.max_spread]

    if noisy:
        print()
        for entry in noisy:
            print(f"::warning::INCONCLUSIVE at {entry['flows']} flows: allocation spread "
                  f"{entry['spread_percent']:.3f}% exceeds the {args.max_spread}% limit.")
        return EXIT_INCONCLUSIVE

    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Price the generator against synthetic projects of different *shape*, not different size.

    ./scripts/measure-catalogue-shape.py                       # the default grid
    ./scripts/measure-catalogue-shape.py --flows 25 --json /tmp/shape.json
    ./scripts/measure-catalogue-shape.py --body-lines 0,50 --pools 0,5

Exit codes:  0 measured   3 the measurement itself broke.
(No verdict. This script reports; it gates nothing, and it must not be wired to a gate.)


What this is for
----------------

docs/adr/ADR-0014 §6 records a specific gap:

    "Real projects pay more" is an extrapolation, not a measurement, and it has an
    unmeasured counterweight. Real capability bodies are larger, which costs more per
    capability. But the synthetic project has 262 capability types across 50 flows (5.2
    per flow) and real projects *reuse* capabilities across flows ... The two effects
    point in opposite directions and neither has been measured.

This measures both, one at a time, on the same instrument the cost gate uses:

  * ``--body-lines`` pads every capability body with ordinary statements. The number of
    Error-typed expressions per capability does not change, so a moved number is
    attributable to body size and to nothing else. A real body would grow its failure
    paths too, so this is the *lower* bound on the effect.

  * ``--pools`` shares capability types across flows. Pool 0 is the default project, where
    every flow owns its capabilities; pool 5 at 50 flows means ten flows per capability
    set. Flow count is held constant throughout, so the flow-analysis half of the
    generator's work is held constant with it.

Neither knob can answer "what does a real project cost", because a real project differs in
both dimensions at once and in others besides. What the grid can answer is which direction
each effect pushes and by how much per unit, which is what ADR-0014 says nobody knows.


Why bytes
---------

The same argument scripts/measure-generator-cost.py makes at length: allocation counts are
reproducible to about one part in a thousand on a shared machine and wall clock is not.
Elapsed milliseconds are reported and are advisory.

**This script does not touch the gate's baseline and cannot.** Its subjects hash
differently from the recorded one by construction, which is why it drives the probe
directly rather than going through check-generator-cost.py.
"""

from __future__ import annotations

import argparse
import json
import os
import pathlib
import re
import shutil
import statistics
import subprocess
import sys
import tempfile

EXIT_OK = 0
EXIT_BROKEN = 3

REPO = pathlib.Path(__file__).resolve().parent.parent

PROBE = REPO / "scripts" / "generator-cost-probe" / "GeneratorCostProbe.csproj"
PROBE_DLL = REPO / "scripts" / "generator-cost-probe" / "bin" / "Release" / "net10.0" / "GeneratorCostProbe.dll"
GENERATE = REPO / "scripts" / "generate-scale-project.py"

DEFAULT_COMPILER = REPO / "src" / "FlowX.Compiler" / "bin" / "Release" / "netstandard2.0" / "FlowX.Compiler.dll"
DEFAULT_REFS = REPO / "src" / "FlowX.Runtime" / "bin" / "Release" / "net10.0"

TYPES = re.compile(r"(\d+) capability types")


def die(message: str) -> None:
    print(f"::error::{message}", file=sys.stderr)
    sys.exit(EXIT_BROKEN)


def build(args: argparse.Namespace) -> None:
    """Build what the probe needs, unless the caller says it is ready."""
    if args.no_build:
        return

    env = dict(os.environ, DOTNET_NOLOGO="1", DOTNET_CLI_TELEMETRY_OPTOUT="1")

    for project, what in (
        (REPO / "src" / "FlowX.Compiler" / "FlowX.Compiler.csproj", "Building FlowX.Compiler"),
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


def measure(flows: int, body_lines: int, pool: int, work: pathlib.Path, args: argparse.Namespace) -> dict:
    """Generate one shape and price it."""
    project = work / f"shape-{flows}-{body_lines}-{pool}"

    generated = subprocess.run(
        [sys.executable, str(GENERATE),
         "--flows", str(flows),
         "--out", str(project),
         "--repo", str(REPO),
         "--capability-body-lines", str(body_lines),
         "--capability-pool", str(pool)],
        capture_output=True, text=True, check=False,
    )

    if generated.returncode != 0:
        print(generated.stdout, file=sys.stderr)
        print(generated.stderr, file=sys.stderr)
        die(f"Generating flows={flows} body={body_lines} pool={pool} failed.")

    found = TYPES.search(generated.stdout)

    if found is None:
        die(f"The generator did not report a capability-type count: {generated.stdout!r}")

    capability_types = int(found.group(1))

    out = work / f"probe-{flows}-{body_lines}-{pool}.json"

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
        die(f"The probe failed at flows={flows} body={body_lines} pool={pool}.")

    report = json.loads(out.read_text(encoding="utf-8"))

    # One plan per flow plus the manifest, exactly as measure-generator-cost.py checks. A
    # flow that silently failed to analyse would make the generator do less work and report
    # a cost that looks like an improvement — the one failure mode worth an assertion.
    expected = flows + 1

    if report["subject"]["generated_trees"] != expected:
        die(f"Expected {expected} generated trees at flows={flows} body={body_lines} "
            f"pool={pool}, got {report['subject']['generated_trees']}.")

    allocated = [sample["allocated_bytes"] for sample in report["samples"]]
    elapsed = [sample["elapsed_ms"] for sample in report["samples"]]
    median = statistics.median(allocated)

    return {
        "flows": flows,
        "body_lines": body_lines,
        "pool": pool,
        "capability_types": capability_types,
        "types_per_flow": round(capability_types / flows, 2),
        "allocated_bytes": int(median),
        "bytes_per_flow": int(median / flows),
        "bytes_per_capability_type": int(median / capability_types),
        "spread_percent": round(100 * (max(allocated) - min(allocated)) / median, 4),
        "elapsed_ms": round(statistics.median(elapsed), 2),
        "generated_chars": report["subject"]["generated_chars"],
        # FLOWX1024, once per .Emit step, saying the outbox does not exist yet. Reported
        # rather than asserted on: the count tracks how many flows carry an emit, which the
        # pool sweep deliberately changes.
        "diagnostics": report["subject"]["diagnostics"],
        "sources_sha256": report["subject"]["sources_sha256"],
    }


def table(rows: list[dict]) -> None:
    print()
    print(f"{'flows':>6} {'body':>5} {'pool':>5} {'cap types':>10} {'/flow':>6} "
          f"{'allocated B':>14} {'B/flow':>10} {'B/cap type':>11} {'spread%':>8} {'ms':>8}")

    for row in rows:
        print(f"{row['flows']:>6} {row['body_lines']:>5} {row['pool'] or '-':>5} "
              f"{row['capability_types']:>10} {row['types_per_flow']:>6} "
              f"{row['allocated_bytes']:>14,} {row['bytes_per_flow']:>10,} "
              f"{row['bytes_per_capability_type']:>11,} {row['spread_percent']:>8} "
              f"{row['elapsed_ms']:>8}")

    print()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--flows", type=int, default=50,
                        help="Flow count, held constant across the grid (default 50, the "
                             "size ADR-0014's instrumented build used).")
    parser.add_argument("--body-lines", default="0,10,25,50",
                        help="Comma-separated padding sizes for the body-size sweep.")
    parser.add_argument("--pools", default="0,25,10,5",
                        help="Comma-separated capability pools for the reuse sweep. 0 means "
                             "one capability set per flow, i.e. the default project.")
    parser.add_argument("--combined", default="10:10,25:10",
                        help="Comma-separated body:pool pairs measured together, so the two "
                             "effects can be weighed against each other rather than only "
                             "against the default. Empty to skip.")
    parser.add_argument("--runs", type=int, default=5, help="Measured runs per point (default 5).")
    parser.add_argument("--warmup", type=int, default=2, help="Discarded runs per point (default 2).")
    parser.add_argument("--compiler", default=str(DEFAULT_COMPILER))
    parser.add_argument("--refs", default=str(DEFAULT_REFS))
    parser.add_argument("--json", help="Write the rows here.")
    parser.add_argument("--no-build", action="store_true",
                        help="Assume FlowX.Compiler, FlowX.Runtime and the probe are built.")
    args = parser.parse_args()

    try:
        body_sweep = [int(v) for v in args.body_lines.replace(" ", ",").split(",") if v]
        pool_sweep = [int(v) for v in args.pools.replace(" ", ",").split(",") if v != ""]
        combined = [
            tuple(int(part) for part in pair.split(":", 1))
            for pair in args.combined.replace(" ", ",").split(",")
            if pair
        ]
    except ValueError:
        die("--body-lines and --pools take whole numbers; --combined takes body:pool pairs.")

    if shutil.which("dotnet") is None:
        die("dotnet is not on PATH. Nothing to measure.")

    build(args)

    if not pathlib.Path(args.compiler).exists():
        die(f"No compiler assembly at {args.compiler}.")

    work = pathlib.Path(tempfile.mkdtemp(prefix="flowx-catalogue-shape-"))

    print("==> Machine")
    try:
        print(f"    {os.cpu_count()} logical cores, load average now {os.getloadavg()[0]:.2f}")
    except OSError:
        print(f"    {os.cpu_count()} logical cores")

    rows: list[dict] = []

    try:
        # The two sweeps share their origin — body 0, pool 0 is the default project — so it
        # is measured once and appears in both. Measuring it twice would invite a reader to
        # treat the difference between the two as signal.
        print("\n==> Body size, capability count held at the default")

        for body in body_sweep:
            rows.append(measure(args.flows, body, 0, work, args))

        print("==> Capability reuse, body size held minimal")

        for pool in pool_sweep:
            if pool == 0:
                continue

            rows.append(measure(args.flows, 0, pool, work, args))

        if combined:
            print("==> Both at once: bigger bodies AND fewer capability types")

            for body, pool in combined:
                rows.append(measure(args.flows, body, pool, work, args))
    finally:
        shutil.rmtree(work, ignore_errors=True)

    table(rows)

    document = {
        "meta": {
            "flows": args.flows,
            "runs": args.runs,
            "warmup": args.warmup,
            "compiler": str(args.compiler),
            "nproc": os.cpu_count(),
        },
        "rows": rows,
    }

    if args.json:
        pathlib.Path(args.json).write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
        print(f"Wrote {args.json}")

    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())

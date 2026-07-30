#!/usr/bin/env bash
#
# P1 scale criterion — build overhead on a synthetic solution of N flows, and the shape
# of the curve that cost traces as N grows.
#
#   ./scripts/measure-scale-overhead.sh                        # 7 rounds, 1/25/50/100/200
#   ./scripts/measure-scale-overhead.sh 12                     # 12 rounds, same sizes
#   ./scripts/measure-scale-overhead.sh 5 50 200               # 5 rounds at two sizes
#   ./scripts/measure-scale-overhead.sh --rounds 15 --sizes 200
#   ./scripts/measure-scale-overhead.sh --no-compiler-server 10  # load-robust CPU timing
#
# Exit codes:  0 PASS   1 FAIL   2 INCONCLUSIVE   3 the measurement itself broke
#
# The comparison is the one scripts/measure-build-overhead.sh makes for the one-flow
# reference sample, at the size the roadmap's P1 exit criterion names:
#
#   with     — the analyzer referenced, the generator running
#   without  — the analyzer dropped, the sources it produced compiled as ordinary files
#
# Both arms reach the SAME final compilation, verified by assembly size before any round
# is timed. The only difference is whether the generator ran.
#
#
# What changed after the first attempt, and why
# ---------------------------------------------
#
# The first run of this measurement reported +23 % against a +8 % budget while the
# within-arm range was 85 % of its own median and the machine's load average moved
# between 2 and 34. That is not a measurement of the generator; it is a measurement of
# whatever else the machine was doing. Four changes, none of which move the number in a
# chosen direction:
#
# **Rounds are sandwiched.** Each size in each round runs three builds — A B A, or B A B
# on alternate rounds — and the paired ratio compares the middle build against the mean
# of the two outside it. Linear drift across the round cancels exactly. Plain alternation
# leaves it in the ratio with a sign set by which arm ran first.
#
# **The sandwich's outside pair is an A/A control.** Two builds of the same arm, the same
# distance apart in time as the real comparison, whose true difference is zero by
# construction. Whatever ratio they produce is this harness's noise floor, measured under
# the same load as the thing being judged. Nothing else here is as useful.
#
# **Every build is timed in CPU as well as wall clock.** Wall clock is what the budget
# means and what a developer waits for, and on a contended machine it is mostly a
# measurement of the other tenants. CPU time — user + sys across the whole build process
# tree — measures work done rather than time waited and barely moves with load.
#
# CPU time is only attributable with the shared compiler server off, because otherwise the
# compile runs inside a VBCSCompiler process that outlives the build — and on a shared
# machine that one server is shared with every other build on the box, so its CPU cannot
# be charged to this build either. Turning the server off costs both arms a cold Roslyn
# start of several seconds, which lands in the denominator and makes the ratio SMALLER
# than a developer would see. So the two configurations decide different things, and the
# analyser enforces it: server on (the default, and B12's configuration) gives the verdict
# on wall clock; server off gives a load-robust per-size cost in milliseconds for the
# growth curve, and a ratio that is only ever a lower bound.
#
# **Sizes are interleaved, not run in blocks.** All the projects are generated and warmed
# up front, then every round visits every size. Blocked sizes make the growth curve a
# measurement of how the machine's load changed between blocks, which is precisely how a
# fit lands on flows^0.99 while its two segments read 0.61 and 1.94.
#
# More than one size is measured because the ratio is the less important half of the
# answer. A generator whose cost is superlinear in flow count fails at 2 000 flows however
# comfortably it passes at 200, and no single size can tell the two apart.
#
# The statistics, the verdict and the refusal to render one live in
# scripts/analyse-scale-samples.py, which reads the JSON this writes. Re-analysing a
# recorded run must not mean re-measuring it.
set -euo pipefail

cd "$(dirname "$0")/.."
REPO="$PWD"

ROUNDS=""
WARMUP=1
SIZES=()
BUDGET=8
MAX_SPREAD=25
JSON_OUT=""
COMPILER_SERVER=1

die() { echo "::error::$*" >&2; exit 3; }

while [ "$#" -gt 0 ]; do
  case "$1" in
    --rounds)          ROUNDS="$2"; shift 2 ;;
    --warmup)          WARMUP="$2"; shift 2 ;;
    --sizes)           IFS=', ' read -r -a SIZES <<<"$2"; shift 2 ;;
    --budget)          BUDGET="$2"; shift 2 ;;
    --max-spread)      MAX_SPREAD="$2"; shift 2 ;;
    --json)            JSON_OUT="$2"; shift 2 ;;
    --no-compiler-server) COMPILER_SERVER=0; shift ;;
    -h|--help)         sed -n '2,20p' "$0" | sed 's/^#\( \|$\)//'; exit 0 ;;
    -*)                die "Unknown option $1" ;;
    *)
      # Positional form, kept because CI and docs/benchmarks/B12-scale.md use it: the
      # first bare number is the round count, the rest are flow counts.
      if [ -z "$ROUNDS" ]; then ROUNDS="$1"; else SIZES+=("$1"); fi
      shift
      ;;
  esac
done

ROUNDS="${ROUNDS:-7}"
[ "${#SIZES[@]}" -gt 0 ] || SIZES=(1 25 50 100 200)

case "$ROUNDS" in ''|*[!0-9]*) die "Round count must be a whole number, got '$ROUNDS'" ;; esac
[ "$ROUNDS" -ge 2 ] || die "At least 2 rounds are needed before any interval can be formed."

export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1

command -v dotnet >/dev/null 2>&1 || die "dotnet is not on PATH. Nothing to measure."
command -v python3 >/dev/null 2>&1 || die "python3 is not on PATH. Nothing to analyse with."

WORK="$(mktemp -d)"
cleanup() { rm -rf "$WORK"; }
trap cleanup EXIT

RAW="$WORK/samples.tsv"
LOADS="$WORK/loads.txt"
: >"$RAW"
: >"$LOADS"

# The default is B12's configuration exactly: whatever `dotnet build` does out of the box,
# including the shared compiler server. --no-compiler-server takes the server away so that
# every process the build starts is a child of this one and its CPU can be measured; the
# price is a cold Roslyn start in both arms, which is why a ratio measured that way is
# only ever a lower bound. -m:1 and nodeReuse:false go with it for the same reason: an
# MSBuild worker that outlives the build takes its CPU with it.
EXTRA_FLAGS=()
if [ "$COMPILER_SERVER" -eq 0 ]; then
  EXTRA_FLAGS=(/p:UseSharedCompilation=false /nodeReuse:false -m:1)
fi

load_now() { cut -d' ' -f1 </proc/loadavg 2>/dev/null || echo 0; }

# One timed build. Echoes "<wall_ms> <cpu_ms>".
#
# `time` on a shell function reports the CPU of every process the function waited for, so
# with the compiler server off the csc child's work is included. With it on, csc outlives
# the build and the CPU figure counts MSBuild alone — which the analyser labels as such
# and excludes, rather than quietly reporting a CPU number that means nothing.
_run_build() {
  if ! dotnet build "$@" >"$WORK/build.log" 2>&1; then
    : >"$WORK/build-failed"
  fi
}

timed_build() {
  local project="$1"; shift
  rm -f "$WORK/build-failed"

  local measured
  measured="$(
    { TIMEFORMAT='%3R %3U %3S'
      time _run_build "$project" -c Release --no-restore --no-dependencies --no-incremental \
        -v q "${EXTRA_FLAGS[@]}" "$@"
    } 2>&1
  )"

  if [ -f "$WORK/build-failed" ]; then
    echo "::error::A timed build failed. The measurement is void." >&2
    cat "$WORK/build.log" >&2
    exit 3
  fi

  awk '{ printf "%d %d\n", ($1 * 1000) + 0.5, (($2 + $3) * 1000) + 0.5 }' <<<"$measured"
}

echo "==> Machine"
echo "    $(nproc) logical cores, load average now $(uptime | sed 's/.*load average: //')"
echo "    .NET SDK $(dotnet --version)"
echo "    compiler server $([ "$COMPILER_SERVER" -eq 1 ] && echo on || echo off)"

# ---------------------------------------------------------------------------------------
# Setup: every size is generated, warmed and verified BEFORE any round runs, so that the
# rounds themselves can interleave the sizes. Doing this per size inside the round loop is
# what turns a growth curve into a record of how the machine's load changed between blocks.
# ---------------------------------------------------------------------------------------

declare -A PROJECT_OF PREGEN_OF

for SIZE in "${SIZES[@]}"; do
  case "$SIZE" in ''|*[!0-9]*) die "Flow count must be a whole number, got '$SIZE'" ;; esac

  PROJECT_DIR="$WORK/scale-$SIZE"
  PROJECT="$PROJECT_DIR/ScaleSynthetic.csproj"
  PREGEN="$WORK/pregen-$SIZE"
  ASSEMBLY="$PROJECT_DIR/bin/Release/net10.0/ScaleSynthetic.dll"

  echo
  echo "==> Preparing $SIZE flows"

  python3 "$REPO/scripts/generate-scale-project.py" --flows "$SIZE" --out "$PROJECT_DIR" --repo "$REPO"

  # Untimed: this restores, builds the FlowX projects the synthetic one references, and
  # produces the generated sources the control arm compiles.
  dotnet build "$PROJECT" -c Release -v q >"$WORK/build.log" 2>&1 || {
    echo "::error::The synthetic project does not build with the generator." >&2
    cat "$WORK/build.log" >&2
    exit 3
  }

  mkdir -p "$PREGEN"
  find "$PROJECT_DIR/obj/generated/FlowX.Compiler" -name '*.g.cs' -exec cp {} "$PREGEN/" \; 2>/dev/null || true
  COUNT=$(find "$PREGEN" -name '*.g.cs' | wc -l)

  # One plan per flow, plus the manifest. A short count means a flow failed to analyse and
  # emitted nothing, so the control arm would compile a smaller program than the treatment
  # arm and part of the measured difference would be missing code. That is the failure
  # mode most likely to produce a number that looks fine and means nothing.
  [ "$COUNT" -eq $(( SIZE + 1 )) ] || die \
    "Expected $(( SIZE + 1 )) generated files at $SIZE flows, captured $COUNT. Some flows did not analyse."

  # Prove the arms really are the same program before spending minutes timing them. A
  # build that merely succeeds is not enough: dropping the pre-generated sources would
  # also succeed, quietly, because nothing in the synthetic project references the plan —
  # and would then time a compilation roughly half the size of its counterpart.
  timed_build "$PROJECT" >/dev/null
  WITH_BYTES=$(stat -c '%s' "$ASSEMBLY")

  timed_build "$PROJECT" /p:FlowXGeneratorDisabled=true "/p:FlowXPreGeneratedDir=$PREGEN" >/dev/null
  WITHOUT_BYTES=$(stat -c '%s' "$ASSEMBLY")

  [ "$WITH_BYTES" -eq "$WITHOUT_BYTES" ] || die \
    "Assembly differs between arms at $SIZE flows: $WITH_BYTES vs $WITHOUT_BYTES bytes. Not the same program."

  echo "    $COUNT generated file(s); both arms produce a $WITH_BYTES byte assembly"

  PROJECT_OF[$SIZE]="$PROJECT"
  PREGEN_OF[$SIZE]="$PREGEN"
done

# ---------------------------------------------------------------------------------------
# Rounds.
# ---------------------------------------------------------------------------------------

build_arm() {
  local size="$1" arm="$2"

  if [ "$arm" = "with" ]; then
    timed_build "${PROJECT_OF[$size]}"
  else
    timed_build "${PROJECT_OF[$size]}" /p:FlowXGeneratorDisabled=true \
      "/p:FlowXPreGeneratedDir=${PREGEN_OF[$size]}"
  fi
}

TOTAL=$(( WARMUP + ROUNDS ))

echo
echo "==> $WARMUP warm-up round(s) then $ROUNDS measured, over ${#SIZES[@]} size(s)"
echo "    3 builds per size per round; $(( TOTAL * ${#SIZES[@]} * 3 )) timed builds in total"
echo

for r in $(seq 1 "$TOTAL"); do
  KEEP=$(( r > WARMUP ? 1 : 0 ))
  INDEX=$(( r - WARMUP ))

  # Sizes are visited in alternating order so that a machine which drifts across a round
  # does not hand the same size the same position in every one of them.
  if [ $(( r % 2 )) -eq 1 ]; then
    ORDER=("${SIZES[@]}")
    # A B A on odd rounds, B A B on even ones, so both arms take the outside slots equally
    # often and neither is systematically favoured by the sandwich.
    PATTERN=(with without with)
  else
    ORDER=()
    for (( i = ${#SIZES[@]} - 1; i >= 0; i-- )); do ORDER+=("${SIZES[$i]}"); done
    PATTERN=(without with without)
  fi

  if [ "$KEEP" -eq 1 ]; then
    printf '    round %2d ' "$INDEX"
  else
    printf '    warm-up  '
  fi

  for SIZE in "${ORDER[@]}"; do
    for slot in 0 1 2; do
      ARM="${PATTERN[$slot]}"
      LOAD="$(load_now)"

      # Command substitution rather than process substitution: a timed build that fails
      # calls exit, and exit inside a process substitution kills only that subshell — the
      # run would carry on recording timings from a build that did not happen.
      TIMING="$(build_arm "$SIZE" "$ARM")"
      read -r WALL CPU <<<"$TIMING"

      if [ "$KEEP" -eq 1 ]; then
        printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
          "$SIZE" "$INDEX" "$slot" "$ARM" "$WALL" "$CPU" "$LOAD" >>"$RAW"
        echo "$LOAD" >>"$LOADS"
      fi
    done

    if [ "$KEEP" -eq 1 ]; then
      printf ' %s:%sms' "$SIZE" "$WALL"
    fi
  done

  printf '   load %s\n' "$(load_now)"
done

# ---------------------------------------------------------------------------------------
# Package the samples and hand them to the analyser.
# ---------------------------------------------------------------------------------------

SAMPLES="${JSON_OUT:-$WORK/samples.json}"

python3 - "$RAW" "$LOADS" "$SAMPLES" "$ROUNDS" "$WARMUP" "$BUDGET" "$COMPILER_SERVER" <<'PY'
import json, subprocess, sys

raw, loads_path, out = sys.argv[1], sys.argv[2], sys.argv[3]
rounds_arg, warmup_arg, budget_arg, server_arg = sys.argv[4:8]

rounds = {}
for line in open(raw):
    if not line.strip():
        continue
    size, index, slot, arm, wall, cpu, load = line.split("\t")
    key = (int(size), int(index))
    rounds.setdefault(key, {})[int(slot)] = {
        "arm": arm,
        "wall_ms": int(wall),
        "cpu_ms": int(cpu),
        "load1": float(load),
    }


def cpu_model():
    try:
        for line in open("/proc/cpuinfo"):
            if line.startswith("model name"):
                return line.split(":", 1)[1].strip()
    except OSError:
        pass
    return "unknown"


def shell(*command):
    try:
        return subprocess.run(command, capture_output=True, text=True).stdout.strip()
    except OSError:
        return ""


document = {
    "meta": {
        "rounds": int(rounds_arg),
        "warmup": int(warmup_arg),
        "sizes": sorted({size for size, _ in rounds}),
        "budget": float(budget_arg),
        "compiler_server": server_arg == "1",
        "nproc": int(shell("nproc") or 0),
        "cpu_model": cpu_model(),
        "dotnet": shell("dotnet", "--version"),
        "load_samples": [float(v) for v in open(loads_path) if v.strip()],
    },
    "rounds": [
        {"size": size, "round": index, "builds": [slots[i] for i in sorted(slots)]}
        for (size, index), slots in sorted(rounds.items())
    ],
}

with open(out, "w", encoding="utf-8") as handle:
    json.dump(document, handle, indent=1)
PY

if [ -n "$JSON_OUT" ]; then
  echo
  echo "==> Raw samples written to $JSON_OUT"
fi

set +e
python3 "$REPO/scripts/analyse-scale-samples.py" "$SAMPLES" \
  --budget "$BUDGET" --max-spread "$MAX_SPREAD"
STATUS=$?
set -e

exit "$STATUS"

#!/usr/bin/env bash
#
# P1 scale criterion — build overhead on a 200-flow synthetic solution.
#
#   ./scripts/measure-scale-overhead.sh             # 5 rounds at 50 and 200 flows
#   ./scripts/measure-scale-overhead.sh 9           # 9 rounds, same sizes
#   ./scripts/measure-scale-overhead.sh 5 25 50 200 # 5 rounds at three sizes
#
# The same comparison scripts/measure-build-overhead.sh makes for the one-flow reference
# sample, made against a project the size the roadmap's P1 exit criterion names:
#
#   with     — the analyzer referenced, the generator running
#   without  — the analyzer dropped, the sources it produced compiled as ordinary files
#
# Both arms reach the SAME final compilation; the only difference is whether the generator
# ran. B12 settled that question at one flow and said in as many words that a one-file
# project maximises the generator's share, so its +0.4 % was biased in a known direction
# rather than merely imprecise. This measures the direction the bias runs the other way.
#
# More than one size is measured because the ratio is the less important half of the
# answer. A generator whose cost is superlinear in flow count fails at 400 flows however
# comfortably it passes at 200, and no single measurement can tell the two apart. The
# growth exponent printed at the end is what decides it.
#
# Arms alternate within a round rather than running all of one then all of the other, so a
# machine that slows down partway through penalises both equally.
set -euo pipefail

cd "$(dirname "$0")/.."
REPO="$PWD"

ROUNDS="${1:-5}"

if [ "$#" -gt 1 ]; then
  shift
  SIZES=("$@")
else
  SIZES=(50 200)
fi

export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1

WORK="$(mktemp -d)"
cleanup() { rm -rf "$WORK"; }
trap cleanup EXIT

RESULTS="$WORK/results.txt"
: >"$RESULTS"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "::error::dotnet is not on PATH. Nothing to measure."
  exit 1
fi

# One build of the synthetic project alone, in milliseconds. --no-dependencies keeps the
# FlowX assemblies out of it: they are identical in both arms, and rebuilding them every
# round would pad the denominator with work the generator has nothing to do with, which
# would shrink the ratio this is trying to measure.
build_ms() {
  local project="$1"
  shift
  local start end

  start=$(date +%s%N)

  if ! dotnet build "$project" -c Release --no-restore --no-dependencies --no-incremental \
       -v q "$@" >"$WORK/build.log" 2>&1; then
    echo "::error::A timed build failed. The measurement is void."
    cat "$WORK/build.log"
    exit 1
  fi

  end=$(date +%s%N)
  echo $(( (end - start) / 1000000 ))
}

for SIZE in "${SIZES[@]}"; do
  PROJECT_DIR="$WORK/scale-$SIZE"
  PROJECT="$PROJECT_DIR/ScaleSynthetic.csproj"
  PREGEN="$WORK/pregen-$SIZE"
  ASSEMBLY="$PROJECT_DIR/bin/Release/net10.0/ScaleSynthetic.dll"

  echo
  echo "=============================================================="
  echo "==> $SIZE flows"
  echo "=============================================================="

  python3 "$REPO/scripts/generate-scale-project.py" --flows "$SIZE" --out "$PROJECT_DIR" --repo "$REPO"

  # The first build restores, builds the FlowX projects the synthetic one references, and
  # produces the generated sources the control arm needs. None of it is timed.
  echo "==> Warming dependencies and capturing the generator's output"
  dotnet build "$PROJECT" -c Release -v q >"$WORK/build.log" 2>&1 || {
    echo "::error::The synthetic project does not build with the generator."
    cat "$WORK/build.log"
    exit 1
  }

  mkdir -p "$PREGEN"
  find "$PROJECT_DIR/obj/generated/FlowX.Compiler" -name '*.g.cs' -exec cp {} "$PREGEN/" \; 2>/dev/null || true
  COUNT=$(find "$PREGEN" -name '*.g.cs' | wc -l)

  # One plan per flow, plus the manifest. A short count means a flow failed to analyse and
  # emitted nothing, so the control arm would compile a smaller program than the treatment
  # arm and the difference between them would be partly missing code. That is the failure
  # mode most likely to produce a number that looks fine and means nothing.
  if [ "$COUNT" -ne $(( SIZE + 1 )) ]; then
    echo "::error::Expected $(( SIZE + 1 )) generated files, captured $COUNT."
    echo "::error::Some flows did not analyse. The comparison would not be like-for-like."
    exit 1
  fi

  echo "    captured $COUNT generated file(s)"

  # Prove the control arm really is the same program before spending minutes timing it.
  # A build that succeeds is not enough: dropping the pre-generated sources would also
  # succeed, quietly, because nothing in the synthetic project references the plan — and
  # would then time a compilation roughly half the size of its counterpart.
  echo "==> Verifying both arms compile the same program"

  build_ms "$PROJECT" >/dev/null
  WITH_BYTES=$(stat -c '%s' "$ASSEMBLY")

  build_ms "$PROJECT" /p:FlowXGeneratorDisabled=true "/p:FlowXPreGeneratedDir=$PREGEN" >/dev/null
  WITHOUT_BYTES=$(stat -c '%s' "$ASSEMBLY")

  if [ "$WITH_BYTES" -ne "$WITHOUT_BYTES" ]; then
    echo "::error::Assembly differs between arms: $WITH_BYTES vs $WITHOUT_BYTES bytes."
    echo "::error::The two arms are not compiling the same program."
    exit 1
  fi

  echo "    both arms produce a $WITH_BYTES byte assembly"

  echo "==> $ROUNDS rounds"

  for i in $(seq 1 "$ROUNDS"); do
    w=$(build_ms "$PROJECT")
    o=$(build_ms "$PROJECT" /p:FlowXGeneratorDisabled=true "/p:FlowXPreGeneratedDir=$PREGEN")

    echo "$SIZE $w $o" >>"$RESULTS"
    printf '    round %2d   with %6d ms   without %6d ms\n' "$i" "$w" "$o"
  done
done

echo
python3 - "$RESULTS" <<'PY'
import math
import statistics
import sys

BUDGET = 8.0

rows = [line.split() for line in open(sys.argv[1]).read().splitlines() if line.strip()]

by_size = {}
for size, with_gen, without_gen in rows:
    by_size.setdefault(int(size), []).append((int(with_gen), int(without_gen)))

sizes = sorted(by_size)
summary = {}

for size in sizes:
    pairs = by_size[size]
    with_gen = [w for w, _ in pairs]
    without_gen = [o for _, o in pairs]

    # Median, not mean: a single scheduling hiccup on shared hardware moves a mean and
    # leaves a median alone. Inherited from measure-build-overhead.sh, and the headline
    # figure is computed its way so the two reports are comparable.
    w = statistics.median(with_gen)
    o = statistics.median(without_gen)
    overhead = (w - o) / o * 100

    # The same question asked pairwise. The two arms of a round run seconds apart and the
    # ratio of medians does not know that, so on a machine whose speed drifts during the
    # run it compares builds that met different machines. Both are reported because
    # disagreement between them IS the finding when it happens: it means the drift is
    # large enough to matter and neither number should be quoted alone.
    paired = [(w_i - o_i) / o_i * 100 for w_i, o_i in pairs]
    paired_overhead = statistics.median(paired)
    paired_delta = statistics.median([w_i - o_i for w_i, o_i in pairs])

    summary[size] = {
        "with": w,
        "without": o,
        "overhead": overhead,
        "paired_overhead": paired_overhead,
        "delta": paired_delta,
        "with_range": (min(with_gen), max(with_gen)),
        "without_range": (min(without_gen), max(without_gen)),
    }

print("  P1 scale criterion — build overhead vs identical non-FlowX code")
print()
print(f"  {'flows':>6}  {'with':>9}  {'without':>9}  {'overhead':>9}  {'paired':>9}  {'verdict':>8}")
print(f"  {'-' * 6}  {'-' * 9}  {'-' * 9}  {'-' * 9}  {'-' * 9}  {'-' * 8}")

for size in sizes:
    row = summary[size]

    # Either statistic over budget fails. Taking the friendlier of two defensible numbers
    # is how a budget stops meaning anything.
    failed = max(row["overhead"], row["paired_overhead"]) > BUDGET

    print(
        f"  {size:>6}  {row['with']:>7.0f} ms  {row['without']:>7.0f} ms  "
        f"{row['overhead']:>+8.1f} %  {row['paired_overhead']:>+8.1f} %  "
        f"{'FAIL' if failed else 'PASS':>8}"
    )

print()

for size in sizes:
    row = summary[size]
    low, high = row["with_range"]

    # The spread decides whether the verdict means anything. Reported per size because a
    # ratio quoted without it is a number wearing a percentage sign.
    spread = (high - low) / row["with"] * 100
    overlap = max(low, row["without_range"][0]) < min(high, row["without_range"][1])

    print(f"  {size:>4} flows   with-arm range {low}–{high} ms ({spread:.1f} % of the median)")
    print(
        f"              generator cost {row['delta']:+.0f} ms, "
        f"{row['delta'] / size:+.1f} ms per flow"
        + ("   ARMS OVERLAP" if overlap else "")
    )

print()

# The ratio is the criterion; this is the finding. A generator whose cost grows faster
# than the flow count fails at 400 flows however comfortably it passes at 200, and no
# single size can tell the two apart. Sizes whose measured cost is negative are dropped
# rather than clamped: a negative cost means the noise floor is above the signal there,
# and pretending otherwise would fit a line to a number that does not exist.
usable = [size for size in sizes if summary[size]["delta"] > 0]

if len(usable) >= 2:
    xs = [math.log(size) for size in usable]
    ys = [math.log(summary[size]["delta"]) for size in usable]
    mean_x = sum(xs) / len(xs)
    mean_y = sum(ys) / len(ys)
    variance = sum((x - mean_x) ** 2 for x in xs)

    exponent = sum((x - mean_x) * (y - mean_y) for x, y in zip(xs, ys)) / variance

    print(
        f"  generator cost scales as flows^{exponent:.2f} "
        f"across {len(usable)} size(s): {', '.join(str(s) for s in usable)}"
    )
    print("  (1.0 is linear; above ~1.2 a pass at 200 flows does not generalise upward)")

    # The fit alone is not enough to report. A least-squares line through three noisy
    # points will happily read 1.0 while the segments it averages disagree with each
    # other and with the answer, and the difference between "linear" and "quadratic
    # above 200 flows" is the whole reason for measuring more than one size. Printing
    # each segment makes a fit that is averaging away a disagreement visible instead of
    # convincing.
    if len(usable) >= 3:
        print()
        print("  segment slopes, which the fit above averages:")

        for small, large in zip(usable, usable[1:]):
            segment = math.log(summary[large]["delta"] / summary[small]["delta"]) / math.log(
                large / small
            )
            print(f"    {small:>4} -> {large:<4} flows^{segment:.2f}")

        print("  A wide spread here means the fit is not evidence of anything; the sizes")
        print("  disagree and the noise floor, not the generator, is being measured.")
else:
    print("  Generator cost is below the measurement floor at all but one size, so no")
    print("  scaling exponent can be read from these numbers. Re-run with more rounds,")
    print("  on a quieter machine, or at larger sizes.")

below_floor = [s for s in sizes if summary[s]["delta"] <= 0]

if below_floor:
    print(
        "  Sizes whose measured generator cost was negative, i.e. below the noise floor, "
        f"and excluded from the fit: {', '.join(str(s) for s in below_floor)}"
    )

print()

worst = max(summary, key=lambda size: max(summary[size]["overhead"], summary[size]["paired_overhead"]))
worst_overhead = max(summary[worst]["overhead"], summary[worst]["paired_overhead"])

print(
    f"  {'PASS' if worst_overhead <= BUDGET else 'FAIL'} against the +{BUDGET:.0f} % budget "
    f"— worst size {worst} flows at {worst_overhead:+.1f} %"
)

sys.exit(0 if worst_overhead <= BUDGET else 1)
PY

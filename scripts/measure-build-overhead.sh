#!/usr/bin/env bash
#
# Budget B12 — build overhead vs identical non-FlowX code.
#
#   ./scripts/measure-build-overhead.sh          # 7 rounds
#   ./scripts/measure-build-overhead.sh 15       # 15 rounds
#
# Builds the reference sample twice per round, alternating:
#
#   with     — the analyzer referenced, the generator running
#   without  — the analyzer dropped, the generated sources compiled as ordinary files
#
# Both arms produce the SAME final compilation. The only difference is whether the
# generator ran, which is what the budget actually asks about. The earlier Roslyn-level
# benchmark could not construct that comparison: its control compiled a file with no
# plan and no dispatcher in it at all, so most of the difference was binding code the
# control did not contain. See docs/benchmarks/B12.md.
#
# Arms alternate within a round rather than running all of one then all of the other,
# so a machine that slows down partway through penalises both equally.
set -euo pipefail

cd "$(dirname "$0")/.."

ROUNDS="${1:-7}"
SAMPLE="samples/ecommerce/Ecommerce.csproj"
GENERATED="samples/ecommerce/obj/generated/FlowX.Compiler"
PREGEN="$(mktemp -d)"

export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1

cleanup() { rm -rf "$PREGEN"; }
trap cleanup EXIT

echo "==> Building once with the generator to capture its output"
dotnet build "$SAMPLE" -c Release --no-incremental -v q >/dev/null

if [ ! -d "$GENERATED" ]; then
  echo "::error::No generated sources at $GENERATED. The generator did not run."
  exit 1
fi

find "$GENERATED" -name '*.g.cs' -exec cp {} "$PREGEN/" \;
COUNT=$(find "$PREGEN" -name '*.g.cs' | wc -l)

if [ "$COUNT" -eq 0 ]; then
  echo "::error::Captured no generated files. The comparison would be meaningless."
  exit 1
fi

echo "    captured $COUNT generated file(s)"

# Dependencies are built once and reused. Rebuilding them every round would measure the
# rest of the solution, which is identical in both arms and would shrink the very
# difference this is trying to see.
echo "==> Warming dependencies"
dotnet build "$SAMPLE" -c Release -v q >/dev/null

# One build, from a clean compilation of the sample only, in milliseconds.
build_ms() {
  local start end
  start=$(date +%s%N)
  dotnet build "$SAMPLE" -c Release --no-restore --no-dependencies --no-incremental \
    -v q "$@" >/dev/null
  end=$(date +%s%N)
  echo $(( (end - start) / 1000000 ))
}

WITH=()
WITHOUT=()

echo "==> $ROUNDS rounds"

for i in $(seq 1 "$ROUNDS"); do
  w=$(build_ms)
  o=$(build_ms /p:FlowXGeneratorDisabled=true "/p:FlowXPreGeneratedDir=$PREGEN")

  WITH+=("$w")
  WITHOUT+=("$o")

  printf '    round %2d   with %5d ms   without %5d ms\n' "$i" "$w" "$o"
done

python3 - "$ROUNDS" "${WITH[@]}" "${WITHOUT[@]}" <<'PY'
import sys, statistics

rounds = int(sys.argv[1])
values = [int(v) for v in sys.argv[2:]]
with_gen, without_gen = values[:rounds], values[rounds:]

# Median, not mean: a single scheduling hiccup on shared hardware moves a mean and
# leaves a median alone, and this comparison has no margin to spend on outliers.
w = statistics.median(with_gen)
o = statistics.median(without_gen)
overhead = (w - o) / o * 100

print()
print(f"  with the generator      {w:6.0f} ms   (min {min(with_gen)}, max {max(with_gen)})")
print(f"  without the generator   {o:6.0f} ms   (min {min(without_gen)}, max {max(without_gen)})")
print()
print(f"  B12 build overhead      {overhead:+.1f} %   budget +8 %")
print()

# The spread is reported because it decides whether the verdict means anything. If the
# arms overlap, the number is noise wearing a percentage sign.
spread = (max(with_gen) - min(with_gen)) / w * 100
print(f"  within-arm spread       {spread:.1f} % of the median")

if max(min(with_gen), min(without_gen)) < min(max(with_gen), max(without_gen)):
    print("  NOTE: the two arms' ranges overlap — treat the figure as indicative.")

print()
print("  PASS" if overhead <= 8 else "  FAIL", "against the +8 % budget")
sys.exit(0 if overhead <= 8 else 1)
PY

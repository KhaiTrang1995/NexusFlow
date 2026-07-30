#!/usr/bin/env bash
#
# Runs the benchmark harness and gates the results against the committed baseline.
#
#   ./scripts/run-benchmarks.sh              # run everything, then gate
#   ./scripts/run-benchmarks.sh '*Dispatch*' # run a subset (no gating: partial run)
#
set -euo pipefail

cd "$(dirname "$0")/.."

FILTER="${1:-*}"
ARTIFACTS="BenchmarkDotNet.Artifacts"

export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# Benchmarks measure the runtime, not the JIT warming up. Tiered PGO would keep
# re-optimising mid-run and widen the distribution for no useful signal.
export DOTNET_TieredPGO=0

rm -rf "$ARTIFACTS"

echo "==> Running benchmarks (filter: $FILTER)"
dotnet run --configuration Release --project tests/FlowX.Benchmarks -- --filter "$FILTER"

if [ "$FILTER" != "*" ]; then
  echo "==> Partial run; skipping the baseline gate."
  echo "    Run without a filter to gate against docs/benchmarks/baseline.json."
  exit 0
fi

echo
echo "==> Checking against the committed baseline"
python3 scripts/check-benchmark-budgets.py "$ARTIFACTS"

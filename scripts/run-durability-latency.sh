#!/usr/bin/env bash
#
# B7 and B8: what a durable step commit and a flow rehydration cost against a real
# PostgreSQL. Until this rig existed, both rows of docs/14-Performance.md carried a number
# nothing had ever measured.
#
#   ./scripts/run-durability-latency.sh                    # the budget's own parameters
#   ./scripts/run-durability-latency.sh --rate 2000 --commits 5000
#
# Every argument is passed through; see tests/FlowX.Durability.Bench/BenchOptions.cs for
# the full list. The recorded run is docs/benchmarks/B7-B8-durability.md.
#
# GATING. Opt-in on FLOWX_POSTGRES_CONNECTION, and the rig itself enforces the same rule
# tests/FlowX.Postgres.Tests does: unset is a skip with a reason, set-but-unreachable is a
# failure rather than a skip, because a skip there would report an unmeasured budget as a
# green job.
#
# In CI:
#
#   FLOWX_POSTGRES_CONNECTION="Host=localhost;Port=5432;Username=postgres;Database=postgres" \
#     ./scripts/run-durability-latency.sh
#
set -euo pipefail

cd "$(dirname "$0")/.."

# Under .artifacts/ because .gitignore already covers it.
RESULTS="${FLOWX_DURABILITY_JSON:-.artifacts/durability-latency.json}"

export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1

if [ -z "${FLOWX_POSTGRES_CONNECTION:-}" ]; then
  echo "==> Skipped: FLOWX_POSTGRES_CONNECTION is not set."
  echo "    Nothing was measured; B7 and B8 remain unverified by this run. Set it to a"
  echo "    PostgreSQL connection string to measure them."
  exit 0
fi

mkdir -p "$(dirname "$RESULTS")"

echo "==> Building the rig"
# Release, because Debug prices the rig's own bookkeeping alongside the store call. The
# difference is small next to a disk write and it is free to avoid.
dotnet build tests/FlowX.Durability.Bench/FlowX.Durability.Bench.csproj -c Release

echo "==> Measuring"
dotnet run --project tests/FlowX.Durability.Bench/FlowX.Durability.Bench.csproj \
  -c Release --no-build -- --json "$RESULTS" "$@"

echo "==> Verdict"
python3 scripts/check-durability-latency.py "$RESULTS"

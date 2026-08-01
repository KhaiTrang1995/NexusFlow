#!/usr/bin/env bash
#
# The QR2 chaos rig: SIGKILL a node at a step boundary, 10 000 times if you ask for it,
# against a shared PostgreSQL, and check that nothing was duplicated and nothing was lost.
#
#   ./scripts/run-chaos-qr2.sh                       # the default run — 200 flows, both arms
#   ./scripts/run-chaos-qr2.sh --flows 10000 --kill-every 100
#   ./scripts/run-chaos-qr2.sh --kill-position before-commit --keep-schema true
#
# Every argument is passed through to the coordinator; see tests/FlowX.Chaos/ChaosOptions.cs
# for the full list. The results document is docs/benchmarks/QR2-chaos.md.
#
# GATING. This script does not run as part of the ordinary suite and must not: it kills
# operating-system processes and takes minutes. It is opt-in on FLOWX_CHAOS, the way
# tests/FlowX.Postgres.Tests is opt-in on FLOWX_POSTGRES_CONNECTION, and it follows that
# suite's rule exactly —
#
#   * FLOWX_CHAOS unset            -> skip, with a reason, exit 0
#   * FLOWX_CHAOS set, no database -> FAIL, exit non-zero
#
# — because a skip in the second case would report a chaos run that never happened as a
# green CI job, which is the one outcome this package exists to prevent.
#
# In CI:
#
#   FLOWX_CHAOS=1 \
#   FLOWX_POSTGRES_CONNECTION="Host=localhost;Port=5432;Username=postgres;Database=postgres" \
#     ./scripts/run-chaos-qr2.sh --flows 2000 --kill-every 50
#
set -euo pipefail

cd "$(dirname "$0")/.."

# Under .artifacts/ because .gitignore already covers it: a harness that leaves an untracked
# file in the repository root teaches people to ignore `git status`.
RESULTS="${FLOWX_CHAOS_JSON:-.artifacts/chaos-qr2.json}"

export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1

if [ -z "${FLOWX_CHAOS:-}" ]; then
  echo "==> Skipped: FLOWX_CHAOS is not set."
  echo "    No process was killed and nothing about QR2 has been verified. This rig"
  echo "    SIGKILLs processes and takes minutes, so it is opt-in rather than part of"
  echo "    the ordinary suite. Set FLOWX_CHAOS=1 and FLOWX_POSTGRES_CONNECTION to run it."
  exit 0
fi

if [ -z "${FLOWX_POSTGRES_CONNECTION:-}" ]; then
  echo "==> FAILED: FLOWX_CHAOS is set and FLOWX_POSTGRES_CONNECTION names no server." >&2
  echo "    This is a failure rather than a skip on purpose: a skip here would report a" >&2
  echo "    chaos run that never happened as a green job." >&2
  exit 1
fi

mkdir -p "$(dirname "$RESULTS")"

echo "==> Building the rig"
dotnet build tests/FlowX.Chaos/FlowX.Chaos.csproj -c Release

echo
echo "==> Running"
dotnet tests/FlowX.Chaos/bin/Release/net10.0/FlowX.Chaos.dll --json "$RESULTS" "$@"

echo
echo "==> Verdict"
python3 scripts/check-chaos-qr2.py "$RESULTS"

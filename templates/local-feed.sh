#!/usr/bin/env bash
#
# Builds the FlowX packages a generated project references, into a local NuGet feed.
#
# Nothing is published to nuget.org yet. `dotnet new flowx` produces a project whose
# .csproj is the shipped shape — plain PackageReferences — so the only pre-release step
# is making those packages resolvable. That is this script, and the day the packages
# publish it is deleted and nothing in the template changes.
#
#   templates/local-feed.sh            pack, and register the feed as a NuGet source
#   templates/local-feed.sh --remove   unregister the source and delete the feed
#
# The source is registered in the user's global NuGet configuration, because the
# generated project deliberately carries no NuGet.config: a NuGet.config beside a web
# project is swept into the default Content glob and ends up in the publish output.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FEED="$ROOT/.artifacts/local-feed"
SOURCE_NAME="flowx-local"

# Every package a generated project resolves, directly or transitively.
PROJECTS=(
  src/FlowX.Abstractions
  src/FlowX.Core
  src/FlowX.Runtime
  src/FlowX.Hosting
  src/FlowX.Compiler
  src/FlowX.Compiler.CodeFixes
  plugins/FlowX.Http
)

if [[ "${1:-}" == "--remove" ]]; then
  dotnet nuget remove source "$SOURCE_NAME" >/dev/null 2>&1 || true
  rm -rf "$FEED"
  echo "Removed the '$SOURCE_NAME' source and $FEED."
  exit 0
fi

mkdir -p "$FEED"
rm -f "$FEED"/*.nupkg

# Evict the previous run's packages from the global cache before repacking.
#
# NuGet caches by id and version, and the version here does not change between runs — so
# a restore after the second pack keeps serving the FIRST build's assemblies. The symptom
# is the worst kind there is: templates/verify.sh builds, runs and asserts against a
# generator from an earlier commit, and reports everything green. Only the ids packed
# below are removed; nothing else in the cache is touched.
CACHE="${NUGET_PACKAGES:-$HOME/.nuget/packages}"

for project in "${PROJECTS[@]}"; do
  rm -rf "$CACHE/$(basename "$project" | tr '[:upper:]' '[:lower:]')"
done

for project in "${PROJECTS[@]}"; do
  # IncludeSymbols is on repository-wide, and the two analyzer projects have no symbol
  # package to make — they ship a netstandard2.0 analyzer asset and no lib/ — so pack
  # fails on them with NU5017 unless it is turned off here.
  dotnet pack "$ROOT/$project" -c Release -o "$FEED" --nologo -p:IncludeSymbols=false \
    | grep -E "Successfully created package|error" || true
done

# Idempotent: re-running must not fail on an already-registered source.
if dotnet nuget list source | grep -q "$SOURCE_NAME"; then
  dotnet nuget update source "$SOURCE_NAME" --source "$FEED" >/dev/null
else
  dotnet nuget add source "$FEED" --name "$SOURCE_NAME" >/dev/null
fi

echo
echo "Feed:   $FEED"
echo "Source: $SOURCE_NAME (global NuGet configuration; 'templates/local-feed.sh --remove' undoes it)"

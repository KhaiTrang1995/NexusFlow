#!/usr/bin/env bash
#
# The acceptance test for `dotnet new flowx`. Everything a reviewer would otherwise do by
# hand: build the feed, pack and install the template, generate a project into a
# throwaway directory, build it with warnings as errors, run it, and drive the endpoint.
#
#   templates/verify.sh
#
# Exit code is the result. Nothing outside .artifacts/ and the temporary directory is
# written, except the two things that are inherently machine-global and are undone at the
# end: the 'flowx-local' NuGet source and the installed template.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TEMPLATE_DIR="$ROOT/templates/FlowX.Templates"
CONTENT="$TEMPLATE_DIR/content/FlowX.Web"
WORK="$(mktemp -d)"
PORT="${FLOWX_VERIFY_PORT:-5177}"
APP_PID=""
INSTALLED=""
FAILURES=0

log()  { printf '\n\033[1m== %s\033[0m\n' "$1"; }
pass() { printf '   ok   %s\n' "$1"; }
fail() { printf '   FAIL %s\n' "$1"; FAILURES=$((FAILURES + 1)); }

cleanup() {
  [[ -n "$APP_PID" ]] && kill "$APP_PID" 2>/dev/null || true
  [[ -n "$INSTALLED" ]] && dotnet new uninstall "$INSTALLED" >/dev/null 2>&1 || true
  rm -rf "$WORK"
}
trap cleanup EXIT

# ---------------------------------------------------------------------------
log "The version the template pins is the version the repository builds"

property() { sed -n "s|.*<$1>\([^<]*\)</$1>.*|\1|p" "$2" | head -n 1; }

pinned="$(property FlowXVersion "$CONTENT/FlowXStarter.csproj")"
built="$(property VersionPrefix "$ROOT/Directory.Build.props")"

if [[ "$pinned" == "$built" ]]; then
  pass "FlowXVersion $pinned matches VersionPrefix $built"
else
  fail "template pins FlowX $pinned but the repository builds $built"
fi

# ---------------------------------------------------------------------------
log "Local package feed"
"$ROOT/templates/local-feed.sh" >/dev/null
pass "packed to .artifacts/local-feed and registered as 'flowx-local'"

# ---------------------------------------------------------------------------
log "Pack and install the template"

dotnet pack "$TEMPLATE_DIR" -c Release -o "$WORK/pack" --nologo >/dev/null
package="$(ls "$WORK"/pack/FlowX.Templates.*.nupkg)"
pass "packed $(basename "$package")"

dotnet new install "$package" --force >/dev/null
INSTALLED="FlowX.Templates"
pass "installed; 'dotnet new flowx' is available"

# ---------------------------------------------------------------------------
log "Generate a project"

dotnet new flowx -o "$WORK/Ordering" -n Ordering >/dev/null
pass "dotnet new flowx -o Ordering -n Ordering"

[[ -f "$WORK/Ordering/Ordering.csproj" ]] \
  && pass "the project file took the name" \
  || fail "expected Ordering.csproj"

if grep -rq "FlowXStarter" "$WORK/Ordering"; then
  fail "the template source name survived substitution:"
  grep -rn "FlowXStarter" "$WORK/Ordering" | sed 's/^/        /'
else
  pass "no occurrence of the template source name remains"
fi

grep -q 'namespace Ordering;' "$WORK/Ordering/Contracts.cs" \
  && pass "the namespace took the name" \
  || fail "expected 'namespace Ordering;'"

[[ ! -d "$WORK/Ordering/.template.config" ]] \
  && pass ".template.config was not copied into the output" \
  || fail ".template.config leaked into the generated project"

# ---------------------------------------------------------------------------
log "Build it, with warnings as errors"

build_log="$WORK/build.log"
if dotnet build "$WORK/Ordering" -c Release --nologo -p:TreatWarningsAsErrors=true > "$build_log" 2>&1; then
  pass "build succeeded"
else
  fail "build failed"
  sed 's/^/        /' "$build_log"
fi

if grep -qE "^ +0 Warning\(s\)" "$build_log"; then
  pass "0 warnings"
else
  fail "the build produced warnings"
  grep -iE "warning" "$build_log" | sed 's/^/        /'
fi

# ---------------------------------------------------------------------------
log "What the generator produced"

generated="$WORK/Ordering/obj/generated/FlowX.Compiler/FlowX.Compiler.FlowPlanGenerator"

[[ -f "$generated/Ordering.OpenTicketFlow.Flow.g.cs" ]] \
  && pass "the plan and dispatcher are on disk, debuggable" \
  || fail "expected a generated flow partial under obj/generated"

# The endpoint the flow declares, emitted from [HttpTrigger] rather than restated in
# Program.cs. Two assertions, because both halves matter: the address has to reach
# generated code, and it must NOT appear anywhere a developer writes — a project that
# names its own route a second time is a project that can drift from it.
endpoints="$generated/FlowXEndpoints.g.cs"
if [[ -f "$endpoints" ]]; then
  pass "the endpoint registration was generated"

  grep -q '"/api/v1/tickets"' "$endpoints" \
    && pass "the generated endpoint carries the route the flow declared" \
    || fail "the generated endpoint does not carry the declared route"

  grep -q 'requireIdempotencyKey: true' "$endpoints" \
    && pass "the generated endpoint carries the declared idempotency rule" \
    || fail "Idempotent = true did not reach the generated endpoint"
else
  fail "no endpoint registration was generated"
fi

if grep -qE '"(POST|GET|PUT|PATCH|DELETE)"|/api/v1/tickets' "$WORK/Ordering/Program.cs"; then
  fail "Program.cs restates the address the flow already declares:"
  grep -nE '"(POST|GET|PUT|PATCH|DELETE)"|/api/v1/tickets' "$WORK/Ordering/Program.cs" \
    | sed 's/^/        /'
else
  pass "Program.cs names no method and no route"
fi

manifest="$generated/FlowXManifest.g.cs"
if [[ -f "$manifest" ]]; then
  pass "the manifest was generated"

  grep -q '""ticket.open""' "$manifest" \
    && pass "the manifest names the flow" \
    || fail "the manifest does not name the flow"

  # The C# member name, not the camelCase wire name — ProblemDetailsMapper matches
  # case-insensitively for exactly that reason.
  grep -q '""ContactPhone""' "$manifest" \
    && pass "the manifest records the [Sensitive] member" \
    || fail "the manifest does not record the [Sensitive] member"

  # A manifest carrying the build agent's directory layout is not reproducible.
  if grep -qE '""source"": ""(/|[A-Za-z]:)' "$manifest"; then
    fail "the manifest records absolute source paths"
  else
    pass "source pointers are relative to the project"
  fi
else
  fail "no manifest was generated"
fi

# ---------------------------------------------------------------------------
log "Run it"

ASPNETCORE_URLS="http://127.0.0.1:$PORT" \
  dotnet run --project "$WORK/Ordering" -c Release --no-build > "$WORK/app.log" 2>&1 &
APP_PID=$!

ready=""
for _ in $(seq 1 60); do
  if curl -fsS -o /dev/null "http://127.0.0.1:$PORT/health" 2>/dev/null; then ready=1; break; fi
  sleep 0.5
done

if [[ -n "$ready" ]]; then
  pass "the application started and /health answers"
else
  fail "the application did not become ready"
  sed 's/^/        /' "$WORK/app.log"
fi

body='{"subject":"Printer on fire","reporter":"ops","contactPhone":"+44 7700 900000"}'

# The generated project authenticates its callers, because every capability it contains
# declares who may call it and the engine enforces that. `post` therefore presents the
# token that satisfies both stances; the anonymous and under-privileged cases are asserted
# on purpose further down, with `post_as`.
post_as() {
  local token="$1" key="$2" payload="${3:-$body}"
  curl -sS -o "$WORK/response" -w '%{http_code}' \
    -X POST "http://127.0.0.1:$PORT/api/v1/tickets" \
    -H 'Content-Type: application/json' \
    ${token:+-H "Authorization: Bearer $token"} \
    ${key:+-H "Idempotency-Key: $key"} \
    -d "$payload"
}

post() { post_as support-token "${1:-}" "${2:-$body}"; }

# ---- the two refusals, first, because they are what a template silently loses ----
#
# This project was shipped for a while with capabilities declaring a stance and no
# authentication wired up to satisfy it, so `dotnet new flowx && dotnet run` produced an
# application that refused its own documented request. Asserting the refusals here means
# the next person to remove the wiring is told which of the two things they broke.

status="$(post_as '' ticket-anon)"
if [[ "$status" == "403" ]] && grep -q '"code":"authorization.not_authenticated"' "$WORK/response"; then
  pass "a request with no token is refused by ticket.validate"
else
  fail "expected 403 + authorization.not_authenticated, got $status $(cat "$WORK/response")"
fi

status="$(post_as reader-token ticket-reader)"
if [[ "$status" == "403" ]] && grep -q '"code":"authorization.permission_denied"' "$WORK/response"; then
  pass "an authenticated caller without ticket.write is refused by ticket.record"
else
  fail "expected 403 + authorization.permission_denied, got $status $(cat "$WORK/response")"
fi

status="$(post ticket-1)"
if [[ "$status" == "200" && "$(cat "$WORK/response")" == '{"ticketId":"ticket-1","subject":"Printer on fire"}' ]]; then
  pass "POST /api/v1/tickets returned the projected result"
else
  fail "POST returned $status $(cat "$WORK/response")"
fi

[[ "$(post ticket-1)" == "200" ]] \
  && pass "the same idempotency key is accepted again" \
  || fail "a repeated request was rejected"

status="$(post ticket-2 '{"subject":"   ","reporter":"ops","contactPhone":"+44 7700 900000"}')"
if [[ "$status" == "400" ]] && grep -q '"code":"ticket.subject_required"' "$WORK/response"; then
  pass "a business failure is problem details carrying the error code"
else
  fail "expected 400 + ticket.subject_required, got $status $(cat "$WORK/response")"
fi

[[ "$(post '' )" == "400" ]] \
  && pass "the endpoint enforces the idempotency the flow declared" \
  || fail "a request with no Idempotency-Key was accepted"

if grep -q '7700 900000' "$WORK/app.log"; then
  fail "the [Sensitive] member reached the log"
else
  pass "the [Sensitive] member is not in the log"
fi

# ---------------------------------------------------------------------------
printf '\n'
if [[ "$FAILURES" -eq 0 ]]; then
  printf '\033[1mdotnet new flowx: verified.\033[0m\n'
else
  printf '\033[1m%s check(s) failed.\033[0m\n' "$FAILURES"
fi
exit "$FAILURES"

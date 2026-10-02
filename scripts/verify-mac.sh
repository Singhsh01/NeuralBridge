#!/usr/bin/env bash
# Full local verification for NeuralBridge on macOS (or Linux). Writes everything to verify-logs/.
# Usage: bash ~/Documents/NeuralBridge/scripts/verify-mac.sh
#
# Steps: restore → build (warnings are errors) → format check → unit tests (xUnit, incl. EF Core
# on SQLite) → SQLite smoke test over HTTP → Playwright suite (mock Google provider, fake
# camera/mic) → accounts on the real SQLite/EF Identity store with Google NOT configured.
set -u
cd "$(dirname "$0")/.."
ROOT=$(pwd)
LOG="$ROOT/verify-logs"

# One run at a time: a second concurrent run would fight over ports and the log folder.
LOCK="${TMPDIR:-/tmp}/neuralbridge-verify.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
  echo "Another verification run is in progress (lock: $LOCK). If it isn't, delete that folder and retry."
  exit 2
fi
PIDS=()
cleanup() {
  for pid in "${PIDS[@]:-}"; do [ -n "$pid" ] && kill "$pid" 2>/dev/null; done
  rmdir "$LOCK" 2>/dev/null
}
trap cleanup EXIT INT TERM

rm -rf "$LOG"; mkdir -p "$LOG"
SUMMARY="$LOG/SUMMARY.txt"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
FAILED=0
step() { # name, command...
  local name=$1; shift
  printf '\n▶ %s ... ' "$name"
  local start; start=$(date +%s)
  ( "$@" ) > "$LOG/$name.log" 2>&1
  local code=$? dur=$(( $(date +%s) - start ))
  echo "$name: exit=$code (${dur}s)" >> "$SUMMARY"
  if [ $code -eq 0 ]; then echo "ok (${dur}s)"; else echo "FAILED (exit $code) — see verify-logs/$name.log"; FAILED=1; fi
  return $code
}

free_port() { # kill anything still listening on a port from an earlier run
  local pids; pids=$(lsof -ti tcp:"$1" 2>/dev/null || true)
  [ -n "$pids" ] && kill $pids 2>/dev/null && sleep 1
  return 0
}

wait_for() { # url
  for _ in $(seq 1 90); do curl -fs "$1" >/dev/null && return 0; sleep 1; done
  return 1
}

echo "NeuralBridge verification — logs in $LOG"
{ sw_vers 2>/dev/null; uname -m; echo; which dotnet; dotnet --info; echo; node --version; npm --version; } > "$LOG/00-environment.log" 2>&1

if ! command -v dotnet >/dev/null || ! dotnet --list-sdks | grep -q '^10\.'; then
  echo "00-environment: .NET 10 SDK missing" >> "$SUMMARY"
  echo; echo "✖ .NET 10 SDK not found. Install it (brew install --cask dotnet-sdk, or https://dotnet.microsoft.com/download/dotnet/10.0) and run this script again."
  exit 1
fi
for p in 5291 5292 5299 5399; do free_port $p; done

step 01-restore dotnet restore NeuralBridge.sln
step 02-build   dotnet build NeuralBridge.sln --no-restore -c Debug
step 03-format  dotnet format NeuralBridge.sln --verify-no-changes --no-restore
step 04-unit-tests dotnet test NeuralBridge.sln --no-build --logger "console;verbosity=normal"

# Smoke test with the real SQLite + EF Core provider (fresh temp database).
# Request bodies go through files so no shell quoting is involved.
smoke() {
  local dir; dir=$(mktemp -d)
  ASPNETCORE_ENVIRONMENT=Development \
  ConnectionStrings__Sqlite="Data Source=$dir/smoke.db" \
  NeuralBridge__Hosting__HttpsRedirection=false \
  dotnet run --project src/NeuralBridge.Web --no-build --no-launch-profile -- --urls http://127.0.0.1:5291 &
  local pid=$!; PIDS+=("$pid")
  wait_for http://127.0.0.1:5291/healthz || { echo "server did not start"; return 1; }
  echo "healthz:   $(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:5291/healthz)"
  echo "home:      $(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:5291/)"
  echo "providers: $(curl -s http://127.0.0.1:5291/api/auth/providers)"
  printf '%s' '{"displayName":"smoke","pin":"4821","lifetimeMinutes":15}' > "$dir/create.json"
  curl -s -X POST -H 'Content-Type: application/json' --data-binary @"$dir/create.json" http://127.0.0.1:5291/api/sessions > "$dir/created.json"
  sed -E 's/"token":"[^"]+"/"token":"<redacted>"/' "$dir/created.json"; echo
  local code; code=$(sed -E 's/.*"code":"([^"]+)".*/\1/' "$dir/created.json")
  printf '{"code":"%s"}' "$code" > "$dir/join-nopin.json"
  printf '{"code":"%s","pin":"0000"}' "$code" > "$dir/join-wrong.json"
  printf '{"code":"%s","pin":"4821"}' "$code" > "$dir/join-pin.json"
  local a b c
  a=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' --data-binary @"$dir/join-nopin.json" http://127.0.0.1:5291/api/sessions/join)
  b=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' --data-binary @"$dir/join-wrong.json" http://127.0.0.1:5291/api/sessions/join)
  c=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' --data-binary @"$dir/join-pin.json" http://127.0.0.1:5291/api/sessions/join)
  echo "join without PIN: $a (expect 404)"; echo "join wrong PIN:   $b (expect 404)"; echo "join with PIN:    $c (expect 200)"
  echo "tables:"; sqlite3 "$dir/smoke.db" '.tables' 2>/dev/null || echo "(sqlite3 CLI not installed)"
  kill $pid; wait $pid 2>/dev/null
  [ "$a" = 404 ] && [ "$b" = 404 ] && [ "$c" = 200 ]
}
step 05-sqlite-smoke smoke

# Playwright: the config starts the mock OAuth provider and the app (in-memory) itself.
e2e() {
  cd tests/NeuralBridge.E2ETests
  npm install --no-audit --no-fund && npx playwright install chromium && NB_PORT=5299 npx playwright test --reporter=list
}
step 06-e2e-playwright e2e

# Accounts against the real EF Core Identity store (SQLite), with Google not configured:
# the Google button must be disabled, and register / log in / reset / delete must work.
e2e_sqlite_no_google() {
  local dir; dir=$(mktemp -d)
  ASPNETCORE_ENVIRONMENT=Development \
  ConnectionStrings__Sqlite="Data Source=$dir/accounts.db" \
  NeuralBridge__Hosting__HttpsRedirection=false \
  NeuralBridge__Location__ReverseGeocodingEnabled=false \
  NeuralBridge__RateLimits__CreateSessionPerMinute=500 NeuralBridge__RateLimits__JoinSessionPerMinute=500 \
  NeuralBridge__RateLimits__AuthenticationAttemptsPerMinute=500 NeuralBridge__RateLimits__HttpRequestsPerMinute=20000 \
  dotnet run --project src/NeuralBridge.Web --no-build --no-launch-profile -- --urls http://127.0.0.1:5292 > "$LOG/07-server.log" 2>&1 &
  local pid=$!; PIDS+=("$pid")
  wait_for http://127.0.0.1:5292/healthz || { echo "server did not start"; return 1; }
  (cd tests/NeuralBridge.E2ETests && NB_BASE_URL=http://127.0.0.1:5292 NB_EXPECT_GOOGLE=false npx playwright test tests/auth.spec.ts tests/security-and-owner.spec.ts --reporter=list)
  local code=$?
  kill $pid; wait $pid 2>/dev/null
  return $code
}
step 07-e2e-sqlite-accounts-no-google e2e_sqlite_no_google

echo; echo "──── Summary ────"; cat "$SUMMARY"
exit $FAILED

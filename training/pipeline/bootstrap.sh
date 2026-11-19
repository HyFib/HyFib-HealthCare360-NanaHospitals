#!/usr/bin/env bash
# From a fresh Linux machine (as root) to a training server holding the 11:10 am morning, ready for make.sh. Each step
# is skipped when it is already done, so the script can be run again after a failure.
#
#   training/pipeline/bootstrap.sh
#
# Needs: .NET 10 SDK, Node 22 with pnpm, PostgreSQL 16 server binaries, ffmpeg with libass, Chromium for Playwright,
# and network access to NuGet, npm, PyPI, GitHub release downloads and the Ubuntu archive.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
ENV_FILE="${HC360_TRAINING_ENV:-$HOME/.hc360-training.env}"
PG_BIN="${PG_BIN:-/usr/lib/postgresql/16/bin}"
PG_DATA="${PG_DATA:-/var/tmp/hc360-pg/data}"
DATA_DIR="${HC360_DATA_DIR:-/var/tmp/hc360-data}"
SNAPSHOTS="${HC360_SNAPSHOTS:-/var/tmp/hc360-snapshots}"
LOG="${HC360_LOG_DIR:-/var/tmp/hc360-logs}"
mkdir -p "$LOG"
step() { echo "== $*"; }

step "libfaketime (the hospital's clock)"
if [ ! -f /usr/lib/x86_64-linux-gnu/faketime/libfaketime.so.1 ]; then
  apt-get update -qq >/dev/null && apt-get install -y -qq faketime libfaketime >/dev/null
fi

step "memory for the server's compiler (6.9 million lines of migration snapshots need about 14 GB)"
if [ "$(swapon --show --noheadings | wc -l)" = 0 ] && [ "$(awk '/MemTotal/ {print $2}' /proc/meminfo)" -lt 25000000 ]; then
  fallocate -l 12G /var/tmp/hc360-swap && chmod 600 /var/tmp/hc360-swap && mkswap /var/tmp/hc360-swap >/dev/null \
    && swapon /var/tmp/hc360-swap || echo "   no swap: the build may run out of memory"
fi

step "secrets (outside the repository: $ENV_FILE)"
if [ ! -f "$ENV_FILE" ]; then
  umask 077
  {
    echo "export HC360_DEMO_PASSWORD='Trn-$(openssl rand -hex 9)-Kx7'"
    db="db-$(openssl rand -hex 12)"
    echo "export HC360_DB_PASSWORD='$db'"
    echo "export HC360_DEMO_DB='postgresql://hc360:$db@localhost:57360/hc360'"
    echo "export FAKETIME_TIMESTAMP_FILE=/var/tmp/hc360-demo-clock.txt"
    echo "export FAKETIME_CACHE_DURATION=1"
    echo "export FAKETIME_DONT_FAKE_MONOTONIC=1"
    echo "export NODE_EXTRA_CA_CERTS=$DATA_DIR/certs/hc360-hospital-ca.crt"
  } >"$ENV_FILE"
fi
# shellcheck disable=SC1090
source "$ENV_FILE"
export HC360_DEMO_PASSWORD HC360_DEMO_DB NODE_EXTRA_CA_CERTS

step "the staff app and the patient portal (before the server: its build lists the files it serves)"
if [ ! -f "$ROOT/src/server/HyFib.HealthCare360.Server/wwwroot/index.html" ]; then
  (cd "$ROOT/src/web" && pnpm install --frozen-lockfile >"$LOG/pnpm.log" 2>&1 && pnpm -r --filter './apps/**' build >>"$LOG/pnpm.log" 2>&1) \
    || { tail -20 "$LOG/pnpm.log"; exit 1; }
fi

step "the server (Release, analyzers off: the compiled code is the same)"
if [ ! -f "$ROOT/src/server/HyFib.HealthCare360.Server/bin/Release/net10.0/HyFib.HealthCare360.Server.dll" ]; then
  (cd "$ROOT" && DOTNET_gcServer=0 DOTNET_GCHeapHardLimit=0x600000000 DOTNET_GCConserveMemory=5 \
    dotnet build src/server/HyFib.HealthCare360.Server -c Release -m:1 -p:RunAnalyzers=false -p:UseSharedCompilation=false \
    >"$LOG/dotnet-build.log" 2>&1) || { tail -20 "$LOG/dotnet-build.log"; exit 1; }
fi

step "the voice and the checking recogniser"
"$HERE/setup-models.sh"

step "PostgreSQL for the training server"
if [ ! -d "$PG_DATA" ]; then
  mkdir -p "$(dirname "$PG_DATA")" && chown postgres:postgres "$(dirname "$PG_DATA")"
  runuser -u postgres -- "$PG_BIN/initdb" -D "$PG_DATA" -U postgres --auth=trust -E UTF8 --locale=C.UTF-8 >"$LOG/initdb.log"
  "$HERE/server.sh" clock 09:00
  "$HERE/server.sh" db-start
  psql -h localhost -p 57360 -U postgres -qc "create role hc360 login password '$HC360_DB_PASSWORD' createdb;" \
    -c "create database hc360 owner hc360;"
fi
"$HERE/server.sh" db-start

if [ ! -f "$SNAPSHOTS/morning.moment" ]; then
  if [ ! -f "$SNAPSHOTS/story.moment" ]; then
    step "the demo seed, made at 9:00 am on an empty database"
    "$HERE/server.sh" stop
    "$HERE/server.sh" clock 09:00
    "$HERE/server.sh" start
    step "the morning, 9:01 to 11:10 am (src/web/demo/story.mjs)"
    "$HERE/server.sh" clock 09:01
    sleep 2
    (cd "$ROOT/src/web" && DEMO_CLOCK_FILE="$FAKETIME_TIMESTAMP_FILE" node demo/story.mjs "$LOG/story.json")
    step "an account for each role the demo has none for"
    node "$HERE/accounts.mjs"
    "$HERE/server.sh" snapshot story
  fi
  step "the take-on, and the snapshot every video starts from"
  "$HERE/server.sh" restore story >/dev/null
  node "$HERE/take-on.mjs"
  "$HERE/server.sh" snapshot morning
fi

"$HERE/server.sh" restore morning
echo "ready: training/pipeline/make.sh <storyboard id>..."

#!/usr/bin/env bash
# The training server the videos are recorded on: HC360 built from this repository, on a PostgreSQL of its own, on a
# clock of its own (libfaketime, as src/web/demo/README.md describes), with the demo seed and the scripted morning of
# src/web/demo/story.mjs. Never point this at a hospital's database: the videos register patients, bill and dispense.
#
#   training/pipeline/server.sh db-start | db-stop
#   training/pipeline/server.sh start | stop | status
#   training/pipeline/server.sh clock HH:MM [YYYY-MM-DD]   move the hospital's clock (IST) and let it run from there
#   training/pipeline/server.sh snapshot NAME              keep the database and data folder as they are now
#   training/pipeline/server.sh restore NAME               put them back, clock at the snapshot's moment, and start
#
# Secrets come from $HC360_TRAINING_ENV (default ~/.hc360-training.env), never from the repository:
#   HC360_DEMO_PASSWORD   the training accounts' password (not on breach lists, not the hospital's name)
#   HC360_DB_PASSWORD     the training database's password
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
ENV_FILE="${HC360_TRAINING_ENV:-$HOME/.hc360-training.env}"
# shellcheck disable=SC1090
[ -f "$ENV_FILE" ] && source "$ENV_FILE"

PG_BIN="${PG_BIN:-/usr/lib/postgresql/16/bin}"
PG_DATA="${PG_DATA:-/var/tmp/hc360-pg/data}"
PG_PORT="${PG_PORT:-57360}"
DATA_DIR="${HC360_DATA_DIR:-/var/tmp/hc360-data}"
SNAPSHOTS="${HC360_SNAPSHOTS:-/var/tmp/hc360-snapshots}"
CLOCK_FILE="${FAKETIME_TIMESTAMP_FILE:-/var/tmp/hc360-demo-clock.txt}"
FAKETIME_LIB="${FAKETIME_LIB:-/usr/lib/x86_64-linux-gnu/faketime}"
SERVER_DIR="$ROOT/src/server/HyFib.HealthCare360.Server"
SERVER_DLL="$SERVER_DIR/bin/Release/net10.0/HyFib.HealthCare360.Server.dll"
LOG_DIR="${HC360_LOG_DIR:-/var/tmp/hc360-logs}"
PID_FILE="$LOG_DIR/server.pid"
mkdir -p "$LOG_DIR" "$SNAPSHOTS"

faketime_env=(FAKETIME_TIMESTAMP_FILE="$CLOCK_FILE" FAKETIME_CACHE_DURATION=1 FAKETIME_DONT_FAKE_MONOTONIC=1)

psql_admin() { psql -h localhost -p "$PG_PORT" -U postgres -v ON_ERROR_STOP=1 -qAt "$@"; }

db_start() {
  # pg_ctl refuses to run as root, and PostgreSQL writes its log as its own user.
  if runuser -u postgres -- "$PG_BIN/pg_ctl" -D "$PG_DATA" status >/dev/null 2>&1; then return; fi
  runuser -u postgres -- env LD_PRELOAD="$FAKETIME_LIB/libfaketime.so.1" "${faketime_env[@]}" \
    "$PG_BIN/pg_ctl" -D "$PG_DATA" -l "$(dirname "$PG_DATA")/postgres.log" -w \
    -o "-p $PG_PORT -c max_connections=300 -c shared_buffers=512MB -c listen_addresses=localhost -c unix_socket_directories=/var/tmp/hc360-pg" start >/dev/null 9>&-
}

db_stop() { runuser -u postgres -- "$PG_BIN/pg_ctl" -D "$PG_DATA" -m fast stop >/dev/null 2>&1 || true; }

# The clock file holds an offset in seconds from the real clock, so the hospital's time runs on from HH:MM.
clock() {
  local hhmm="$1" day="${2:-$(TZ=Asia/Kolkata date +%F)}"
  local target now
  target=$(TZ=Asia/Kolkata date -d "$day $hhmm" +%s)
  now=$(date +%s)
  printf '%+d\n' $((target - now)) >"$CLOCK_FILE"
  chmod 644 "$CLOCK_FILE"
}

start() {
  [ -f "$SERVER_DLL" ] || { echo "Build the server first: dotnet build src/server/HyFib.HealthCare360.Server -c Release"; exit 1; }
  if [ -f "$PID_FILE" ] && kill -0 "$(cat "$PID_FILE")" 2>/dev/null; then echo "running"; return; fi
  db_start
  mkdir -p "$DATA_DIR"
  (
    cd "$SERVER_DIR"
    env LD_PRELOAD="$FAKETIME_LIB/libfaketimeMT.so.1" "${faketime_env[@]}" \
      ASPNETCORE_ENVIRONMENT=Development \
      DOTNET_gcServer=0 \
      HC360_ConnectionStrings__Hospital="Host=localhost;Port=$PG_PORT;Database=hc360;Username=hc360;Password=$HC360_DB_PASSWORD;Maximum Pool Size=50" \
      HC360_HealthCare360__DataDirectory="$DATA_DIR" \
      HC360_HealthCare360__SeedDemoData=true \
      HC360_HealthCare360__Bootstrap__AdminPassword="$HC360_DEMO_PASSWORD" \
      HC360_HealthCare360__Bootstrap__RequirePasswordChange=false \
      HC360_HealthCare360__Security__EnforceMfa=false \
      HC360_HealthCare360__Security__AdministrationRequiresDesktop=false \
      HC360_HealthCare360__Network__EnableMdns=false \
      HC360_HealthCare360__Network__EnableWifiMonitor=false \
      HC360_HealthCare360__RateLimits__SignIn__PermitLimit=2000 \
      HC360_HealthCare360__RateLimits__PerUser__TokenLimit=4000 \
      HC360_HealthCare360__RateLimits__PerUser__TokensPerPeriod=2000 \
      nohup dotnet "$SERVER_DLL" >"$LOG_DIR/server.log" 2>&1 9>&- &  # 9: make.sh's lock, which the server must not keep
    echo $! >"$PID_FILE"
  )
  for _ in $(seq 1 180); do
    if curl -sk -o /dev/null -w '%{http_code}' https://localhost:7360/health/live 2>/dev/null | grep -q 200; then
      echo "up: https://localhost:7360 (staff), https://localhost:7361/portal/ (patients)"
      return
    fi
    if ! kill -0 "$(cat "$PID_FILE")" 2>/dev/null; then echo "the server stopped:"; tail -30 "$LOG_DIR/server.log"; exit 1; fi
    sleep 1
  done
  echo "the server did not answer in 3 minutes"; tail -30 "$LOG_DIR/server.log"; exit 1
}

stop() {
  if [ -f "$PID_FILE" ]; then
    kill "$(cat "$PID_FILE")" 2>/dev/null || true
    for _ in $(seq 1 30); do kill -0 "$(cat "$PID_FILE")" 2>/dev/null || break; sleep 1; done
    kill -9 "$(cat "$PID_FILE")" 2>/dev/null || true
    rm -f "$PID_FILE"
  fi
}

snapshot() {
  local name="$1"
  stop
  psql_admin -c "drop database if exists hc360_snap_$name;" -c "create database hc360_snap_$name template hc360 owner hc360;"
  rm -rf "$SNAPSHOTS/$name.data" && cp -a "$DATA_DIR" "$SNAPSHOTS/$name.data"
  # The moment the snapshot shows, read from the server's own clock (the database's now()).
  psql_admin -d hc360 -c "select to_char(now() at time zone 'Asia/Kolkata', 'YYYY-MM-DD HH24:MI');" >"$SNAPSHOTS/$name.moment"
  echo "snapshot $name at $(cat "$SNAPSHOTS/$name.moment") IST"
}

restore() {
  local name="$1" moment
  moment="$(cat "$SNAPSHOTS/$name.moment")"
  stop
  db_start
  psql_admin -c "select pg_terminate_backend(pid) from pg_stat_activity where datname = 'hc360' and pid <> pg_backend_pid();" >/dev/null
  psql_admin -c "drop database if exists hc360;" -c "create database hc360 template hc360_snap_$name owner hc360;"
  rm -rf "$DATA_DIR" && cp -a "$SNAPSHOTS/$name.data" "$DATA_DIR"
  clock "${moment#* }" "${moment% *}"
  start
}

case "${1:-}" in
  db-start) db_start ;;
  db-stop) db_stop ;;
  start) start ;;
  stop) stop ;;
  status) curl -sk -o /dev/null -w '%{http_code}\n' https://localhost:7360/health/live || true ;;
  clock) clock "$2" "${3:-}" ;;
  snapshot) snapshot "$2" ;;
  restore) restore "$2" ;;
  *) sed -n '2,15p' "$0"; exit 2 ;;
esac

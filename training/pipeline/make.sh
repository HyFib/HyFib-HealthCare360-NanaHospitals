#!/usr/bin/env bash
# Makes training videos from their storyboards, each from the same recorded hospital morning:
#
#   training/pipeline/make.sh 00-getting-started w2-front-office ...     (ids of training/storyboards/*.mjs)
#   training/pipeline/make.sh --all
#
# For each: the training server goes back to the snapshot "morning" (server.sh), the storyboard runs on it as its
# role (capture.mjs: the run is the video's test), the narration is spoken (narrate.py), the video is put together
# (compose.py) and checked (qa.py). Output in training/out/<id>/ (not committed): <id>.mp4, .srt, .vtt, qa.md.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
OUT="${HC360_TRAINING_OUT:-$ROOT/training/out}"
PY="${HC360_PY:-$ROOT/training/.venv/bin/python}"
SNAPSHOT="${HC360_SNAPSHOT:-morning}"
ENV_FILE="${HC360_TRAINING_ENV:-$HOME/.hc360-training.env}"
# shellcheck disable=SC1090
[ -f "$ENV_FILE" ] && source "$ENV_FILE"
export HC360_DEMO_PASSWORD HC360_DEMO_DB
# The training server's own hospital CA (server.sh keeps its data in /var/tmp/hc360-data).
export NODE_EXTRA_CA_CERTS="${NODE_EXTRA_CA_CERTS:-${HC360_DATA_DIR:-/var/tmp/hc360-data}/certs/hc360-hospital-ca.crt}"

ids=("$@")
if [ "${1:-}" = "--all" ]; then
  mapfile -t ids < <(ls "$ROOT/training/storyboards" | sed -n 's/\.mjs$//p' | sort)
fi
[ ${#ids[@]} -gt 0 ] || { sed -n '2,10p' "$0"; exit 2; }

failed=()
for id in "${ids[@]}"; do
  echo "== $id"
  dir="$OUT/$id"
  rm -rf "$dir" && mkdir -p "$dir"
  # One run at a time on the training server: restoring it restarts the server under anyone else's run.
  if ! (flock 9 && "$HERE/server.sh" restore "$SNAPSHOT" >/dev/null && node "$HERE/capture.mjs" "$ROOT/training/storyboards/$id.mjs" "$dir") 9>"${HC360_LOCK:-/var/tmp/hc360-server.lock}"; then
    failed+=("$id (capture)"); continue
  fi
  "$PY" -I "$HERE/narrate.py" "$dir/segments.json" "$dir/audio"
  "$PY" -I "$HERE/compose.py" "$dir"
  if ! "$PY" -I "$HERE/qa.py" "$dir"; then failed+=("$id (qa)"); fi
done
if [ ${#failed[@]} -gt 0 ]; then
  printf 'failed: %s\n' "${failed[@]}"
  exit 1
fi

#!/usr/bin/env bash
# Makes videos again after a change of wording only, without a new capture: the storyboard's words go over the screens
# captured here before (retell.mjs, which refuses when the steps themselves changed), then the narration is spoken,
# the video put together and checked as make.sh does. Needs no training server.
#
#   training/pipeline/retell.sh w4-pathologist w5-dietitian ...     (ids made on this machine: training/out/<id>/)
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
OUT="${HC360_TRAINING_OUT:-$ROOT/training/out}"
PY="${HC360_PY:-$ROOT/training/.venv/bin/python}"

[ $# -gt 0 ] || { sed -n '2,6p' "$0"; exit 2; }
failed=()
for id in "$@"; do
  echo "== $id"
  dir="$OUT/$id"
  if [ ! -f "$dir/capture.json" ] || [ -f "$dir/failure.json" ]; then
    failed+=("$id (no capture here: make.sh)"); continue
  fi
  if ! node "$HERE/retell.mjs" "$ROOT/training/storyboards/$id.mjs" "$dir"; then
    failed+=("$id (steps changed: make.sh)"); continue
  fi
  "$PY" -I "$HERE/narrate.py" "$dir/segments.json" "$dir/audio"
  "$PY" -I "$HERE/compose.py" "$dir"
  if ! "$PY" -I "$HERE/qa.py" "$dir"; then failed+=("$id (qa)"); fi
done
if [ ${#failed[@]} -gt 0 ]; then
  printf 'failed: %s\n' "${failed[@]}"
  exit 1
fi

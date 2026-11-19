#!/usr/bin/env bash
# Checks a storyboard against the training server without making its video: restores the morning, runs the
# storyboard as its role and lists the screenshots. For authors: run it until it passes, then make.sh makes the video.
#
#   training/pipeline/check.sh <storyboard id>          (training/storyboards/<id>.mjs; output in training/out/<id>/)
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
ENV_FILE="${HC360_TRAINING_ENV:-$HOME/.hc360-training.env}"
# shellcheck disable=SC1090
[ -f "$ENV_FILE" ] && source "$ENV_FILE"
export HC360_DEMO_PASSWORD HC360_DEMO_DB
export NODE_EXTRA_CA_CERTS="${NODE_EXTRA_CA_CERTS:-${HC360_DATA_DIR:-/var/tmp/hc360-data}/certs/hc360-hospital-ca.crt}"
id="$1"
dir="${HC360_TRAINING_OUT:-$ROOT/training/out}/$id"
rm -rf "$dir" && mkdir -p "$dir"
status=0
(flock 9 && "$HERE/server.sh" restore "${HC360_SNAPSHOT:-morning}" >/dev/null && node "$HERE/capture.mjs" "$ROOT/training/storyboards/$id.mjs" "$dir") \
  9>"${HC360_LOCK:-/var/tmp/hc360-server.lock}" || status=$?
if [ -f "$dir/failure.json" ]; then
  echo "failure: $dir/failure.json and $dir/failure.png"
fi
ls "$dir/shots" 2>/dev/null | sed "s#^#$dir/shots/#" | tail -50
exit $status

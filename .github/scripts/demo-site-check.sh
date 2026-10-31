#!/usr/bin/env bash
# Checks that the live demo site is the committed one (used by .github/workflows/demo-site.yml):
#   - each file of the site folder, and the page at /, is served byte for byte as committed;
#   - each header the _headers file sets for /* is on the page and on an asset, with its value;
#   - Cloudflare serves it (its cf-ray header), unless CLOUDFLARE=optional.
# Usage: demo-site-check.sh <address> <site folder> <_headers file>
# Writes a Markdown report to REPORT (default: standard output) and a workflow error for each difference; exits 1 if
# there is one. It waits up to WAIT seconds for the site to answer at all. A file that differs is fetched again
# (RETRIES times, DELAY seconds apart) while a new deploy reaches every server.
set -euo pipefail

base=${1%/}
site=$2
headers=$3
report=${REPORT:-/dev/stdout}
retries=${RETRIES:-12}
delay=${DELAY:-10}
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
failed=0

fail() {
  echo "::error::$1"
  failed=1
}

echo "## $base" >> "$report"

# A Custom Domain made by this deploy answers once its certificate is in place: wait for it (WAIT seconds, 15 apart).
wait=${WAIT:-600}
for ((waited = 0; ; waited += 15)); do
  if curl --silent --fail --output /dev/null "$base/"; then
    break
  fi
  if [ "$waited" -ge "$wait" ]; then
    fail "$base/ did not answer within $wait seconds."
    echo "- \`/\`: **no answer within $wait seconds**" >> "$report"
    exit 1
  fi
  sleep 15
done

mapfile -t files < <(cd "$site" && find . -type f | sed 's|^\./||' | sort)
for file in "" "${files[@]}"; do
  want=$(sha256sum < "$site/${file:-index.html}" | cut -d' ' -f1)
  got=""
  for ((try = 1; try <= retries; try++)); do
    if curl --silent --fail --location --output "$work/body" "$base/$file"; then
      got=$(sha256sum < "$work/body" | cut -d' ' -f1)
    fi
    if [ "$got" = "$want" ]; then
      break
    fi
    if [ "$try" -lt "$retries" ]; then
      sleep "$delay"
    fi
  done
  if [ "$got" = "$want" ]; then
    echo "- \`/$file\`: as committed" >> "$report"
  else
    fail "$base/$file is not the committed ${file:-index.html}."
    echo "- \`/$file\`: **not as committed**" >> "$report"
  fi
done

# The headers of the /* block, as "Name: value" lines.
mapfile -t expected < <(awk '
  /^[[:space:]]*(#|$)/ { next }
  /^\/\*[[:space:]]*$/ { block = 1; next }
  /^[^[:space:]]/ { block = 0 }
  block && /:/ { sub(/^[[:space:]]+/, ""); sub(/[[:space:]]+$/, ""); print }
' "$headers")
if [ "${#expected[@]}" -eq 0 ]; then
  fail "$headers sets no header for /*."
fi

# A header of the last response fetched: curl writes one block per redirect it follows, so the last one counts.
header() {
  grep -i "^$1:" "$work/head.txt" | tail -n 1 | cut -d: -f2- | sed 's/^[[:space:]]*//' || true
}

# The page, and the first asset: the /* rule covers both.
asset=$(printf '%s\n' "${files[@]}" | grep -m1 '^assets/' || true)
for path in "" "$asset"; do
  if ! curl --silent --fail --location --dump-header "$work/head" --output /dev/null "$base/$path"; then
    fail "$base/$path did not answer."
    continue
  fi
  tr -d '\r' < "$work/head" > "$work/head.txt"
  for line in "${expected[@]}"; do
    name=${line%%:*}
    value=${line#*:}
    value=${value#"${value%%[![:space:]]*}"}
    got=$(header "$name")
    if [ "$got" = "$value" ]; then
      echo "- \`/$path\` sends \`$name: $value\`" >> "$report"
    else
      fail "$base/$path sends '$name: $got', not '$value'."
      echo "- \`/$path\`: **\`$name\` is '$got', not '$value'**" >> "$report"
    fi
  done
  ray=$(header cf-ray)
  if [ -n "$ray" ]; then
    echo "- \`/$path\` is served by Cloudflare (cf-ray $ray)" >> "$report"
  elif [ "${CLOUDFLARE:-required}" = required ]; then
    fail "$base/$path is not served by Cloudflare: it has no cf-ray header."
  fi
done

# The certificate the site answers with: its name, who issued it, and when it expires.
if [[ $base == https://* ]]; then
  curl --silent --verbose --output /dev/null "$base/" 2>&1 \
    | sed -n 's/^\* *\(subject\|issuer\|expire date\): */- \1: /p' >> "$report" || true
fi

exit "$failed"

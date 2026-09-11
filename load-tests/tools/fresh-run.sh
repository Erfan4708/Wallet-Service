#!/usr/bin/env bash
# One measured repetition on a fresh stack.
#
#   load-tests/tools/fresh-run.sh <label> <image> [mode] [duration]
#
#   label     results prefix, e.g. d1
#   image     API image to run, e.g. ledger-api:candidate
#   mode      matrix mode (default: key)
#   duration  per-run duration (default: 45s)
#
# LEDGER_DB_MAX_POOL_SIZE, if set, passes through to Compose.
#
# Every repetition starts from an empty database. Throughput measurably falls as
# the tables accumulate history, so a repetition that inherited the previous one's
# data would be comparing different databases, not different code.
#
# On Windows hosts the script also refuses to start with less than 1.5 GB of free
# memory. Docker Desktop's VM keeps file pages cached long after the containers
# stop needing them, and a host that runs out of memory mid-run ends the run; if
# memory is short, the VM's page cache is dropped first. That evicts cached file
# pages only, changes no setting, and persists nothing.
set -euo pipefail

label="${1:?label required}"
image="${2:?image required}"
mode="${3:-key}"
duration="${4:-45s}"

here="$(cd "$(dirname "$0")/.." && pwd)"
root="$(cd "$here/.." && pwd)"
cd "$root"

stamp() {
  echo "$(date -u +%H:%M:%S) $*"
}

free_mb() {
  powershell.exe -NoProfile -Command \
    "[math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory/1024)" | tr -d '\r'
}

if command -v powershell.exe > /dev/null 2>&1; then
  free="$(free_mb)"
  stamp "host free memory ${free} MB"

  if [ "${free:-0}" -lt 1500 ]; then
    docker run --rm --privileged --entrypoint sh postgres:16-alpine \
      -c "sync; echo 3 > /proc/sys/vm/drop_caches"
    sleep 5
    free="$(free_mb)"
    stamp "after dropping the VM page cache: ${free} MB"

    if [ "${free:-0}" -lt 1500 ]; then
      stamp "not starting ${label}: less than 1500 MB free"
      exit 3
    fi
  fi
fi

docker image inspect "$image" > /dev/null

docker compose down -v > /dev/null 2>&1
LEDGER_IMAGE="$image" docker compose up -d > /dev/null 2>&1

started=$(date +%s)
until [ "$(docker inspect ledger-api --format '{{.State.Health.Status}}' 2>/dev/null)" = "healthy" ]; do
  if [ $(( $(date +%s) - started )) -gt 240 ]; then
    stamp "the API did not become healthy within 240 s"
    exit 5
  fi
  sleep 3
done

# The connection string carries the database password, so only the pool setting
# is reported, never the string itself.
stamp "fresh stack on $(docker inspect ledger-api --format '{{.Config.Image}}'), pool maximum ${LEDGER_DB_MAX_POOL_SIZE:-100}"

stamp "${label} start"
status=0
"$here/tools/matrix.sh" "$label" "$mode" "$duration" > "$here/results/${label}-matrix.log" 2>&1 || status=$?
stamp "${label} exit=${status}"

python "$here/tools/aggregate.py" "$label"

exit "$status"

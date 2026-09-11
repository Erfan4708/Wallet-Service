#!/usr/bin/env bash
# Runs one k6 scenario inside the Compose network.
#
#   load-tests/run.sh <scenario> [KEY=VALUE ...]
#   load-tests/run.sh deposit VUS=50 DURATION=60s RUN_NAME=deposit-50
#
# k6 runs in a container on the same network as the API and talks to api:8080
# directly. That keeps host networking and Windows port reservations out of the
# measurement, and needs nothing installed but Docker.
set -euo pipefail

if [ $# -lt 1 ]; then
  echo "usage: $0 <scenario> [KEY=VALUE ...]" >&2
  exit 2
fi

scenario="$1"
shift

# Git Bash on Windows rewrites anything that looks like a path, including the
# container paths below; this stops it. A no-op everywhere else.
export MSYS_NO_PATHCONV=1

# Docker Desktop on Windows wants D:/... rather than /d/...; `pwd -W` provides it
# under Git Bash and fails harmlessly elsewhere.
here="$(cd "$(dirname "$0")" && (pwd -W 2>/dev/null || pwd))"
mkdir -p "$here/results"

env_args=(-e "RUN_NAME=${scenario}")
for pair in "$@"; do
  env_args+=(-e "$pair")
done

exec docker run --rm \
  --network "${LEDGER_NETWORK:-ledger_ledger}" \
  -v "$here:/load-tests:ro" \
  -v "$here/results:/results" \
  "${env_args[@]}" \
  grafana/k6:0.54.0 run --quiet "/load-tests/scenarios/${scenario}.js"

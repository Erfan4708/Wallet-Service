#!/usr/bin/env bash
# Runs the benchmark matrix recorded in docs/PERFORMANCE.md.
#
#   load-tests/tools/matrix.sh <label> full|key [duration]
#
#   full  every scenario at every concurrency level (the staged profile)
#   key   the four comparison points, for repeated runs and before/after checks
#
# Each run gets a resource/lock sampler that starts after setup has finished and
# stops before the run does, so its averages describe the measured window only.
# Results land in load-tests/results/<label>-<run>.{json,csv} and
# <label>-<run>-samples.csv.
set -euo pipefail

label="${1:?label required}"
mode="${2:?full or key required}"
duration="${3:-45s}"
window="${duration%s}"

here="$(cd "$(dirname "$0")/.." && pwd)"

# Database-wide counters, taken before and after each run: WAL flushes and the
# time they took, commits, rollbacks and deadlocks. The difference between the two
# rows is what the run cost the database. A snapshot that cannot connect is
# recorded as blanks.
pgstats() {
  docker exec ledger-postgres psql -U ledger -d ledger -tA -F, -c "
    SELECT extract(epoch FROM now())::bigint, w.wal_sync, round(w.wal_sync_time::numeric, 1),
           d.xact_commit, d.xact_rollback, d.deadlocks
      FROM pg_stat_wal w, pg_stat_database d
     WHERE d.datname = 'ledger';" 2>/dev/null || echo ",,,,,"
}

run() {
  local name="$1" scenario="$2" vus="$3"
  shift 3

  local stats="$here/results/${label}-${name}-pg.csv"
  echo "epoch,wal_sync,wal_sync_time_ms,xact_commit,xact_rollback,deadlocks" > "$stats"
  pgstats >> "$stats"

  ( sleep 5 && "$here/tools/sample.sh" "${label}-${name}" "$(( window - 10 ))" 2 > /dev/null ) &
  local sampler=$!

  "$here/run.sh" "$scenario" \
    VUS="$vus" DURATION="$duration" THRESHOLDS=off \
    RUN_NAME="${label}-${name}" "$@" || true

  wait "$sampler" || true
  pgstats >> "$stats"
}

# Warm-up: JIT, connection pools, caches. Measured like any other run and then
# discarded, so the first real run is not paying start-up costs.
run warmup mixed 25 CURRENCIES=USD,EUR WALLETS=200

if [ "$mode" = "full" ]; then
  for vus in 10 50 100; do
    run "read-${vus}" read "$vus" CURRENCIES=USD,EUR WALLETS=200
  done

  for vus in 1 10 25 50 100 200; do
    run "deposit-usd-${vus}" deposit "$vus" CURRENCIES=USD WALLETS=200
  done

  for vus in 50 100; do
    run "deposit-usd-eur-${vus}" deposit "$vus" CURRENCIES=USD,EUR WALLETS=200
  done

  run withdraw-usd-50 withdraw 50 CURRENCIES=USD WALLETS=200

  for vus in 10 50 100 200; do
    run "transfer-usd-${vus}" transfer "$vus" CURRENCIES=USD WALLETS=200
  done

  for vus in 25 50 100; do
    run "mixed-${vus}" mixed "$vus" CURRENCIES=USD,EUR WALLETS=200
  done

  run contention-pair-50 contention 50 CURRENCIES=USD HOT_ACCOUNTS=2 PATTERN=mesh
  run contention-mesh4-50 contention 50 CURRENCIES=USD HOT_ACCOUNTS=4 PATTERN=mesh
  run contention-fanout10-50 contention 50 CURRENCIES=USD HOT_ACCOUNTS=10 PATTERN=fanout
fi

if [ "$mode" = "key" ]; then
  # Below saturation, saturated, and past the connection limit.
  run deposit-usd-10 deposit 10 CURRENCIES=USD WALLETS=200
  run deposit-usd-50 deposit 50 CURRENCIES=USD WALLETS=200
  run deposit-usd-200 deposit 200 CURRENCIES=USD WALLETS=200
  run transfer-usd-50 transfer 50 CURRENCIES=USD WALLETS=200
  run mixed-50 mixed 50 CURRENCIES=USD,EUR WALLETS=200
  run contention-pair-50 contention 50 CURRENCIES=USD HOT_ACCOUNTS=2 PATTERN=mesh
fi

# The settlement-row experiment. Same concurrency, one currency or two, alternated
# so that anything drifting over the run affects both sides equally. If
# throughput scales with the number of currencies, the per-currency settlement
# account is the constraint.
if [ "$mode" = "hotrow" ]; then
  for round in 1 2; do
    run "deposit-usd-50-r${round}" deposit 50 CURRENCIES=USD WALLETS=200
    run "deposit-usd-eur-50-r${round}" deposit 50 CURRENCIES=USD,EUR WALLETS=200
  done
fi

echo "matrix ${label} (${mode}) complete"

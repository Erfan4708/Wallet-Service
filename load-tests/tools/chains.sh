#!/usr/bin/env bash
# Samples lock-wait chains while a load test runs.
#
#   load-tests/tools/chains.sh <name> <seconds> [interval]
#
# Writes load-tests/results/<name>-chains.csv with one row per interval:
#   waiting      backends blocked by another backend
#   chained      waiting backends whose blocker is itself waiting: a queue
#                behind a queue
#   oldest_ms    how long the longest-waiting statement has been waiting
#
# A single hot row produces many waiting backends and no chains: everyone waits
# on the one holder. Chains appear when a transaction holds one lock while it
# waits for another, so that whoever wants the first lock queues behind a
# transaction that is itself queued.
#
# pg_blocking_pids takes the lock manager's partition locks, so this sampler is
# not free. Use it for a diagnostic run, not alongside a measured one.
set -euo pipefail

name="$1"
seconds="$2"
interval="${3:-1}"

here="$(cd "$(dirname "$0")/.." && pwd)"
out="$here/results/${name}-chains.csv"
mkdir -p "$here/results"

echo "epoch,waiting,chained,oldest_ms" > "$out"

end=$(( $(date +%s) + seconds ))
while [ "$(date +%s)" -lt "$end" ]; do
  row=$(docker exec ledger-postgres psql -U ledger -d ledger -tA -F, -c "
    WITH waiting AS (
      SELECT pid, pg_blocking_pids(pid) AS blockers, now() - query_start AS waited
        FROM pg_stat_activity
       WHERE datname = 'ledger' AND wait_event_type = 'Lock'
    )
    SELECT count(*),
           count(*) FILTER (WHERE EXISTS (
             SELECT 1 FROM waiting AS blocker WHERE blocker.pid = ANY (waiting.blockers))),
           coalesce(round(extract(epoch FROM max(waited)) * 1000), 0)
      FROM waiting;" 2>/dev/null) || row=",,"

  echo "$(date +%s),${row}" >> "$out"
  sleep "$interval"
done

echo "wrote $out"

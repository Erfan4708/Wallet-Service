#!/usr/bin/env bash
# Samples resource usage and database contention while a load test runs.
#
#   load-tests/tools/sample.sh <name> <seconds> [interval]
#
# Writes load-tests/results/<name>-samples.csv with one row per interval:
#   epoch, api cpu%, api memory, postgres cpu%, postgres memory,
#   active backends, backends waiting on a lock, idle-in-transaction backends,
#   pending outbox messages
#
# Run it alongside run.sh in a second terminal. Sampling is external on purpose:
# nothing is added to the application to make it measurable.
set -euo pipefail

name="$1"
seconds="$2"
interval="${3:-2}"

here="$(cd "$(dirname "$0")/.." && pwd)"
out="$here/results/${name}-samples.csv"
mkdir -p "$here/results"

echo "epoch,api_cpu,api_mem,pg_cpu,pg_mem,active,lock_waiting,idle_in_tx,outbox_pending" > "$out"

end=$(( $(date +%s) + seconds ))
while [ "$(date +%s)" -lt "$end" ]; do
  stats=$(docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}}' ledger-api ledger-postgres)
  api=$(echo "$stats" | awk '$1=="ledger-api"{print $2","$3}')
  pg=$(echo "$stats" | awk '$1=="ledger-postgres"{print $2","$3}')

  # A sample that cannot connect is recorded as blanks rather than ending the
  # sampler: when every connection slot is taken, that gap is itself the finding.
  db=$(docker exec ledger-postgres psql -U ledger -d ledger -tA -F, -c "
    SELECT
      count(*) FILTER (WHERE state = 'active' AND backend_type = 'client backend'),
      count(*) FILTER (WHERE wait_event_type = 'Lock'),
      count(*) FILTER (WHERE state = 'idle in transaction'),
      (SELECT count(*) FROM outbox_messages WHERE published_at IS NULL)
    FROM pg_stat_activity WHERE datname = 'ledger';" 2>/dev/null) || db=",,,"

  echo "$(date +%s),${api},${pg},${db}" >> "$out"
  sleep "$interval"
done

echo "wrote $out"

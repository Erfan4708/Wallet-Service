#!/usr/bin/env bash
# The broker goes away under load, then comes back.
#
#   load-tests/tools/outbox-outage.sh <label> [vus] [duration]
#
# Sustained deposits (every one writes an outbox message). One third of the way
# in, RabbitMQ is stopped; two thirds of the way in, it is started again. The
# claims being tested:
#
#   - deposits keep succeeding while the broker is down: a broker outage is not a
#     financial outage
#   - the backlog is durable and grows while the broker is away
#   - after recovery the backlog drains to zero without losing a message
#
# Prints the timeline, the drain time, and the final outbox state. Run
# load-tests/sql/verify-invariants.sql afterwards to confirm every transaction
# still has exactly one outbox message.
set -euo pipefail

label="${1:?label required}"
vus="${2:-50}"
duration="${3:-90s}"
window="${duration%s}"

here="$(cd "$(dirname "$0")/.." && pwd)"

query() {
  docker exec ledger-postgres psql -U ledger -d ledger -tA -c "$1"
}

pending() {
  query "SELECT count(*) FROM outbox_messages WHERE published_at IS NULL"
}

stamp() {
  echo "$(date -u +%H:%M:%S) $*"
}

stamp "pending before load: $(pending)"

( sleep 5 && "$here/tools/sample.sh" "$label" "$(( window + 180 ))" 2 > /dev/null ) &
sampler=$!

"$here/run.sh" deposit \
  VUS="$vus" DURATION="$duration" CURRENCIES=USD,EUR WALLETS=200 \
  THRESHOLDS=off RUN_NAME="$label" &
load=$!

sleep $(( window / 3 ))
stamp "stopping rabbitmq (pending $(pending))"
docker stop ledger-rabbitmq > /dev/null

sleep $(( window / 3 ))
stamp "starting rabbitmq (pending $(pending))"
docker start ledger-rabbitmq > /dev/null

wait "$load" || true
stamp "load finished (pending $(pending))"

started=$(date +%s)
while [ "$(pending)" != "0" ]; do
  if [ $(( $(date +%s) - started )) -gt 600 ]; then
    stamp "backlog did not drain within 600 s"
    break
  fi
  sleep 2
done
stamp "drained $(( $(date +%s) - started )) s after load finished (pending $(pending))"

wait "$sampler" || true

query "SELECT 'total=' || count(*)
           || ' pending=' || count(*) FILTER (WHERE published_at IS NULL)
           || ' retried=' || count(*) FILTER (WHERE attempts > 1)
           || ' max_attempts=' || coalesce(max(attempts), 0)
         FROM outbox_messages"

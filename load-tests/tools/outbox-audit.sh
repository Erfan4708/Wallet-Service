#!/usr/bin/env bash
# Proves, end to end, that no confirmed outbox message was lost by the broker.
#
#   load-tests/tools/outbox-audit.sh start <label>   bind an audit queue, remember the published count
#   load-tests/tools/outbox-audit.sh check <label>   compare what reached the queue with what was published
#   load-tests/tools/outbox-audit.sh stop            remove the audit queue
#
# The service publishes to the `ledger.events` topic exchange and binds no queue of
# its own; in this environment nothing consumes the events, so a confirmed message
# is routed nowhere and discarded. Confirmation alone therefore proves the broker
# accepted a message, not that anything could have received it. An audit queue
# bound with `#` receives a copy of every event: durable, and the messages are
# persistent, so they survive the broker being stopped and started mid-run.
#
# At-least-once delivery means the queue may hold MORE messages than the database
# marked published — a message republished after its lease expired is a
# duplicate, not an error. It must never hold FEWER: that would be a lost message.
#
# Broker credentials default to the Compose development defaults.
set -euo pipefail

command="${1:?start, check or stop required}"
label="${2:-audit}"

here="$(cd "$(dirname "$0")/.." && pwd)"
state="$here/results/${label}-audit-start.txt"
queue="ledger-audit"
user="${RABBITMQ_USER:-ledger}"
password="${RABBITMQ_PASSWORD:-ledger}"

admin() {
  docker exec ledger-rabbitmq rabbitmqadmin -u "$user" -p "$password" -q "$@"
}

published() {
  docker exec ledger-postgres psql -U ledger -d ledger -tA -c \
    "SELECT count(*) FROM outbox_messages WHERE published_at IS NOT NULL"
}

pending() {
  docker exec ledger-postgres psql -U ledger -d ledger -tA -c \
    "SELECT count(*) FROM outbox_messages WHERE published_at IS NULL"
}

depth() {
  # rabbitmqctl reads the queue itself; the management API's counts lag by a
  # statistics interval.
  docker exec ledger-rabbitmq rabbitmqctl -q list_queues name messages |
    awk -v q="$queue" '$1 == q { print $2 }'
}

case "$command" in
  start)
    admin declare queue name="$queue" durable=true > /dev/null
    admin declare binding source=ledger.events destination="$queue" routing_key='#' > /dev/null
    admin purge queue name="$queue" > /dev/null
    [ "$(pending)" = "0" ] || { echo "the outbox backlog must be empty before an audit starts" >&2; exit 1; }
    published > "$state"
    echo "audit queue bound and empty; published so far: $(cat "$state")"
    ;;

  check)
    [ -f "$state" ] || { echo "no audit started for label ${label}" >&2; exit 1; }
    [ "$(pending)" = "0" ] || { echo "the outbox backlog is not empty yet; check after it drains" >&2; exit 1; }

    before=$(cat "$state")
    after=$(published)
    delta=$(( after - before ))
    received=$(depth)

    echo "published during the audit: ${delta}"
    echo "delivered to the audit queue: ${received}"

    if [ "${received:-0}" -lt "$delta" ]; then
      echo "LOST: $(( delta - received )) confirmed message(s) never reached the queue"
      exit 2
    fi

    echo "lost: 0   duplicates (at-least-once redeliveries): $(( received - delta ))"
    ;;

  stop)
    admin delete queue name="$queue" > /dev/null
    echo "audit queue removed"
    ;;

  *)
    echo "unknown command ${command}" >&2
    exit 1
    ;;
esac

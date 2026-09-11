"""Summarises a benchmark matrix into one table.

    python load-tests/tools/aggregate.py <label> [<label> ...]

Joins each run's k6 CSV with its resource sampler CSV and prints a Markdown
table: throughput, latency percentiles, outcomes, and the average CPU and
lock-wait counts observed while the run was under load.

Analysis only. Nothing here is needed to run the benchmarks themselves.
"""

import csv
import glob
import os
import statistics
import sys

RESULTS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "results")


def percent(value):
    return float(value.strip().rstrip("%") or 0)


def samples(run):
    path = os.path.join(RESULTS, f"{run}-samples.csv")
    if not os.path.exists(path):
        return {}

    with open(path, newline="") as handle:
        rows = list(csv.DictReader(handle))

    if not rows:
        return {}

    def mean(column, convert=float):
        values = [convert(row[column]) for row in rows if row.get(column) not in (None, "")]
        return statistics.mean(values) if values else None

    def peak(column):
        values = [int(row[column]) for row in rows if row.get(column) not in (None, "")]
        return max(values) if values else None

    return {
        "api_cpu": mean("api_cpu", percent),
        "pg_cpu": mean("pg_cpu", percent),
        "lock_waiting_avg": mean("lock_waiting"),
        "lock_waiting_max": peak("lock_waiting"),
        "active_avg": mean("active"),
        "outbox_pending_max": peak("outbox_pending"),
    }


def database(run):
    """What the run cost the database, from the counters matrix.sh takes before and after it."""
    path = os.path.join(RESULTS, f"{run}-pg.csv")
    if not os.path.exists(path):
        return {}

    with open(path, newline="") as handle:
        rows = [row for row in csv.DictReader(handle) if row.get("wal_sync")]

    if len(rows) != 2:
        return {}

    before, after = rows

    def delta(column):
        return float(after[column]) - float(before[column])

    syncs = delta("wal_sync")

    return {
        "fsync_ms": delta("wal_sync_time_ms") / syncs if syncs > 0 else None,
        "deadlocks": int(delta("deadlocks")),
    }


def fmt(value, digits=1):
    return "-" if value is None else f"{value:.{digits}f}"


def main(labels):
    print("| run | VUs | req/s | ok/s | p50 ms | p95 ms | p99 ms | refused | unexpected | API CPU % | PG CPU % | lock waits avg/max | WAL flush ms | deadlocks |")
    print("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|")

    for label in labels:
        for path in sorted(glob.glob(os.path.join(RESULTS, f"{label}-*.csv"))):
            if path.endswith("-samples.csv") or path.endswith("-pg.csv"):
                continue

            with open(path, newline="") as handle:
                row = next(csv.DictReader(handle))

            extra = samples(row["run"])
            db = database(row["run"])
            print(
                f"| {row['run']} | {row['vus']} | {row['rps']} | {row['success_per_s']} | "
                f"{row['p50_ms']} | {row['p95_ms']} | {row['p99_ms']} | {row['refused']} | {row['unexpected']} | "
                f"{fmt(extra.get('api_cpu'))} | {fmt(extra.get('pg_cpu'))} | "
                f"{fmt(extra.get('lock_waiting_avg'))}/{fmt(extra.get('lock_waiting_max'), 0)} | "
                f"{fmt(db.get('fsync_ms'), 3)} | {db.get('deadlocks', '-')} |"
            )


if __name__ == "__main__":
    main(sys.argv[1:] or ["baseline"])

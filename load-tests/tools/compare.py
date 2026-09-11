"""Compares repeated benchmark sets.

    python load-tests/tools/compare.py pre1,pre2,pre3 [lock1,lock2,lock3]

Each argument is a comma-separated list of matrix labels run in the same mode.
For every run in a set it prints the mean across repetitions of throughput and
the latency percentiles, together with the lowest and highest value observed.
Given a second set it also prints the change in the mean, and whether the two
ranges overlap.

The range is the point. A difference between two means only says something if it
is larger than the variation between repetitions of the same code; when the
ranges overlap, the honest reading is "no measurable change".

Analysis only. Nothing here is needed to run the benchmarks themselves.
"""

import csv
import glob
import os
import statistics
import sys

RESULTS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "results")

METRICS = [
    ("rps", "req/s"),
    ("p50_ms", "p50 ms"),
    ("p95_ms", "p95 ms"),
    ("p99_ms", "p99 ms"),
]


def load(labels):
    runs = {}

    for label in labels:
        for path in glob.glob(os.path.join(RESULTS, f"{label}-*.csv")):
            if path.endswith("-samples.csv") or path.endswith("-pg.csv"):
                continue

            with open(path, newline="") as handle:
                row = next(csv.DictReader(handle))

            name = row["run"][len(label) + 1:]
            if name == "warmup":
                continue

            runs.setdefault(name, []).append(row)

    return runs


def summary(rows, column):
    values = [float(row[column]) for row in rows]
    return statistics.mean(values), min(values), max(values), len(values)


def cell(stat):
    mean, low, high, count = stat
    return f"{mean:,.1f} ({low:,.1f}-{high:,.1f}, n={count})"


def main(arguments):
    if not arguments:
        print(__doc__)
        return 1

    before = load(arguments[0].split(","))
    after = load(arguments[1].split(",")) if len(arguments) > 1 else None

    if after is None:
        print("| run | metric | mean (min-max) |")
        print("|---|---|---:|")
    else:
        print("| run | metric | before: mean (min-max) | after: mean (min-max) | change | ranges overlap |")
        print("|---|---|---:|---:|---:|---|")

    for name in sorted(before):
        for column, title in METRICS:
            first = summary(before[name], column)

            if after is None:
                print(f"| {name} | {title} | {cell(first)} |")
                continue

            if name not in after:
                continue

            second = summary(after[name], column)
            change = (second[0] - first[0]) / first[0] * 100 if first[0] else 0.0
            overlap = "yes" if first[1] <= second[2] and second[1] <= first[2] else "no"

            print(f"| {name} | {title} | {cell(first)} | {cell(second)} | {change:+.1f} % | {overlap} |")

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

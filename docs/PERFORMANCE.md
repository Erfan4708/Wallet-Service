# Performance

How the ledger behaves under load, where it stops scaling, and why. Every number
in this document was measured on the environment described in section 1, with
the scripts under `load-tests/`. Nothing is estimated or extrapolated; where a
question could not be answered by measurement, the document says so.

## Summary

All figures are from one laptop running everything at once (section 1); they
compare well against each other and say little about dedicated hardware.

- **Throughput and latency, final code.** Deposits in one currency: 165 req/s
  from 50 concurrent clients (p99 about 800 ms), 151 req/s from 200. Transfers:
  about 880 req/s from 50 clients (p99 about 170 ms). Mixed traffic: about 790
  req/s from 50 clients. Reads, measured in the baseline and untouched since:
  6,437 req/s from 50 clients (p99 19 ms), 6,656 from 100 (p99 31 ms).
- **Errors.** No run of the service as committed returned an unexpected response.
  The only three in the entire phase came from a change that was rejected. Refusals
  — insufficient funds, a duplicate reversal — matched the database's rollbacks
  exactly (section 9).
- **As concurrency rises.** Reads keep scaling to about 50 clients and then level
  off. Deposits in one currency level off from about 10: extra clients only queue,
  and latency grows with the queue. Transfers level off around 50.
- **The bottleneck is the per-currency settlement account.** Every deposit and
  withdrawal locks that one row. 97.1 % of all statement time in the baseline was
  spent waiting for it; spreading deposits over two currencies — two rows — gave
  1.9 times the throughput (section 7).
- **PostgreSQL is not short of CPU or disk for deposits.** It used about one core
  during deposit runs, and a commit's WAL flush took about 0.7 ms. It was waiting
  on row locks, which is a property of the transaction design. Transfers, which do
  not share a hot row, pushed PostgreSQL to about six cores on this shared
  machine; that ceiling was not investigated further.
- **Row locks are the contention**, and the settlement row is the hot row,
  confirmed directly. **Deadlocks: none**, on any stack, at any concurrency
  (section 9).
- **What helped.** Indexing the pending outbox by the order it is claimed in made
  a claim about a hundred times cheaper and let the publisher keep up (8.2).
  Removing the savepoint EF Core wraps around every save took two round trips out
  of the locked window: deposits +13 % from 50 clients with p99 halved, +28 % from
  200 (8.3). Together, against the original code: deposits +14 % from 50 clients
  and +28 % from 200, mixed traffic +10 %, two-hot-wallet contention +47 % (8.4).
- **What did not.** Locking the settlement row last instead of first cut deposit
  throughput by a fifth and made latency far less fair; reverted (8.1). Capping
  the connection pool changed no throughput and traded one tail for another; not
  adopted (8.5).
- **Correctness held throughout.** Ten ledger invariants showed zero violations
  after every stack, including runs killed mid-flight; idempotency held under
  load; a broker outage under load lost no messages — 16,338 published, 16,339
  delivered, one legitimate duplicate (sections 10 and 11).
- **Still limited by:** the settlement row, a single outbox publisher, an outbox
  that is never pruned, and a connection pool sized like the server (section 12).
- **Measured code.** Everything above was measured on the code committed as
  `50447ef`. Phase 7 later tightened request validation, made the idempotency
  comparison check accounts and direction, and added a read endpoint and OpenAPI.
  None of those adds a database round trip to a money movement, and none was
  re-benchmarked.

## Contents

1. [Test environment](#1-test-environment) · 2. [Software versions](#2-software-versions) ·
3. [Database configuration](#3-database-configuration) ·
4. [API configuration](#4-api-configuration-during-measurement) ·
5. [How the load tests work](#5-how-the-load-tests-work) · 6. [Baseline](#6-baseline) ·
7. [Where the time goes](#7-where-the-time-goes) · 8. [Optimisations tried](#8-optimisations-tried) ·
9. [Concurrency and deadlocks](#9-concurrency-and-deadlocks) ·
10. [Correctness after load](#10-correctness-after-load) ·
11. [The outbox under load](#11-the-outbox-under-load-and-without-a-broker) ·
12. [What is still slow](#12-what-is-still-slow-and-what-was-not-answered) ·
13. [How far these numbers can be trusted](#13-how-far-these-numbers-can-be-trusted) ·
14. [Reproducing the measurements](#14-reproducing-the-measurements)

| Optimisation | Section | Result |
|---|---|---|
| Lock the settlement account last | 8.1 | **Rejected** — deposits −19.9 % at 50 clients, p99 1.4 s → 7.7 s |
| Index pending outbox messages by `id` | 8.2 | **Kept** — claim 110 ms → 1.1 ms |
| Disable EF Core's automatic savepoint | 8.3 | **Kept** — deposits +13.0 % at 50 clients, +28.2 % at 200 |
| Cap the connection pool at 40 | 8.5 | **Not adopted** — no throughput change, deposit p99 doubled at 50 clients |

## 1. Test environment

Everything — k6, the API, PostgreSQL, RabbitMQ, Redis, Prometheus and Grafana —
runs on **one laptop**, inside Docker Desktop's WSL2 VM. The load generator
competes with the system under test for the same CPUs. The numbers are therefore
**relative**: they are good for comparing scenarios, concurrency levels and
before/after changes on this machine, and they say nothing about what the service
would do on dedicated hardware.

| | |
|---|---|
| Host | Intel Core i7-10750H (6 cores / 12 threads), 15.9 GB RAM |
| Host OS | Windows 11 Pro 10.0.26200 |
| Docker | Docker Desktop 29.7.2, WSL2 kernel 6.18.33.2 |
| Docker VM | 12 CPUs, 7.7 GiB |
| Container limits | none (all services share the VM) |
| Network | k6 runs in a container on the Compose network and calls `api:8080` directly |

## 2. Software versions

| Component | Version |
|---|---|
| Repository | `9b49910` plus the Phase 6 changes, committed afterwards as `50447ef` |
| .NET runtime (container) | ASP.NET Core 8.0.31 |
| .NET SDK (host) | 8.0.200 |
| EF Core / Npgsql | 8.0.11 / 8.0.x |
| PostgreSQL | 16.15 (alpine) |
| RabbitMQ | 3.13.7 |
| Redis | 7.4.11 |
| k6 | 0.54.0 (`grafana/k6:0.54.0`) |
| Prometheus / Grafana | 2.55.1 / 11.3.1 |

## 3. Database configuration

Stock `postgres:16-alpine` defaults, deliberately untuned, plus diagnostics only:

| Setting | Value |
|---|---|
| `max_connections` | 100 (3 reserved for superusers) |
| `shared_buffers` | 160 MB |
| `work_mem` | 4 MB |
| `synchronous_commit` | on |
| `fsync` | on |
| `wal_sync_method` | fdatasync |
| `default_transaction_isolation` | read committed |
| `deadlock_timeout` | 1 s |
| `shared_preload_libraries` | `pg_stat_statements` (diagnostics) |
| `log_lock_waits` | on (diagnostics) |
| `track_io_timing` | on (diagnostics) |
| `track_wal_io_timing` | on (diagnostics; enabled after the baseline matrix, before every before/after run) |

`synchronous_commit` and `fsync` stay on. Turning them off would inflate every
write number and describe a system that can lose committed money.

## 4. API configuration during measurement

Production environment, exactly as Compose runs it:

- request logging on (one JSON event per request to stdout)
- tracing on, sample ratio 1.0, no exporter; metrics on, Prometheus scraping every 15 s
- Npgsql connection pool at its default maximum of 100 connections (except the
  capped runs in 8.5)
- outbox publisher on (batch 50, poll 5 s, lease 60 s), RabbitMQ publisher confirms on

Telemetry stays enabled throughout. A benchmark that switches it off measures a
service nobody runs.

## 5. How the load tests work

### Tool

[k6](https://k6.io/), run from its official container image on the Compose
network. It was chosen because scenarios are plain JavaScript that can be read and
reviewed like code, it reports latency percentiles and custom counters natively,
and it needs nothing installed on the host. NBomber (C#) was the alternative; it
would share the language of the service, but it runs in-process with the .NET
runtime on the same machine, and k6's container isolates the load generator's
runtime from the system under test more cleanly.

### Rules the tests follow

- **Every money movement goes through the public HTTP API.** Test wallets are
  created with `POST /accounts` and funded with real deposits, each carrying an
  idempotency key so that re-running setup never funds a wallet twice. Nothing
  writes to the database directly, and there are no benchmark-only endpoints.
- **Every write carries an idempotency key**, a fresh UUID per request, as a
  well-behaved client's would. The idempotency lookup is therefore part of every
  deposit, withdrawal, transfer and reversal measured here.
- **Test data is deterministic.** Wallet identifiers are derived from currency and
  index (`b1000000-0000-4000-8000-000000000042` is USD wallet 42), so every run
  addresses the same accounts and a run can be reproduced exactly.
- **Setup is excluded from the results.** Latency and throughput are taken from the
  `{scenario:load}` sub-metrics only, and rates are computed over the configured
  duration, so the burst of setup requests never pollutes a percentile.
- **Every response is classified.** `2xx` is success. `409`/`422` are *refusals*
  (insufficient funds, a duplicate reversal) and are expected in scenarios that
  can provoke them. Anything else is *unexpected* and counts as an error.
- **Thresholds are test thresholds, not SLAs.** A threshold in `load-tests/`
  answers "did this run behave like the previous ones?", not "is this fast
  enough for a customer?". No business SLA has been defined for this service.

### Scenarios

| | Script | What each iteration does | What it isolates |
|---|---|---|---|
| A | `read.js` | `GET /accounts/{id}` on a random wallet | read path, no locks |
| B | `deposit.js` | deposit 1.00 into a random wallet | wallet lock **plus the currency's settlement account** |
| C | `transfer.js` | transfer 1.00 between two random distinct wallets | two wallet locks, no system account |
| D | `withdraw.js` | withdraw 1.00 from a random wallet | as B, plus the sufficient-funds decision |
| E | `mixed.js` | 50 % read, 15 % deposit, 12 % withdrawal, 20 % transfer, 2 % idempotent replay, 1 % double reversal | a plausible blend; also checks idempotency and duplicate-reversal protection under load |
| F | `contention.js` | transfers among 2, 4 or 10 hot wallets (`mesh`), or from one source to many (`fanout`) | worst-case row contention |

The mixed scenario fails a run if a replayed request moves money twice or a
transaction is reversed twice (`idempotency_violations`).

### Profiles and measurement discipline

- Constant-VU runs of 45 s, each preceded by setup, with no think time: each VU
  issues its next request as soon as the last one returns, so the load is
  closed-loop and throughput is what the system delivers at that concurrency.
- A **warm-up run** (mixed, 25 VUs) precedes every matrix and is discarded, so the
  first measured run is not paying for JIT compilation and cold pools.
- A **sampler** (`load-tests/tools/sample.sh`) records API and PostgreSQL CPU,
  active backends, backends waiting on locks, and the outbox backlog every 2 s,
  starting after setup and stopping before the run ends.
- Before/after comparisons use the **same key set run three times** on each side.
  In 8.1 each side's three repetitions shared one fresh stack, with the outbox
  drained to zero between them; from the regression check in 8.2 onwards every
  repetition got a fresh stack of its own (`load-tests/tools/fresh-run.sh`), so no
  repetition inherits another's data or publishing.

### What is observed, and what was added to observe it

Some of the questions this phase had to answer could not be answered with the
telemetry Phase 5 left behind, so the missing pieces were added **before** the
baseline was taken. Every measurement in this document, before and after any
change, ran with the same instrumentation.

**Application metrics** (Prometheus, `/metrics`):

| Metric | Answers |
|---|---|
| `ledger_outbox_pending` | how large the unpublished backlog is (refreshed at most once per poll interval) |
| `ledger_outbox_published_total`, `ledger_outbox_publish_failures_total` | publishing throughput and failures |
| `db_client_connections_usage{state}` | connection pool in use vs idle (Npgsql) |
| `db_client_commands_executing` | database commands in flight |
| `db_client_commands_duration_seconds` | database command latency, with buckets in seconds (Npgsql's defaults assume milliseconds and put every command in one bucket) |

Npgsql labels its metrics with the connection string minus the password, which
would publish the host, database and user name to anyone who can read `/metrics`.
A metric view keeps only the `state` label. No metric carries an account,
transaction or wallet identifier.

**Grafana** (`deploy/grafana/dashboards/ledger-service.json`): every query now uses
one-minute rates, so a 45-second run is visible. Two existing panels, the
connection pool and the GC heap, queried metrics the service never exported and
showed nothing; they now query metrics that exist. New panels show p99 latency by
route, the outbox backlog, the outbox publishing and failure rates, and thread-pool
queue length with monitor-lock contention.

**PostgreSQL** (diagnostics only, `docker-compose.yml`): `pg_stat_statements` for
per-statement timing, `track_io_timing` and `track_wal_io_timing` for the cost of
reads and commit flushes, and `log_lock_waits` for any lock wait longer than a
second.

**Outside the application** (`load-tests/tools/`): `sample.sh` records container CPU
and memory, active and lock-waiting backends and the outbox backlog every two
seconds; `matrix.sh` snapshots WAL, commit, rollback and deadlock counters before
and after each run; `chains.sh` samples lock-wait chains for diagnostic runs;
`outbox-audit.sh` binds an audit queue to prove messages reached the broker. None
of this is compiled into the service.

## 6. Baseline

Fresh stack (`docker compose down -v`, `up -d --build`), all services healthy,
migrations applied, then `load-tests/tools/matrix.sh baseline full 45s`
(2026-09-10 22:10:08Z to 22:28:44Z, exit 0). Zero unexpected responses in every
run.

### Reads

| VUs | req/s | p50 ms | p95 ms | p99 ms | API CPU % | PG CPU % |
|---:|---:|---:|---:|---:|---:|---:|
| 10 | 4,751.4 | 1.83 | 3.23 | 4.57 | 435.1 | 181.3 |
| 50 | 6,437.2 | 6.91 | 13.33 | 18.86 | 491.0 | 241.8 |
| 100 | 6,655.5 | 13.98 | 23.39 | 31.27 | 506.4 | 244.1 |

(CPU is per core: 100 % is one core of the 12 available to the VM.)

### Deposits, one currency (USD)

| VUs | req/s | p50 ms | p95 ms | p99 ms | backends waiting on a lock (avg) |
|---:|---:|---:|---:|---:|---:|
| 1 | 125.0 | 7.47 | 9.54 | 11.68 | 0.0 |
| 10 | 152.7 | 62.39 | 82.62 | 95.18 | 9.0 |
| 25 | 154.4 | 156.81 | 191.74 | 219.16 | 22.8 |
| 50 | 147.8 | 291.82 | 552.31 | 1,466.15 | 48.7 |
| 100 | 118.1 | 588.99 | 2,589.20 | 3,932.67 | not sampled ¹ |
| 200 | 99.9 ² | 1,696.67 | 4,389.89 | 6,311.94 | not sampled ¹ |

### Transfers (USD, 200 wallets)

| VUs | req/s | p50 ms | p95 ms | p99 ms |
|---:|---:|---:|---:|---:|
| 10 | 683.0 | 13.37 | 21.39 | 27.34 |
| 50 | 881.9 | 48.16 | 112.56 | 163.35 |
| 100 | 882.7 | 80.41 | 293.93 | 557.53 |
| 200 | 920.9 | 183.57 | 412.33 | 690.90 |

### Other runs

| Run | VUs | req/s | p50 ms | p95 ms | p99 ms | refused |
|---|---:|---:|---:|---:|---:|---:|
| deposit USD + EUR | 50 | 204.9 ² | 198.72 | 670.26 | 882.03 | 0 |
| deposit USD + EUR | 100 | 184.1 ² | 291.31 | 1,962.15 | 3,666.56 | 0 |
| withdraw USD | 50 | 119.8 | 385.61 | 673.60 | 1,225.60 | 0 |
| mixed | 25 | 758.8 | 8.31 | 205.12 | 293.92 | 325 |
| mixed | 50 | 729.2 | 8.25 | 463.80 | 848.51 | 303 |
| mixed | 100 | 603.1 | 8.35 | 974.49 | 2,922.90 | 252 |
| contention, 2 hot wallets | 50 | 96.6 | 373.22 | 887.33 | 3,934.66 | 0 |
| contention, 4 hot wallets | 50 | 168.3 | 49.44 | 1,042.87 | 1,192.25 | 0 |
| contention, fan-out from 1 of 10 | 50 | 117.3 | 297.94 | 694.65 | 3,819.28 | 0 |

¹ From the 100-VU deposit run onward the sampler recorded nothing: its `psql`
connection was refused with `FATAL: sorry, too many clients already`. Section 7
explains why; the sampler now records a blank row instead of stopping.

² **Disturbed runs, not used as evidence.** Between about 22:17:50Z and 22:20:20Z
a CPU-heavy log search ran on the host by mistake, overlapping these three runs.
They are listed for completeness and were re-measured cleanly afterwards.

## 7. Where the time goes

### 7.1 Deposits queue behind one row

Deposit throughput in one currency stops rising at 10 VUs and then falls:

- 125.0 req/s at 1 VU, 152.7 at 10, 154.4 at 25, 147.8 at 50, 118.1 at 100.
- Median latency grows in proportion to the number of VUs — 62 ms at 10, 157 ms
  at 25, 292 ms at 50 — which is what a single queue looks like: every extra
  client waits one more turn.
- The sampler saw on average 9.0, 22.8 and 48.7 backends waiting on a lock at 10,
  25 and 50 VUs. That is every VU but one.

Transfers go through the same handler shape, the same repository, the same
`SaveChanges` and the same outbox write. The one difference is that a transfer
locks two wallets and no system account. On the same machine, transfers reach
881.9 req/s at 50 VUs, roughly six times what deposits manage.

The database's own accounting says the same thing. Over the whole baseline
matrix, `pg_stat_statements` attributes **97.1 % of all statement execution time**
to one statement:

```sql
SELECT 1 FROM accounts WHERE id = $1 FOR UPDATE   -- 599,688 calls, mean 69.96 ms
```

`EXPLAIN ANALYZE` of that statement, uncontended, takes 0.100 ms: an index scan on
the primary key and a `LockRows` node. The other 69.86 ms of the average call is
waiting for another transaction to release the row. The row is the currency's
settlement account, which every deposit and every withdrawal must lock because
every one of them changes its balance.

### 7.2 No deadlocks, no unexpected failures

Across the baseline matrix `pg_stat_database` recorded 2,730,948 commits,
**0 deadlocks**, and 1,189 rollbacks. The rollbacks are exactly the 1,189 refusals
k6 counted in the four mixed runs (309 + 325 + 303 + 252): insufficient funds and
duplicate reversals, rolled back on purpose. No run returned an unexpected status.

### 7.3 The outbox claim gets slower as history grows

The publisher claims work with `… WHERE published_at IS NULL AND next_attempt_at
<= now() ORDER BY id LIMIT 50 FOR UPDATE SKIP LOCKED`. With 293,244 rows in the
table, 30,101 of them pending, the plan was:

```text
Limit (actual time=108.308..108.491 rows=50)
  -> LockRows
     -> Index Scan using "PK_outbox_messages" on outbox_messages
          Filter: ((published_at IS NULL) AND (next_attempt_at <= now()))
          Rows Removed by Filter: 263053
Execution Time: 108.535 ms
```

The partial index that exists for this query, `ix_outbox_messages_pending`, is
keyed on `next_attempt_at`. The query orders by `id`, so the planner walks the
primary key from the oldest message and discards every row already published —
263,053 of them here. Over the baseline the claim ran 5,824 times at a mean of
60.38 ms. Published rows are never pruned, so this cost grows for as long as the
service runs.

### 7.4 One API instance can take every database connection

During the 100- and 200-VU runs, `psql` was refused with `FATAL: sorry, too many
clients already`, and the pool did not give the connections back when the load
stopped: 52 were still open and idle about two minutes after the matrix finished.
Npgsql's default maximum pool size is 100; PostgreSQL's `max_connections` is 100. The application also connects as
the database superuser, so it can occupy the three connections PostgreSQL reserves
for administrators. Nothing failed for clients — k6 recorded no unexpected
responses — but an operator could not have connected to investigate, and a second
API instance could not have connected at all. Section 8.5 measures what capping
the pool would cost.

### 7.5 The settlement row, tested directly

If one settlement account per currency is the constraint, splitting the same
deposits across two currencies should nearly double throughput, because there are
then two rows to queue on instead of one. If the constraint were anything global —
CPU, the connection pool, WAL flushing — it would not. Same image, fresh stack,
50 VUs, runs alternated so that drift affects both sides:

| Run | req/s | p50 ms | p95 ms | p99 ms | WAL flush ms |
|---|---:|---:|---:|---:|---:|
| USD only, round 1 | 149.3 | 283.52 | 566.61 | 1,541.51 | 0.696 |
| USD + EUR, round 1 | 282.1 | 161.51 | 418.37 | 593.23 | 0.709 |
| USD only, round 2 | 146.6 | 295.14 | 557.01 | 1,383.07 | 0.711 |
| USD + EUR, round 2 | 278.7 | 163.85 | 399.26 | 584.34 | 0.715 |

Two settlement rows deliver 1.9 times the throughput of one. The constraint is the
per-currency settlement account.

### 7.6 Not the disk, and what a deposit does while it holds the row

The mean WAL flush during deposit runs was 0.70–0.72 ms. At about 150 deposits a
second the settlement row is released roughly every 6.5 ms, so the flush that
makes each commit durable is around a tenth of the time the row is held. PostgreSQL
is not waiting on storage; it is waiting on the transaction holding the row to
finish talking to the application.

What that conversation consists of was counted with `pg_stat_statements`, reset
before a 20-second, single-VU deposit run over ten wallets (2,548 deposits):

| Round trip | Per deposit |
|---|---:|
| `BEGIN` | 1 |
| look up the currency's settlement account by `system_key` | 1 |
| `SELECT … FOR UPDATE`, then read the account | 2 + 2 |
| idempotency-key lookup | 1 |
| `SAVEPOINT` (created by EF Core around `SaveChanges`) | 1 |
| one batch: transaction, two entries, outbox message, two balance updates | 1 |
| `RELEASE SAVEPOINT` | 1 |
| `COMMIT` (runs the deferred zero-sum check) | 1 |

Eleven round trips. The settlement account's identifier sorts before every wallet,
so under the original lock order it was locked **first**, and eight of the eleven
happened while every other deposit in the currency waited.

## 8. Optimisations tried

Each change below was held to the same procedure: a hypothesis stated before the
change, the key set measured three times on a fresh stack without it, three times
on a fresh stack with it, and the change kept only if the difference is larger
than the variation between repetitions.

### 8.1 Lock the settlement account last — rejected

**Hypothesis.** Section 7.6 shows the settlement row is held for eight round trips
because it is locked first. Locking customer accounts first and system accounts
last would shorten that to six, so the row would be released sooner and deposit
throughput would rise.

**Change.** `AccountRepository.GetForUpdateAsync` locked customer accounts first
and system accounts last, ascending identifier within each group. The group came
from one unlocked read of `account_type` before any lock was taken — safe because
an account's type never changes, so every transaction derives the same order.
It was still a single total order, so it could not introduce deadlocks, and it
shipped with a database-level test proving the new order; that test was run
against the old order and failed, as it should.

**Measurement.** Fresh stack for each side, key set three times each. Mean of the
three repetitions, with the lowest and highest in brackets:

| Run | Metric | Before | After | Change |
|---|---|---:|---:|---:|
| deposit, USD, 10 VUs | req/s | 155.9 (154.4–157.8) | 130.6 (121.4–144.9) | −16.2 % |
| | p50 / p95 / p99 ms | 61.7 / 75.2 / 87.1 | 23.1 / 232.1 / 1,480.0 | |
| deposit, USD, 50 VUs | req/s | 144.6 (143.0–147.3) | 115.8 (112.0–122.1) | −19.9 % |
| | p50 / p95 / p99 ms | 300.5 / 576.8 / 1,425.5 | 26.6 / 3,192.2 / 7,739.7 | |
| deposit, USD, 200 VUs | req/s | 117.8 (117.3–118.7) | 117.5 (114.7–119.8) | no change ¹ |
| | p50 / p95 / p99 ms | 1,484.5 / 3,389.6 / 4,779.0 | 1,012.7 / 4,381.8 / 6,012.0 | |
| transfer, USD, 50 VUs | req/s | 862.2 (848.0–880.8) | 820.4 (816.4–826.5) | −4.8 % |
| | p99 ms | 171.1 | 174.2 | no change ¹ |
| mixed, 50 VUs | req/s | 717.3 (712.5–720.6) | 694.6 (689.9–699.9) | −3.2 % |
| | p99 ms | 839.8 | 1,009.2 | +20.2 % |
| contention, 2 hot wallets, 50 VUs | req/s | 106.3 (104.6–108.7) | 127.0 (123.7–129.0) | +19.4 % |
| | p95 / p99 ms | 856.5 / 4,037.4 | 580.8 / 851.8 | |

¹ The before and after ranges overlap.

**Result: rejected and reverted.** The change made the path it was aimed at worse.
Deposit throughput fell by a fifth and the tail latency grew several-fold, while
the median fell sharply — a lucky majority got faster and an unlucky minority got
very much slower. Transfers, which have no system account and gained nothing but
the extra read, lost about 5 %.

The two-hot-wallet contention run improved, repeatably. Those are transfers only,
where the change added a read before the locks and altered nothing else; this
document does not claim to know why that helped, and it is not a reason to keep a
change that slows every deposit.

**Looking for the cause.** The shape suggested a convoy: a deposit holding its
wallet while it queues for the settlement row, so that any other deposit to the
same wallet queues behind a transaction that is itself queued. If so, spreading
the same load over ten times as many wallets should shrink the tail. Each image
ran 50 VUs of USD deposits for 45 s over 200 and then 2,000 wallets, with lock
waits sampled once a second through the middle 25 s (`tools/chains.sh`). Single
runs; these are diagnostics, not the before/after evidence above.

| Lock order | Wallets | req/s | p50 ms | p95 ms | p99 ms | max ms | longest wait sampled | unexpected |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| settlement first (kept) | 200 | 146.6 | 290.92 | 564.16 | 1,501.66 | 4,229.40 | 3.5 s | 0 |
| settlement first (kept) | 2,000 | 149.7 | 286.46 | 529.99 | 1,524.70 | 3,934.37 | 3.3 s | 0 |
| settlement last (rejected) | 200 | 120.4 | 25.51 | 2,992.13 | 7,429.15 | 14,191.65 | 11.9 s | 0 |
| settlement last (rejected) | 2,000 | 92.1 | 34.41 | 260.08 | 22,838.39 | 44,662.88 | 29.2 s | 3 |

- **The convoy explanation is wrong.** Ten times more wallets made the rejected
  order's tail worse, not better.
- **Counting lock-wait chains cannot tell the orders apart.** Both showed about 46
  of 48 waiting backends queued behind another waiting backend. That is simply how
  PostgreSQL queues many waiters on one row: one waiter holds the row's lock slot
  while it waits for the owner to commit, and the rest wait for that waiter.
- **What differs is fairness.** With the settlement row locked first, no sampled
  wait exceeded 3.5 s. Locked last, the median fell to around 30 ms while some
  requests waited 12 to 29 seconds. Why the lock order changes fairness this much
  was not established here.
- The three unexpected responses are the only ones in every run recorded in this
  document, and they came from the rejected order. Their status was not captured:
  the k6 summary did not yet record statuses (it does now), and the API container
  had been removed before its log was read. With the settlement row locked first,
  the API log for the same diagnostics held no 5xx responses, errors or warnings.

### 8.2 Index the pending outbox by the order it is claimed in

**Hypothesis.** Section 7.3 shows the claim query walking the primary key over the
entire published history, because the partial index that covers pending messages
is keyed on `next_attempt_at` while the claim orders by `id`. Keying the same
partial index on `id` should let PostgreSQL start at the oldest pending message
and read only pending rows, so the cost of a claim should stop growing with
history and the publisher should drain faster.

**Change.** One migration, `IndexPendingOutboxById`: drop
`ix_outbox_messages_pending (next_attempt_at) WHERE published_at IS NULL` and
create `ix_outbox_messages_pending (id) WHERE published_at IS NULL`. The claim
statement, its `FOR UPDATE SKIP LOCKED`, the lease and the at-least-once sequence
are untouched.

**Before.** A fresh stack on the old index, grown with 265,164 transfers, then left
to drain with no load. The publisher emptied a backlog of 190,009 messages in
518 seconds, and got slower the more it had already published:

| Messages already published | Publishing rate over the next 30 s |
|---:|---:|
| 75,355 | 479 msg/s |
| 130,520 | 425 msg/s |
| 178,500 | 377 msg/s |
| 221,648 | 340 msg/s |
| 260,850 | 313 msg/s |

With the backlog empty, one claim still read every row in the table to find
nothing:

```text
Limit (actual time=90.798..90.805 rows=0 loops=1)
  -> LockRows
     -> Index Scan using "PK_outbox_messages" on outbox_messages
          Filter: ((published_at IS NULL) AND (next_attempt_at <= now()))
          Rows Removed by Filter: 265364
          Buffers: shared hit=265691
Execution Time: 90.877 ms
```

An idle publisher polls every five seconds, so an empty outbox of this size costs
a 90 ms walk through every row of the table every five seconds, indefinitely, and
the walk lengthens with every message ever published.

**Before and after, same database.** A broker outage under load, run on the old
index, then the migration applied to that same database, then the identical run
again: deposits across USD and EUR from 50 clients for 90 seconds, RabbitMQ stopped
for the middle 30 of them. The second run started with more history than the
first, so any effect of table size works against the new index.

| | Old index (`next_attempt_at`) | New index (`id`) |
|---|---:|---:|
| Outbox history at start | 265,364 | 290,015 |
| Claim statement, mean (max) | 110.45 ms (219.25) over 524 claims | 1.12 ms (3.66) over 510 claims |
| Claim with an empty backlog (`EXPLAIN ANALYZE`) | 90.9 ms, 265,691 buffers | 0.42 ms, 103 buffers |
| Backlog after 31 s of load, broker still up | 1,720 | 200 |
| Backlog when the broker came back | 10,873 | 9,423 |
| Backlog when the load ended | 15,556 (still growing) | 9,048 (already shrinking) |
| Drained after the load ended ¹ | 57 s | 45 s |
| Messages that needed more than one attempt ² | up to 248 | 246 |
| Deposits during the run | 271.7 req/s, 0 unexpected | 262.3 req/s, 0 unexpected |
| Invariant violations afterwards | 0 | 0 |

¹ Not a throughput figure. Messages claimed while the broker was down must wait for
their lease or back-off to expire before they can be tried again, so the drain time
is dominated by that waiting; per second, the post-load drain was no faster on the
new index.

² The attempt counter is cumulative over the table, so each run's figure is the
difference from the previous reading; the first includes anything that happened on
this database before it.

**Result: kept.** A claim is about a hundred times cheaper and no longer grows with
history. The cleanest comparison is the first 31 seconds of each run, before the
broker was touched: the same deposit load left a backlog of 1,720 messages on the
old index and 200 on the new one. On the old index the publisher could not keep up
with 272 deposits a second; on the new one it could.

Deposit throughput was 3.5 % lower in the second run. That is reported, not
claimed: these are single runs on different table sizes, and a deposit only
maintains this index, exactly as it maintained the old one.

**Nothing else got slower.** A new index is a new write on every insert and every
publish, so the key set was run three more times on fresh stacks with the new
index and compared with the three "before" repetitions on the old one:

| Run | req/s before | req/s after | p99 ms before | p99 ms after |
|---|---:|---:|---:|---:|
| deposit, USD, 10 VUs | 155.9 (154.4–157.8) | 154.5 (152.9–155.7) | 87.1 | 88.5 |
| deposit, USD, 50 VUs | 144.6 (143.0–147.3) | 146.3 (144.0–147.6) | 1,425.5 | 1,515.6 |
| deposit, USD, 200 VUs | 117.8 (117.3–118.7) | 117.7 (116.9–118.9) | 4,779.0 | 4,833.3 |
| transfer, USD, 50 VUs | 862.2 (848.0–880.8) | 872.2 (866.5–878.6) | 171.1 | 167.9 |
| mixed, 50 VUs | 717.3 (712.5–720.6) | 720.8 (713.8–728.7) | 839.8 | 852.2 |
| contention, 2 hot wallets | 106.3 (104.6–108.7) | 108.8 (107.7–109.6) | 4,037.4 | 3,810.7 |

Of the 24 throughput and latency figures compared, 23 differ by less than the
variation between repetitions. The exception is the mixed scenario's p95, 5.8 %
higher (438.4 to 463.8 ms) with ranges that do not overlap, while its p99 does
overlap; it is recorded here as observed, without a claimed cause.

**Operational note.** EF Core applies migrations inside a transaction, so the new
index is built with a plain `CREATE INDEX`, which blocks writes to `outbox_messages`
while it runs. On this table that was quick. On a production outbox with years of
history, build it with `CREATE INDEX CONCURRENTLY` outside the migration first, or
prune published messages before migrating.

### 8.3 Stop EF Core creating a savepoint in every transaction — kept

**Hypothesis.** Section 7.6 counted two statements a deposit makes that the
service never asked for: `SAVEPOINT` and `RELEASE SAVEPOINT`, which EF Core wraps
around `SaveChanges` whenever an explicit transaction is open. A savepoint exists
so that a caller who catches a failed save can roll back to it and carry on in the
same transaction. Nothing here does: the unit of work translates the failure and
rethrows, the telemetry wrapper rethrows, and a failed save always ends the request
with the whole transaction rolled back. Removing the savepoint should take two
round trips out of the eight a deposit makes while the settlement row is locked —
without touching the lock order, and so without the fairness problem in 8.1.

**Change.** One line in `UnitOfWork.ExecuteInTransactionAsync`:
`AutoSavepointsEnabled = false` before the transaction begins. A failed save still
throws and still rolls back everything. The existing integration tests for failed
saves — duplicate accounts, a second reversal, a conflicting idempotency key —
pass unchanged. A new test, `LedgerRoundTripTests`, asserts that a deposit starts
one transaction, commits once and creates no savepoint; run with savepoints turned
back on, it fails.

**Round trips.** Counted again with `pg_stat_statements` at one client: no savepoint
statement was recorded, and a deposit now makes nine round trips, six of them while
the settlement row is held (previously eleven and eight).

**Measurement.** Control: fresh stacks on the image with 8.2 and without this
change. Candidate: the same plus this change. Three repetitions each:

| Run | Metric | Control | Candidate | Change |
|---|---|---:|---:|---:|
| deposit, USD, 10 VUs | req/s | 154.5 (152.9–155.7) | 164.8 (164.4–165.2) | +6.6 % |
| | p50 / p95 / p99 ms | 62.5 / 76.0 / 88.5 | 58.5 / 71.4 / 84.1 | |
| deposit, USD, 50 VUs | req/s | 146.3 (144.0–147.6) | 165.4 (164.8–166.0) | +13.0 % |
| | p50 / p95 / p99 ms | 291.9 / 569.4 / 1,515.6 | 273.5 / 469.8 / 797.9 | |
| deposit, USD, 200 VUs | req/s | 117.7 (116.9–118.9) | 150.8 (150.6–151.0) | +28.2 % |
| | p50 / p95 / p99 ms | 1,471.3 / 3,430.5 / 4,833.3 | 1,150.3 / 2,628.4 / 3,689.2 | |
| transfer, USD, 50 VUs | req/s | 872.2 (866.5–878.6) | 883.3 (873.0–896.4) | no change ¹ |
| | p99 ms | 167.9 | 168.8 | no change ¹ |
| mixed, 50 VUs | req/s | 720.8 (713.8–728.7) | 791.4 (776.7–806.2) | +9.8 % |
| | p95 / p99 ms | 463.8 / 852.2 | 400.8 / 780.2 ¹ | |
| contention, 2 hot wallets, 50 VUs | req/s | 108.8 (107.7–109.6) | 156.9 (156.7–157.0) | +44.1 % |
| | p50 / p95 / p99 ms | 336.2 / 825.9 / 3,810.7 | 228.2 / 683.8 / 2,444.6 | |

¹ The ranges overlap; not claimed as a change. Every other difference in the table
is larger than the variation between repetitions. No run returned an unexpected
response and no deadlock was recorded.

**Result: kept.** This is the change 8.1 was trying to be. Doing less while the
settlement row is held raised deposit throughput in one currency by 13 % at 50
clients and halved p99, and by 28 % at 200 clients. Transfers spread over 200
wallets are not waiting on any single row, so they gained nothing — as expected.
The two-hot-wallet contention run gained most, because there every transaction
holds both contended rows from start to finish.

### 8.4 All kept changes together

The original code (section 8.1's "before" repetitions) against the code with both
kept changes, 8.2 and 8.3. Fresh stacks, three repetitions each, the connection
pool at its default of 100 on both sides:

| Run | req/s before | req/s after | Change | p99 ms before | p99 ms after |
|---|---:|---:|---:|---:|---:|
| deposit, USD, 10 VUs | 155.9 (154.4–157.8) | 164.8 (164.4–165.2) | +5.7 % | 87.1 | 84.1 ¹ |
| deposit, USD, 50 VUs | 144.6 (143.0–147.3) | 165.4 (164.8–166.0) | +14.4 % | 1,425.5 | 797.9 |
| deposit, USD, 200 VUs | 117.8 (117.3–118.7) | 150.8 (150.6–151.0) | +28.0 % | 4,779.0 | 3,689.2 |
| transfer, USD, 50 VUs | 862.2 (848.0–880.8) | 883.3 (873.0–896.4) | no change ¹ | 171.1 | 168.8 ¹ |
| mixed, 50 VUs | 717.3 (712.5–720.6) | 791.4 (776.7–806.2) | +10.3 % | 839.8 | 780.2 ¹ |
| contention, 2 hot wallets, 50 VUs | 106.3 (104.6–108.7) | 156.9 (156.7–157.0) | +47.5 % | 4,037.4 | 2,444.6 |

¹ The ranges overlap; not claimed as a change.

The settlement row is still the ceiling. What changed is how much work happens
while it is held, and so how high the ceiling is.

### 8.5 Cap the connection pool — not adopted as the default

**Hypothesis.** Section 7.4 shows one API instance holding every PostgreSQL
connection under heavy load. A deposit can only make progress once it holds its
currency's settlement row, which admits one writer at a time, so connections
beyond a handful can only wait. Capping the pool at 40 — room for two instances
and an operator inside the server's limit — should therefore cost no throughput,
and should keep the database reachable.

**Change.** Configuration only: the API's connection string in Compose gained
`Maximum Pool Size=${LEDGER_DB_MAX_POOL_SIZE:-100}`. The default is Npgsql's own,
so nothing changes unless the variable is set.

**Measurement.** Both sides on the final image (8.2 and 8.3), fresh stacks, three
repetitions each, run alternately. These runs came after the laptop's sleep
(section 13), when variation between identical repetitions was up to about 10 %,
so a difference of a few percent in throughput would not show.

| Run | Metric | Pool 100 | Pool 40 |
|---|---|---:|---:|
| deposit, USD, 10 VUs | req/s | 154.7 (140.3–165.4) | 159.8 (156.9–163.5) ¹ |
| deposit, USD, 50 VUs | req/s | 156.3 (142.4–163.7) | 153.5 (148.3–159.6) ¹ |
| | p50 / p95 / p99 ms | 300.2 / 471.2 / 682.0 | 224.7 / 1,106.2 / 1,496.7 |
| deposit, USD, 200 VUs | req/s | 144.2 (138.2–152.5) | 154.1 (150.8–160.2) ¹ |
| | p50 / p95 / p99 ms | 1,213.6 / 2,757.4 / 3,893.5 | 1,233.0 ¹ / 1,713.7 / 3,709.1 ¹ |
| transfer, USD, 50 VUs | req/s | 832.5 (757.0–908.2) | 817.6 (780.9–854.9) ¹ |
| | p99 ms | 178.3 | 158.4 |
| mixed, 50 VUs | req/s | 735.5 (694.4–790.4) | 778.4 (756.5–798.6) ¹ |
| | p50 / p95 / p99 ms | 8.4 / 442.2 / 871.5 | 19.4 / 338.4 / 560.7 |
| contention, 2 hot wallets, 50 VUs | req/s | 148.2 (141.0–157.1) | 152.9 (142.1–161.9) ¹ |
| | p50 / p95 / p99 ms | 247.0 / 696.6 / 2,313.6 | 152.1 / 1,808.8 / 2,672.8 |
| Database reachable during and after the 200-VU run | | in 0 of 3 | in 3 of 3 |
| Unexpected responses | | 0 | 0 |

¹ The ranges overlap; not claimed as a change.

**Result: not adopted as the default.** Throughput did not measurably change
anywhere, and with the cap the database stayed reachable, as predicted. But the
cap did not remove waiting, it moved it — out of PostgreSQL's lock queue and into
the pool's — and that reshaped latency in both directions:

- **worse** for writers contending on one row at moderate concurrency: deposits
  from 50 clients more than doubled their p99 (682 to 1,497 ms), and the
  two-hot-wallet run more than doubled its p95;
- **better** where the queue was longest or shared with reads: deposits from 200
  clients cut p95 by 38 %, and the mixed scenario cut p95 and p99 by a quarter and
  a third — while its median more than doubled, because reads now wait for a
  connection behind writers.

A default that doubles the tail latency of the most common money movement is not a
performance improvement, so the default stays at 100 and the cap stays available
as `LEDGER_DB_MAX_POOL_SIZE`. The problem it was meant to solve is still real and
is recorded in section 12: pool size has to be chosen together with
`max_connections` and the number of instances, and the service should not connect
as a superuser.

## 9. Concurrency and deadlocks

### No deadlocks, anywhere

PostgreSQL's `deadlocks` counter stayed at **0** on every stack in this document:

| Stack | Commits | Rollbacks | Deadlocks |
|---|---:|---:|---:|
| baseline matrix | 2,730,948 | 1,189 | 0 |
| the same stack after three further repetitions, the last killed mid-run (cumulative: includes the row above) | 4,050,931 | 2,693 | 0 |
| three "before" repetitions | 1,313,024 | 1,826 | 0 |
| three "after" repetitions (rejected lock order) | 1,270,168 | 1,757 | 0 |
| final stack (the last connection-pool control repetition) | 433,637 | 551 | 0 |

Every other run in the document also recorded its own before-and-after counters,
and every one that could connect recorded zero deadlocks.

That covers transfers in both directions between the same pair of wallets,
transfers fanning out from one wallet, deposits, withdrawals, idempotent replays
and reversals, all concurrently, at up to 200 clients. It is what ADR-006 claims
for a total lock order: not that deadlocks are unlikely, but that they cannot
occur. The integration tests assert the same thing directly
(`Transfers_in_opposite_directions_do_not_deadlock` and the mixed-traffic test),
and they pass.

### Every rollback is a refusal

A rollback that is not a business refusal would mean a failure — a constraint
violation the domain should have prevented, a serialisation error, a timeout.
Rollbacks were reconciled against the refusals k6 counted (insufficient funds and
duplicate reversals in the mixed scenario):

- baseline matrix: 1,189 rollbacks, 309 + 325 + 303 + 252 = 1,189 refusals;
- "before" repetitions: 1,826 rollbacks, 1,826 refusals;
- "after" repetitions: 1,757 rollbacks against 1,756 refusals. One rollback is
  unaccounted for. The API was stopped immediately before the counter was read,
  which may have rolled back one in-flight transaction; that was not verified;
- final stack: 551 rollbacks, 286 + 265 = 551 refusals.

The interrupted runs cannot be reconciled, because the killed load generator never
wrote its summary.

### Row contention

Transfers squeezed into a handful of wallets, 50 clients, baseline:

| Hot wallets | Pattern | req/s | p50 ms | p99 ms | Unexpected |
|---:|---|---:|---:|---:|---:|
| 2 | random pairs | 96.6 | 373.22 | 3,934.66 | 0 |
| 4 | random pairs | 168.3 | 49.44 | 1,192.25 | 0 |
| 10 | one source to many | 117.3 | 297.94 | 3,819.28 | 0 |

Throughput under contention is bounded by the most contended row, exactly as with
the settlement account: two wallets that every transfer touches behave like a
single hot row, four spread the load, and a fan-out from one source serialises on
that source whatever the number of destinations. Every transfer still either
completed or was refused; none failed.

## 10. Correctness after load

Throughput means nothing if the ledger is wrong at the end of it. After every
stack was loaded, `load-tests/sql/verify-invariants.sql` ran ten checks against the
database, each reporting the number of violations:

1. the ledger sums to zero in every currency;
2. every stored balance equals the sum of that account's entries;
3. no wallet balance is negative;
4. every transaction balances and has at least two entries;
5. no entry exists without its transaction and its account;
6. every entry is in its account's currency;
7. every transaction has exactly one outbox message;
8. no outbox message exists without its transaction;
9. no idempotency key was used twice;
10. no transaction was reversed twice.

| After | Transactions | Violations |
|---|---:|---:|
| the baseline matrix | 293,244 | 0 on every check |
| load runs killed mid-flight, then the API stopped with 35,848 messages unpublished | 542,070 | 0 on every check |
| three "before" repetitions | 284,762 | 0 on every check |
| three "after" repetitions (rejected lock order) | 271,198 | 0 on every check |
| each of the three broker-outage runs (section 11) | 290,015 / 313,625 / 329,963 | 0 on every check |
| the final stack (the last connection-pool control repetition) | 90,173 | 0 on every check |

Idempotency was also checked while it was under load, not only afterwards. The
mixed scenario replays requests with the same idempotency key and requires the
same transaction back, and reverses transactions twice and requires the second to
be refused. Across all 22 mixed and warm-up runs recorded, the violation counter
stayed at **0**.

Nothing about these results depends on the load generator behaving: the killed
runs were stopped with requests in flight, and the database was still consistent.
Every guarantee is enforced inside PostgreSQL transactions, locks and constraints,
so a client disappearing mid-request can only leave a transaction uncommitted,
never half-committed.

## 11. The outbox under load, and without a broker

ADR-009 makes three promises about messaging: a broker outage is not a financial
outage, every committed transaction's message is kept until it can be published,
and nothing confirmed by the broker is lost. Each was tested under load by
`load-tests/tools/outbox-outage.sh`: deposits across USD and EUR from 50 clients,
with RabbitMQ stopped for the middle third of the run and started again.

| Run | Deposits | Unexpected | Backlog when the broker stopped | when it returned | when load ended | Drained after load |
|---|---:|---:|---:|---:|---:|---:|
| old outbox index, 90 s | 271.7 req/s | 0 | 1,720 | 10,873 | 15,556 | 57 s |
| new outbox index, 90 s | 262.3 req/s | 0 | 200 | 9,423 | 9,048 | 45 s |
| new index with audit queue, 60 s | 272.3 req/s | 0 | 921 | 7,247 | 11,189 | 52 s |

**Money kept moving.** Deposits ran at 262–272 req/s through the outage runs,
against 279–282 req/s for the same load with the broker up in section 7.5 —
different runs, images and table sizes — and not one request failed.
RabbitMQ is not in the transaction, so its absence cannot roll one back.

**Messages were kept.** The backlog grew for as long as the broker was away and
drained to zero after it returned, every time. Afterwards the invariant
"every transaction has exactly one outbox message" held with zero violations.

**Nothing was lost.** In this environment nothing consumes the events, and a topic
exchange with no queue bound confirms a message and then discards it, so broker
confirmation alone proves only that the broker accepted a message. For the third
run a durable audit queue was bound to `ledger.events` with `#`, purged, and
counted afterwards (`load-tests/tools/outbox-audit.sh`):

| Published during the run (database) | Delivered to the audit queue | Lost | Duplicates |
|---:|---:|---:|---:|
| 16,338 | 16,339 | 0 | 1 |

The broker was stopped and started in the middle of that run; the queue is durable
and the messages persistent, so everything published before the stop was still
there after it. The one extra message is an at-least-once redelivery — published,
confirmed, and published again because the confirmation had not been recorded —
which is exactly what ADR-009 says consumers must tolerate.

**Retries stayed bounded.** In each outage run roughly 220–250 messages needed
more than one attempt, and none needed more than three.

**The publisher is the slower half.** It publishes one message at a time and waits
for the broker's confirmation of each. On the old index it could not keep up with
272 deposits a second even with the broker healthy, and during the 800-per-second
transfer run used to grow the table it published about 230 messages a second
while the backlog climbed past 190,000. With the new index it kept up with
262–272 deposits a second; its ceiling at higher write rates was not re-measured.
A sustained write rate above what one publisher can confirm grows the backlog
without bound, which is safe — the messages are durable — but means consumers
fall further behind for as long as it lasts.

## 12. What is still slow, and what was not answered

### Bottlenecks that remain

1. **The settlement row.** Every deposit and withdrawal in a currency still queues
   on one row. With both kept changes, one currency takes about 165 deposits a
   second from 50 clients on this machine; more clients add latency rather than
   throughput (about 151 a second from 200). The data-model answer is settlement
   sharding (ADR-010). It was tested only indirectly: two currencies, and so two
   rows, gave 1.9 times the throughput of one.
2. **One outbox publisher, one message at a time.** It waits for the broker to
   confirm each message before sending the next. On the old index it published
   about 230 messages a second while an 800-per-second transfer load ran; on the
   new index it kept up with about 270 deposits a second, and its ceiling at
   higher rates was not re-measured. Sustained writes above what it can confirm
   grow the backlog — safely, since the messages are durable, but without limit.
   Confirming in batches, or running several publishers (the claim already uses
   `SKIP LOCKED`), are the obvious next steps; neither was built or measured.
3. **The outbox is never pruned.** Section 8.2 stops history from slowing the
   claim, but the table still grows by a row for every transaction, and so do the
   backup, the vacuum work and the cost of any future change to its indexes.
4. **Connections.** With the default pool of 100 and PostgreSQL's
   `max_connections` of 100, one API instance under heavy load held every
   connection the server had, and an operator's `psql` was refused; a second
   instance would have been refused the same way. Section 8.5 measures what
   capping the pool costs.
5. **The application connects as the database superuser.** Apart from the security
   question, that is why the three connections PostgreSQL reserves for
   administrators were not reserved in practice.
6. **A deposit still makes nine round trips, six of them while the settlement row
   is locked:** reading the settlement account, locking and reading the wallet,
   the idempotency lookup, the write batch and the commit. Every one of them is
   paid for by every other deposit waiting on the row. Folding each lock and its
   read into a single statement would take two more out of the locked window, but
   ADR-006 separated them deliberately after composing them was observed not to
   hold the lock, so that change would need its own evidence first.

### Questions this phase did not answer

- Why locking the settlement row last made waits so unfair (8.1). A convoy on
  shared wallets was tested and ruled out; no other explanation was established.
- Why the two-hot-wallet contention run improved under that rejected order, when
  all the change added to a transfer was one read before its locks.
- Why the transfer runs with a 40-connection pool averaged about 1.7 ms per WAL
  flush, against about 0.7 ms in every other run whose counters could be read. The
  transfer runs with the default pool could not be read at all — the pool held
  every connection — so this is not a like-for-like comparison.
- What the telemetry costs. Request logging, tracing and metrics stayed on
  throughout and were never measured separately.
- How the service behaves over hours rather than minutes: vacuum, checkpoints
  under sustained load, and table and index bloat.

## 13. How far these numbers can be trusted

Every figure above is real and reproducible, and every one of them is also narrower
than it looks. Read them with these limits in mind:

- **One laptop, shared.** k6, the API, PostgreSQL, RabbitMQ and the metrics stack
  all ran in one Docker Desktop VM on six physical cores. The load generator took
  CPU from the system it was measuring, and other applications on the host were
  running. Absolute throughput on dedicated servers would differ, possibly a lot;
  the comparisons between runs on this machine are what the numbers support.
- **The machine may not stay the same machine.** The laptop went to sleep once
  during the phase (Windows logged sleep at 04:07:58Z and the return from low power
  at 08:12:08Z). No measured run spanned it. Conditions after a wake cannot be
  assumed equal to those before — a snapshot taken during a later run found the
  host near CPU saturation with other applications (a browser, a messaging client,
  the antivirus scanner) active — so the connection-pool experiment did not reuse
  its control from before the sleep. It re-measured the control afterwards,
  alternating runs with the candidate. As it turned out, the first post-wake
  control ran deposits at 10 VUs at 165.4 req/s, in line with the 164.4–165.2
  measured on the same image before the sleep.
- **Closed-loop load.** Each virtual user sends its next request the moment the
  last one returns. That measures capacity at a given concurrency. It does not
  model independent arrivals, where requests keep coming while the system is slow
  and queues can grow without bound; open-loop tests were not run.
- **Short runs, few repetitions.** Runs lasted 45 seconds after warm-up, and
  before/after comparisons used three repetitions a side. Before the laptop slept
  (see above), throughput varied by about ±2 % between repetitions and p99 by up
  to about ±13 %. After it, variation was several times larger: two repetitions
  of an identical configuration differed in throughput by up to about 10 %, so the
  connection-pool experiment, which ran after the sleep, supports weaker
  conclusions than the others. Differences inside the measured ranges are never
  claimed as changes. Nothing here says anything about behaviour over hours —
  vacuum, checkpoints under sustained load, or table bloat.
- **Data size matters and was controlled, not varied.** Throughput fell measurably
  as the same stack accumulated history: deposits at 50 VUs ran at 147.8 req/s in
  the baseline matrix and at 133.4–134.5 req/s in three later repetitions on the
  same, by then larger, database. Comparisons were therefore made on fresh stacks.
  Behaviour with tens of millions of entries was not measured.
- **Blind spots at high concurrency.** At 100 VUs and above the API's connection
  pool held every PostgreSQL connection, so the resource sampler and the per-run
  database counters could not connect. Those cells are blank rather than estimated.
- **Untuned PostgreSQL, default pool.** The database ran on stock settings apart
  from diagnostics, with the WAL on the same virtual disk as the data. One API
  instance, one database, no replicas, no connection pooler.
- **Telemetry was on.** Request logging, tracing and metrics stayed enabled
  throughout, as in production. Their cost is included in every number and was not
  measured separately.
- **One workload shape.** Wallets were chosen uniformly at random. Real traffic is
  skewed — a few very busy accounts — and would make wallet rows hotter than they
  were here.

### Experiment disclosures

Everything that went wrong while measuring, in one place:

- **Three baseline runs were disturbed** by a CPU-heavy log search run on the host
  by mistake (22:17:50Z–22:20:20Z). They are marked in section 6 and were not used as
  evidence; the settlement-row comparison they belonged to was re-run cleanly (7.5).
- **A first "before" set was discarded.** Three repetitions run back to back on the
  baseline's database drifted downwards as it grew — deposits at 50 VUs fell from
  147.8 to 133.4–134.5 req/s — so every comparison was re-measured on fresh stacks.
- **Two background runs were killed** by the tooling when the host ran low on memory.
  The orphaned load was stopped, partial repetitions were discarded, and later runs
  checked free memory first.
- **The laptop slept once** (04:07:58Z–08:12:08Z). No measured run spanned it; the
  connection-pool experiment, which ran afterwards, re-measured its own control.
- **Two claims in the working notes were wrong and corrected before publication:** a
  supposed ~5 % post-wake slowdown (it came from a pool-40 run, not from the wake)
  and a per-run retry count that was in fact cumulative (8.2).
- **Three unexpected responses** occurred, all on the rejected lock-order image; their
  status was not captured (8.1).
- **One rollback** in the lock-order set was not matched to a refusal (9).

## 14. Reproducing the measurements

Everything runs from a Bash shell (Git Bash on Windows works) with Docker running.
k6 is not installed on the host; `run.sh` runs it from its container on the
Compose network.

```bash
# 1. A fresh stack. -v matters: results depend on how much history the tables hold.
docker compose down -v
docker compose up -d --build

# 2. One scenario, by hand.
load-tests/run.sh deposit VUS=50 DURATION=45s CURRENCIES=USD WALLETS=200

# 3. The full staged matrix, or the key set used for before/after comparisons.
load-tests/tools/matrix.sh baseline full 45s
load-tests/tools/matrix.sh pre1 key 45s

# 4. One table from any number of matrix labels.
python load-tests/tools/aggregate.py pre1 pre2 pre3

# 5. Correctness after load: every row must report 0 violations.
docker exec -i ledger-postgres psql -U ledger -d ledger < load-tests/sql/verify-invariants.sql

# 6. Database diagnostics.
docker exec -i ledger-postgres psql -U ledger -d ledger < load-tests/sql/reset-stats.sql
docker exec -i ledger-postgres psql -U ledger -d ledger < load-tests/sql/diagnostics.sql
docker exec -i ledger-postgres psql -U ledger -d ledger < load-tests/sql/explain.sql
docker exec -i ledger-postgres psql -U ledger -d ledger < load-tests/sql/blocking.sql

# 7. Broker outage under load, and proof that nothing was lost.
load-tests/tools/outbox-audit.sh start audit
load-tests/tools/outbox-outage.sh audit 50 60s
load-tests/tools/outbox-audit.sh check audit     # once the backlog has drained
load-tests/tools/outbox-audit.sh stop

# 8. Before/after comparison across repetitions (mean, range, overlap).
python load-tests/tools/compare.py pre1,pre2,pre3 lock1,lock2,lock3

# 9. The settlement-row experiment, and lock-chain diagnostics.
load-tests/tools/matrix.sh hot hotrow 45s
load-tests/tools/chains.sh diag 25 1             # alongside a run, not a measured one

# 10. One repetition on a fresh stack, on a given image and pool size.
LEDGER_DB_MAX_POOL_SIZE=40 load-tests/tools/fresh-run.sh b1 ledger-api:local
```

Two Compose variables make comparisons possible without editing any file:
`LEDGER_IMAGE` runs the stack on a specific API image
(`LEDGER_IMAGE=ledger-api:candidate docker compose up -d`), and
`LEDGER_DB_MAX_POOL_SIZE` sets the API's maximum database pool size (default 100,
Npgsql's own default).

Every run's CSV carries an `unexpected_statuses` column, for example `500:2 0:1`,
so an unexpected response is never just a count.

Raw results land in `load-tests/results/` (ignored by Git): per run, a k6 JSON
summary, a one-line CSV, the sampler's CSV, and the database counters taken
before and after.

### Scenario settings

| Variable | Default | Meaning |
|---|---|---|
| `VUS` | 10 | concurrent virtual users |
| `DURATION` | 60s | measured window, excluding setup |
| `CURRENCIES` | USD | comma-separated; each iteration picks one |
| `WALLETS` | 200 | wallets per currency |
| `FUNDING` | 1000000.00 | opening deposit per wallet, made once through the API |
| `HOT_ACCOUNTS` | 4 | wallets used by `contention.js` |
| `PATTERN` | mesh | `mesh` (random pairs) or `fanout` (one source) for `contention.js` |
| `THRESHOLDS` | on | `off` disables the test thresholds, as the matrix does |
| `BASE_URL` | http://api:8080 | the API as seen from the Compose network |
| `LEDGER_NETWORK` | ledger_ledger | Docker network `run.sh` attaches k6 to |

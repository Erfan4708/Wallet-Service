# Concurrency

How the ledger stays correct when requests arrive at the same moment, what that
costs in throughput, and what was measured. Every number here comes from the Phase 6
benchmarks in [PERFORMANCE.md](PERFORMANCE.md), taken on one laptop running the
whole stack; treat them as comparisons, not capacity figures.

## The problem

Two withdrawals of 25 from a wallet holding 40 must not both succeed. A domain rule
cannot prevent that on its own: each request reads a balance of 40, each decides 25
is affordable, and the wallet ends at −10. The check and the write have to be made
safe against each other, and that is a database concern.

## The mechanism

### One explicit transaction per operation

Every money movement runs inside `IUnitOfWork.ExecuteInTransactionAsync`, which opens
an explicit PostgreSQL transaction at the **READ COMMITTED** isolation level (the
driver issues `BEGIN TRANSACTION ISOLATION LEVEL READ COMMITTED`) and commits once at
the end. The ledger rows, the balance updates and the outbox row are written in that
one transaction. A nested call reuses the open transaction rather than starting a
second one.

EF Core would normally wrap `SaveChanges` in a savepoint inside an explicit
transaction. That is disabled: nothing here recovers from a failed save and carries
on, so the savepoint was two extra round trips while rows were locked (see
"What the transaction does while it holds a lock" below).

### Lock first, read second

`AccountRepository.GetForUpdateAsync` takes a row lock on each account the
operation will change **before** any balance is read:

```sql
SELECT 1 FROM accounts WHERE id = $1 FOR UPDATE;   -- lock
SELECT … FROM accounts WHERE id = $1;              -- then read
```

- The lock is its own statement, and the account is read afterwards. Composing
  `FOR UPDATE` into a query the ORM shapes was observed, in development, not to hold
  the lock (ADR-006).
- Any copy of the account the context loaded before the lock is discarded first, so
  the read cannot return a balance from before the lock existed.
- The method refuses to run outside a transaction, because a lock taken in
  autocommit is released before the caller could use it.

READ COMMITTED is enough because every decision is made on rows the transaction has
locked and then re-read. The protection SERIALIZABLE adds, against phantoms and write
skew across rows that were not locked, is not needed when the rows a decision
depends on are known in advance, and it would cost a retry loop on every operation.

### Deterministic lock order

The locks are acquired one row at a time, in ascending identifier order, whatever
order the caller named the accounts in:

| Operation | Accounts locked |
|---|---|
| Deposit, withdrawal | the wallet and the currency's settlement account |
| Transfer | both wallets |
| Reversal | every account the original transaction touched |

**Why order matters.** A deadlock needs a cycle: transaction 1 holds row A and waits
for B while transaction 2 holds B and waits for A. If every transaction acquires its
rows in one total order, nobody can hold a later row while waiting for an earlier
one, so a cycle cannot form. A transfer from A to B and a transfer from B to A both
lock A first; the second simply waits for the first to commit. This makes deadlocks
impossible rather than unlikely, and PostgreSQL never has to kill a transaction to
break one.

Locking rows one statement at a time, rather than with `WHERE id = ANY(...) ORDER BY
id FOR UPDATE`, is deliberate: a query plan may lock rows in the order it finds them
rather than the order it returns them.

### Checks made under the locks

Two checks must see every concurrent writer's committed work, so both are made after
the locks are held:

- **Idempotency.** A replayed request finds the transaction its first attempt
  committed. Two attempts with the same key and the same accounts queue on those
  locks, so the second one's lookup sees the first one's result. If anything ever
  bypassed that check, the partial unique index on the key refuses the second
  insert, which surfaces as 409.
- **Reversal.** "Has this transaction already been reversed?" is asked while the
  original's accounts are locked, so two concurrent reversals of the same transaction
  cannot both pass. The partial unique index on `reverses_transaction_id` is the
  backstop.

### What the integration tests prove

`tests/Ledger.Infrastructure.Tests/LedgerConcurrencyTests.cs` runs against a real
PostgreSQL started by Testcontainers, with each task on its own connection:

| Test | Scenario | Asserts |
|---|---|---|
| `Concurrent_withdrawals_cannot_overdraw_an_account` | 10 withdrawals of 25 from a wallet holding 100 | exactly 4 succeed, 6 are refused, the balance is 0, no deadlock |
| `Concurrent_transfers_from_one_account_cannot_overdraw_it` | 8 transfers of 20 from a wallet holding 60 | exactly 3 succeed, no deadlock |
| `Transfers_in_opposite_directions_do_not_deadlock` | 20 rounds of A→B and B→A at once | every transfer succeeds, both balances unchanged, no deadlock |
| `Concurrent_requests_with_the_same_key_move_the_money_once` | 6 deposits with one idempotency key | money moves once, one transaction, every caller sees the same identifier |
| `The_invariants_survive_mixed_concurrent_traffic` | deposits, withdrawals and transfers in both directions on two wallets | no deadlock, every ledger invariant holds, no negative balance |

Every one of them finishes by asserting the global invariants: the ledger sums to
zero and every balance equals its entries.

Under load, Phase 6 recorded **zero deadlocks** in PostgreSQL's own counter on every
stack it ran, across millions of commits, with transfers in both directions between
the same two wallets at up to 200 concurrent clients. Every rollback it recorded was
reconciled against a business refusal counted by the load generator, apart from a
single one it could not attribute (PERFORMANCE.md, section 9).

## The cost: contention

Correctness by locking has a price, and the price is paid at the most contended row.

### The settlement account is the ceiling

Every deposit and withdrawal in a currency locks that currency's one settlement
account, because every one of them changes its balance. They therefore run one at a
time, however many clients send them.

Measured on the original code (baseline, USD only):

| Concurrent clients | Deposits per second | Median latency |
|---:|---:|---:|
| 1 | 125.0 | 7.47 ms |
| 10 | 152.7 | 62.39 ms |
| 25 | 154.4 | 156.81 ms |
| 50 | 147.8 | 291.82 ms |
| 100 | 118.1 | 588.99 ms |

Throughput stops rising at 10 clients while median latency grows in proportion to the
queue. Three measurements tie this to the settlement row specifically:

- `pg_stat_statements` attributed **97.1 %** of all statement time in the baseline to
  `SELECT 1 FROM accounts WHERE id = $1 FOR UPDATE`, at a mean of 69.96 ms per call,
  against 0.1 ms for the same statement uncontended.
- Spreading the same deposits over two currencies — two settlement rows — gave
  **1.9 times** the throughput: 278.7–282.1 against 146.6–149.3 deposits a second at
  50 clients.
- Transfers, which lock two wallets and no system account, reached about 860–880 a
  second at 50 clients on the same machine.

PostgreSQL was not short of CPU or disk on the deposit path: it used about one core,
and a commit's WAL flush averaged 0.7 ms, around a tenth of the time the row was
held.

### Why the settlement row is locked first

The seeded settlement accounts have the lowest identifiers of any account, so under
ascending order they are locked **first**, and held for everything the transaction
does afterwards. That was originally a side effect of seeding. It is now a deliberate,
measured property (ADR-010).

### The rejected optimisation: lock it last

Locking it last looked like the obvious way to hold it for less time. The change was
built properly — customer accounts first and system accounts last, identifier order
within each group, still one total order and so still deadlock-free — and tested,
including a database-level test proving the new order that failed against the old
one. It was measured three repetitions against three on fresh stacks:

| Scenario | Before | After |
|---|---|---|
| deposits, 50 clients | 144.6 req/s, p99 1,425.5 ms | 115.8 req/s (−19.9 %), p99 7,739.7 ms |
| deposits, 10 clients | 155.9 req/s, p99 87.1 ms | 130.6 req/s (−16.2 %), p99 1,480.0 ms |
| transfers, 50 clients | 862.2 req/s | 820.4 req/s (−4.8 %, from the extra read it needed) |

It made the path it targeted slower and far less fair: with the settlement row locked
first, no sampled lock wait exceeded 3.5 seconds; locked last, some requests waited 12
to 29 seconds while the median fell. An obvious explanation, deposits queueing behind
other deposits holding the same wallet, was tested by spreading the load over ten
times as many wallets, and ruled out: the tail got worse. **Why** the order changed
fairness so much was not established. The change was reverted, and a comment in
`AccountRepository` points to the evidence.

### What worked: doing less while holding the lock

Counting a deposit's database round trips showed eleven, eight of them made while the
settlement row was held. Two were a savepoint and its release, which EF Core adds
around `SaveChanges` inside an explicit transaction so a caller could recover from a
failed save — something this service never does. Removing them, without touching the
lock order, left nine round trips and six inside the locked window. Three repetitions
against three:

| Scenario | Before | After |
|---|---|---|
| deposits, 10 clients | 154.5 req/s | 164.8 req/s (+6.6 %) |
| deposits, 50 clients | 146.3 req/s, p99 1,515.6 ms | 165.4 req/s (+13.0 %), p99 797.9 ms |
| deposits, 200 clients | 117.7 req/s | 150.8 req/s (+28.2 %) |
| transfers, 50 clients | 872.2 req/s | 883.3 req/s (no measurable change) |

The lesson generalises: every statement added inside a ledger transaction is paid for
by every other movement waiting on the same row. `LedgerRoundTripTests` fails if the
savepoint returns.

### Hot wallets

The same queueing happens on any busy wallet. Transfers squeezed into a few wallets
at 50 clients, on the original code:

| Hot wallets | Pattern | Transfers per second | p99 |
|---:|---|---:|---:|
| 2 | random pairs | 96.6 | 3,934.66 ms |
| 4 | random pairs | 168.3 | 1,192.25 ms |
| 10 | one source to many | 117.3 | 3,819.28 ms |

Two wallets that every transfer touches behave like a single hot row; a fan-out from
one source serialises on that source however many destinations there are. Every
transfer still either completed or was refused. With both kept Phase 6 changes, the
two-wallet case rose from 106.3 to 156.9 transfers a second (PERFORMANCE.md, 8.4).

### The connection pool

The API's Npgsql pool defaults to 100 connections, which is also PostgreSQL's
`max_connections` here. Past about 100 concurrent requests one instance held every
connection the server had, and an administrator's `psql` was refused. Capping the pool
at 40 kept the server reachable and changed no measured throughput, but it moved the
queue from PostgreSQL's lock manager into the pool and doubled deposit p99 latency at
50 clients, so the default was left at 100 (PERFORMANCE.md, 8.5). Pool size has to be
chosen together with the number of instances and the server's limit.

## Remaining limitations

- **Per-currency write throughput is bounded by one row.** About 165 deposits and
  withdrawals a second per currency on the reference laptop. The data-model answer is
  to shard the settlement account — several settlement accounts per currency, each
  movement using one — which the two-currency measurement suggests would scale roughly
  with the number of rows. It is not built; it changes the chart of accounts, and
  reporting the settlement position would mean summing the shards.
- **Latency grows with concurrency, not just throughput flattening.** Closed-loop tests
  measure capacity at a given concurrency. A burst of independent arrivals above the
  ceiling would queue; that was not tested.
- **A client-chosen wallet identifier can sort below a settlement account.** The lock
  order is still total and still correct; that wallet's deposits would take the
  settlement lock second. The effect was not measured.
- **No retries on transient database errors.** A request that fails for a transient
  reason returns 500; the client retries, safely if it sent an idempotency key (see
  [FAILURES.md](FAILURES.md)).

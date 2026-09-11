# Failure behaviour

What happens when something goes wrong: which part fails, what stays durable,
whether the caller's request fails, whether retrying is safe, whether work can be
done twice, and how the system recovers.

Everything here is behaviour the code has today, and each section names its
evidence:

- **Automated tests**, named so they can be found.
- **End-to-end checks** against the Compose stack, run on the Phase 7 image on
  11 September 2026: services stopped and started, the API killed with `SIGKILL`
  mid-traffic, and a queue bound to the exchange to see what a consumer would
  receive.
- **The Phase 6 load tests** in [PERFORMANCE.md](PERFORMANCE.md).

Where a behaviour follows from the design but was not exercised, the section says
so.

## Contents

1. [PostgreSQL unavailable](#1-postgresql-unavailable)
2. [RabbitMQ unavailable](#2-rabbitmq-unavailable)
3. [Redis unavailable](#3-redis-unavailable)
4. [The outbox publisher crashes](#4-the-outbox-publisher-crashes)
5. [The API crashes after the database commit](#5-the-api-crashes-after-the-database-commit)
6. [A crash around the RabbitMQ confirmation](#6-a-crash-around-the-rabbitmq-confirmation)
7. [A consumer receives a message twice](#7-a-consumer-receives-a-message-twice)
8. [The same HTTP request arrives twice](#8-the-same-http-request-arrives-twice)
9. [Invalid currency](#9-invalid-currency)
10. [Insufficient balance](#10-insufficient-balance)
11. [Concurrent transfers](#11-concurrent-transfers)
12. [A reversal requested twice](#12-a-reversal-requested-twice)

[What is not handled](#what-is-not-handled) lists the gaps.

## The rules every case follows

- **Money moves in one PostgreSQL transaction or not at all.** Each movement commits
  four things together, or none of them: the ledger transaction, its entries, the
  balance updates and the outbox row.
- **Refused requests leave nothing behind.** A 400, 404, 409 or 422 writes no
  transaction, no entry and no outbox row. The idempotency mechanism does not
  remember the refusal either.
- **The service does not retry on the caller's behalf.** Failed database commands
  are not retried, and nothing is queued for later. Recovery comes from two places:
  the client retrying a request, and the outbox publisher retrying a message.
- **Retrying a money movement is safe only if the first attempt is identifiable.**
  - An `Idempotency-Key` header makes a retry return the original result.
  - A client-chosen `transactionId` in the body makes a second commit fail with 409.
  - Without either, a retry after an unknown outcome can move the money twice.
  - A reversal can never be applied twice, key or not.
- **Error responses never carry internals.** Every error is RFC 9457 problem
  details with a `traceId`. A 500 carries only a type, a title, a status and the
  `traceId`.

## Summary

| Failure | Does the request fail? | What stays durable | Is a retry safe? | Can work be duplicated? | Recovery |
|---|---|---|---|---|---|
| PostgreSQL unavailable | Yes, 500; readiness 503 | Everything already committed | Yes, with a key or `transactionId` | Only if a client retries with neither | Automatic when PostgreSQL returns |
| RabbitMQ unavailable | No | Money and outbox rows | Nothing to retry; the publisher retries | Yes, a message can be delivered twice | Automatic when the broker returns |
| Redis unavailable | No; readiness `Degraded` | Nothing lives in Redis | Nothing to retry | No | Automatic reconnection |
| Outbox publisher crashes | No | Outbox rows and their claims | Automatic, after the lease | Yes, if the broker already had the message | Lease expires, any publisher claims it |
| Crash after commit | The client gets no response | The whole committed transaction | Yes, with a key or `transactionId` | Only if a client retries with neither | Restart; the client retries |
| Crash around the confirmation | No HTTP request involved | Outbox row, and the message if the broker persisted it | Automatic | Yes | Republished after the lease or backoff |
| Duplicate delivery | No HTTP request involved | The ledger is unaffected | Consumers must de-duplicate | Yes, for a consumer that does not de-duplicate | Consumer-side |
| Duplicate HTTP request | No; replay 200 or conflict 409 | The first transaction only | Yes, with the same key | No, with a key or `transactionId` | Nothing to recover |
| Invalid currency | Yes, 400 or 422 | Nothing written | Pointless without a change | No | Fix the request |
| Insufficient balance | Yes, 422 | Nothing written | Yes; it succeeds once funds arrive | No | Fund the wallet |
| Concurrent transfers | No; they queue | Every committed transfer | Yes, with a key | No | Nothing to recover |
| Reversal requested twice | Second one 409, or 200 on key replay | The first reversal | Yes, always | No | Nothing to recover |

---

## 1. PostgreSQL unavailable

**What fails.**
- **Endpoints:** every endpoint that reads or writes data returns 500 with a
  generic problem details body. The e2e 500 carried only `type`, `title` ("An
  unexpected error occurred."), `status` and `traceId`: no host, no connection
  string, no exception type.
- **Readiness:** `GET /health/ready` returns **503** with
  `{"status":"Unhealthy","checks":{"postgres":"Unhealthy",…}}`.
- **Outbox publisher:** it cannot claim messages. It logs the failure and tries
  again every poll interval (5 s).

**What keeps working.** `GET /health/live` stays **200**, because liveness checks
nothing external. An orchestrator takes the instance out of traffic but does not
restart it into a crash loop.

**What stays durable.** Everything committed before the outage. A transaction in
flight when the connection is lost is rolled back by PostgreSQL, so there is no
half-written movement, and its row locks are released with it.

**Does the request fail?** Yes. Writes are not queued or buffered anywhere.

**Is a retry safe?** Yes, if the request carried an `Idempotency-Key` or a
`transactionId`. If the connection was lost during `COMMIT`, the outcome is
unknown to the service as well as to the client, and only the key or the
identifier settles it. A retry after recovery then:
- returns the committed transaction, if the commit succeeded;
- records it once, if the commit failed.

**Can work be duplicated?** Only when a client retries a request that had neither
a key nor an identifier.

**How it recovers.** Automatically:
- The connection pool opens new connections as soon as PostgreSQL accepts them.
- Readiness returns to 200.
- The publisher drains whatever accumulated.

No manual step is needed.

**Evidence.**
- **E2E:** with PostgreSQL stopped, readiness returned 503, liveness 200 and
  `GET /accounts/{id}` 500 with the body above. After PostgreSQL started, the same
  request returned 200. A deposit sent during the outage with an idempotency key
  failed with 500; retried with the same key after recovery, it returned 201 once.
- **Tests:** `HealthCheckTests.Readiness_is_unhealthy_when_the_database_cannot_be_reached`.
- **CI:** starts the image without any database and requires liveness to pass.

## 2. RabbitMQ unavailable

**What fails.** Publishing.
- **Each attempt:** the connection times out after 5 s, or the broker's
  confirmation does not arrive within 10 s.
- **The row:** the failure is recorded on the outbox row (`last_error`), and the
  next attempt is scheduled 2^attempts seconds later, at most 300 s.
- **The batch:** it stops at the first failure, so one outage does not burn
  through every message's attempts.
- **Readiness:** returns **200** with `"status":"Degraded"` and
  `"rabbitmq":"Degraded"`.

**What stays durable.** Everything. RabbitMQ is not part of any financial
transaction, so its absence cannot roll one back. Every committed transaction
still has its outbox row.

**Does the request fail?** No. Deposits, withdrawals, transfers and reversals
return 201 as normal.

**Is a retry safe?** There is nothing for a client to retry; the publisher retries
each message itself.

**Can work be duplicated?** Yes, as a duplicate *delivery*. A confirmation that
timed out may belong to a message the broker did persist, and retrying it
publishes that message again (section 6).

**How it recovers.**
- **Reconnection:** automatic. The publisher rebuilds its connection on the next
  attempt.
- **Pending messages:** each is published once its backoff expires. After a long
  outage a message that has reached the 300 s cap can wait up to five minutes
  after the broker returns.
- **Messages already routed:** persistent messages in durable queues survive a
  broker restart.
- **Messages with no queue:** with no queue bound, the topic exchange confirms a
  message and discards it. Queues belong to consumers.

**Evidence.**
- **E2E, broker stopped:** readiness reported `Degraded` with 200, a deposit
  returned 201, and its message stayed pending. It was published 11 s after the
  broker started.
- **Phase 6, under load:** 50 clients depositing through a broker outage lost no
  request (262–272 deposits a second). The backlog reached up to 15,556 messages
  and drained within 45–57 s of the load ending (PERFORMANCE.md, section 11).
- **Tests:**
  - `OutboxTests.A_broker_failure_leaves_the_message_pending_with_its_error_recorded`
  - `OutboxTests.A_message_that_failed_is_retried_once_its_backoff_expires`
  - `MessagingAndCacheTests.Publishing_to_an_unreachable_broker_fails_rather_than_silently_succeeding`
  - `MessagingAndCacheTests.The_broker_health_check_degrades_rather_than_fails_when_it_is_down`

## 3. Redis unavailable

**What fails.** The `redis` readiness check, which reports `Degraded`. Readiness
still returns **200**.

**What stays durable.** Nothing is kept in Redis. **No code reads or writes it**;
it is provisioned, connected and health-checked only (ADR-011), and it runs
without persistence.

**Does the request fail?** No. **Is a retry safe?** There is nothing to retry.
**Can work be duplicated?** No.

**How it recovers.** The connection is created with `AbortOnConnectFail` off, so
the API starts without Redis and reconnects when Redis returns. If no Redis
connection string is configured at all, the check reports `Healthy` ("not
configured").

**Evidence.**
- **E2E:** with Redis stopped, readiness reported `"redis":"Degraded"` with 200. It
  returned to `Healthy` after Redis started.
- **Tests:** `MessagingAndCacheTests.The_cache_health_check_degrades_rather_than_fails_when_redis_is_down`
  and `…_is_healthy_when_redis_is_not_configured`.

## 4. The outbox publisher crashes

The publisher is a background service inside each API process, so it crashes when
the process does.

**What fails.** Publishing of the messages it had claimed.

**What stays durable.** The outbox rows. The claim is a committed update: it adds
one to `attempts` and pushes `next_attempt_at` 60 s into the future, which acts as
a lease. No database transaction is open while a message is being published, so a
crash strands no locks.

**Does the request fail?** No HTTP request depends on publishing. Requests in
flight in the same process fail as described in section 5.

**Is a retry safe?** It is automatic.

**Can work be duplicated?** Only if the broker had already received a message
before the crash (section 6). A message whose publish never started is published
late, not twice.

**How it recovers.** Once the lease expires, any publisher claims the message: the
restarted process, or another API instance. Several instances can run the
publisher at once, because the claim uses `FOR UPDATE SKIP LOCKED` and a claimed
row is not claimable again until its lease expires.

**Evidence.**
- **E2E, the scenario:** with the broker stopped, the API was killed with `SIGKILL`
  while three messages were claimed and unpublished. The broker and then the API
  were restarted.
- **E2E, the outcome:** all three were published 43 s after the API became ready,
  consistent with the 60 s lease running out. The queue bound to the exchange
  received each exactly once.
- **Tests:**
  - `OutboxTests.Claiming_leases_a_message_so_a_second_worker_skips_it`
  - `OutboxTests.An_expired_lease_makes_a_message_claimable_again`
  - `OutboxTests.Messages_pending_from_a_previous_process_are_published_after_a_restart`

**Limit.** The lease covers a whole batch of up to 50 messages. If publishing a
batch took longer than 60 s, a second instance could claim its tail and publish
those messages too: a duplicate delivery, not a loss.

## 5. The API crashes after the database commit

**What fails.** The client's connection. It receives no response, and cannot tell
whether its money moved.

**What stays durable.**
- **If `COMMIT` completed:** the whole transaction, including its outbox row, so
  the event will be published.
- **If the process died before `COMMIT`:** nothing. PostgreSQL rolls the
  transaction back when the connection drops and releases its locks.

**Does the request fail?** From the client's point of view, yes.

**Is a retry safe?** It depends on what the request carried:

| The request carried | Retry after the crash, first attempt committed | first attempt not committed |
|---|---|---|
| `Idempotency-Key` | **200**, the original transaction, `wasReplayed: true` | **201**, recorded once |
| `transactionId` only | **409**; confirm with `GET /ledger/transactions/{id}` | **201**, recorded once |
| neither | **201 again: the money moves twice** | 201 |

**Can work be duplicated?** Not with a key or an identifier. The outbox row was
committed once, so there is exactly one message to publish.

**How it recovers.** The process is restarted (Compose defines no restart policy;
an orchestrator would restart on liveness). The client retries.

**Evidence.**
- **E2E, the scenario:** 20 concurrent clients sent 2,000 deposits of 1.00 into one
  wallet, each with its own key. The API was killed with `SIGKILL` after four
  seconds.
- **E2E, at the kill:**
  - 455 requests had received 201, and 1,544 got connection errors.
  - **456** deposits had committed: one whose response was lost.
  - No transaction was left open in PostgreSQL.
- **E2E, the retries:**
  - After a restart, every key was retried: the 456 committed ones returned
    **200**, the other 1,544 returned **201**.
  - A second retry of all 2,000 returned 200.
  - The wallet held exactly **2,000.00**.
- **E2E, afterwards:**
  - Every transaction had one outbox message, and all were published.
  - All ten ledger invariant checks reported zero violations.
  - PostgreSQL recorded zero deadlocks.
- **Tests:** `LedgerConcurrencyTests.Concurrent_requests_with_the_same_key_move_the_money_once`,
  `OutboxTests.A_committed_transaction_leaves_exactly_one_outbox_message` and
  `OutboxTests.A_refused_transaction_leaves_no_outbox_message`.
- **Phase 6:** load runs killed mid-flight also left every invariant intact
  (PERFORMANCE.md, section 10).

## 6. A crash around the RabbitMQ confirmation

The publisher's steps, one message at a time:
1. claim and commit;
2. publish;
3. wait for the broker's confirmation;
4. mark the row published.

A crash, a timeout or a lost connection can fall between any two of them:

| Where it happens | Does the broker have the message? | The outbox row | What follows |
|---|---|---|---|
| after the claim, before publishing | no | pending, leased | published once the lease expires; **no duplicate** |
| after publishing, before the confirmation arrives (or it times out after 10 s) | possibly | pending: leased after a crash, or backed off after a timeout | published again; **a duplicate if the broker had accepted it** |
| after the confirmation, before `published_at` is written | yes | pending, leased | published again once the lease expires; **a duplicate** |
| after `published_at` is committed | yes | published | nothing further |

**What stays durable.** The outbox row, always. On the broker, messages are
published persistent (delivery mode 2) to a durable exchange, so a durable queue
keeps them across a broker restart.

**Does the request fail?** No HTTP request is involved.

**Is a retry safe?** It is automatic.

**Can work be duplicated?** Yes. **Delivery is at-least-once; exactly-once is not
claimed**, because the broker and the database cannot commit together. Section 7
describes how a consumer copes.

**Evidence.**
- **Tests:** `OutboxTests.A_crash_after_publishing_but_before_marking_causes_a_duplicate`
  reproduces the third row of the table.
- **Phase 6:** an audit queue received 16,339 messages for 16,338 published, with
  none lost and one duplicate of exactly this kind (PERFORMANCE.md, section 11).

## 7. A consumer receives a message twice

**What fails.** Nothing in the ledger. A duplicate is a second copy of an event
about a transaction that exists once. It can come from:
- the windows in section 6;
- a batch outliving its lease (section 4);
- the broker redelivering a message a consumer did not acknowledge.

**What stays durable.** The ledger is unaffected.

**Does the request fail?** No request is involved.

**Is a retry safe?** A consumer must make processing idempotent. It can:
- de-duplicate on `MessageId`, which equals the ledger transaction identifier and
  never changes between deliveries;
- record that it processed the message in the same transaction as its own effect.

**Can work be duplicated?** Yes, by a consumer that does not de-duplicate.

**How it recovers.** On the consumer's side. **This repository contains no
consumer**, so consumer de-duplication is a contract, not something implemented or
demonstrated here.

**What a consumer receives**, as observed in the e2e run:

```text
routing key  ledger.transaction.recorded.v1   (exchange ledger.events, topic)
properties   message_id = <transaction id>, type = ledger.transaction.recorded.v1,
             delivery_mode = 2 (persistent), content_type = application/json
payload      {"Kind":"Deposit","Currency":"USD","MessageId":"72169ba6-…",
              "OccurredAt":"2026-09-11T13:40:48.7453343+00:00","TransactionId":"72169ba6-…"}
```

The payload carries no amount and no balance. A consumer that needs them reads
`GET /ledger/transactions/{id}`.

**Evidence.** In the e2e run, a durable queue bound to the exchange received 405
messages for the 405 transactions recorded while it was bound. They carried 405
distinct message identifiers; that run happened to produce no duplicate.

## 8. The same HTTP request arrives twice

Every money endpoint accepts an optional `Idempotency-Key` header of up to 200
characters.

| Case | Response | Money moved |
|---|---|---|
| Same key, same request, after the first committed | **200**, the original body, `wasReplayed: true`, no `Location` | once |
| Same key, sent concurrently | one **201**; the others wait on the same row locks, then **200** | once |
| Same key, different request: another amount, currency, wallet, direction, operation, or reversal of another transaction | **409** "Idempotency key '…' was already used for a different operation." | once, by the first request |
| Same key, after the first request was *refused* (400, 404, 409, 422) | evaluated again from scratch; a refusal is not remembered | once, if it now succeeds |
| An empty or blank key | treated as no key | every time |
| A key longer than 200 characters | **400**, `errors.IdempotencyKey` | no |
| No key, the same `transactionId` in the body | **409** "The change conflicts with a record that already exists." | once |
| No key and no `transactionId` | **201** each time | **every time** |
| `POST /accounts` with the same `accountId` | **409** | one account |

**Scope of a key.**
- **Global:** one unique index across every transaction, not scoped per account
  or per client. The API has no authentication, so there is no client to scope
  by, and two callers who choose the same key collide with a 409.
- **Permanent:** keys never expire.
- **Advice:** clients should use a random UUID for each logical operation.

**What stays durable.** The first transaction and its single outbox row. A replay
writes nothing, so it produces no second event.

**How it is enforced.**
- **The lookup:** the key is looked up *after* the operation's row locks are held,
  so a concurrent duplicate sees the first one's committed work.
- **The comparison:** a stored transaction matches only if it has the same kind
  and contains every leg the new request names, each with the same account,
  direction and amount (ADR-007 and its Phase 7 amendment).
- **The backstop:** the partial unique index on `idempotency_key`.

**Evidence.**
- **E2E:**
  - a replayed transfer returned 200;
  - the same key used to deposit into another wallet, or to transfer in the
    opposite direction, returned 409;
  - a replayed reversal returned 200, and its key used to reverse another
    transaction returned 409;
  - four deposits with an empty or blank key made four transactions.
- **Tests:**
  - `LedgerConcurrencyTests.Concurrent_requests_with_the_same_key_move_the_money_once`
  - `LedgerHandlerTests.Reusing_a_key_for_a_deposit_to_another_wallet_is_a_conflict`
  - `LedgerHandlerTests.Reusing_a_key_for_a_transfer_in_the_opposite_direction_is_a_conflict`
  - `LedgerHandlerTests.Replaying_a_reversal_returns_the_original_reversal`
  - `LedgerTransactionTests.A_transfer_replay_must_name_both_wallets_in_the_same_direction`
- **Invariants:** "no idempotency key used twice" held after every load run.

## 9. Invalid currency

| Input | Response | Where it is caught |
|---|---|---|
| A code that is not a supported currency, such as `"XYZ"` or `"GBP"` | **400** "The request could not be read." | request binding |
| A number that is not a defined currency, such as `999` | **400** "Validation failed.", `errors.Currency`: "Unknown currency." | validation |
| More decimal places than the currency has, such as `1.001` USD | **400**, `errors.Amount`: "USD is expressed in 2 decimal places." | validation |
| A currency other than the wallet's, such as EUR into a USD wallet | **422** "Cannot combine amounts in different currencies: USD and EUR." | domain |
| A transfer between wallets holding different currencies | **422**, the same message | domain |

**What fails.** The request only. The currencies are USD, EUR and IRR.

**What stays durable.** Nothing is written.
- A 400 is decided before any transaction starts or any row is locked.
- A 422 is decided under the locks and rolled back.
- Should the domain check ever be bypassed, composite foreign keys still refuse an
  entry whose currency differs from its account's or its transaction's.

**Does the request fail?** Yes. **Is a retry safe?** Yes, and pointless without
changing the request. **Can work be duplicated?** No.

**How it recovers.** The client corrects the request. The refusal is counted in
`ledger_transaction_failures_total` with reason `validation` or
`currency_mismatch`. An unrecognised currency is labelled `unknown`, never with
the raw value.

**Evidence.**
- **E2E:** every row of the table.
- **Tests:** `RequestBindingTests`, `LedgerHandlerTests`, `MoneyTests` and
  `LedgerConstraintTests`.

## 10. Insufficient balance

**What fails.** A withdrawal, transfer or reversal that would take a wallet below
zero. The domain refuses it with **422** "Business rule violated.", and the detail
names the account, its balance and the amount requested, for example "Account … holds
30.00 USD but 500.00 USD was requested."

**What stays durable.** Nothing. The transaction rolls back and no outbox row is
written. The check is made on a balance read *after* the wallet's row lock was
taken, so two withdrawals racing for the same funds cannot both pass. The check
constraint `ck_accounts_balance_not_negative` is the database's backstop. System
accounts are exempt: a settlement account may go negative, and that is how issued
money is represented (DOMAIN.md).

**Does the request fail?** Yes.

**Is a retry safe?** Yes: nothing happened, and a key is not consumed by a
refusal. The retry succeeds once the wallet holds the amount.

**Can work be duplicated?** No.

**How it recovers.** The wallet is funded, and the client retries.

**Known exposure.** The 422 detail tells the caller the wallet's balance. Without
authentication, `GET /accounts/{id}` does the same (see the README's security
notes).

**Evidence.**
- **E2E:** a withdrawal of 500.00 from a wallet holding 30.00 returned 422.
- **Tests:**
  - `LedgerConcurrencyTests.Concurrent_withdrawals_cannot_overdraw_an_account`:
    10 withdrawals of 25 from 100, exactly 4 succeed.
  - `LedgerConcurrencyTests.Concurrent_transfers_from_one_account_cannot_overdraw_it`.
  - `LedgerConstraintTests`, for the check constraint.
- **Phase 6:** every rollback under load matched a refusal counted by the load
  generator, apart from one it could not attribute (PERFORMANCE.md, section 9).

## 11. Concurrent transfers

**What happens.**
- **Locking:** each transfer locks both wallets, one row at a time, in ascending
  identifier order.
- **Queueing:** anything else that touches either wallet waits for the commit.
- **Opposite directions:** a transfer from A to B and one from B to A both lock A
  first, so they queue instead of deadlocking (CONCURRENCY.md).

**What fails.** Nothing, apart from refusals decided under the locks: insufficient
funds (section 10).

**What stays durable.** Every committed transfer. Balances always equal the sum of
their entries.

**Does the request fail?** Only on a refusal, or with a 500 on a database error.
The service configures no lock or statement timeout; each statement is bounded
only by Npgsql's default 30-second command timeout. That timeout is not exercised
by the tests.

**Is a retry safe?** For a 500, as in section 1: with a key or an identifier.

**Can work be duplicated?** No.

**How it recovers.** There is nothing to recover. The cost of this safety is
queueing: busy wallets, and above all each currency's settlement account,
serialise the movements that touch them (CONCURRENCY.md, ADR-010).

**Evidence.**
- **Tests:** `LedgerConcurrencyTests.Transfers_in_opposite_directions_do_not_deadlock`
  (20 rounds of A→B and B→A at once) and
  `LedgerConcurrencyTests.The_invariants_survive_mixed_concurrent_traffic`.
- **Phase 6:** zero deadlocks in PostgreSQL's counter on every stack, at up to 200
  concurrent clients (PERFORMANCE.md, section 9).
- **E2E:** zero deadlocks after 20 concurrent clients deposited into one wallet.

## 12. A reversal requested twice

| Case | Response |
|---|---|
| The same transaction reversed again | **409** "Transaction … has already been reversed." |
| Two reversals of the same transaction at the same time | one **201**; the other waits on the same row locks, then **409** |
| A retry with the same `Idempotency-Key` | **200**, the original reversal |
| That key used to reverse a different transaction | **409**, idempotency conflict |
| Reversing a reversal | **409** "Transaction … is itself a reversal and cannot be reversed." |
| A wallet no longer holds the amount to be returned | **422**, insufficient funds; nothing written |
| An unknown transaction | **404** |

**What fails.** The second request.

**What stays durable.** The first reversal: a new transaction whose entries negate
the original's. The original transaction and its entries are never modified;
entries are append-only.

**Does the request fail?** Yes, with 409, unless it is a replay with the same key.

**Is a retry safe?** Always. A transaction can be reversed at most once whether or
not the request carried a key, so a retry after an unknown outcome cannot reverse
twice: a 409 confirms the reversal exists.

**Can work be duplicated?** No.
- **The check:** "already reversed?" is asked while the original's accounts are
  locked.
- **The backstop:** the partial unique index `ux_ledger_transactions_reverses`
  refuses a second reversal with a 409 even if that check were bypassed.
- **Not tested concurrently:** the concurrent case follows from both. No test
  sends two reversals at the same moment.

**How it recovers.** There is nothing to recover. A reversal that was itself a
mistake is corrected with a new movement, not by reversing the reversal.

**Evidence.**
- **E2E:**
  - reversing a transfer returned 201, and reversing it again 409;
  - reversing the reversal returned 409;
  - a reversal replayed with its key returned 200, and the key reused for another
    transaction 409.
- **Tests:**
  - `LedgerPersistenceTests.A_transaction_cannot_be_reversed_twice`, on PostgreSQL
  - `LedgerHandlerTests.Reversing_the_same_transaction_twice_is_refused`
  - `LedgerTransactionTests.A_reversal_cannot_itself_be_reversed`
  - `LedgerHandlerTests.Reusing_a_reversal_key_for_another_transaction_is_a_conflict`
- **Invariants:** "no transaction reversed twice" held after every load run.

---

## What is not handled

These are known gaps, not hidden ones:

- **Retries and timeouts.**
  - Transient database errors are not retried, and there is no circuit breaker.
  - No lock or request timeout is configured beyond Npgsql's default command
    timeout.
- **The outbox.**
  - It has no dead-letter handling and no attempt limit: a message that can never
    be published is retried every 300 s indefinitely.
  - Published rows are never pruned.
- **Idempotency.**
  - Only committed transactions are remembered. A refusal is re-evaluated on
    retry, and no response body is stored.
  - Keys never expire, and are not scoped to a client, because there is no
    authentication.
- **Consumers.** No consumer exists in this repository, so consumer-side
  de-duplication is not demonstrated.
- **Health checks.** The RabbitMQ readiness check opens a new broker connection on
  every probe.
- **Deployment.** The Compose stack is a development environment: one PostgreSQL
  instance with no replica or backup, no restart policy for the API, and no alerts.
  The signals for alerting exist: readiness, `ledger_outbox_pending`,
  `ledger_outbox_publish_failures_total` and `ledger_transaction_failures_total`.

# Architecture

How the service is put together, and how a request moves through it. The
diagrams are Mermaid, so they render on GitHub and live in version control beside
the code they describe. Every step shown here is what the code does today; where
something is a limitation or not built, the text says so.

- [1. System context](#1-system-context)
- [2. Layers and dependencies](#2-layers-and-dependencies)
- [3. A deposit, end to end](#3-a-deposit-end-to-end)
- [4. A transfer and its two locks](#4-a-transfer-and-its-two-locks)
- [5. Publishing from the outbox](#5-publishing-from-the-outbox)
- [6. Why lock ordering prevents deadlocks, and what it cannot fix](#6-why-lock-ordering-prevents-deadlocks-and-what-it-cannot-fix)
- [7. What is the source of truth](#7-what-is-the-source-of-truth)

## 1. System context

```mermaid
flowchart LR
    client([HTTP client])

    subgraph api[Ledger API process]
        endpoints[Endpoints<br/>thin HTTP layer]
        app[Application<br/>use cases]
        domain[Domain<br/>ledger rules]
        publisher[Outbox publisher<br/>background service]
        endpoints --> app --> domain
    end

    pg[(PostgreSQL<br/>source of truth)]
    redis[(Redis<br/>connection and health only)]
    rabbit[[RabbitMQ<br/>ledger.events topic exchange]]
    prom[(Prometheus)]
    grafana[Grafana]

    client -- REST + JSON --> endpoints
    app -- "one transaction:<br/>entries, balances, outbox row" --> pg
    publisher -- claim / mark published --> pg
    publisher -- "publish with confirms" --> rabbit
    api -. readiness ping .-> redis
    prom -- "scrapes GET /metrics" --> endpoints
    grafana -- queries --> prom
```

| Component | Role | If it is unavailable |
|---|---|---|
| PostgreSQL | Every account, balance, ledger entry, idempotency key and outbox message. The only source of truth. | Requests that need data fail with 500; readiness returns 503. See [FAILURES.md](FAILURES.md). |
| RabbitMQ | Receives `ledger.transaction.recorded.v1` events published from the outbox. Not part of any financial transaction. | Money keeps moving; events wait in the outbox; readiness reports `Degraded` with 200. |
| Redis | Provisioned and health-checked. **Nothing reads or writes it.** | Readiness reports `Degraded` with 200. Nothing else changes. |
| Prometheus, Grafana | Scrape and display the API's metrics. | The API is unaffected. |

## 2. Layers and dependencies

```mermaid
flowchart TB
    api["Ledger.Api<br/>composition root, endpoints, error contract,<br/>observability, OpenAPI"]
    infra["Ledger.Infrastructure<br/>EF Core, PostgreSQL, repositories, unit of work,<br/>outbox, RabbitMQ, Redis, health checks"]
    application["Ledger.Application<br/>use cases, repository and unit-of-work interfaces,<br/>validation, ledger telemetry"]
    domain["Ledger.Domain<br/>Account, Money, LedgerTransaction, LedgerEntry<br/>no project or package references"]

    api --> application
    api --> infra
    infra --> application
    application --> domain
    infra --> domain
```

Dependencies point inwards. `Ledger.Domain` references nothing, so a change to
the database, the ORM or the web framework cannot alter a ledger rule, and the
rules are tested without any of them. `Ledger.Application` declares the
interfaces it needs (`IAccountRepository`, `ILedgerTransactionRepository`,
`IUnitOfWork`); `Ledger.Infrastructure` implements them with `internal` classes,
so EF Core is invisible outside that project. `Ledger.Api` is the only project
that knows all of them exist. See ADR-002 and ADR-003 in
[DECISIONS.md](DECISIONS.md).

## 3. A deposit, end to end

```mermaid
sequenceDiagram
    autonumber
    actor Client
    participant EP as Endpoint<br/>POST /accounts/{id}/deposits
    participant H as DepositHandler
    participant UoW as UnitOfWork
    participant Repo as AccountRepository
    participant Dom as LedgerTransaction<br/>(domain)
    participant PG as PostgreSQL

    Client->>EP: amount, currency, Idempotency-Key header
    EP->>H: DepositCommand
    H->>H: validate identifiers, amount, currency,<br/>key and reference lengths
    Note over H: invalid → 400 problem details,<br/>nothing locked, nothing written
    H->>UoW: ExecuteInTransactionAsync
    UoW->>PG: BEGIN (READ COMMITTED)
    H->>Repo: find settlement account by system key
    Repo->>PG: SELECT … WHERE system_key = 'SETTLEMENT:USD'
    H->>Repo: GetForUpdateAsync([wallet, settlement])
    Note over Repo,PG: locks taken one row at a time,<br/>ascending identifier
    Repo->>PG: SELECT 1 … FOR UPDATE (settlement)
    Repo->>PG: SELECT account (settlement)
    Repo->>PG: SELECT 1 … FOR UPDATE (wallet)
    Repo->>PG: SELECT account (wallet)
    H->>PG: look up the idempotency key (after the locks)
    alt key already used for this deposit
        H-->>Client: 200 with the original transaction
    else key used for a different request
        H-->>Client: 409 problem details
    end
    H->>Dom: LedgerTransaction.Deposit(wallet, settlement, amount)
    Dom->>Dom: wallet +amount, settlement −amount,<br/>seal: entries sum to zero,<br/>raise LedgerTransactionRecorded
    H->>UoW: SaveChangesAsync
    UoW->>UoW: turn the domain event into an outbox row
    UoW->>PG: one batch: INSERT ledger_transactions,<br/>2 × ledger_entries and outbox_messages,<br/>UPDATE 2 × accounts
    UoW->>PG: COMMIT
    Note over PG: a deferred trigger checks the entries sum to zero.<br/>The ledger rows, both balances and the outbox row<br/>commit together or not at all
    EP-->>Client: 201 Created, Location: /ledger/transactions/{id}
```

Points worth drawing out:

- **The outbox row is in the same transaction as the money.** There is no moment
  at which the balance has changed and the event has not been recorded, or the
  reverse. Publishing happens later and separately (section 5).
- **Locks come before reads.** Every balance the decision depends on is read after
  its row is locked, so nothing can change it between the check and the write.
- **The idempotency lookup comes after the locks.** Two requests with the same key
  and the same accounts queue on those locks, and the second sees the first one's
  committed transaction. The unique index on the key is the backstop.
- **Nine round trips, six while the settlement row is held.** EF Core's automatic
  savepoint around `SaveChanges` is disabled because it added two more inside the
  locked window and served no purpose here (PERFORMANCE.md, section 8.3).

A withdrawal is the mirror image, with one more rule: the wallet must hold the
amount, or the domain refuses with `InsufficientFundsException` (422) and the
transaction rolls back.

## 4. A transfer and its two locks

```mermaid
sequenceDiagram
    autonumber
    actor Client
    participant H as TransferHandler
    participant Repo as AccountRepository
    participant PG as PostgreSQL

    Client->>H: source = B, destination = A, 30.00 USD
    H->>PG: BEGIN
    H->>Repo: GetForUpdateAsync([B, A])
    Repo->>Repo: de-duplicate and sort → [A, B]
    Repo->>PG: SELECT 1 FROM accounts WHERE id = A FOR UPDATE
    Repo->>PG: SELECT account A
    Repo->>PG: SELECT 1 FROM accounts WHERE id = B FOR UPDATE
    Repo->>PG: SELECT account B
    H->>PG: idempotency lookup
    H->>H: LedgerTransaction.Transfer(B → A)<br/>B −30.00, A +30.00, sums to zero
    H->>PG: INSERT transaction, entries and outbox row, UPDATE balances
    H->>PG: COMMIT
    H-->>Client: 201 Created
```

The locks are taken in identifier order, **not** in source-then-destination order.
A transfer from B to A and a transfer from A to B therefore both lock A first, so
they queue instead of deadlocking. No settlement account is involved, which is why
transfers spread across many wallets scale far better than deposits
(PERFORMANCE.md, section 7.1).

## 5. Publishing from the outbox

```mermaid
sequenceDiagram
    autonumber
    participant P as OutboxPublisher<br/>(background loop)
    participant PG as PostgreSQL
    participant MQ as RabbitMQ

    loop until a batch comes back smaller than 50, then every 5 s
        P->>PG: claim up to 50 due, unpublished rows in id order<br/>(FOR UPDATE SKIP LOCKED), add 1 to attempts<br/>and lease each one for 60 s
        Note over P,PG: the claim commits on its own.<br/>No transaction stays open during the publish
        loop each claimed message, in order
            P->>MQ: publish to ledger.events<br/>(persistent, MessageId = transaction id)
            MQ-->>P: publisher confirm
            Note over P,MQ: ⚠ crash window: the broker has the message,<br/>but it is not yet marked published
            P->>PG: UPDATE … SET published_at = now()
        end
        alt broker unreachable or no confirm within 10 s
            P->>PG: record the error, next attempt after<br/>2^attempts seconds (at most 300)
            Note over P: stop this batch. The rest keep their lease<br/>and are retried once it expires
        end
    end
```

**Delivery is at-least-once, never exactly-once.** If the process dies between the
broker's confirmation and the `published_at` update, the row is still pending; once
its 60-second lease expires another claim picks it up and publishes it again. The
duplicate is unavoidable, because the broker and the database cannot commit
together. Every message therefore carries a stable `MessageId` equal to the ledger
transaction identifier, and a consumer must de-duplicate on it. A crash **before**
publishing produces no duplicate: the lease expires and the message is simply
published late.

What the design guarantees instead: a committed transaction always has exactly one
outbox row, a broker outage never rolls back money, and nothing confirmed by the
broker is lost. Phase 6 verified all three under load, including an end-to-end
audit queue that received 16,339 messages for 16,338 published (PERFORMANCE.md,
section 11). Several API instances may run the publisher at once: `SKIP LOCKED`
plus the lease keeps two of them from publishing the same row at the same time.

Nothing in this repository consumes the events; the exchange exists for other
services to bind to.

## 6. Why lock ordering prevents deadlocks, and what it cannot fix

```mermaid
sequenceDiagram
    participant T1 as Transfer A → B
    participant Rows as accounts rows
    participant T2 as Transfer B → A

    Note over T1,T2: both sort their accounts to [A, B]
    T1->>Rows: lock A ✔
    T2->>Rows: lock A (waits for T1)
    T1->>Rows: lock B ✔
    T1->>Rows: write, COMMIT → releases A and B
    Rows-->>T2: lock A ✔
    T2->>Rows: lock B ✔
    T2->>Rows: write, COMMIT
    Note over T1,T2: without sorting, T1 would hold A and wait for B<br/>while T2 held B and waited for A: a deadlock
```

A deadlock needs a cycle: each transaction holding a row the other wants. If every
transaction acquires its rows in one global order, no cycle can form, so no
deadlock can occur — not "rarely", but never. Phase 6 recorded zero deadlocks
across every stack and every scenario, including transfers in both directions
between the same two wallets.

What ordering cannot fix is **contention**. Every deposit and withdrawal in a
currency must lock that currency's single settlement account, so they all queue on
one row:

```mermaid
flowchart LR
    d1[deposit wallet 1] --> s
    d2[deposit wallet 2] --> s
    d3[withdrawal wallet 3] --> s
    d4[deposit wallet 4] --> s
    s[("SETTLEMENT:USD<br/>one row, one writer at a time")]
```

That row is the measured throughput ceiling for deposits and withdrawals in one
currency — about 165 a second on the reference laptop — and it is accepted
deliberately. The details, the rejected alternative of locking it last, and the
future option of sharding it are in [CONCURRENCY.md](CONCURRENCY.md) and ADR-010.

## 7. What is the source of truth

| Data | Kind | Where it lives |
|---|---|---|
| Ledger entries | **Source of truth.** The historical record of every movement; append-only. | `ledger_entries` |
| Ledger transactions | Source of truth. Groups entries, carries the idempotency key and any reversal link. | `ledger_transactions` |
| Account balance | **Derived state**, materialised. Always written in the same transaction as the entries that change it, and must equal their sum. | `accounts.balance_amount` |
| Outbox messages | Durable record of events to publish, committed with the money. | `outbox_messages` |
| Events on RabbitMQ | Asynchronous notification, delivered at least once. Never authoritative. | `ledger.events` exchange |
| Metrics, logs, traces | Observability only. No amounts, no balances in metrics or logs. | Prometheus, stdout |
| Redis | Nothing. | — |

# Architecture Decision Records

This document records the significant architectural decisions taken in this
project, why they were taken, and what they cost us.

Each decision gets its own numbered section. Records are append-only: once a
decision has been made, it is not rewritten. If we change our mind later, we add
a new ADR and mark the old one as `Superseded by ADR-XXX`.

**Status** is one of: `Proposed`, `Accepted`, `Superseded by ADR-XXX`, `Deprecated`.
A decision refined later without being reversed keeps its record and gains a dated
amendment note.

## Index

| ADR | Decision | Status |
|---|---|---|
| [001](#adr-001) | Account balance is stored, not derived from ledger entries | Accepted, completed by 005 |
| [002](#adr-002) | Repository abstractions live in the application layer | Accepted |
| [003](#adr-003) | PostgreSQL through EF Core, configured entirely outside the domain | Accepted, extended by 005 |
| [004](#adr-004) | Money is `decimal` / `numeric`; currency stored as its ISO 4217 alpha code | Accepted |
| [005](#adr-005) | Double-entry ledger with system accounts and one aggregate owning every balance change | Accepted |
| [006](#adr-006) | Pessimistic row locks in a deterministic order under READ COMMITTED | Accepted, extended by 010 |
| [007](#adr-007) | Idempotency through a unique key, checked in the transaction that moves the money | Accepted, amended in Phase 7 |
| [008](#adr-008) | Serilog, OpenTelemetry and Prometheus for observability | Accepted, deployment recorded in 011 |
| [009](#adr-009) | Transactional outbox with at-least-once delivery | Accepted |
| [010](#adr-010) | Settlement account stays locked first; the settlement row is the accepted write limit | Accepted |
| [011](#adr-011) | One non-root image, a Compose stack with a one-shot migrator, and Redis with no role | Accepted, recorded retrospectively |

---

## ADR-001

### Title

Account balance is stored, not derived from ledger entries.

### Status

Accepted. Completed by ADR-005, which makes the relationship between the balance
and the entries explicit now that the entries exist.

### Context

An account's balance can either be a column that is updated as money moves, or a
figure computed by summing the account's ledger entries on demand. The choice
affects read cost, how concurrency will be controlled, and which artefact is
treated as the truth when two disagree.

At the time of writing there are no ledger entries yet, so nothing can be
derived from them; but the decision had to be made now because it shapes the
schema everything else is built on.

### Decision

`Account` owns a balance, and `Account.Credit` / `Account.Debit` are the only
ways to change it. It is persisted as a column on the `accounts` table.

### Alternatives Considered

**Derive the balance by summing entries.** A single source of truth, so the
balance can never disagree with the ledger, and historical balances ("as of 30
June") come free. Rejected for now because reading a balance would cost O(n) in
the account's history, and because enforcing "no overdraft" against a sum
requires preventing a concurrent insert between reading and writing — which in
practice means locking the account row anyway, at which point that row may as
well hold the balance.

### Consequences

- Balance reads are O(1), and there is a single row to lock when the concurrency
  phase arrives.
- The invariant can be enforced by the database itself (see ADR-003), not only
  by the domain.
- Once ledger entries exist there will be two representations of the same fact.
  They must be written in one transaction, the entries are the audit truth, and
  a reconciliation check (`SUM(entries) = balance`) will be needed to prove they
  agree. That obligation is created by this decision and does not exist under
  the alternative.
- The account row becomes a contention point: every movement on a busy account
  serialises on it.

---

## ADR-002

### Title

Repository abstractions live in the application layer.

### Status

Accepted.

### Context

The use cases need to load and store accounts. The interface describing that
could live in the domain layer (the classic domain-driven design placement,
where a repository is part of the domain model's contract) or in the application
layer (the clean architecture placement, where each use case declares what it
needs from the outside world).

### Decision

`IAccountRepository` and `IUnitOfWork` are declared in `Ledger.Application` and
implemented in `Ledger.Infrastructure`. The interfaces are specific to the
aggregate rather than a generic `IRepository<T>`.

### Alternatives Considered

**Interfaces in the domain layer.** Defensible, and arguably more orthodox
domain-driven design: it keeps the aggregate and the contract for retrieving it
together. Rejected because the domain currently has no need to retrieve
anything — every rule it enforces operates on objects it is handed — so the
interface would exist in the domain purely for other layers' benefit.

**A generic `IRepository<T>`.** Rejected. A generic repository can only offer
operations meaningful for every entity, which pushes it towards exposing
`IQueryable` so callers can express the rest. That puts query construction, and
therefore knowledge of the persistence model, back in the application layer —
the coupling the abstraction existed to remove.

### Consequences

- The domain has no project references and no packages at all, which is a
  property that can be checked mechanically.
- Adding an aggregate means adding an interface, rather than getting one for
  free from a generic base. That is intended: each one states exactly the access
  its use cases need.
- The unit of work is separate from the repository, because atomicity spans
  aggregates while a repository does not.

---

## ADR-003

### Title

PostgreSQL persistence via EF Core, configured entirely outside the domain.

### Status

Accepted. Extended by ADR-005, which adds database triggers to the schema.

### Context

The domain model must not know it is stored. EF Core needs enough information to
map it, and that information has to live somewhere.

### Decision

- All mapping lives in `IEntityTypeConfiguration<T>` classes in
  `Ledger.Infrastructure`. There are no EF Core attributes, base classes or
  package references in `Ledger.Domain`.
- `Money` is mapped as an owned type, so the balance occupies two columns in the
  `accounts` table rather than a separate table or a serialized blob.
- The schema is created by migrations, and the integration tests apply those
  migrations to an empty database rather than calling `EnsureCreated`, so what
  is tested is what will run in production.
- Invariants that can be expressed as constraints are also enforced by the
  database: a primary key on the identifier, `NOT NULL` on every column, a check
  that the balance is not negative, and a check that the currency is one the
  system knows.
- Unique-violation errors are translated in the infrastructure layer into the
  application layer's `ConflictException`, so a duplicate key surfaces as a
  conflict rather than an unhandled database error.

### Alternatives Considered

**Attributes on the entity.** Fewer files, but it makes the domain reference EF
Core and lets storage concerns accumulate on the model.

**Serializing `Money` to JSON or text.** Rejected: it makes the amount opaque to
SQL, so the database can neither sum balances nor enforce that one is
non-negative.

**Leaving all invariants to the domain.** Rejected for the ones a constraint can
express. The domain protects against defects in this codebase; the constraint
also protects against a bad migration, a second service, or a person at a `psql`
prompt. That is worth having for the rules this system exists to guarantee.

### Consequences

- `Account` gained a private parameterless constructor, because an
  object-relational mapper rebuilds an object from a row and cannot pass an
  owned value through a constructor. This is the one concession to persistence
  in the domain. It is private, so `Open`, `Credit` and `Debit` remain the only
  routes in, and it introduces no dependency.
- The currency check constraint means adding a currency requires a migration.
  For a ledger that is arguably correct — adding a currency is a schema-level
  event that deserves review — but it is a real cost.
- The non-negative check encodes a *wallet's* rule. Introducing an overdraft or
  credit account means revisiting it rather than working around it.

---

## ADR-004

### Title

Money is `decimal` in code and `numeric` in PostgreSQL; currency is stored as
its ISO 4217 alpha code.

### Status

Accepted.

### Context

Monetary amounts must be exact. Binary floating point cannot represent most
decimal fractions, so errors accumulate across entries and equality comparisons
become unreliable — in a double-entry ledger that means the books stop
balancing, silently.

Separately, the currency needs a database representation that will still mean
the same thing in ten years.

### Decision

- `Money.Amount` is a `decimal`, stored in a `numeric(19,4)` column.
- Scale 4 rather than the 2 that every currency currently in the enum uses.
- `Currency` is stored as its three-letter ISO 4217 alpha code in a
  `character varying(3)` column, via an EF Core value converter.

### Alternatives Considered

**`double`.** Rejected outright: `0.1 + 0.2 != 0.3`.

**Integer minor units (a `long` of cents).** Makes precision errors structurally
impossible and is the better choice for a system whose clients are written in
languages without a decimal type. Rejected for now because `decimal` keeps the
domain code and its tests readable, and maps directly onto PostgreSQL's exact
`numeric`. Worth revisiting if a Go or JavaScript client ever consumes these
values, since JSON numbers are doubles.

**Scale 2, matching today's currencies.** Rejected: the Kuwaiti dinar has three
decimal places, and widening a numeric column on a large table later is a
migration nobody wants to run. The extra headroom permits nothing new, because
the domain remains the authority on what precision each currency may be paid in.

**Storing the enum's numeric value.** Rejected in favour of the alpha code,
which stays readable to an auditor and portable to every external system that
speaks ISO 4217, for the cost of two bytes. Crucially, neither option is the
enum's *ordinal position*: the enum members carry explicit ISO numeric values, so
reordering them cannot change the meaning of stored rows.

### Consequences

- Amounts round-trip exactly, including values chosen to break floating point.
- The currency column is human-readable in `psql` and in a database dump.
- A currency added to the enum must also be added to the check constraint in a
  migration.

---

## ADR-005

### Title

Double-entry ledger with system accounts, signed amounts, and a single aggregate
that owns every balance change.

### Status

Accepted.

### Context

A wallet service has to record why every balance is what it is, and prove that no
operation created or destroyed money. Recording a deposit as a credit to a wallet
cannot do that: the money arrives from nowhere, the ledger cannot sum to zero, and
the strongest assertion the system could make about itself becomes unavailable.

The rule that entries must balance spans several entries, which is the definition
of an aggregate boundary. Left outside one, it holds only where a caller
remembers it.

### Decision

- **Every movement has a counterparty.** A deposit debits a system settlement
  account and credits a wallet; a withdrawal is the mirror. Wallets may never go
  negative; system accounts are expected to, and the amount is meaningful — it is
  how much value the platform has issued.
- **Signed amounts in one column.** Positive increases the account's balance,
  negative decreases it. The invariant is then `SUM(amount) = 0` per transaction.
- **`LedgerTransaction` is an aggregate root and the only thing that can move a
  balance.** `LedgerEntry` has no public constructor, and `Account.Credit` and
  `Account.Debit` are `internal` to the domain. No sequence of public calls
  anywhere in the solution produces a lone entry, or a balance change without one.
- **Corrections are reversals.** A correcting transaction negates the original;
  nothing is edited or deleted, and a transaction may be reversed at most once.
- **One currency per transaction**, enforced structurally by composite foreign
  keys from each entry to its account's currency and its transaction's.
- **Domain events** are plain records raised by the aggregate. Infrastructure
  drains them when saving, which is where the outbox will write its messages —
  inside the same transaction, so "the money moved" and "the world was told"
  commit together.
- The balance stays stored, as ADR-001 decided, but **the entries are now the
  source of truth** and the balance is a projection of them written in the same
  transaction. Where they disagree the entries win, and a reconciliation query
  proves they agree.

### Alternatives Considered

**Separate debit and credit columns**, the classic accounting presentation.
Rejected: it turns the balance check into a comparison of two aggregates, breaks
the composability of negation for reversals, and admits rows with both columns
populated or neither.

**Single-sided deposits, with no system accounts.** Much smaller, and rejected
outright: the global zero-sum invariant would be unassertable, which removes the
one test that proves the ledger is correct.

**Validating the balance in a service rather than an aggregate.** Rejected: the
invalid state stays representable, and every new code path is another chance to
forget the check.

**A full chart of accounts with asset/liability nature.** This is a wallet ledger,
not a general ledger for statutory reporting. Adding an account nature later is
additive; the entries themselves would not change.

### Consequences

- Money can be proven neither created nor destroyed, by summing one column.
- Setting up a test balance now requires a real deposit, because conjuring one is
  no longer possible. Three existing tests had to change, which is the point.
- Every currency needs a settlement account before it can be used; these are
  seeded by migration, because a deposit is impossible without one.
- Foreign exchange will need the composite foreign keys relaxed and a per-currency
  balance check in their place.

---

## ADR-006

### Title

Pessimistic row locks, acquired in a deterministic order, under READ COMMITTED.

### Status

Accepted. Extended by ADR-010, which records what this lock order means for
throughput, as measured.

### Context

Two concurrent withdrawals from an account holding just enough for one must not
both succeed. A domain invariant cannot prevent that: it holds within one process
and says nothing about two requests arriving in the same millisecond.

### Decision

Every ledger use case runs inside an explicit database transaction, and takes
`SELECT ... FOR UPDATE` on each account it will change **before** reading any
balance. Locks are acquired one row at a time, sorted by identifier. Isolation
stays READ COMMITTED.

The lock is issued as a statement of its own, and the entity is read afterwards.
Composing `FOR UPDATE` into a query the ORM then shapes was observed not to hold
the lock in practice: concurrent transactions read the same stale balance and the
projection drifted from the ledger. Locking and reading as two explicit steps
removes the ambiguity, and the repository refuses to lock at all when no
transaction is open, because a lock taken in autocommit is released before the
caller can use it.

### Alternatives Considered

**SERIALIZABLE isolation.** Correct, but it buys protection against phantoms we do
not have — the rows the decision depends on are known and can be locked directly —
and it costs a retry loop on every operation.

**Optimistic concurrency with a version column.** Better under low contention, but
a lost update here is a customer's money being wrong, and under contention it
degrades into a livelock of retries. PostgreSQL's `xmin` can provide this later
with no schema change if the trade-off shifts.

**Locking in argument order.** Rejected: a transfer A to B and a transfer B to A
would each hold what the other wants, and PostgreSQL would resolve it by killing
one. Sorting makes the deadlock impossible rather than merely unlikely.

### Consequences

- Concurrent withdrawals serialise; exactly as many succeed as there are funds for.
- Every movement on a busy account queues behind the others. Balance sharding is
  the known answer if that ever matters, and it is not needed yet.
- Deposits and withdrawals in one currency all contend on that currency's
  settlement account, which is the hottest row in the system by construction.

---

## ADR-007

### Title

Idempotency through a unique key on the transaction, checked inside the same
transaction that moves the money.

### Status

Accepted. Amended in Phase 7: the request fingerprint now includes the accounts and
direction of every leg (see the amendment at the end of this record).

### Context

A client whose request times out will retry, and must not be charged twice. The
check and the money movement have to be one atomic act: a check performed
separately is a race two retries can both pass.

### Decision

`ledger_transactions.idempotency_key` carries a partial unique index. A use case
looks for an existing transaction under the key **after** acquiring its account
locks, and returns that transaction's result if it finds one. A replay is a
success that returns the original result, not a conflict.

Requests with the same key necessarily touch the same accounts, so the locks
serialise them and the second one's read sees the first one's committed
transaction. The unique index remains the ultimate authority if that check is ever
bypassed.

A key reused with different parameters is rejected with a conflict rather than
being given the original's result, which would report success for an operation
that never happened.

### Alternatives Considered

**A separate `idempotency_keys` table storing serialized responses.** More
general, and the right addition when the HTTP layer needs a retry to return the
same *body* — including for requests that failed validation. Rejected as the core
mechanism because the guarantee that matters is atomicity with the money
movement, which a unique index on the transaction gives directly.

**Checking before taking the locks.** Rejected: it leaves a window in which both
attempts find nothing and both proceed.

### Consequences

- A retried request moves money exactly once, proven under real concurrency.
- Because the outbox will be written in the same transaction, a deduplicated retry
  emits no second event; idempotency and at-least-once delivery compose without
  extra machinery.
- The request fingerprint is deliberately small — kind, currency and amount. A
  fuller one would hash the whole request and store it beside the key.

### Amendment (Phase 7)

A Phase 7 audit found that the fingerprint above did not keep this record's promise
that a key reused with different parameters is refused. It compared only the kind,
the currency and the amount, so a deposit of the same amount into a *different*
wallet under a used key was answered with 200 and the first wallet's transaction:
the caller was told its money had arrived when none had moved. The same held for a
transfer between the same two wallets in the opposite direction, and a reversal
request replayed the result stored under its key whatever that result was.

The fingerprint is now the kind of movement plus every leg the request names — each
account and the signed amount it moves — and a stored transaction matches only if it
contains all of them. A replayed reversal must be a reversal of the same original.
Anything else is refused with 409. The mechanism, the locking and the unique index are
unchanged; only the comparison made after the lookup is stricter.

---

## ADR-008

### Title

Application observability: Serilog for logs, OpenTelemetry for traces and
metrics, Prometheus for scraping.

### Status

Accepted. The Prometheus and Grafana deployment this record leaves out of scope was
added later and is recorded in ADR-011.

### Context

A ledger is investigated after the fact. "Why did this transfer fail at 03:14",
"is the withdrawal path slower than it was last week", and "is this instance
serving traffic it cannot complete" are the questions that matter, and none of
them can be answered by reading console output. They need logs that can be
queried, traces that connect a request to the database command it waited on, and
metrics that can be aggregated over time.

The constraint that shapes the design is that this is financial data. Telemetry
leaves the service and lands in systems with wider access than the database:
whoever can read a dashboard can usually read every label on it. So the question
is not only what to measure but what must never be measured.

### Decision

**Serilog for logging.** Log events keep their properties as data rather than
being flattened into a sentence, which is what makes "every refused withdrawal on
this account today" a filter rather than a grep. Output is compact JSON in
production and human-readable text in development, both chosen by configuration.
Serilog 3 populates `TraceId` and `SpanId` from the ambient `Activity`, so logs
and traces share identifiers with nothing bespoke in between.

**OpenTelemetry for traces and metrics.** It is the vendor-neutral standard, so
the exporter is a deployment decision rather than a rewrite. Crucially, the
application layer does not depend on it: it emits signals through
`System.Diagnostics.ActivitySource` and `Meter` — base class library types —
and the composition root decides who listens. OpenTelemetry appears in exactly
one project.

**Instrumentation lives at the composition root**, except for the ledger's own
signals, which live in the application layer because they describe application
concepts. `LedgerTelemetry` is injected, not static, so a test can isolate an
instance and nothing depends on ambient global state.

**Prometheus for scraping**, exposed at `/metrics` by the OpenTelemetry exporter.

**Two health endpoints with different meanings.** `/health/live` checks nothing
external; `/health/ready` checks PostgreSQL through the same `DbContext` the
application serves requests with.

**One owner for exception logging.** ASP.NET Core's `ExceptionHandlerMiddleware`
logs every exception it routes at `Error` with a stack trace, before anything has
decided what the exception means. That logger is silenced, and
`GlobalExceptionHandler` takes the responsibility: unexpected failures at `Error`
with the whole exception, expected refusals at `Debug` with only a status and a
title.

### Alternatives Considered

**The built-in `ILogger` console provider.** No third-party dependency, but it
renders a message and discards the structure, which is the property the whole
decision rests on.

**`prometheus-net` instead of the OpenTelemetry exporter.** Simpler for metrics
alone, and rejected because it would leave traces and metrics on different
models, with two ways to name a dimension and no shared resource identity.

**Emitting metrics from the API endpoints rather than the use cases.** Would keep
the application layer entirely free of instrumentation, and rejected because it
measures the wrong thing: an operation invoked from anywhere but HTTP would be
invisible, and the endpoint does not know why an operation failed.

**A custom correlation identifier.** Rejected outright. ASP.NET Core already
creates a W3C trace context per request, propagates it, and puts it on every log
event; a parallel scheme would be a second answer to a question that already has
one.

**Logging the ledger operation's amount and balance.** Rejected. The client is
told the amounts because it is their money; a log is a different audience.

### Consequences

- A trace runs from the HTTP request, through the ledger operation, into the
  PostgreSQL command, which is what turns "this was slow" into "this waited on a
  row lock".
- Metric labels are a closed set. An unrecognised currency collapses to
  `unknown` rather than becoming a label, so a caller cannot create unbounded
  time series by sending nonsense.
- No metric carries an account identifier, a transaction identifier or an
  idempotency key. Spans carry identifiers, because that is how a specific
  failure is found; they carry no amounts.
- Silencing the framework's exception logger means that if this service's own
  handler ever fails, its log is lost too. The exception still propagates and is
  recorded by the server.
- The Prometheus exporter is a pre-release package. It is the official one, and
  the alternative was a second metrics model.
- Deploying Prometheus, Grafana and a collector is deliberately not part of this
  phase. The application now produces the signals; where they are stored and
  displayed is an infrastructure concern with its own failure modes, and mixing
  the two would mean neither is finished.

### Metrics reference

| Metric | Type | Unit | Labels | Meaning |
| --- | --- | --- | --- | --- |
| `ledger.transactions` | Counter | transactions | `ledger.operation`, `ledger.currency`, `ledger.outcome` | Every ledger operation attempted, whether it succeeded or was refused. Rate and error ratio per operation. |
| `ledger.transaction.failures` | Counter | transactions | `ledger.operation`, `ledger.failure_reason` | Refusals broken down by cause, from a closed set. Answers *why* the failure rate moved. |
| `ledger.transaction.duration` | Histogram | seconds | `ledger.operation`, `ledger.outcome` | End-to-end time of an operation, including waiting for row locks. Latency percentiles and lock contention. |
| `ledger.accounts.opened` | Counter | accounts | `ledger.currency` | Accounts opened. Growth, and a sanity check against the accounts table. |

`ledger.operation` is one of `deposit`, `withdrawal`, `transfer`, `reversal`.
`ledger.outcome` is `success` or `failure`. `ledger.currency` is an ISO 4217
code or `unknown`. `ledger.failure_reason` is one of a fixed list including
`validation`, `insufficient_funds`, `conflict`, `not_found`,
`currency_mismatch`, `already_reversed` and `error`.

Standard HTTP, ASP.NET Core and .NET runtime metrics come from the OpenTelemetry
instrumentation packages and are not redefined here.

---

## ADR-009

### Title

Transactional outbox for messaging, with at-least-once delivery.

### Status

Accepted.

### Context

Other systems need to know when the ledger records a transaction. The obvious
implementation — commit the financial change, then publish to the broker — is a
**dual write**, and it has no correct failure mode:

- crash after the commit and before the publish, and the money has moved but
  nothing downstream will ever learn of it;
- publish before the commit, and a rollback leaves the world told about a
  transaction that never happened;
- wrap the broker call inside the database transaction, and a slow broker holds
  account row locks open, so a messaging problem becomes a financial outage.

There is no ordering of two independent systems that makes this atomic.

### Decision

The intent to publish is written **into the same database transaction** as the
money:

```text
BEGIN
  ledger_transactions + ledger_entries + account balances
  outbox_messages
COMMIT                         <- one atomic act

  ... later, and separately ...

claim  -> publish -> mark published
```

`UnitOfWork.SaveChangesAsync` turns the domain events raised during the unit of
work into `outbox_messages` rows before it saves, so they are part of the same
statement batch and the same transaction. Either both are committed or neither
is.

A background `OutboxPublisher` then drains the table. Claiming is one statement
that commits immediately:

```sql
UPDATE outbox_messages
   SET attempts = attempts + 1, next_attempt_at = now() + lease
 WHERE id IN (SELECT id FROM outbox_messages
               WHERE published_at IS NULL AND next_attempt_at <= now()
               ORDER BY id LIMIT :batch
                 FOR UPDATE SKIP LOCKED)
RETURNING ...;
```

`SKIP LOCKED` lets several workers drain the same table without contending, and
pushing `next_attempt_at` forward leases the row: no other worker takes it while
this one publishes, and if the worker dies the lease simply expires. **No
database transaction is held open while waiting for the broker.**

Publication uses RabbitMQ publisher confirms. A message is marked published only
after the broker has confirmed it.

### Delivery semantics: at-least-once, and not more

The sequence is *claim and commit → publish and confirm → mark published*. A
crash in the window between the broker confirming and the mark committing leaves
the row pending, and the message is published again once its lease expires.

**This is unavoidable.** Closing the window would require the broker and the
database to commit together, which they cannot. Delivery is therefore
at-least-once, and **exactly-once is not claimed anywhere**.

Consumers must be idempotent. Every message carries a `MessageId` derived from
the ledger transaction identifier rather than generated randomly, so the same
transaction always produces the same message identifier — which is what makes a
repeat recognisable. A test reproduces the duplicate window deliberately, because
it is a property to be designed against rather than a bug to be fixed.

### Retry behaviour

A failed attempt records the error, increments the attempt count and schedules
the next try with exponential backoff, capped. A failing batch stops after the
first failure: if the broker is down the rest would fail too, and burning their
attempt counts and backoff for nothing only slows the eventual recovery.

### Alternatives Considered

**Publish directly from the use case.** Simplest, and rejected: it is exactly the
dual write above.

**Change data capture from the WAL.** Removes the publisher entirely and gives
stronger ordering guarantees, at the cost of another piece of infrastructure to
run and understand. Worth revisiting at a volume this service is nowhere near.

**A distributed transaction across PostgreSQL and RabbitMQ.** Rejected. Two-phase
commit is available in principle and is operationally miserable, and RabbitMQ's
support for it is not something to depend on.

**Holding `SELECT … FOR UPDATE` open across the publish.** Simpler code, and
rejected because it couples database lock duration to broker latency.

### Consequences

- A broker outage never rolls back a financial transaction. Transactions keep
  committing with their outbox rows; the backlog grows and drains on recovery.
  This is verified by a test and by hand against the Compose stack.
- RabbitMQ is therefore **not** a hard readiness dependency. Nor is Redis, which
  nothing reads or writes. Both are registered as *degradable*: readiness reports
  `Degraded` and still returns 200, because an instance that refused traffic for
  either would be refusing requests it can serve correctly.
- The outbox table grows without bound. Nothing prunes published rows yet; that
  is a maintenance job, and it needs a retention decision first.
- Message payloads carry identifiers, a kind and a currency — **never amounts or
  balances**. A broker fans messages out to every service with a binding, and
  they sit in backlogs and dead-letter queues indefinitely.
- Ordering is per-message, not global. Nothing guarantees a consumer sees two
  transactions in the order they were committed, only that it eventually sees
  both.

---

## ADR-010

### Title

The settlement account stays locked first, and the per-currency settlement row is
accepted as the write-throughput limit.

### Status

Accepted.

### Context

Phase 6 measured the service under load; the method and every number are in
`docs/PERFORMANCE.md`. The finding that matters here: every deposit and every
withdrawal in a currency must lock that currency's settlement account (ADR-005,
ADR-006), so they all queue on one row.

- Deposits in one currency level off at roughly 145–155 per second from 10
  concurrent clients upward, and fall to about 118 per second at 100–200.
- Spreading the same load over two currencies — two settlement rows — gives 1.9
  times the throughput. Transfers, which touch no system account, reach about 860
  per second on the same machine.
- 97.1 % of all database statement time is spent in `SELECT … FOR UPDATE`, almost
  all of it waiting. The WAL flush that makes a commit durable averages 0.7 ms,
  about a tenth of the time the row is held.

The seeded settlement accounts have the lowest identifiers, so under ADR-006's
ascending order they are locked first, and the settlement row is held for eight
of the eleven round trips a deposit makes.

### Decision

The lock order stays as ADR-006 states it: ascending identifier. For deposits and
withdrawals that means the settlement row is locked first. That is now a measured
property rather than an accident of seeding, and `AccountRepository` points here.

The single settlement row per currency is accepted as the limit on deposits and
withdrawals at this stage. No change is made to the chart of accounts.

What the transaction does while it holds the row is the lever that works. EF Core
wraps `SaveChanges` inside an explicit transaction in a savepoint, so that a caller
can recover from a failed save and carry on; nothing in this service ever does, so
the savepoint was two round trips inside the locked window with no purpose.
`UnitOfWork` now disables it. A deposit makes nine round trips instead of eleven,
six of them while the settlement row is held instead of eight. Measured three
repetitions against three: deposit throughput +13 % at 50 concurrent clients, with
p99 latency roughly halved, and +28 % at 200; transfers, which are not limited by
one row, unchanged. `LedgerRoundTripTests` fails if the savepoint returns.

### Alternatives Considered

**Lock the settlement account last.** Customer accounts first, system accounts
last, identifier order within each group, the group read from the immutable
`account_type` before locking. Still a total order, so still deadlock-free. The
hypothesis was that a shorter hold would mean more throughput. Built, tested
(including a database-level test that failed against the old order), and measured
three times against three on fresh stacks:

- deposits at 50 clients: −19.9 % throughput, p99 from 1.4 s to 7.7 s;
- deposits at 10 clients: −16.2 %, p99 from 87 ms to 1.5 s;
- transfers: −4.8 %, from the extra read.

Diagnostics ruled out the obvious explanation, a convoy on shared wallets — ten
times more wallets made the tail worse — and showed the new order to be far less
fair: some requests waited up to 29 seconds where the existing order's longest
sampled wait was 3.5 seconds. Why was not established. Rejected and reverted.

**Shard the settlement account.** Several settlement accounts per currency, each
movement using one. The two-currency run is direct evidence that it would scale
roughly with the number of rows. Not done now: it changes the chart of accounts,
reporting the settlement position means summing the shards, and nothing yet needs
more than the measured rate. It is the next step when one currency must take more
deposits.

**Buy throughput with correctness.** `synchronous_commit = off`, optimistic
concurrency with retries, or not locking the settlement account at all. Rejected
without measurement. The first could lose committed money — and the flush is a
tenth of the hold time, so it would buy little. The others let two movements act
on a stale settlement balance.

### Consequences

- On the reference machine one currency takes about 165 deposits and withdrawals
  a second with the savepoint removed (about 145 before). Latency grows in
  proportion to the number of concurrent writers in that currency, and at 200 of
  them throughput is lower, about 151 a second.
- Any statement added inside a ledger transaction is paid for by every other
  movement waiting on the same row. New round trips there should be measured, not
  assumed to be cheap.
- Changing the identifiers of system accounts, or the lock order, changes
  performance as well as correctness and must be re-measured.
- Wallet identifiers are chosen by clients. A wallet whose identifier sorts below
  its settlement account would have the settlement row locked after it. That is
  still a total order and still correct; its effect on that wallet's deposits was
  not measured.
- Settlement sharding is the documented lever for more per-currency write
  throughput.

---

## ADR-011

### Title

The service ships as one non-root image, runs locally as a Compose stack with a
one-shot migrator, and is given Redis without giving Redis a role.

### Status

Accepted. Recorded retrospectively in Phase 7: these decisions were made when the
production infrastructure was added, and no record captured them at the time.

### Context

The ledger has to run somewhere other than a developer's IDE, alongside PostgreSQL,
a broker, and something that scrapes its metrics — and a reviewer has to be able to
start all of it with one command. Several choices in that environment affect
correctness rather than convenience: when migrations run, which dependencies can
stop the service from serving, and whether infrastructure that exists invites being
used for things it must not hold.

### Decision

- **One image for the API and the migrator.** A multi-stage Dockerfile builds with
  warnings as errors and runs on the ASP.NET runtime image as a dedicated
  unprivileged user. No connection string or credential is baked in.
- **Migrations run once, before the API, from that image** (`--migrate`). Compose
  starts the API only after the migrator exits successfully. Replicas never migrate
  on start-up, because EF Core takes no lock that would make concurrent migrations
  safe.
- **PostgreSQL is the only hard dependency.** Readiness fails (503) without it.
  RabbitMQ and Redis are *degradable*: readiness reports `Degraded` and still returns
  200, because an instance that cannot reach them can still move money correctly
  (ADR-009). Liveness checks nothing external.
- **Redis is provisioned, connected and health-checked, and nothing reads or writes
  it.** It runs without persistence. No balance, idempotency record or lock is kept
  there, because a cache that holds financial state is how a stale value becomes
  authoritative. It exists so a future feature with a legitimate need — rate
  limiting, for example — has the infrastructure ready.
- **Prometheus and Grafana are part of the stack**, provisioned from files in
  `deploy/`, so the dashboard exists on first start and survives `down -v`.
- **Local exposure is minimal by default.** Host ports are unconventional (Windows
  reserves ranges containing 5432 and 5672) and every one binds to 127.0.0.1 unless
  `LEDGER_BIND_ADDRESS` says otherwise, because the development credentials are
  well known and Redis has no password. Every port and credential is an environment
  variable with a development default; nothing in the repository is a secret.
- **CI proves what the repository claims**: a Release build with warnings as errors,
  every test with Docker required and a check that nothing was skipped, an image that
  starts and passes liveness without a database while running as a non-root user,
  and a valid Compose file.

### Alternatives Considered

**Migrating on API start-up.** Simpler, and unsafe with more than one replica.

**Making RabbitMQ a hard readiness dependency.** Rejected: it would turn a broker
outage into a financial outage, which the outbox exists to prevent.

**Using Redis for idempotency keys or as a balance cache.** Rejected. The idempotency
check must be atomic with the money movement, which only the database transaction
gives; a cached balance would be a second, weaker source of truth.

**Leaving Redis out until something needs it.** Defensible. It was provisioned so the
environment and its health semantics are in place, at the cost of one container that
does nothing yet — a cost the documentation states plainly.

**Publishing ports on every interface.** Docker's default, and rejected for a stack
with a passwordless Redis and default credentials.

### Consequences

- `docker compose up -d --build` starts a complete, observable environment from a
  clean clone.
- A reader must not infer from Redis's presence that anything is cached; the README,
  the architecture document and the health check description all say it has no role.
- The Compose stack is a development environment, not a deployment. It has no TLS,
  no secrets management, no authentication in front of the API, and the application
  connects to PostgreSQL as a superuser.
- The image build is exercised on every push, but no image is published.

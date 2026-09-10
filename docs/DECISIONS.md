# Architecture Decision Records

This document records the significant architectural decisions taken in this
project, why they were taken, and what they cost us.

Each decision gets its own numbered section. Records are append-only: once a
decision has been made, it is not rewritten. If we change our mind later, we add
a new ADR and mark the old one as `Superseded by ADR-XXX`.

**Status** is one of: `Proposed`, `Accepted`, `Superseded by ADR-XXX`, `Deprecated`.

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

Accepted.

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

Accepted.

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

---

## ADR-008

### Title

Application observability: Serilog for logs, OpenTelemetry for traces and
metrics, Prometheus for scraping.

### Status

Accepted.

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

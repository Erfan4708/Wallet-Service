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

Accepted, and expected to be revisited when ledger entries exist.

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

Accepted.

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

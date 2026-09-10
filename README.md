# ledger-core

A double-entry **ledger / wallet core service** built as a study in backend
correctness: accounts, balances, transfers, and an append-only ledger that stays
financially consistent under concurrent load.

The interesting part of this project is not the API surface — it is atomicity,
concurrency control, idempotency and auditability. There is deliberately no UI.

## Status

🚧 **In progress.** A double-entry ledger records every movement, accounts
persist to PostgreSQL through EF Core, and deposits, withdrawals, transfers and
reversals are exposed over HTTP, and the service reports on itself through
structured logs, traces and Prometheus metrics. **Not yet built:** an outbox and
message broker, a deployed metrics stack, and CI.

| Area | Status |
| --- | --- |
| Solution & layer scaffolding | ✅ Done |
| Money, Currency, Account | ✅ Done |
| Application boundaries (use cases, abstractions, errors) | ✅ Done |
| PostgreSQL + EF Core persistence | ✅ Done |
| Integration tests (Testcontainers) | ✅ Done |
| Double-entry ledger, transfers, reversals | ✅ Done |
| Concurrency control & idempotency | ✅ Done |
| HTTP endpoints | ✅ Done |
| Observability (logs, traces, metrics, health) | ✅ Done |
| Outbox + message broker | ⬜ Not started |
| Prometheus / Grafana deployment | ⬜ Not started |
| CI | ⬜ Not started |

## Architecture

Four layers, with dependencies pointing inwards towards the domain:

```text
Ledger.Api             HTTP / REST composition root
    │
    ├──────────────► Ledger.Infrastructure   PostgreSQL, EF Core, external services
    │                       │
    └──────────────► Ledger.Application      use cases: create account, deposit,
                            │                withdraw, transfer
                            ▼
                    Ledger.Domain            entities, value objects, invariants
                                             (no framework dependencies)
```

### Why the domain depends on nothing

`Ledger.Domain` has no project references and no NuGet packages, and is intended
to stay that way: it knows nothing about ASP.NET Core, EF Core, PostgreSQL or
HTTP.

That is not purity for its own sake. The rules this service exists to enforce —
a balance may not go negative, currencies may not be mixed, entries must balance
— change for business reasons. A database schema, an ORM and a web framework
change for entirely unrelated ones. Keeping them in separate projects, with the
compiler enforcing the direction, means an infrastructure change *cannot*
silently alter a business rule, and the rules can be tested in milliseconds with
no database running.

### Dependency inversion

When the application layer needs something from the outside world, it declares
the interface it needs and infrastructure implements it:

```text
Ledger.Application   defines  IAccountRepository, IUnitOfWork
        ▲
        │ implements
Ledger.Infrastructure         (EF Core / PostgreSQL — not built yet)
```

The arrow points inwards even though the data flows outwards. `Ledger.Api` is
the composition root and the only project that knows every layer exists.

### Persistence

The application layer never names a database. It declares what it needs, and the
infrastructure layer supplies it:

```text
Deposit / Withdraw / Transfer / Reverse    application: knows only the interfaces
        │
        ▼
IAccountRepository, IUnitOfWork            application: declares what it needs
        ▲
        │ implemented by
AccountRepository, UnitOfWork               infrastructure
        │
        ▼
LedgerDbContext (EF Core)                   infrastructure: owns all mapping
        │
        ▼
PostgreSQL                                  the source of truth for account state
```

PostgreSQL holds the persisted state of every account. `LedgerDbContext`, the
entity configurations and the migrations all live in `Ledger.Infrastructure`, and
no EF Core type is visible from any other project — the repository and unit of
work are `internal`, so the only way to reach them is through the interfaces the
application layer owns.

Three tables: `accounts`, `ledger_transactions` and `ledger_entries`. Money is
`numeric(19,4)` — exact base-10, never a floating point type — and currency is the
ISO 4217 alpha code rather than an enum ordinal.

The database enforces what it can, so the guarantees survive a bad migration, a
second service or a person at a `psql` prompt:

| Guarantee | Enforced by |
| --- | --- |
| A transaction's entries sum to zero, with at least two legs | deferred constraint trigger, at `COMMIT` |
| Entries are append-only | trigger rejecting `UPDATE`/`DELETE` |
| An entry matches its account's and transaction's currency | composite foreign keys |
| A wallet balance is never negative; a system account may be | conditional check constraint |
| A retry cannot move money twice | partial unique index on the idempotency key |
| A transaction is reversed at most once | partial unique index |

Concurrency is handled with `SELECT … FOR UPDATE` taken in identifier order
before any balance is read, under READ COMMITTED. The reasoning for all of this is
in [ADR-005, ADR-006 and ADR-007](docs/DECISIONS.md).

### Observability

The service emits three signals, all correlated by the W3C trace identifier that
ASP.NET Core creates for each request:

| Signal | How | Where it goes |
| --- | --- | --- |
| Logs | Serilog, compact JSON in production | stdout |
| Traces | OpenTelemetry: ASP.NET Core, HttpClient, Npgsql, plus ledger spans | console exporter in development |
| Metrics | OpenTelemetry: HTTP, runtime, plus ledger metrics | `GET /metrics`, Prometheus text format |

A trace runs from the HTTP request, through the ledger operation, into the
PostgreSQL command it waited on. The same trace identifier appears on every log
event (`@tr`), in the `trace-id` response header, and in the `traceId` field of
every error response — so a caller can quote one value and it will find the
request in all three.

| Endpoint | Purpose |
| --- | --- |
| `GET /health/live` | Liveness. Checks **nothing external** — a database outage must not make an orchestrator restart every instance. |
| `GET /health/ready` | Readiness. Checks PostgreSQL through the application's own `DbContext`; returns 503 when it is unreachable. |
| `GET /metrics` | Prometheus scrape endpoint. |

```bash
curl -i localhost:18080/health/live      # 200, and a trace-id header
curl    localhost:18080/health/ready     # 200 healthy, 503 with checks:{postgres:Unhealthy}
curl -s localhost:18080/metrics | grep ledger_
```

**What is deliberately not measured.** No metric carries an account identifier, a
transaction identifier or an idempotency key — one time series per account is
unbounded growth. An unrecognised currency collapses to `unknown` rather than
becoming a label of its own. Spans carry identifiers, because that is how a
specific failure is found, but never amounts or balances. Custom metrics are
listed in full in [ADR-008](docs/DECISIONS.md).

Everything is configurable under `Serilog` and `Observability` in
`appsettings.json`: minimum log levels, service name, sampling ratio, and whether
each exporter runs.

### Error contract

Every unhandled exception becomes an RFC 9457 `ProblemDetails` response through
a single `IExceptionHandler`, so the contract is consistent across endpoints
that do not exist yet:

| Exception | Status | Meaning |
| --- | --- | --- |
| `ValidationException` | 400 | the request was never well-formed |
| `NotFoundException` | 404 | the request was fine; the thing is not there |
| `ConflictException` | 409 | conflicts with current state (later: concurrency) |
| `TransactionAlreadyReversedException` | 409 | the request was sound; the world moved |
| `DomainException` | 422 | well-formed, but a business rule refused it |
| anything else | 500 | a defect — logged in full, reported without detail |

Stack traces, exception types and internal messages are never returned to a
client; they go to the log.

## Domain concepts

- **Money** — an immutable amount in one currency; exact (`decimal`), compared by
  value, and refuses to combine currencies or to hold an amount more precise
  than the currency can be paid in.
- **Account** — an entity owning a balance. A *wallet* holds a customer's money
  and may never go negative; a *system* account is the ledger's counterparty with
  the outside world and is expected to.
- **Ledger entry** — one signed movement on one account, immutable, append-only.
- **Ledger transaction** — a balanced set of entries, and the only thing in the
  system that can move a balance.

### Why a deposit has two sides

Crediting a wallet on its own would create money from nothing. A deposit debits a
settlement account and credits the wallet, so every transaction sums to zero and
the whole ledger does too:

```text
deposit 100 EUR    SETTLEMENT:EUR  -100.00      wallet  +100.00     → sums to 0
transfer 30 EUR    wallet A         -30.00      wallet B  +30.00    → sums to 0
```

That is what makes the system's strongest claim testable: **`SUM(ledger_entries.amount) = 0`
across the entire database**, asserted after every integration test. The
settlement account's negative balance is not an error — it is how much value the
platform has issued, and what reconciles against the bank rail.

`Account.Credit` and `Account.Debit` are `internal` to the domain, reachable only
from the transaction aggregate. A balance therefore cannot move without entries
recording why — enforced by the compiler, not by convention.

## Technology stack

- C# / .NET 8 (LTS)
- ASP.NET Core Web API
- PostgreSQL
- Entity Framework Core
- xUnit
- Testcontainers
- Docker / Docker Compose
- GitHub Actions

PostgreSQL, EF Core, Testcontainers, Docker and CI are planned; they are not
wired up yet.

## Repository layout

```text
src/
  Ledger.Domain/          domain model (Entities, ValueObjects, Exceptions, Enums, Common)
  Ledger.Application/     use cases, abstractions the outer layers implement, app errors
  Ledger.Infrastructure/  EF Core DbContext, entity configuration, migrations, repositories
  Ledger.Api/             HTTP host, composition root, error contract, observability
tests/
  Ledger.Domain.Tests/
  Ledger.Application.Tests/
  Ledger.Infrastructure.Tests/  real PostgreSQL via Testcontainers
  Ledger.Api.Tests/
docs/
  DECISIONS.md            architecture decision records
```

## Getting started

```bash
dotnet build
dotnet test
```

Requires the .NET 8 SDK; the exact version is pinned in `global.json`.

The integration tests start their own throwaway PostgreSQL container through
Testcontainers, so **they need a running Docker daemon**. Without one they report
as skipped rather than failing. Set `LEDGER_REQUIRE_DOCKER=1` to turn a skip into
a failure, and set it in CI — if the tests that prove the PostgreSQL mapping do
not run, the mapping is unverified, and that should break the build rather than
pass quietly:

```bash
LEDGER_REQUIRE_DOCKER=1 dotnet test
```

To run the API against a database, start one and supply a connection string:

```bash
docker compose up -d postgres
dotnet run --project src/Ledger.Api      # listens on http://localhost:18080
```

The development database is published on **port 55432**, not the conventional
5432. Windows reserves scattered TCP ranges for Hyper-V and WSL2, and 5432
commonly falls inside one; Docker then fails to bind it with a permissions error
that looks like a Docker problem but is not. `netsh interface ipv4 show
excludedportrange protocol=tcp` lists the reserved ranges. Set
`POSTGRES_PORT=5432` on a machine where that port is free.

`appsettings.Development.json` carries a local development connection string.
Anywhere else, `ConnectionStrings:LedgerDatabase` must come from the environment
(`ConnectionStrings__LedgerDatabase`) or a secret store — credentials are never
committed.

Schema changes are applied with migrations:

```bash
LEDGER_DESIGN_TIME_CONNECTION="Host=localhost;Port=55432;Database=ledger;Username=ledger;Password=ledger"   dotnet dotnet-ef database update --project src/Ledger.Infrastructure --startup-project src/Ledger.Infrastructure
```

## Architecture decisions

Significant decisions are recorded in [docs/DECISIONS.md](docs/DECISIONS.md).

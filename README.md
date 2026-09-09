# ledger-core

A double-entry **ledger / wallet core service** built as a study in backend
correctness: accounts, balances, transfers, and an append-only ledger that stays
financially consistent under concurrent load.

The interesting part of this project is not the API surface — it is atomicity,
concurrency control, idempotency and auditability. There is deliberately no UI.

## Status

🚧 **In progress.** The domain model for money and accounts is implemented and
tested, and the application/API boundaries are in place. **There is no
persistence and there are no HTTP endpoints yet** — nothing in this repository
talks to a database.

| Area | Status |
| --- | --- |
| Solution & layer scaffolding | ✅ Done |
| Money, Currency, Account | ✅ Done |
| Application boundaries (use cases, abstractions, errors) | ✅ Done |
| Ledger entries & transfers (double-entry) | ⬜ Not started |
| PostgreSQL + EF Core persistence | ⬜ Not started |
| HTTP endpoints | ⬜ Not started |
| Concurrency control & idempotency | ⬜ Not started |
| Integration tests (Testcontainers) | ⬜ Not started |
| Docker & CI | ⬜ Not started |

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

### Error contract

Every unhandled exception becomes an RFC 9457 `ProblemDetails` response through
a single `IExceptionHandler`, so the contract is consistent across endpoints
that do not exist yet:

| Exception | Status | Meaning |
| --- | --- | --- |
| `ValidationException` | 400 | the request was never well-formed |
| `NotFoundException` | 404 | the request was fine; the thing is not there |
| `ConflictException` | 409 | conflicts with current state (later: concurrency) |
| `DomainException` | 422 | well-formed, but a business rule refused it |
| anything else | 500 | a defect — logged in full, reported without detail |

Stack traces, exception types and internal messages are never returned to a
client; they go to the log.

## Domain concepts

Implemented:

- **Money** — an immutable amount in one currency; exact (`decimal`), compared by
  value, and refuses to combine currencies or to hold an amount more precise
  than the currency can be paid in.
- **Account** — an entity owning a balance that can only move through `Credit`
  and `Debit`, which enforce matching currency, positive amounts and a
  non-negative balance.

Not implemented yet:

- **Ledger entry** — an immutable, append-only record of a single movement.
- **Transfer** — moves money between two accounts as balanced debit/credit pairs.

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
  Ledger.Infrastructure/  persistence and external integrations (empty seam for now)
  Ledger.Api/             HTTP host, composition root, error contract
tests/
  Ledger.Domain.Tests/
  Ledger.Application.Tests/
  Ledger.Api.Tests/
docs/
  DECISIONS.md            architecture decision records
```

## Getting started

```bash
dotnet build
dotnet test
```

Requires the .NET 8 SDK. The exact SDK version is pinned in `global.json`.

## Architecture decisions

Significant decisions are recorded in [docs/DECISIONS.md](docs/DECISIONS.md).

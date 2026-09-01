# ledger-core

A double-entry **ledger / wallet core service** built as a study in backend
correctness: accounts, balances, transfers, and an append-only ledger that stays
financially consistent under concurrent load.

The interesting part of this project is not the API surface — it is atomicity,
concurrency control, idempotency and auditability. There is deliberately no UI.

## Status

🚧 **Bootstrap.** The repository, solution and layer boundaries exist. **No domain
logic, persistence, or HTTP endpoints have been implemented yet.**

| Area | Status |
| --- | --- |
| Solution & layer scaffolding | ✅ Done |
| Core domain (Money, Account, LedgerEntry, Transfer) | ⬜ Not started |
| Application use cases | ⬜ Not started |
| PostgreSQL + EF Core persistence | ⬜ Not started |
| HTTP API | ⬜ Not started |
| Concurrency control & idempotency | ⬜ Not started |
| Integration tests (Testcontainers) | ⬜ Not started |
| Docker & CI | ⬜ Not started |

## Planned architecture

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

`Ledger.Domain` has no project references and no third-party packages, and is
intended to stay that way: it should know nothing about ASP.NET Core, EF Core,
PostgreSQL or HTTP.

## Planned domain concepts

- **Money** — value object carrying an amount and a currency.
- **Account** — owns its balance and enforces its own balance rules.
- **Ledger entry** — an immutable, append-only record of a single movement.
- **Transfer** — moves money between two accounts as balanced debit/credit pairs.

These are described here as intent. None of them are implemented yet.

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
  Ledger.Application/     use cases
  Ledger.Infrastructure/  persistence and external integrations
  Ledger.Api/             HTTP host
tests/
  Ledger.Domain.Tests/
  Ledger.Application.Tests/
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

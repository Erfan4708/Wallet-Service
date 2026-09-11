# Domain model

The concepts the ledger is built from, and the rules that hold between them. The
model lives in `src/Ledger.Domain`, which has no project or package references: it
knows nothing about HTTP, EF Core or PostgreSQL. Many of the rules are enforced a
second time by the database, so they survive a bug in the application, a second
service, or a person at a `psql` prompt. Both layers are listed for each rule.

## Concepts

### Currency

An enumeration of the currencies the ledger can hold: `USD`, `EUR` and `IRR`. Each
member's underlying value is its ISO 4217 **numeric** code (840, 978, 364), so
reordering the enum can never change the meaning of stored data. Currencies are
stored and sent on the wire as their three-letter alpha code.

Each currency has a number of decimal places an amount may use
(`CurrencyExtensions.DecimalPlaces`); all three currently use two. The Iranian
*toman* is a colloquial unit of ten rials, not an ISO currency, and is not
modelled.

### Money

An immutable amount in exactly one currency (`ValueObjects/Money.cs`).

- The amount is a `decimal`, never a floating point type.
- An amount more precise than its currency allows cannot be constructed. Nothing
  is rounded silently.
- Amounts may be negative: a negative amount is the debit side of an entry.
- Adding, subtracting or comparing amounts of different currencies throws
  `CurrencyMismatchException`. Arithmetic that overflows throws rather than
  wrapping.
- Two amounts are equal when their currency and value are equal.

### Account

An entity with an identity, a type, a currency and a balance (`Entities/Account.cs`).
The identifier is supplied by the caller, so the domain never generates one.

- A **wallet** holds a customer's money. Its balance may never go negative.
- A **system account** is the ledger's counterparty with the outside world. Its
  balance may go negative, and the negative figure is meaningful: it is how much
  value has been issued into wallets. A system account has a `SystemKey`; a wallet
  never does.

An account's type never changes after it is opened.

`Account.Credit` and `Account.Debit` are `internal`, reachable only from
`LedgerTransaction`. No public code path can change a balance without writing the
ledger entries that explain the change.

### Settlement accounts

One system account per currency, keyed `SETTLEMENT:USD`, `SETTLEMENT:EUR` and
`SETTLEMENT:IRR`, created by a database migration because a deposit is impossible
without one. Their identifiers end in the currency's ISO numeric code, for example
`00000000-0000-0000-0000-000000000840` for USD.

A deposit debits the settlement account and credits the wallet; a withdrawal does
the opposite. The settlement account's balance is therefore the negative of the
net value currently held in that currency's wallets.

### LedgerTransaction

The aggregate root of the ledger, and the only thing that can move a balance
(`Entities/LedgerTransaction.cs`). One transaction records one financial event as a
balanced set of entries in a single currency.

| Field | Meaning |
|---|---|
| `Id` | Supplied by the caller. |
| `Kind` | `Deposit`, `Withdrawal`, `Transfer` or `Reversal`. |
| `Currency` | The one currency of every entry. |
| `OccurredAt` | When the event happened in the business's world. The database separately stamps when it was recorded. |
| `IdempotencyKey` | Optional. Trimmed; a blank key is stored as absent. |
| `ExternalReference` | Optional identifier from whatever external system caused the movement. |
| `ReversesTransactionId` | For a reversal, the transaction it negates. |
| `Entries` | The signed entries, in order. |

Transactions are created only through four factories, each of which writes the
entries **and** applies them to the accounts in one step, then seals the result:

| Kind | Accounts | Entries |
|---|---|---|
| Deposit | a wallet and a system account | wallet `+amount`, settlement `−amount` |
| Withdrawal | a wallet and a system account | wallet `−amount`, settlement `+amount` |
| Transfer | two different wallets | source `−amount`, destination `+amount` |
| Reversal | every account the original touched | the negation of every original entry |

Sealing checks that there are at least two entries and that they sum to zero, then
raises a `LedgerTransactionRecorded` domain event carrying the transaction's
identifier, kind, currency and occurrence time — never its amounts.

### LedgerEntry

One signed movement of value on one account, belonging to one transaction
(`Entities/LedgerEntry.cs`).

- **Signed:** positive increases the account's balance, negative decreases it.
- **Non-zero.**
- **No public constructor:** an entry can only be produced by a transaction
  factory, so a lone or unbalanced entry cannot be created.
- **Immutable:** no property can be set after creation.
- Its `Id` is the one identifier assigned by the database, a monotonic integer.

### Transaction kinds

| Kind | Money | Refused when |
|---|---|---|
| `Deposit` | enters the system into a wallet | the account is not a wallet, the counterparty is not a system account, the currencies differ, the amount is not positive |
| `Withdrawal` | leaves the system from a wallet | as above, or the wallet holds less than the amount |
| `Transfer` | moves between two wallets | either account is not a wallet, both are the same account, the currencies differ, the source holds less than the amount |
| `Reversal` | negates an earlier transaction | the original is itself a reversal, it has already been reversed, or a wallet no longer holds what reversing would take back |

### Reversal

A mistake is corrected by recording a new transaction whose entries are the exact
negation of the original's. Nothing is edited or deleted. The original's entries
stay exactly as written, so the reason an account holds what it holds can always
be reconstructed from the ledger.

- A transaction can be reversed at most once.
- A reversal cannot itself be reversed; if a correction was wrong, the answer is a
  new transaction describing the intended position.
- Reversing a deposit takes the money back out of the wallet, so it is refused if
  the wallet has since spent it.

## Invariants

| # | Rule | Enforced in the domain by | Enforced in PostgreSQL by |
|---|---|---|---|
| 1 | Every transaction balances to zero and has at least two entries. | `LedgerTransaction.Seal` | deferred constraint trigger `ledger_entries_balanced`, checked at `COMMIT` |
| 2 | Entries are immutable and append-only. | no setters, no public constructor | trigger `ledger_entries_append_only` rejects `UPDATE` and `DELETE` |
| 3 | A wallet balance never goes negative. | `Account.Debit` | check `ck_accounts_balance_not_negative` |
| 4 | A system account may go negative. | the rule in 3 applies to wallets only | the same check exempts `account_type = 'System'` |
| 5 | Every entry is in its account's currency and its transaction's currency. | `Money` refuses mixed arithmetic; factories check both accounts | composite foreign keys `fk_ledger_entries_account_currency` and `fk_ledger_entries_transaction_currency` |
| 6 | Amounts are never zero; movements are positive. | entry constructor; factories | check `ck_ledger_entries_amount_not_zero` |
| 7 | An amount is no more precise than its currency. | `Money` constructor | — (the column is `numeric(19,4)`) |
| 8 | A transaction is reversed at most once, by a compensating transaction. | `Reverse` factory; reversal handler checks under lock | partial unique index `ux_ledger_transactions_reverses` |
| 9 | An idempotency key identifies at most one transaction. | replay lookup under lock | partial unique index `ux_ledger_transactions_idempotency_key` |
| 10 | A system account has a system key and a wallet does not. | separate factories | check `ck_accounts_system_key_matches_type` |

### Balance and entries

The ledger entries are the **historical source of truth**. The balance on each
account is **materialised current state**: a projection of that account's entries,
stored so it can be read in one row and locked in one row. The two are written in
the same database transaction, and only `LedgerTransaction` can change a balance,
so they cannot drift apart. If they ever disagreed, the entries would be right.

Two consequences can be checked with a query at any time, and are checked after
every integration test and every load test:

- for every account, `balance = SUM(entries.amount)`;
- across the entire database, per currency, `SUM(entries.amount) = 0`.

The second is the strongest statement the system makes about itself: money is
neither created nor destroyed, only moved. It is only possible because every
deposit and withdrawal has a settlement counterparty.

## Idempotency, from the domain's side

A retried request carrying an idempotency key must not move money twice, and a key
reused for a *different* request must not receive the original's result.
`LedgerTransaction.Matches` decides whether a stored transaction is the same
request: the same kind, and every account the request names with the same signed
amount. A deposit of the same amount into a different wallet, or a transfer between
the same wallets in the opposite direction, does not match and is refused with 409.
How the lookup is made safe under concurrency is in [CONCURRENCY.md](CONCURRENCY.md).

## Validation versus rules

The application layer validates a request before any rule is consulted: identifiers
present, a known currency, a positive amount no larger than the ledger can store and
no more precise than the currency, and an idempotency key or external reference of
at most 200 characters. A failure is a 400 that names every offending field at once,
and nothing is locked or written. A well-formed request that a domain rule refuses —
insufficient funds, mismatched currencies, a transfer to the same account — is a 422.
The domain still guards every one of its own rules, so a caller that bypassed
validation would be refused, not obeyed.

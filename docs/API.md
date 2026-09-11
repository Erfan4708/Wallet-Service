# HTTP API

The ledger's HTTP interface: eight endpoints over JSON, plus the operational
endpoints for health, metrics and the API description.

**There is no authentication.** Anyone who can reach the API can call every
endpoint. That is a known limitation (see the README), not an oversight in this
document.

The examples were captured from the Compose stack on 11 September 2026, so
identifiers, timestamps and trace identifiers will differ on another run. The base
URL of the local stack is `http://localhost:18080`.

## Contents

- [OpenAPI and Swagger UI](#openapi-and-swagger-ui)
- [Conventions](#conventions)
- [Headers](#headers)
- [Endpoints at a glance](#endpoints-at-a-glance)
- [Accounts](#accounts)
- [Money movements](#money-movements)
- [Ledger transactions](#ledger-transactions)
- [Idempotency](#idempotency)
- [Errors](#errors)
- [Operational endpoints](#operational-endpoints)
- [What the API does not offer](#what-the-api-does-not-offer)

## OpenAPI and Swagger UI

| What | Where |
|---|---|
| Swagger UI | `GET /swagger` |
| OpenAPI document | `GET /swagger/v1/swagger.json` |

**When they are served.** Both are served only when `OpenApi:Enabled` is true.
- **Default:** on in the Development environment, off everywhere else, where they
  return 404.
- **Compose:** the stack turns them on (`LEDGER_OPENAPI_ENABLED`, default `true`).

**What the document describes.**
- all eight operations below, with their request and response schemas;
- every problem-details response each one can return;
- the optional `Idempotency-Key` request header on the four money-moving operations;
- the `trace-id` response header on every response.

`OpenApiDocumentTests` pins these properties, so the document cannot silently drift
from the endpoints. The health and metrics endpoints are not part of the document.

## Conventions

- **Content.** Requests send `Content-Type: application/json`. Successful responses
  are `application/json; charset=utf-8`, and errors are `application/problem+json`.
- **Property names.** Property names are camelCase.
- **Identifiers.** Identifiers are GUID strings. A path segment that is not a GUID
  matches no route, and the response is **404 with an empty body**, not problem
  details.
- **Currencies.** Currencies are ISO 4217 codes: `"USD"`, `"EUR"` or `"IRR"`. Any
  other string is rejected as unreadable (400). Send the code: a number that is
  not a defined currency fails validation.
- **Amounts.** An amount is a JSON number:
  - greater than zero;
  - with at most two decimal places (all three currencies use two);
  - at most `999999999999999.99`.
- **Amount scale in responses.** Amounts compare equal as numbers, but their
  textual scale depends on where the value came from:
  - A **newly recorded** transaction echoes the scale of the request: `100.00`,
    or `25` if `25` was sent.
  - Anything **read back from the database** has four decimal places: `100.0000`.
    That covers balances, statements, a replayed request, and the entries of a
    reversal.
- **Timestamps.** Timestamps are ISO 8601 with an offset, set by the server clock.
  PostgreSQL stores microseconds, so a timestamp read back has six fractional
  digits where the original response had seven.
- **Blank strings.** An empty or blank `Idempotency-Key` or `externalReference`
  counts as absent.

## Headers

| Header | Where | Meaning |
|---|---|---|
| `Idempotency-Key` | request, money-moving operations | Optional, at most 200 characters. Makes a retry return the original result instead of moving money again. See [Idempotency](#idempotency). |
| `Location` | response to `201 Created` | The new resource: `/accounts/{id}` or `/ledger/transactions/{id}`. |
| `trace-id` | every response | The W3C trace identifier of the request. The same value is in the `traceId` field of every error and on every log line the request produced. |

## Endpoints at a glance

| Method | Path | Purpose | Success | Errors |
|---|---|---|---|---|
| `POST` | `/accounts` | Open a wallet | 201 | 400, 409 |
| `GET` | `/accounts/{id}` | Read an account and its balance | 200 | 400, 404 |
| `GET` | `/accounts/{id}/statement?limit=` | Balance and the most recent entries | 200 | 400, 404 |
| `POST` | `/accounts/{id}/deposits` | Deposit into a wallet | 201, 200 on replay | 400, 404, 409, 422 |
| `POST` | `/accounts/{id}/withdrawals` | Withdraw from a wallet | 201, 200 on replay | 400, 404, 409, 422 |
| `POST` | `/transfers` | Move money between two wallets | 201, 200 on replay | 400, 404, 409, 422 |
| `GET` | `/ledger/transactions/{id}` | Read a recorded transaction | 200 | 400, 404 |
| `POST` | `/ledger/transactions/{id}/reversal` | Reverse a recorded transaction | 201, 200 on replay | 400, 404, 409, 422 |

## Accounts

### `POST /accounts`

Opens a **wallet** in one currency. System accounts, such as the settlement
account for each currency, are created by migration and cannot be opened here.

| Field | Type | Required | Notes |
|---|---|---|---|
| `currency` | string | yes | `USD`, `EUR` or `IRR` |
| `accountId` | GUID | no | Supply one to make a retry safe: a second request with the same identifier gets 409 instead of opening a second account. Generated if omitted. |

```http
POST /accounts
Content-Type: application/json

{"currency":"USD","accountId":"555cf4f9-6938-421e-b3ce-8b3a52fa0e47"}
```

```http
HTTP/1.1 201 Created
Location: /accounts/555cf4f9-6938-421e-b3ce-8b3a52fa0e47
trace-id: 02cd82ed005d25f1845ff675965d7763

{"id":"555cf4f9-6938-421e-b3ce-8b3a52fa0e47","currency":"USD","balance":0}
```

| Error | When |
|---|---|
| 400 "The request could not be read." | missing body, malformed JSON, unsupported currency code |
| 400 "Validation failed." | an undefined currency number, or an empty GUID as `accountId` |
| 409 "Account '…' already exists." | `accountId` is already in use |

### `GET /accounts/{id}`

```http
HTTP/1.1 200 OK

{"id":"555cf4f9-6938-421e-b3ce-8b3a52fa0e47","currency":"USD","balance":95.0000}
```

The errors are 404 "Account '…' was not found." and 400 for the empty GUID.

### `GET /accounts/{id}/statement`

Returns the account's balance and its most recent ledger entries, **newest
first**. The balance and the entries come from the same read, so the statement
shows why the account holds what it holds.

| Query parameter | Default | Range |
|---|---|---|
| `limit` | 50 | 1–500; anything else is 400 "The limit must be between 1 and 500." |

```http
GET /accounts/555cf4f9-6938-421e-b3ce-8b3a52fa0e47/statement?limit=10
```

```json
{
  "accountId": "555cf4f9-6938-421e-b3ce-8b3a52fa0e47",
  "currency": "USD",
  "balance": 95.0000,
  "entries": [
    {"entryId": 4841, "transactionId": "233a6888-458f-449d-882c-a38c15720925", "amount": -30.0000, "currency": "USD"},
    {"entryId": 4839, "transactionId": "4dc0a64a-cf11-46a9-b390-e3e257ef69ce", "amount": 25.0000, "currency": "USD"},
    {"entryId": 4837, "transactionId": "94c4531a-294f-4145-8d8e-2b8a6846de5c", "amount": 100.0000, "currency": "USD"}
  ]
}
```

There is no cursor. A statement is the most recent entries up to `limit`.

## Money movements

Deposits, withdrawals and transfers share one request shape and one response
shape.

**Request fields**

| Field | Type | Required | Notes |
|---|---|---|---|
| `amount` | number | yes | Greater than zero, at most two decimal places, at most `999999999999999.99` |
| `currency` | string | yes | Must be the wallet's currency, or both wallets' currency for a transfer |
| `transactionId` | GUID | no | The identifier the transaction will be recorded under. Supplying it makes a retry without an idempotency key safe: a second attempt gets 409. Generated if omitted. |
| `externalReference` | string | no | An identifier from the system that caused the movement, at most 200 characters. Stored, not returned. |
| `sourceAccountId`, `destinationAccountId` | GUID | transfers only | Must differ |

**Response: a ledger transaction**

| Field | Meaning |
|---|---|
| `transactionId` | The transaction's identifier, also in `Location` |
| `kind` | `Deposit`, `Withdrawal`, `Transfer` or `Reversal` |
| `currency` | The transaction's currency |
| `occurredAt` | When it was recorded |
| `entries` | The signed entries, which always sum to zero: positive adds to the account, negative takes from it |
| `wasReplayed` | `false` when this request recorded the transaction (**201**, with `Location`); `true` when an earlier request with the same idempotency key did (**200**, no `Location`) |

**Refusals shared by all three**

| Status | When |
|---|---|
| 400 "Validation failed." | an amount of zero or less, too many decimal places, above the maximum; an undefined currency; an empty GUID; a key or reference longer than 200 characters; a transfer to the same account |
| 400 "The request could not be read." | missing body, malformed JSON, unsupported currency code |
| 404 | an account does not exist |
| 409 | the idempotency key was used for a different request; the `transactionId` already exists |
| 422 | insufficient funds; a currency different from the account's; a system account named where a wallet is required |

### `POST /accounts/{id}/deposits`

Credits the wallet and debits the currency's settlement account.

```http
POST /accounts/555cf4f9-6938-421e-b3ce-8b3a52fa0e47/deposits
Content-Type: application/json
Idempotency-Key: f6f86a9f-52b3-408b-92f1-c5c7d6aba00f

{"amount":100.00,"currency":"USD","externalReference":"bank-transfer-8841"}
```

```http
HTTP/1.1 201 Created
Location: /ledger/transactions/94c4531a-294f-4145-8d8e-2b8a6846de5c
trace-id: 2cd061bda1d578d2492ca9d8050913a9

{"transactionId":"94c4531a-294f-4145-8d8e-2b8a6846de5c","kind":"Deposit","currency":"USD",
 "occurredAt":"2026-09-11T14:04:22.2730127+00:00",
 "entries":[{"accountId":"555cf4f9-6938-421e-b3ce-8b3a52fa0e47","amount":100.00,"currency":"USD"},
            {"accountId":"00000000-0000-0000-0000-000000000840","amount":-100.00,"currency":"USD"}],
 "wasReplayed":false}
```

The same request sent again, with the same key:

```http
HTTP/1.1 200 OK

{"transactionId":"94c4531a-294f-4145-8d8e-2b8a6846de5c","kind":"Deposit","currency":"USD",
 "occurredAt":"2026-09-11T14:04:22.273012+00:00",
 "entries":[{"accountId":"555cf4f9-6938-421e-b3ce-8b3a52fa0e47","amount":100.0000,"currency":"USD"},
            {"accountId":"00000000-0000-0000-0000-000000000840","amount":-100.0000,"currency":"USD"}],
 "wasReplayed":true}
```

`00000000-0000-0000-0000-000000000840` is the USD settlement account. The EUR
account ends in `978` and the IRR account in `364`.

### `POST /accounts/{id}/withdrawals`

Debits the wallet and credits the settlement account. It is refused when the wallet
does not hold the amount.

```http
POST /accounts/3177d1dd-cdd1-4411-86aa-5ba16828c4ac/withdrawals
Idempotency-Key: c4424425-b049-4237-aa9c-8bd33facaca4

{"amount":10.00,"currency":"USD"}
```

```http
HTTP/1.1 201 Created
Location: /ledger/transactions/56d18ee3-7c93-4964-a550-4708da46a394

{"transactionId":"56d18ee3-7c93-4964-a550-4708da46a394","kind":"Withdrawal","currency":"USD",
 "occurredAt":"2026-09-11T14:04:23.6302954+00:00",
 "entries":[{"accountId":"3177d1dd-cdd1-4411-86aa-5ba16828c4ac","amount":-10.00,"currency":"USD"},
            {"accountId":"00000000-0000-0000-0000-000000000840","amount":10.00,"currency":"USD"}],
 "wasReplayed":false}
```

```http
POST /accounts/3177d1dd-cdd1-4411-86aa-5ba16828c4ac/withdrawals

{"amount":500.00,"currency":"USD"}
```

```http
HTTP/1.1 422 Unprocessable Entity
Content-Type: application/problem+json

{"type":"https://tools.ietf.org/html/rfc4918#section-11.2","title":"Business rule violated.","status":422,
 "detail":"Account 3177d1dd-cdd1-4411-86aa-5ba16828c4ac holds 20.00 USD but 500.00 USD was requested.",
 "traceId":"cfacdb201674a140967362559f29c541"}
```

### `POST /transfers`

Moves money between two wallets that hold the transfer's currency.

```http
POST /transfers
Idempotency-Key: 7a60c44a-47b7-41bc-8033-0bd2bf25b249

{"sourceAccountId":"555cf4f9-6938-421e-b3ce-8b3a52fa0e47",
 "destinationAccountId":"3177d1dd-cdd1-4411-86aa-5ba16828c4ac",
 "amount":30.00,"currency":"USD"}
```

```http
HTTP/1.1 201 Created
Location: /ledger/transactions/233a6888-458f-449d-882c-a38c15720925

{"transactionId":"233a6888-458f-449d-882c-a38c15720925","kind":"Transfer","currency":"USD",
 "occurredAt":"2026-09-11T14:04:23.2463074+00:00",
 "entries":[{"accountId":"555cf4f9-6938-421e-b3ce-8b3a52fa0e47","amount":-30.00,"currency":"USD"},
            {"accountId":"3177d1dd-cdd1-4411-86aa-5ba16828c4ac","amount":30.00,"currency":"USD"}],
 "wasReplayed":false}
```

A transfer to the source account itself is 400
(`errors.DestinationAccountId`: "An account cannot transfer to itself."). A
transfer between wallets of different currencies is 422.

## Ledger transactions

### `GET /ledger/transactions/{id}`

The resource a money movement's `Location` header names. `wasReplayed` is always
`false` here.

```http
HTTP/1.1 200 OK

{"transactionId":"233a6888-458f-449d-882c-a38c15720925","kind":"Transfer","currency":"USD",
 "occurredAt":"2026-09-11T14:04:23.246307+00:00",
 "entries":[{"accountId":"555cf4f9-6938-421e-b3ce-8b3a52fa0e47","amount":-30.0000,"currency":"USD"},
            {"accountId":"3177d1dd-cdd1-4411-86aa-5ba16828c4ac","amount":30.0000,"currency":"USD"}],
 "wasReplayed":false}
```

An unknown transaction is 404 "LedgerTransaction '…' was not found.".

### `POST /ledger/transactions/{id}/reversal`

Records a new transaction that negates every entry of the original. It has no
request body and takes an optional `Idempotency-Key`. The reversal's own identifier
is generated by the server.

```http
POST /ledger/transactions/2f15f426-aadf-44a3-b400-94346b46603d/reversal
Idempotency-Key: a8bb0144-c096-4694-8d55-835ef9e89ef3
```

```http
HTTP/1.1 201 Created
Location: /ledger/transactions/b61e744b-9a76-4607-ab96-93a8a1cb09bf

{"transactionId":"b61e744b-9a76-4607-ab96-93a8a1cb09bf","kind":"Reversal","currency":"USD",
 "occurredAt":"2026-09-11T14:06:52.2035287+00:00",
 "entries":[{"accountId":"49f67a65-c2b2-4232-b07c-70471434ca0e","amount":-40.0000,"currency":"USD"},
            {"accountId":"00000000-0000-0000-0000-000000000840","amount":40.0000,"currency":"USD"}],
 "wasReplayed":false}
```

| Situation | Response |
|---|---|
| The same request again, with the same key | 200, the reversal above, `wasReplayed: true` |
| The transaction was already reversed | 409 "Transaction 2f15f426-… has already been reversed." |
| The transaction is itself a reversal | 409 "Transaction b61e744b-… is itself a reversal and cannot be reversed." |
| The key was used for a different request | 409 "Idempotency key '…' was already used for a different operation." |
| A wallet no longer holds the amount being returned | 422 "Account … holds 95.00 USD but 100.00 USD was requested." |
| The transaction does not exist | 404 "LedgerTransaction '…' was not found." |
| The key is longer than 200 characters | 400 |

A transaction can be reversed only once, whether or not the request carries a key,
so retrying a reversal is always safe.

## Idempotency

**When a stored transaction matches.** A request with an `Idempotency-Key` is
compared with the transaction already stored under that key. They match when both
of these are the same:
- the kind of operation;
- every leg the request names: each account, the direction money moves in, and the
  amount with its currency.

A reversal must also reverse the same original transaction.

**The outcomes:**
- **A match** returns the stored transaction with **200** and
  `"wasReplayed": true`. Nothing is written, and no second event is published.
- **Anything else** is **409**.
- **Not compared:** `transactionId` and `externalReference` are not part of the
  comparison. A replay that changes either still returns the original transaction,
  with its original identifier.

**Scope of a key.**
- **Global:** a key is not scoped to an account, an operation or a client.
- **Permanent:** it never expires.
- **Only for successes:** a key is remembered only once a transaction commits. A
  refused request (400, 404, 409, 422) can be retried with the same key and is
  evaluated from scratch.
- **Blank keys:** an empty or blank key counts as no key.

**Choosing a key.** Clients should use a new random UUID for each logical
operation, and reuse it only to retry that operation.

[FAILURES.md](FAILURES.md), section 8, lists every case, including concurrent
duplicates and crashes, and ADR-007 records the design.

## Errors

Every error the service produces is an RFC 9457 problem details object:

| Field | Present | Meaning |
|---|---|---|
| `type` | always | A link to the HTTP specification section for the status |
| `title` | always | A fixed sentence per kind of error, safe to match on |
| `status` | always | The HTTP status |
| `detail` | except on 500 | A human-readable explanation, not meant to be parsed |
| `errors` | validation failures only | A map from field name to messages |
| `traceId` | always | The request's trace identifier, equal to the `trace-id` header |

| Status | `title` | Typical `detail` |
|---|---|---|
| 400 | Validation failed. | "One or more validation errors occurred.", with `errors` such as `{"Amount":["The amount must be greater than zero."]}` or `{"Currency":["Unknown currency."]}` |
| 400 | The request could not be read. | "The request body or its parameters are missing or malformed." |
| 404 | Not found. | "Account '…' was not found." / "LedgerTransaction '…' was not found." |
| 409 | Conflict. | "Account '…' already exists." · "Idempotency key '…' was already used for a different operation." · "The change conflicts with a record that already exists." · "Transaction … has already been reversed." · "Transaction … is itself a reversal and cannot be reversed." |
| 422 | Business rule violated. | "Account … holds 20.00 USD but 500.00 USD was requested." · "Cannot combine amounts in different currencies: USD and EUR." · "Account … is a System account where a Wallet account is required." |
| 500 | An unexpected error occurred. | none |

**Where the messages come from.**
- **Races:** two conflicts come from database unique indexes and appear only if two
  requests race past the application's own check: "A transaction with this
  idempotency key already exists." and "That transaction has already been
  reversed.".
- **Unreadable requests:** the framework's message would name internal types and
  JSON paths, so it is replaced with a fixed sentence.
- **500s:** a 500 never includes a stack trace, an exception type or a connection
  detail; those go to the log, under the same trace identifier.

Validation failure:

```http
HTTP/1.1 400 Bad Request
Content-Type: application/problem+json

{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"Validation failed.","status":400,
 "detail":"One or more validation errors occurred.",
 "errors":{"Limit":["The limit must be between 1 and 500."]},
 "traceId":"5f08a67e2a1d44e8dd7aafcf6e8b7da2"}
```

A request the server could not handle, here with PostgreSQL stopped:

```http
HTTP/1.1 500 Internal Server Error
Content-Type: application/problem+json

{"type":"https://tools.ietf.org/html/rfc9110#section-15.6.1","title":"An unexpected error occurred.","status":500,
 "traceId":"f2154f02bad5797759e030813d6d8678"}
```

## Operational endpoints

| Endpoint | Response |
|---|---|
| `GET /health/live` | Always 200 while the process serves HTTP: `{"status":"Healthy","checks":{}}` |
| `GET /health/ready` | 200 `Healthy`; 200 `Degraded` when RabbitMQ or Redis is unreachable; 503 `Unhealthy` when PostgreSQL is, e.g. `{"status":"Degraded","checks":{"postgres":"Healthy","rabbitmq":"Degraded","redis":"Healthy"}}` |
| `GET /metrics` | Prometheus text format; the ledger metrics are listed in the README |
| `GET /swagger` | Swagger UI, when OpenAPI is enabled |

Health responses carry check names and statuses only, never exception messages.

## What the API does not offer

Deliberately out of scope, and listed so their absence is not mistaken for a gap
in this document:
- authentication and authorisation;
- listing or searching accounts;
- looking a transaction up by external reference;
- closing, freezing or limiting accounts;
- cursor pagination of statements;
- currency exchange;
- webhooks: events go to RabbitMQ.

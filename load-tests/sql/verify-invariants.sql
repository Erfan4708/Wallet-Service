-- Financial correctness after load. Every row must report 0 violations.
--
--   docker exec -i ledger-postgres psql -U ledger -d ledger < load-tests/sql/verify-invariants.sql
--
-- A load test that only checks HTTP status codes proves the API answered. These
-- prove the money is still right.

SELECT 'global ledger sum is zero per currency' AS invariant,
       count(*) AS violations
  FROM (SELECT currency FROM ledger_entries GROUP BY currency HAVING sum(amount) <> 0) t

UNION ALL
SELECT 'stored balance equals sum of entries',
       count(*)
  FROM accounts a
  LEFT JOIN (SELECT account_id, sum(amount) AS total FROM ledger_entries GROUP BY account_id) e
         ON e.account_id = a.id
 WHERE a.balance_amount <> coalesce(e.total, 0)

UNION ALL
SELECT 'no wallet balance is negative',
       count(*)
  FROM accounts
 WHERE account_type = 'Wallet' AND balance_amount < 0

UNION ALL
SELECT 'every transaction balances with at least two entries',
       count(*)
  FROM (SELECT t.id
          FROM ledger_transactions t
          LEFT JOIN ledger_entries e ON e.transaction_id = t.id
         GROUP BY t.id
        HAVING count(e.id) < 2 OR coalesce(sum(e.amount), 0) <> 0) t

UNION ALL
SELECT 'no entry without a transaction or an account',
       count(*)
  FROM ledger_entries e
  LEFT JOIN ledger_transactions t ON t.id = e.transaction_id
  LEFT JOIN accounts a ON a.id = e.account_id
 WHERE t.id IS NULL OR a.id IS NULL

UNION ALL
SELECT 'every entry is in its account currency',
       count(*)
  FROM ledger_entries e
  JOIN accounts a ON a.id = e.account_id
 WHERE e.currency <> a.balance_currency

UNION ALL
SELECT 'every transaction has exactly one outbox message',
       count(*)
  FROM (SELECT t.id
          FROM ledger_transactions t
          LEFT JOIN outbox_messages o ON o.aggregate_id = t.id
         GROUP BY t.id
        HAVING count(o.id) <> 1) t

UNION ALL
SELECT 'no outbox message without its transaction',
       count(*)
  FROM outbox_messages o
  LEFT JOIN ledger_transactions t ON t.id = o.aggregate_id
 WHERE t.id IS NULL

UNION ALL
SELECT 'no idempotency key used twice',
       count(*)
  FROM (SELECT idempotency_key FROM ledger_transactions
         WHERE idempotency_key IS NOT NULL
         GROUP BY idempotency_key HAVING count(*) > 1) t

UNION ALL
SELECT 'no transaction reversed twice',
       count(*)
  FROM (SELECT reverses_transaction_id FROM ledger_transactions
         WHERE reverses_transaction_id IS NOT NULL
         GROUP BY reverses_transaction_id HAVING count(*) > 1) t;

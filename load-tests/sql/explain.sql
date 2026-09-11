-- Execution plans for the statements on the ledger's hot path, run against the
-- data a benchmark leaves behind:
--
--   docker exec -i ledger-postgres psql -U ledger -d ledger < load-tests/sql/explain.sql
--
-- The statements mirror what EF Core and the repositories issue. Each runs inside
-- a transaction that is rolled back, so the locking statements take and release
-- their locks without changing anything.

\set wallet '''b1000000-0000-4000-8000-000000000001'''
\set settlement '''00000000-0000-0000-0000-000000000840'''

BEGIN;

\echo '--- settlement lookup by system key ---'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT id FROM accounts WHERE system_key = 'SETTLEMENT:USD' LIMIT 1;

\echo '--- row lock, as taken by GetForUpdateAsync ---'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT 1 FROM accounts WHERE id = :settlement FOR UPDATE;

\echo '--- account read after locking ---'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT * FROM accounts WHERE id = :wallet LIMIT 1;

\echo '--- idempotency lookup ---'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT * FROM ledger_transactions WHERE idempotency_key = 'bench-fund-b1000000-0000-4000-8000-000000000001' LIMIT 1;

\echo '--- statement: recent entries for an account ---'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT * FROM ledger_entries WHERE account_id = :wallet ORDER BY id DESC LIMIT 50;

\echo '--- outbox claim candidate scan ---'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT id FROM outbox_messages
 WHERE published_at IS NULL AND next_attempt_at <= now()
 ORDER BY id LIMIT 50 FOR UPDATE SKIP LOCKED;

ROLLBACK;

-- Who is waiting on whom, right now. Run repeatedly while a contention scenario
-- is under load:
--
--   docker exec -i ledger-postgres psql -U ledger -d ledger < load-tests/sql/blocking.sql
--
-- Every waiting backend is listed with the backends blocking it and how long its
-- transaction has been open. Parameter values are not visible here; which row is
-- hot shows up in the server log instead, because log_lock_waits reports the
-- tuple being waited on (for example "while locking tuple (0,3) in relation
-- accounts"), and that tuple identifier resolves to an account with:
--
--   SELECT id, account_type, system_key FROM accounts WHERE ctid = '(0,3)';

SELECT a.pid,
       pg_blocking_pids(a.pid)                          AS blocked_by,
       a.wait_event_type,
       a.wait_event,
       round(extract(epoch FROM now() - a.xact_start)::numeric * 1000, 1) AS xact_ms,
       left(regexp_replace(a.query, '\s+', ' ', 'g'), 90) AS query
  FROM pg_stat_activity a
 WHERE a.datname = 'ledger'
   AND cardinality(pg_blocking_pids(a.pid)) > 0
 ORDER BY xact_ms DESC;

SELECT count(*) FILTER (WHERE cardinality(pg_blocking_pids(pid)) > 0) AS waiting,
       count(*) FILTER (WHERE state = 'active')                        AS active,
       count(*) FILTER (WHERE state = 'idle in transaction')           AS idle_in_transaction
  FROM pg_stat_activity
 WHERE datname = 'ledger';

-- Where the database spent its time during a run.
--
--   docker exec -i ledger-postgres psql -U ledger -d ledger < load-tests/sql/diagnostics.sql
--
-- Reset between runs with load-tests/sql/reset-stats.sql so each report covers
-- exactly one run.

\echo '--- statements by total time ---'
SELECT round(total_exec_time::numeric, 0)          AS total_ms,
       calls,
       round(mean_exec_time::numeric, 3)           AS mean_ms,
       round((100 * total_exec_time / nullif(sum(total_exec_time) OVER (), 0))::numeric, 1) AS pct,
       left(regexp_replace(query, '\s+', ' ', 'g'), 110) AS query
  FROM pg_stat_statements
 WHERE dbid = (SELECT oid FROM pg_database WHERE datname = 'ledger')
 ORDER BY total_exec_time DESC
 LIMIT 15;

\echo '--- transactions, rollbacks, deadlocks ---'
SELECT xact_commit, xact_rollback, deadlocks, conflicts,
       blks_hit, blks_read,
       round(100.0 * blks_hit / nullif(blks_hit + blks_read, 0), 2) AS cache_hit_pct
  FROM pg_stat_database
 WHERE datname = 'ledger';

\echo '--- table access: sequential vs index scans ---'
SELECT relname, seq_scan, seq_tup_read, idx_scan, n_tup_ins, n_tup_upd, n_dead_tup
  FROM pg_stat_user_tables
 ORDER BY relname;

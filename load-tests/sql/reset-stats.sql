-- Clears accumulated statistics so the next diagnostics report covers one run.
SELECT pg_stat_statements_reset();
SELECT pg_stat_reset();

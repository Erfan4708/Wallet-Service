-- Per-statement timing for performance investigation. Loaded through
-- shared_preload_libraries in docker-compose.yml; this only registers the view.
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;

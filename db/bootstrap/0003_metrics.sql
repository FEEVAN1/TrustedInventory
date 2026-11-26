-- Cluster bootstrap addendum for M6 operations metrics (DEC-079). Run once per cluster by a superuser after 0002_purge.sql.
-- ti_metrics_reader cannot log in: it only owns ti.operations_metrics(), which returns aggregates across tenants and never a tenant
-- id or name.
CREATE ROLE ti_metrics_reader NOLOGIN NOBYPASSRLS;
GRANT ti_metrics_reader TO ti_owner;                     -- lets migrations hand the function over (DEC-028 pattern)
GRANT USAGE ON SCHEMA ti TO ti_metrics_reader;

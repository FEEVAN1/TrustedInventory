-- Cluster bootstrap addendum for S3 deletion (DEC-072). Run once per cluster by a superuser after 0001_control_plane.sql.
-- ti_purger cannot log in: it only owns cp.purge_tenant() and cp.replay_deletion(), the single path by which an approved
-- deletion removes a tenant's live data (LFC-004). The functions check the approval in the database before deleting.
CREATE ROLE ti_purger NOLOGIN NOBYPASSRLS;
GRANT ti_purger TO ti_owner;                             -- lets migrations hand the functions over (DEC-028 pattern)
GRANT USAGE ON SCHEMA ti, cp TO ti_purger;

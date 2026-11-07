-- Cluster bootstrap addendum for v3.0 (DEC-035). Run once per cluster by a superuser after 0000_cluster.sql.
-- Source: db/design/saas-schema-draft.sql (validated by db/design/run-saas-smoke.ps1).
-- Roles need a superuser (or CREATEROLE). Passwords are set by deployment, never here.
CREATE ROLE ti_control LOGIN NOBYPASSRLS NOINHERIT;      -- control plane: signup, provisioning, billing, webhooks
CREATE ROLE cp_access_reader NOLOGIN NOBYPASSRLS;        -- owns cp.tenant_access() only (mirrors ti_membership_reader)
GRANT cp_access_reader TO ti_owner;                      -- lets migrations hand the function over (DEC-028 pattern)

CREATE SCHEMA cp AUTHORIZATION ti_owner;
REVOKE ALL ON SCHEMA cp FROM PUBLIC;
GRANT USAGE ON SCHEMA cp TO ti_app, ti_control, cp_access_reader;
GRANT USAGE ON SCHEMA ti TO ti_control, cp_access_reader;
-- ti_platform deliberately gets no USAGE on cp: platform operations never read billing data (SAS-004).

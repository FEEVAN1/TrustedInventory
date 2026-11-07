-- Cluster bootstrap. Run once per database by a superuser (platform operator), before the migrator.
-- Creates roles, the schema and extensions. Passwords are set separately by the deployment
-- (ALTER ROLE ... PASSWORD) and never appear in this file (TEN-002, AGT-004).

CREATE ROLE ti_owner NOLOGIN NOBYPASSRLS;                    -- owns every object; migrations SET ROLE to it
CREATE ROLE ti_migrator LOGIN NOBYPASSRLS IN ROLE ti_owner;  -- migration identity, unavailable to runtime
CREATE ROLE ti_app LOGIN NOBYPASSRLS NOINHERIT;              -- API + worker runtime
CREATE ROLE ti_platform LOGIN NOBYPASSRLS NOINHERIT;         -- tenant provisioning only
CREATE ROLE ti_membership_reader NOLOGIN NOBYPASSRLS;        -- owns ti.user_memberships() only

-- Lets migrations (as ti_owner) transfer the membership function to ti_membership_reader.
GRANT ti_membership_reader TO ti_owner;

CREATE EXTENSION IF NOT EXISTS btree_gist;  -- device assignment non-overlap (M5)

CREATE SCHEMA ti AUTHORIZATION ti_owner;
REVOKE ALL ON SCHEMA ti FROM PUBLIC;
GRANT USAGE ON SCHEMA ti TO ti_app, ti_platform, ti_membership_reader;

-- Runtime roles must never create objects anywhere.
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

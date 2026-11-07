-- PENDING migration: move to db/migrations/0005_tenant_status_contract.sql in the M2 release, after every
-- deployed API/worker version reads lifecycle and security suspension instead of tenant.status (SOP-005, DEC-042).
-- Not embedded in the migrator while it lives here.
-- CONTRACT step, released only after every running API/worker version reads lifecycle and suspension
-- (docs/migration-design.md §3). Drops the v2.0 status column and its compatibility trigger and function.
-- New code always sets lifecycle; from here an omitted value means a fresh signup.
ALTER TABLE ti.tenant
  ALTER COLUMN lifecycle SET DEFAULT 'Provisioning',
  ALTER COLUMN lifecycle SET NOT NULL,
  ALTER COLUMN slug SET NOT NULL,
  ALTER COLUMN home_region SET NOT NULL;
DROP TRIGGER tenant_sync_legacy_status ON ti.tenant;
DROP FUNCTION ti.tenant_sync_legacy_status();
DROP FUNCTION ti.user_memberships(text);      -- replaced by user_memberships_v2 in 0004
ALTER TABLE ti.tenant DROP COLUMN status;
DROP TYPE ti.tenant_status;

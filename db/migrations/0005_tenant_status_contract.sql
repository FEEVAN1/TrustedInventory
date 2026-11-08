-- 0005 (M2 release): CONTRACT step for the tenant (DEC-033, DEC-042). Released once no API or worker code read
-- tenant.status: the worker selects tenants by lifecycle (DEC-043) and the enum mapping was removed.
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

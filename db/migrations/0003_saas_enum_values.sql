-- 0003 (S1): new labels on existing enum types. They must commit before 0004 uses them (DEC-042).
-- New labels on existing types. Appended only, never reordered (docs/migration-design.md §3).
ALTER TYPE ti.role_name ADD VALUE IF NOT EXISTS 'TenantOwner';
ALTER TYPE ti.role_name ADD VALUE IF NOT EXISTS 'BillingAdministrator';
ALTER TYPE ti.role_name ADD VALUE IF NOT EXISTS 'PlatformBilling';
ALTER TYPE ti.export_type ADD VALUE IF NOT EXISTS 'TenantArchive';
ALTER TYPE ti.alert_type ADD VALUE IF NOT EXISTS 'ProvisioningFailed';
ALTER TYPE ti.alert_type ADD VALUE IF NOT EXISTS 'WebhookBacklog';
ALTER TYPE ti.alert_type ADD VALUE IF NOT EXISTS 'EntitlementMismatch';
ALTER TYPE ti.alert_type ADD VALUE IF NOT EXISTS 'BillingReconciliation';

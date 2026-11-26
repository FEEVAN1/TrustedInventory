-- 0019 (S2 slice 4): plan change idempotency and provider links; plan change notices (DEC-065). Expand-only.

-- The Idempotency-Key of changeSubscription; a retry returns the same change.
ALTER TABLE cp.subscription_change ADD COLUMN idempotency_key uuid;
ALTER TABLE cp.subscription_change ADD COLUMN provider_change_ref cp.provider_ref;
CREATE UNIQUE INDEX subscription_change_key_uq ON cp.subscription_change (tenant_id, idempotency_key) WHERE idempotency_key IS NOT NULL;

ALTER TYPE cp.billing_notice_kind ADD VALUE IF NOT EXISTS 'PlanChangeBlocked';
ALTER TYPE cp.billing_notice_kind ADD VALUE IF NOT EXISTS 'PlanChangeApplied';

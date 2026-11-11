-- 0021 (S2 slice 7): providers that replace a subscription to change its price (Paystack, DEC-068). Expand-only.
ALTER TABLE cp.subscription_change ADD COLUMN provider_new_subscription_ref cp.provider_ref;
CREATE UNIQUE INDEX subscription_change_new_subscription_uq ON cp.subscription_change (provider_new_subscription_ref)
  WHERE provider_new_subscription_ref IS NOT NULL;

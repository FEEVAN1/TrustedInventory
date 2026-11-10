-- 0017 (S2): commercial control additions (DEC-062). Expand-only: new tables and nullable/defaulted columns.

-- Operator tasks raised by billing: reconciliation differences, unknown provider references, disputes, refunds and
-- chargebacks (BIL-005, BIL-006, BIL-008). Control plane only; tenant_id is NULL when the reference is unknown.
CREATE TYPE cp.billing_task_kind AS ENUM ('SubscriptionMismatch','UnpaidInvoice','EntitlementMismatch',
  'UnknownBillingReference','Dispute','Refund','Chargeback');
CREATE TYPE cp.billing_task_status AS ENUM ('Open','Resolved');

CREATE TABLE cp.billing_task (
  id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  kind         cp.billing_task_kind NOT NULL,
  status       cp.billing_task_status NOT NULL DEFAULT 'Open',
  tenant_id    uuid REFERENCES ti.tenant(id),
  provider     cp.provider_code NOT NULL,
  dedupe_key   text NOT NULL UNIQUE CHECK (char_length(dedupe_key) BETWEEN 1 AND 300),   -- one task per cause
  detail       text NOT NULL CHECK (char_length(detail) BETWEEN 1 AND 2000),
  created_at   timestamptz NOT NULL DEFAULT now(),
  resolved_at  timestamptz,
  resolved_by  ti.subject_text,
  resolution   text CHECK (resolution IS NULL OR char_length(resolution) BETWEEN 1 AND 1000),
  version      integer NOT NULL DEFAULT 1 CHECK (version > 0),
  CHECK ((status = 'Resolved') = (resolved_at IS NOT NULL AND resolved_by IS NOT NULL AND resolution IS NOT NULL))
);
CREATE INDEX billing_task_open_ix ON cp.billing_task (created_at) WHERE status = 'Open';

-- Reconciliation cursor per provider (BIL-006).
CREATE TABLE cp.billing_sync_state (
  provider        cp.provider_code PRIMARY KEY,
  cursor          text CHECK (cursor IS NULL OR char_length(cursor) <= 500),
  last_run_at     timestamptz,
  last_success_at timestamptz,
  last_error      text CHECK (last_error IS NULL OR char_length(last_error) <= 2000)
);

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['billing_task','billing_sync_state'] LOOP
    EXECUTE format('ALTER TABLE cp.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE cp.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY control_plane ON cp.%I TO ti_control USING (true) WITH CHECK (true)', t);
    EXECUTE format('GRANT SELECT, INSERT, UPDATE ON cp.%I TO ti_control', t);
  END LOOP;
END $$;

-- Inbox failure classification for the platform console (WebhookEvent.failureCode).
ALTER TABLE cp.webhook_inbox ADD COLUMN failure_code text CHECK (failure_code IS NULL OR failure_code ~ '^[A-Z][A-Z0-9_]{0,63}$');

-- Invoice issue time as reported by the provider (Invoice.issuedAt).
ALTER TABLE cp.invoice ADD COLUMN issued_at timestamptz;

-- Catalog metadata (DEC-062). Adding a defaulted column does not fire the published-plan guard.
ALTER TABLE cp.plan_version ADD COLUMN trial_days integer NOT NULL DEFAULT 14 CHECK (trial_days BETWEEN 0 AND 90);
-- Draft concurrency token (ETag / If-Match on adding prices and publishing); frozen with the rest once published.
ALTER TABLE cp.plan_version ADD COLUMN revision integer NOT NULL DEFAULT 1 CHECK (revision > 0);

-- Catalog changes belong to no tenant, so they are evidenced here rather than in a tenant chain. Append-only.
CREATE TYPE cp.catalog_action AS ENUM ('PlanVersionCreated','PriceVersionCreated','PlanVersionPublished');
CREATE TABLE cp.catalog_event (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  action            cp.catalog_action NOT NULL,
  plan_version_id   uuid NOT NULL REFERENCES cp.plan_version(id),
  price_version_id  uuid REFERENCES cp.price_version(id),
  actor_subject     ti.subject_text NOT NULL,
  reason            text NOT NULL CHECK (char_length(reason) BETWEEN 1 AND 1000),
  request_id        text CHECK (request_id IS NULL OR char_length(request_id) <= 200),
  detail            jsonb NOT NULL CHECK (jsonb_typeof(detail) = 'object'),
  occurred_at       timestamptz NOT NULL,
  CHECK ((action = 'PriceVersionCreated') = (price_version_id IS NOT NULL))
);
CREATE INDEX catalog_event_plan_ix ON cp.catalog_event (plan_version_id, occurred_at);
CREATE TRIGGER catalog_event_append_only BEFORE UPDATE OR DELETE ON cp.catalog_event
  FOR EACH ROW EXECUTE FUNCTION ti.reject_mutation();
ALTER TABLE cp.catalog_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE cp.catalog_event FORCE ROW LEVEL SECURITY;
CREATE POLICY control_plane ON cp.catalog_event TO ti_control USING (true) WITH CHECK (true);
GRANT SELECT, INSERT ON cp.catalog_event TO ti_control;
-- No route sets display approval: publicly shown prices need owner approval (D09, DEC-040).
ALTER TABLE cp.price_version ADD COLUMN display_approved boolean NOT NULL DEFAULT false;

-- A tenant reads the plan and prices it is contracted to, or has a change scheduled to, even when they are not public
-- (Subscription.planCode, scheduled changes). The subqueries run under the tenant_read policies, so only the caller's
-- own subscription can make a catalog row visible.
CREATE POLICY subscribed_read ON cp.plan_version FOR SELECT TO ti_app USING (
  EXISTS (SELECT 1 FROM cp.subscription s WHERE s.plan_version_id = plan_version.id)
  OR EXISTS (SELECT 1 FROM cp.subscription_change c WHERE c.target_plan_version_id = plan_version.id));
CREATE POLICY subscribed_read ON cp.price_version FOR SELECT TO ti_app USING (
  EXISTS (SELECT 1 FROM cp.subscription s WHERE s.price_version_id = price_version.id)
  OR EXISTS (SELECT 1 FROM cp.subscription_change c WHERE c.target_price_version_id = price_version.id)
  OR EXISTS (SELECT 1 FROM cp.checkout_session k WHERE k.price_version_id = price_version.id));

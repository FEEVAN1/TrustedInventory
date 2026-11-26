-- 0018 (S2 slice 3): billing notices relayed to tenant outboxes (DEC-064). Expand-only.

-- Notices raised by verified billing events. ti_control writes them in the event's transaction; it still has no outbox
-- privilege (SAS-004). The worker relays each one into the tenant's outbox from a tenant transaction (ti_app), which may
-- read its own tenant's rows and set relayed_at only.
CREATE TYPE cp.billing_notice_kind AS ENUM ('SubscriptionActivated','PaymentFailed','PaymentRecovered');

CREATE TABLE cp.billing_notice (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id   uuid NOT NULL REFERENCES ti.tenant(id),
  kind        cp.billing_notice_kind NOT NULL,
  dedupe_key  text NOT NULL UNIQUE CHECK (char_length(dedupe_key) BETWEEN 1 AND 300),   -- one notice per cause
  detail      jsonb NOT NULL CHECK (jsonb_typeof(detail) = 'object'),
  created_at  timestamptz NOT NULL,
  relayed_at  timestamptz,
  UNIQUE (tenant_id, id),
  CHECK (relayed_at IS NULL OR relayed_at >= created_at)
);
CREATE INDEX billing_notice_pending_ix ON cp.billing_notice (tenant_id, created_at) WHERE relayed_at IS NULL;

ALTER TABLE cp.billing_notice ENABLE ROW LEVEL SECURITY;
ALTER TABLE cp.billing_notice FORCE ROW LEVEL SECURITY;
CREATE POLICY control_plane ON cp.billing_notice TO ti_control USING (true) WITH CHECK (true);
GRANT SELECT, INSERT ON cp.billing_notice TO ti_control;
CREATE POLICY tenant_relay ON cp.billing_notice TO ti_app
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
GRANT SELECT ON cp.billing_notice TO ti_app;
GRANT UPDATE (relayed_at) ON cp.billing_notice TO ti_app;

-- A relayed notice stays relayed: relayed_at is set once and nothing else changes.
CREATE FUNCTION cp.billing_notice_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.relayed_at IS NOT NULL
     OR (NEW.id, NEW.tenant_id, NEW.kind, NEW.dedupe_key, NEW.detail, NEW.created_at)
        IS DISTINCT FROM (OLD.id, OLD.tenant_id, OLD.kind, OLD.dedupe_key, OLD.detail, OLD.created_at) THEN
    RAISE EXCEPTION 'billing_notice % is immutable once relayed', OLD.id USING ERRCODE = 'integrity_constraint_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER billing_notice_guard BEFORE UPDATE ON cp.billing_notice FOR EACH ROW EXECUTE FUNCTION cp.billing_notice_guard();

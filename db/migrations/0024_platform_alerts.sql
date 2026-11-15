-- 0024 (S3 slice 5): persisted platform alerts (SOP-003, DEC-074). Expand-only.
-- Platform-wide service signals, never tenant data: each alert names a kind and carries counts and ages only. One open alert per
-- kind and scope (dedupe_key); the monitor closes it when the condition clears. Acknowledgement records who and when.
CREATE TYPE cp.platform_alert_kind AS ENUM ('WebhookBacklog', 'EntitlementMismatch', 'ProvisioningFailures', 'ReconciliationBacklog',
  'UnpaidInvoices');
CREATE TYPE cp.platform_alert_status AS ENUM ('Open', 'Acknowledged', 'Closed');

CREATE TABLE cp.platform_alert (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  kind              cp.platform_alert_kind NOT NULL,
  severity          text NOT NULL CHECK (severity IN ('Warning', 'Critical')),
  status            cp.platform_alert_status NOT NULL DEFAULT 'Open',
  dedupe_key        text NOT NULL CHECK (char_length(dedupe_key) BETWEEN 1 AND 300),
  detail            jsonb NOT NULL CHECK (jsonb_typeof(detail) = 'object'),
  opened_at         timestamptz NOT NULL,
  last_seen_at      timestamptz NOT NULL,
  acknowledged_by   ti.subject_text,
  acknowledged_at   timestamptz,
  acknowledgement   text CHECK (acknowledgement IS NULL OR char_length(acknowledgement) BETWEEN 1 AND 1000),
  closed_at         timestamptz,
  version           integer NOT NULL DEFAULT 1 CHECK (version > 0),
  CHECK ((status = 'Closed') = (closed_at IS NOT NULL)),
  CHECK ((acknowledged_at IS NULL) = (acknowledged_by IS NULL)),
  CHECK (status <> 'Acknowledged' OR acknowledged_at IS NOT NULL),
  CHECK (last_seen_at >= opened_at)
);
CREATE UNIQUE INDEX platform_alert_open_uq ON cp.platform_alert (dedupe_key) WHERE status <> 'Closed';
CREATE INDEX platform_alert_status_ix ON cp.platform_alert (status, opened_at DESC);

ALTER TABLE cp.platform_alert ENABLE ROW LEVEL SECURITY;
ALTER TABLE cp.platform_alert FORCE ROW LEVEL SECURITY;
CREATE POLICY control_plane ON cp.platform_alert TO ti_control USING (true) WITH CHECK (true);
GRANT SELECT, INSERT, UPDATE ON cp.platform_alert TO ti_control;

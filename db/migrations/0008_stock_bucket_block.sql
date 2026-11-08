-- 0008 (M2): stock blocks raised by ledger reconciliation (OPS-001, DEC-049).
-- One row per bucket per alert. A block holds while its alert is not Closed; closing the alert is the audited
-- operator procedure, and the rows stay as evidence (append-only). Stock commands refuse to change a blocked
-- bucket with 409 STOCK_BLOCKED.

CREATE TABLE ti.stock_bucket_block (
  tenant_id     uuid NOT NULL REFERENCES ti.tenant(id),
  bucket_id     uuid NOT NULL,
  alert_id      uuid NOT NULL,
  expected_qty  numeric NOT NULL,          -- on hand recomputed from the ledger
  stored_qty    ti.qty NOT NULL,           -- on hand found in the projection
  created_at    timestamptz NOT NULL DEFAULT now(),
  created_by    ti.subject_text NOT NULL,
  PRIMARY KEY (tenant_id, bucket_id, alert_id),
  FOREIGN KEY (tenant_id, bucket_id) REFERENCES ti.stock_bucket (tenant_id, id),
  FOREIGN KEY (tenant_id, alert_id) REFERENCES ti.alert (tenant_id, id),
  CHECK (expected_qty IS DISTINCT FROM stored_qty)
);
CREATE INDEX stock_bucket_block_alert_ix ON ti.stock_bucket_block (tenant_id, alert_id);

CREATE TRIGGER stock_bucket_block_append_only BEFORE UPDATE OR DELETE ON ti.stock_bucket_block
  FOR EACH ROW EXECUTE FUNCTION ti.reject_mutation();
CREATE TRIGGER stock_bucket_block_no_truncate BEFORE TRUNCATE ON ti.stock_bucket_block
  FOR EACH STATEMENT EXECUTE FUNCTION ti.reject_mutation();

ALTER TABLE ti.stock_bucket_block ENABLE ROW LEVEL SECURITY;
ALTER TABLE ti.stock_bucket_block FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON ti.stock_bucket_block TO ti_app
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
GRANT SELECT, INSERT ON ti.stock_bucket_block TO ti_app;

-- 0022 (S3 slice 1): workspace closure and platform lifecycle controls (LFC-002, DEC-070). Expand-only.
ALTER TABLE cp.deletion_record
  ADD COLUMN idempotency_key            uuid,
  ADD COLUMN outstanding_invoice_count  integer NOT NULL DEFAULT 0 CHECK (outstanding_invoice_count >= 0),
  ADD COLUMN archive_export_id          uuid,
  ADD COLUMN version                    integer NOT NULL DEFAULT 1 CHECK (version > 0);
CREATE UNIQUE INDEX deletion_record_idempotency_uq ON cp.deletion_record (tenant_id, idempotency_key)
  WHERE idempotency_key IS NOT NULL;

-- Retention or legal hold instruction (LFC-004): who asked and on what reference; the flag alone blocks deletion.
ALTER TABLE ti.tenant
  ADD COLUMN retention_hold_reason     ti.reason_text,
  ADD COLUMN retention_hold_reference  text CHECK (retention_hold_reference IS NULL OR char_length(retention_hold_reference) BETWEEN 1 AND 200),
  ADD CONSTRAINT tenant_retention_hold_reason_ck CHECK (NOT retention_hold OR retention_hold_reason IS NOT NULL);

-- The owner's closure status is read in a tenant transaction (ti_app), limited to the caller's tenant like other cp reads.
CREATE POLICY tenant_read ON cp.deletion_record FOR SELECT TO ti_app
  USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
GRANT SELECT ON cp.deletion_record TO ti_app;

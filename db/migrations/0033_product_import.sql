-- 0033 (M6, S1 carry-over): the optional import of the setup checklist (spec ONB-004, DEC-099). Expand-only.
-- A product import is all or nothing: every row is validated, then every product is created in one transaction with its own audit
-- event, or nothing is. This table records each completed import (never the file), so the checklist can show the step as done.
CREATE TABLE ti.product_import (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id   uuid NOT NULL REFERENCES ti.tenant(id),
  row_count   integer NOT NULL CHECK (row_count BETWEEN 1 AND 1000),
  digest      bytea NOT NULL CHECK (octet_length(digest) = 32),   -- SHA-256 of the file as received
  created_at  timestamptz NOT NULL,
  created_by  ti.subject_text NOT NULL,
  UNIQUE (tenant_id, id)
);
CREATE INDEX product_import_created_ix ON ti.product_import (tenant_id, created_at DESC, id DESC);

DO $$
BEGIN
  EXECUTE 'CREATE TRIGGER product_import_append_only BEFORE UPDATE OR DELETE ON ti.product_import FOR EACH ROW EXECUTE FUNCTION ti.reject_mutation()';
  EXECUTE 'CREATE TRIGGER product_import_no_truncate BEFORE TRUNCATE ON ti.product_import FOR EACH STATEMENT EXECUTE FUNCTION ti.reject_mutation()';
  EXECUTE 'ALTER TABLE ti.product_import ENABLE ROW LEVEL SECURITY';
  EXECUTE 'ALTER TABLE ti.product_import FORCE ROW LEVEL SECURITY';
  EXECUTE $p$CREATE POLICY tenant_isolation ON ti.product_import TO ti_app
             USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
             WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)$p$;
  -- The approved purge (DEC-072) covers this table like every tenant table.
  EXECUTE 'CREATE POLICY purge ON ti.product_import TO ti_purger USING (true)';
  EXECUTE 'GRANT SELECT, DELETE ON ti.product_import TO ti_purger';
  EXECUTE 'GRANT SELECT, INSERT ON ti.product_import TO ti_app';
END $$;

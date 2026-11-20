-- 0032 (M6, S1 carry-over): opt-in sample data (spec ONB-004, DEC-098). Expand-only.
-- A workspace owner may add one labelled sample set (a warehouse with zone and bins, a unit, products and partners) to explore the
-- screens, and remove it again. Sample data is isolated from production quantities: no stock bucket may ever hold a sample product or
-- sit in a sample warehouse (trigger below), so sample records never mix with real stock and onboarding never creates live stock.
CREATE TABLE ti.sample_data_set (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id   uuid NOT NULL REFERENCES ti.tenant(id),
  status      text NOT NULL CHECK (status IN ('Active', 'Removed')),
  version     integer NOT NULL DEFAULT 1 CHECK (version > 0),
  created_at  timestamptz NOT NULL,
  created_by  ti.subject_text NOT NULL,
  removed_at  timestamptz,
  removed_by  ti.subject_text,
  UNIQUE (tenant_id, id),
  CHECK ((status = 'Removed') = (removed_at IS NOT NULL) AND (removed_at IS NULL) = (removed_by IS NULL))
);
-- At most one active set per workspace.
CREATE UNIQUE INDEX sample_data_set_active_uq ON ti.sample_data_set (tenant_id) WHERE status = 'Active';

CREATE TABLE ti.sample_data_item (
  tenant_id    uuid NOT NULL REFERENCES ti.tenant(id),
  set_id       uuid NOT NULL,
  object_type  text NOT NULL CHECK (object_type IN ('Warehouse', 'Zone', 'Bin', 'Unit', 'Product', 'Partner')),
  object_id    uuid NOT NULL,
  PRIMARY KEY (tenant_id, object_id),
  FOREIGN KEY (tenant_id, set_id) REFERENCES ti.sample_data_set (tenant_id, id)
);
CREATE INDEX sample_data_item_set_ix ON ti.sample_data_item (tenant_id, set_id);

-- Isolation: a sample product or warehouse never holds stock, whichever path writes the bucket.
CREATE FUNCTION ti.sample_stock_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF EXISTS (SELECT 1 FROM ti.sample_data_item i
             WHERE i.tenant_id = NEW.tenant_id AND i.object_id IN (NEW.product_id, NEW.warehouse_id)
               AND i.object_type IN ('Product', 'Warehouse')) THEN
    RAISE EXCEPTION 'Sample data never holds stock (ONB-004)' USING ERRCODE = 'TI002';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER sample_stock_guard BEFORE INSERT ON ti.stock_bucket FOR EACH ROW EXECUTE FUNCTION ti.sample_stock_guard();

-- Items are written once with their set; the set changes only to Removed.
CREATE TRIGGER sample_data_item_append_only BEFORE UPDATE OR DELETE ON ti.sample_data_item FOR EACH ROW EXECUTE FUNCTION ti.reject_mutation();

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['sample_data_set', 'sample_data_item'] LOOP
    EXECUTE format('ALTER TABLE ti.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE ti.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format($p$CREATE POLICY tenant_isolation ON ti.%I TO ti_app
                     USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                     WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)$p$, t);
    -- The approved purge (DEC-072) covers these tables like every tenant table.
    EXECUTE format('CREATE POLICY purge ON ti.%I TO ti_purger USING (true)', t);
    EXECUTE format('GRANT SELECT, DELETE ON ti.%I TO ti_purger', t);
  END LOOP;
END $$;
GRANT SELECT, INSERT, UPDATE ON ti.sample_data_set TO ti_app;
GRANT SELECT, INSERT ON ti.sample_data_item TO ti_app;

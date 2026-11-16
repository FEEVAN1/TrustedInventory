-- 0026 (M5 slice 2): recalls (EXC-006, EXC-007, DEC-076). Expand-only. Types ti.recall_status and ti.recall_outreach_status
-- exist since 0001. A recall flag and its history are never deleted (only the approved tenant purge removes them, DEC-072).
CREATE TABLE ti.recall (
  id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id            uuid NOT NULL REFERENCES ti.tenant(id),
  product_id           uuid NOT NULL,
  all_lots             boolean NOT NULL,           -- no lot or serial named: every lot and serial of the product
  reason               ti.reason_text NOT NULL,
  effective_at         timestamptz NOT NULL,
  status               ti.recall_status NOT NULL DEFAULT 'Open',
  closure_approval_id  uuid,
  closure_reason       ti.reason_text,
  closed_at            timestamptz,
  closed_by            ti.subject_text,
  last_modified_by     ti.subject_text NOT NULL,
  created_at           timestamptz NOT NULL DEFAULT now(),
  created_by           ti.subject_text NOT NULL,
  updated_at           timestamptz NOT NULL DEFAULT now(),
  version              integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, id, product_id),
  FOREIGN KEY (tenant_id, product_id) REFERENCES ti.product (tenant_id, id),
  FOREIGN KEY (tenant_id, closure_approval_id) REFERENCES ti.approval (tenant_id, id),
  CHECK ((status = 'Closed') = (closed_at IS NOT NULL AND closure_approval_id IS NOT NULL AND closed_by IS NOT NULL AND closure_reason IS NOT NULL))
);
CREATE INDEX recall_open_ix ON ti.recall (tenant_id, product_id) WHERE status = 'Open';

CREATE TABLE ti.recall_item (
  id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id    uuid NOT NULL REFERENCES ti.tenant(id),
  recall_id    uuid NOT NULL,
  product_id   uuid NOT NULL,
  lot_id       uuid,
  serial_id    uuid,
  created_at   timestamptz NOT NULL DEFAULT now(),
  created_by   ti.subject_text NOT NULL,
  UNIQUE (tenant_id, id),
  UNIQUE NULLS NOT DISTINCT (tenant_id, recall_id, lot_id, serial_id),
  FOREIGN KEY (tenant_id, recall_id, product_id) REFERENCES ti.recall (tenant_id, id, product_id),
  FOREIGN KEY (tenant_id, product_id, lot_id) REFERENCES ti.lot (tenant_id, product_id, id),
  FOREIGN KEY (tenant_id, product_id, serial_id) REFERENCES ti.serial (tenant_id, product_id, id),
  CHECK ((lot_id IS NULL) <> (serial_id IS NULL))
);
CREATE INDEX recall_item_lot_ix ON ti.recall_item (tenant_id, lot_id) WHERE lot_id IS NOT NULL;
CREATE INDEX recall_item_serial_ix ON ti.recall_item (tenant_id, serial_id) WHERE serial_id IS NOT NULL;

-- Manual outreach log (EXC-006): one row per record, append-only, so the history of each customer's status is kept.
CREATE TABLE ti.recall_outreach (
  id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id    uuid NOT NULL REFERENCES ti.tenant(id),
  recall_id    uuid NOT NULL,
  customer_id  uuid NOT NULL,
  status       ti.recall_outreach_status NOT NULL,
  note         text CHECK (note IS NULL OR char_length(note) BETWEEN 1 AND 2000),
  created_at   timestamptz NOT NULL DEFAULT now(),
  created_by   ti.subject_text NOT NULL,
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, recall_id) REFERENCES ti.recall (tenant_id, id),
  FOREIGN KEY (tenant_id, customer_id) REFERENCES ti.partner (tenant_id, id)
);
CREATE INDEX recall_outreach_recall_ix ON ti.recall_outreach (tenant_id, recall_id, customer_id, created_at);

-- Is this stock covered by an open recall? Used by allocation, picking, dispatch, release and closure (EXC-006).
CREATE FUNCTION ti.recall_covers(p_product uuid, p_lot uuid, p_serial uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
  SELECT EXISTS (
    SELECT 1 FROM ti.recall r
    WHERE r.status = 'Open' AND r.product_id = p_product
      AND (r.all_lots OR EXISTS (
        SELECT 1 FROM ti.recall_item i
        WHERE i.tenant_id = r.tenant_id AND i.recall_id = r.id
          AND ((p_lot IS NOT NULL AND i.lot_id = p_lot) OR (p_serial IS NOT NULL AND i.serial_id = p_serial)))))
$$;

DO $$
DECLARE t text;
BEGIN
  EXECUTE 'CREATE TRIGGER recall_item_append_only BEFORE UPDATE OR DELETE ON ti.recall_item FOR EACH ROW EXECUTE FUNCTION ti.reject_mutation()';
  EXECUTE 'CREATE TRIGGER recall_outreach_append_only BEFORE UPDATE OR DELETE ON ti.recall_outreach FOR EACH ROW EXECUTE FUNCTION ti.reject_mutation()';
  FOREACH t IN ARRAY ARRAY['recall', 'recall_item', 'recall_outreach'] LOOP
    EXECUTE format('ALTER TABLE ti.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE ti.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format($p$CREATE POLICY tenant_isolation ON ti.%I TO ti_app
                     USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                     WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)$p$, t);
    -- The approved purge (DEC-072) covers new tenant tables too.
    EXECUTE format('CREATE POLICY purge ON ti.%I TO ti_purger USING (true)', t);
    EXECUTE format('GRANT SELECT, DELETE ON ti.%I TO ti_purger', t);
    IF t = 'recall' THEN
      EXECUTE format('GRANT SELECT, INSERT, UPDATE ON ti.%I TO ti_app', t);
    ELSE
      EXECUTE format('GRANT SELECT, INSERT ON ti.%I TO ti_app', t);
    END IF;
  END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION ti.recall_covers(uuid, uuid, uuid) TO ti_app;

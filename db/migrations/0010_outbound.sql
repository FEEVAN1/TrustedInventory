-- 0010 (M3): outbound orders, reservations, picks and shipments (OUT-001..006, STK-002, STK-005, STK-006, STK-008).
-- Source: db/design/schema-draft.sql (validated in M0), plus the movement -> reservation foreign key deferred by 0006
-- and the Draft-only guard on order lines (DEC-051, as DEC-047 for purchase orders).

CREATE TABLE ti.outbound_order (
  id                      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id               uuid NOT NULL REFERENCES ti.tenant(id),
  customer_id             uuid NOT NULL,
  warehouse_id            uuid NOT NULL,
  reference               ti.code_text NOT NULL,
  planned_dispatch_date   date NOT NULL,
  status                  ti.outbound_order_status NOT NULL DEFAULT 'Draft',
  confirmed_by            ti.subject_text,
  confirmed_at            timestamptz,
  cancel_reason           ti.reason_text,
  created_at              timestamptz NOT NULL DEFAULT now(),
  created_by              ti.subject_text NOT NULL,
  updated_at              timestamptz NOT NULL DEFAULT now(),
  version                 integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, warehouse_id, id),
  UNIQUE (tenant_id, reference),
  FOREIGN KEY (tenant_id, customer_id) REFERENCES ti.partner (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES ti.warehouse (tenant_id, id),
  CHECK ((status = 'Cancelled') = (cancel_reason IS NOT NULL)),
  CHECK ((confirmed_at IS NULL) = (confirmed_by IS NULL))
);
CREATE INDEX outbound_order_status_ix ON ti.outbound_order (tenant_id, status, planned_dispatch_date);

CREATE TABLE ti.outbound_order_line (
  id                              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id                       uuid NOT NULL REFERENCES ti.tenant(id),
  order_id                        uuid NOT NULL,
  line_number                     integer NOT NULL CHECK (line_number > 0),
  product_id                      uuid NOT NULL,
  requested_base_qty              ti.pos_qty NOT NULL,
  min_remaining_shelf_life_days   integer NOT NULL DEFAULT 0 CHECK (min_remaining_shelf_life_days >= 0),
  allocated_base_qty              ti.qty NOT NULL DEFAULT 0,  -- active + picked + consumed reservations
  picked_base_qty                 ti.qty NOT NULL DEFAULT 0,
  dispatched_base_qty             ti.qty NOT NULL DEFAULT 0,
  created_at                      timestamptz NOT NULL DEFAULT now(),
  created_by                      ti.subject_text NOT NULL,
  updated_at                      timestamptz NOT NULL DEFAULT now(),
  version                         integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, id, product_id),
  UNIQUE (tenant_id, order_id, line_number),
  FOREIGN KEY (tenant_id, order_id) REFERENCES ti.outbound_order (tenant_id, id),
  FOREIGN KEY (tenant_id, product_id) REFERENCES ti.product (tenant_id, id),
  CHECK (allocated_base_qty <= requested_base_qty),
  CHECK (dispatched_base_qty <= picked_base_qty),
  CHECK (picked_base_qty <= allocated_base_qty)
);

CREATE TABLE ti.reservation (
  id                      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id               uuid NOT NULL REFERENCES ti.tenant(id),
  order_line_id           uuid NOT NULL,
  product_id              uuid NOT NULL,
  bucket_id               uuid NOT NULL,          -- storage bucket while Active; staging bucket once Picked (STK-002)
  qty                     ti.pos_qty NOT NULL,
  state                   ti.reservation_state NOT NULL DEFAULT 'Active',
  expires_at              timestamptz,
  parent_reservation_id   uuid,                   -- partial pick splits segments (section 13)
  created_at              timestamptz NOT NULL DEFAULT now(),
  created_by              ti.subject_text NOT NULL,
  updated_at              timestamptz NOT NULL DEFAULT now(),
  version                 integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, order_line_id, product_id) REFERENCES ti.outbound_order_line (tenant_id, id, product_id),
  FOREIGN KEY (tenant_id, bucket_id, product_id) REFERENCES ti.stock_bucket (tenant_id, id, product_id),
  FOREIGN KEY (tenant_id, parent_reservation_id) REFERENCES ti.reservation (tenant_id, id),
  CHECK ((state = 'Active') = (expires_at IS NOT NULL))   -- picked reservations never expire (STK-008)
);
CREATE INDEX reservation_live_ix ON ti.reservation (tenant_id, bucket_id) WHERE state IN ('Active','Picked');
CREATE INDEX reservation_expiry_ix ON ti.reservation (tenant_id, expires_at) WHERE state = 'Active';
CREATE INDEX reservation_order_line_ix ON ti.reservation (tenant_id, order_line_id);

-- Deferred from 0006: picks, unpicks and dispatches name the reservation they move (CHECK in 0006).
ALTER TABLE ti.stock_movement
  ADD CONSTRAINT stock_movement_reservation_fk FOREIGN KEY (tenant_id, reservation_id) REFERENCES ti.reservation (tenant_id, id);

CREATE TABLE ti.pick (
  id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id          uuid NOT NULL REFERENCES ti.tenant(id),
  reservation_id     uuid NOT NULL,             -- the Active segment this task directs (OUT-003)
  warehouse_id       uuid NOT NULL,
  source_bin_id      uuid NOT NULL,
  product_id         uuid NOT NULL,
  lot_id             uuid,
  task_qty           ti.pos_qty NOT NULL,
  picked_qty         ti.qty NOT NULL DEFAULT 0,
  assignee_subject   ti.subject_text,
  status             ti.task_status NOT NULL DEFAULT 'Open',
  created_at         timestamptz NOT NULL DEFAULT now(),
  created_by         ti.subject_text NOT NULL,
  updated_at         timestamptz NOT NULL DEFAULT now(),
  version            integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, reservation_id),
  FOREIGN KEY (tenant_id, reservation_id) REFERENCES ti.reservation (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id, source_bin_id) REFERENCES ti.bin (tenant_id, warehouse_id, id),
  FOREIGN KEY (tenant_id, product_id) REFERENCES ti.product (tenant_id, id),
  FOREIGN KEY (tenant_id, product_id, lot_id) REFERENCES ti.lot (tenant_id, product_id, id),
  CHECK (picked_qty <= task_qty)
);
CREATE INDEX pick_assignee_ix ON ti.pick (tenant_id, warehouse_id, status, assignee_subject);

-- Immutable record of each scan-validated confirmation (OUT-004).
CREATE TABLE ti.pick_confirmation (
  id                       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id                uuid NOT NULL REFERENCES ti.tenant(id),
  pick_id                  uuid NOT NULL,
  picked_qty               ti.pos_qty NOT NULL,
  picked_reservation_id    uuid NOT NULL,       -- resulting Picked segment
  staging_bin_id           uuid NOT NULL,
  warehouse_id             uuid NOT NULL,
  scanned                  jsonb NOT NULL,      -- scanned bin/product/lot/serial identifiers as received
  movement_id              uuid NOT NULL,
  created_at               timestamptz NOT NULL DEFAULT now(),
  created_by               ti.subject_text NOT NULL,
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, pick_id) REFERENCES ti.pick (tenant_id, id),
  FOREIGN KEY (tenant_id, picked_reservation_id) REFERENCES ti.reservation (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id, staging_bin_id) REFERENCES ti.bin (tenant_id, warehouse_id, id),
  FOREIGN KEY (tenant_id, movement_id) REFERENCES ti.stock_movement (tenant_id, id)
);

CREATE TABLE ti.shipment (
  id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id            uuid NOT NULL REFERENCES ti.tenant(id),
  order_id             uuid NOT NULL,
  warehouse_id         uuid NOT NULL,
  dispatch_reference   ti.code_text NOT NULL,
  status               ti.shipment_status NOT NULL DEFAULT 'Dispatched',
  dispatched_at        timestamptz NOT NULL,
  created_at           timestamptz NOT NULL DEFAULT now(),
  created_by           ti.subject_text NOT NULL,
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, order_id, dispatch_reference),
  FOREIGN KEY (tenant_id, warehouse_id, order_id) REFERENCES ti.outbound_order (tenant_id, warehouse_id, id)
);

CREATE TABLE ti.shipment_line (
  id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id       uuid NOT NULL REFERENCES ti.tenant(id),
  shipment_id     uuid NOT NULL,
  order_line_id   uuid NOT NULL,
  product_id      uuid NOT NULL,
  lot_id          uuid,
  serial_id       uuid,
  base_qty        ti.pos_qty NOT NULL,
  returned_qty    ti.qty NOT NULL DEFAULT 0,
  movement_id     uuid NOT NULL,
  created_at      timestamptz NOT NULL DEFAULT now(),
  created_by      ti.subject_text NOT NULL,
  updated_at      timestamptz NOT NULL DEFAULT now(),
  version         integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, id, product_id),
  FOREIGN KEY (tenant_id, shipment_id) REFERENCES ti.shipment (tenant_id, id),
  FOREIGN KEY (tenant_id, order_line_id, product_id) REFERENCES ti.outbound_order_line (tenant_id, id, product_id),
  FOREIGN KEY (tenant_id, product_id, lot_id) REFERENCES ti.lot (tenant_id, product_id, id),
  FOREIGN KEY (tenant_id, product_id, serial_id) REFERENCES ti.serial (tenant_id, product_id, id),
  FOREIGN KEY (tenant_id, movement_id) REFERENCES ti.stock_movement (tenant_id, id),
  CHECK (returned_qty <= base_qty)                -- EXC-005 / AT-EXC-03
);
CREATE INDEX shipment_line_lot_ix ON ti.shipment_line (tenant_id, product_id, lot_id);

-- Order lines change their ordered fields only while the order is Draft (DEC-051).
CREATE FUNCTION ti.outbound_order_line_draft_only() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  line ti.outbound_order_line := CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
  order_status ti.outbound_order_status;
BEGIN
  IF TG_OP = 'UPDATE'
     AND (NEW.order_id, NEW.line_number, NEW.product_id, NEW.requested_base_qty, NEW.min_remaining_shelf_life_days)
         IS NOT DISTINCT FROM
         (OLD.order_id, OLD.line_number, OLD.product_id, OLD.requested_base_qty, OLD.min_remaining_shelf_life_days) THEN
    RETURN NEW;  -- allocation, pick and dispatch counters and stamps only
  END IF;
  SELECT status INTO order_status FROM ti.outbound_order WHERE tenant_id = line.tenant_id AND id = line.order_id;
  IF order_status IS DISTINCT FROM 'Draft' THEN
    RAISE EXCEPTION 'outbound order lines can change only while the order is Draft (order is %)', order_status
      USING ERRCODE = 'check_violation';
  END IF;
  RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END $$;

CREATE TRIGGER outbound_order_line_draft_only
  BEFORE INSERT OR UPDATE OR DELETE ON ti.outbound_order_line
  FOR EACH ROW EXECUTE FUNCTION ti.outbound_order_line_draft_only();

-- Append-only enforcement, RLS and grants (migration-design.md §2).
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['pick_confirmation','shipment'] LOOP
    EXECUTE format('CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON ti.%I FOR EACH ROW EXECUTE FUNCTION ti.reject_mutation()', t || '_append_only', t);
    EXECUTE format('CREATE TRIGGER %I BEFORE TRUNCATE ON ti.%I FOR EACH STATEMENT EXECUTE FUNCTION ti.reject_mutation()', t || '_no_truncate', t);
  END LOOP;

  FOREACH t IN ARRAY ARRAY['outbound_order','outbound_order_line','reservation','pick','pick_confirmation','shipment','shipment_line'] LOOP
    EXECUTE format('ALTER TABLE ti.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE ti.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format($p$CREATE POLICY tenant_isolation ON ti.%I TO ti_app
                     USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                     WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)$p$, t);
    IF t IN ('pick_confirmation','shipment') THEN
      EXECUTE format('GRANT SELECT, INSERT ON ti.%I TO ti_app', t);
    ELSE
      EXECUTE format('GRANT SELECT, INSERT, UPDATE ON ti.%I TO ti_app', t);
    END IF;
  END LOOP;
END $$;
GRANT DELETE ON ti.outbound_order_line TO ti_app;  -- draft line replacement only (trigger above)

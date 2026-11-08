-- 0006 (M2): inbound ledger — lots, serials, stock buckets, purchase orders, receipts, putaway tasks,
-- the append-only movement ledger and permanent document operation keys (STK-004, STK-007, INB-001..006).
-- Source: db/design/schema-draft.sql (validated in M0). The movement -> reservation foreign key is added by the
-- M3 migration that creates ti.reservation.

-- ===========================================================================
-- Lots and serials
-- ===========================================================================
CREATE TABLE ti.lot (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id         uuid NOT NULL REFERENCES ti.tenant(id),
  product_id        uuid NOT NULL,
  handling_profile  ti.handling_profile NOT NULL,  -- copied from product; FK below forbids divergence
  lot_number        ti.identifier_text NOT NULL,
  expiry_date       date,
  supplier_ref      text CHECK (supplier_ref IS NULL OR char_length(supplier_ref) BETWEEN 1 AND 200),
  created_at        timestamptz NOT NULL DEFAULT now(),
  created_by        ti.subject_text NOT NULL,
  updated_at        timestamptz NOT NULL DEFAULT now(),
  version           integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, product_id, id),
  UNIQUE (tenant_id, product_id, lot_number),
  FOREIGN KEY (tenant_id, product_id, handling_profile) REFERENCES ti.product (tenant_id, id, handling_profile),
  CHECK (handling_profile = 'General' OR expiry_date IS NOT NULL)  -- medical lots require expiry
);
CREATE INDEX lot_expiry_ix ON ti.lot (tenant_id, product_id, expiry_date);

CREATE TABLE ti.serial (
  id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id      uuid NOT NULL REFERENCES ti.tenant(id),
  product_id     uuid NOT NULL,
  lot_id         uuid,
  serial_number  ti.identifier_text NOT NULL,
  created_at     timestamptz NOT NULL DEFAULT now(),
  created_by     ti.subject_text NOT NULL,
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, product_id, id),
  UNIQUE (tenant_id, product_id, serial_number),
  FOREIGN KEY (tenant_id, product_id) REFERENCES ti.product (tenant_id, id),
  FOREIGN KEY (tenant_id, product_id, lot_id) REFERENCES ti.lot (tenant_id, product_id, id)
);

-- ===========================================================================
-- Stock
-- ===========================================================================
CREATE TABLE ti.stock_bucket (
  id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id       uuid NOT NULL REFERENCES ti.tenant(id),
  warehouse_id    uuid NOT NULL,
  bin_id          uuid NOT NULL,
  product_id      uuid NOT NULL,
  lot_id          uuid,
  serial_id       uuid,
  quality_status  ti.quality_status NOT NULL,
  received_at     timestamptz NOT NULL,           -- FIFO/LIFO ordering key (STK-005); earliest receipt of this stock
  on_hand_qty     ti.qty NOT NULL DEFAULT 0,
  reserved_qty    ti.qty NOT NULL DEFAULT 0,
  created_at      timestamptz NOT NULL DEFAULT now(),
  created_by      ti.subject_text NOT NULL,
  updated_at      timestamptz NOT NULL DEFAULT now(),
  version         integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, id, product_id),
  UNIQUE NULLS NOT DISTINCT (tenant_id, bin_id, product_id, lot_id, serial_id, quality_status),
  FOREIGN KEY (tenant_id, warehouse_id, bin_id) REFERENCES ti.bin (tenant_id, warehouse_id, id),
  FOREIGN KEY (tenant_id, product_id) REFERENCES ti.product (tenant_id, id),
  FOREIGN KEY (tenant_id, product_id, lot_id) REFERENCES ti.lot (tenant_id, product_id, id),
  FOREIGN KEY (tenant_id, product_id, serial_id) REFERENCES ti.serial (tenant_id, product_id, id),
  CHECK (reserved_qty <= on_hand_qty),                                     -- STK-001
  CHECK (serial_id IS NULL OR (on_hand_qty IN (0, 1) AND reserved_qty IN (0, 1)))
);
CREATE INDEX stock_bucket_location_ix ON ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id);
CREATE INDEX stock_bucket_lot_ix ON ti.stock_bucket (tenant_id, product_id, lot_id);
-- One physical serial unit may be on hand in at most one bucket.
CREATE UNIQUE INDEX stock_bucket_serial_on_hand_uq ON ti.stock_bucket (tenant_id, serial_id)
  WHERE serial_id IS NOT NULL AND on_hand_qty > 0;

-- ===========================================================================
-- Inbound
-- ===========================================================================
CREATE TABLE ti.purchase_order (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id        uuid NOT NULL REFERENCES ti.tenant(id),
  supplier_id      uuid NOT NULL,
  warehouse_id     uuid NOT NULL,
  reference        ti.code_text NOT NULL,
  status           ti.purchase_order_status NOT NULL DEFAULT 'Draft',
  approved_by      ti.subject_text,
  approved_at      timestamptz,
  short_close_reason ti.reason_text,
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       ti.subject_text NOT NULL,
  updated_at       timestamptz NOT NULL DEFAULT now(),
  version          integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, warehouse_id, id),
  UNIQUE (tenant_id, reference),
  FOREIGN KEY (tenant_id, supplier_id) REFERENCES ti.partner (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES ti.warehouse (tenant_id, id),
  CHECK ((status = 'ShortClosed') = (short_close_reason IS NOT NULL))
);
CREATE INDEX purchase_order_status_ix ON ti.purchase_order (tenant_id, status, created_at);

CREATE TABLE ti.purchase_order_line (
  id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id           uuid NOT NULL REFERENCES ti.tenant(id),
  purchase_order_id   uuid NOT NULL,
  line_number         integer NOT NULL CHECK (line_number > 0),
  product_id          uuid NOT NULL,
  uom_id              uuid NOT NULL,
  uom_factor_to_base  numeric NOT NULL CHECK (uom_factor_to_base > 0),  -- frozen conversion snapshot
  ordered_uom_qty     ti.pos_qty NOT NULL,
  ordered_base_qty    ti.pos_qty NOT NULL,
  received_base_qty   ti.qty NOT NULL DEFAULT 0,
  created_at          timestamptz NOT NULL DEFAULT now(),
  created_by          ti.subject_text NOT NULL,
  updated_at          timestamptz NOT NULL DEFAULT now(),
  version             integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, id, product_id),
  UNIQUE (tenant_id, purchase_order_id, line_number),
  FOREIGN KEY (tenant_id, purchase_order_id) REFERENCES ti.purchase_order (tenant_id, id),
  FOREIGN KEY (tenant_id, product_id) REFERENCES ti.product (tenant_id, id),
  FOREIGN KEY (tenant_id, uom_id) REFERENCES ti.unit (tenant_id, id),
  CHECK (received_base_qty <= ordered_base_qty)   -- INB-001 zero over-receipt tolerance (D08)
);

CREATE TABLE ti.receipt (
  id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id          uuid NOT NULL REFERENCES ti.tenant(id),
  purchase_order_id  uuid NOT NULL,
  warehouse_id       uuid NOT NULL,
  status             ti.receipt_status NOT NULL,
  received_at        timestamptz NOT NULL,
  closed_at          timestamptz,
  created_at         timestamptz NOT NULL DEFAULT now(),
  created_by         ti.subject_text NOT NULL,
  updated_at         timestamptz NOT NULL DEFAULT now(),
  version            integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id, purchase_order_id) REFERENCES ti.purchase_order (tenant_id, warehouse_id, id)
);
CREATE INDEX receipt_status_ix ON ti.receipt (tenant_id, status, received_at);

CREATE TABLE ti.receipt_line (
  id                       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id                uuid NOT NULL REFERENCES ti.tenant(id),
  receipt_id               uuid NOT NULL,
  purchase_order_line_id   uuid NOT NULL,
  product_id               uuid NOT NULL,
  lot_id                   uuid,
  uom_id                   uuid NOT NULL,
  uom_factor_to_base       numeric NOT NULL CHECK (uom_factor_to_base > 0),
  uom_qty                  ti.pos_qty NOT NULL,
  base_qty                 ti.pos_qty NOT NULL,
  measured_temperature_c   ti.celsius,
  staging_bin_id           uuid NOT NULL,
  warehouse_id             uuid NOT NULL,
  quality_status           ti.quality_status NOT NULL CHECK (quality_status IN ('Available','Quarantine','Hold')),
  inspection_result        ti.inspection_result NOT NULL DEFAULT 'Passed',  -- Failed general stock goes to Hold (INB-004)
  supplier_reference       text CHECK (supplier_reference IS NULL OR char_length(supplier_reference) BETWEEN 1 AND 200),
  scanned_barcode          text CHECK (scanned_barcode IS NULL OR char_length(scanned_barcode) BETWEEN 1 AND 200),  -- raw GS1 string (INB-002)
  quality_reason           ti.reason_text,
  bucket_id                uuid NOT NULL,
  created_at               timestamptz NOT NULL DEFAULT now(),
  created_by               ti.subject_text NOT NULL,
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, receipt_id) REFERENCES ti.receipt (tenant_id, id),
  FOREIGN KEY (tenant_id, purchase_order_line_id, product_id) REFERENCES ti.purchase_order_line (tenant_id, id, product_id),
  FOREIGN KEY (tenant_id, product_id, lot_id) REFERENCES ti.lot (tenant_id, product_id, id),
  FOREIGN KEY (tenant_id, uom_id) REFERENCES ti.unit (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id, staging_bin_id) REFERENCES ti.bin (tenant_id, warehouse_id, id),
  FOREIGN KEY (tenant_id, bucket_id, product_id) REFERENCES ti.stock_bucket (tenant_id, id, product_id),
  CHECK (quality_status <> 'Hold' OR quality_reason IS NOT NULL),  -- INB-003 hold needs a reason
  CHECK (inspection_result = 'Passed' OR quality_status <> 'Available')
);

CREATE TABLE ti.receipt_line_serial (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id        uuid NOT NULL REFERENCES ti.tenant(id),
  receipt_line_id  uuid NOT NULL,
  serial_id        uuid NOT NULL,
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       ti.subject_text NOT NULL,
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, serial_id),                  -- a serial is received once (INB-004)
  FOREIGN KEY (tenant_id, receipt_line_id) REFERENCES ti.receipt_line (tenant_id, id),
  FOREIGN KEY (tenant_id, serial_id) REFERENCES ti.serial (tenant_id, id)
);

-- ===========================================================================
-- Warehouse tasks (putaway in M2; rework and exception tasks reuse the table later)
-- ===========================================================================
CREATE TABLE ti.warehouse_task (
  id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id             uuid NOT NULL REFERENCES ti.tenant(id),
  warehouse_id          uuid NOT NULL,
  type                  ti.task_type NOT NULL,
  status                ti.task_status NOT NULL DEFAULT 'Open',
  receipt_line_id       uuid,
  product_id            uuid,
  lot_id                uuid,
  source_bin_id         uuid,
  recommended_bin_id    uuid,
  task_qty              ti.pos_qty,
  remaining_qty         ti.qty,
  related_type          ti.code_text,           -- e.g. OutboundOrder, Recall, Excursion
  related_id            uuid,
  reason                ti.reason_text,
  assignee_subject      ti.subject_text,
  completed_at          timestamptz,
  created_at            timestamptz NOT NULL DEFAULT now(),
  created_by            ti.subject_text NOT NULL,
  updated_at            timestamptz NOT NULL DEFAULT now(),
  version               integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES ti.warehouse (tenant_id, id),
  FOREIGN KEY (tenant_id, receipt_line_id) REFERENCES ti.receipt_line (tenant_id, id),
  FOREIGN KEY (tenant_id, product_id) REFERENCES ti.product (tenant_id, id),
  FOREIGN KEY (tenant_id, product_id, lot_id) REFERENCES ti.lot (tenant_id, product_id, id),
  FOREIGN KEY (tenant_id, warehouse_id, source_bin_id) REFERENCES ti.bin (tenant_id, warehouse_id, id),
  FOREIGN KEY (tenant_id, warehouse_id, recommended_bin_id) REFERENCES ti.bin (tenant_id, warehouse_id, id),
  CHECK (remaining_qty IS NULL OR remaining_qty <= task_qty),
  CHECK (type <> 'Putaway' OR (receipt_line_id IS NOT NULL AND task_qty IS NOT NULL))
);
CREATE INDEX warehouse_task_open_ix ON ti.warehouse_task (tenant_id, warehouse_id, type, status);

-- ===========================================================================
-- Movement ledger (STK-004): append-only, authoritative.
-- ===========================================================================
CREATE TABLE ti.stock_movement (
  id                      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id               uuid NOT NULL REFERENCES ti.tenant(id),
  movement_type           ti.movement_type NOT NULL,
  operation_key           text NOT NULL CHECK (char_length(operation_key) BETWEEN 1 AND 300),
  product_id              uuid NOT NULL,
  lot_id                  uuid,
  serial_id               uuid,
  source_bucket_id        uuid,
  destination_bucket_id   uuid,
  base_qty                ti.pos_qty NOT NULL,
  document_type           ti.document_type NOT NULL,
  document_id             uuid NOT NULL,
  document_line_id        uuid,
  reservation_id          uuid,
  reversal_of_id          uuid,
  reason                  ti.reason_text,
  actor_subject           ti.subject_text NOT NULL,
  occurred_at             timestamptz NOT NULL,
  recorded_at             timestamptz NOT NULL DEFAULT now(),
  correlation_id          text,
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, operation_key),            -- permanent business operation key (STK-007)
  FOREIGN KEY (tenant_id, source_bucket_id, product_id) REFERENCES ti.stock_bucket (tenant_id, id, product_id),
  FOREIGN KEY (tenant_id, destination_bucket_id, product_id) REFERENCES ti.stock_bucket (tenant_id, id, product_id),
  FOREIGN KEY (tenant_id, product_id, lot_id) REFERENCES ti.lot (tenant_id, product_id, id),
  FOREIGN KEY (tenant_id, product_id, serial_id) REFERENCES ti.serial (tenant_id, product_id, id),
  FOREIGN KEY (tenant_id, reversal_of_id) REFERENCES ti.stock_movement (tenant_id, id),
  CHECK ((movement_type = 'Reversal') = (reversal_of_id IS NOT NULL)),
  CHECK (source_bucket_id IS NOT NULL OR destination_bucket_id IS NOT NULL),
  CHECK (source_bucket_id IS DISTINCT FROM destination_bucket_id),
  CHECK (movement_type NOT IN ('Receipt','AdjustmentIncrease','ReturnReceipt')
         OR (source_bucket_id IS NULL AND destination_bucket_id IS NOT NULL)),
  CHECK (movement_type NOT IN ('Dispatch','TransitDisposition','AdjustmentDecrease','Disposal','SupplierReturn')
         OR (source_bucket_id IS NOT NULL AND destination_bucket_id IS NULL)),
  CHECK (movement_type IN ('Receipt','AdjustmentIncrease','ReturnReceipt','Dispatch','TransitDisposition',
                           'AdjustmentDecrease','Disposal','SupplierReturn','Reversal')
         OR (source_bucket_id IS NOT NULL AND destination_bucket_id IS NOT NULL)),
  CHECK (movement_type NOT IN ('Pick','Unpick','Dispatch') OR reservation_id IS NOT NULL)
);
CREATE INDEX stock_movement_document_ix ON ti.stock_movement (tenant_id, document_type, document_id);
CREATE INDEX stock_movement_lot_ix ON ti.stock_movement (tenant_id, product_id, lot_id, occurred_at);
CREATE INDEX stock_movement_occurred_ix ON ti.stock_movement (tenant_id, occurred_at, id);
CREATE INDEX stock_movement_source_ix ON ti.stock_movement (tenant_id, source_bucket_id);
CREATE INDEX stock_movement_destination_ix ON ti.stock_movement (tenant_id, destination_bucket_id);
CREATE INDEX stock_movement_reversal_ix ON ti.stock_movement (tenant_id, reversal_of_id) WHERE reversal_of_id IS NOT NULL;

-- Permanent command-level keys for operations that must never post twice, even after the
-- 7-day idempotency record expires (STK-007), e.g. 'dispatch:{orderId}:{dispatchReference}'.
CREATE TABLE ti.document_operation (
  tenant_id         uuid NOT NULL REFERENCES ti.tenant(id),
  operation_key     text NOT NULL CHECK (char_length(operation_key) BETWEEN 1 AND 300),
  operation_type    ti.code_text NOT NULL,
  result_reference  jsonb NOT NULL,
  created_at        timestamptz NOT NULL DEFAULT now(),
  created_by        ti.subject_text NOT NULL,
  PRIMARY KEY (tenant_id, operation_key)
);

-- ===========================================================================
-- Append-only enforcement, RLS and grants for the tables created above (migration-design.md §2)
-- ===========================================================================
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['stock_movement','document_operation','receipt_line','receipt_line_serial']
  LOOP
    EXECUTE format('CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON ti.%I FOR EACH ROW EXECUTE FUNCTION ti.reject_mutation()', t || '_append_only', t);
    EXECUTE format('CREATE TRIGGER %I BEFORE TRUNCATE ON ti.%I FOR EACH STATEMENT EXECUTE FUNCTION ti.reject_mutation()', t || '_no_truncate', t);
  END LOOP;

  FOREACH t IN ARRAY ARRAY['lot','serial','stock_bucket','purchase_order','purchase_order_line','receipt','receipt_line',
                           'receipt_line_serial','warehouse_task','stock_movement','document_operation']
  LOOP
    EXECUTE format('ALTER TABLE ti.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE ti.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format($p$CREATE POLICY tenant_isolation ON ti.%I TO ti_app
                     USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                     WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)$p$, t);
    IF t IN ('stock_movement','document_operation','receipt_line','receipt_line_serial') THEN
      EXECUTE format('GRANT SELECT, INSERT ON ti.%I TO ti_app', t);
    ELSE
      EXECUTE format('GRANT SELECT, INSERT, UPDATE ON ti.%I TO ti_app', t);
    END IF;
  END LOOP;
END $$;
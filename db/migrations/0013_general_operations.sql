-- 0013 (M4): approvals, adjustments, transfers, counts, customer returns, attachments and export jobs
-- (IAM-003, IAM-004, SOD-1, EXC-001..005, STK-003, AUD-004, TEN-005, DEC-056).

-- ---------------------------------------------------------------------------------------------------------------
-- Approvals: bind the exact subject version and content digest (IAM-003); the requester never signs (SOD-1).
CREATE TABLE ti.approval (
  id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id            uuid NOT NULL REFERENCES ti.tenant(id),
  subject_type         ti.approval_subject_type NOT NULL,
  subject_id           uuid NOT NULL,
  subject_version      integer NOT NULL CHECK (subject_version > 0),
  content_digest       ti.sha256 NOT NULL,
  action               ti.approval_action NOT NULL,
  request_reason       ti.reason_text NOT NULL,
  requester_subject    ti.subject_text NOT NULL,
  status               ti.approval_status NOT NULL DEFAULT 'Pending',
  approver_subject     ti.subject_text,
  meaning              ti.signature_meaning,
  sign_reason          ti.reason_text,
  reauth_reference     ti.sha256,                 -- digest of the re-auth token only (DEC-009)
  reauth_verified_at   timestamptz,
  signed_at            timestamptz,
  expires_at           timestamptz,
  consumed_at          timestamptz,
  consumed_operation   text CHECK (consumed_operation IS NULL OR char_length(consumed_operation) BETWEEN 1 AND 300),
  invalidated_reason   ti.reason_text,
  created_at           timestamptz NOT NULL DEFAULT now(),
  created_by           ti.subject_text NOT NULL,
  updated_at           timestamptz NOT NULL DEFAULT now(),
  version              integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  CHECK (approver_subject IS NULL OR approver_subject <> requester_subject),   -- SOD-1
  CHECK (signed_at IS NULL OR (approver_subject IS NOT NULL AND meaning IS NOT NULL AND sign_reason IS NOT NULL
                               AND reauth_reference IS NOT NULL AND reauth_verified_at IS NOT NULL)),
  CHECK (status NOT IN ('Approved','Consumed') OR signed_at IS NOT NULL),
  CHECK (status <> 'Rejected' OR (approver_subject IS NOT NULL AND sign_reason IS NOT NULL AND meaning = 'Rejection' AND signed_at IS NULL)),
  CHECK (status NOT IN ('Approved','Consumed') OR expires_at = signed_at + interval '30 minutes'),
  CHECK ((status = 'Consumed') = (consumed_at IS NOT NULL AND consumed_operation IS NOT NULL)),
  CHECK ((status = 'Invalidated') = (invalidated_reason IS NOT NULL))
);
CREATE INDEX approval_subject_ix ON ti.approval (tenant_id, subject_type, subject_id, status);
CREATE UNIQUE INDEX approval_consumed_operation_uq ON ti.approval (tenant_id, consumed_operation) WHERE consumed_operation IS NOT NULL;

-- The binding and the signature never change once written; status moves only forward (DEC-056).
CREATE FUNCTION ti.approval_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF (NEW.tenant_id, NEW.subject_type, NEW.subject_id, NEW.subject_version, NEW.content_digest, NEW.action, NEW.request_reason,
      NEW.requester_subject, NEW.created_at, NEW.created_by)
     IS DISTINCT FROM
     (OLD.tenant_id, OLD.subject_type, OLD.subject_id, OLD.subject_version, OLD.content_digest, OLD.action, OLD.request_reason,
      OLD.requester_subject, OLD.created_at, OLD.created_by) THEN
    RAISE EXCEPTION 'an approval binding cannot change (IAM-003)' USING ERRCODE = 'check_violation';
  END IF;
  IF OLD.approver_subject IS NOT NULL AND (NEW.approver_subject, NEW.meaning, NEW.sign_reason, NEW.reauth_reference, NEW.reauth_verified_at,
                                    NEW.signed_at, NEW.expires_at)
     IS DISTINCT FROM (OLD.approver_subject, OLD.meaning, OLD.sign_reason, OLD.reauth_reference, OLD.reauth_verified_at,
                       OLD.signed_at, OLD.expires_at) THEN
    RAISE EXCEPTION 'a signature or rejection cannot change' USING ERRCODE = 'check_violation';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'Pending' AND NEW.status IN ('Approved','Rejected','Invalidated','Expired'))
    OR (OLD.status = 'Approved' AND NEW.status IN ('Consumed','Invalidated','Expired'))) THEN
    RAISE EXCEPTION 'approval cannot move from % to %', OLD.status, NEW.status USING ERRCODE = 'check_violation';
  END IF;
  IF OLD.status NOT IN ('Pending','Approved') AND ROW(NEW.*) IS DISTINCT FROM ROW(OLD.*) THEN
    RAISE EXCEPTION 'a % approval is final', OLD.status USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER approval_guard BEFORE UPDATE ON ti.approval FOR EACH ROW EXECUTE FUNCTION ti.approval_guard();

-- ---------------------------------------------------------------------------------------------------------------
-- Attachments: tenant-prefixed object keys (TEN-005); PDF, JPEG or PNG up to 10 MiB (DEC-022).
CREATE TABLE ti.attachment (
  id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id      uuid NOT NULL REFERENCES ti.tenant(id),
  entity_type    ti.code_text,
  entity_id      uuid,
  file_name      text NOT NULL CHECK (char_length(file_name) BETWEEN 1 AND 255),
  object_key     text NOT NULL CHECK (char_length(object_key) BETWEEN 1 AND 512),
  media_type     text NOT NULL CHECK (media_type IN ('application/pdf','image/jpeg','image/png')),
  size_bytes     bigint NOT NULL CHECK (size_bytes BETWEEN 1 AND 10485760),
  digest_sha256  ti.sha256 NOT NULL,
  scan_status    ti.attachment_scan_status NOT NULL DEFAULT 'Pending',
  created_at     timestamptz NOT NULL DEFAULT now(),
  created_by     ti.subject_text NOT NULL,
  updated_at     timestamptz NOT NULL DEFAULT now(),
  version        integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, object_key),
  CHECK (object_key LIKE tenant_id::text || '/%'),
  CHECK ((entity_type IS NULL) = (entity_id IS NULL))
);
CREATE INDEX attachment_entity_ix ON ti.attachment (tenant_id, entity_type, entity_id);

-- ---------------------------------------------------------------------------------------------------------------
-- Transfers between warehouses through the source warehouse's Transit bin (EXC-001, STK-003, DEC-010).
CREATE TABLE ti.transfer (
  id                         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id                  uuid NOT NULL REFERENCES ti.tenant(id),
  source_warehouse_id        uuid NOT NULL,
  destination_warehouse_id   uuid NOT NULL,
  reference                  ti.code_text NOT NULL,
  status                     ti.transfer_status NOT NULL DEFAULT 'Draft',
  approved_by                ti.subject_text,
  approved_at                timestamptz,
  dispatched_at              timestamptz,
  received_at                timestamptz,
  created_at                 timestamptz NOT NULL DEFAULT now(),
  created_by                 ti.subject_text NOT NULL,
  updated_at                 timestamptz NOT NULL DEFAULT now(),
  version                    integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, reference),
  FOREIGN KEY (tenant_id, source_warehouse_id) REFERENCES ti.warehouse (tenant_id, id),
  FOREIGN KEY (tenant_id, destination_warehouse_id) REFERENCES ti.warehouse (tenant_id, id),
  CHECK (source_warehouse_id <> destination_warehouse_id),
  CHECK ((approved_by IS NULL) = (approved_at IS NULL)),
  CHECK (status IN ('Draft','Cancelled') OR approved_at IS NOT NULL),
  CHECK (status NOT IN ('InTransit','PartReceived','Received') OR dispatched_at IS NOT NULL),
  CHECK ((status = 'Received') = (received_at IS NOT NULL))
);
CREATE INDEX transfer_status_ix ON ti.transfer (tenant_id, status, created_at);

CREATE TABLE ti.transfer_line (
  id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id          uuid NOT NULL REFERENCES ti.tenant(id),
  transfer_id        uuid NOT NULL,
  line_number        integer NOT NULL CHECK (line_number > 0),
  product_id         uuid NOT NULL,
  lot_id             uuid,
  requested_qty      ti.pos_qty NOT NULL,
  dispatched_qty     ti.qty NOT NULL DEFAULT 0,
  received_qty       ti.qty NOT NULL DEFAULT 0,
  disposed_qty       ti.qty NOT NULL DEFAULT 0,  -- approved TransitDisposition (STK-003)
  created_at         timestamptz NOT NULL DEFAULT now(),
  created_by         ti.subject_text NOT NULL,
  updated_at         timestamptz NOT NULL DEFAULT now(),
  version            integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, transfer_id, line_number),
  FOREIGN KEY (tenant_id, transfer_id) REFERENCES ti.transfer (tenant_id, id),
  FOREIGN KEY (tenant_id, product_id) REFERENCES ti.product (tenant_id, id),
  FOREIGN KEY (tenant_id, product_id, lot_id) REFERENCES ti.lot (tenant_id, product_id, id),
  CHECK (dispatched_qty <= requested_qty),
  CHECK (received_qty + disposed_qty <= dispatched_qty)
);

-- ---------------------------------------------------------------------------------------------------------------
-- Stock counts: bin locks, blind snapshot, revisions (EXC-003, DEC-008).
CREATE TABLE ti.stock_count (
  id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id          uuid NOT NULL REFERENCES ti.tenant(id),
  warehouse_id       uuid NOT NULL,
  reason             ti.reason_text NOT NULL,
  status             ti.count_status NOT NULL DEFAULT 'Draft',
  revision           integer NOT NULL DEFAULT 1 CHECK (revision > 0),
  snapshot_sequence  bigint,
  locked_at          timestamptz,
  submitted_at       timestamptz,
  approval_id        uuid,
  created_at         timestamptz NOT NULL DEFAULT now(),
  created_by         ti.subject_text NOT NULL,
  updated_at         timestamptz NOT NULL DEFAULT now(),
  version            integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, warehouse_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES ti.warehouse (tenant_id, id),
  FOREIGN KEY (tenant_id, approval_id) REFERENCES ti.approval (tenant_id, id),
  CHECK (status IN ('Draft','Cancelled') OR locked_at IS NOT NULL),
  CHECK (status NOT IN ('Submitted','Approved','Posted','Closed') OR submitted_at IS NOT NULL),
  CHECK (status NOT IN ('Approved','Posted') OR approval_id IS NOT NULL)
);

CREATE TABLE ti.stock_count_bin (
  id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id      uuid NOT NULL REFERENCES ti.tenant(id),
  count_id       uuid NOT NULL,
  warehouse_id   uuid NOT NULL,
  bin_id         uuid NOT NULL,
  lock_active    boolean NOT NULL DEFAULT false, -- true from start until post or cancel (EXC-003)
  created_at     timestamptz NOT NULL DEFAULT now(),
  created_by     ti.subject_text NOT NULL,
  updated_at     timestamptz NOT NULL DEFAULT now(),
  version        integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, count_id, bin_id),
  FOREIGN KEY (tenant_id, warehouse_id, count_id) REFERENCES ti.stock_count (tenant_id, warehouse_id, id),
  FOREIGN KEY (tenant_id, warehouse_id, bin_id) REFERENCES ti.bin (tenant_id, warehouse_id, id)
);
CREATE UNIQUE INDEX stock_count_bin_one_lock_uq ON ti.stock_count_bin (tenant_id, bin_id) WHERE lock_active;

CREATE TABLE ti.stock_count_line (
  id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id      uuid NOT NULL REFERENCES ti.tenant(id),
  count_id       uuid NOT NULL,
  revision       integer NOT NULL CHECK (revision > 0),
  bin_id         uuid NOT NULL,
  product_id     uuid NOT NULL,
  lot_id         uuid,
  serial_id      uuid,
  expected_qty   ti.qty NOT NULL,               -- snapshot; never returned to blind counters
  observed_qty   ti.qty,
  variance_qty   numeric GENERATED ALWAYS AS (observed_qty - expected_qty) STORED,
  counted_by     ti.subject_text,
  counted_at     timestamptz,
  created_at     timestamptz NOT NULL DEFAULT now(),
  created_by     ti.subject_text NOT NULL,
  updated_at     timestamptz NOT NULL DEFAULT now(),
  version        integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE NULLS NOT DISTINCT (tenant_id, count_id, revision, bin_id, product_id, lot_id, serial_id),
  FOREIGN KEY (tenant_id, count_id, bin_id) REFERENCES ti.stock_count_bin (tenant_id, count_id, bin_id),
  FOREIGN KEY (tenant_id, product_id) REFERENCES ti.product (tenant_id, id),
  FOREIGN KEY (tenant_id, product_id, lot_id) REFERENCES ti.lot (tenant_id, product_id, id),
  FOREIGN KEY (tenant_id, product_id, serial_id) REFERENCES ti.serial (tenant_id, product_id, id),
  CHECK ((observed_qty IS NULL) = (counted_at IS NULL) AND (counted_at IS NULL) = (counted_by IS NULL))
);

-- ---------------------------------------------------------------------------------------------------------------
-- Adjustments: signed delta, reason, approval, posted once (EXC-004).
CREATE TABLE ti.adjustment (
  id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id             uuid NOT NULL REFERENCES ti.tenant(id),
  warehouse_id          uuid NOT NULL,
  bucket_id             uuid,                     -- NULL only for an increase into a bucket that does not exist yet
  bin_id                uuid NOT NULL,
  product_id            uuid NOT NULL,
  lot_id                uuid,
  serial_id             uuid,
  quality_status        ti.quality_status NOT NULL, -- medical increases default to Hold (DEC-022)
  delta_qty             ti.signed_qty NOT NULL,
  reason_code           ti.code_text NOT NULL,
  narrative             ti.reason_text NOT NULL,
  count_id              uuid,                     -- set when raised from a count variance
  status                ti.adjustment_status NOT NULL DEFAULT 'Requested',
  approval_id           uuid,
  posted_movement_id    uuid,
  created_at            timestamptz NOT NULL DEFAULT now(),
  created_by            ti.subject_text NOT NULL,
  updated_at            timestamptz NOT NULL DEFAULT now(),
  version               integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES ti.warehouse (tenant_id, id),
  FOREIGN KEY (tenant_id, bucket_id, product_id) REFERENCES ti.stock_bucket (tenant_id, id, product_id),
  FOREIGN KEY (tenant_id, warehouse_id, bin_id) REFERENCES ti.bin (tenant_id, warehouse_id, id),
  FOREIGN KEY (tenant_id, product_id) REFERENCES ti.product (tenant_id, id),
  FOREIGN KEY (tenant_id, product_id, lot_id) REFERENCES ti.lot (tenant_id, product_id, id),
  FOREIGN KEY (tenant_id, product_id, serial_id) REFERENCES ti.serial (tenant_id, product_id, id),
  FOREIGN KEY (tenant_id, count_id) REFERENCES ti.stock_count (tenant_id, id),
  FOREIGN KEY (tenant_id, approval_id) REFERENCES ti.approval (tenant_id, id),
  FOREIGN KEY (tenant_id, posted_movement_id) REFERENCES ti.stock_movement (tenant_id, id),
  CHECK (delta_qty > 0 OR bucket_id IS NOT NULL),   -- a reduction always targets an existing bucket
  CHECK (status <> 'Posted' OR bucket_id IS NOT NULL),
  CHECK ((status = 'Posted') = (posted_movement_id IS NOT NULL)),
  CHECK (status NOT IN ('Approved','Posted') OR approval_id IS NOT NULL)
);
CREATE INDEX adjustment_status_ix ON ti.adjustment (tenant_id, warehouse_id, status);

-- A posted adjustment is history: it never changes again (EXC-004, DEC-056).
CREATE FUNCTION ti.adjustment_posted_frozen() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.status = 'Posted' THEN
    RAISE EXCEPTION 'a posted adjustment cannot change' USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER adjustment_posted_frozen BEFORE UPDATE ON ti.adjustment FOR EACH ROW EXECUTE FUNCTION ti.adjustment_posted_frozen();

-- ---------------------------------------------------------------------------------------------------------------
-- Customer returns: into Quarantine, capped by what was shipped, inspected in parts (EXC-005).
CREATE TABLE ti.customer_return (
  id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id              uuid NOT NULL REFERENCES ti.tenant(id),
  warehouse_id           uuid NOT NULL,
  shipment_line_id       uuid NOT NULL,
  product_id             uuid NOT NULL,
  serial_id              uuid,                    -- a shipment line carries at most one serial (DEC-055)
  returned_qty           ti.pos_qty NOT NULL,
  quarantine_bin_id      uuid NOT NULL,
  bucket_id              uuid NOT NULL,           -- the Quarantine bucket the goods entered
  receipt_movement_id    uuid NOT NULL,
  reason                 ti.reason_text NOT NULL,
  status                 ti.return_status NOT NULL DEFAULT 'Received',
  inspected_qty          ti.qty NOT NULL DEFAULT 0,
  disposition            ti.return_disposition,   -- the latest inspection's disposition
  approval_id            uuid,
  created_at             timestamptz NOT NULL DEFAULT now(),
  created_by             ti.subject_text NOT NULL,
  updated_at             timestamptz NOT NULL DEFAULT now(),
  version                integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES ti.warehouse (tenant_id, id),
  FOREIGN KEY (tenant_id, shipment_line_id, product_id) REFERENCES ti.shipment_line (tenant_id, id, product_id),
  FOREIGN KEY (tenant_id, product_id, serial_id) REFERENCES ti.serial (tenant_id, product_id, id),
  FOREIGN KEY (tenant_id, warehouse_id, quarantine_bin_id) REFERENCES ti.bin (tenant_id, warehouse_id, id),
  FOREIGN KEY (tenant_id, bucket_id, product_id) REFERENCES ti.stock_bucket (tenant_id, id, product_id),
  FOREIGN KEY (tenant_id, receipt_movement_id) REFERENCES ti.stock_movement (tenant_id, id),
  FOREIGN KEY (tenant_id, approval_id) REFERENCES ti.approval (tenant_id, id),
  CHECK (serial_id IS NULL OR returned_qty = 1),
  CHECK (inspected_qty <= returned_qty),
  CHECK ((inspected_qty = 0) = (disposition IS NULL)),
  CHECK ((status = 'Received') = (inspected_qty < returned_qty))
);
CREATE INDEX customer_return_shipment_line_ix ON ti.customer_return (tenant_id, shipment_line_id);

CREATE TABLE ti.customer_return_inspection (
  id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id       uuid NOT NULL REFERENCES ti.tenant(id),
  return_id       uuid NOT NULL,
  disposition     ti.return_disposition NOT NULL,
  qty             ti.pos_qty NOT NULL,
  reason          ti.reason_text NOT NULL,
  movement_id     uuid NOT NULL,
  approval_id     uuid,
  created_at      timestamptz NOT NULL DEFAULT now(),
  created_by      ti.subject_text NOT NULL,
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, return_id) REFERENCES ti.customer_return (tenant_id, id),
  FOREIGN KEY (tenant_id, movement_id) REFERENCES ti.stock_movement (tenant_id, id),
  FOREIGN KEY (tenant_id, approval_id) REFERENCES ti.approval (tenant_id, id)
);

-- ---------------------------------------------------------------------------------------------------------------
-- Export jobs (AUD-004, TEN-005, NFR-EXPORTS).
CREATE TABLE ti.export_job (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id        uuid NOT NULL REFERENCES ti.tenant(id),
  type             ti.export_type NOT NULL,
  status           ti.export_status NOT NULL DEFAULT 'Queued',
  filter           jsonb NOT NULL,
  filter_digest    ti.sha256 NOT NULL,
  stream_from_seq  bigint,
  stream_to_seq    bigint,
  object_key       text CHECK (object_key IS NULL OR (char_length(object_key) <= 512 AND object_key LIKE tenant_id::text || '/%')),
  manifest         jsonb,                        -- integrity manifest and signature reference (AUD-004)
  failure_reason   text CHECK (failure_reason IS NULL OR char_length(failure_reason) BETWEEN 1 AND 2000),
  completed_at     timestamptz,
  retain_until     timestamptz,
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       ti.subject_text NOT NULL,
  updated_at       timestamptz NOT NULL DEFAULT now(),
  version          integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  CHECK ((stream_from_seq IS NULL) = (stream_to_seq IS NULL) AND (stream_from_seq IS NULL OR stream_from_seq <= stream_to_seq)),
  CHECK ((status = 'Completed') = (object_key IS NOT NULL AND manifest IS NOT NULL AND completed_at IS NOT NULL)),
  CHECK ((status = 'Failed') = (failure_reason IS NOT NULL))
);
CREATE INDEX export_job_status_ix ON ti.export_job (tenant_id, status, created_at);

-- ---------------------------------------------------------------------------------------------------------------
-- Append-only enforcement, RLS and grants (migration-design.md §2). No DELETE anywhere: history is kept.
DO $$
DECLARE t text;
BEGIN
  EXECUTE 'CREATE TRIGGER customer_return_inspection_append_only BEFORE UPDATE OR DELETE ON ti.customer_return_inspection FOR EACH ROW EXECUTE FUNCTION ti.reject_mutation()';
  EXECUTE 'CREATE TRIGGER customer_return_inspection_no_truncate BEFORE TRUNCATE ON ti.customer_return_inspection FOR EACH STATEMENT EXECUTE FUNCTION ti.reject_mutation()';

  FOREACH t IN ARRAY ARRAY['approval','attachment','transfer','transfer_line','stock_count','stock_count_bin','stock_count_line','adjustment',
                           'customer_return','customer_return_inspection','export_job'] LOOP
    EXECUTE format('ALTER TABLE ti.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE ti.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format($p$CREATE POLICY tenant_isolation ON ti.%I TO ti_app
                     USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                     WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)$p$, t);
    IF t = 'customer_return_inspection' THEN
      EXECUTE format('GRANT SELECT, INSERT ON ti.%I TO ti_app', t);
    ELSE
      EXECUTE format('GRANT SELECT, INSERT, UPDATE ON ti.%I TO ti_app', t);
    END IF;
  END LOOP;
END $$;

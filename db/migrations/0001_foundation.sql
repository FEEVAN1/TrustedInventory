-- 0001 foundation (M1): organization, catalog, locations, alerts, outbox, idempotency, audit.
-- Applied by the migrator as ti_migrator with SET ROLE ti_owner. Source: db/design/schema-draft.sql.
-- Stock, inbound, outbound, exception and medical tables arrive with M2-M5 (AGT-001).

-- ---------------------------------------------------------------------------
-- Domains (DEC-014). numeric without typmod + scale CHECK so excess precision is
-- rejected rather than silently rounded by numeric(20,6).
-- ---------------------------------------------------------------------------
CREATE DOMAIN ti.qty AS numeric CHECK (VALUE >= 0 AND scale(VALUE) <= 6 AND VALUE < 100000000000000);
CREATE DOMAIN ti.pos_qty AS numeric CHECK (VALUE > 0 AND scale(VALUE) <= 6 AND VALUE < 100000000000000);
CREATE DOMAIN ti.signed_qty AS numeric CHECK (VALUE <> 0 AND scale(VALUE) <= 6 AND abs(VALUE) < 100000000000000);
CREATE DOMAIN ti.name_text AS text CHECK (VALUE = btrim(VALUE) AND char_length(VALUE) BETWEEN 1 AND 200);
CREATE DOMAIN ti.code_text AS text CHECK (VALUE = btrim(VALUE) AND char_length(VALUE) BETWEEN 1 AND 64);
CREATE DOMAIN ti.reason_text AS text CHECK (char_length(VALUE) BETWEEN 1 AND 2000 AND VALUE ~ '\S');
CREATE DOMAIN ti.identifier_text AS text CHECK (VALUE = btrim(VALUE) AND char_length(VALUE) BETWEEN 1 AND 100);  -- lot/serial numbers as scanned
CREATE DOMAIN ti.subject_text AS text CHECK (char_length(VALUE) BETWEEN 1 AND 255);
CREATE DOMAIN ti.sha256 AS bytea CHECK (octet_length(VALUE) = 32);
CREATE DOMAIN ti.celsius AS numeric(6,2) CHECK (VALUE BETWEEN -273.15 AND 1000);

-- ---------------------------------------------------------------------------
-- Enums (docs/enums.md)
-- ---------------------------------------------------------------------------
CREATE TYPE ti.tenant_status AS ENUM ('Active','Suspended');
CREATE TYPE ti.handling_profile AS ENUM ('General','Medical');
CREATE TYPE ti.tracking_mode AS ENUM ('Quantity','Lot','Serial','LotAndSerial');
CREATE TYPE ti.rotation_rule AS ENUM ('FIFO','LIFO','FEFO');
CREATE TYPE ti.partner_type AS ENUM ('Supplier','Customer','Both');
CREATE TYPE ti.bin_kind AS ENUM ('Storage','Staging','Quarantine','Transit');
CREATE TYPE ti.quality_status AS ENUM ('Quarantine','Available','Hold','Recalled','Damaged');
CREATE TYPE ti.purchase_order_status AS ENUM ('Draft','Approved','PartReceived','Received','ShortClosed','Cancelled');
CREATE TYPE ti.receipt_status AS ENUM ('Draft','Posted','Closed','Cancelled');
CREATE TYPE ti.outbound_order_status AS ENUM ('Draft','Confirmed','PartAllocated','Allocated','PartPicked','Picked','PartDispatched','Dispatched','Closed','Cancelled');
CREATE TYPE ti.reservation_state AS ENUM ('Active','Picked','Consumed','Released','Expired');
CREATE TYPE ti.shipment_status AS ENUM ('Dispatched');
CREATE TYPE ti.task_type AS ENUM ('Putaway','Rework','Exception');
CREATE TYPE ti.task_status AS ENUM ('Open','InProgress','Completed','Cancelled');
CREATE TYPE ti.transfer_status AS ENUM ('Draft','Approved','InTransit','PartReceived','Received','Cancelled');
CREATE TYPE ti.count_status AS ENUM ('Draft','Counting','Submitted','Approved','Posted','Closed','Cancelled');
CREATE TYPE ti.adjustment_status AS ENUM ('Requested','Approved','Posted','Rejected','Cancelled');
CREATE TYPE ti.return_status AS ENUM ('Received','Inspected','Closed');
CREATE TYPE ti.return_disposition AS ENUM ('Release','Damaged','SupplierReturn','Disposal');
CREATE TYPE ti.recall_status AS ENUM ('Open','Closed');
CREATE TYPE ti.approval_status AS ENUM ('Pending','Approved','Rejected','Expired','Invalidated','Consumed');
CREATE TYPE ti.approval_subject_type AS ENUM ('Adjustment','CountVariance','QualityRelease','HoldRelease','ReturnRelease','RecallClosure','TransitDisposition','ExpiryCorrection','ExcursionDisposition');
CREATE TYPE ti.signature_meaning AS ENUM ('Approval','Rejection','QualityRelease','Disposition');
CREATE TYPE ti.excursion_status AS ENUM ('Open','Acknowledged','Resolved');
CREATE TYPE ti.excursion_disposition AS ENUM ('Release','KeepOnHold','Damaged');
CREATE TYPE ti.alert_type AS ENUM ('Excursion','StaleDevice','ExpiryApproaching','ReconciliationMismatch','AuditChainFailure','OutboxDeadLetter','PutawayException');
CREATE TYPE ti.alert_severity AS ENUM ('Info','Warning','High');
CREATE TYPE ti.alert_status AS ENUM ('Open','Acknowledged','Closed');
CREATE TYPE ti.export_type AS ENUM ('Audit','Movements','Traceability','StockSnapshot');
CREATE TYPE ti.export_status AS ENUM ('Queued','Running','Completed','Failed','Expired');
CREATE TYPE ti.attachment_scan_status AS ENUM ('Pending','Clean','Rejected');
CREATE TYPE ti.role_name AS ENUM ('PlatformAdministrator','TenantAdministrator','WarehouseManager','Receiver','Picker','Dispatcher','QaOfficer','InventoryApprover','Auditor','TelemetryIntegration');
CREATE TYPE ti.actor_type AS ENUM ('User','Service','Device','System');
CREATE TYPE ti.approval_action AS ENUM ('Post','Release','Close','Correct','Dispose');
CREATE TYPE ti.inspection_result AS ENUM ('Passed','Failed');
CREATE TYPE ti.document_type AS ENUM ('Receipt','PutawayTask','BinTransfer','OutboundOrder','Shipment','Transfer','Count','Adjustment','Return','Recall','QualityDecision','Excursion','ExpiryCorrection');
CREATE TYPE ti.recall_outreach_status AS ENUM ('Pending','Contacted','Acknowledged','Completed');
CREATE TYPE ti.transit_disposition_type AS ENUM ('Lost','Damaged');
CREATE TYPE ti.movement_type AS ENUM ('Receipt','Putaway','BinTransfer','Pick','Unpick','Dispatch','TransferDispatch','TransferReceipt','TransitDisposition','QualityChange','AdjustmentIncrease','AdjustmentDecrease','ReturnReceipt','Disposal','SupplierReturn','Reversal');

-- Standard audit columns on mutable tenant tables (section 07):
--   created_at timestamptz, created_by subject, updated_at timestamptz, version integer (optimistic concurrency).
-- Append-only tables carry created_at/created_by only; they are never updated.

-- ===========================================================================
-- Organization
-- ===========================================================================
CREATE TABLE ti.tenant (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  name        ti.name_text NOT NULL,
  status      ti.tenant_status NOT NULL DEFAULT 'Active',
  created_at  timestamptz NOT NULL DEFAULT now(),
  created_by  ti.subject_text NOT NULL,
  updated_at  timestamptz NOT NULL DEFAULT now(),
  version     integer NOT NULL DEFAULT 1 CHECK (version > 0)
);

CREATE TABLE ti.membership (
  id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id     uuid NOT NULL REFERENCES ti.tenant(id),
  user_subject  ti.subject_text NOT NULL,
  display_name  ti.name_text,
  active        boolean NOT NULL DEFAULT true,
  revoked_at    timestamptz,
  revoked_by    ti.subject_text,
  created_at    timestamptz NOT NULL DEFAULT now(),
  created_by    ti.subject_text NOT NULL,
  updated_at    timestamptz NOT NULL DEFAULT now(),
  version       integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, user_subject),
  UNIQUE (tenant_id, id, user_subject),
  CHECK (active = (revoked_at IS NULL))
);

CREATE TABLE ti.warehouse (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id   uuid NOT NULL REFERENCES ti.tenant(id),
  code        ti.code_text NOT NULL,
  name        ti.name_text NOT NULL,
  timezone    text NOT NULL CHECK (char_length(timezone) BETWEEN 1 AND 64),  -- IANA; validated by API against tz database
  active      boolean NOT NULL DEFAULT true,
  created_at  timestamptz NOT NULL DEFAULT now(),
  created_by  ti.subject_text NOT NULL,
  updated_at  timestamptz NOT NULL DEFAULT now(),
  version     integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, code)
);

CREATE TABLE ti.role_grant (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id        uuid NOT NULL REFERENCES ti.tenant(id),
  membership_id    uuid NOT NULL,
  grantee_subject  ti.subject_text NOT NULL,
  role             ti.role_name NOT NULL CHECK (role <> 'PlatformAdministrator'),
  warehouse_id     uuid,                          -- NULL = all warehouses of the tenant
  reason           ti.reason_text NOT NULL,
  granted_by       ti.subject_text NOT NULL,
  granted_at       timestamptz NOT NULL DEFAULT now(),
  revoked_at       timestamptz,
  revoked_by       ti.subject_text,
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       ti.subject_text NOT NULL,
  updated_at       timestamptz NOT NULL DEFAULT now(),
  version          integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, membership_id, grantee_subject) REFERENCES ti.membership (tenant_id, id, user_subject),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES ti.warehouse (tenant_id, id),
  CHECK (role <> 'QaOfficer' OR granted_by <> grantee_subject),  -- SOD-3
  CHECK ((revoked_at IS NULL) = (revoked_by IS NULL))
);
CREATE UNIQUE INDEX role_grant_active_uq ON ti.role_grant (tenant_id, membership_id, role, warehouse_id)
  NULLS NOT DISTINCT WHERE revoked_at IS NULL;

CREATE TABLE ti.configuration_version (
  id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id       uuid NOT NULL REFERENCES ti.tenant(id),
  scope_type      text NOT NULL CHECK (scope_type IN ('Tenant','Warehouse','Product')),
  scope_id        uuid,                           -- NULL for Tenant scope
  config_version  integer NOT NULL CHECK (config_version > 0),
  settings        jsonb NOT NULL,
  reason          ti.reason_text NOT NULL,
  effective_from  timestamptz NOT NULL DEFAULT now(),
  created_at      timestamptz NOT NULL DEFAULT now(),
  created_by      ti.subject_text NOT NULL,
  UNIQUE (tenant_id, id),
  UNIQUE NULLS NOT DISTINCT (tenant_id, scope_type, scope_id, config_version),
  CHECK ((scope_type = 'Tenant') = (scope_id IS NULL))
);

-- ===========================================================================
-- Catalog and locations
-- ===========================================================================
CREATE TABLE ti.unit (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id   uuid NOT NULL REFERENCES ti.tenant(id),
  code        ti.code_text NOT NULL,
  name        ti.name_text NOT NULL,
  active      boolean NOT NULL DEFAULT true,
  created_at  timestamptz NOT NULL DEFAULT now(),
  created_by  ti.subject_text NOT NULL,
  updated_at  timestamptz NOT NULL DEFAULT now(),
  version     integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, code)
);

CREATE TABLE ti.excursion_policy (
  id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id              uuid NOT NULL REFERENCES ti.tenant(id),
  name                   ti.name_text NOT NULL,
  hold_on_first_reading  boolean NOT NULL DEFAULT true,   -- TEL-003 R1 default; only true is supported in R1
  notify_recipients      jsonb NOT NULL DEFAULT '[]'::jsonb,
  notes                  text CHECK (notes IS NULL OR char_length(notes) <= 2000),
  active                 boolean NOT NULL DEFAULT true,
  created_at             timestamptz NOT NULL DEFAULT now(),
  created_by             ti.subject_text NOT NULL,
  updated_at             timestamptz NOT NULL DEFAULT now(),
  version                integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, name),
  CHECK (hold_on_first_reading)
);

CREATE TABLE ti.storage_profile (
  id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id            uuid NOT NULL REFERENCES ti.tenant(id),
  name                 ti.name_text NOT NULL,
  min_c                ti.celsius NOT NULL,
  max_c                ti.celsius NOT NULL,
  monitoring_required  boolean NOT NULL DEFAULT true,
  stale_after_seconds  integer NOT NULL DEFAULT 300 CHECK (stale_after_seconds > 0),
  excursion_policy_id  uuid NOT NULL,             -- CFG-002: no profile without an excursion policy
  active               boolean NOT NULL DEFAULT true,
  created_at           timestamptz NOT NULL DEFAULT now(),
  created_by           ti.subject_text NOT NULL,
  updated_at           timestamptz NOT NULL DEFAULT now(),
  version              integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, name),
  FOREIGN KEY (tenant_id, excursion_policy_id) REFERENCES ti.excursion_policy (tenant_id, id),
  CHECK (min_c < max_c)
);

CREATE TABLE ti.zone (
  id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id     uuid NOT NULL REFERENCES ti.tenant(id),
  warehouse_id  uuid NOT NULL,
  code          ti.code_text NOT NULL,
  name          ti.name_text NOT NULL,
  temp_min_c    ti.celsius,                       -- DEC-012: NULL pair = uncontrolled ambient
  temp_max_c    ti.celsius,
  is_system     boolean NOT NULL DEFAULT false,   -- DEC-010 system zone holding the Transit bin
  active        boolean NOT NULL DEFAULT true,
  created_at    timestamptz NOT NULL DEFAULT now(),
  created_by    ti.subject_text NOT NULL,
  updated_at    timestamptz NOT NULL DEFAULT now(),
  version       integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, warehouse_id, id),
  UNIQUE (tenant_id, warehouse_id, code),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES ti.warehouse (tenant_id, id),
  CHECK ((temp_min_c IS NULL) = (temp_max_c IS NULL)),
  CHECK (temp_min_c IS NULL OR temp_min_c < temp_max_c)
);

CREATE TABLE ti.zone_allowed_hazard_class (
  id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id     uuid NOT NULL REFERENCES ti.tenant(id),
  zone_id       uuid NOT NULL,
  hazard_class  ti.code_text NOT NULL,
  created_at    timestamptz NOT NULL DEFAULT now(),
  created_by    ti.subject_text NOT NULL,
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, zone_id, hazard_class),
  FOREIGN KEY (tenant_id, zone_id) REFERENCES ti.zone (tenant_id, id)
);

CREATE TABLE ti.bin (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id         uuid NOT NULL REFERENCES ti.tenant(id),
  warehouse_id      uuid NOT NULL,
  zone_id           uuid NOT NULL,
  code              ti.code_text NOT NULL,
  kind              ti.bin_kind NOT NULL DEFAULT 'Storage',
  pick_priority     integer NOT NULL DEFAULT 1000 CHECK (pick_priority >= 0),
  capacity          ti.pos_qty,
  capacity_uom_id   uuid,
  active            boolean NOT NULL DEFAULT true,
  created_at        timestamptz NOT NULL DEFAULT now(),
  created_by        ti.subject_text NOT NULL,
  updated_at        timestamptz NOT NULL DEFAULT now(),
  version           integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, warehouse_id, id),
  UNIQUE (tenant_id, warehouse_id, code),         -- scanned bin codes resolve within a warehouse
  FOREIGN KEY (tenant_id, warehouse_id, zone_id) REFERENCES ti.zone (tenant_id, warehouse_id, id),
  FOREIGN KEY (tenant_id, capacity_uom_id) REFERENCES ti.unit (tenant_id, id),
  CHECK ((capacity IS NULL) = (capacity_uom_id IS NULL))
);
CREATE UNIQUE INDEX bin_one_transit_per_warehouse ON ti.bin (tenant_id, warehouse_id) WHERE kind = 'Transit';

CREATE TABLE ti.product (
  id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id           uuid NOT NULL REFERENCES ti.tenant(id),
  sku                 ti.code_text NOT NULL,
  name                ti.name_text NOT NULL,
  handling_profile    ti.handling_profile NOT NULL,
  tracking_mode       ti.tracking_mode NOT NULL,
  base_uom_id         uuid NOT NULL,
  base_precision      smallint NOT NULL CHECK (base_precision BETWEEN 0 AND 6),
  rotation_rule       ti.rotation_rule NOT NULL,
  hazard_class        ti.code_text,
  storage_profile_id  uuid,                       -- NULL = no temperature requirement
  active              boolean NOT NULL DEFAULT true,
  created_at          timestamptz NOT NULL DEFAULT now(),
  created_by          ti.subject_text NOT NULL,
  updated_at          timestamptz NOT NULL DEFAULT now(),
  version             integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, id, handling_profile),
  UNIQUE (tenant_id, sku),
  FOREIGN KEY (tenant_id, base_uom_id) REFERENCES ti.unit (tenant_id, id),
  FOREIGN KEY (tenant_id, storage_profile_id) REFERENCES ti.storage_profile (tenant_id, id),
  CHECK (handling_profile = 'General' OR tracking_mode IN ('Lot','LotAndSerial')),  -- medical requires lots
  CHECK (handling_profile = 'General' OR rotation_rule = 'FEFO'),                    -- medical is FEFO only
  CHECK (tracking_mode NOT IN ('Serial','LotAndSerial') OR base_precision = 0)      -- whole serial units
);

CREATE TABLE ti.product_conversion (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id         uuid NOT NULL REFERENCES ti.tenant(id),
  product_id        uuid NOT NULL,
  alternate_uom_id  uuid NOT NULL,
  factor_to_base    numeric NOT NULL CHECK (factor_to_base > 0 AND scale(factor_to_base) <= 6),
  active            boolean NOT NULL DEFAULT true,
  created_at        timestamptz NOT NULL DEFAULT now(),
  created_by        ti.subject_text NOT NULL,
  updated_at        timestamptz NOT NULL DEFAULT now(),
  version           integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, product_id, alternate_uom_id),
  FOREIGN KEY (tenant_id, product_id) REFERENCES ti.product (tenant_id, id),
  FOREIGN KEY (tenant_id, alternate_uom_id) REFERENCES ti.unit (tenant_id, id)
);

CREATE TABLE ti.product_barcode (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id   uuid NOT NULL REFERENCES ti.tenant(id),
  product_id  uuid NOT NULL,
  gtin        text NOT NULL CHECK (gtin ~ '^[0-9]{14}$'),  -- normalised to GTIN-14, leading zeros kept (INB-002)
  active      boolean NOT NULL DEFAULT true,               -- retired mappings stay for history (DEC-019)
  created_at  timestamptz NOT NULL DEFAULT now(),
  created_by  ti.subject_text NOT NULL,
  updated_at  timestamptz NOT NULL DEFAULT now(),
  version     integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, product_id) REFERENCES ti.product (tenant_id, id)
);
-- One active product per GTIN, so a scan never maps ambiguously (INB-002).
CREATE UNIQUE INDEX product_barcode_active_gtin_uq ON ti.product_barcode (tenant_id, gtin) WHERE active;

CREATE TABLE ti.partner (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id   uuid NOT NULL REFERENCES ti.tenant(id),
  code        ti.code_text NOT NULL,
  name        ti.name_text NOT NULL,
  type        ti.partner_type NOT NULL,
  active      boolean NOT NULL DEFAULT true,
  created_at  timestamptz NOT NULL DEFAULT now(),
  created_by  ti.subject_text NOT NULL,
  updated_at  timestamptz NOT NULL DEFAULT now(),
  version     integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, code)
);

-- ===========================================================================
-- Alerts, exports, outbox, idempotency
-- ===========================================================================
CREATE TABLE ti.alert (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id         uuid NOT NULL REFERENCES ti.tenant(id),
  type              ti.alert_type NOT NULL,
  severity          ti.alert_severity NOT NULL,
  status            ti.alert_status NOT NULL DEFAULT 'Open',
  warehouse_id      uuid,
  zone_id           uuid,
  dedupe_key        text NOT NULL CHECK (char_length(dedupe_key) BETWEEN 1 AND 300),
  details           jsonb NOT NULL,              -- products, lots, first/last abnormal values (TEL-005)
  opened_at         timestamptz NOT NULL DEFAULT now(),
  acknowledged_by   ti.subject_text,
  acknowledged_at   timestamptz,
  closed_at         timestamptz,
  created_at        timestamptz NOT NULL DEFAULT now(),
  created_by        ti.subject_text NOT NULL,
  updated_at        timestamptz NOT NULL DEFAULT now(),
  version           integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES ti.warehouse (tenant_id, id),
  FOREIGN KEY (tenant_id, zone_id) REFERENCES ti.zone (tenant_id, id)
);
CREATE UNIQUE INDEX alert_open_dedupe_uq ON ti.alert (tenant_id, dedupe_key) WHERE status <> 'Closed';
CREATE INDEX alert_status_ix ON ti.alert (tenant_id, status, opened_at);

CREATE TABLE ti.outbox_event (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id         uuid NOT NULL REFERENCES ti.tenant(id),
  event_id          uuid NOT NULL UNIQUE,          -- consumer deduplication key
  event_type        ti.code_text NOT NULL,
  payload           jsonb NOT NULL,
  attempts          integer NOT NULL DEFAULT 0 CHECK (attempts BETWEEN 0 AND 10),
  next_attempt_at   timestamptz NOT NULL DEFAULT now(),
  lease_owner       text,
  lease_expires_at  timestamptz,
  sent_at           timestamptz,
  dead_lettered_at  timestamptz,
  last_error        text,
  created_at        timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, id),
  CHECK (sent_at IS NULL OR dead_lettered_at IS NULL)
);
CREATE INDEX outbox_unsent_ix ON ti.outbox_event (next_attempt_at, tenant_id) WHERE sent_at IS NULL AND dead_lettered_at IS NULL;

CREATE TABLE ti.idempotency_record (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id        uuid NOT NULL REFERENCES ti.tenant(id),
  principal        ti.subject_text NOT NULL,
  route            text NOT NULL CHECK (char_length(route) BETWEEN 1 AND 300),  -- method + route template
  key              uuid NOT NULL,
  payload_digest   ti.sha256 NOT NULL,
  response_status  smallint NOT NULL,
  response_body    jsonb NOT NULL,
  created_at       timestamptz NOT NULL DEFAULT now(),
  expires_at       timestamptz NOT NULL,
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, principal, route, key),
  CHECK (expires_at >= created_at + interval '7 days')  -- STK-007 minimum retention
);
CREATE INDEX idempotency_expiry_ix ON ti.idempotency_record (expires_at);

-- ===========================================================================
-- Audit (AUD-001..004)
-- ===========================================================================
-- One row per tenant stream; locking it serializes sequence + hash assignment (AUD-002).
CREATE TABLE ti.audit_stream (
  tenant_id      uuid PRIMARY KEY REFERENCES ti.tenant(id),
  last_sequence  bigint NOT NULL DEFAULT 0 CHECK (last_sequence >= 0),
  last_hash      ti.sha256 NOT NULL DEFAULT decode(repeat('00', 32), 'hex'),
  updated_at     timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE ti.audit_event (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id        uuid NOT NULL REFERENCES ti.tenant(id),
  sequence         bigint NOT NULL CHECK (sequence > 0),
  occurred_at      timestamptz NOT NULL,
  actor_subject    ti.subject_text NOT NULL,
  actor_type       ti.actor_type NOT NULL,
  request_id       text,
  correlation_id   text,
  event_type       ti.code_text NOT NULL,
  object_type      ti.code_text NOT NULL,
  object_id        uuid,
  object_version   integer,
  reason           ti.reason_text,
  before_state     jsonb,                        -- redacted snapshots
  after_state      jsonb,
  previous_hash    ti.sha256 NOT NULL,
  event_hash       ti.sha256 NOT NULL,
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, sequence),
  UNIQUE (tenant_id, event_hash)
);
CREATE INDEX audit_event_object_ix ON ti.audit_event (tenant_id, object_type, object_id);

CREATE TABLE ti.audit_checkpoint (
  id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id           uuid NOT NULL REFERENCES ti.tenant(id),
  checkpoint_date     date NOT NULL,
  sequence            bigint NOT NULL CHECK (sequence >= 0),
  event_hash          ti.sha256 NOT NULL,
  signature           bytea NOT NULL,
  signing_key_id      text NOT NULL,
  external_reference  text,                       -- location in retention-protected storage (AUD-003)
  created_at          timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, checkpoint_date)
);


-- ===========================================================================
-- Append-only enforcement (defence in depth beside missing grants, AUD-003)
-- ===========================================================================
CREATE FUNCTION ti.reject_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION '% on %.% is not permitted: append-only table', TG_OP, TG_TABLE_SCHEMA, TG_TABLE_NAME
    USING ERRCODE = 'insufficient_privilege';
END $$;

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['audit_event','audit_checkpoint']
  LOOP
    EXECUTE format('CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON ti.%I FOR EACH ROW EXECUTE FUNCTION ti.reject_mutation()', t || '_append_only', t);
    EXECUTE format('CREATE TRIGGER %I BEFORE TRUNCATE ON ti.%I FOR EACH STATEMENT EXECUTE FUNCTION ti.reject_mutation()', t || '_no_truncate', t);
  END LOOP;
END $$;

-- ===========================================================================
-- Row-level security (TEN-002, TEN-003, DEC-013)
-- ===========================================================================
-- Every table with tenant_id: ENABLE + FORCE RLS, one policy for ti_app. No policy exists for
-- ti_owner, so even the owner sees no tenant rows (no universal bypass, TEN-004).
-- Later migrations repeat this for each table they create (docs/migration-design.md section 2).
DO $$
DECLARE t text;
BEGIN
  FOR t IN
    SELECT c.relname FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped
    WHERE n.nspname = 'ti' AND c.relkind = 'r'
  LOOP
    EXECUTE format('ALTER TABLE ti.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE ti.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format($p$CREATE POLICY tenant_isolation ON ti.%I TO ti_app
                     USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                     WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)$p$, t);
  END LOOP;
END $$;

-- tenant: ti_app sees only its own tenant row (read-only); ti_platform manages tenants.
ALTER TABLE ti.tenant ENABLE ROW LEVEL SECURITY;
ALTER TABLE ti.tenant FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_self ON ti.tenant FOR SELECT TO ti_app
  USING (id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY platform_manage ON ti.tenant TO ti_platform USING (true) WITH CHECK (true);

-- Platform provisioning: audit stream and the first administrator membership/grant (TEN-004).
CREATE POLICY platform_provision ON ti.membership TO ti_platform USING (true) WITH CHECK (true);
CREATE POLICY platform_provision ON ti.role_grant TO ti_platform USING (true) WITH CHECK (true);
CREATE POLICY platform_provision ON ti.audit_stream TO ti_platform USING (true) WITH CHECK (true);
CREATE POLICY platform_provision ON ti.audit_event FOR INSERT TO ti_platform WITH CHECK (true);
-- The platform appends to a tenant's chain and must read the stream head it links to.
CREATE POLICY platform_chain_head ON ti.audit_event FOR SELECT TO ti_platform USING (true);

-- Membership lookup before tenant context exists (tenant selector, IAM-002).
CREATE POLICY membership_lookup ON ti.tenant FOR SELECT TO ti_membership_reader USING (true);
CREATE POLICY membership_lookup ON ti.membership FOR SELECT TO ti_membership_reader USING (true);
CREATE POLICY membership_lookup ON ti.role_grant FOR SELECT TO ti_membership_reader USING (true);

CREATE FUNCTION ti.user_memberships(p_subject text)
RETURNS TABLE (tenant_id uuid, tenant_name text, tenant_status ti.tenant_status, membership_id uuid,
               role ti.role_name, warehouse_id uuid)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
  SELECT t.id, t.name, t.status, m.id, g.role, g.warehouse_id
  FROM ti.membership m
  JOIN ti.tenant t ON t.id = m.tenant_id
  LEFT JOIN ti.role_grant g ON g.tenant_id = m.tenant_id AND g.membership_id = m.id AND g.revoked_at IS NULL
  WHERE m.user_subject = p_subject AND m.active
$$;

-- ti_owner is a member of ti_membership_reader (db/bootstrap), so it can hand the function over.
-- PostgreSQL requires the new owner to hold CREATE on the schema; it is held only for this statement.
GRANT CREATE ON SCHEMA ti TO ti_membership_reader;
ALTER FUNCTION ti.user_memberships(text) OWNER TO ti_membership_reader;
REVOKE CREATE ON SCHEMA ti FROM ti_membership_reader;
REVOKE ALL ON FUNCTION ti.user_memberships(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ti.user_memberships(text) TO ti_app;
GRANT SELECT ON ti.tenant, ti.membership, ti.role_grant TO ti_membership_reader;

-- ===========================================================================
-- Grants. No runtime role owns any table. Append-only tables: SELECT + INSERT only.
-- ===========================================================================
DO $$
DECLARE t text;
BEGIN
  FOR t IN SELECT tablename FROM pg_tables WHERE schemaname = 'ti' AND tablename NOT IN ('tenant','schema_migration') LOOP
    IF t IN ('audit_event','audit_checkpoint') THEN
      EXECUTE format('GRANT SELECT, INSERT ON ti.%I TO ti_app', t);
    ELSIF t = 'idempotency_record' THEN
      EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON ti.%I TO ti_app', t);  -- expiry cleanup only
    ELSE
      EXECUTE format('GRANT SELECT, INSERT, UPDATE ON ti.%I TO ti_app', t);
    END IF;
  END LOOP;
END $$;

GRANT SELECT ON ti.tenant TO ti_app;
GRANT SELECT, INSERT, UPDATE ON ti.tenant, ti.membership, ti.role_grant, ti.audit_stream TO ti_platform;
GRANT SELECT, INSERT ON ti.audit_event TO ti_platform;
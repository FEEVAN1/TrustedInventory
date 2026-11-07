-- TrustedInventory schema draft (M0). PostgreSQL 16.
-- Design artefact, not a migration: M1 splits it into EF Core + raw SQL migrations (docs/migration-design.md).
-- Run as a superuser on an empty database. Enum values: docs/enums.md. Tenant context: DEC-013.

\set ON_ERROR_STOP on

-- ---------------------------------------------------------------------------
-- Roles (TEN-002). Passwords are set by deployment, never in this file.
-- ---------------------------------------------------------------------------
CREATE ROLE ti_owner NOLOGIN NOBYPASSRLS;                -- owns every object; migrations SET ROLE to it
CREATE ROLE ti_migrator LOGIN NOBYPASSRLS IN ROLE ti_owner; -- migration identity, unavailable to runtime
CREATE ROLE ti_app LOGIN NOBYPASSRLS NOINHERIT;          -- API + worker runtime
CREATE ROLE ti_platform LOGIN NOBYPASSRLS NOINHERIT;     -- tenant provisioning only
CREATE ROLE ti_membership_reader NOLOGIN NOBYPASSRLS;    -- owns ti.user_memberships() only

CREATE EXTENSION IF NOT EXISTS btree_gist;  -- device assignment non-overlap

CREATE SCHEMA ti AUTHORIZATION ti_owner;
REVOKE ALL ON SCHEMA ti FROM PUBLIC;
GRANT USAGE ON SCHEMA ti TO ti_app, ti_platform, ti_membership_reader;

SET ROLE ti_owner;
SET search_path = ti;

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
  created_at  timestamptz NOT NULL DEFAULT now(),
  created_by  ti.subject_text NOT NULL,
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, gtin),
  FOREIGN KEY (tenant_id, product_id) REFERENCES ti.product (tenant_id, id)
);

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

CREATE TABLE ti.attachment (
  id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id      uuid NOT NULL REFERENCES ti.tenant(id),
  entity_type    ti.code_text NOT NULL,
  entity_id      uuid NOT NULL,
  object_key     text NOT NULL CHECK (char_length(object_key) BETWEEN 1 AND 512),
  media_type     text NOT NULL CHECK (char_length(media_type) BETWEEN 1 AND 127),
  size_bytes     bigint NOT NULL CHECK (size_bytes > 0),
  digest_sha256  ti.sha256 NOT NULL,
  scan_status    ti.attachment_scan_status NOT NULL DEFAULT 'Pending',
  created_at     timestamptz NOT NULL DEFAULT now(),
  created_by     ti.subject_text NOT NULL,
  updated_at     timestamptz NOT NULL DEFAULT now(),
  version        integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, object_key),
  CHECK (object_key LIKE tenant_id::text || '/%')   -- TEN-005 tenant-scoped object keys
);
CREATE INDEX attachment_entity_ix ON ti.attachment (tenant_id, entity_type, entity_id);

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
-- Outbound
-- ===========================================================================
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
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES ti.warehouse (tenant_id, id)
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
  FOREIGN KEY (tenant_id, warehouse_id, staging_bin_id) REFERENCES ti.bin (tenant_id, warehouse_id, id)
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
  CHECK (returned_qty <= base_qty)                -- EXC-005 / AT-EXC-03
);
CREATE INDEX shipment_line_lot_ix ON ti.shipment_line (tenant_id, product_id, lot_id);

-- ===========================================================================
-- Tasks, transfers, counts, adjustments, returns, recalls
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

CREATE TABLE ti.transfer (
  id                         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id                  uuid NOT NULL REFERENCES ti.tenant(id),
  source_warehouse_id        uuid NOT NULL,
  destination_warehouse_id   uuid NOT NULL,
  reference                  ti.code_text NOT NULL,
  status                     ti.transfer_status NOT NULL DEFAULT 'Draft',
  approved_by                ti.subject_text,
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
  CHECK (source_warehouse_id <> destination_warehouse_id)
);
CREATE INDEX transfer_status_ix ON ti.transfer (tenant_id, status, created_at);

CREATE TABLE ti.transfer_line (
  id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id          uuid NOT NULL REFERENCES ti.tenant(id),
  transfer_id        uuid NOT NULL,
  line_number        integer NOT NULL CHECK (line_number > 0),
  source_bucket_id   uuid NOT NULL,             -- preserves lot, serial, quality (EXC-001)
  product_id         uuid NOT NULL,
  requested_qty      ti.pos_qty NOT NULL,
  dispatched_qty     ti.qty NOT NULL DEFAULT 0,
  received_qty       ti.qty NOT NULL DEFAULT 0,
  disposed_qty       ti.qty NOT NULL DEFAULT 0, -- approved TransitDisposition (STK-003); type is on the movement's approval
  created_at         timestamptz NOT NULL DEFAULT now(),
  created_by         ti.subject_text NOT NULL,
  updated_at         timestamptz NOT NULL DEFAULT now(),
  version            integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, transfer_id, line_number),
  FOREIGN KEY (tenant_id, transfer_id) REFERENCES ti.transfer (tenant_id, id),
  FOREIGN KEY (tenant_id, source_bucket_id, product_id) REFERENCES ti.stock_bucket (tenant_id, id, product_id),
  CHECK (dispatched_qty <= requested_qty),
  CHECK (received_qty + disposed_qty <= dispatched_qty)
);

CREATE TABLE ti.stock_count (
  id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id     uuid NOT NULL REFERENCES ti.tenant(id),
  warehouse_id  uuid NOT NULL,
  reference     ti.code_text NOT NULL,
  status        ti.count_status NOT NULL DEFAULT 'Draft',
  revision      integer NOT NULL DEFAULT 1 CHECK (revision > 0),
  snapshot_at   timestamptz,
  approval_id   uuid,
  created_at    timestamptz NOT NULL DEFAULT now(),
  created_by    ti.subject_text NOT NULL,
  updated_at    timestamptz NOT NULL DEFAULT now(),
  version       integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, warehouse_id, id),
  UNIQUE (tenant_id, reference),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES ti.warehouse (tenant_id, id)
);

CREATE TABLE ti.stock_count_bin (
  id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id      uuid NOT NULL REFERENCES ti.tenant(id),
  count_id       uuid NOT NULL,
  warehouse_id   uuid NOT NULL,
  bin_id         uuid NOT NULL,
  lock_active    boolean NOT NULL DEFAULT false, -- true from start until post/cancel (EXC-003)
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
  FOREIGN KEY (tenant_id, product_id, lot_id) REFERENCES ti.lot (tenant_id, product_id, id),
  FOREIGN KEY (tenant_id, product_id, serial_id) REFERENCES ti.serial (tenant_id, product_id, id)
);

CREATE TABLE ti.approval (
  id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id            uuid NOT NULL REFERENCES ti.tenant(id),
  subject_type         ti.approval_subject_type NOT NULL,
  subject_id           uuid NOT NULL,
  subject_version      integer NOT NULL CHECK (subject_version > 0),
  content_digest       ti.sha256 NOT NULL,        -- SHA-256 of canonical subject content (IAM-003)
  action               ti.approval_action NOT NULL,
  request_reason       ti.reason_text NOT NULL,
  requester_subject    ti.subject_text NOT NULL,
  status               ti.approval_status NOT NULL DEFAULT 'Pending',
  approver_subject     ti.subject_text,
  meaning              ti.signature_meaning,
  sign_reason          ti.reason_text,
  reauth_reference     ti.sha256,                 -- digest of the re-auth token only (DEC-009)
  signed_at            timestamptz,
  expires_at           timestamptz,
  consumed_at          timestamptz,
  consumed_operation   text,
  created_at           timestamptz NOT NULL DEFAULT now(),
  created_by           ti.subject_text NOT NULL,
  updated_at           timestamptz NOT NULL DEFAULT now(),
  version              integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  CHECK (approver_subject IS NULL OR approver_subject <> requester_subject),   -- SOD-1
  CHECK (status IN ('Pending','Invalidated') OR status = 'Expired' AND signed_at IS NULL
         OR (approver_subject IS NOT NULL AND meaning IS NOT NULL AND sign_reason IS NOT NULL
             AND reauth_reference IS NOT NULL AND signed_at IS NOT NULL)),
  CHECK (status NOT IN ('Approved','Consumed') OR expires_at = signed_at + interval '30 minutes'),
  CHECK ((status = 'Consumed') = (consumed_at IS NOT NULL))
);
CREATE INDEX approval_subject_ix ON ti.approval (tenant_id, subject_type, subject_id, status);
-- An approval is consumed by exactly one posting.
CREATE UNIQUE INDEX approval_consumed_operation_uq ON ti.approval (tenant_id, consumed_operation) WHERE consumed_operation IS NOT NULL;

ALTER TABLE ti.stock_count ADD FOREIGN KEY (tenant_id, approval_id) REFERENCES ti.approval (tenant_id, id);

CREATE TABLE ti.adjustment (
  id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id             uuid NOT NULL REFERENCES ti.tenant(id),
  warehouse_id          uuid NOT NULL,
  bucket_id             uuid,                     -- NULL for an increase into a bucket that does not exist yet; resolved at posting
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
  CHECK (delta_qty > 0 OR bucket_id IS NOT NULL),   -- a reduction always targets an existing bucket
  CHECK (status <> 'Posted' OR bucket_id IS NOT NULL),
  FOREIGN KEY (tenant_id, approval_id) REFERENCES ti.approval (tenant_id, id),
  CHECK ((status = 'Posted') = (posted_movement_id IS NOT NULL)),
  CHECK (status NOT IN ('Approved','Posted') OR approval_id IS NOT NULL)
);

CREATE TABLE ti.customer_return (
  id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id              uuid NOT NULL REFERENCES ti.tenant(id),
  warehouse_id           uuid NOT NULL,
  shipment_line_id       uuid NOT NULL,
  product_id             uuid NOT NULL,
  returned_qty           ti.pos_qty NOT NULL,
  quarantine_bucket_id   uuid NOT NULL,           -- all returns enter Quarantine (EXC-005)
  status                 ti.return_status NOT NULL DEFAULT 'Received',
  disposition            ti.return_disposition,
  inspection_notes       ti.reason_text,
  inspected_by           ti.subject_text,
  approval_id            uuid,
  created_at             timestamptz NOT NULL DEFAULT now(),
  created_by             ti.subject_text NOT NULL,
  updated_at             timestamptz NOT NULL DEFAULT now(),
  version                integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES ti.warehouse (tenant_id, id),
  FOREIGN KEY (tenant_id, shipment_line_id, product_id) REFERENCES ti.shipment_line (tenant_id, id, product_id),
  FOREIGN KEY (tenant_id, quarantine_bucket_id, product_id) REFERENCES ti.stock_bucket (tenant_id, id, product_id),
  FOREIGN KEY (tenant_id, approval_id) REFERENCES ti.approval (tenant_id, id),
  CHECK ((status = 'Received') = (disposition IS NULL))
);

CREATE TABLE ti.recall (
  id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id       uuid NOT NULL REFERENCES ti.tenant(id),
  reference       ti.code_text NOT NULL,
  product_id      uuid NOT NULL,
  reason          ti.reason_text NOT NULL,
  effective_at    timestamptz NOT NULL,
  status          ti.recall_status NOT NULL DEFAULT 'Open',
  closure_approval_id uuid,
  closed_at       timestamptz,
  created_at      timestamptz NOT NULL DEFAULT now(),
  created_by      ti.subject_text NOT NULL,
  updated_at      timestamptz NOT NULL DEFAULT now(),
  version         integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, id, product_id),
  UNIQUE (tenant_id, reference),
  FOREIGN KEY (tenant_id, product_id) REFERENCES ti.product (tenant_id, id),
  FOREIGN KEY (tenant_id, closure_approval_id) REFERENCES ti.approval (tenant_id, id),
  CHECK ((status = 'Closed') = (closed_at IS NOT NULL AND closure_approval_id IS NOT NULL))
);

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
  CHECK (lot_id IS NOT NULL OR serial_id IS NOT NULL)
);

CREATE TABLE ti.recall_outreach (
  id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id    uuid NOT NULL REFERENCES ti.tenant(id),
  recall_id    uuid NOT NULL,
  customer_id  uuid NOT NULL,
  status       ti.recall_outreach_status NOT NULL DEFAULT 'Pending',  -- manual outreach tracking (EXC-006)
  note         ti.reason_text,
  created_at   timestamptz NOT NULL DEFAULT now(),
  created_by   ti.subject_text NOT NULL,
  updated_at   timestamptz NOT NULL DEFAULT now(),
  version      integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, recall_id, customer_id),
  FOREIGN KEY (tenant_id, recall_id) REFERENCES ti.recall (tenant_id, id),
  FOREIGN KEY (tenant_id, customer_id) REFERENCES ti.partner (tenant_id, id)
);

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
  FOREIGN KEY (tenant_id, reservation_id) REFERENCES ti.reservation (tenant_id, id),
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

ALTER TABLE ti.pick_confirmation ADD FOREIGN KEY (tenant_id, movement_id) REFERENCES ti.stock_movement (tenant_id, id);
ALTER TABLE ti.shipment_line ADD FOREIGN KEY (tenant_id, movement_id) REFERENCES ti.stock_movement (tenant_id, id);
ALTER TABLE ti.adjustment ADD FOREIGN KEY (tenant_id, posted_movement_id) REFERENCES ti.stock_movement (tenant_id, id);

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
-- Telemetry and excursions
-- ===========================================================================
CREATE TABLE ti.device (
  id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id              uuid NOT NULL REFERENCES ti.tenant(id),
  external_id            ti.code_text NOT NULL,
  integration_subject    ti.subject_text NOT NULL, -- service principal allowed to post for this device (TEL-001)
  credentials_reference  text NOT NULL CHECK (char_length(credentials_reference) BETWEEN 1 AND 300), -- secret-manager ref, never the secret
  interval_seconds       integer NOT NULL DEFAULT 60 CHECK (interval_seconds > 0),
  last_reading_at        timestamptz,
  active                 boolean NOT NULL DEFAULT true,
  created_at             timestamptz NOT NULL DEFAULT now(),
  created_by             ti.subject_text NOT NULL,
  updated_at             timestamptz NOT NULL DEFAULT now(),
  version                integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, external_id)
);

-- Effective-dated device placement (TEL-004); the current zone is the open assignment.
CREATE TABLE ti.device_zone_assignment (
  id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id       uuid NOT NULL REFERENCES ti.tenant(id),
  device_id       uuid NOT NULL,
  zone_id         uuid NOT NULL,
  effective_from  timestamptz NOT NULL,
  effective_to    timestamptz,
  created_at      timestamptz NOT NULL DEFAULT now(),
  created_by      ti.subject_text NOT NULL,
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, device_id) REFERENCES ti.device (tenant_id, id),
  FOREIGN KEY (tenant_id, zone_id) REFERENCES ti.zone (tenant_id, id),
  CHECK (effective_to IS NULL OR effective_to > effective_from),
  EXCLUDE USING gist (tenant_id WITH =, device_id WITH =, tstzrange(effective_from, effective_to) WITH &&)
);

CREATE TABLE ti.telemetry_reading (
  id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id           uuid NOT NULL REFERENCES ti.tenant(id),
  device_id           uuid NOT NULL,
  zone_id             uuid NOT NULL,             -- derived server-side from the assignment at observed_at
  external_event_id   ti.code_text NOT NULL,
  observed_at         timestamptz NOT NULL,
  received_at         timestamptz NOT NULL DEFAULT now(),
  temperature_c       ti.celsius NOT NULL,
  is_late             boolean NOT NULL DEFAULT false,
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, device_id, external_event_id), -- duplicate eventId is a no-op (TEL-001)
  FOREIGN KEY (tenant_id, device_id) REFERENCES ti.device (tenant_id, id),
  FOREIGN KEY (tenant_id, zone_id) REFERENCES ti.zone (tenant_id, id),
  CHECK (observed_at <= received_at + interval '60 seconds')  -- TEL-002 future skew
);
CREATE INDEX telemetry_reading_device_ix ON ti.telemetry_reading (tenant_id, device_id, observed_at);
CREATE INDEX telemetry_reading_zone_ix ON ti.telemetry_reading (tenant_id, zone_id, observed_at);

CREATE TABLE ti.excursion (
  id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id              uuid NOT NULL REFERENCES ti.tenant(id),
  zone_id                uuid NOT NULL,
  product_id             uuid NOT NULL,           -- evaluated against this product's profile (DEC-012)
  storage_profile_id     uuid NOT NULL,
  excursion_policy_id    uuid NOT NULL,
  trigger_reading_id     uuid NOT NULL,
  started_at             timestamptz NOT NULL,
  ended_at               timestamptz,
  first_abnormal_c       ti.celsius NOT NULL,
  last_abnormal_c        ti.celsius NOT NULL,
  is_historical          boolean NOT NULL DEFAULT false, -- raised from a late reading (TEL-004)
  status                 ti.excursion_status NOT NULL DEFAULT 'Open',
  acknowledged_by        ti.subject_text,
  acknowledged_at        timestamptz,
  disposition            ti.excursion_disposition,
  disposition_approval_id uuid,
  resolved_at            timestamptz,
  created_at             timestamptz NOT NULL DEFAULT now(),
  created_by             ti.subject_text NOT NULL,
  updated_at             timestamptz NOT NULL DEFAULT now(),
  version                integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, zone_id) REFERENCES ti.zone (tenant_id, id),
  FOREIGN KEY (tenant_id, product_id) REFERENCES ti.product (tenant_id, id),
  FOREIGN KEY (tenant_id, storage_profile_id) REFERENCES ti.storage_profile (tenant_id, id),
  FOREIGN KEY (tenant_id, excursion_policy_id) REFERENCES ti.excursion_policy (tenant_id, id),
  FOREIGN KEY (tenant_id, trigger_reading_id) REFERENCES ti.telemetry_reading (tenant_id, id),
  FOREIGN KEY (tenant_id, disposition_approval_id) REFERENCES ti.approval (tenant_id, id),
  CHECK ((status = 'Resolved') = (disposition IS NOT NULL AND disposition_approval_id IS NOT NULL AND resolved_at IS NOT NULL)),
  CHECK (status = 'Open' OR acknowledged_at IS NOT NULL)
);
-- One open excursion per zone/product; repeat abnormal readings extend it (AT-TEL-01 "holds once").
CREATE UNIQUE INDEX excursion_open_uq ON ti.excursion (tenant_id, zone_id, product_id) WHERE status <> 'Resolved';

CREATE TABLE ti.excursion_bucket (
  id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id      uuid NOT NULL REFERENCES ti.tenant(id),
  excursion_id   uuid NOT NULL,
  bucket_id      uuid NOT NULL,                 -- Hold bucket created by the excursion
  held_qty       ti.pos_qty NOT NULL,
  created_at     timestamptz NOT NULL DEFAULT now(),
  created_by     ti.subject_text NOT NULL,
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, excursion_id, bucket_id),
  FOREIGN KEY (tenant_id, excursion_id) REFERENCES ti.excursion (tenant_id, id),
  FOREIGN KEY (tenant_id, bucket_id) REFERENCES ti.stock_bucket (tenant_id, id)
);

CREATE TABLE ti.excursion_shipment_flag (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id         uuid NOT NULL REFERENCES ti.tenant(id),
  excursion_id      uuid NOT NULL,
  shipment_line_id  uuid NOT NULL,              -- potentially affected delivery for QA review (TEL-004)
  qa_outcome        ti.reason_text,
  created_at        timestamptz NOT NULL DEFAULT now(),
  created_by        ti.subject_text NOT NULL,
  updated_at        timestamptz NOT NULL DEFAULT now(),
  version           integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, excursion_id, shipment_line_id),
  FOREIGN KEY (tenant_id, excursion_id) REFERENCES ti.excursion (tenant_id, id),
  FOREIGN KEY (tenant_id, shipment_line_id) REFERENCES ti.shipment_line (tenant_id, id)
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

CREATE TABLE ti.export_job (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id        uuid NOT NULL REFERENCES ti.tenant(id),
  type             ti.export_type NOT NULL,
  status           ti.export_status NOT NULL DEFAULT 'Queued',
  filter           jsonb NOT NULL,
  filter_digest    ti.sha256 NOT NULL,
  stream_from_seq  bigint,
  stream_to_seq    bigint,
  object_key       text CHECK (object_key IS NULL OR object_key LIKE tenant_id::text || '/%'),
  manifest         jsonb,                        -- integrity manifest + signature references (AUD-004)
  error            text,
  completed_at     timestamptz,
  expires_at       timestamptz,
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       ti.subject_text NOT NULL,
  updated_at       timestamptz NOT NULL DEFAULT now(),
  version          integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  CHECK ((status = 'Completed') = (object_key IS NOT NULL AND manifest IS NOT NULL))
);

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
-- Append-only enforcement (defence in depth beside missing grants)
-- ===========================================================================
CREATE FUNCTION ti.reject_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION '% on %.% is not permitted: append-only table', TG_OP, TG_TABLE_SCHEMA, TG_TABLE_NAME
    USING ERRCODE = 'insufficient_privilege';
END $$;

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['stock_movement','audit_event','audit_checkpoint','telemetry_reading',
                           'pick_confirmation','document_operation','receipt_line','receipt_line_serial']
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

-- Platform provisioning: create audit streams and the first administrator membership/grant (TEN-004).
CREATE POLICY platform_provision ON ti.membership TO ti_platform USING (true) WITH CHECK (true);
CREATE POLICY platform_provision ON ti.role_grant TO ti_platform USING (true) WITH CHECK (true);
CREATE POLICY platform_provision ON ti.audit_stream TO ti_platform USING (true) WITH CHECK (true);
CREATE POLICY platform_provision ON ti.audit_event FOR INSERT TO ti_platform WITH CHECK (true);

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

RESET ROLE;
ALTER FUNCTION ti.user_memberships(text) OWNER TO ti_membership_reader;
REVOKE ALL ON FUNCTION ti.user_memberships(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ti.user_memberships(text) TO ti_app;
GRANT SELECT ON ti.tenant, ti.membership, ti.role_grant TO ti_membership_reader;

-- ===========================================================================
-- Grants. No runtime role owns any table. Append-only tables: SELECT + INSERT only.
-- ===========================================================================
SET ROLE ti_owner;
-- Types and domains keep the default PUBLIC USAGE privilege.

DO $$
DECLARE t text;
BEGIN
  FOR t IN SELECT tablename FROM pg_tables WHERE schemaname = 'ti' AND tablename <> 'tenant' LOOP
    IF t IN ('stock_movement','audit_event','audit_checkpoint','telemetry_reading','pick_confirmation',
             'document_operation','receipt_line','receipt_line_serial') THEN
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
GRANT INSERT ON ti.audit_event TO ti_platform;

RESET ROLE;

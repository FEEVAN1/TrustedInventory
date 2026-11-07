# Data dictionary

Logical model for TrustedInventory R1 (spec sections 07–08 plus DEC-011). The executable form is
`db/design/schema-draft.sql` (PostgreSQL 16, schema `ti`). It is checked by `db/design/rls-smoke.sql`.
Diagrams are in `er-model.md` and enum values in `enums.md`.

## Conventions

| Rule | Implementation | Requirement |
|------|----------------|-------------|
| IDs | `uuid` PK, default `gen_random_uuid()`. The application may supply time-ordered UUIDs. | section 07 |
| Tenant ownership | Every tenant-owned table has `tenant_id uuid NOT NULL` → `tenant`, plus `UNIQUE (tenant_id, id)` | TEN-001 |
| Cross-row references | Always composite: `(tenant_id, x_id) → parent (tenant_id, id)`. Where a child must also agree on product, warehouse or handling profile, the FK includes that column too, e.g. `(tenant_id, warehouse_id, bin_id) → bin`. | TEN-001 |
| Standard columns | Mutable tables: `created_at timestamptz`, `created_by subject`, `updated_at timestamptz`, `version integer > 0`, which is the optimistic-concurrency ETag (API-002). Append-only tables have only `created_at`/`created_by`, or their own event timestamps. | section 07 |
| Names | Domain `name_text`: trimmed, 1–200 characters | section 07 |
| Codes | Domain `code_text`: trimmed, 1–64 characters, unique within parent | section 07 |
| Reasons | Domain `reason_text`: 1–2000 characters with at least one non-blank character. The API caps reasons at 1000. | AUD-001 |
| Identifiers | Domain `identifier_text`: trimmed, 1–100 characters, for lot and serial numbers as scanned | INB-002 |
| Quantities | Domains `qty` (≥ 0), `pos_qty` (> 0), `signed_qty` (≠ 0). All are unconstrained `numeric` with `scale ≤ 6` and an absolute value below 10¹⁴. Excess precision is **rejected, not rounded**: `numeric(20,6)` would round it silently. | DEC-014, section 09 |
| Temperatures | Domain `celsius`: `numeric(6,2)`, −273.15 to 1000 | |
| Hashes | Domain `sha256`: 32-byte `bytea` | AUD-002 |
| Timestamps | `timestamptz` (UTC on the wire). Expiry is a `date`. | section 03 |
| Retirement | Master data has `active`. Referenced rows are retired, never deleted, and `ti_app` has no DELETE except on `idempotency_record`. | section 07 |
| Append-only | `stock_movement`, `audit_event`, `audit_checkpoint`, `telemetry_reading`, `pick_confirmation`, `document_operation`, `receipt_line`, `receipt_line_serial` give `ti_app` SELECT and INSERT only. A trigger also rejects UPDATE, DELETE and TRUNCATE, superuser included. | STK-004, AUD-003 |

"Std" in the tables below means the standard mutable columns.

---

## 1. Organization

### tenant (platform table)
Isolated business account. There is no `tenant_id`: `ti_app` can read only the row whose `id` equals the context tenant, and `ti_platform` manages the table.

| Column | Type | Null | Constraint |
|--------|------|------|------------|
| id | uuid | N | PK |
| name | name_text | N | |
| status | tenant_status | N | default `Active` |
| Std | | | |

### membership
A user's membership in a tenant. A session must select an active membership (IAM-002).

| Column | Type | Null | Constraint |
|--------|------|------|------------|
| user_subject | subject_text | N | OIDC `sub`; unique per tenant |
| display_name | name_text | Y | No other personal data is stored |
| active | boolean | N | `active = (revoked_at IS NULL)` |
| revoked_at, revoked_by | timestamptz, subject | Y | |
| Std | | | |

Keys: `UNIQUE (tenant_id, user_subject)`, `UNIQUE (tenant_id, id, user_subject)` (FK target for role_grant).

### role_grant
A role granted to a membership, optionally scoped to one warehouse. Separation of duty: see `permissions.md`.

| Column | Type | Null | Constraint |
|--------|------|------|------------|
| membership_id, grantee_subject | uuid, subject | N | FK `(tenant_id, membership_id, grantee_subject) → membership` |
| role | role_name | N | Never `PlatformAdministrator` |
| warehouse_id | uuid | Y | NULL means all warehouses. FK → warehouse. |
| reason | reason_text | N | Grant audit |
| granted_by, granted_at | subject, timestamptz | N | `QaOfficer` requires `granted_by <> grantee_subject` (SOD-3) |
| revoked_at, revoked_by | | Y | Set together |
| Std | | | |

Keys: partial unique `(tenant_id, membership_id, role, warehouse_id) NULLS NOT DISTINCT WHERE revoked_at IS NULL`.

### configuration_version
Versioned tenant, warehouse and product configuration (CFG-001). Rows are inserted, never edited.

| Column | Type | Null | Constraint |
|--------|------|------|------------|
| scope_type | text | N | `Tenant`, `Warehouse` or `Product` |
| scope_id | uuid | Y | NULL exactly when scope is Tenant |
| config_version | integer | N | Unique per scope |
| settings | jsonb | N | Validated by the application against a schema per scope |
| reason | reason_text | N | |
| effective_from | timestamptz | N | Rules apply prospectively |

## 2. Catalog and locations

### warehouse
| Column | Type | Null | Constraint |
|--------|------|------|------------|
| code | code_text | N | Unique per tenant |
| name | name_text | N | |
| timezone | text | N | IANA name, validated by the API. Drives expiry cut-off (section 03). |
| active | boolean | N | |
| Std | | | |

### zone
A storage area with an operating temperature range and allowed hazard classes (DEC-012).

| Column | Type | Null | Constraint |
|--------|------|------|------------|
| warehouse_id | uuid | N | FK → warehouse |
| code, name | | N | Code unique per warehouse |
| temp_min_c, temp_max_c | celsius | Y | Both NULL (uncontrolled ambient) or both set with min < max |
| is_system | boolean | N | System zone holding the Transit bin (DEC-010) |
| active | boolean | N | |
| Std | | | |

Keys: `UNIQUE (tenant_id, warehouse_id, id)` lets bins prove their warehouse.

### zone_allowed_hazard_class
An explicit allowed-class mapping. R1 does not infer chemistry (section 07). Unique `(tenant_id, zone_id, hazard_class)`.

### bin
| Column | Type | Null | Constraint |
|--------|------|------|------------|
| warehouse_id, zone_id | uuid | N | FK `(tenant_id, warehouse_id, zone_id) → zone` so the bin and zone share a warehouse |
| code | code_text | N | Unique per **warehouse**, so scans are unambiguous (stricter than per zone) |
| kind | bin_kind | N | `Storage`, `Staging`, `Quarantine` or `Transit` |
| pick_priority | integer ≥ 0 | N | Putaway ranking (INB-005) |
| capacity, capacity_uom_id | pos_qty, uuid | Y | Set together. Capacity is enforced when present (section 07). |
| active | boolean | N | |
| Std | | | |

Keys: partial unique for one `Transit` bin per warehouse.

### unit
Unit of measure, tenant-owned. Code unique per tenant.

### storage_profile
Product temperature requirements (CFG-002).

| Column | Type | Null | Constraint |
|--------|------|------|------------|
| name | name_text | N | Unique per tenant |
| min_c, max_c | celsius | N | `min_c < max_c` |
| monitoring_required | boolean | N | |
| stale_after_seconds | integer > 0 | N | Default 300 (TEL-002) |
| excursion_policy_id | uuid | N | Required. With NOT NULL limits, any product linked to a profile is valid for activation (CFG-002). |
| active | boolean | N | |
| Std | | | |

### excursion_policy
`hold_on_first_reading` must be true in R1 (TEL-003). `notify_recipients` is a jsonb list, used only when configured (TEL-005).

### product
| Column | Type | Null | Constraint |
|--------|------|------|------------|
| sku | code_text | N | Unique per tenant |
| name | name_text | N | |
| handling_profile | handling_profile | N | Medical requires `tracking_mode IN (Lot, LotAndSerial)` and `rotation_rule = FEFO` |
| tracking_mode | tracking_mode | N | Serial modes require `base_precision = 0` |
| base_uom_id | uuid | N | FK → unit |
| base_precision | smallint 0–6 | N | Maximum fractional digits for this product's quantities (application-enforced) |
| rotation_rule | rotation_rule | N | FIFO default for General; LIFO by tenant choice (section 03) |
| hazard_class | code_text | Y | DEC-011 |
| storage_profile_id | uuid | Y | NULL means no temperature requirement |
| active | boolean | N | |
| Std | | | |

Keys: `UNIQUE (tenant_id, id, handling_profile)` is the FK target that pins lot handling to the product.

### product_conversion
`factor_to_base > 0`, scale ≤ 6. Unique `(tenant_id, product_id, alternate_uom_id)`. Transaction lines freeze the factor (`uom_factor_to_base`) when they are created.

### product_barcode
GTIN to product mapping (INB-002). `gtin` is normalised to 14 digits with leading zeros kept, unique per tenant, so a GTIN can never be ambiguous.

### partner
Supplier or customer, code unique per tenant. No payment or patient data.

### lot
| Column | Type | Null | Constraint |
|--------|------|------|------------|
| product_id, handling_profile | uuid, enum | N | FK `(tenant_id, product_id, handling_profile) → product`. A product's profile cannot change once lots exist. |
| lot_number | identifier_text | N | Unique per tenant and product |
| expiry_date | date | Y | Required when `handling_profile = Medical` |
| supplier_ref | text ≤ 200 | Y | |
| Std | | | |

Expiry is immutable after receipt except through an audited QA correction (`ExpiryCorrection` approval) while stock is held. The application enforces this; see the invariants section.

### serial
`serial_number` is an `identifier_text`. Unique `(tenant_id, product_id, serial_number)`. The optional `lot_id` is checked by FK `(tenant_id, product_id, lot_id) → lot`.

### attachment
`object_key` must start with `{tenant_id}/` (TEN-005). It also records `digest_sha256`, `scan_status` and `size_bytes > 0`. Files are downloaded only through authorized short-lived links.

## 3. Stock

### stock_bucket
The current-balance projection of the ledger (STK-004). There is one row per stock dimension.

| Column | Type | Null | Constraint |
|--------|------|------|------------|
| warehouse_id, bin_id | uuid | N | FK `(tenant_id, warehouse_id, bin_id) → bin` |
| product_id | uuid | N | FK → product |
| lot_id | uuid | Y | FK `(tenant_id, product_id, lot_id) → lot` |
| serial_id | uuid | Y | FK `(tenant_id, product_id, serial_id) → serial`. If set, quantities are 0 or 1. |
| quality_status | quality_status | N | Separate buckets per quality state (MED-002) |
| received_at | timestamptz | N | FIFO/LIFO ordering key (STK-005) |
| on_hand_qty | qty | N | Includes staged picked goods (STK-001) |
| reserved_qty | qty | N | `reserved_qty ≤ on_hand_qty` |
| Std | | | `version` is used for optimistic concurrency on API reads |

Keys: `UNIQUE NULLS NOT DISTINCT (tenant_id, bin_id, product_id, lot_id, serial_id, quality_status)`, `UNIQUE (tenant_id, id, product_id)` (FK target that forces movements and reservations to agree on product), and partial unique `(tenant_id, serial_id) WHERE on_hand_qty > 0` (one physical unit per serial).

### stock_movement (append-only)
The authoritative ledger (STK-004).

| Column | Type | Null | Constraint |
|--------|------|------|------------|
| movement_type | movement_type | N | See `enums.md` |
| operation_key | text ≤ 300 | N | **Unique per tenant.** A permanent business-operation key such as `receipt:{receiptLineId}` or `pick:{confirmationId}` (STK-007). |
| product_id, lot_id, serial_id | uuid | N/Y/Y | Denormalised for traceability queries |
| source_bucket_id, destination_bucket_id | uuid | Y | FKs include product. Whether each is NULL is fixed by movement type: external sources are Receipt, AdjustmentIncrease and ReturnReceipt; external destinations are Dispatch, TransitDisposition, AdjustmentDecrease, Disposal and SupplierReturn; all other types need both. |
| base_qty | pos_qty | N | Always > 0. Direction comes from source and destination. |
| document_type, document_id, document_line_id | document_type enum, uuid, uuid | N/N/Y | Generic document link |
| reservation_id | uuid | Y | Required for Pick, Unpick and Dispatch |
| reversal_of_id | uuid | Y | Required exactly when type is `Reversal` |
| reason | reason_text | Y | |
| actor_subject | subject | N | Comes from the token, never from the request body (API-002) |
| occurred_at, recorded_at | timestamptz | N | |
| correlation_id | text | Y | |

### document_operation (append-only)
A permanent command-level key, such as `dispatch:{orderId}:{dispatchReference}`, that prevents a second posting after the 7-day idempotency record has expired (STK-007). PK `(tenant_id, operation_key)`. `result_reference` lets a late retry replay the original result.

## 4. Inbound

### purchase_order
`status` is a purchase_order_status. `reference` is unique per tenant. `short_close_reason` is required exactly when the status is `ShortClosed` (INB-001). FKs go to supplier partner and warehouse.

### purchase_order_line
Conversion snapshot (`uom_id`, `uom_factor_to_base`, `ordered_uom_qty`), `ordered_base_qty > 0` and `received_base_qty ≤ ordered_base_qty`. The last is the zero over-receipt tolerance (INB-001, D08). If the owner later allows a tolerance, this CHECK becomes an application rule.

### receipt
Posted atomically by one command (DEC-005). FK `(tenant_id, warehouse_id, purchase_order_id) → purchase_order`, so a receipt cannot land in a different warehouse from its order.

### receipt_line (append-only)
Records the frozen conversion, `base_qty > 0`, `measured_temperature_c`, `staging_bin_id` (FK checks the warehouse), `quality_status` (Available, Quarantine or Hold; Hold requires a reason), `inspection_result` (`Passed`/`Failed`; Failed can never be Available), `supplier_reference`, the raw `scanned_barcode` (DEC-021) and `bucket_id`. The FK to the PO line includes product, so a line cannot receive a different product.

### receipt_line_serial (append-only)
`UNIQUE (tenant_id, serial_id)`: a serial is received once. Reused serial stock is rejected (INB-004).

### warehouse_task
Putaway, rework and exception tasks (DEC-011). Putaway tasks require `receipt_line_id` and `task_qty`, and `remaining_qty ≤ task_qty`. Bin FKs check the warehouse.

## 5. Outbound

### outbound_order
`planned_dispatch_date date`, `reference` unique per tenant, FKs to customer and warehouse. The aggregate status is derived from the lines (section 13).

### outbound_order_line
`requested_base_qty > 0` and `min_remaining_shelf_life_days ≥ 0`, plus maintained counters with `picked ≤ allocated ≤ requested` and `dispatched ≤ picked`.

### reservation
A stock commitment segment (section 13).

| Column | Type | Null | Constraint |
|--------|------|------|------------|
| order_line_id, product_id | uuid | N | FK includes product |
| bucket_id | uuid | N | The storage bucket while Active; the **staging** bucket once Picked (STK-002) |
| qty | pos_qty | N | |
| state | reservation_state | N | |
| expires_at | timestamptz | Y | Set **exactly when** Active. Picked never expires (STK-008). |
| parent_reservation_id | uuid | Y | Segment split on partial pick |
| Std | | | |

Indexes: live `(tenant_id, bucket_id) WHERE state IN (Active, Picked)`; expiry worker `(tenant_id, expires_at) WHERE state = Active`.

### pick
A directed pick task, one per Active reservation segment (OUT-003). It holds `task_qty` and `picked_qty ≤ task_qty`, plus source bin, product, lot, assignee and `status` (task_status).

### pick_confirmation (append-only)
One row per scan-validated confirmation (OUT-004). Stores `picked_qty`, the resulting Picked reservation segment, staging bin, the raw `scanned` identifiers (jsonb, including serials) and the movement.

### shipment / shipment_line
Created by dispatch. `UNIQUE (tenant_id, order_id, dispatch_reference)`. A line records product, lot, serial and `base_qty`, plus a `returned_qty ≤ base_qty` counter. That counter enforces EXC-005 and AT-EXC-03 in the database.

## 6. Transfers, counts, adjustments, returns, recalls

### transfer / transfer_line
Source warehouse differs from destination. A line references its source bucket, which preserves lot, serial and quality (EXC-001). Quantities satisfy `dispatched ≤ requested` and `received + disposed ≤ dispatched`. The in-transit amount is `dispatched − received − disposed`, held in the source warehouse's Transit bin (DEC-010).

### stock_count / stock_count_bin / stock_count_line
`stock_count_bin.lock_active` with partial unique `(tenant_id, bin_id) WHERE lock_active`: at most one active count lock per bin (EXC-003). Count lines are unique per `(count, revision, bin, product, lot, serial)` and hold `expected_qty` (never sent to blind counters), `observed_qty` and the generated `variance_qty`. A rejected count creates a new revision; earlier observations are kept.

### adjustment
`delta_qty` is signed and non-zero, with `reason_code` and `narrative`. The target is given by bin, product, lot, serial and `quality_status` dimensions. `bucket_id` is required for a reduction, may be NULL for an increase into a new bucket, and must be set once Posted (DEC-022). `count_id` is set when the adjustment comes from a variance. `Approved` and `Posted` require `approval_id`, and `Posted` requires exactly one `posted_movement_id` (EXC-004).

### customer_return
References the shipment line, with an FK that includes product. `returned_qty > 0`. The return lands in a Quarantine bucket (EXC-005). `disposition` is NULL while the status is `Received`.

### recall / recall_item / recall_outreach
A recall has a product, reason and `effective_at`. Closing it requires `closure_approval_id` and `closed_at` (EXC-007). Each item is a lot or a serial of that product. Outreach status (`recall_outreach_status`) is recorded manually (EXC-006).

## 7. Approvals

### approval
| Column | Type | Null | Constraint |
|--------|------|------|------------|
| subject_type, subject_id, subject_version | | N | Binds the exact request version (IAM-003) |
| content_digest | sha256 | N | Digest of the canonical subject content |
| action | approval_action | N | What the consuming command does |
| request_reason, requester_subject | | N | |
| status | approval_status | N | |
| approver_subject | subject | Y | `≠ requester_subject` (SOD-1) |
| meaning, sign_reason, reauth_reference, signed_at | | Y | All required once signed. `reauth_reference` is a digest only, never a credential (DEC-009). |
| expires_at | timestamptz | Y | Equals `signed_at + 30 min` when Approved or Consumed |
| consumed_at, consumed_operation | | Y | Set exactly when Consumed. `consumed_operation` is unique, so one approval allows one posting. |
| Std | | | |

## 8. Telemetry and excursions

### device
`external_id` unique per tenant. `integration_subject` is the only service principal allowed to post for the device (TEL-001). `credentials_reference` names a secret-manager entry, never the secret. `interval_seconds` defaults to 60.

### device_zone_assignment
Effective-dated placement (TEL-004). A `btree_gist` exclusion constraint prevents overlapping periods per device.

### telemetry_reading (append-only)
Has `zone_id`, derived server-side from the assignment in force at `observed_at`, so a client can't substitute a zone (TEL-001). `UNIQUE (tenant_id, device_id, external_event_id)` makes a duplicate a no-op. `observed_at ≤ received_at + 60 s` enforces the future-skew rule (TEL-002). `is_late` marks late readings.

### excursion
One per zone and product, evaluated against that product's profile (DEC-012). Partial unique `(tenant_id, zone_id, product_id) WHERE status <> Resolved`, so repeated readings extend the open excursion and stock is held only once (AT-TEL-01). `Resolved` requires a disposition, its approval and `resolved_at`; leaving `Open` requires an acknowledgement. `is_historical` marks excursions raised from late readings (TEL-004).

### excursion_bucket / excursion_shipment_flag
`excursion_bucket` lists the Hold buckets the excursion created, with `held_qty`. `excursion_shipment_flag` lists potentially affected dispatched shipment lines for QA review. They are only flagged and never shown as physically blocked (TEL-004).

## 9. Alerts, exports, integration, audit

### alert
Type, severity, status and `details` (jsonb: zone, products, lots, first and last abnormal values; TEL-005). Partial unique `(tenant_id, dedupe_key) WHERE status <> Closed` prevents duplicate open alerts.

### export_job
`filter` and `filter_digest`, audit stream range, `object_key` (tenant-prefixed), `manifest` (integrity manifest and signature references; AUD-004) and `expires_at`. The 15-minute expiry applies to download links, not the job.

### outbox_event
Written in the same transaction as the change (ARC-003). `event_id` is globally unique for consumer deduplication. Rows carry `attempts ≤ 10` (section 18), lease columns, `sent_at` and `dead_lettered_at`. Partial index on unsent rows.

### idempotency_record
`UNIQUE (tenant_id, principal, route, key)`. Stores `payload_digest`, the stored response, and `expires_at ≥ created_at + 7 days` (STK-007).

### audit_stream
One row per tenant with `last_sequence` and `last_hash`. Locking this row serializes sequence and hash assignment (AUD-002).

### audit_event (append-only)
Records sequence (unique per tenant), occurred_at, actor (subject and `actor_type`: User, Service, Device or System), request and correlation IDs, event, object type, ID and version, reason, redacted before and after snapshots, `previous_hash` and `event_hash` (AUD-001, AUD-002).

### audit_checkpoint (append-only)
A daily signed checkpoint: sequence, hash, signature, `signing_key_id` and `external_reference` to retention-protected storage (AUD-003). Unique per tenant and day.

---

## Stock invariants: where each is enforced

| Requirement | Rule | Database enforcement | Application enforcement |
|-------------|------|----------------------|-------------------------|
| STK-001 | `0 ≤ reserved ≤ on_hand`; no negative stock | CHECK on stock_bucket; `qty` domain ≥ 0 | |
| STK-001 | allocatable = on_hand − reserved only if Available, compatible, unexpired, not blocked | **Cannot be a constraint**: it depends on today's date in the warehouse timezone, open recalls and excursions, and device staleness | Allocation query computes eligibility inside the locking transaction. Dispatch rechecks it (OUT-005). |
| STK-002 | Receipt +on_hand; reserve +reserved; pick moves on_hand and reserved to staging; dispatch −both | Movement source/destination rules by type; reservation `bucket_id` + state; `expires_at` only while Active | Command handlers update buckets and reservations in one transaction |
| STK-003 | Transfers conserve quantity | `received + disposed ≤ dispatched ≤ requested` on transfer_line | Ledger conservation test (AT-EXC-01) |
| STK-004 | Ledger authoritative; no unexplained balance edits | Append-only grants and triggers on stock_movement | Reconciliation job recomputes `Σ in − Σ out` per bucket and alerts (OPS-001). Buckets change only in the same transaction as a movement. |
| STK-005 | Deterministic lock order | | `SELECT … FOR UPDATE` in the documented order (`migration-design.md`). Bounded retry. |
| STK-006 | All-or-nothing allocation | | One transaction; rollback on any shortage |
| STK-007 | Idempotency and permanent operation keys | `idempotency_record` unique key; `stock_movement.operation_key` and `document_operation` unique | Digest comparison; replay of stored response |
| STK-008 | Unpicked reservations expire in 24 h; picked never expire; no double consumption | CHECK `(state = Active) = (expires_at IS NOT NULL)` | Worker locks reservation and bucket rows, re-reads state, releases only Active and expired |
| Section 09 | Excess precision rejected | Quantity domains: `scale ≤ 6` | Per-product `base_precision`; conversions that produce more digits are rejected |
| Section 09 | Reversal ≤ unreversed quantity | FK and type check only | Lock the buckets the original movement touched (`FOR UPDATE` on stock_bucket; row locks on stock_movement itself would need the UPDATE privilege `ti_app` deliberately lacks), then sum earlier reversals and compare |
| INB-004 | No duplicate or reused serial | `receipt_line_serial` unique serial; one on-hand bucket per serial | |
| MED-003 | Expired is derived | Not stored | Computed from `lot.expiry_date` and `warehouse.timezone` at the controllable clock's "now" |
| Section 07 | Lot expiry immutable after receipt | | Update handler permits change only with a consumed `ExpiryCorrection` approval while stock is held. Audited. |
| Section 07 | Storage compatibility (temperature and hazard) | | Putaway, transfer and receipt staging validation (DEC-012) |
| CFG-002 | Temperature-controlled product needs limits and policy | Profile limits and policy NOT NULL | |
| EXC-003 | Count locks block movements | One active lock per bin (partial unique) | Every movement handler checks `lock_active` for source and destination bins in the same transaction (DEC-008) |

Because the database cannot express eligibility, expiry and compatibility, those rules live in one
Application-layer eligibility service used by allocation, pick, dispatch and release. Integration tests
for that service run against real PostgreSQL with a controllable clock (section 19).

## 10. SaaS control plane (v3.0, sections 22–28)

Design draft: `db/design/saas-schema-draft.sql`, validated by `db/design/run-saas-smoke.ps1` (59 checks on
PostgreSQL 16). Real migrations arrive in S1/S2 (DEC-032). Decisions: DEC-033 to DEC-040.

### Tenant changes (replaces `status`, DEC-033)

| Column | Type | Null | Notes |
|--------|------|------|-------|
| lifecycle | ti.tenant_lifecycle | no (after 0005) | Provisioning, Ready, Archived, DeletionPending, Deleted; default Provisioning |
| slug | text | no (after 0005) | Lowercase DNS-label style, globally unique (`lower(slug)` index); navigation only (SAS-003) |
| home_region | text | no (after 0005) | Explicit at provisioning (SAS-002) |
| ready_at, archived_at, deletion_requested_at, deleted_at | timestamptz | yes | Ready requires ready_at |
| retention_hold | boolean | no | Blocks deletion (LFC-004) |
| security_suspended_at, security_suspension_reason, security_suspended_by | | yes | All or none; set by platform operations |
| provisioning_job_id | uuid | yes | FK `cp.provisioning_job` |

`status` and type `ti.tenant_status` are dropped by the contract step (0005) after a compatibility trigger
keeps both shapes in sync during rollout. Billing restriction is **not** a column: it is computed per
request (`docs/saas-state-matrix.md`).

### Tenant-scoped SaaS tables in `ti` (forced RLS, `tenant_isolation` for `ti_app`)

| Table | Purpose | Key constraints | Requirement |
|-------|---------|-----------------|-------------|
| invitation | Pending team invitations | `token_hash` (SHA-256) unique; lifetime ≤ 72 h; one pending per email; roles exclude owner and platform roles; Accepted ⇔ accepted_at, subject and membership | ONB-005, AT-ONB-02 |
| ownership_transfer | Owner hand-over awaiting recipient acceptance | One pending per tenant; recipient must be a member (composite FK); stores re-auth digest | ONB-006, SOD-5 |
| usage_counter | Quota usage per metric | PK (tenant_id, metric); `used ≥ 0`; locked FOR UPDATE inside the creating transaction | ENT-003, AT-ENT-01 |
| usage_reservation | Attachment-byte reservations before upload | `amount > 0`; expiry index for release | ENT-003 |

Invitations are in `ti` because tenant administrators manage them in their own tenant transaction;
acceptance happens before the invitee is a member, so `ti_control` reads (not creates) them by token hash.

### Control-plane tables in `cp` (forced RLS; `ti_control` full access)

| Table | Purpose | Key constraints | `ti_app` access |
|-------|---------|-----------------|-----------------|
| plan_version | Plan code + version, features, quota map | `(plan_code, version)` unique; immutable once published | SELECT published public rows |
| price_version | Amount per plan version, currency, interval | Minor units ≥ 0; exponent 0–4; ISO currency; immutable once referenced (trigger) | SELECT prices of published plans |
| billing_account | One per tenant | `tenant_id` unique; `(provider, provider_customer_id)` unique | SELECT own row **without** `tax_reference` |
| subscription | One per tenant | `tenant_id` unique; state-dependent CHECKs (Trialing needs trial dates, Active needs price and period, PastDue needs grace_end); `revision` bumps on every change | SELECT own |
| checkout_session | Hosted checkout attempts | One open per tenant; `(tenant_id, idempotency_key)` unique; HTTPS URL only; Completed ⇔ completed_at | SELECT own |
| subscription_change | Scheduled upgrade/downgrade | One Pending/Scheduled per subscription; Blocked needs blockers | SELECT own |
| entitlement_snapshot | Features + limits in force | Exactly one `is_current` per tenant; `(tenant_id, entitlement_version)` unique | SELECT own |
| invoice | Provider invoice snapshot | `(provider, provider_invoice_ref)` unique; `total = subtotal − discount + tax`; Paid ⇔ paid_at | SELECT own |
| payment | Payment and refund records | `(provider, provider_payment_ref)` unique; refunds reference the original | SELECT own |
| webhook_inbox | Verified provider events | `(provider, event_id)` unique; encrypted payload, redacted summary | none |
| provisioning_job | Resumable signup provisioning | `signup_key` unique; tenant id allocated up front; Ready ⇔ ready_at | none |
| terms_acceptance | Terms/privacy/subscription-terms evidence | Append-only | none |
| deletion_record | Closure, deletion register | One open per tenant; Deleted needs approval and backup expiry | none |

Money columns everywhere: `bigint` minor units + `char(3)` currency + `smallint` exponent (BIL-001).

### Access decision function

`cp.tenant_access()` (SECURITY DEFINER, owner `cp_access_reader`, EXECUTE for `ti_app` only) returns the
current tenant's lifecycle, security suspension, subscription state and clocks, and current entitlement
version, features and limits. It takes no tenant argument and reads `app.tenant_id`, so a request can only
ask about its own tenant and gets nothing without context. Access mode is computed in C# from this row.
`ti.user_memberships_v2(subject)` replaces `ti.user_memberships` and adds slug, lifecycle and a suspension
flag for the tenant selector.

### Roles

| Role | Login | Privileges |
|------|-------|------------|
| `ti_control` | yes | All `cp` tables (no DELETE; no UPDATE on terms_acceptance); `ti.tenant`, `membership`, `role_grant`, `audit_stream`, `usage_counter` (S/I/U); `audit_event` (S/I); `invitation` (S/U). **No** catalog, location or stock privileges. |
| `cp_access_reader` | no | Owns `cp.tenant_access()`; SELECT on tenant, subscription, entitlement_snapshot |
| `ti_platform` | yes | Unchanged; no USAGE on schema `cp` |
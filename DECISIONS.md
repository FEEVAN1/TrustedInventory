# Decisions

Engineering decisions taken while implementing the TrustedInventory specification. The current baseline
is **v3.0 SaaS** (`TrustedInventory_Implementation_Specification_updated.pdf`, 7 November 2026), which
replaces v2.0 (`TrustedInventory_Implementation_Specification.pdf`, 5 November 2026); DEC-001 to DEC-031
were taken against v2.0 and remain in force unless a later entry supersedes them. Each entry records the
conflict or gap, the resolution, and whether owner confirmation is still needed (AGT-003).

Status values: **Adopted** (engineering default, revisit only with a new entry), **Needs owner**
(default in force for development, must be confirmed before launch), **Superseded**.

Owner-level launch decisions D01–D10 from spec section 21 remain open and are tracked at the end.

---

## DEC-001 Runtime: .NET 10 LTS instead of .NET 8
- **Status:** Adopted (ARC-001 requires recording it)
- **Context:** ARC-001 retains .NET 8 as the compatibility baseline but requires verifying supported
  versions. .NET 8 LTS support ends on 10 November 2026, four days after this decision.
- **Decision:** Target `net10.0` (LTS, supported to November 2028). SDK pinned by `global.json`
  to 10.0.201 with `latestPatch` roll-forward. PostgreSQL 16 is retained (supported to November 2028).
- **Consequence:** Package versions are pinned in `Directory.Packages.props`; `packages.lock.json`
  is committed per project and restore runs in locked mode when `CI=true`.

## DEC-002 Development identity provider: Keycloak
- **Status:** Adopted for development; production provider remains D02 (Needs owner)
- **Decision:** Docker Compose runs Keycloak with a synthetic realm (users per role, TOTP MFA required).
  Integration tests use a signed-token test adapter that issues tokens with controllable `auth_time`
  and `acr`, so re-authentication rules can be tested against a controllable clock.
- **Consequence:** The API depends only on standard OIDC metadata (issuer, audience, JWKS), so the
  production provider can be swapped without code changes.

## DEC-003 Contract-first OpenAPI
- **Status:** Adopted
- **Context:** AGT-002 asks for generated OpenAPI; section 16 requires the full schema before frontend
  integration, which means before any endpoint code exists.
- **Decision:** `docs/openapi.yaml` (OpenAPI 3.1) is hand-authored in M0 and is the contract. From M1
  the API also generates its document at build time, and CI fails if the generated document diverges
  from the contract for implemented routes. The web client is generated from the contract.

## DEC-004 Quality status set
- **Status:** Adopted
- **Context:** Section 13 lists Quarantine, Available, Hold and Recalled but also refers to Damaged and
  Disposed dispositions without defining them as statuses.
- **Decision:** `QualityStatus = Quarantine | Available | Hold | Recalled | Damaged`.
  `Damaged` is a non-allocatable status for physically present stock awaiting disposal or supplier
  return. **Disposed is not a status:** disposal is an outbound `Disposal` movement that removes the
  quantity. `Expired` is never stored; it is derived from the lot expiry date and warehouse timezone (MED-003).
- **Allowed transitions:** Quarantine→Available (approved release, MED-001); Quarantine|Available→Hold;
  Quarantine|Available|Hold→Recalled (recall only); Hold→Available (disposition + approval);
  Quarantine|Hold→Damaged (reason); Recalled→Damaged (recall disposition, EXC-007). Recalled never
  goes to Available through ordinary release.

## DEC-005 Receipts post in one command
- **Status:** Adopted
- **Context:** The Receipt state model has Draft→Posted and Draft→Cancelled, but `POST /inventory/receive`
  returns a Posted receipt directly.
- **Decision:** In R1 a receipt is created and posted atomically by `POST /inventory/receive`.
  Scan-and-correct happens client-side before submission (INB-003). `Draft` and `Cancelled` stay in
  the enum for forward compatibility but are not reachable through the R1 API.
  Receipt states used in R1: `Posted → Closed` (closed when all putaway tasks are complete).

## DEC-006 One allocation service, two routes
- **Status:** Adopted
- **Decision:** `POST /orders/{id}/allocate` is canonical and uses each product's rotation rule
  (medical products are always FEFO). `POST /orders/allocate-fefo` is retained from the source PRD;
  it calls the same service with FEFO forced for every line. Both use persisted order lines only and
  return the same result schema.

## DEC-007 Permission catalogue
- **Status:** Adopted; role-to-permission mapping Needs owner (D03)
- **Context:** Section 05 describes roles but not discrete permissions; OUT/picker text refers to
  "explicit dispatch permission".
- **Decision:** Authorization is by permission, never by role name. Roles are named bundles of
  permissions. Picker and Dispatcher are separate roles. See `docs/permissions.md`.

## DEC-008 Stock counts and protective quality changes
- **Status:** Adopted
- **Context:** EXC-003 locks counted bins against stock mutations; holds and recalls must still be
  able to block stock immediately (TEL-003, EXC-006).
- **Decision:** A count lock blocks every quantity movement into or out of the locked bins (putaway,
  pick, unpick, bin transfer, adjustment, dispatch from staging in that bin). It does **not** block
  protective quality transitions (→Hold, →Recalled) because they change no physical quantity and
  failing them would be less safe. Releases (→Available) on locked bins are blocked until the count
  closes. Counts compare physical quantity by product/lot/serial across all quality statuses.

## DEC-009 Signature timing
- **Status:** Adopted
- **Context:** IAM-004 requires re-authentication no older than 5 minutes for signing; section 13
  expires approvals 30 minutes after signature.
- **Decision:** The 5-minute check applies when signing: `POST /approvals/{id}/sign` carries a
  `reauthToken` (a fresh ID token obtained with `max_age=0` / `prompt=login`). The server validates
  the token signature, issuer, audience, `sub` = caller, `auth_time` within 300 s, and MFA `acr`/`amr`,
  then stores only a digest of the token as `reauth_reference`. The signed approval may be consumed by
  a posting command for 30 minutes after `signed_at`. Posting rechecks the approver's membership and
  the subject version/digest (IAM-003) but needs no new re-authentication.

## DEC-010 Transit location
- **Status:** Adopted
- **Decision:** Every warehouse has one system-managed `Transit` bin in a system zone. Transfer dispatch
  moves stock from source bins into the **source** warehouse's transit bin; destination receipt moves it
  into a destination staging bin. Transit bins are excluded from allocation, putaway recommendation and
  counts, and are exempt from storage compatibility checks. Quality status, lot, expiry and serial are
  preserved in transit.
- **Known limitation:** temperature conditions during transit are not monitored in R1.

## DEC-011 Entities added beyond the section 07–08 dictionary
- **Status:** Adopted
- **Decision:** These are required by workflows elsewhere in the spec but missing from the dictionary:
  - `ProductBarcode` (tenant, gtin, product) unique per tenant: INB-002 GTIN mapping.
  - `Product.hazard_class` and `Product.storage_profile_id`: needed for compatibility (section 07).
  - `ZoneAllowedHazardClass`: explicit allowed-class mapping (section 07).
  - `Shipment` / `ShipmentLine`: dispatch returns `shipmentId`; returns reference shipment lines (EXC-005).
  - `WarehouseTask` (type Putaway | Rework | Exception): putaway tasks (INB-005/006), rework (OUT-006),
    exception tasks when no compatible bin exists.
  - `Recall` / `RecallItem`, `Alert`, `ExportJob`, `ConfigurationVersion`, `AuditCheckpoint`.
  - Bin `kind` (Storage | Staging | Transit | Quarantine) and `pick_priority`.

## DEC-012 Storage compatibility rule
- **Status:** Adopted
- **Decision:** A zone declares an operating range (`temp_min_c`, `temp_max_c`, nullable for
  uncontrolled ambient) and a set of allowed hazard classes. A product is compatible with a zone when:
  (a) the product has no storage profile with limits, or the zone range lies entirely within the
  product profile's `[min_c, max_c]`; and (b) the product has no hazard class, or the zone allows it.
  Excursion evaluation uses each **product's** profile limits against the zone's readings (TEL-003).

## DEC-013 Tenant context mechanism
- **Status:** Adopted
- **Decision:** Every database transaction begins with
  `SELECT set_config('app.tenant_id', $1, true)` (transaction-local) after membership validation.
  RLS policies compare `tenant_id` to `NULLIF(current_setting('app.tenant_id', true), '')::uuid`;
  a missing setting yields NULL and therefore no rows (TEN-003 fail-closed). Database roles:
  `ti_owner` (NOLOGIN, owns objects), `ti_migrator` (login that can switch to `ti_owner`; migrations
  only), `ti_app` (runtime API/worker; NOBYPASSRLS, not owner), `ti_platform` (tenant provisioning:
  tenants, memberships, role grants and their audit events; no stock access),
  `ti_membership_reader` (NOLOGIN; owns only the SECURITY DEFINER function
  `ti.user_memberships(subject)`, used before tenant context exists). FORCE RLS applies to the owner
  too; no role has a bypass policy. See `docs/migration-design.md`.

## DEC-014 Quantity representation
- **Status:** Adopted
- **Decision:** Database plain `numeric` with CHECK `scale(q) <= 6 AND abs(q) < 10^14` (the spec's
  `numeric(20,6)` range, but `numeric(20,6)` itself would silently round a 7th decimal instead of
  rejecting it); C# `decimal`; JSON decimal **strings** matching
  `^(0|[1-9][0-9]{0,13})(\.[0-9]{1,6})?$` (14 integer digits, up to 6 fractional). Per-product
  `base_precision` (0–6) further limits fractional digits; serial-tracked and discrete products use 0.
  Operational quantities must be > 0 (validated separately from the pattern).

## DEC-015 No mediator: direct application services
- **Status:** Adopted (owner instruction, 6 November 2026)
- **Context:** Section 04 names MediatR; MediatR 13+ is commercially licensed. The owner asked for an
  approach that does not use the MediatR/mediator pattern.
- **Decision:** Each module exposes plain application service classes (e.g. `WarehouseService`) with
  one method per use case. Minimal-API endpoints inject the service and call it directly; there is no
  request/handler dispatch. Cross-cutting behaviour is explicit inside each method, in a fixed order:
  `caller.Require…` (authorization, IAM-001) → validator (FluentValidation, used directly) →
  `ITenantUnitOfWork.WriteAsync/ReadAsync/WriteIdempotentAsync` (transaction, tenant context, audit,
  outbox, idempotency, retries). `AddApplication()` registers every public `*Service` class and every
  validator by reflection, so adding a module needs no shared registration edits.

## DEC-016 Additional endpoints required by workflows
- **Status:** Adopted
- **Decision:** The contract adds routes the workflows need but section 16/17 does not list:
  `GET /picks` (assigned tasks), `POST /picks/{id}/unpick`, `POST /orders/{id}/deallocate`,
  `POST /orders/{id}/cancel`, `GET /putaway/tasks`, `GET /tasks` and `POST /tasks/{id}/complete`
  (rework and exception tasks), `POST /quality/hold`, `POST /quality/disposition`,
  `GET /excursions`, `POST /excursions/{id}/acknowledge|resolve`, device CRUD, membership/role-grant
  administration, platform tenant provisioning, `GET /me` (memberships for the tenant selector),
  `POST /purchase-orders/{id}/short-close`, `POST /approvals/{id}/reject`, `POST /audit/verify`.

## DEC-017 Development and test environment
- **Status:** Adopted
- **Decision:** Real PostgreSQL 16 for all transaction and RLS tests (Testcontainers, from M1). The
  working directory path contains two non-breaking spaces (U+00A0) in `Evans  Felix`; scripts must
  quote paths and resolve them from the filesystem rather than typing them. The 8.3 short path
  (`C:\Users\EVANSF~1\DOCUME~1\PERSON~1\NEWFOL~1\Projects\TRUSTE~1`) avoids the problem.

## DEC-018 If-Match scope
- **Status:** Adopted
- **Decision:** `If-Match` is required on every PATCH and on every state-transition POST addressed to
  an existing resource (`/{collection}/{id}/{action}`). Collection-level stock commands
  (`/inventory/receive`, `/putaway/confirm`, `/inventory/bin-transfers`, `/orders/allocate-fefo`,
  `/quality/*`, `POST /returns`, `POST /recalls`) rely on the idempotency key plus row locks and
  revalidation instead. Consequence: `/orders/{id}/allocate` needs If-Match, `/orders/allocate-fefo`
  does not (DEC-006 alias keeps the source PRD shape).
- **Telemetry:** `POST /telemetry/readings` takes neither Idempotency-Key nor X-Tenant-Id; tenant and
  zone come from the device credential and `(deviceId, eventId)` deduplicates (TEL-001). A duplicate
  returns 200 with `duplicate: true`.

## DEC-019 Retirement and PATCH format
- **Status:** Adopted
- **Decision:** No DELETE routes. Master data is retired with `POST /{id}/retire` → 204 with the new
  ETag. PATCH bodies use `application/merge-patch+json`.

## DEC-020 Contract additions beyond DEC-016
- **Status:** Adopted
- **Decision:** Also added: `POST /inventory/bin-transfers` (EXC-002 had no route),
  `POST /transfers/{id}/disposition|cancel`, `POST /lots/{id}/expiry-correction`,
  `POST /counts/{id}/reject|cancel`, `POST /adjustments/{id}/cancel`, `POST /purchase-orders/{id}/cancel`,
  `POST /recalls/{id}/outreach`, configuration versions (`GET /configuration`,
  `GET|POST /configuration/versions`), `POST /devices/{id}/rotate-credential`,
  `GET /telemetry/readings`, attachments (upload, metadata, short-lived download link),
  `GET /exports/{id}/download-link`, read-only lists for reservations, shipments, lots, serials,
  receipts and audit events, CRUD for units, storage profiles, partners and zones, membership
  deactivation and role-grant revocation.
- **Special `x-permission` values:** `authenticated` (`GET /me`), `none` (health probes),
  `a|b` (approval sign/reject: the signing permission depends on the subject type, SOD-2).

## DEC-021 Receive item fields
- **Status:** Adopted
- **Context:** Section 16's receive item lacks the supplier reference INB-003 requires for medical
  receipts and the inspection result INB-004 depends on.
- **Decision:** Receive items add optional `supplierReference`, `scannedBarcode`, `inspectionResult`
  (`Passed`|`Failed`) and `exceptionReason` (required when recording expired or out-of-range arrivals
  into quarantine/hold). `targetBinId` is the staging or quarantine bin the goods first land in.

## DEC-022 Smaller contract defaults
- **Status:** Adopted unless noted
- Count submissions carry `lotNumber`/`serialNumber` as strings so counters can record unknown lots.
- Attachments: PDF, JPEG, PNG up to 10 MiB; downloadable only when scan status is `Clean`. Upload
  permission is `adjustments.request` for now. **Needs owner:** a dedicated `attachments.upload`
  permission is probably better.
- A recall with no lots or serials listed covers every lot of the product.
- An adjustment increase on medical stock with no target bucket lands in `Hold`.
- Device credential rotation: the previous credential stays valid until the new one is first used or
  24 hours pass, whichever is sooner.

## DEC-023 Database design details
- **Status:** Adopted
- Bin codes are unique per **warehouse** (stricter than per zone) so a scanned bin code is never ambiguous.
- Device location is an effective-dated `device_zone_assignment` table with a no-overlap exclusion
  constraint (`btree_gist`); readings store the zone derived at ingestion (TEL-001, TEL-004).
- A pick is a task per reservation (`pick`) plus immutable per-scan `pick_confirmation` rows.
- `document_operation` holds permanent command keys (e.g. `dispatch:{orderId}:{dispatchReference}`)
  so a command cannot post twice after its 7-day idempotency record expires (STK-007).
- At most one open excursion per zone and product, so affected stock is held once (AT-TEL-01).
- Storage profiles carry no hazard field; hazard compatibility uses only `product.hazard_class` and
  the zone's allowed classes (DEC-012).
- Zero over-receipt is a database CHECK (`received <= ordered`). A future tolerance (D08) moves this
  rule to the application.
- GTINs are stored normalized to 14 digits; the API accepts GTIN-8/12/13/14.
- Receipt lines, receipt serials, stock movements and audit events are append-only (grants plus
  triggers that also block TRUNCATE).
- The 60-second telemetry future-skew rule is also a database CHECK (TEL-002).

## DEC-025 SQL-first migrations and a separate migrator
- **Status:** Adopted (supersedes the EF-generated migration plan in docs/migration-design.md §2)
- **Context:** Most of the schema (roles, RLS policies, composite foreign keys, domains, triggers, the
  SECURITY DEFINER function, partial unique indexes) cannot be expressed by EF Core migrations, and the
  M0 draft was already validated as SQL.
- **Decision:** Migrations are reviewed SQL files in `db/migrations/NNNN_name.sql`, embedded in the
  `TrustedInventory.Migrator` console project and applied in order, each in its own transaction, as
  `ti_migrator` with `SET ROLE ti_owner`. `ti.schema_migration` records each id with a SHA-256 checksum;
  a changed applied file stops the run. An advisory lock prevents concurrent migrators. The migrator is
  the only component holding migration credentials (TEN-002). Cluster setup (roles, schema, extensions)
  is `db/bootstrap/0000_cluster.sql`, run once by a superuser. EF Core is used only for data access.
- **Consequence:** `SchemaVersion.Required` in Infrastructure names the latest migration; readiness
  fails until it is applied, and a test keeps it equal to the newest file.

## DEC-026 Platform administrators
- **Status:** Adopted for development; production mechanism Needs owner (with D02)
- **Context:** Role grants are per tenant and the schema forbids granting `PlatformAdministrator` there.
- **Decision:** Platform administrators are the token subjects listed in configuration
  `Platform:AdministratorSubjects`. They get only `platform.*` permissions and no tenant membership,
  so they have no stock access (section 05). Platform endpoints use the `ti_platform` pool.

## DEC-027 Storage profile contract
- **Status:** Adopted
- **Decision:** `permittedHazardClasses` is removed from the storage profile contract; hazard
  compatibility comes only from the product hazard class and the zone's allowed classes (DEC-012,
  DEC-023). The contract's embedded `excursionPolicy {immediateHold, notes}` is stored as the profile's
  own `excursion_policy` row (one per profile, created and updated with it); `notes` was added to that
  table. R1 accepts only `immediateHold: true` (TEL-003).

## DEC-028 Membership function ownership
- **Status:** Adopted
- **Decision:** PostgreSQL requires a function's new owner to hold CREATE on its schema. Migration 0001
  grants CREATE on `ti` to `ti_membership_reader` only for the `ALTER FUNCTION … OWNER` statement and
  revokes it immediately after. The M0 draft did this step as superuser and so never hit the rule.

## DEC-029 Catalog rules (M1)
- **Status:** Adopted
- A conversion factor may not carry more decimal places than the product's `basePrecision` (section 07
  "unsupported precision").
- Each storage profile owns one excursion policy named `storage-profile:{profileId}`, retired with it.
  `notify_recipients` stays at its default `[]` until alert delivery arrives in M5.
- A duplicate active GTIN is 422 `VALIDATION_FAILED` on `gtin`; `BARCODE_CONFLICT` is reserved for
  scanned-versus-entered mismatches at receiving (INB-003).
- Retiring a product does not retire its barcodes or conversions; its active barcodes keep their GTINs
  reserved until retired themselves. A storage profile or unit still used by active records cannot be
  retired (409).
- SKU length is 64 characters (database `code_text`); the contract is aligned to it.

## DEC-030 Tenant administration rules (M1)
- **Status:** Adopted
- `/platform/*` is gated by an authorization policy (authenticated, MFA, subject in
  `Platform:AdministratorSubjects`) that runs before the body is read (DEC-026).
- Provisioning a tenant writes the tenant, its audit stream, the first membership and a
  TenantAdministrator grant in one `ti_platform` transaction; the events open the new tenant's chain
  with the platform administrator as actor.
- Configuration version 1 is the built-in defaults and is never stored; stored versions start at 2.
  Creating a version requires If-Match naming the current version. `overReceiptTolerancePercent` must be
  0 until D08 is decided (the database CHECK enforces zero over-receipt, DEC-023).
- A member cannot deactivate their own membership (409), so an administrator cannot lock everyone out
  by accident. `TelemetryIntegration` and `PlatformAdministrator` cannot be granted to user memberships.
  Self-granting any role carrying `approvals.sign.quality` is 409 `APPROVAL_SELF` (SOD-3).
- Reasons for deactivation and revocation are kept in the audit event only (no column).
- `POST /audit/verify` runs in a write transaction because a failure opens a High `AuditChainFailure`
  alert (OPS-001). Checkpoints are compared by hash here; signatures are verified by the checkpoint job.
  `/audit/events` pages newest-first by sequence.

## DEC-031 Worker jobs and development stack (M1)
- **Status:** Adopted for development
- Jobs enumerate tenant ids through `ti_platform`, then work inside a per-tenant `ti_app` transaction as
  `system:worker` with no permissions (TEN-004). Outbox delivery and idempotency cleanup run for Active
  tenants; audit checkpoints include suspended tenants. Creating a checkpoint is not itself audited.
  (Tenant selection superseded by DEC-043.)
- Outbox: leased with `FOR UPDATE SKIP LOCKED`, exponential backoff (5 s base, 1 h cap), dead letter and
  one High `OutboxDeadLetter` alert after the 10th failure (NFR-JOBS). The development sink and the
  checkpoint retention store are **simulated** and labelled as such (AGT-004).
- Checkpoints are HMAC-SHA256 signed with `Audit:CheckpointKey` / `Audit:CheckpointKeyId`; the worker
  refuses to start without them. Production uses an asymmetric key in the secret manager (D06).
- Keycloak's hostname is fixed to `http://keycloak:8080` so host-obtained tokens and the API agree on the
  issuer; a browser login from the host needs `127.0.0.1 keycloak` in the hosts file. Synthetic users'
  password and TOTP seed come from `.env` (never committed). The PWA client allows direct grants for
  verification only, and that flow still requires password and OTP. MFA reaches the API as `amr=pwd,otp`
  from Keycloak's AMR mapper.
- `Migrator seed` writes synthetic tenants, memberships and grants as `ti_platform` with fixed ids and
  no provisioning audit events; development only.
- Images restore in locked mode and run as non-root.

## DEC-032 Adopting the v3.0 SaaS baseline
- **Status:** Adopted (owner instruction, 7 November 2026)
- **Decision:** v3.0 sections 22–31 (SaaS delivery, onboarding, plans, billing, lifecycle) are in scope.
  Delivery order: close M1 → v3.0 contract pass (this entry and DEC-033 to DEC-040) → S1 provisioning →
  M2 → S2/S3 before any commercial activation (spec section 31). Sections 7–19 are unchanged in
  substance, so M0/M1 work stands except where the entries below say otherwise.
- **Conflicts kept from earlier decisions:** v3.0 section 04 still names MediatR and .NET 8. The owner's
  no-mediator instruction (DEC-015) and .NET 10 (DEC-001; .NET 8 support ends 10 November 2026) stay.

## DEC-033 Tenant state: stored lifecycle, computed access mode
- **Status:** Adopted
- **Context:** Section 07 gives the tenant one `access_restriction` value; section 27 says
  `restriction_reasons` (plural); section 26 requires effective access computed on every command from
  current state, without waiting for a worker. A tenant can be both security-suspended and unpaid.
- **Decision:** The tenant row stores `lifecycle` (Provisioning, Ready, Archived, DeletionPending,
  Deleted) and a nullable **security suspension** (reason, at, by). Billing restriction is **not stored**:
  it is derived on each request from the subscription (state, `trial_end`, `grace_end`, `period_end`) and
  the request time. The computed **access mode** applies LFC-001 precedence:
  `Deleted > Closed (Archived/DeletionPending) > SecuritySuspended > BillingRestricted > Provisioning >
  Normal`. `restrictionReasons` in API responses lists every active reason, highest first. The v2.0
  `status Active/Suspended` column is replaced (migration 0003).

## DEC-034 Access categories and the state matrix
- **Status:** Adopted
- **Decision:** Every operation carries an access category (`x-access` in the contract, metadata on the
  endpoint). One table, `docs/saas-state-matrix.md`, decides which categories each access mode allows;
  the request pipeline enforces it before body binding, after membership and before permission
  (SAS-005). No endpoint carries its own lifecycle checks. Safety categories (monitoring, holds, recall
  initiation, safe reservation release) stay available in every mode except Deleted and, for humans,
  SecuritySuspended (section 26, SUI-003).

## DEC-035 Control-plane schema and roles
- **Status:** Adopted
- **Decision:** Control-plane tables (billing accounts, plan/price versions, subscriptions, changes,
  entitlement snapshots, invoices, payments, webhook inbox, provisioning jobs, checkout sessions, terms
  acceptance, deletion records, invitations) live in schema `cp`, owned by `ti_owner`. A new runtime role
  `ti_control` (provisioning, billing, webhooks, reconciliation) has privileges on `cp` and on the tenant
  provisioning rows only — **no stock or catalog privileges** (SAS-004). `ti_app` reads its own tenant's
  access state through the SECURITY DEFINER function `cp.tenant_access(tenant_id)` and has RLS-scoped
  SELECT on its own tenant's tenant-facing billing rows (gated in the application by `billing.read`).
  Usage counters are tenant-scoped rows in `ti` updated inside the command transaction (ENT-003).
  `ti_platform` keeps tenant metadata and security suspension only.

## DEC-036 SaaS roles and permissions
- **Status:** Adopted; bundles Need owner (with D03)
- **Decision:** New tenant roles `TenantOwner` and `BillingAdministrator`, new platform role
  `PlatformBilling` beside `PlatformAdministrator` (renamed in prose to platform operations). New
  permissions in `docs/permissions.md`. `TenantOwner` is never granted through the role-grant endpoint:
  only signup (first owner) and the ownership-transfer flow create it, and at least one active owner must
  remain (ONB-006). `BillingAdministrator` is granted only by an owner (billing delegation). A tenant
  administrator cannot grant either. Signup gives the first owner both `TenantOwner` and
  `TenantAdministrator` so setup can start.

## DEC-037 Routes for the SaaS surface
- **Status:** Adopted
- **Decision:** Tenant-facing SaaS routes are under `/api/v1/saas/*` (section 28). Platform routes move
  from `/api/v1/platform/*` to `/platform/v1/*` as section 28 shows. Platform tenant creation is removed:
  tenants are created only through signup (ONB-002); the platform console keeps metadata, provisioning
  failures and reasoned security suspend/resume. Public routes (no token): `GET /api/v1/saas/plans` and
  `POST /api/v1/webhooks/billing/{provider}` (signature-authenticated). Signup requires a verified email
  claim and MFA like every human route (IAM-002).

## DEC-038 Entitlements and quotas in the pipeline
- **Status:** Adopted
- **Decision:** Each operation may declare a feature key (`x-entitlement`) and a quota key (`x-quota`).
  The pipeline rejects a missing feature with 403 `FEATURE_NOT_ENABLED` before binding. Quota
  reservation happens inside the command transaction by locking and incrementing the tenant's usage
  counter row, so concurrent creations cannot exceed the limit (ENT-003, AT-ENT-01); the limit comes from
  the current entitlement snapshot read in the same transaction. Exports stay on every plan (section 24).
  `core_inventory` gates all inventory operations; `medical_storage` gates medical products, quality
  release and recall; `telemetry` gates devices and readings; `service_api` gates service credentials;
  `advanced_reporting` gates reports beyond the basic set.

## DEC-039 Billing provider adapter and simulator
- **Status:** Adopted; provider choice Needs owner (D09)
- **Decision:** Billing goes through an `IBillingProvider` adapter (checkout session, portal session,
  webhook signature verification, authoritative resource fetch). The only implementation until the owner
  chooses a provider is a clearly labelled **simulator** with its own signing secret and a test console;
  live checkout stays disabled by configuration (section 25, AGT-004). Amounts are integer minor units
  with currency and exponent (BIL-001). Design in `docs/billing-adapter.md`.

## DEC-040 Commercial and lifecycle defaults used for development
- **Status:** Needs owner (D09, D10)
- **Decision:** Development uses the spec's proposed defaults: 14-day trial from Ready, no card, one
  trial per tenant; Starter/Growth/Enterprise **fixture** plans (`docs/entitlement-catalog.json`) with
  placeholder prices in a test currency; monthly/annual flat subscriptions; 7-day past-due grace; 90-day
  export window; no proration. None of these may be published, charged or applied to production data
  until the owner approves them.

## DEC-041 v3.0 contract details
- **Status:** Adopted
- **Invitations** use tenant-header routes (`POST/GET /api/v1/invitations`, `/invitations/{id}/revoke`)
  instead of the spec's `/tenants/{id}/invitations`, consistent with every other tenant route.
  `POST /invitations/accept` takes no tenant header (the caller is not a member yet); the token binds
  the tenant.
- **Special annotation values:** `x-access: none` (health, public plans, billing webhook) and
  `x-access: platform` (every `/platform/v1` route); `x-permission: member` (any active member: own
  entitlements, accepting an ownership transfer addressed to the caller). The tenant middleware must
  support `member` in S1.
- **Preview routes** `POST /saas/checkout/preview` and `/saas/subscription/change/preview` return the
  `previewDigest` that checkout and plan changes must echo (BIL-003, ENT-005).
- **`/platform/v1`** paths use a path-level `servers: [{url: '/'}]` override. The five v2.0
  `/api/v1/platform/tenants` operations stay, deprecated, with their v2.0 response shape until S1 moves
  the implementation; tenant creation is then removed (DEC-037).
- **Problem body** gains optional `quota`, `feature`, `recovery` and `blockers[]` so 409/403 SaaS
  errors name the limit, the missing feature or the recovery action (ENT-004, section 28).
- **Entitlements:** `core_inventory` on every non-safety inventory operation, `telemetry` on device and
  reading operations, `medical_storage` on quality release; body-dependent medical checks (medical
  products, medical return release) are enforced by the service. No route needs `service_api` or
  `advanced_reporting` yet.
- **`resolveExcursion` is `safety`;** its Release outcome is refused while restricted (state matrix).
  `completeTask` and `dispositionTransitQuantity` are `routine-write`; rework that frees stock does so
  through `unpick`, which is `safety`, so a restricted tenant can still clear held picks.
- Closure and ownership transfer requests carry a `reauthToken` (SOD-7, DEC-009); closure also needs the
  typed confirmation `CLOSE <slug>`.

## DEC-042 Control-plane schema design (validated draft)
- **Status:** Adopted; validated by `db/design/run-saas-smoke.ps1` (59 checks, PostgreSQL 16)
- **Backfills under forced RLS.** `ti_owner` has no RLS policy (no universal bypass, TEN-004), so a data
  migration's `UPDATE` on a tenant table silently affects zero rows. A migration that must backfill wraps
  it as `ALTER TABLE … NO FORCE ROW LEVEL SECURITY; <backfill>; ALTER TABLE … FORCE ROW LEVEL SECURITY;`
  inside its own transaction, so no other session ever sees the table unforced. Every future data
  migration on a tenant table follows this rule.
- **Tenant change in three migrations:** 0003 adds enum labels (they must commit before use); 0004
  expands (lifecycle, slug, home region, security suspension, with a two-way compatibility trigger
  between the old `status` and the security suspension so M1 code keeps working); 0005 contracts (drops
  `status`, `ti.tenant_status` and the v2.0 membership function). v2.0 `Suspended` tenants become
  security-suspended, not billing-restricted.
- **`cp.tenant_access()` takes no argument;** it reads `app.tenant_id`, so a request can only see its own
  tenant's state. The access mode itself is computed only in C# from the function's output
  (saas-state-matrix.md), giving one implementation to test. `ti.user_memberships_v2` adds slug,
  lifecycle and the suspension flag and excludes Deleted tenants.
- **Placement:** invitations, ownership transfers, usage counters and upload reservations live in `ti`
  with tenant RLS, because tenants change them in their own transactions (ownership acceptance changes
  role grants). The control plane only reads and updates invitations for acceptance. Everything billing
  lives in `cp`.
- **Roles:** `ti_control` has no DELETE anywhere and runs the platform billing console; there is no
  separate billing-operator database role. `cp_access_reader` (NOLOGIN) owns `cp.tenant_access()`.
  `ti_app` reads `billing_account` without `tax_reference` (column grant), and reads published public
  plan and price rows without a tenant context.
- **Additions:** `tenant.deleted_at`; `subscription.revision` (internal, monotonic) plus
  `provider_revision` on subscriptions and invoices for out-of-order provider events (BIL-005);
  `plan_version.is_public` for contract-only plans; one open checkout per tenant; one Pending/Scheduled
  change per subscription.
- **Simulator:** HMAC-SHA256 webhook signatures from `Billing:Simulator:SigningSecret`, 5-minute replay
  window, two secrets during rotation; `Billing:Provider` and `Billing:LiveCheckoutEnabled=false`; inbox
  processing gives up after 10 attempts and quarantines. Fixture plans in `docs/entitlement-catalog.json`
  use the ISO test currency XTS.

## DEC-043 Worker jobs follow the state matrix's background-work table (S1)
- **Status:** Adopted (supersedes the tenant selection in DEC-031)
- **Conflict:** DEC-031 (M1) runs outbox delivery for `Active` tenants only and idempotency cleanup and
  audit checkpoints for every tenant, selected by the v2.0 `ti.tenant.status` column. The v3.0 table
  "Background work by mode" in `docs/saas-state-matrix.md` runs all three in every mode except `Deleted`
  (checkpoints there: deletion evidence only). The M1 worker therefore skipped outbox delivery for
  security-suspended tenants and kept working on deleted tenants. It also read `status`, which the pending
  0005 contract migration drops, so every job would fail after M2 (SAS-005, AT-SAS-01).
- **Resolution:** the v3.0 matrix wins. `Domain.Saas.BackgroundWork` holds the table as code next to
  `AccessMatrix`, and a unit test walks every cell. `TenantJobRunner` reads `lifecycle` and the security
  suspension through `ti_platform` and asks the table whether a job runs; it never reads `status`.
- **Why lifecycle is enough:** no row in the table differs between Normal, BillingRestricted and
  SecuritySuspended, so the worker never needs billing state (`ti_platform` has no `cp` access, SAS-004).
  Closed and Deleted come from lifecycle alone (`TenantAccessState.Mode`). A future job whose row depends on
  billing state must read the control plane first.
- **Not weakened:** tenant isolation is unchanged (per-tenant `ti_app` transaction, `system:worker` with no
  permissions, TEN-004). Delivering already-committed events during a security suspension changes no tenant
  data. Deleted tenants' deletion-evidence checkpoint arrives with the deletion flow in S3; until then
  Deleted tenants get no background work at all.
- Background work is never entitlement-gated: a tenant whose plan lacks a feature still gets outbox
  delivery, cleanup and checkpoints (AT-SAS-01 runs two plans on one pooled connection).

## DEC-044 Signup and invitation abuse limits (S1)
- **Status:** Adopted for development; limit values proposed until the owner approves them (with D09/D10)
- **Context:** ONB-007 requires abuse limits on signup and invitation creation. The only limit was the global
  per-principal request limiter (600/minute), which would allow hundreds of workspaces per identity. A stricter
  request limiter cannot tell a retry from a new signup, and AT-ONB-01 retries one signup ten times.
- **Decision:** limits count *created records* in the database, so idempotent replays never count:
  new provisioning jobs per identity per rolling 24 hours (`Saas:Abuse:SignupsPerIdentityPerDay`, default 3) and
  new invitations per tenant per rolling hour (`Saas:Abuse:InvitationsPerTenantPerHour`, default 30). Revoked and
  expired invitations still count; creation is the abused action. Over the limit: 429 `RATE_LIMITED` with
  `Retry-After` set to the seconds until the oldest counted record leaves the window.
- **Concurrency:** each check takes a transaction-scoped advisory lock (`signup:<subject>` on the control pool,
  `invite:<tenant>` in the tenant transaction) before counting, so a parallel burst cannot pass the count
  together. Advisory locks are taken first in their transaction and touch no table, so the table lock order in
  docs/migration-design.md (STK-005) is unaffected.
- **Email enumeration:** invitation creation never looks up identities or other tenants; the only conflict it
  reports is a pending invitation in the caller's own tenant. Signup takes the email from the verified token,
  never from the body. Tests pin identical responses for registered and unregistered addresses.
- Per-address limits for anonymous traffic stay with the global limiter; signup always needs a verified identity.

## DEC-045 Warehouse and SKU quotas enforced in S1 (milestone deviation)
- **Status:** Adopted (owner instruction, 8 Nov 2026)
- **Conflict:** `createWarehouse` and `createProduct` declare `x-quota` (`active_warehouses`, `active_skus`) and
  are implemented, but their services never reserved it, so a tenant at its plan limit could keep creating.
  SAS-005 (S1) forbids any endpoint bypassing an applicable gate; resource usage counting is ENT-003, which
  the milestone table places in S2, and AGT-001 says not to build later-milestone features.
- **Resolution:** the control wins. Every *implemented* operation that declares `x-quota` reserves it in S1:
  create reserves before domain validation (SAS-005 order) and retire releases, both in the command
  transaction through the existing `IQuotaGate` (ENT-003 mechanics, unchanged). A conformance test fails when
  an implemented `x-quota` operation has no quota evidence, so a new endpoint cannot ship without it.
- **Still S2:** devices and attachment bytes (their endpoints do not exist yet), the daily counter
  reconciliation job and `EntitlementMismatch` alert, downgrades and upgrade flows (ENT-005, ENT-006).
- Retire releases after the domain transition and before the change is flushed, so a counter seeded lazily
  from active records counts the retiring record once and then releases it. The counter lock follows the
  document row (lock order, migration-design §11.5).

## DEC-046 Setup checklist in S1; sample data deferred to M2 (ONB-004)
- **Status:** Adopted (owner decisions, 8 Nov 2026)
- **Checklist:** `GET /saas/setup-checklist` (`getSetupChecklist`), `x-permission: member`, `x-access: read`, so it
  is available once the workspace is Ready and while billing-restricted, never during provisioning. Every item
  is computed from records on each read; nothing is stored and reading writes nothing:
  `business-profile` (billing account with legal name and country), `warehouse-timezone` (an active
  warehouse; time zone is mandatory), `zones-bins` (an active non-system zone holding an active non-Transit
  bin), `products-units` (an active unit and an active product), `team-invitations` (an invitation was sent,
  or a second active member exists), and `import` (optional, `Unavailable` until an import exists). The
  response carries `trialEnd` so the onboarding screen can show trial days; limits and usage stay in
  `getEntitlements`.
- **Sample data deferred to M2:** ONB-004 requires sample data to be isolated from production quantities, and
  quantities first exist in M2. Building it now would add a migration (colliding with the pending 0005/0006
  numbering) for master data only, and M2 would have to redesign it for stock. M2 adds opt-in, labelled,
  removable sample data whose quantities never mix with production ones. ONB-004 stays Partial until then.
- **UI deferred:** the web app is a scaffold; the checklist screen arrives with the first real screens, and
  browser evidence for ONB-004 is recorded as missing until then.
- No onboarding step creates stock (nothing in S1 can); M2 tests must keep that true.

## DEC-047 Purchase order workflow (M2, INB-001)
- **Status:** Adopted
- **Reference:** the contract allowed 100 characters, the column (`ti.code_text`) 64. The reference now uses the
  contract `Code` schema (1–50 characters, letters, digits and `. _ / -`), the same rule as other business codes,
  unique per tenant.
- **Draft edits replace lines.** `updatePurchaseOrder` replaces all lines while Draft, which needs DELETE on
  `ti.purchase_order_line`; 0006 granted none. Migration 0007 grants DELETE to `ti_app` and adds a trigger
  that allows inserting or deleting a line, or changing its ordered fields (product, unit, factor, quantities,
  line number), only while the order is Draft. `received_base_qty` stays updatable for receiving. Approved
  orders are therefore frozen in the database as well as in the service (AGT-003: the new privilege is
  narrower than the rule it serves).
- **States:** Draft → Approved (`purchase-orders.approve`). Draft, or Approved with nothing received → Cancelled
  (reason recorded in the audit event). Approved or PartReceived → ShortClosed (reason stored). Receiving moves
  Approved → PartReceived → Received (slice 3). Received, ShortClosed and Cancelled are final.
- **Lines:** each names an active product and either its base unit (factor 1) or a unit with an active
  conversion for that product; the factor is frozen on the line. The base quantity must respect the product's
  base precision, and serial-tracked products order whole units (STK-PRECISION). The supplier must be an
  active partner of type Supplier or Both; the warehouse must be active.
- **Scope:** `purchase-orders.*` and `stock.read` are warehouse-scoped. Writes need the permission for the
  order's warehouse; lists show only orders in warehouses the caller may read; an order outside the caller's
  scope answers 403, as for zones.
- **Not decided here:** whether the approver must differ from the creator. The spec asks only for manager
  approval; separation of duties for approvals is SOD-1 (M4 signing).

## DEC-048 Receiving (M2, INB-002 to INB-004, AT-INB-01/02)
- **Status:** Adopted
- **GS1 (INB-002):** a Domain parser reads the raw element string, either with FNC1 group separators (ASCII 29)
  or in bracketed human-readable form, and supports AIs 01 (GTIN, 14 digits), 17 (expiry, YYMMDD; day 00 is the
  last day of the month; GS1 century window), 10 (lot) and 21 (serial), both up to 20 characters. Values stay
  strings, so leading zeros survive. Any other AI, a bad check digit or a malformed string is
  `UNKNOWN_BARCODE`. The GTIN must map to exactly one active product barcode of the tenant.
- **Scan versus entered (INB-003):** when `scannedBarcode` is sent, the mapped product, lot, expiry and serial must
  equal what was entered; a scanned value fills an omitted one. Any disagreement is `BARCODE_CONFLICT` naming
  the field.
- **Medical evidence (INB-003):** medical lines need lot, expiry, supplier reference and, when the product's
  storage profile requires monitoring, the arrival temperature; otherwise `MISSING_MEDICAL_EVIDENCE`.
- **Initial quality (INB-004):** medical stock starts `Quarantine`; general stock `Available`, or `Hold` when
  inspection failed. An arrival that is expired (expiry before today in the warehouse time zone, via the injected
  clock) or outside the product profile's temperature range is accepted only with `exceptionReason`, and lands
  in `Quarantine` (medical) or `Hold` (general); it is never Available. A failed inspection also needs the reason
  (Hold requires one in the schema). Each line that lands in Quarantine writes one `StockQuarantined` audit event
  (AT-INB-01).
- **Order and lines (INB-001, AT-INB-02):** the order must be `Approved` or `PartReceived` and in the request's
  warehouse; each item names a line of that order and its product; the item's unit is the product base unit or an
  active conversion; base quantity respects product precision. If any line's received plus requested base
  quantity would exceed its ordered quantity, the whole request fails with `422 OVER_RECEIPT` and posts nothing
  (zero tolerance, D08). Lines become received, and the order `PartReceived` or `Received`.
- **Tracking:** lot-tracked products need a lot number; serial-tracked products need exactly one serial per base
  unit; quantity-tracked products take neither. **Serial lines:** a serial unit has its own bucket (quantity 0 or
  1, 0006) and a receipt line references one bucket, so a serial-tracked item is received in the product's base
  unit and posted as one receipt line per serial (each line's `serialIds` holds that one serial). A larger unit
  would make one serial a fraction of it. A lot is found or created per product and number; an existing lot
  with a different expiry is a `BARCODE_CONFLICT` on `expirationDate`. Any serial number already known for the
  product is `DUPLICATE_SERIAL`.
- **Target bin:** an active `Staging` or `Quarantine` bin of the warehouse whose zone is compatible with the product
  (DEC-012); otherwise `INCOMPATIBLE_BIN`.
- **Posting (ARC-003, STK-004):** one transaction locks the purchase order row and its lines, then upserts stock
  buckets in natural-key order (`INSERT … ON CONFLICT … DO UPDATE` adds to `on_hand_qty`; serial units get one
  bucket each), then writes receipt, lines, serial links, one `Receipt` movement per line (per serial for serial
  lines), the audit events, the outbox `GoodsReceived` event and the idempotency record. The audit stream stays the
  last lock (migration-design §5).
- **Never twice (STK-007, AT-STK-02):** `Idempotency-Key` is required. Besides the 7-day idempotency record, the
  command writes a permanent `document_operation` row `receive:<subject>:<key>`; a retry after the record has
  expired finds it and returns the original receipt instead of posting again.
- **Putaway tasks** are created by this same command from slice 5 (INB-005); until then `putawayRemainingQuantity`
  equals the line's base quantity and receipts stay `Posted`.

## DEC-049 Stock projection, eligibility, reconciliation and blocking (M2, STK-001, STK-004, OPS-001)
- **Status:** Adopted
- **Eligibility (STK-001):** a bucket is allocatable (`onHand − reserved`) only when every check passes; otherwise
  allocatable is 0 and `ineligibilityReasons` names each failure: quality not `Available` (`Quarantine`, `Hold`,
  `Recalled`, `Damaged`); lot expired before today in the warehouse time zone (`Expired`); bin zone incompatible
  with the product (`StorageIncompatible`, DEC-012); bin retired (`BinInactive`); Transit bin (`TransitLocation`);
  Staging or Quarantine bin, i.e. not yet put away (`AwaitingPutaway`, matching migration-design §5, which takes
  allocation candidates only from Storage bins); blocked under OPS-001 (`Blocked`). `StaleTelemetry`,
  `OpenExcursion` and `CountLocked` are computed once telemetry (M5) and counts (M4) exist.
- **Contract change:** `IneligibilityReason` gains `AwaitingPutaway` and `Blocked`; the contract had no value for
  either, although STK-001 requires "not blocked" and §5 requires Storage bins.
- **Reconciliation (STK-004):** a worker job (`BackgroundJob.LedgerReconciliation`, row "Ledger reconciliation …"
  of the background-work table: every mode except Deleted) recomputes each bucket's on hand from the ledger
  (destination quantities minus source quantities) and compares it with the stored projection. It never writes a
  balance. Reserved quantities are reconciled against reservations once reservations exist (M3).
- **Mismatch (OPS-001):** one High `ReconciliationMismatch` alert per tenant (dedupe `ledger-reconciliation`),
  whose details list each bucket with expected and stored quantities, and one row in the new append-only
  `ti.stock_bucket_block` (migration 0008) per mismatched bucket. A block holds while its alert is not Closed;
  closing the alert is the audited operator procedure, and the rows remain as evidence.
- **Blocking (OPS-001):** a stock command that would change a blocked bucket fails with `409 STOCK_BLOCKED` and
  posts nothing. An open `AuditChainFailure` blocks **every** stock mutation of the tenant: once the audit trail
  is broken, no stock change can be trusted to be recorded. **Needs owner:** this is deliberately strict.
- **Reads:** stock and movement lists are scoped to the warehouses the caller may read (`stock.read`); a movement's
  warehouse is its source or destination bucket's.

## DEC-050 Putaway (M2, INB-005, INB-006, AT-INB-03)
- **Status:** Adopted
- **Candidate bins (hard constraints):** active bins of the warehouse whose zone is compatible with the product
  (DEC-012). Available stock goes to `Storage` bins; Quarantine or Hold stock may go to `Storage` or `Quarantine`
  bins and keeps its quality (moving never releases it, INB-006). **Capacity:** a bin with a capacity accepts the
  quantity only if its current load plus the quantity, both in the bin's capacity unit (the product's base unit or
  an active conversion), fits. If any product already in the bin, or the product being moved, cannot be expressed
  in that unit, the bin is unsuitable (fail closed).
- **Ranking (INB-005):** bins already holding the same product and lot first, then lower pick priority, then bin code.
  Recommendations are computed when read and when confirming, so they reflect current stock.
- **Tasks:** receiving creates one `Putaway` task per receipt line (source = staging bin, quantity = base quantity,
  top recommendation stored). With no candidate it creates an `Exception` task for the same line and quantity
  instead, never an arbitrary destination, and raises one Warning `PutawayException` alert per warehouse.
  An exception task can still be confirmed later to a suitable bin, with a reason.
- **Confirmation (INB-006, AT-INB-03):** `Idempotency-Key` plus a permanent operation key
  `putaway:<subject>:<key>`; lock order task row → source and target buckets (by id) → audit stream. The scanned
  product (and lot, for lot-tracked products) must match the task (`BARCODE_CONFLICT`); the quantity cannot exceed
  what remains; an unsuitable target is `422 INCOMPATIBLE_BIN` and leaves task and stock unchanged; a target that is
  not the current top recommendation needs a reason; blocked buckets refuse (OPS-001). On hand moves from the
  staging bucket to the target bucket with the same product, lot, serial and quality (keeping the earliest
  `received_at`), with one `Putaway` movement carrying the reason. The task completes at zero remaining; a
  receipt becomes `Closed` once all its tasks are complete (DEC-005).
- **Migration 0009:** `warehouse_task` gains `completed_by` and `resolution`, which the contract's WarehouseTask
  exposes and 0006 lacked.

## DEC-051 Outbound schema (M3, migration 0010)
- **Status:** Adopted
- **Source:** the M0-validated draft (`db/design/schema-draft.sql`): `outbound_order`, `outbound_order_line`,
  `reservation` (with parent segments for partial picks), `pick`, `pick_confirmation`, `shipment`, `shipment_line`.
  The movement → reservation foreign key deferred by 0006 is added here.
- **Draft lines:** `updateOutboundOrder` replaces all lines while Draft, as for purchase orders (DEC-047): `ti_app`
  gets DELETE on `outbound_order_line`, guarded by a trigger that allows line inserts, deletes and changes to
  ordered fields (product, requested quantity, shelf life, line number) only while the order is Draft. Allocation,
  pick and dispatch counters stay updatable in any status.
- **Append-only:** `pick_confirmation` and `shipment` (immutable records, OUT-004, ShipmentStatus has one value).
  `shipment_line` stays updatable only for `returned_qty` (EXC-005, M4).
- **Grants:** `ti_app` SELECT/INSERT/UPDATE on the mutable tables, SELECT/INSERT on append-only ones, no DELETE
  except draft order lines; nothing for `ti_control` or `ti_platform` (SAS-004).
- **Additions to the draft (stricter only, see DEC-052 for the workflow):** an order is `Cancelled` exactly when it has a cancel reason;
  `confirmed_at` and `confirmed_by` are set together; `pick_confirmation.movement_id` and `shipment_line.movement_id`
  reference the ledger entry they record.

## DEC-052 Outbound order workflow (M3, OUT-001, OUT-006 cancellation)
- **Status:** Adopted
- **Reference:** as for purchase orders (DEC-047), the contract's 100 characters did not fit the 64-character column;
  the order reference now uses the contract `Code` schema (1–50 characters), unique per tenant.
- **Lines (OUT-001):** quantities are in the product's base unit (the contract line has no unit), positive, within the
  product's base precision and whole for serial-tracked products; products must be active; minimum shelf life is
  0–3650 days. The customer must be an active partner of type Customer or Both; the warehouse must be active; the
  planned dispatch date cannot be before today in the warehouse's time zone. Confirmation rechecks all of these.
- **Status (section 13):** Draft and Cancelled are stored decisions; every other status is derived from line
  quantities after each change: any dispatched → PartDispatched, all dispatched → Dispatched; else all picked →
  Picked, any picked → PartPicked; else all allocated → Allocated, any allocated → PartAllocated; else Confirmed.
  Closed is reserved for the post-dispatch window (returns, M4).
- **Draft edits** replace all lines (0010 trigger, DEC-051); confirmed orders cannot change their lines.
- **Cancellation (OUT-006):** a safety operation (`x-access: safety`) with `Idempotency-Key`, If-Match and a reason.
  It refuses with 409 `INVALID_STATE` while any quantity is picked or dispatched (picked stock must be unpicked
  first). Otherwise it releases every Active reservation of the order in one transaction, with lock order order row →
  lines → reservations (by id) → buckets (by id) → audit stream: bucket `reserved_qty` decreases, the reservation
  becomes `Released`, the line's allocated quantity decreases. Then the order is `Cancelled` with its reason.
- **Blocks:** releasing a reservation changes only `reserved_qty`, never on hand, so it cannot hide stock; cancellation
  therefore releases even on buckets blocked under OPS-001 (DEC-049), which would otherwise trap the commitment.
  Commands that change on hand (pick, dispatch) stay blocked.

## DEC-053 Allocation (M3, OUT-002, STK-005, STK-006, STK-008, AT-OUT-01, AT-STK-01)
- **Status:** Adopted
- **What is allocated:** the unreserved remainder of every line (requested − allocated) of an order in a confirmed status;
  never a Draft, Cancelled, Dispatched or Closed order (STK-006).
- **Rotation:** the product's rotation rule (FEFO, FIFO, LIFO); medical products always FEFO; `/orders/allocate-fefo`
  forces FEFO for every line (DEC-006).
- **Eligible buckets (OUT-002, migration-design §5):** the order's warehouse and product; quality `Available`; an
  active `Storage` bin in a zone compatible with the product (DEC-012, compatible zones computed first and passed to
  the query); not blocked (OPS-001); something unreserved. With an expiry: `expiry_date > effective dispatch date`
  and `expiry_date − effective dispatch date ≥ min_remaining_shelf_life_days`, where the effective dispatch date is
  the later of the planned date and today in the warehouse's time zone. Buckets without an expiry have no shelf-life
  limit.
- **Locking (STK-005):** order row → its lines (`line_number`) → for each line its candidate buckets `FOR UPDATE` in
  rule order (FEFO `expiry, received_at, lot_id, id`; FIFO `received_at, id`; LIFO `received_at DESC, id`), then
  revalidate `on_hand − reserved` on the locked rows → audit stream. Deadlocks and serialization failures retry at most
  three times with jitter, then `409 RETRYABLE_CONFLICT` (existing unit of work).
- **All-or-nothing (default) versus partial (STK-006):** any shortage in all-or-nothing mode fails with
  `409 INSUFFICIENT_STOCK`, listing the shortages, and creates nothing (AT-OUT-01). Partial mode commits what is
  available and returns exact shortages.
- **Reservations (STK-008):** `Active` with `expires_at = now + 24 h`; the bucket's `reserved_qty` and the line's
  allocated quantity grow in the same transaction; the order's status is recalculated.
- **Blocks:** an open audit-chain failure refuses allocation (`STOCK_BLOCKED`, DEC-049); blocked buckets are simply
  not candidates.
- **Deallocation:** a safety operation that releases Active reservations of the named lines (or all) with a reason;
  picked stock needs unpick first. Same release path as cancellation (DEC-052).
- **Pick tasks:** allocation creates one pick task per new Active reservation (OUT-003, DEC-054); releasing a
  reservation cancels its open pick.
- **Reconciliation (completes DEC-049):** each bucket's `reserved_qty` must equal the sum of its `Active` and `Picked`
  reservations; a difference is a mismatch like an on-hand one. Migration 0011 adds `expected_reserved_qty` and
  `stored_reserved_qty` to `stock_bucket_block` and requires on hand **or** reserved to differ.

## DEC-054 Picking and unpick (M3, OUT-003, OUT-004, section 13)
- **Status:** Adopted
- **Tasks (OUT-003):** allocation creates one pick per new Active reservation (the pick's *own* segment), showing the
  exact bin, product, lot, expiry and quantity. Invariant: `task_qty = picked_qty + qty of the own segment while it is
  Active`; `remaining = task_qty − picked_qty`. The contract's pick `state` is the own segment's state.
- **At most one picked segment per pick.** A partial pick splits a `Picked` child off the own segment (parent = own
  segment) on the staging bucket; later partial picks grow that child; picking everything that remains moves the own
  segment itself to staging as `Picked` and folds the child into it (the child ends `Released`, quantity kept as
  history). All confirmations of one pick use the same staging bin. This keeps unpick to one segment and one movement,
  as the contract's `UnpickResult` requires.
- **Confirmation (OUT-004):** `Idempotency-Key`, If-Match on the pick and a permanent operation key
  `pick:<subject>:<key>`. The scanned bin, product and lot (when the bucket has one) must match the task: 422
  `VALIDATION_FAILED` naming the field, nothing changed. Another lot is never substituted, medical or not. Serial-tracked
  picks list exactly the serial of the reserved bucket. The quantity is positive, within the product's precision and no
  more than remains. The staging bin is an active `Staging` bin of the warehouse compatible with the product (DEC-012).
  The own segment must be Active and unexpired, the source bucket still `Available` and its lot not expired in the
  warehouse time zone, and neither bucket blocked (`409 STOCK_BLOCKED`).
- **Stock and reservation move together:** lock order order row → order lines → pick → reservations → source and
  staging buckets (by id) → audit, so every pick command on an order is serialized with allocation and release. Source
  `on_hand` and `reserved` both drop by the quantity; the staging bucket (same product, lot, serial and quality) gains
  both, so warehouse stock never changes at pick time. One `Pick` movement names the picked segment; a
  `pick_confirmation` row records the scans. The line's picked quantity grows and the order status is recalculated.
- **Unpick (safety, section 13):** quantity, an active `Storage` target bin compatible with the product, and a reason.
  The quantity comes back into the own segment so the pick can be redone: while the own segment is still Active (part
  picked) the target must be its bin and the quantity joins it (the child shrinks, or ends `Released` when emptied);
  when the own segment is Picked it becomes Active again on the target bucket with the returned quantity (24 h expiry)
  and any rest stays on staging as a new Picked child. The pick's source bin follows the own segment. One `Unpick`
  movement naming the own segment; the line's picked quantity drops.
- **Release of a part-picked pick:** deallocation or cancellation releases only the Active remainder; the picked child
  folds into the own segment (Picked, on staging) and the task closes as `Completed` with `task_qty = picked_qty`. A pick
  with nothing picked is `Cancelled`. Reservation expiry (slice 5) follows the same rule.

## DEC-055 Dispatch, post-pick blocks, rework and reservation expiry (M3, OUT-005, OUT-006, STK-008)
- **Status:** Adopted
- **Dispatch (OUT-005):** `POST /orders/{id}/dispatch` with `Idempotency-Key`, If-Match on the order and a permanent
  operation key `dispatch:<subject>:<key>`. Each named pick (of this order) ships its staged segment whole: the own
  segment once Picked, otherwise its picked child. By default every line must be fully picked and every staged segment
  of the order named (409 `INVALID_STATE` otherwise); `allowPartial: true` ships the named picks and leaves the order
  `PartDispatched`. A reference already used on the order is refused (409 `INVALID_STATE`), backing the unique key.
- **Rechecks in the deduction transaction:** lock order row → lines → picks → segments → staging buckets (by id) →
  audit. Each segment must still be `Picked` on a line of this order (reservation ownership), its bucket `Available`,
  not blocked by an open reconciliation alert, its lot not expired, and no audit-chain failure open. Expiry blocks on
  the expiry day itself in the warehouse time zone (`expiry_date > today`), matching allocation and the AT-MED-01 rule;
  picking now uses the same rule. Recall, excursion and telemetry-staleness holds arrive with M5 and plug into the same
  recheck.
- **Effect (STK-002, AT-STK-03):** one shipment, one shipment line and one `Dispatch` movement per segment (document
  Shipment, line = shipment line, reservation = the segment). The staging bucket loses on hand and reserved together;
  the segment becomes `Consumed`; the line's dispatched quantity grows and the order status is recalculated. Shipments
  are append-only, so the API reports `version` 1 and `updatedAt = createdAt`.
- **Post-pick block (OUT-006, AT-OUT-02 hold variant):** when any named segment fails a recheck the dispatch posts
  nothing and returns 409 `STOCK_BLOCKED` listing `affectedPicks` (pick and cause) and `reworkTaskIds`. The rework
  tasks (`warehouse_task` type `Rework`, related to the pick, at the staging bin) are written in a **separate**
  transaction after the dispatch rolls back, one open task per pick; the rejection itself is never stored as an
  idempotent answer. An unpick of the pick works the task down; a later successful dispatch of the pick completes it.
- **Contract alignment:** `shipment.dispatch_reference` becomes `identifier_text` (1–100, trimmed) in migration 0012
  because the contract allows 100 characters and `code_text` stopped at 64.
- **Reservation expiry worker (STK-008):** a scheduled job (`ReservationExpiry`, all modes except Deleted per the state
  matrix) finds orders with Active reservations past `expires_at`, then per order, each in its own transaction so one
  order that cannot be released (for example a corrupted bucket awaiting reconciliation) is logged and retried without
  stopping other orders or tenants, takes the same locks as release
  (order row → lines → picks → reservations → buckets), re-reads, and expires only still-Active, still-due segments:
  reserved returns to the bucket, the segment ends `Expired`, the line's allocation drops, the pick closes its
  remainder. A part-picked task keeps its picked part as with deallocation (DEC-054). Picked segments never expire,
  and dispatch consumes only Picked segments while expiry touches only Active ones, both under the order lock, so the
  two can never consume the same commitment.

## DEC-056 General-operations schema (M4, migration 0013)
- **Status:** Adopted
- **Context:** the M0 schema draft predates the M4 contract shapes. Contract first (DEC-003): the tables follow
  `docs/openapi.yaml`; the draft's controls are kept.
- **Approvals (IAM-003, IAM-004, SOD-1):** as drafted, plus `reauth_verified_at` (the contract's `reauthVerifiedAt`)
  and `invalidated_reason` (required exactly when `Invalidated`). A trigger freezes the binding (subject, version,
  digest, action, requester, reason) and the signature once written, and allows only Pending → Approved, Rejected,
  Invalidated or Expired, and Approved → Consumed, Invalidated or Expired. The requester can never sign (CHECK), one
  approval allows one posting (unique `consumed_operation`), and it lasts 30 minutes from signing (CHECK).
- **Adjustments (EXC-004):** as drafted, with `posted_movement_id` now a foreign key to the ledger. A Posted adjustment
  is frozen by trigger. Evidence files are `attachment` rows bound to the adjustment; the contract's `contentDigest` is
  computed from the canonical request, not stored.
- **Transfers (EXC-001, STK-003):** lines name a product and optional lot (the contract's `TransferLineInput`), not a
  source bucket; dispatch picks the buckets and the movements carry them. `dispatched ≤ requested` and
  `received + disposed ≤ dispatched` as drafted; `approved_at` joins `approved_by`.
- **Counts (EXC-003):** no reference (the contract has none); `reason`, `snapshot_sequence`, `locked_at` and
  `submitted_at` added; `stock_count_bin.lock_active` keeps at most one active lock per bin. The DB backstop that
  refuses movements in locked bins comes with the count slice.
- **Returns (EXC-005):** `returned_qty`, `quarantine_bin_id`, the quarantine `bucket_id`, `reason`, at most one
  serial (a shipment line holds one serial, DEC-055) and the `ReturnReceipt` movement. Inspection may be split:
  `customer_return_inspection` is append-only (disposition, quantity, movement, approval) and the return keeps
  `inspected_qty ≤ returned_qty`, the latest `disposition`, and status `Inspected` exactly when all is inspected.
  The returned quantity is also counted on `shipment_line.returned_qty`, whose CHECK caps returns at the shipped
  quantity in the database (AT-EXC-03).
- **Exports and attachments (AUD-004, TEN-005):** object keys must start with the tenant id. Export jobs keep
  `filter`/`filter_digest`, the audit stream range, `manifest`, `failure_reason`, `completed_at` and `retain_until`
  (the 15-minute limit applies to download links, not the job). Attachments add `file_name`, allow only PDF, JPEG and
  PNG up to 10 MiB (DEC-022), and bind to an entity optionally (uploaded before the adjustment exists).

## DEC-057 Approvals and adjustments (M4, IAM-003, IAM-004, SOD-1, SOD-2, SOD-4, EXC-004)
- **Status:** Adopted
- **Approval subjects:** this slice supports `Adjustment` subjects with action `Post`; count variances and transit
  dispositions join in their slices, quality subjects in M5 (other types return 422 until then). A subject has at most
  one live (Pending or unexpired Approved) approval; requesting a new one is refused with 409 `INVALID_STATE`.
- **Binding (IAM-003):** the request names the subject version, which must be current (409 `APPROVAL_STALE`); the
  server stores the SHA-256 of the subject's canonical JSON (RFC 8785, the audit canonicaliser). For an adjustment
  that is id, warehouse, bucket, bin, product, lot, serial, quality, delta, reason code, narrative and the sorted
  evidence attachment ids. Editing or cancelling the adjustment changes its version and invalidates its live approval
  (reason recorded).
- **Signing (IAM-004, SOD-1, SOD-2, DEC-009):** If-Match on the approval, a fresh re-auth token (the S1 verifier:
  issuer, audience, `sub` = caller, `auth_time` ≤ 300 s, MFA; only its digest is stored with the verification time),
  meaning `Approval` and a reason. The signer must differ from the requester and from the adjustment's creator and
  last modifier (409 `APPROVAL_SELF`); migration 0014 adds `adjustment.last_modified_by` for this. An adjustment needs
  `approvals.sign.inventory`; a medical product's also needs `approvals.sign.quality` (SOD-2). A subject that moved on
  since the request gives 409 `APPROVAL_STALE`. The signature is valid 30 minutes. Signing never writes the adjustment:
  that would change its version and void the binding. The stored adjustment status moves Requested → Posted, Rejected
  or Cancelled; the API reports `Approved` (with `approvalId`) while a signed, unexpired approval binds its current
  version and digest.
- **Rejection:** the same permission and SOD-1 rules with a reason and no re-auth (DEC-056); the adjustment becomes
  `Rejected` (final; edit nothing, request a new adjustment).
- **Approval inbox:** visible to holders of `approvals.request` **or** either sign permission, as the contract's
  description says; the operations' `x-permission` is widened to that alternative form (contract change).
  `awaitingMySignature` lists Pending approvals the caller could sign. An Approved approval past `expires_at` reads
  as `Expired`; it is persisted as Expired when the next approval for the subject is requested.
- **Adjustments (EXC-004):** request with bin (not Transit), product, optional lot/serial/bucket and quality (medical
  increases default to `Hold`, DEC-022), a signed delta within precision (serial-tracked: ±1), reason code,
  narrative and up to 20 evidence attachments (bound to the adjustment; already bound elsewhere is refused). A
  reduction names an existing bucket and may not take on hand below reserved (409 `INSUFFICIENT_STOCK`), checked at
  request, edit and post.
- **Posting:** `Idempotency-Key`, If-Match on the adjustment, the approval id, and a permanent operation key
  `adjustment:<subject>:<key>`. The approval must be Approved, unexpired and bind the current version and digest,
  and its approver must still be an active member holding the sign permissions (SOD-4); otherwise 409
  `APPROVAL_STALE`. In one transaction: lock the bucket, recheck reserved, apply blocks (OPS-001), post one
  `AdjustmentIncrease` or `AdjustmentDecrease` movement (an increase creates the bucket if needed), mark the
  adjustment `Posted` and the approval `Consumed` with operation `adjustment:<id>:post` (unique, so it can never
  post twice).

## DEC-058 Bin transfers and warehouse transfers (M4, EXC-001, EXC-002, STK-003, AT-EXC-01)
- **Status:** Adopted
- **Bin transfer (EXC-002):** `POST /inventory/bin-transfers` (`putaway.confirm`), `Idempotency-Key` and a permanent
  operation key `bin-transfer:<subject>:<key>`. Moves unreserved stock from a bucket to another active bin of the same
  warehouse in one transaction: the target is not Transit, suits the quality (Available → Storage or Staging; others →
  Storage or Quarantine, as for putaway, DEC-050) and the product (DEC-012). Lot, serial and quality are preserved;
  serial-tracked moves name the bucket's serial. Buckets lock in id order; blocked buckets refuse (OPS-001). One
  `BinTransfer` movement (document `BinTransfer`, id = the operation).
- **Warehouse transfer lifecycle (EXC-001):** create (lines: product, optional lot of that product, quantity; distinct
  product/lot pairs; source ≠ destination; reference unique per tenant) → approve (If-Match) → dispatch → receive.
  Dispatch and receipt may each happen in parts. Draft, Approved and Cancelled are stored decisions; afterwards the
  status follows the lines: anything dispatched → `InTransit`; anything received or disposed → `PartReceived`; every
  line fully dispatched with `received + disposed = dispatched` → `Received` (`received_at` set). Cancellation is only
  possible before anything is dispatched. `transfers.manage` must cover the source warehouse for create, approve,
  dispatch, cancel and disposition, and the destination for receipt. Migration 0015 adds `transfer.last_modified_by`.
- **Dispatch (STK-003, DEC-010):** `Idempotency-Key`, If-Match on the transfer, permanent key `transfer-dispatch:<subject>:<key>`.
  Each line names a source bucket of the source warehouse with the line's product (and lot, when the line names one),
  not in a Transit bin, and a quantity no more than the line's remainder or the bucket's unreserved stock. Stock moves to
  the bucket of the source warehouse's Transit bin with the same product, lot, serial and quality (`TransferDispatch`
  movement, document Transfer, line = the transfer line). Tenant stock is unchanged.
- **Receipt (AT-EXC-01):** permanent key `transfer-receive:<subject>:<key>`. Each line takes up to its in-transit quantity
  into an active Staging bin of the destination warehouse that suits the product. The line's in-transit stock is read
  from the ledger (its dispatches minus receipts and dispositions, by transit bucket) and drawn oldest bucket first;
  serial-tracked lines name the serials. `TransferReceipt` movements keep lot, serial and quality. Shortages stay in
  transit. Received stock is then placed with a bin transfer.
- **Transit disposition (STK-003, IAM-003):** an approval of subject type `TransitDisposition`, subject = the transfer,
  action `Dispose`, binding the transfer version and digest (id, warehouses, reference, status, lines with all
  counters). The approval request cannot carry the disposition itself, so the approval authorises writing off in-transit
  quantity of the transfer exactly as it stood when signed: any later dispatch or receipt changes the version and makes
  it stale. Signers need `approvals.sign.inventory` (plus `approvals.sign.quality` when a line is a medical product) and
  must differ from the requester, the transfer's creator and its last modifier (SOD-1). Posting the disposition
  (`Idempotency-Key`, If-Match, lineId, quantity ≤ in transit, Lost or Damaged, reason, approvalId) posts
  `TransitDisposition` movements out of the line's transit buckets, raises `disposed_qty` and consumes the approval with
  operation `transfer:<id>:disposition:<approval>` (unique). Every dispatch, receipt, disposition, approval or
  cancellation of the transfer invalidates its other live disposition approvals, as an edit does for an adjustment. The approval code is generalised: a subject registry supplies version, digest,
  authors and sign permissions per subject type.

## DEC-059 Stock counts and the count lock (M4, EXC-003, AT-EXC-04, DEC-008)
- **Status:** Adopted
- **Lifecycle (section 13):** create (warehouse, 1–500 distinct non-Transit bins of it, reason; `counts.perform`) →
  start (If-Match; locks the bins, refused with 409 `COUNT_LOCKED` when another count holds one; snapshots the
  expected on hand per bin, product, lot and serial across all quality statuses (DEC-008) as revision 1, and records
  the tenant audit sequence as `snapshotSequence`) → submit (`counts.perform`) → approve or reject (`counts.manage`)
  → post (`counts.manage`). Reject returns the count to Counting as revision n+1 (expected copied, observations
  empty; earlier revisions kept). Cancel (`counts.manage`, reason) unlocks the bins from any state before posting and
  keeps every observation. Migration 0016 adds `stock_count.last_modified_by`.
- **Blind count (EXC-003):** while the count is not yet Submitted, expected quantities and variances read as `null`
  for callers without `counts.manage`. A submission reports bin, product, optional lot and serial numbers, and the
  observed quantity. Lines it does not mention count as observed zero (the counter counted the whole bin). A lot or
  serial number the tenant does not know is refused with 422 rather than recorded, because a lot needs its expiry
  (deviation from DEC-022's "counters can record unknown lots"; such finds go through an adjustment with evidence).
- **Independent approval (IAM-003, SOD-1):** an approval of subject type `CountVariance` (action `Post`) binds the
  count version and the digest of the current revision's lines (bin, product, lot, serial, expected, observed). The
  signer needs `approvals.sign.inventory` (plus quality for medical products) and must differ from the requester, the
  count's creator and last modifier, and every counter of the revision. `approve` checks the binding and records the
  approval. Approving changes the count's version, so `post` rechecks the approval is signed, unexpired, still entitled
  (SOD-4) and that the lines' digest is unchanged (bins are locked, so the lines cannot move in between), then
  consumes it with operation `count:<id>:post`.
- **Posting (EXC-004):** in one transaction the bins are unlocked first, then each variance posts as an adjustment
  record (count, approval, Posted) with one `AdjustmentIncrease`/`AdjustmentDecrease` movement per bucket touched.
  A shortage reduces unreserved stock in the order Available, Hold, Quarantine, other statuses; a shortage larger than
  the unreserved stock is refused with 409 `INSUFFICIENT_STOCK` (resolve reservations first). A surplus lands in the
  Available bucket, or Hold for medical products (DEC-022). The count ends `Posted`, or `Closed` when there was no
  variance. `Idempotency-Key` plus a permanent operation key `count-post:<subject>:<key>`.
- **Count lock (DEC-008):** a trigger on `stock_movement` refuses any movement into or out of a bin with an active
  count lock (SQLSTATE `TI001`, mapped to 409 `COUNT_LOCKED`), so putaway, pick, unpick, dispatch, bin and warehouse
  transfers, adjustments and receipts are all covered in one place. Quality changes to Hold or Recalled stay allowed
  (protective, M5). Reservations are not movements and are not blocked.

## DEC-060 Customer returns (M4, EXC-005, AT-EXC-03)
- **Status:** Adopted
- **Recording a return (`returns.manage`):** `Idempotency-Key` and a permanent operation key `return:<subject>:<key>`.
  It names a shipment line of the tenant and a quantity no more than shipped less already returned (422
  `RETURN_EXCEEDS_SHIPPED`, AT-EXC-03; the `shipment_line.returned_qty` CHECK backs it in the database). The shipment
  line row is locked, so concurrent returns cannot together exceed it. The goods enter an active Quarantine bin of the
  shipment's warehouse that suits the product (DEC-012), always as `Quarantine` quality, general goods included
  (EXC-005), with the shipped lot and serial (a serial-tracked return names that serial). One `ReturnReceipt`
  movement (document Return).
- **Inspection (`returns.inspect`):** `Idempotency-Key`, If-Match on the return, permanent key
  `return-inspect:<subject>:<key>`. Inspections may split the returned quantity; each records an append-only
  inspection row and moves the stock:
  - `Release`: to an active Storage bin of the warehouse that suits the product, as `Available` (`QualityChange`
    movement from the quarantine bucket). Releasing **medical** returns needs QA quality release with an approval
    (`ReturnRelease`, `quality.release`), which arrives with M5; until then it is refused with 422.
  - `Damaged`: stays in the quarantine bin as `Damaged` quality (`QualityChange`), never allocatable.
  - `SupplierReturn` / `Disposal`: leaves stock (`SupplierReturn` / `Disposal` movement, with the reason).
  The return becomes `Inspected` once everything is inspected; `disposition` shows the latest decision. `Closed` is
  not used in M4. A count lock on either bin refuses the movement (DEC-059) except protective quality changes.

## DEC-061 Attachments, exports and download links (M4, TEN-005, AUD-004, NFR-EXPORTS)
- **Status:** Adopted (export retention period: owner decision D05)
- **Object storage (simulated, AGT-004):** an `IObjectStore` writes tenant-prefixed keys (`{tenant}/attachments/{id}`,
  `{tenant}/exports/{id}.jsonl`) to a local directory (`Storage:Directory`), visibly labelled as a simulator; the
  production store is D06. API and worker share it.
- **Download links (TEN-005, section 18):** a link is `…/download?token=…` where the token is
  HMAC-SHA256(`Storage:LinkKey`) over tenant, resource kind, resource id, the requesting subject and an expiry at most
  15 minutes ahead. The download routes are ordinary tenant routes, so the bearer token, membership, permission and
  access mode are re-checked at download time; the service then requires the token to match the tenant header, the
  caller, the resource and an unexpired time (404 otherwise: no hint whether the resource exists). Contract change:
  `GET /attachments/{id}/download` (`stock.read`, read) and `GET /exports/{id}/download` (`exports.create`, export)
  are added; the existing `download-link` operations issue the links.
- **Attachments:** multipart upload (`adjustments.request`, quota `attachment_bytes`). The server checks the size
  (1 B–10 MiB) and the content by its leading bytes (PDF, JPEG or PNG; the declared type must agree), stores the
  SHA-256, and counts the bytes against the quota in the insert transaction (the body is fully received first, so no
  separate reservation is needed; an object written before a rolled-back insert is an orphan for the S3 clean-up
  job). Scanning is a visible simulator run at upload: content containing the EICAR test string is `Rejected`,
  anything else `Clean`. Links are issued only for `Clean` files (409 `INVALID_STATE` otherwise). An optional
  entity binding must name an open adjustment.
- **Exports (AUD-004, NFR-EXPORTS):** `POST /exports` (`exports.create`, access `export`) validates the filters
  (time range, warehouse, product, lot), stores them with their SHA-256, and returns 202 with a `Queued` job. The
  worker's `ExportGeneration` job (all modes except Deleted, a new row in the state matrix's background-work table)
  writes JSON Lines: `Audit` (audit events in the range, `streamRange` = the first and last sequence exported),
  `Movements` (ledger movements in the range and filters) and `StockSnapshot` (current buckets matching the
  filters); for the latter two `streamRange` is the audit position the export is consistent with. The manifest holds
  the file digest, the file count and `signatureReference` = `hmac-sha256:<checkpoint key id>:<signature>` over the
  digest, filter digest and stream range, signed with the audit checkpoint key (AUD-003). `Traceability` arrives
  with M5 and `TenantArchive` with S3 (both 422 until then). Completed files are kept for 30 days (`retainUntil`),
  a default the owner must confirm with D05. Creating, generating and downloading an export are audited.

## DEC-062 S2 commercial control: plan, architecture, catalog and schema (S2, ENT-001, BIL-001, BIL-006)
- **Status:** Adopted (provider, prices and currency remain owner decision D09; all S2 billing runs against the simulator)
- **Slices:** (1) catalog, money, subscription and invoice reads, migration 0017; (2) provider adapter, labelled
  simulator, checkout preview/consent, hosted checkout, billing portal; (3) signed webhook inbox and processor;
  (4) plan changes, cancel and resume; (5) reconciliation, outage behaviour, platform billing consoles, quota races;
  (6) gate flow and handoff docs.
- **Architecture:** tenant-facing billing *reads* run in the tenant unit of work (`ti_app`, the `tenant_read` RLS
  policies from 0004), so they can only see the caller's tenant. Billing *writes* run in a control-plane billing
  store on the `ti_control` pool (DEC-035) with the tenant id taken from the validated caller or from our own
  provider references, never from a payload; each command is one transaction that also appends the audit events to
  the tenant's own chain (AUD-001, ARC-003). `ti_control` still has no stock privilege (SAS-004): anything that needs
  stock facts (downgrade blockers, slice 4) is computed in a tenant transaction first and rechecked when applied.
  Platform billing routes require `Platform:BillingSubjects` (PlatformBilling, SOD-8); `listPlanVersions` admits
  either platform list.
- **Money (BIL-001):** `Money(currency, minorUnits, exponent)`: ISO 4217 code, `long` minor units, exponent 0–4, never
  floating point; amounts are copied into checkout, invoice and change rows and never recomputed from the catalog.
- **Catalog (ENT-001):** `createPlanVersion` adds a `Draft` with the next version number of its plan code; features and
  limits must use the catalog keys, and every quota key needs a non-negative limit. `createPriceVersion` adds an
  immutable price to a **draft** plan version only (new terms on a published plan need a new plan version);
  `publishPlanVersion` needs at least one price and freezes the plan (0004's trigger). Drafts carry a `revision`
  (ETag, If-Match on price and publish). Catalog changes belong to no tenant, so each one is recorded in the
  append-only `cp.catalog_event` (actor, action, reason, request id) instead of a tenant chain.
  Status is derived: `Draft` (unpublished), `Published`, or `Retired` (published with no price
  active now or later). **No live commerce (DEC-040):** while `Billing:Provider` is `simulator`, any currency other
  than the test currency `XTS` is refused (422), and `display_approved` stays false because no route sets it (prices
  shown publicly need owner approval, D09). `trialDays` is stored on the plan (default 14); provisioning keeps using
  the configured trial length until D09 decides per-plan trials.
- **Reads:** `getSubscription` returns state, periods, grace, pending cancellation, the open scheduled change, access
  mode, limits and usage; its version and ETag are the subscription revision. `listInvoices` / `getInvoice` return
  the snapshot lines and totals as `Money` and the latest payment status (BIL-006).
- **Migration 0017:** `cp.billing_task` (operator tasks for reconciliation, unknown references, disputes, refunds and
  chargebacks; `dedupe_key` unique so a replayed cause opens one task; control plane only); `cp.billing_sync_state`
  (reconciliation cursor per provider); `cp.webhook_inbox.failure_code`; `cp.invoice.issued_at`;
  `cp.plan_version.trial_days` and `revision`; `cp.catalog_event`; `cp.price_version.display_approved`. `ti_control` keeps **no** privilege on `ti.outbox_event`
  (SchemaConformanceTests, SAS-004); how billing notices reach tenants is decided in slice 3 without widening it.

## DEC-063 Provider adapter, simulator, checkout and billing portal (S2, BIL-002, BIL-003, AT-BIL-01)
- **Status:** Adopted (the real provider remains D09; nothing here can charge)
- **Adapter:** `IBillingProvider` (Application) with `EnsureCustomer`, `EnsurePrice`, `CreateCheckoutSession` and
  `CreatePortalSession` in this slice; webhook verification and authoritative fetches arrive in slices 3 and 5. Every
  mutating call carries the idempotency key from `docs/billing-adapter.md` (`customer:{billingAccountId}`,
  `price:{priceVersionId}`, `checkout:{checkoutSessionId}`, `portal:{billingAccountId}:{minute}`), so a retry never creates
  a second provider object. A provider failure is `503 SERVICE_UNAVAILABLE`; nothing in our records changes on failure.
- **Simulator (AGT-004):** `SimulatedBillingProvider`, labelled `[SIMULATED BILLING]`, is the only implementation and
  holds its state in memory per process. Hosted pages are `https://billing-simulator.invalid/...` URLs that nobody can open:
  outcomes (pay, fail, abandon) are driven through the simulator's test console, which tests call directly; a browsable
  console is UI-milestone work. Prices without a provider reference are registered on first checkout as
  `sim_price_{priceVersionId}`. 0017 replaces 0004's price guard so that a referenced price may be linked to the
  provider once (provider and reference set from NULL); every term (amount, currency, exponent, interval, plan, start)
  and any existing reference stay immutable.
- **Who and when (BIL-003):** `billing.manage` (owner or billing administrator). A checkout starts a paid subscription
  for a tenant that is `Trialing` (before or after trial end) or `Cancelled`; an `Active` or `PastDue` subscription
  changes plan through slice 4 instead (409 `INVALID_STATE`). The price must be active now and belong to a published
  public plan or the tenant's contracted plan.
- **Preview and consent:** `previewCheckout` states plan, amount, interval, renewal wording, the cancellation policy
  (effective at paid period end, no refund or proration, BIL-008) and an indicative `startsAt` (the period starts when
  the provider confirms payment). `previewDigest` = SHA-256 over the RFC 8785 canonical JSON of tenant, price version,
  plan version, currency, amount, exponent, interval, renewal and cancellation wording, and **not** the time, so the
  same terms give the same digest. `createCheckout` recomputes it; a mismatch (the terms changed or were never shown)
  is 409 `INVALID_STATE` and nothing is created. The accepted digest is stored on the session as consent evidence.
- **Checkout session:** `createCheckout` needs an `Idempotency-Key`. The same key with the same price and digest returns
  the same session while it is open (201 again); the same key with a different body is 409 `IDEMPOTENCY_MISMATCH`. A new
  key while another session is open for the tenant expires that session first (one open session per tenant, 0004).
  The row is written before the provider call and filled in after it, so a provider failure leaves an open row without
  a URL that the same key completes on retry. Sessions expire after 60 minutes; `getCheckout` reports `Expired` with
  retry guidance from then on without writing. **A return URL never completes a session** (BIL-002, AT-BIL-01): only a
  verified paid event does, in slice 3, so the subscription stays as it was until then.
- **Billing portal:** `createBillingPortalSession` ensures the provider customer and returns a provider URL valid for
  5 minutes; no card data passes through the service (BIL-002).
- **Audit:** `CheckoutStarted`, `CheckoutExpired` and `BillingPortalOpened` go to the tenant's own chain in the same
  control-plane transaction (AUD-001).

## DEC-064 Signed webhook inbox, event processor and billing notices (S2, BIL-004, BIL-005, BIL-007, BIL-008, AT-BIL-01, AT-BIL-02, AT-BIL-04)
- **Status:** Adopted (simulator only; real provider wire formats arrive with D09). Grace length 7 days and the notice
  schedule are proposals (D10, BIL-007).
- **Receiving (BIL-004):** `receiveBillingWebhook` is anonymous (no token, no tenant header) and accepts only the
  configured provider key; any other key is 401. The raw body (at most 256 KiB, else 400) is verified **before** it is
  parsed. Simulator signature header `Simulator-Signature: t=<unix seconds>,v1=<hex>`; `v1` = HMAC-SHA256 over
  `"{t}.{raw body}"` with `Billing:Simulator:SigningSecret` (required, at least 32 characters) or, during rotation,
  `Billing:Simulator:PreviousSigningSecret`; comparison is constant time; `t` must be within 5 minutes of our clock in
  either direction. A bad or missing signature or a stale timestamp is 401; a verified body that is not a JSON event
  with `id`, `type` and `created` is 400; neither writes anything. The event is inserted into `cp.webhook_inbox`
  (`ON CONFLICT (provider, event_id) DO NOTHING`) and 200 `{received, duplicate}` is returned only after commit; a
  storage failure is 503 so the provider retries.
- **Payload at rest:** AES-256-GCM with `Billing:InboxKey` (base64, 32 bytes; required by API and worker, from the vault
  in production), associated data `provider|event_id`, stored as `v1 | nonce | tag | ciphertext`. `payload_redacted`
  holds the event type and provider references only: no amounts, names or emails. Logs carry event id, type and tenant.
- **Processing (BIL-005):** `BillingEventJob` in the worker, on the `ti_control` pool. Each due row (`Received`, or
  `Failed` with attempts left and `next_attempt_at` due) is processed in **one transaction** that locks the inbox row
  (`FOR UPDATE SKIP LOCKED`, so two workers never process one event), applies every effect and sets the final state.
  Lock order: inbox row → billing account → subscription → invoice → audit stream (last, DEC-024). On an exception the
  transaction rolls back and a second one records `Failed`, attempts + 1, backoff `min(30 s × 2^attempts, 1 h)`,
  `failure_code = PROCESSING_ERROR` and the exception type only; after 10 attempts the row stays `Failed` for operators
  (the `WebhookBacklog` alert and the console arrive with slice 5).
- **Tenant resolution:** only from **our** records: the customer reference must match a `cp.billing_account`
  (provider, provider_customer_id). Unknown → `Quarantined` (`UNKNOWN_REFERENCE`) plus one `UnknownBillingReference` task
  per unknown customer. A subscription, checkout or invoice reference that we hold for a *different* tenant →
  `Quarantined` (`REFERENCE_MISMATCH`) plus a task. Tenant ids in payloads are never read.
- **Ordering:** a subscription change applies only when the event's subscription revision is greater than the stored
  `provider_revision`; an invoice row changes only on a newer invoice revision, and a `Paid` invoice never returns to
  `Open`. Payments are unique by provider reference. So duplicates are no-ops and an older failure cannot regress a paid
  period (AT-BIL-02).
- **Effects** (rules in `Domain/Saas/BillingEvents.cs`):
  - *Initial invoice paid* (references our checkout session): session `Completed`; subscription `Active` on the
    session's price and its plan, period from the invoice, provider subscription reference and revision set, grace and
    pending cancellation cleared; new current entitlement snapshot (previous one closed). A second paid initial event
    for the same provider subscription changes nothing; one for a different provider subscription opens a
    `SubscriptionMismatch` task and changes nothing (no double activation).
  - *Initial payment failed*: invoice and payment recorded; trial, end state and session unchanged (BIL-003).
  - *Renewal paid*: period advanced; `PastDue → Active` and grace cleared only when no other open invoice of the
    subscription remains (BIL-007).
  - *Renewal failed*: `Active → PastDue` with `grace_end = due date + 7 days`; already `PastDue` keeps its grace.
  - *Dispute, chargeback, refund*: payment/refund rows and one operator task per provider reference; no access change,
    no stock effect (BIL-008).
  - Other event types: `Ignored`.
- **Lifecycle precedence (LFC-001, AT-BIL-04):** billing never reads or writes the security suspension, so a payment
  can settle dues but the computed mode stays `SecuritySuspended`. A `Deleted` tenant gets no effect at all: the event
  is `Ignored` (`TENANT_DELETED`) with a `SubscriptionMismatch` task for refund review; nothing is created.
- **Notices without an outbox grant:** `ti_control` still has no privilege on `ti.outbox_event` (SAS-004). The processor
  writes `cp.billing_notice` rows (SubscriptionActivated, PaymentFailed, PaymentRecovered) in its transaction;
  `BillingNoticeRelayJob` moves them into the tenant's outbox from a per-tenant `ti_app` transaction (TenantJobRunner,
  matrix row "Billing reminders and reconciliation"). `ti_app` may read its own tenant's notices and set only
  `relayed_at` (migration 0018). Day 3 and 6 reminders come with slice 5's schedule.
- **Audit:** `SubscriptionActivated`, `CheckoutCompleted`, `SubscriptionRenewed`, `SubscriptionPastDue`,
  `PaymentRecorded`, `PaymentFailed`, `BillingDisputeOpened`, `ChargebackRecorded`, `RefundRecorded` in the tenant's own
  chain, actor `system:billing-webhook` (System), correlation id = provider event id.
- **Simulator console:** `PayCheckout`, `FailCheckout`, `PayRenewal`, `FailRenewal`, `Dispute`, `Refund` build signed
  events (`SimEvent`: body bytes and signature header) for tests to deliver; `Sign(body, at)` and `Forge` exercise replay
  and forgery. Nothing is charged.

## DEC-065 Plan changes, cancellation and resume (S2, ENT-005, ENT-006, BIL-008, AT-ENT-02)
- **Status:** Adopted (simulator only). Recall and excursion blockers arrive with M5's tables; telemetry removal is
  covered by the `active_devices` quota until M5 adds devices.
- **Who and when:** `previewSubscriptionChange` and `changeSubscription` need `billing.manage` and an `Active`
  subscription with no pending cancellation. `Trialing` and `Cancelled` buy through checkout; `PastDue` settles dues
  first; a pending cancellation is resumed first (all 409 `INVALID_STATE`). The target price must be active and in a
  published public plan or the contracted plan (else 404) and differ from the current price.
- **Effective date (ENT-005):** every change takes effect at the current `period_end`: the next full billing cycle, no
  proration. The preview states the amount and that date. `previewDigest` = SHA-256 of the RFC 8785 JSON of tenant,
  subscription, current and target price, target plan, amount, exponent, interval, `effectiveAt` and the no-proration
  wording; a renewal in between changes the date and so the digest (409 `INVALID_STATE`, preview again).
- **Blockers (ENT-006, AT-ENT-02):** computed in a **tenant** transaction (`ti_app`; the control role still reads no
  stock, SAS-004): `QUOTA_EXCEEDED` for every quota whose current usage exceeds the target limit (with quota, used,
  limit); when the target drops `medical_storage`: `MEDICAL_STOCK_ON_HAND` (on-hand Medical buckets),
  `MEDICAL_RESERVATIONS` (Active or Picked reservations of Medical products) and `MEDICAL_IN_TRANSIT` (dispatched,
  not fully received transfer lines of Medical products). The preview lists them; `changeSubscription` refuses with
  409 `DOWNGRADE_BLOCKED` and `Problem.blockers`. Nothing is ever deleted or deactivated to make a change fit.
- **Scheduling:** `Idempotency-Key` and `If-Match` (subscription revision) are required. The same key with the same
  body returns the same change (202 again, checked before If-Match so a retry after a lost response works); a different
  body is 409 `IDEMPOTENCY_MISMATCH`. Under the billing account and subscription locks the store cancels any other open
  change, inserts the new one `Pending` (key and acceptance evidence: who, when, digest), bumps the subscription
  revision and audits `SubscriptionChangeRequested`. Then the provider is told (`ScheduleChange`, key `change:{id}`) and
  the row becomes `Scheduled` with the provider reference. A provider failure is 503 and leaves the `Pending` row,
  which the same key completes. Migration 0019 adds `idempotency_key` (unique per tenant) and `provider_change_ref`.
- **Applying (worker `PlanChangeJob`, ENT-006 recheck):** for each `Scheduled` change whose `effective_at` has passed
  it recomputes the blockers in the tenant's transaction, then in a control transaction locks the subscription, the
  change and the tenant's usage counters (the rows `QuotaGate` locks, so no quota reservation can slip between the
  recheck and the new snapshot) and rechecks the quota blockers against them.
  - Blocked → the provider schedule is cancelled (key `change-cancel:{id}`), the change becomes `Blocked` with its
    blockers, `SubscriptionChangeBlocked` is audited and a `PlanChangeBlocked` notice goes to the owner. The old plan
    stays. A provider failure leaves the change for the next run.
  - An upgrade (any limit raised or feature added, none removed) waits for the verified **paid** invoice of the period
    starting at `effective_at` (ENT-005); other changes apply at the boundary.
  - Applied → subscription plan and price become the target, revision + 1, a new current entitlement snapshot,
    `SubscriptionChangeApplied` audited, `PlanChangeApplied` notice. A deleted tenant's change is `Cancelled`.
  Medical facts are not lockable from the control plane; a medical receipt between the tenant recheck and the apply is
  still governed by feature gating and the safety rules (safety never consults entitlements).
- **Cancel and resume (BIL-008):** `subscription.cancel` (owner), `If-Match`. Cancel needs `Active` or `PastDue`; the
  provider is told first (`cancel:{subscriptionId}:{revision}`), then `cancel_at_period_end` is set, the revision
  bumped, an open change cancelled (also at the provider) and `SubscriptionCancellationRequested` audited with the
  reason. Repeating it changes nothing. Service continues to `period_end`; from then access is `BillingRestricted` by
  computation (DEC-033) without waiting for any event. The provider's `customer.subscription.deleted` sets `Cancelled`
  (`SubscriptionEnded`). Resume clears only a still-pending cancellation before `period_end`
  (`resume:{subscriptionId}:{revision}`); otherwise 409 `INVALID_STATE` and a new checkout is needed. Data is kept.
- **Simulator:** `ScheduleChange` makes the next renewal invoice use the target price; `CancelScheduledChange`,
  `CancelAtPeriodEnd`, `Resume`; console `EndSubscription` sends `customer.subscription.deleted`.

## DEC-066 Reconciliation, provider outage, billing consoles, reminders and quota races (S2, BIL-005, BIL-006, BIL-007, SOP-003, AT-SOP-01, AT-ENT-01, AT-ENT-03)
- **Status:** Adopted (simulator only). Reminder days 0, 3 and 6 are the BIL-007 proposal (D10). Persisted platform alert
  records and the monitoring dashboard are S3 (SOP-003); this slice raises the alerts as structured `[BILLING ALERT]` error
  logs and makes the evidence visible in the consoles below. This narrows what DEC-064 deferred to "slice 5".
- **Reconciliation (BIL-006, worker `BillingReconciliationJob`, daily by default and on demand after an outage):**
  1. *Catch up:* the adapter lists provider events after the stored cursor (`ListEventsSince`, `cp.billing_sync_state`). Each
     one goes into the inbox through the same insert as a webhook (`received_via = 'Reconciliation'`, deduplicated on
     `(provider, event_id)`), then the normal processor applies it. Missed or late webhooks therefore converge through the
     one apply path, with the same ordering, ownership and precedence rules (DEC-064). Events fetched over the provider's
     authenticated API carry no webhook signature; they are never accepted from a caller.
  2. *Compare:* for every provider-linked subscription of a non-deleted tenant it fetches the provider's current subscription
     (`FetchSubscription`) and compares state, period end and pending cancellation. A difference that the events did not
     resolve opens one `SubscriptionMismatch` task per subscription revision. Open or uncollectible invoices past their due
     date open one `UnpaidInvoice` task each. A subscription whose current entitlement snapshot is missing, expired or for
     another plan opens an `EntitlementMismatch` task and an `EntitlementMismatch` alert. Nothing is corrected silently.
  3. The cursor, `last_run_at`, `last_success_at` and `last_error` are recorded.
- **Provider outage (BIL-005, SOP-003, AT-SOP-01):** access never depends on the provider: it is computed from our last
  verified state, so tenants are not restricted while the provider is down. Webhooks are still stored (the inbox needs no
  provider). A run that cannot reach the provider keeps its cursor, records `last_error`, raises a `BillingReconciliation`
  alert and changes nothing; the next run catches up. Commands that need the provider (checkout, portal, changes, cancel)
  answer 503 and keep their retry path (DEC-063, DEC-065).
- **Webhook backlog (SOP-003):** each `BillingEventJob` pass raises a `WebhookBacklog` alert when the oldest unapplied event is
  older than five minutes or an event has exhausted its 10 attempts.
- **Reminders (BIL-007):** `BillingReminderJob` (hourly) adds a `PaymentReminder` notice for each `PastDue` subscription on day 3
  and day 6 after the due date (`grace_end − 7 days`), one per day per grace period; day 0 is the `PaymentFailed` notice. They
  reach the tenant through the notice relay (DEC-064). Migration 0020 adds the notice kind and `webhook_inbox.received_via`.
- **Consoles (PlatformBilling, `Platform:BillingSubjects`, SOD-8):** `listReconciliationTasks` (status and kind filters, newest
  first, keyset cursor), `resolveReconciliationTask` (resolution 1–1000 characters; records who and when; 409 if already
  resolved; audited in the tenant's chain when the task names a tenant; it never changes stock or access) and
  `listWebhookEvents` (metadata only: state, retries, failure code; payloads stay encrypted).
- **Quota races (AT-ENT-01, AT-ENT-03):** no new mechanism: `QuotaGate` locks the counter row before reading the limit
  (DEC-038) and plan changes recheck under the same locks (DEC-065). Tests run concurrent requests for the final warehouse
  slot and concurrent attachment uploads against the byte limit. Uploads reserve bytes inside the upload transaction, so a
  failed upload releases them by rolling back; expiring upload reservations arrive only if resumable uploads are added.

## DEC-067 Owner decisions D09 (provider, currency, prices) and D10 (grace and reminders) (S2, owner, 11 Nov 2026)
- **Status:** Adopted (owner). Still open: D09 plan names, quotas, per-plan trials and taxes; D10 90-day export window and
  retention-over-deletion.
- **Provider (D09):** Paystack. The simulator stays the only provider in development and automated tests (AGT-004); Paystack
  runs in **test mode** until a production configuration with live keys is separately approved. No Paystack key is ever in the
  repository: test and live secrets come from the vault or the environment.
- **Currency (D09):** NGN, exponent 2 (kobo). XTS remains the test currency for fixtures. The catalog accepts NGN and XTS; it
  refuses every other currency.
- **Prices (D09):** configurable by platform billing operators through the existing plan catalog API (DEC-062), with no code or
  deployment change. Publishing a plan version is the approval to show its prices (`display_approved` set at publication).
  Prices stay immutable once referenced, and invoices keep the price they were issued at (ENT-001).
- **Grace and reminders (D10, BIL-007):** the 7-day past-due grace from the due date and reminders on day 0, 3 and 6 are approved
  as implemented (DEC-064, DEC-066).
- **S2 gate:** stays open. Slice 7 adds the Paystack adapter: HMAC-SHA512 webhook verification over the raw body
  (`x-paystack-signature`), Paystack's replay controls (event dedupe; published source IPs), amounts in kobo, plan changes as
  disable-at-period-end plus a new subscription, and reconciliation by comparing subscriptions and transactions. It is built and
  tested against a labelled Paystack-format simulator in the repository, then run once against Paystack test mode by the owner
  with test keys held outside the repository. S2 closes when that sandbox run passes.

## DEC-068 Paystack adapter, NGN catalog prices and provider-neutral adjustments (S2 slice 7, D09, BIL-002, BIL-004, BIL-005)
- **Status:** Adopted. Paystack runs in **test mode only**: startup refuses a `sk_live_` key and `Billing:LiveCheckoutEnabled`
  (DEC-040). Automated tests use a labelled in-process Paystack API simulator; the real test-mode run is the owner's
  (`docs/paystack-sandbox.md`).
- **Configuration:** `Billing:Provider = paystack`, `Billing:Paystack:SecretKey` (required, `sk_test_…`, from the vault or
  environment, never the repository), optional `Billing:Paystack:BaseUrl` (default `https://api.paystack.co`) and
  `Billing:Paystack:WebhookSourceIps` (comma-separated; empty means not enforced). `Billing:ReturnUrl` is the callback URL.
- **Catalog (D09):** NGN (exponent 2, kobo) and the test currency XTS are accepted; any other currency is 422. Publishing a plan
  version sets `display_approved` on its NGN prices (publication is the owner-approved display, DEC-067); XTS prices are never
  approved for display. A provider offers only its own currencies: Paystack NGN, the simulator XTS and NGN; an unsupported price
  is not offered (404), so nothing is ever sent to Paystack in XTS.
- **Mapping (adapter):**
  - Customer `POST /customer` (email, legal name, `metadata.billing_account_id`) → `customer_code`. Price `POST /plan`
    (name `TI {priceVersionId}`, kobo, `monthly`/`annually`, NGN) → `plan_code`; an existing plan with that name is reused, so a
    retry creates no second plan.
  - Checkout `POST /transaction/initialize` (email, amount, plan, reference `ti-cs-{checkoutId}`, callback) →
    `authorization_url`; the reference is the session reference. Paystack refuses a reused reference, so a checkout whose
    first call was lost cannot be resumed with the same key: the caller gets 503 with guidance to start a new checkout.
  - Portal: `GET /subscription/{code}/manage/link` (5 minutes in our record).
  - Plan change: Paystack cannot reprice a subscription. Scheduling disables renewal of the current subscription
    (`/subscription/disable` with its email token) and creates the new one with `start_date = effectiveAt`; the new code is
    stored on the change (`provider_new_subscription_ref`, migration 0021). Cancelling the schedule disables the new one and
    re-enables the old one. Applying the change moves `cp.subscription` to the new code. Events for an open change's new
    subscription belong to the tenant (no quarantine).
  - Cancel: `/subscription/disable`; resume: `/subscription/enable`.
- **Webhooks (BIL-004):** `x-paystack-signature` = hex HMAC-SHA512 of the raw body with the secret key, compared in constant
  time; Paystack signs no timestamp, so its replay controls are deduplication on a stable event id
  (`{event}:{data.id}`) and, when configured, the published source IP allowlist. Events used: `charge.success` (initial, our
  `ti-cs-` reference), `invoice.update` with a paid invoice (renewal), `invoice.payment_failed`, `subscription.disable`,
  `charge.dispute.create`, `refund.processed`; others are Ignored. Ordering uses the resource's `updatedAt`/`paid_at` in
  milliseconds as the monotonic revision.
- **Authoritative resolution (BIL-005):** a provider may need to fetch before an event can be applied. `IBillingWebhooks` gains
  `ResolveAsync`, called by the processor before it locks the row: Paystack's initial `charge.success` fetches the subscription
  created for the plan and customer (code, `next_payment_date` as period end). A fetch failure is a normal processing failure
  with backoff.
- **Early provider end (provider-neutral):** a `SubscriptionEnded` event for a subscription with a pending cancellation whose
  paid period has not ended no longer sets `Cancelled` at once (Paystack disables immediately on cancel); it is audited and the
  subscription stays paid until `period_end`. A new `SubscriptionPeriodEndJob` sets `Cancelled` once `period_end` has passed.
  Access was already restricted at `period_end` by computation, so this is bookkeeping. An end without a pending cancellation
  (provider-initiated) still cancels at once. Reconciliation treats "ended at the provider" as consistent with a pending
  cancellation before `period_end`.
- **Reconciliation with Paystack:** Paystack has no event-list API, so `ListEventsSince` returns nothing and convergence relies
  on Paystack's own webhook retries plus the comparison (`FetchSubscription`: active → Active, attention → PastDue,
  non-renewing → Active with pending cancellation, cancelled/completed → Cancelled), whose differences become operator tasks.

## DEC-069 S3 starts while the S2 gate waits for the owner's Paystack run (owner instruction, 13 Nov 2026; AGT-001)
- **Status:** Adopted (owner). The owner set the Paystack test-mode run aside and asked for the remaining modules to be built.
- **Conflict:** AGT-001 allows only the current milestone. S2 is code-complete; its gate waits only on the owner's sandbox run,
  which needs nothing new from the code.
- **Resolution:** S3 (lifecycle and service) is the current milestone from today. S2 stays **open**, not met: its gate is assessed
  after the Paystack run, and no commercial activation happens before both S2 and S3 pass (section 31). After S3 the order is
  M5 then M6, one milestone at a time. No control is weakened: the simulator stays the only provider in development and tests,
  and startup still refuses live keys and live checkout (DEC-040, DEC-068).

## DEC-070 S3 lifecycle and service: plan, closure and platform lifecycle controls (S3, LFC-001, LFC-002, AT-LFC-01)
- **Status:** Adopted. Proposed defaults (D10: 90-day export window, retention over deletion) stay configurable and unapproved.
- **Slices:** (1) closure request and status, retention hold, closure withdrawal (migration 0022); (2) tenant archive export
  (LFC-003); (3) approved deletion, deletion register and restore replay (LFC-004, AT-LFC-02); (4) tenant-aware rate limits,
  export concurrency, fair worker scheduling, noisy-tenant test (SOP-001, SOP-002, AT-SOP-01); (5) persisted platform alerts,
  service status and support routes, notice targeting (SOP-003, SOP-006, SUI-002); (6) gate evidence, runbooks, handoff docs.
  Browser evidence for section 29 screens (SUI-001, SUI-003 browser) needs the web app, which is still a placeholder; it moves
  to the M6 UI work and is listed as a carry-over, not claimed.
- **Closure (`POST /saas/closure`, LFC-002):** owner only (`tenant.close` and the TenantOwner role), re-authentication within
  5 minutes (SOD-7), `confirmation` must equal `CLOSE <slug>`, Idempotency-Key required. Steps:
  1. Safety blockers, read in a tenant transaction (`ti_app`; the control role reads no stock, SAS-004): medical stock on hand,
     medical stock in transit, stock on hand in `Hold` or `Recalled`. Recalls and excursions join this list when M5 creates
     them. Any blocker: the open closure record is `Blocked` with `hold_results`, the response is 409 `RETENTION_BLOCKED` with
     the blockers, and nothing else changes; safety work continues.
  2. Outstanding billing: unpaid invoices are counted and returned (`outstandingInvoiceCount`); they never block closure and the
     billing category stays open while closing.
  3. A paid subscription not yet cancelled is cancelled at period end through the provider first (same path as
     `cancelSubscription`, DEC-065), so nothing renews after closure.
  4. One `ti_control` transaction: lock the tenant row, require `Ready`; the closure record becomes `ExportWindow` with
     `export_window_ends_at = max(now, paid or trial end) + Lifecycle:ExportWindowDays` (default 90, D10 proposed);
     `ti.tenant.lifecycle = Archived`, `archived_at`, and `deletion_requested_at` when `requestedDeletion`; audit events in the
     tenant's chain.
  5. A `TenantArchive` export is queued in a tenant transaction and linked to the record (`archiveExportId`).
  A retry with the same key returns the same record and resumes any step not finished; another key while a closure is open is
  409 `INVALID_STATE`. Legal hold blocks deletion, not closure.
- **Status (`GET /saas/closure`):** the latest record, owner only (setup-status category; Closed mode allows it to the owner).
- **Platform lifecycle controls (PlatformOperations, audited in the tenant's chain, If-Match on the tenant version):**
  `POST /platform/v1/tenants/{id}/retention-hold` sets or clears `retention_hold` with a reason and instruction reference;
  `POST /platform/v1/tenants/{id}/closure/withdraw` sets the open record `Withdrawn` and the tenant back to `Ready`
  (`archived_at` and `deletion_requested_at` cleared). The subscription stays cancelled at period end; the owner can resume it.
- **Migration 0022:** `cp.deletion_record.idempotency_key` (unique per tenant), `outstanding_invoice_count`,
  `archive_export_id`, `version`; `ti.tenant.retention_hold_reason`, `retention_hold_reference`; `ti_app` may read its own
  tenant's deletion record (`tenant_read` policy, like other cp reads).
- **Retry after archiving (as built):** once the workspace is Archived the access gate refuses `requestClosure` (ownership
  category is not allowed in Closed mode), even for the same Idempotency-Key, with recovery `view-closure`; the client reads
  `GET /saas/closure`. So the archive export is queued **before** the archiving transaction and its id is written in that same
  transaction; a retry while the workspace is still Ready (a Blocked attempt) replays from the record.

## DEC-071 Tenant archive export (S3 slice 2, LFC-003, AT-LFC-02)
- **Status:** Adopted.
- **Who:** `type = TenantArchive` on `POST /exports` is owner only, checked at creation, at link issue and again at download
  (a non-owner gets 403 at creation and 404 for the link or file). Closure queues one automatically (DEC-070).
- **Format:** one ZIP (`application/zip`, object key `…/exports/{id}.zip`): `tenant.json`; `data/{table}.jsonl` for each tenant
  table in `TenantArchive.Tables`; `lineage/lots.jsonl` (per lot: receipt lines, movements, shipment lines); `audit/events.jsonl`
  with previous and event hashes, and `audit/integrity.json` (stream range, last hash, hash rule, signed checkpoints) so the
  chain can be verified offline; `attachments/manifest.json` and the bytes of **clean** attachments only; `manifest.json` with
  tenant id, generation time, filter and a SHA-256, size and row count per file. The export job's own manifest signs the ZIP
  digest as for other exports (DEC-061).
- **Scope:** every query runs in the tenant's own `ti_app` transaction, so RLS confines it to that tenant (TEN-003); nothing is
  read from the control plane, so provider payloads and tax references cannot appear. Columns holding token or
  re-authentication digests (`token_hash`, `reauth_reference`) are dropped. Tables left out, each with a reason, are listed in
  `TenantArchive.Excluded` (`invitation`: token digests; `idempotency_record`, `outbox_event`, usage counters: internal).
  A schema test fails when a tenant table is in neither list.
- **Links:** the existing signed links (15 minutes, bound to tenant, caller and export, DEC-061). After deletion the files are
  removed with the tenant's storage prefix, so any link fails (DEC-072).

## DEC-072 Approved deletion, the deletion register and restore replay (S3 slice 3, LFC-004, AT-LFC-01, AT-LFC-02)
- **Status:** Adopted. Destructive deletion runs only against throwaway test databases (section 30). Backup retention is the
  proposed D06 schedule (`Lifecycle:BackupRetentionDays`, default 35) until the owner approves one.
- **Conflict (AGT-003):** the ledger, document operations, pick confirmations, shipments and audit tables are append-only, and
  no runtime role may delete tenant data; LFC-004 requires removing eligible live data. **Resolution:** a separate NOLOGIN role
  `ti_purger` (bootstrap `0002_purge.sql`) owns `cp.purge_tenant(tenant)`, the only path that deletes live rows. The function
  checks in the database that the tenant is `DeletionPending`, has an approved `DeletionPending` record and no retention hold,
  then deletes every tenant table except the audit evidence, in passes until foreign keys allow. The append-only and
  draft-only triggers allow DELETE only for `ti_purger` with the purge marker set for that row's tenant; `audit_event` and
  `audit_checkpoint` stay undeletable by anyone (and `ti_purger` has no grant on them). `ti_app` gains nothing; `ti_control`
  gains only EXECUTE on the two functions. Audit history is kept, so the control is narrowed, not weakened.
- **Approval:** `POST /platform/v1/tenants/{id}/deletion/approve` (platform operations, reason, If-Match): the closure must be in
  its export window with the owner's deletion request and no retention hold (hold: 409 `RETENTION_BLOCKED`). Without the
  owner's request, deletion needs an approved retention policy (D10), which does not exist yet, so it is refused.
- **Deletion job (worker, every 15 minutes):** for an approved record whose window has ended: recheck safety blockers in the
  tenant transaction (any blocker: record `Blocked`, nothing removed); then `DeletionPending` (tenant and record, audited);
  `cp.purge_tenant()` (memberships, role grants and invitations go with the data, so no token gives access); remove the tenant's
  storage prefix (`IObjectStore.DeleteTenantAsync`, which also clears orphaned objects, the M4 carry-over); then `Deleted` with
  `completed_at`, `backup_expiry_at = now + BackupRetentionDays` and a `scope` listing rows and objects removed and what was
  retained. Each step is idempotent; a record left `DeletionPending` resumes at the purge.
- **Retained, with restricted access:** the tenant row (deletion evidence), `audit_event`, `audit_checkpoint`, `audit_stream`,
  control-plane billing records, and the deletion record. No blanket seven-year purge; legal holds prevail.
- **Deletion register and restore (AT-LFC-02):** the register is the set of `Deleted` records. The migrator gains
  `export-deletion-register [file]` (live control plane) and `replay-deletions [file]` (restored environment, API and workers
  stopped). Replay calls `cp.replay_deletion()` per entry: marks the tenant `DeletionPending` under the register's approval
  (withdrawing any older open record restored from the backup), purges, marks `Deleted`, then verifies no membership, grant,
  invitation or usage row remains and exits non-zero otherwise. The test restores a real `pg_dump` taken before the deletion.

## DEC-073 Tenant-aware rate limits, export concurrency and fair worker scheduling (S3 slice 4, SOP-001, SOP-002, AT-SOP-01)
- **Status:** Adopted. Budgets are the spec's proposed runtime budgets, configurable, until the owner approves them.
- **Request budgets:** fixed one-minute windows, no queueing. Each person in each tenant: `RateLimit:HumanPerMinute` (120). Each
  service identity (token with a `client_id` claim) in each tenant: `RateLimit:ServicePerMinute` (600). Anonymous calls per
  client address: `RateLimit:PermitsPerMinute` (600). Chained with an overall budget per identity across all tenant headers,
  `RateLimit:PerIdentityPerMinute` (1200), so cycling made-up tenant ids buys nothing.
- **Why the subject is in every key:** the limiter runs before membership is checked, so the tenant header is not yet trusted.
  Keying by tenant alone would let a forged header spend another tenant's budget. With the subject in the key, a forged header
  only moves the caller into another partition of their own. Consequence: the 600/min service budget is per service identity
  per tenant, not a tenant aggregate; an aggregate needs validated membership and is left for M5, where service principals and
  device ingestion arrive (telemetry gets its own budget then, per SOP-001).
- **No database work for rejected requests:** the limiter runs after authentication and before tenant context, so a 429 opens no
  connection and no transaction. Throttled requests are not usage (nothing is billed per request).
- **Exports:** at most `ExportService.MaxConcurrent` (2) unfinished exports per tenant, counted under a transaction-scoped
  advisory lock; the third is 429 `RATE_LIMITED` with Retry-After. The archive queued by closure is never refused.
- **Fair worker scheduling:** the export worker serves tenants round-robin, one export per tenant per pass, up to 50 per run.
  Other per-tenant jobs already process each tenant in bounded batches.
- **Evidence:** ten tenants, one sending 150 concurrent requests against a budget of 30: exactly 30 served and 120 rejected with
  Retry-After, while all 90 requests of the other nine tenants succeed. The full section 18 workload test (50 users, 1M
  movements) on staging remains M6 evidence (NFR-PERF); SOP-002 stays Partial until then.
- **Connection pools are closed with their host (found by this slice):** `AppDataSource`, `PlatformDataSource` and
  `ControlDataSource` now dispose their Npgsql pools, so a stopped host (or a test host) no longer leaves idle connections
  that count against the database connection limit. Before this, extra test hosts could exhaust the limit and later tests failed
  with 500s depending on order.

## DEC-074 Platform alerts, service status and billing notice recipients (S3 slice 5, SOP-003, SOP-006, SUI-002)
- **Status:** Adopted.
- **Signals (`GET /platform/v1/health`, platform.health.read):** signups in 24 hours (started, Ready, Failed), signup-to-Ready
  latency p50/p95, trials ending within three days, Ready workspaces whose trial ended unpaid, webhook backlog and oldest age,
  open entitlement mismatch tasks, open and day-old reconciliation tasks, open invoices, seat counters that differ from active
  memberships (quota reconciliation), open alerts. Aggregates only; no tenant name, data or stock (SOP-006, SAS-004).
- **Alerts (migration 0024, `cp.platform_alert`):** the worker's monitor (every minute) evaluates the signals: webhook backlog
  older than five minutes and any entitlement mismatch are Critical; provisioning failures, reconciliation backlog (tasks open
  over a day or counter divergence) and unpaid invoices are Warning. One open alert per kind; a present signal refreshes it, a
  cleared one closes it. Operators list alerts and acknowledge them with a note (If-Match); the row records who and when.
  A provider outage raises the backlog alert but never restricts tenants (DEC-066).
- **Service status (`GET /api/v1/service-status`, public):** overall and per-component status from the readiness checks, and the
  support contact (`Support:Contact`, `Support:StatusPageUrl`; "Not configured" until set). The customer SLA remains a
  commercial decision. Problem responses already carry trace ids for support (SOP-006).
- **Billing notices (SUI-002):** the relay writes one outbox message per notice and recipient: each active TenantOwner and
  BillingAdministrator (by subject; delivery resolves the verified address at the identity provider) and the billing contact
  address. The event id is a SHA-256-derived UUID of notice and recipient, so a repeated relay collides on the outbox's unique
  event id instead of sending twice. No notification preferences exist, so nothing can suppress required notices; warehouse
  users never see the messages.

## DEC-075 M5 medical: plan, and quality hold, release and disposition (M5 slice 1, MED-001, MED-002, MED-003, SOD-2, EXC-007)
- **Status:** Adopted. M5 is the current milestone from 15 Nov 2026 (owner instruction "go ahead with M5"; S2 and S3 gates still
  open, DEC-069). Medical policies (D04) remain proposed defaults that QA must validate before live use.
- **Slices:** (1) quality hold, release and disposition with MED-001 eligibility, expiry and shelf-life boundaries; (2) recalls:
  blocking, impact, outreach, closure (EXC-006, EXC-007, AT-OUT-02), plus recall blockers for closure and downgrades; (3) devices
  and telemetry ingestion (TEL-001, TEL-002, ENT-003/004 device quota); (4) excursions, holds, stale telemetry blocks, late
  readings and shipment flags, tenant alerts (TEL-003 to TEL-005, AT-TEL-01/02); (5) traceability export (M4 carry-over),
  gate evidence and handoff docs.
- **One bucket per approval (contract change):** an approval binds one subject version and content digest (IAM-003). A quality
  release therefore binds **one bucket**: `QualityReleaseRequest` takes `bucketId` instead of `bucketIds` (the drafted array would
  let one signature cover buckets whose contents differ). Subject types: `QualityRelease` (Quarantine → Available) and
  `HoldRelease` (Hold → Available) bind the bucket at its version with a digest over id, quality, bin, product, lot, serial and
  on hand; any stock change bumps the bucket version, so a changed bucket makes the approval stale (409 `APPROVAL_STALE`). New
  subject type `QualityDisposition` binds a bucket for dispositions that need approval. Signers need `approvals.sign.quality`
  (SOD-2); the requester and anyone who last moved the bucket cannot sign (SOD-1).
- **Hold (`POST /quality/hold`, safety, idempotent):** each bucket (locked in id order) in `Available` or `Quarantine` moves
  entirely to the `Hold` bucket of the same bin with one `QualityChange` movement. Reservations on it move with the stock
  (re-pointed to the Hold bucket): an Active one can no longer be picked, a Picked one cannot be dispatched (existing M3 dispatch
  recheck and rework task), and the planner deallocates or unpicks, which stays allowed (MED-002). Holding never consumes stock.
  A bucket under an open reconciliation block is refused (its quantities are under investigation).
- **Release (`POST /quality/release`, routine-write, idempotent):** source `Quarantine` (QualityRelease) or `Hold` (HoldRelease);
  `Recalled`, `Damaged` and `Available` cannot be released (EXC-007). MED-001 checks, all at release time and none overridable
  by the signer: identifiers complete for the tracking mode (lot and expiry for lot tracking and for every medical lot, serial
  for serial tracking); not expired in the warehouse's time zone (MED-003: derived from the date, not from a status); every
  receipt line of this lot or serial passed inspection; the bin's zone suits the product's storage profile; no open recall
  (slice 2) and no open excursion (slice 4) covers it. Failures return 422 `RELEASE_INELIGIBLE` with each reason. The whole
  bucket moves to the `Available` bucket of the same bin, unreserved.
- **Disposition (`POST /quality/disposition`, safety, idempotent):** `Damaged` moves the quantity to a Damaged bucket (status
  change); `Disposal` and `SupplierReturn` post outbound movements that leave the ledger. An approval (`QualityDisposition`) is
  required for medical stock and for recalled stock (EXC-007). Reserved quantity is never disposed: release the reservation
  first. Dispositions never make stock available.

## DEC-076 Recalls: immediate blocking, impact, outreach and closure (M5 slice 2, EXC-006, EXC-007, AT-OUT-02)
- **Status:** Adopted.
- **Schema (migration 0026):** `ti.recall` (product, `all_lots` when no lot or serial is named, reason, effective time, status,
  closure approval, reason, actor and time, `last_modified_by`), `ti.recall_item` (exactly one of lot or serial; append-only),
  `ti.recall_outreach` (append-only log per customer; the latest row is the current status). `ti.recall_covers(product, lot,
  serial)` answers whether an open recall covers stock. The approved purge (DEC-072) covers the new tables; the tenant archive
  includes them.
- **Opening (`POST /recalls`, safety, idempotent):** in the same transaction every covered bucket that is Available, in
  Quarantine or on Hold moves whole to Recalled with its reservations (DEC-075 mechanism, document type Recall), including staged
  picked stock and stock in transit bins. So picking refuses (quality changed), dispatch refuses with 409 `STOCK_BLOCKED` and
  rework tasks (existing M3 path, AT-OUT-02), and allocation also excludes anything an open recall covers, for stock that arrives
  later (a return of a recalled lot, a transfer receipt). Release eligibility reports `Recalled` (MED-001).
- **Impact (`GET /recalls/{id}`):** covered buckets with stock (any quality), transfer lines in transit (matched by lot: transfer
  lines carry no serial), Active or Picked reservations with their orders, and shipment lines with customer and dispatch time for
  outreach.
- **Outreach (`POST /recalls/{id}/outreach`, If-Match):** a manual record per customer and status while the recall is open; no
  automatic messaging in R1.
- **Closure (`POST /recalls/{id}/close`, recalls.close, idempotent, If-Match):** refused (409 `INVALID_STATE` naming what remains)
  while covered stock is on hand, in transit or reserved: QA disposes of it through `POST /quality/disposition`, which needs a
  QualityDisposition approval for recalled stock (DEC-075). Then a `RecallClosure` approval binds the recall version and what
  remains; the opener and the last editor cannot sign (SOD-1) and the signer needs `approvals.sign.quality` (SOD-2). The recall,
  its items and outreach history are kept; Recalled buckets keep their status at zero.
- **Other blockers:** an open recall blocks workspace closure (`OPEN_RECALLS`, DEC-070) and, for medical products, a plan change
  that removes medical storage (`MEDICAL_OPEN_RECALLS`, ENT-006).
- **Not yet:** medical customer returns are still released through QA quality release of the quarantine bucket rather than the
  return inspection (the M4 return path refuses medical release); a recall raised after shipment never claims the goods were
  blocked (TEL-004 wording applies to recalls as well).

## DEC-077 Devices, telemetry ingestion, excursions and tenant alerts (M5 slices 3-4, TEL-001 to TEL-005, AT-TEL-01, AT-TEL-02, ENT-003, ENT-004)
- **Status:** Adopted. Thresholds are the proposed D04 defaults (60 s interval, stale after 300 s, 60 s future skew, immediate hold) and
  need QA validation before live use. Devices remain simulated in development (D07); no real gateway is connected.
- **Devices (migration 0027):** `ti.device` (external id, name, active, last reading) counted against `active_devices`
  (atomic quota reservation at creation, released at retirement, ENT-003/004). Zone placement is effective-dated in
  `ti.device_zone_assignment` (non-overlapping per device, `btree_gist`); moving a device closes the current row and opens a new one,
  so a reading's zone is the one in force at its `observedAt` (TEL-004).
- **Credentials (TEL-001):** `POST /devices/{id}/rotate-credential` returns `ti_dev_<credentialId>.<secret>` once (32 random bytes);
  only a SHA-256 of the secret is stored. The previous credential stays valid until the new one is first used or 24 hours pass.
  A gateway sends it as a bearer token on `POST /telemetry/readings` only; the JWT handler ignores it and the route is anonymous at
  that layer. `ti.device_for_credential()` (SECURITY DEFINER, owned by `ti_membership_reader`) is the only lookup without tenant
  context: it returns the tenant and device for a credential id, and the secret is compared in constant time. The tenant and zone
  come from the device, never from the body; the request schema has no tenant or zone fields. Then the usual access gate applies
  (telemetry-ingest category, `telemetry` entitlement). Device traffic has its own rate budget per credential
  (`RateLimit:DevicePerMinute`, default 120).
- **Readings (TEL-001, TEL-002):** a duplicate `eventId` per device returns 200 `duplicate: true` with no side effects (unique key).
  `observedAt` more than 60 s in the future is 422. A reading is **late** when it arrives more than 300 s after `observedAt` or is
  older than the device's latest observation; it is stored with both times (`processingStatus = LateReviewQueued` when it raises
  a review). Readings are append-only.
- **Excursions (TEL-003, AT-TEL-01):** a reading outside the storage profile of any product with stock in the zone opens one
  excursion per zone and profile (or extends the open one: last abnormal value, max/min), places every Available or Quarantine
  bucket of those products in the zone on Hold with its reservations (DEC-075, document type Excursion, recorded in
  `ti.excursion_bucket`), and opens one tenant alert (`ti.alert` type Excursion, deduplicated). A duplicate or a second abnormal
  reading never holds twice. A normal reading records `endedAt` but never releases stock or resolves the excursion (section 13).
- **Late readings (TEL-004, AT-TEL-02):** a late abnormal reading opens a historical excursion, holds current stock as above, and
  flags shipment lines of the affected products dispatched since `observedAt` from that warehouse
  (`ti.excursion_shipment_flag`) for QA investigation. It never claims dispatched goods were blocked, and a late normal reading
  never ends or clears anything.
- **Stale telemetry (TEL-003):** a zone with a current device assignment is stale when no assigned active device reported within
  300 s. Stock of products whose profile requires monitoring is then not allocated, picked or dispatched (`ti.zone_telemetry_stale`
  in the allocation query and the pick and dispatch rechecks). The monitor job raises one StaleDevice alert per device.
- **Resolution (`POST /excursions/{id}/resolve`):** needs an `ExcursionDisposition` approval of the excursion version (QA signs,
  SOD-2). `Release` returns the held buckets to Available only if each passes MED-001 apart from this excursion (and is refused with
  `SUBSCRIPTION_RESTRICTED` in restricted modes because it makes stock available); `KeepOnHold` leaves them held; `Damaged` moves
  them to Damaged. Acknowledgement (`alerts.acknowledge`) records who and when.
- **Tenant alerts (TEL-005):** `GET /alerts`, `GET /alerts/{id}`, `POST /alerts/{id}/acknowledge` over `ti.alert`, with zone,
  products, lots and first/last abnormal values in its details. Every new alert is also published to the outbox (`AlertRaised`),
  whose retries and dead-letter handling already exist; email delivery stays optional and unconfigured.

## DEC-078 Lot traceability and the Traceability export (M5 slice 5, section 02, M4 carry-over)
- **Status:** Adopted.
- **Report (`GET /traceability/lots/{id}`, stock.read):** one read-only tenant transaction returns the lot and product; receipts (receipt,
  line, purchase order, supplier, warehouse, quantity, time, quality at receipt); quality history from `QualityChange` movements (time,
  bucket, from, to, reason); every movement of the lot; current buckets with stock; quantities in transit; shipments with order,
  customer, quantity and returned quantity; customer returns; recalls covering the lot; excursions that held it. Every fact comes
  from the append-only ledger or the documents that reference the lot; nothing is recomputed from current state.
- **Export (`POST /exports`, type `Traceability`):** requires `filters.lotId` (422 otherwise) and writes the same report as one JSON
  line with the usual signed manifest (DEC-061).

## DEC-079 M6 release readiness: plan, structured logs and operations metrics (M6 slice 1, NFR-MONITOR)
- **Status:** Adopted. M6 is the current milestone from 16 Nov 2026 on the owner's instruction ("go ahead with M6"). The M5 gate is
  still open (AT-AUD-01, CFG-002 evidence, INB-003, medical return release); the S2 and S3 gates wait on owner approvals (DEC-069).
  M6 builds evidence; it cannot itself approve production policies, which stay owner decisions (D01-D10).
- **Slices:** (1) structured logs and operations metrics; (2) backup restore drill with measured RPO/RTO and reconciliation
  (AT-OPS-01, NFR-RECOVERY); (3) performance benchmark harness (NFR-PERF, opt-in); (4) web app shell: OIDC sign-in with PKCE,
  tenant and warehouse selector, API client with loading/empty/validation/unauthorized/offline/retry states, scanner input
  (UI-001, UI-002); (5) receive-to-dispatch screens; (6) browser test of receive-to-dispatch (AT-UI-01); (7) deployment checklist
  and release evidence pack (SEC-TRANSPORT, NFR-AVAIL, SOP-004, SOP-005, SUCCESS-02).
- **Logs:** JSON console logs in API and worker with scopes. Every API request carries a scope with `traceId`, `tenantId` (once the
  membership is validated) and the route name; jobs carry `tenantId` and the job name. Headers, bodies, tokens and secrets are never
  logged; a test captures the log output of real requests and fails if a bearer token, device secret or reauth token appears.
- **Metrics (`GET /platform/v1/metrics`, platform.health.read, Prometheus text format):** process counters from a `Meter`
  (`ti_allocation_failures_total`, `ti_transaction_retries_total` for deadlock/serialization retries,
  `ti_rate_limited_total`) and database gauges read through `ti.operations_metrics()`: open ledger mismatch alerts, maximum outbox
  age, dead-lettered outbox messages, stale devices, held quantity and buckets on Hold, open excursions. The function is SECURITY
  DEFINER, owned by a new NOLOGIN role `ti_metrics_reader` (bootstrap `0003_metrics.sql`), and returns aggregates across tenants
  only, never a tenant id or name, so platform operators see service health without tenant data (SAS-004, SOP-006).

## DEC-080 Backup restore drill (M6 slice 2, AT-OPS-01, NFR-RECOVERY)
- **Status:** Adopted as development evidence. The production drill (managed backups, point-in-time recovery, encrypted storage) is
  a deployment step under D06 and is still owed before launch.
- **Drill (RecoveryDrillTests):** a `pg_dump` of the live test database is restored into a new database; the live deletion
  register is replayed before any access (DEC-072); the ledger reconciliation job runs on the restored data and raises no mismatch
  for the drilled tenant; a second API instance on the restored database verifies the tenant's audit chain (`POST /audit/verify`)
  and serves the same on-hand and reserved stock as at the backup; a record written after the backup is absent.
- **Measured on the development workstation (17 Nov 2026):** restore plus register replay 1.6 s; restore to verified service 5.0 s
  (budget RTO 4 h). A dump-only backup loses every write after it, so RPO equals the backup interval: the proposed RPO of 15 minutes
  needs continuous WAL archiving / point-in-time recovery in production (D06), which this drill cannot show.
- **Runbook:** docs/operations.md "Backup restore" (replaces the placeholder): restore in isolation, replay the register, reconcile
  the ledger, verify each tenant's audit chain against its latest external checkpoint, record timings, then open access.

## DEC-081 Performance benchmark harness (M6 slice 3, NFR-PERF, SOP-002)
- **Status:** Adopted. The harness is opt-in (`TI_RUN_PERF=1`, `TI_PERF_SECONDS`, default 60) and reported as skipped otherwise, so
  a normal test run never claims performance evidence.
- **Workload:** 20 products with one million synthetic movements; 50 concurrent virtual users for the period, 80 % stock queries
  (`GET /stock`, `GET /stock/movements` by product) and 20 % stock commands (bin transfers); p95 per category against the proposed
  budgets (queries 1 s, commands 2 s, section 18); one allocation of a 100-unit line over 100 candidate lots timed separately.
- **Evidence status:** a workstation run against the test container is indicative only. Release evidence is the same harness against
  staging with production-like sizing (NFR-PERF stays Partial until then).
- **What the first runs found and fixed (17 Nov 2026, workstation, 30 s window):** the stock list computed each bucket's last
  movement by loading the bucket's whole history into memory, and newest-first movement lists had no matching index: query p95
  6.5 s. Fixed with a per-bucket index probe and migration 0029 (newest-first indexes on source, destination, recorded time and
  product). After the fix, with all 50 users, the API and PostgreSQL on one laptop: queries p95 1.00 s (2,134 requests), commands
  p95 1.33 s (590 bin transfers), no errors, allocation over 100 lots 0.65 s. Commands meet the budget; queries sit at the 1 s
  budget on this hardware, so the staging run decides NFR-PERF.

## DEC-082 Web app for receive-to-dispatch, QA inbox and alerts; browser evidence (M6 slices 4-6, UI-001, UI-002, AT-UI-01)
- **Status:** Adopted. Covers the receive-to-dispatch screens, the QA approval inbox, alerts and the workspace switcher. Catalog and
  location editing, counts, transfers, returns, recalls, traceability, administration, audit and billing screens are still API-only
  and listed as UI gaps in the release evidence (UI-SCREENS stays Partial).
- **Sign-in:** OIDC authorization code with PKCE against the existing public client (`trustedinventory-pwa`); tokens only in this tab's
  sessionStorage. Signatures re-authenticate in a popup (`prompt=login`, `max_age=0`) and send the new ID token as `reauthToken`
  (DEC-009). The re-auth callback page loads its script from a file because the CSP forbids inline scripts.
- **Client behaviour (UI-001, UI-002):** every command gets one Idempotency-Key per user action, reused on retry after a timeout or
  network failure, so a retry returns the stored outcome instead of acting twice; a definite rejection draws a new key. Scans are
  shown as "scanned" and only the server's answer is shown as confirmed. Loading, empty, validation (field errors), unauthorized,
  offline and retry states, with the trace id on every failure. Keyboard-wedge scanning (type and Enter) everywhere; camera
  scanning through BarcodeDetector where the browser has it, falling back to typing when unavailable or refused. Short audible
  feedback for success and error. Workspace switching re-keys the whole view so nothing of the previous tenant stays shown.
  44 px touch targets, visible focus, labelled inputs; tables collapse to cards below 600 px.
- **Nothing is decided in the browser:** stock, eligibility, access and entitlements always come from the API (SUI-001).
- **Browser test (`web/e2e`, Playwright 1.64.1, `npm run test:e2e`):** against the Docker Compose stack, at handheld (390 px, touch)
  and desktop widths. API setup as alice, then in the browser: Keycloak sign-in with password and one-time code; receive with a
  synthetic GTIN (restricted-circulation range 02) registered through the API; a misread barcode first, refused by the server with
  `UNKNOWN_BARCODE`, the field and the trace id shown, then the valid scan; putaway with a wrong-product scan error, then success;
  offline banner; allocation; pick with a wrong-bin scan error, then success; dispatch; the ledger then shows 7 of 12 left. One
  browser at a time (`workers: 1`), because the shared synthetic user's one-time code is accepted once per 30-second window.
  Passed at both widths on 17 Nov 2026. Accessibility checks with an automated rule engine are not yet added.
- **Defects the first browser run found and fixed:** (1) screens resolved products and bins from one list page (`limit=500`, above
  the contract maximum of 200), so a tenant with more rows would see "Product" and could not scan a valid bin; putaway now loads the
  task's product by id, resolves a scanned bin through the server's bin search with an exact code match, and order work loads only
  the order warehouse's staging bins. (2) A refresh switched every list back to "loading", which unmounted the task that had just
  succeeded and lost its server confirmation; a refresh now keeps the shown data until the answer arrives, and putaway shows its
  confirmation at screen level because a finished task leaves the open list.

## DEC-083 Deployment checklist and release evidence pack (M6 slice 7, SEC-TRANSPORT, NFR-AVAIL, SOP-004, SOP-005, SUCCESS-02)
- **Status:** Adopted. `docs/deployment-checklist.md` lists every production item with its status (Ready, Needs environment
  evidence, Owner decision), the exact secret settings that stop startup when missing, the canary and rollback steps, and the
  owner's launch checklist. `docs/release-evidence.md` maps each gate and M6 requirement to its evidence and lists every gap.
- **Rollback compatibility (SOP-005):** migrations 0017 to 0029 are expand-only, so the previous build runs against the new schema;
  the earlier contract steps ran only after the code stopped needing the old shape (DEC-042, DEC-043). The canary and rollback
  drills themselves need the target environment.
- **What M6 cannot do from this repository:** approve production policies, choose region and provider (D06), configure the production
  identity provider (D02), or produce staging and production measurements. These stay owner and operations evidence.

## DEC-024 Audit chain and lock order
- **Status:** Adopted
- **Decision:** Each tenant has an `audit_stream` row; appending an audit event locks it, which
  serializes the per-tenant sequence inside the posting transaction (AUD-002). Event body is canonical
  JSON per RFC 8785; `event_hash = SHA-256(previous_hash || UTF-8 body)`.
- **Global lock order for stock commands:** document row → reservations → stock buckets (STK-005
  ordering) → audit stream. Because `ti_app` has no UPDATE on the ledger, a reversal locks the affected
  buckets rather than the original movement row.

---

## Owner launch decisions (spec section 21), still open

| ID | Default in force | Owner must confirm |
|----|------------------|--------------------|
| D01 release | R1A then R1B | Commercial release scope |
| D02 identity | External OIDC + MFA (Keycloak in development, DEC-002) | Production provider, account recovery |
| D03 approval | Two distinct people + signer re-auth; role bundles in `docs/permissions.md` | Authorized roles, delegated cover |
| D04 medical policies | Product-specific limits, immediate excursion hold, 300 s staleness | QA-approved profiles, device validation |
| D05 retention | Seven years | Jurisdictions, record classes, legal holds, deletion |
| D06 hosting | Managed PostgreSQL, encrypted object storage; backup retention 35 days proposed (`Lifecycle:BackupRetentionDays`, DEC-072) | Region, provider, failover, recovery budget, backup schedule |
| D07 integrations | Simulator only | Real sensors, gateway transport, GS1 samples |
| D08 operational limits | Zero over-receipt, 24 h reservation expiry, online-only mutations; request budgets 120/min per person per tenant and 600/min per service identity, two concurrent exports (DEC-073) | Suitability |
| D09 SaaS commercial | **Partly decided (DEC-067):** Paystack (test mode until live keys are approved), NGN, prices configurable in the platform catalog. Default still in force: 14-day trial, flat recurring subscription, fixture plans | Plan names, quotas, per-plan trials, taxes |
| D10 SaaS lifecycle | **Partly decided (DEC-067):** 7-day past-due grace and reminders on day 0, 3 and 6 approved. Default still in force: 90-day export window, retention overrides deletion | 90-day export window, retention over deletion |

## DEC-084 Compose stack configuration caught up with the code (M6, found by the browser run)
- **Status:** Adopted. The first `docker compose up` since S3 showed two start-up failures that the in-process test hosts could not
  see. Both are fixed without loosening a check.
- **API:** `Invitations:TokenKey` (required since M4, fail closed) was missing from the Compose settings and `.env.example`. It is now
  `TI_INVITATION_KEY` (placeholder only in the example; the real value lives in the untracked `.env`).
- **Worker:** the worker validates its service graph at start-up in Development, and the shared application services need the team
  adapters and a step-up re-authentication verifier that only the API registered. The worker now registers the team adapters and a
  `NoReauthVerifier` that always refuses (IAM-005): the worker serves no people, so a step-up action reached from a job fails
  instead of skipping the check. Service validation itself stays on.
- **Evidence:** the stack starts with all services up and AT-UI-01 passes against it. The browser test needs the API, so missing API
  settings fail it; the worker has no automated start-up check yet (known limitation): after `docker compose up`, confirm with
  `docker compose ps` that `worker` is running.

## DEC-085 Remaining screens of the inventory (M6, UI-SCREENS, AT-UI-02)
- **Status:** Adopted. Builds the rest of the section 17 screen inventory and the section 29 tenant surfaces on the existing API.
  No control moves into the browser: every screen calls the contract, the server checks every call, and the screen shows its
  refusal (code, field errors, trace id).
- **Screens:** catalog (products with barcodes and unit conversions, units, partners, storage profiles; retire, never delete);
  locations (warehouses, zones, bins); transfers (approve, dispatch, receive, transit write-off); blind counts (bin scan, variances,
  approval, post); adjustments with evidence upload; customer returns and inspection; rework and exception tasks; stock explorer with
  filters, paging and a per-line drilldown (history, hold, release, disposition, bin move) and open alerts on top; recalls (impact,
  outreach, QA closure); lot traceability with export; temperature devices (register, rotate credential with the secret shown once,
  retire) and excursions; team (members, role grants with warehouse scope, invitations); versioned settings; audit trail with chain
  verification and export; exports; job failures; billing (subscription, quota meters, plan preview before checkout, invoices,
  cancel and resume); checkout return; setup checklist; ownership transfer; workspace closure; restriction banner.
- **One approval pattern:** every action that needs a second person goes through one component that lists approvals for the
  subject, requests one bound to the exact version on screen with the server's action name, and passes the signed approval id to
  the command. A stale version is refused by the server (412) and shown.
- **Menu by permission:** the menu lists only routes whose list permission the member holds (from `/me`). This is navigation only;
  a typed URL still reaches the screen and the server refuses it. On narrow screens the menu sits behind a Menu button.
- **Checkout return:** the return URL carries no ids, so the checkout id is kept in session storage and the bounded checkout status is
  polled (20 times, 3 s apart). The page shows Pending until the server confirms; it never shows success from the redirect alone.
- **Task endpoints implemented:** the screen run found that `GET /tasks`, `GET /tasks/{id}` and `POST /tasks/{id}/complete`
  (contract since DEC-016) were never mapped. They now exist. Completing a rework or exception task needs `tasks.manage` in the
  task's warehouse, If-Match and a resolution, and is refused (409 `INVALID_STATE`) while the task's product and lot still have stock
  in its source bin, so a task cannot be closed with its stock left behind. Putaway tasks still complete only through putaway
  confirmation. Completing the last open task of a receipt closes the receipt, as putaway confirmation does (DEC-005). Nothing here
  moves stock. Rework tasks now name their outbound order and order line as the source instead of an empty receipt.
- **Request log status:** refusals raised as exceptions (for example a missing permission in the tenant middleware) were logged as
  "answered 200", because the log middleware ran inside the exception handler. It now wraps the handler and logs the status the
  caller received. ASP.NET's authorization log is kept at Information, so a refused policy names the failed requirement (no token
  is logged).
- **Rendering faults are contained:** each screen renders inside an error boundary, so an answer the screen did not expect shows a
  failure state while the header, menu and other screens keep working. Found when a queued export (manifest still null, as the
  contract allows) blanked the app; the export screen now reads the manifest as optional.
- **Browser evidence:** AT-UI-02 (`web/e2e/screens.spec.ts`) signs in through Keycloak, opens every screen in the menu and requires a
  heading, a settled load and no failure state, then adds a unit, a warehouse with a zone and bin, verifies the audit chain, queues an
  export and loads billing (labelled simulator) and the setup checklist. Test warehouses are retired by the next run so repeated runs
  stay within the plan's active-warehouse quota; alice gets the Auditor role through the API for the audit screens.
- **Known limitations:** the person receiving an ownership transfer accepts through a link the owner shares (no list endpoint for
  pending transfers); releasing returned medical goods and correcting a lot's expiry show the server's refusal, because approvals
  for those actions are not supported yet (M5 carry-over); most write paths of the new screens are exercised by the API integration
  tests, not yet in the browser; no automated accessibility rule checks; platform consoles and the public signup page are not built.
  A browser sign-in was refused as not multi-factor twice in about fifteen sign-ins on 17-18 Nov 2026 although the token showed a
  one-time code in every probe; not reproduced with logging on, and now diagnosable from the authorization log.

## DEC-086 Accessibility rule checks and browser evidence for screen write paths (M6, UI-001, UI-002, UI-SCREENS)
- **Status:** Adopted. Adds automated accessibility checks and browser tests for write paths that only had API evidence, and fixes
  what those runs found. No control moved: every check of a result goes back to the server.
- **Accessibility (AT-UI-03, `web/e2e/accessibility.spec.ts`):** axe-core (`@axe-core/playwright` 4.14.0; `axe-core` pinned to
  4.14.0 with an npm override because 4.14.1 was three days old) runs the WCAG 2.0, 2.1 and 2.2 A and AA rules on the signed-out
  start page, the open handheld menu, every screen in the menu with its collapsed forms opened, and four states behind an
  interaction: search picker matches, a stock line's drilldown, an order's work panel and a server refusal. Any violation fails the
  test, one line per rule and element. The engine was checked against a planted violation before it was trusted. A rule engine
  finds only part of the WCAG failures; a manual screen-reader and keyboard review is still owed before release.
- **Lost answer retried (UI-001, in AT-UI-01):** the receipt's answer is held past the screen's 15-second limit after the server
  has posted it. The screen shows "No answer yet"; "Try again" sends the same Idempotency-Key, the server returns the original
  outcome and the ledger check finds the goods received once.
- **Independent approval in two browsers (AT-UI-04, `web/e2e/approvals.spec.ts`):** alice requests an adjustment with an evidence
  file and asks for approval; her own inbox does not offer it (SOD-1); quinn, in a second browser, signs after re-authenticating
  in the Keycloak pop-up; alice posts it and the ledger changes once.
- **Administration writes (AT-UI-05, `web/e2e/administration.spec.ts`):** an invitation sent and revoked, a warehouse-scoped role
  granted and revoked, a settings version saved with a reason and kept in the history, a simulated device registered, its
  credential issued once and never shown again, and the device retired.
- **Export completion (AT-UI-02):** the export check now follows the queued export to Completed and downloads it through its
  short-lived link; it used to stop at "queued".
- **Defects found and fixed:**
  - The simulated object store volume was owned by root, so the non-root API and worker could not write: every attachment upload
    answered 500 and every export failed in the worker. The images now create `/data/objects-simulated` owned by the app user,
    which Docker copies into a new empty volume. An existing development volume keeps its old owner; remove it once
    (`docker compose down -v`, or `docker volume rm trustedinventory_objects` with api and worker stopped).
  - Re-authentication from the browser was always refused (`REAUTH_REQUIRED`): the pop-up's ID token is addressed to the web client,
    and the Compose stack did not list `trustedinventory-pwa` in `Auth:ReauthAudiences` (DEC-009 expects it; the deployment
    checklist already names the setting). No approval could be signed in the browser until now.
  - An approved adjustment could not be posted from its screen: "Approved" is derived (a Requested adjustment with a live signed
    approval, DEC-057) and the detail showed the approval step only for "Requested". It now shows it for both.
  - The order screen's Allocate and Dispatch buttons could be pressed before the order had loaded, sending version 0 (400
    `MALFORMED_REQUEST`). They stay disabled until the version is known.
  - Revoking an invitation or a role grant was always refused (400 `MALFORMED_REQUEST`): the contract requires a reason
    (`RevokeInvitationRequest`, `RevokeRoleGrantRequest`) and the screens sent no body. Both screens now ask for the reason and keep
    Revoke disabled until it is given. A check of every bodiless command in the screens against the contract found no other case.
  - The search picker's matches were marked `role="listbox"` around buttons, which assistive technology cannot use (axe
    `aria-required-children`, critical). They are now a plain labelled list of buttons.
- **Evidence:** AT-UI-01 to AT-UI-05 pass at handheld and desktop widths against the Compose stack on 19 Nov 2026, run file by file;
  a full-suite run was stopped for low memory on the workstation and is not counted.
- **Known limitations:** the e2e files are not covered by the web typecheck (Playwright strips types; no Node type package is added
  for them); transfers, counts, returns, tasks, recalls, traceability and the billing and workspace write paths are still proven by
  API integration tests only; platform consoles and the public signup page are not built.

## DEC-087 Browser evidence for the remaining write paths; contract gaps they exposed (M6, UI-SCREENS, API-16)
- **Status:** Adopted, except the identity provider change at the end, which is **proposed** and waits for the owner.
- **Browser tests:** AT-UI-06 (`web/e2e/transfers-counts.spec.ts`): a transfer created, approved, dispatched, part-received and its
  shortage written off with a second person's signature; a blind count with a scanned bin, a server-calculated variance, an
  independent approval and posting; the ledger agrees. AT-UI-07 (`web/e2e/returns-tasks.spec.ts`): a customer return received into
  quarantine, refused to the receiver at inspection (a QA duty) and released to storage by the QA officer; an exception task refused
  while its stock is in the staging bin, the stock moved from the stock drilldown, then the task completed. AT-UI-08
  (`web/e2e/recalls-traceability.spec.ts`): a lot put away in the browser with its lot scanned (a wrong lot refused first); a recall
  of the lot that blocks its stock, customer outreach, disposal of the recalled units still on hand and closure, each with another QA
  signature; the lot traced from receipt to customer; the owner's traceability export completes and the QA officer's is refused.
  AT-UI-09 (`web/e2e/billing-workspace.spec.ts`): billing against the labelled simulator (a cancellation needs a reason; the seeded
  subscription, never bought through the provider, is refused with the server's explanation; plans show that no price is approved
  for display, D09, so no checkout can start; the simulator portal link is issued) and an ownership offer made after re-authentication
  and withdrawn. Workspace closure is not driven in the browser: it would stop the shared synthetic tenant and the screen has no
  withdrawal (API evidence AT-LFC-01/02). Shared setup moved to `web/e2e/support.ts`, and each test now grants the roles it needs, so no
  test depends on another having run first (a fresh stack showed AT-UI-04 relied on AT-UI-01's grants).
  The setup helper now accepts only the "already granted" refusal: it had let every 409 through, which hid the server refusing alice
  QA signing for herself (`APPROVAL_SELF`, a correct control). AT-UI-08 gets a second QA signer the way a workspace would: another
  administrator grants the role. Playwright actions now time out after 60 s instead of waiting for the whole test limit.
- **Controls the runs confirmed (no change):** a receiver cannot inspect a return (`returns.inspect` is a QA duty); nobody grants
  themselves QA signing (`APPROVAL_SELF`); a recall cannot close while recalled stock is on hand (EXC-007); and a closure approval
  signed before the disposal is refused as stale (`APPROVAL_STALE`), because it is bound to the recall's content including its impact.
  AT-UI-08 therefore disposes of the stock first and asks for the closure approval afterwards.
- **For the owner:** the QA officer role has no `exports.create`, so QA cannot export a lot's traceability report during a recall;
  only the owner and the Auditor can (docs/permissions.md). The screens follow the matrix; whether QA should export is a product
  decision. The billing simulator's checkout and portal links point to a non-resolvable `.invalid` host by design, so no browser
  test can complete a payment in development; checkout and plan changes also wait for display-approved prices (D09).
- **Contract and implementation gaps found and fixed:**
  - `listLots`, `getLot` and `listSerials` were in the contract but never mapped (404): the recall lot picker, the traceability lot
    search and every lot name on screen failed. They are implemented read-only with `stock.read` in the tenant, as the traceability
    report is (DEC-078); number filters match a case-sensitive prefix (now stated in the contract); serials report version 1 and
    `updatedAt = createdAt` (append-only, as shipments, DEC-055); `expiryCorrected` stays false. Integration test
    `Lots_and_serials_are_listed_filtered_paged_and_read_within_the_tenant` covers filters, paging, cursor binding, ETag, 404 and a
    second tenant seeing nothing.
  - `ContractConformanceTests` only checked that implemented endpoints are in the contract. The new
    `Every_contract_operation_is_implemented` checks the reverse, with named exceptions: the two health checks (mapped without an
    endpoint name), `correctLotExpiry` (needs approvals for lot corrections, DEC-085) and `getInvoiceDownloadLink` (no invoice
    documents exist yet, S2). The billing screen no longer offers an invoice "PDF" button that could only fail.
  - `CreateCountRequest` listed `reason` as optional, but DEC-059 and the server require it; the screen labelled it optional and every
    count without a reason was refused. The contract now requires it and the screen asks for it. A comparison of every server reason
    rule with the contract found no other mismatch.
  - The putaway screen sent neither the scanned lot (required for lot-tracked tasks) nor a reason when the bin is not the top
    recommendation (INB-006), so both were always refused. It now asks for both.
  - The stock drilldown's Move tab prefilled the free quantity, which is 0 for staging or held stock that can still move. It now
    prefills the on-hand quantity only when nothing is reserved and otherwise leaves the field empty; the server checks it.
  - The recall and traceability screens hid a failed lot lookup; they now show it with a retry.
  - Cancelling the subscription sent no body, but the contract requires a reason (`CancelSubscriptionRequest`): every cancellation was
    refused. The screen asks for the reason. A second audit of every command sent without a body, now including plain-string paths,
    found no other case (the first audit, for revocations, missed plain-string paths).
- **Evidence:** on 19 Nov 2026, against a fresh Compose stack, AT-UI-01 to AT-UI-09 pass at handheld and desktop widths, run file by
  file; the release evidence pack lists the reruns and their causes. AT-UI-03 now creates its own stock line and confirmed order. Unit
  tests 290 pass; integration tests 291 pass on PostgreSQL 16 (the opt-in benchmark skipped).
- **Identity provider finding (proposed fix, not applied):** the intermittent "Sign-in must use multi-factor authentication" refusal
  noted in DEC-085 is explained. The development realm sets `default.reference.maxAge` to `0` on its `amr` references, so Keycloak
  names a sign-in method only in a token issued in the same second as the login step. Captured tokens: every token with
  `iat - auth_time = 0` carried `amr ["pwd","otp"]` and was accepted; both with `iat - auth_time = 1` carried `amr []` and were
  refused by the API's MFA requirement, which is the correct behaviour for such a token. Later traces showed the same split (three
  refusals in all, each with `amr []` one second after `auth_time`) and accepted tokens with only `amr ["otp"]`, where the password
  step had happened a second before the one-time code: each method drops out of the claim once a second has passed. One later refusal
  (DEC-088 signup run) had `amr []` with `iat - auth_time = 0`, so those two claims are only a proxy: the max age is measured from when
  each login step ran, which can be a second before both `auth_time` and `iat` are stamped. The proposed fix is unchanged. The proposal is to set the max age to the
  realm's session maximum (36000 s), so the methods a session used stay in its tokens; sensitive actions still need a
  re-authentication under 300 s old (DEC-009). This changes how MFA evidence is issued, so it waits for the owner. Until then, browser
  runs fail intermittently at sign-in, more often on a loaded machine. The browser test helper now reports a refused token's `amr`,
  `acr` and `iat` (never the token).

## DEC-088 Public product, plans and signup pages; creating another workspace (M6, section 29, SAS-003, ONB-001, ONB-002, ONB-007)
- **Status:** Adopted.
- **Routes:** `/` (product page when signed out), `/plans` and `/signup` are path routes served by the same web app (SAS-003); the
  workspace keeps its hash routes. Sign-in now remembers the full same-origin path to return to (`/signup`, later `/platform`); a
  return value that does not start with a single `/` is replaced by `/#/`, so it can never leave the site.
- **Plans:** read from the public `GET /saas/plans` with no token. A price appears only when the server marks it approved for display
  (section 28); otherwise the plan says "Pricing is not published yet". No price, badge or compliance claim is invented; the
  medical section says the customer must validate the controls for regulated use.
- **Signup:** asks the visitor to sign in first (the owner is always the signed-in, email-verified, MFA identity, ONB-001); takes
  business name, workspace address, billing country, plan and acceptance of the terms and privacy versions; submits once per attempt
  with an Idempotency-Key that a retry reuses (ONB-002); then polls the bounded provisioning status and offers to open the workspace
  when Ready, or shows the stable failure code and reference when Failed (ONB-007). A 403 explains email verification and MFA; a 429
  explains the daily limit. The terms and privacy versions are `draft-2026-11`, shown as drafts: the legal texts are launch
  dependencies the owner supplies, and the accepted version is recorded as the server already does.
- **Account creation** stays with the identity provider (registration is off in the development realm; D02); the page says accounts are
  managed by the organisation's sign-in provider.
- **Another workspace:** the workspace chooser links to `/signup` ("starts empty, with its own plan and subscription"); abuse review
  and trial eligibility stay on the server (ONB-003).
- **Development setting:** the Compose stack sets `Saas:Abuse:SignupsPerIdentityPerDay` to 50 so repeated browser runs can start real
  workspaces. Production keeps the default of 3 (ONB-007); `AbuseLimitTests` still prove the limit and its 429.
- **Evidence:** AT-UI-10 (`web/e2e/public-signup.spec.ts`) reads the product page and plans signed out, signs in from the signup page
  as bob, provisions a workspace to Ready, opens it and finds it in the chooser, with the WCAG A and AA rules checked on each page; it
  passes at handheld and desktop widths (one earlier attempt hit the DEC-087 sign-in refusal and showed the MFA guidance as designed).

## DEC-089 Platform operations and billing consoles (M6, section 29, SAS-003, SAS-004, SOD-8)
- **Status:** Adopted.
- **Surface:** `/platform`, a separate page of the web app against `/platform/v1`, with no tenant header and no workspace data (SAS-004).
  Tabs: Health (aggregates only), Tenants (lifecycle, access, plan; security suspension and lift, retention hold set and release,
  closure withdrawal, deletion approval, each with a reason and If-Match), Provisioning (failed jobs and retry), Alerts (acknowledge
  with a note), Plans (read for both roles; billing operators draft plan versions, add prices and publish), Billing tasks
  (reconciliation, unpaid invoices, disputes, refunds and chargebacks, resolved with a resolution) and Webhooks. Every tab renders
  inside the error boundary (DEC-085).
- **Roles stay apart:** the server decides each tab by the caller's platform list (`Platform:AdministratorSubjects` or
  `Platform:BillingSubjects`); the console only shows the refusal. A synthetic `billing-operator` (subject ...0005) is added to the
  development realm and configured as the billing operator, so the two consoles are exercised by two different people.
- **Prices:** typed amounts become integer minor units by string handling (exponent 2), never floating point. XTS is the test
  currency and is never shown to customers; publication remains the approval to display prices (DEC-067).
- **Not built here:** complimentary access (section 29: independent approval and expiry) has no contract or requirement yet; it is
  slice 8 of this round.
- **Found while building:** the console first rendered an alert's `detail` as text; the contract defines it as a map of counts, and
  rendering the object blanked the page. It now lists the counts, and the error boundary contains any such fault.
- **Evidence:** AT-UI-11 (`web/e2e/platform.spec.ts`): the operations administrator suspends and lifts a synthetic workspace,
  places and releases a retention hold, reads provisioning, alerts and plans, and is refused billing tasks; the billing operator is
  refused workspaces, drafts a plan version and adds an XTS price marked "Not shown", and reads billing tasks and webhooks; WCAG A
  and AA rules pass on each page; handheld and desktop widths, 20 Nov 2026 (one desktop attempt hit the DEC-087 sign-in refusal).

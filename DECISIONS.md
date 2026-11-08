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
| D06 hosting | Managed PostgreSQL, encrypted object storage | Region, provider, failover, recovery budget |
| D07 integrations | Simulator only | Real sensors, gateway transport, GS1 samples |
| D08 operational limits | Zero over-receipt, 24 h reservation expiry, online-only mutations | Suitability |
| D09 SaaS commercial | 14-day trial, flat recurring subscription, fixture plans, simulator provider (DEC-039, DEC-040) | Plan names, quotas, currency, prices, taxes, payment provider |
| D10 SaaS lifecycle | 7-day past-due grace, 90-day export window, retention overrides deletion | Approve before launch |

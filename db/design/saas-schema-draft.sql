-- TrustedInventory v3.0 SaaS control plane: schema DRAFT (design artefact, not a migration).
-- Applies on top of db/bootstrap/0000_cluster.sql + db/migrations/0001 + 0002. It is the design for the
-- S1/S2 migrations (DEC-032 to DEC-040). docs/migration-design.md §11 explains each step.
--
-- The file is split into sections. db/design/run-saas-smoke.ps1 applies each section separately, exactly
-- as production would: "superuser" sections are bootstrap addenda run once per cluster by the platform
-- operator; "migrator" sections run as ti_migrator with SET ROLE ti_owner, ONE TRANSACTION PER SECTION,
-- the way TrustedInventory.Migrator applies a migration file. Each migrator section maps to one future
-- migration file, named in its header.
--
-- Why separate files: ALTER TYPE ... ADD VALUE cannot be used by later statements in the same transaction,
-- so new enum labels (0003) must commit before constraints that mention them (0004).

-- @@ section bootstrap-addendum as superuser
-- Roles need a superuser (or CREATEROLE). Passwords are set by deployment, never here.
CREATE ROLE ti_control LOGIN NOBYPASSRLS NOINHERIT;      -- control plane: signup, provisioning, billing, webhooks
CREATE ROLE cp_access_reader NOLOGIN NOBYPASSRLS;        -- owns cp.tenant_access() only (mirrors ti_membership_reader)
GRANT cp_access_reader TO ti_owner;                      -- lets migrations hand the function over (DEC-028 pattern)

CREATE SCHEMA cp AUTHORIZATION ti_owner;
REVOKE ALL ON SCHEMA cp FROM PUBLIC;
GRANT USAGE ON SCHEMA cp TO ti_app, ti_control, cp_access_reader;
GRANT USAGE ON SCHEMA ti TO ti_control, cp_access_reader;
-- ti_platform deliberately gets no USAGE on cp: platform operations never read billing data (SAS-004).

-- @@ section 0003_saas_enum_values as migrator
-- New labels on existing types. Appended only, never reordered (docs/migration-design.md §3).
ALTER TYPE ti.role_name ADD VALUE IF NOT EXISTS 'TenantOwner';
ALTER TYPE ti.role_name ADD VALUE IF NOT EXISTS 'BillingAdministrator';
ALTER TYPE ti.role_name ADD VALUE IF NOT EXISTS 'PlatformBilling';
ALTER TYPE ti.export_type ADD VALUE IF NOT EXISTS 'TenantArchive';
ALTER TYPE ti.alert_type ADD VALUE IF NOT EXISTS 'ProvisioningFailed';
ALTER TYPE ti.alert_type ADD VALUE IF NOT EXISTS 'WebhookBacklog';
ALTER TYPE ti.alert_type ADD VALUE IF NOT EXISTS 'EntitlementMismatch';
ALTER TYPE ti.alert_type ADD VALUE IF NOT EXISTS 'BillingReconciliation';

-- @@ section 0004_saas_control_plane as migrator
-- EXPAND step for the tenant (DEC-033) plus every new table. M1 code keeps working: ti.tenant.status
-- stays and is kept in sync by a trigger until 0005 drops it.

-- ---------------------------------------------------------------------------
-- Types
-- ---------------------------------------------------------------------------
CREATE TYPE ti.tenant_lifecycle AS ENUM ('Provisioning','Ready','Archived','DeletionPending','Deleted');
CREATE TYPE ti.quota_key AS ENUM ('active_seats','active_warehouses','active_skus','active_devices','attachment_bytes');
CREATE TYPE ti.invitation_status AS ENUM ('Pending','Accepted','Revoked','Expired');
CREATE TYPE ti.usage_reservation_state AS ENUM ('Reserved','Committed','Released','Expired');
CREATE TYPE ti.ownership_transfer_status AS ENUM ('Pending','Accepted','Cancelled','Expired');

CREATE TYPE cp.subscription_state AS ENUM ('Trialing','Active','PastDue','Cancelled');
CREATE TYPE cp.subscription_change_state AS ENUM ('Pending','Scheduled','Blocked','Applied','Cancelled');
CREATE TYPE cp.billing_interval AS ENUM ('Month','Year');
CREATE TYPE cp.checkout_status AS ENUM ('Open','Completed','Expired');
CREATE TYPE cp.invoice_status AS ENUM ('Draft','Open','Paid','Void','Uncollectible');
CREATE TYPE cp.payment_status AS ENUM ('Pending','Succeeded','Failed','Refunded');
CREATE TYPE cp.provisioning_status AS ENUM ('Pending','Provisioning','Ready','Failed');
CREATE TYPE cp.provisioning_step AS ENUM ('AllocateTenant','CreateOwnerMembership','CreateBillingAccount',
                                          'AttachTrial','InitializeConfiguration','MarkReady');
CREATE TYPE cp.webhook_processing_state AS ENUM ('Received','Processed','Ignored','Quarantined','Failed');
CREATE TYPE cp.closure_status AS ENUM ('Requested','ExportWindow','Blocked','DeletionPending','Deleted','Withdrawn');
CREATE TYPE cp.terms_document AS ENUM ('Terms','Privacy','SubscriptionTerms');

-- Money (BIL-001): integer minor units with explicit ISO 4217 currency and exponent. No floats, no conversion.
CREATE DOMAIN cp.currency_code AS char(3) CHECK (VALUE ~ '^[A-Z]{3}$');
CREATE DOMAIN cp.minor_amount AS bigint CHECK (VALUE >= 0);
CREATE DOMAIN cp.currency_exponent AS smallint CHECK (VALUE BETWEEN 0 AND 4);
CREATE DOMAIN cp.provider_code AS text CHECK (VALUE ~ '^[a-z][a-z0-9_-]{1,31}$');
CREATE DOMAIN cp.provider_ref AS text CHECK (char_length(VALUE) BETWEEN 1 AND 255);

-- ---------------------------------------------------------------------------
-- Tenant: EXPAND (DEC-033). status stays readable for the M1 code path until 0005.
-- ---------------------------------------------------------------------------
ALTER TABLE ti.tenant
  ADD COLUMN lifecycle                 ti.tenant_lifecycle,
  ADD COLUMN slug                      text,
  ADD COLUMN home_region               text,
  ADD COLUMN ready_at                  timestamptz,
  ADD COLUMN archived_at               timestamptz,
  ADD COLUMN deletion_requested_at     timestamptz,
  ADD COLUMN deleted_at                timestamptz,
  ADD COLUMN retention_hold            boolean NOT NULL DEFAULT false,
  ADD COLUMN security_suspended_at     timestamptz,
  ADD COLUMN security_suspension_reason ti.reason_text,
  ADD COLUMN security_suspended_by     ti.subject_text,
  ADD COLUMN provisioning_job_id       uuid;

-- Slugs are navigation only (SAS-003) and globally unique. They are stored already normalised (lowercase
-- DNS-label style), and the unique index is on lower(slug) as a second guard.
ALTER TABLE ti.tenant ADD CONSTRAINT tenant_slug_format
  CHECK (slug IS NULL OR slug ~ '^[a-z0-9]([a-z0-9-]{1,61}[a-z0-9])$');
CREATE UNIQUE INDEX tenant_slug_uq ON ti.tenant (lower(slug));
ALTER TABLE ti.tenant ADD CONSTRAINT tenant_home_region_format
  CHECK (home_region IS NULL OR home_region ~ '^[a-z0-9][a-z0-9-]{1,31}$');
ALTER TABLE ti.tenant ADD CONSTRAINT tenant_security_suspension_complete
  CHECK ((security_suspended_at IS NULL) = (security_suspension_reason IS NULL)
     AND (security_suspended_at IS NULL) = (security_suspended_by IS NULL));
ALTER TABLE ti.tenant ADD CONSTRAINT tenant_ready_has_time
  CHECK (lifecycle IS NULL OR lifecycle = 'Provisioning' OR ready_at IS NOT NULL);

-- Backfill v2.0 rows: Active -> Ready; Suspended -> Ready + security suspension (the only suspension v2.0
-- had was a platform action, so it maps to the security reason, not to billing).
-- FORCE RLS hides every tenant row from ti_owner too (no owner policy, TEN-004), so a data backfill must lift
-- FORCE for this one table inside the migration transaction and restore it before commit. Only the migration
-- identity can do this; the schema conformance test checks FORCE is back on every table.
ALTER TABLE ti.tenant NO FORCE ROW LEVEL SECURITY;
UPDATE ti.tenant SET
  lifecycle   = 'Ready',
  ready_at    = created_at,
  home_region = 'default',
  slug        = 'tenant-' || left(replace(id::text, '-', ''), 12),
  security_suspended_at      = CASE WHEN status = 'Suspended' THEN updated_at END,
  security_suspension_reason = CASE WHEN status = 'Suspended' THEN 'Migrated from v2.0 suspension' END,
  security_suspended_by      = CASE WHEN status = 'Suspended' THEN 'system:migration-0004' END;
ALTER TABLE ti.tenant FORCE ROW LEVEL SECURITY;

-- Keep the legacy column in sync while both code paths are deployed (expand/contract).
CREATE FUNCTION ti.tenant_sync_legacy_status() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' AND NEW.lifecycle IS NULL THEN
    -- Old code inserting through the v2.0 path.
    NEW.lifecycle := 'Ready';
    NEW.ready_at := coalesce(NEW.ready_at, NEW.created_at);
    NEW.home_region := coalesce(NEW.home_region, 'default');
    NEW.slug := coalesce(NEW.slug, 'tenant-' || left(replace(NEW.id::text, '-', ''), 12));
  END IF;
  IF TG_OP = 'UPDATE' AND NEW.status IS DISTINCT FROM OLD.status THEN
    -- Old code suspending/reactivating: mirror into the security suspension.
    IF NEW.status = 'Suspended' AND NEW.security_suspended_at IS NULL THEN
      NEW.security_suspended_at := now();
      NEW.security_suspension_reason := 'Suspended through the v2.0 platform route';
      NEW.security_suspended_by := 'system:legacy-route';
    ELSIF NEW.status = 'Active' THEN
      NEW.security_suspended_at := NULL;
      NEW.security_suspension_reason := NULL;
      NEW.security_suspended_by := NULL;
    END IF;
  ELSE
    NEW.status := CASE WHEN NEW.security_suspended_at IS NULL THEN 'Active' ELSE 'Suspended' END::ti.tenant_status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER tenant_sync_legacy_status BEFORE INSERT OR UPDATE ON ti.tenant
  FOR EACH ROW EXECUTE FUNCTION ti.tenant_sync_legacy_status();

-- Signup-time roles are never grantable through role-grant routes (DEC-036, SOD-5/SOD-6 enforced in the
-- application; the database blocks the platform-only labels outright).
ALTER TABLE ti.role_grant ADD CONSTRAINT role_grant_not_platform_billing CHECK (role <> 'PlatformBilling');

-- ---------------------------------------------------------------------------
-- Tenant-scoped SaaS tables in schema ti (managed through tenant requests as ti_app, RLS-isolated)
-- ---------------------------------------------------------------------------

-- Invitations live in ti, not cp: tenant administrators create, list and revoke them inside their own
-- tenant transaction, so they need the same RLS isolation as memberships. Acceptance happens before the
-- invitee is a member (no tenant context), so ti_control performs the token lookup and the membership
-- insert (ONB-005). Only the SHA-256 of the token is stored.
CREATE TABLE ti.invitation (
  id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id           uuid NOT NULL REFERENCES ti.tenant(id),
  email_normalized    text NOT NULL CHECK (email_normalized = lower(btrim(email_normalized))
                                           AND email_normalized ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$'
                                           AND char_length(email_normalized) <= 320),
  token_hash          ti.sha256 NOT NULL UNIQUE,
  roles               ti.role_name[] NOT NULL CHECK (cardinality(roles) BETWEEN 1 AND 10),
  warehouse_ids       uuid[] NOT NULL DEFAULT '{}',
  status              ti.invitation_status NOT NULL DEFAULT 'Pending',
  expires_at          timestamptz NOT NULL,
  invited_by          ti.subject_text NOT NULL,
  accepted_by_subject ti.subject_text,
  accepted_at         timestamptz,
  membership_id       uuid,
  revoked_at          timestamptz,
  revoked_by          ti.subject_text,
  created_at          timestamptz NOT NULL DEFAULT now(),
  created_by          ti.subject_text NOT NULL,
  updated_at          timestamptz NOT NULL DEFAULT now(),
  version             integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, membership_id) REFERENCES ti.membership (tenant_id, id),
  CHECK (expires_at > created_at AND expires_at <= created_at + interval '72 hours'),     -- ONB-005
  CHECK (NOT (roles && ARRAY['PlatformAdministrator','PlatformBilling','TenantOwner','TelemetryIntegration']::ti.role_name[])),
  CHECK ((status = 'Accepted') = (accepted_at IS NOT NULL AND accepted_by_subject IS NOT NULL AND membership_id IS NOT NULL)),
  CHECK ((status = 'Revoked') = (revoked_at IS NOT NULL AND revoked_by IS NOT NULL))
);
-- At most one live invitation per address per tenant.
CREATE UNIQUE INDEX invitation_pending_uq ON ti.invitation (tenant_id, email_normalized) WHERE status = 'Pending';
CREATE INDEX invitation_expiry_ix ON ti.invitation (expires_at) WHERE status = 'Pending';

-- Ownership transfer: tenant-scoped like invitations, because the current owner starts it in a tenant
-- request and acceptance changes role grants in the same tenant transaction (ONB-006, SOD-5).
CREATE TABLE ti.ownership_transfer (
  id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id           uuid NOT NULL REFERENCES ti.tenant(id),
  from_subject        ti.subject_text NOT NULL,
  to_membership_id    uuid NOT NULL,
  to_subject          ti.subject_text NOT NULL,
  status              ti.ownership_transfer_status NOT NULL DEFAULT 'Pending',
  reauth_reference    ti.sha256 NOT NULL,                  -- digest of the owner's fresh ID token (DEC-009)
  expires_at          timestamptz NOT NULL,
  accepted_at         timestamptz,
  cancelled_at        timestamptz,
  created_at          timestamptz NOT NULL DEFAULT now(),
  created_by          ti.subject_text NOT NULL,
  updated_at          timestamptz NOT NULL DEFAULT now(),
  version             integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, to_membership_id, to_subject) REFERENCES ti.membership (tenant_id, id, user_subject),
  CHECK (from_subject <> to_subject),
  CHECK ((status = 'Accepted') = (accepted_at IS NOT NULL))
);
CREATE UNIQUE INDEX ownership_transfer_pending_uq ON ti.ownership_transfer (tenant_id) WHERE status = 'Pending';

-- Quota usage (ENT-003). Locked FOR UPDATE and incremented in the same transaction that creates the
-- counted resource; the limit comes from the current entitlement snapshot read in that transaction.
CREATE TABLE ti.usage_counter (
  tenant_id   uuid NOT NULL REFERENCES ti.tenant(id),
  metric      ti.quota_key NOT NULL,
  used        bigint NOT NULL DEFAULT 0 CHECK (used >= 0),
  version     integer NOT NULL DEFAULT 1 CHECK (version > 0),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, metric)
);

-- Attachment bytes are reserved before upload and released on expiry or failure (ENT-003).
CREATE TABLE ti.usage_reservation (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id   uuid NOT NULL REFERENCES ti.tenant(id),
  metric      ti.quota_key NOT NULL,
  amount      bigint NOT NULL CHECK (amount > 0),
  state       ti.usage_reservation_state NOT NULL DEFAULT 'Reserved',
  reference   text CHECK (reference IS NULL OR char_length(reference) <= 200),
  expires_at  timestamptz NOT NULL,
  created_at  timestamptz NOT NULL DEFAULT now(),
  created_by  ti.subject_text NOT NULL,
  updated_at  timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, id)
);
CREATE INDEX usage_reservation_expiry_ix ON ti.usage_reservation (expires_at) WHERE state = 'Reserved';

-- ---------------------------------------------------------------------------
-- Control plane: schema cp (SAS-004, DEC-035)
-- ---------------------------------------------------------------------------

-- Plan catalog. Not tenant-scoped. Immutable once published (ENT-001).
CREATE TABLE cp.plan_version (
  id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  plan_code     text NOT NULL CHECK (plan_code ~ '^[a-z][a-z0-9-]{1,39}$'),
  version       integer NOT NULL CHECK (version > 0),
  display_name  ti.name_text NOT NULL,
  features      text[] NOT NULL,
  quotas        jsonb NOT NULL CHECK (jsonb_typeof(quotas) = 'object'),
  is_public     boolean NOT NULL DEFAULT true,       -- false: contract-only catalog entries, hidden from /saas/plans
  published_at  timestamptz,
  created_at    timestamptz NOT NULL DEFAULT now(),
  created_by    ti.subject_text NOT NULL,
  UNIQUE (plan_code, version),
  CHECK (features <@ ARRAY['core_inventory','service_api','advanced_reporting','medical_storage','telemetry']),
  CHECK (quotas ?& ARRAY['active_seats','active_warehouses','active_skus','active_devices','attachment_bytes'])
);

CREATE TABLE cp.price_version (
  id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  plan_version_id     uuid NOT NULL REFERENCES cp.plan_version(id),
  currency            cp.currency_code NOT NULL,
  minor_unit_amount   cp.minor_amount NOT NULL,
  exponent            cp.currency_exponent NOT NULL,
  billing_interval    cp.billing_interval NOT NULL,
  active_from         timestamptz NOT NULL,
  active_to           timestamptz,
  provider            cp.provider_code,
  provider_price_ref  cp.provider_ref,
  created_at          timestamptz NOT NULL DEFAULT now(),
  created_by          ti.subject_text NOT NULL,
  CHECK (active_to IS NULL OR active_to > active_from),
  CHECK ((provider IS NULL) = (provider_price_ref IS NULL)),
  UNIQUE (provider, provider_price_ref)
);

CREATE TABLE cp.billing_account (
  id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id             uuid NOT NULL UNIQUE REFERENCES ti.tenant(id),       -- SAS-001: one per tenant
  billing_contact_email text NOT NULL CHECK (billing_contact_email = lower(btrim(billing_contact_email))
                                             AND char_length(billing_contact_email) <= 320),
  business_legal_name   ti.name_text NOT NULL,
  country               char(2) NOT NULL CHECK (country ~ '^[A-Z]{2}$'),
  tax_reference         text CHECK (tax_reference IS NULL OR char_length(tax_reference) <= 64), -- column-restricted
  provider              cp.provider_code,
  provider_customer_id  cp.provider_ref,
  created_at            timestamptz NOT NULL DEFAULT now(),
  created_by            ti.subject_text NOT NULL,
  updated_at            timestamptz NOT NULL DEFAULT now(),
  version               integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (provider, provider_customer_id),
  CHECK ((provider IS NULL) = (provider_customer_id IS NULL))
);

CREATE TABLE cp.subscription (
  id                         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id                  uuid NOT NULL UNIQUE REFERENCES ti.tenant(id),  -- SAS-001: one subscription
  billing_account_id         uuid NOT NULL,
  plan_version_id            uuid NOT NULL REFERENCES cp.plan_version(id),
  price_version_id           uuid REFERENCES cp.price_version(id),        -- NULL while Trialing
  state                      cp.subscription_state NOT NULL,
  trial_start                timestamptz,
  trial_end                  timestamptz,
  period_start               timestamptz,
  period_end                 timestamptz,
  grace_end                  timestamptz,
  cancel_at_period_end       boolean NOT NULL DEFAULT false,
  cancelled_at               timestamptz,
  provider                   cp.provider_code,
  provider_subscription_ref  cp.provider_ref,
  provider_revision          bigint,           -- monotonic provider ordering, when the provider has one (BIL-005)
  revision                   bigint NOT NULL DEFAULT 1 CHECK (revision > 0),   -- internal, bumped on every change
  created_at                 timestamptz NOT NULL DEFAULT now(),
  updated_at                 timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, billing_account_id) REFERENCES cp.billing_account (tenant_id, id),
  UNIQUE (provider, provider_subscription_ref),
  CHECK ((provider IS NULL) = (provider_subscription_ref IS NULL)),
  CHECK (state <> 'Trialing' OR (trial_start IS NOT NULL AND trial_end > trial_start)),
  CHECK (state <> 'Active'   OR (price_version_id IS NOT NULL AND period_start IS NOT NULL AND period_end > period_start)),
  CHECK (state <> 'PastDue'  OR (period_end IS NOT NULL AND grace_end IS NOT NULL)),
  CHECK (state <> 'Cancelled' OR cancelled_at IS NOT NULL)
);
CREATE INDEX subscription_trial_end_ix ON cp.subscription (trial_end) WHERE state = 'Trialing';
CREATE INDEX subscription_grace_end_ix ON cp.subscription (grace_end) WHERE state = 'PastDue';
CREATE INDEX subscription_period_end_ix ON cp.subscription (period_end) WHERE state IN ('Active','PastDue');

-- Initial and renewal-change checkouts. A provider return URL never completes a session (BIL-002).
CREATE TABLE cp.checkout_session (
  id                        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id                 uuid NOT NULL REFERENCES ti.tenant(id),
  billing_account_id        uuid NOT NULL,
  price_version_id          uuid NOT NULL REFERENCES cp.price_version(id),
  status                    cp.checkout_status NOT NULL DEFAULT 'Open',
  accepted_preview_digest   ti.sha256 NOT NULL,                 -- BIL-003 consent evidence
  idempotency_key           uuid NOT NULL,
  provider                  cp.provider_code NOT NULL,
  provider_session_ref      cp.provider_ref,
  hosted_url                text CHECK (hosted_url IS NULL OR hosted_url ~ '^https://'),
  expires_at                timestamptz NOT NULL,
  completed_at              timestamptz,
  created_at                timestamptz NOT NULL DEFAULT now(),
  created_by                ti.subject_text NOT NULL,
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, idempotency_key),
  UNIQUE (provider, provider_session_ref),
  FOREIGN KEY (tenant_id, billing_account_id) REFERENCES cp.billing_account (tenant_id, id),
  CHECK ((status = 'Completed') = (completed_at IS NOT NULL))
);
CREATE UNIQUE INDEX checkout_one_open_uq ON cp.checkout_session (tenant_id) WHERE status = 'Open';

CREATE TABLE cp.subscription_change (
  id                       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id                uuid NOT NULL REFERENCES ti.tenant(id),
  subscription_id          uuid NOT NULL,
  target_plan_version_id   uuid NOT NULL REFERENCES cp.plan_version(id),
  target_price_version_id  uuid NOT NULL REFERENCES cp.price_version(id),
  state                    cp.subscription_change_state NOT NULL DEFAULT 'Pending',
  requested_by             ti.subject_text NOT NULL,
  requested_at             timestamptz NOT NULL DEFAULT now(),
  effective_at             timestamptz NOT NULL,
  preview_digest           ti.sha256 NOT NULL,
  acceptance               jsonb NOT NULL,          -- who accepted which preview, when (BIL-003)
  blockers                 jsonb,                   -- usage/medical blockers when Blocked (ENT-006)
  applied_at               timestamptz,
  created_at               timestamptz NOT NULL DEFAULT now(),
  updated_at               timestamptz NOT NULL DEFAULT now(),
  version                  integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, subscription_id) REFERENCES cp.subscription (tenant_id, id),
  CHECK ((state = 'Applied') = (applied_at IS NOT NULL)),
  CHECK (state <> 'Blocked' OR blockers IS NOT NULL)
);
CREATE UNIQUE INDEX subscription_change_open_uq ON cp.subscription_change (subscription_id)
  WHERE state IN ('Pending','Scheduled');
CREATE INDEX subscription_change_due_ix ON cp.subscription_change (effective_at) WHERE state = 'Scheduled';

-- Exactly one current snapshot per tenant (ENT-002); history kept for audit.
CREATE TABLE cp.entitlement_snapshot (
  id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id              uuid NOT NULL REFERENCES ti.tenant(id),
  subscription_revision  bigint NOT NULL CHECK (subscription_revision > 0),
  entitlement_version    integer NOT NULL CHECK (entitlement_version > 0),
  plan_version_id        uuid NOT NULL REFERENCES cp.plan_version(id),
  features               text[] NOT NULL,
  limits                 jsonb NOT NULL CHECK (jsonb_typeof(limits) = 'object'),
  effective_from         timestamptz NOT NULL,
  effective_to           timestamptz,
  is_current             boolean NOT NULL,
  created_at             timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, entitlement_version),
  CHECK (effective_to IS NULL OR effective_to > effective_from),
  CHECK (features <@ ARRAY['core_inventory','service_api','advanced_reporting','medical_storage','telemetry'])
);
CREATE UNIQUE INDEX entitlement_snapshot_current_uq ON cp.entitlement_snapshot (tenant_id) WHERE is_current;

CREATE TABLE cp.invoice (
  id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id             uuid NOT NULL REFERENCES ti.tenant(id),
  billing_account_id    uuid NOT NULL,
  subscription_id       uuid,
  provider              cp.provider_code NOT NULL,
  provider_invoice_ref  cp.provider_ref NOT NULL,
  number                text CHECK (number IS NULL OR char_length(number) <= 64),
  currency              cp.currency_code NOT NULL,
  exponent              cp.currency_exponent NOT NULL,
  lines                 jsonb NOT NULL CHECK (jsonb_typeof(lines) = 'array'),   -- snapshot (BIL-001)
  subtotal              cp.minor_amount NOT NULL,
  discount              cp.minor_amount NOT NULL DEFAULT 0,
  tax                   cp.minor_amount NOT NULL DEFAULT 0,
  total                 cp.minor_amount NOT NULL,
  period_start          timestamptz,
  period_end            timestamptz,
  status                cp.invoice_status NOT NULL,
  due_at                timestamptz,
  paid_at               timestamptz,
  provider_revision     bigint,
  created_at            timestamptz NOT NULL DEFAULT now(),
  updated_at            timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, id),
  UNIQUE (provider, provider_invoice_ref),
  FOREIGN KEY (tenant_id, billing_account_id) REFERENCES cp.billing_account (tenant_id, id),
  FOREIGN KEY (tenant_id, subscription_id) REFERENCES cp.subscription (tenant_id, id),
  CHECK (total = subtotal - discount + tax),
  CHECK (discount <= subtotal),
  CHECK (period_end IS NULL OR period_end > period_start),
  CHECK ((status = 'Paid') = (paid_at IS NOT NULL))
);
CREATE INDEX invoice_unpaid_ix ON cp.invoice (due_at) WHERE status IN ('Open','Uncollectible');

CREATE TABLE cp.payment (
  id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id             uuid NOT NULL REFERENCES ti.tenant(id),
  invoice_id            uuid NOT NULL,
  provider              cp.provider_code NOT NULL,
  provider_payment_ref  cp.provider_ref NOT NULL,
  provider_event_id     cp.provider_ref,
  status                cp.payment_status NOT NULL,
  currency              cp.currency_code NOT NULL,
  exponent              cp.currency_exponent NOT NULL,
  amount                cp.minor_amount NOT NULL,
  refund_of_id          uuid,                       -- refunds are separate, referenced records (section 27)
  provider_occurred_at  timestamptz NOT NULL,       -- provider event time
  recorded_at           timestamptz NOT NULL DEFAULT now(),   -- internal processing time
  UNIQUE (tenant_id, id),
  UNIQUE (provider, provider_payment_ref),
  FOREIGN KEY (tenant_id, invoice_id) REFERENCES cp.invoice (tenant_id, id),
  FOREIGN KEY (tenant_id, refund_of_id) REFERENCES cp.payment (tenant_id, id),
  CHECK (refund_of_id IS NULL OR status = 'Refunded')
);

-- Webhook inbox: not tenant-scoped (events arrive without tenant context). Payloads are stored encrypted
-- by the application; only a redacted summary is queryable. Never log full payloads (section 27).
CREATE TABLE cp.webhook_inbox (
  id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  provider             cp.provider_code NOT NULL,
  event_id             cp.provider_ref NOT NULL,
  event_type           text NOT NULL CHECK (char_length(event_type) BETWEEN 1 AND 100),
  provider_created_at  timestamptz,
  received_at          timestamptz NOT NULL DEFAULT now(),
  raw_body_digest      ti.sha256 NOT NULL,
  payload_encrypted    bytea NOT NULL,
  payload_redacted     jsonb,
  tenant_id            uuid REFERENCES ti.tenant(id),         -- resolved from verified references, never from the payload's claim
  processing_state     cp.webhook_processing_state NOT NULL DEFAULT 'Received',
  attempts             integer NOT NULL DEFAULT 0 CHECK (attempts BETWEEN 0 AND 10),
  next_attempt_at      timestamptz NOT NULL DEFAULT now(),
  lease_owner          text,
  lease_expires_at     timestamptz,
  last_error           text CHECK (last_error IS NULL OR char_length(last_error) <= 2000),
  processed_at         timestamptz,
  UNIQUE (provider, event_id),                                 -- BIL-004 deduplication
  CHECK ((processing_state IN ('Processed','Ignored')) = (processed_at IS NOT NULL))
);
CREATE INDEX webhook_inbox_pending_ix ON cp.webhook_inbox (next_attempt_at) WHERE processing_state IN ('Received','Failed');

-- Provisioning job (ONB-002, AT-ONB-01). signup_key = the signup request's Idempotency-Key scoped to the
-- verified subject, so retries of the same signup resolve to this job.
CREATE TABLE cp.provisioning_job (
  id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  signup_key             text NOT NULL UNIQUE CHECK (char_length(signup_key) BETWEEN 1 AND 300),
  requested_by_subject   ti.subject_text NOT NULL,
  request_digest         ti.sha256 NOT NULL,         -- same key with a different body is IDEMPOTENCY_MISMATCH
  request                jsonb NOT NULL,             -- business name, slug, country, plan version, terms versions
  tenant_id              uuid NOT NULL UNIQUE,       -- allocated up front; tenant row created by step 1
  status                 cp.provisioning_status NOT NULL DEFAULT 'Pending',
  current_step           cp.provisioning_step,
  completed_steps        cp.provisioning_step[] NOT NULL DEFAULT '{}',
  step_outcomes          jsonb NOT NULL DEFAULT '{}'::jsonb,
  failure_code           text CHECK (failure_code IS NULL OR char_length(failure_code) <= 64),
  attempts               integer NOT NULL DEFAULT 0 CHECK (attempts BETWEEN 0 AND 10),
  next_attempt_at        timestamptz NOT NULL DEFAULT now(),
  lease_owner            text,
  lease_expires_at       timestamptz,
  ready_at               timestamptz,
  abuse_review           boolean NOT NULL DEFAULT false,   -- ONB-003 repeated-trial flag
  created_at             timestamptz NOT NULL DEFAULT now(),
  updated_at             timestamptz NOT NULL DEFAULT now(),
  CHECK ((status = 'Ready') = (ready_at IS NOT NULL)),
  CHECK (status <> 'Failed' OR failure_code IS NOT NULL)
);
CREATE INDEX provisioning_job_runnable_ix ON cp.provisioning_job (next_attempt_at) WHERE status IN ('Pending','Provisioning');
ALTER TABLE ti.tenant ADD CONSTRAINT tenant_provisioning_job_fk
  FOREIGN KEY (provisioning_job_id) REFERENCES cp.provisioning_job(id);

-- Terms acceptance (ONB-001, BIL-003). Append-only evidence.
CREATE TABLE cp.terms_acceptance (
  id                       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  subject                  ti.subject_text NOT NULL,
  tenant_id                uuid REFERENCES ti.tenant(id),
  provisioning_job_id      uuid REFERENCES cp.provisioning_job(id),
  document                 cp.terms_document NOT NULL,
  document_version         text NOT NULL CHECK (char_length(document_version) BETWEEN 1 AND 64),
  accepted_at              timestamptz NOT NULL,
  checkout_preview_digest  ti.sha256,
  created_at               timestamptz NOT NULL DEFAULT now(),
  CHECK (tenant_id IS NOT NULL OR provisioning_job_id IS NOT NULL)
);
CREATE INDEX terms_acceptance_tenant_ix ON cp.terms_acceptance (tenant_id);

-- Closure and deletion register (LFC-002, LFC-004). A backup restore replays every Deleted row before any
-- tenant access is opened. The tenant row itself is kept, minimal, with lifecycle Deleted.
CREATE TABLE cp.deletion_record (
  id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id              uuid NOT NULL REFERENCES ti.tenant(id),
  status                 cp.closure_status NOT NULL DEFAULT 'Requested',
  requested_by           ti.subject_text NOT NULL,
  requested_at           timestamptz NOT NULL DEFAULT now(),
  reason                 ti.reason_text NOT NULL,
  reauth_reference       ti.sha256 NOT NULL,
  deletion_requested     boolean NOT NULL,
  export_window_ends_at  timestamptz,
  approved_by            ti.subject_text,
  approved_at            timestamptz,
  scope                  jsonb,
  hold_results           jsonb,        -- controlled stock / recall / legal-hold blockers found (RETENTION_BLOCKED)
  completed_at           timestamptz,
  backup_expiry_at       timestamptz,  -- when the last backup holding the data ages out (no immediate-erasure claim)
  created_at             timestamptz NOT NULL DEFAULT now(),
  updated_at             timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, id),
  CHECK ((status = 'Deleted') = (completed_at IS NOT NULL)),
  CHECK (status <> 'Deleted' OR (approved_at IS NOT NULL AND backup_expiry_at IS NOT NULL)),
  CHECK (status <> 'Blocked' OR hold_results IS NOT NULL)
);
CREATE UNIQUE INDEX deletion_record_open_uq ON cp.deletion_record (tenant_id)
  WHERE status IN ('Requested','ExportWindow','Blocked','DeletionPending');

-- ---------------------------------------------------------------------------
-- Price immutability once referenced (ENT-001)
-- ---------------------------------------------------------------------------
CREATE FUNCTION cp.price_version_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF EXISTS (SELECT 1 FROM cp.subscription WHERE price_version_id = OLD.id)
     OR EXISTS (SELECT 1 FROM cp.checkout_session WHERE price_version_id = OLD.id)
     OR EXISTS (SELECT 1 FROM cp.subscription_change WHERE target_price_version_id = OLD.id) THEN
    IF TG_OP = 'DELETE' THEN
      RAISE EXCEPTION 'price_version % is referenced and cannot be deleted', OLD.id USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    -- Only closing the availability window is allowed on a referenced price.
    IF (NEW.plan_version_id, NEW.currency, NEW.minor_unit_amount, NEW.exponent, NEW.billing_interval,
        NEW.active_from, NEW.provider, NEW.provider_price_ref)
       IS DISTINCT FROM
       (OLD.plan_version_id, OLD.currency, OLD.minor_unit_amount, OLD.exponent, OLD.billing_interval,
        OLD.active_from, OLD.provider, OLD.provider_price_ref) THEN
      RAISE EXCEPTION 'price_version % is referenced and immutable', OLD.id USING ERRCODE = 'integrity_constraint_violation';
    END IF;
  END IF;
  RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END $$;
CREATE TRIGGER price_version_immutable BEFORE UPDATE OR DELETE ON cp.price_version
  FOR EACH ROW EXECUTE FUNCTION cp.price_version_guard();

-- Published plans are immutable (ENT-001): new terms mean a new version.
CREATE FUNCTION cp.plan_version_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.published_at IS NOT NULL THEN
    RAISE EXCEPTION 'plan_version % is published and immutable', OLD.id USING ERRCODE = 'integrity_constraint_violation';
  END IF;
  RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END $$;
CREATE TRIGGER plan_version_immutable BEFORE UPDATE OR DELETE ON cp.plan_version
  FOR EACH ROW EXECUTE FUNCTION cp.plan_version_guard();

-- Terms acceptance is evidence: append-only (reuses 0001's reject_mutation).
CREATE TRIGGER terms_acceptance_append_only BEFORE UPDATE OR DELETE ON cp.terms_acceptance
  FOR EACH ROW EXECUTE FUNCTION ti.reject_mutation();

-- ---------------------------------------------------------------------------
-- Row-level security
-- ---------------------------------------------------------------------------
-- New tenant-scoped ti tables: same tenant_isolation policy as 0001 (TEN-002), plus control-plane access
-- where provisioning or invitation acceptance needs it.
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['invitation','ownership_transfer','usage_counter','usage_reservation'] LOOP
    EXECUTE format('ALTER TABLE ti.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE ti.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format($p$CREATE POLICY tenant_isolation ON ti.%I TO ti_app
                     USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                     WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)$p$, t);
    EXECUTE format('GRANT SELECT, INSERT, UPDATE ON ti.%I TO ti_app', t);
  END LOOP;
END $$;

-- ti_control: provisioning creates tenants, owner memberships, grants, audit streams and usage counters;
-- invitation acceptance reads the invitation by token hash. No policy or grant on any stock/catalog table.
CREATE POLICY control_plane ON ti.tenant TO ti_control USING (true) WITH CHECK (true);
CREATE POLICY control_plane ON ti.membership TO ti_control USING (true) WITH CHECK (true);
CREATE POLICY control_plane ON ti.role_grant TO ti_control USING (true) WITH CHECK (true);
CREATE POLICY control_plane ON ti.audit_stream TO ti_control USING (true) WITH CHECK (true);
CREATE POLICY control_plane_insert ON ti.audit_event FOR INSERT TO ti_control WITH CHECK (true);
CREATE POLICY control_plane_read ON ti.audit_event FOR SELECT TO ti_control USING (true);
CREATE POLICY control_plane ON ti.invitation TO ti_control USING (true) WITH CHECK (true);
CREATE POLICY control_plane ON ti.usage_counter TO ti_control USING (true) WITH CHECK (true);
GRANT SELECT, INSERT, UPDATE ON ti.tenant, ti.membership, ti.role_grant, ti.audit_stream, ti.usage_counter TO ti_control;
GRANT SELECT, INSERT ON ti.audit_event TO ti_control;
GRANT SELECT, UPDATE ON ti.invitation TO ti_control;

-- cp: every table forced RLS. ti_control has full access; ti_app may only SELECT its own tenant's
-- tenant-facing billing rows (application gates these with billing.read) and published catalog entries.
DO $$
DECLARE t text;
BEGIN
  FOR t IN SELECT tablename FROM pg_tables WHERE schemaname = 'cp' LOOP
    EXECUTE format('ALTER TABLE cp.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE cp.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY control_plane ON cp.%I TO ti_control USING (true) WITH CHECK (true)', t);
    EXECUTE format('GRANT SELECT, INSERT, UPDATE ON cp.%I TO ti_control', t);
  END LOOP;

  FOREACH t IN ARRAY ARRAY['subscription','invoice','payment','entitlement_snapshot','checkout_session',
                           'subscription_change','billing_account'] LOOP
    EXECUTE format($p$CREATE POLICY tenant_read ON cp.%I FOR SELECT TO ti_app
                     USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)$p$, t);
  END LOOP;
END $$;
GRANT SELECT ON cp.subscription, cp.invoice, cp.payment, cp.entitlement_snapshot, cp.checkout_session,
                cp.subscription_change TO ti_app;
-- Tax references are access-restricted (section 27): ti_app gets every billing_account column except it.
GRANT SELECT (id, tenant_id, billing_contact_email, business_legal_name, country, provider, created_at, updated_at, version)
  ON cp.billing_account TO ti_app;
-- Published, public catalog entries are readable without tenant context (GET /saas/plans).
CREATE POLICY published_read ON cp.plan_version FOR SELECT TO ti_app USING (published_at IS NOT NULL AND is_public);
CREATE POLICY published_read ON cp.price_version FOR SELECT TO ti_app
  USING (EXISTS (SELECT 1 FROM cp.plan_version p WHERE p.id = plan_version_id AND p.published_at IS NOT NULL AND p.is_public));
GRANT SELECT ON cp.plan_version, cp.price_version TO ti_app;
-- Deletion register and webhook inbox: control plane only. terms_acceptance: append-only for everyone.
REVOKE UPDATE ON cp.terms_acceptance FROM ti_control;

-- ---------------------------------------------------------------------------
-- cp.tenant_access(): the narrow decision function for tenant requests (SAS-004, DEC-035)
-- ---------------------------------------------------------------------------
-- Takes NO tenant argument: it reads app.tenant_id, which the unit of work sets transaction-locally from a
-- validated membership (DEC-013). A tenant request therefore cannot ask about another tenant, and without
-- context it returns no row (fail closed). Access mode itself is computed in C# from this row and the
-- request time, following docs/saas-state-matrix.md; SQL does not duplicate that logic.
CREATE POLICY access_reader ON ti.tenant FOR SELECT TO cp_access_reader USING (true);
CREATE POLICY access_reader ON cp.subscription FOR SELECT TO cp_access_reader USING (true);
CREATE POLICY access_reader ON cp.entitlement_snapshot FOR SELECT TO cp_access_reader USING (true);
GRANT SELECT ON ti.tenant TO cp_access_reader;
GRANT SELECT ON cp.subscription, cp.entitlement_snapshot TO cp_access_reader;

CREATE FUNCTION cp.tenant_access()
RETURNS TABLE (tenant_id uuid, lifecycle ti.tenant_lifecycle, security_suspended_at timestamptz,
               subscription_state cp.subscription_state, trial_end timestamptz, period_end timestamptz,
               grace_end timestamptz, cancel_at_period_end boolean, subscription_revision bigint,
               entitlement_version integer, features text[], limits jsonb, entitlement_effective_to timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
  SELECT t.id, t.lifecycle, t.security_suspended_at,
         s.state, s.trial_end, s.period_end, s.grace_end, s.cancel_at_period_end, s.revision,
         e.entitlement_version, e.features, e.limits, e.effective_to
  FROM ti.tenant t
  LEFT JOIN cp.subscription s ON s.tenant_id = t.id
  LEFT JOIN cp.entitlement_snapshot e ON e.tenant_id = t.id AND e.is_current
  WHERE t.id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
$$;

-- New owner needs CREATE on cp for the hand-over; held only for this statement (DEC-028 pattern).
GRANT CREATE ON SCHEMA cp TO cp_access_reader;
ALTER FUNCTION cp.tenant_access() OWNER TO cp_access_reader;
REVOKE CREATE ON SCHEMA cp FROM cp_access_reader;
REVOKE ALL ON FUNCTION cp.tenant_access() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cp.tenant_access() TO ti_app;

-- Membership lookup now reports lifecycle and security suspension alongside the legacy status, so the
-- tenant selector can show restriction badges before a tenant is chosen (section 29).
GRANT CREATE ON SCHEMA ti TO ti_membership_reader;
CREATE FUNCTION ti.user_memberships_v2(p_subject text)
RETURNS TABLE (tenant_id uuid, tenant_name text, tenant_slug text, lifecycle ti.tenant_lifecycle,
               security_suspended boolean, membership_id uuid, role ti.role_name, warehouse_id uuid)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
  SELECT t.id, t.name, t.slug, t.lifecycle, t.security_suspended_at IS NOT NULL, m.id, g.role, g.warehouse_id
  FROM ti.membership m
  JOIN ti.tenant t ON t.id = m.tenant_id
  LEFT JOIN ti.role_grant g ON g.tenant_id = m.tenant_id AND g.membership_id = m.id AND g.revoked_at IS NULL
  WHERE m.user_subject = p_subject AND m.active AND t.lifecycle <> 'Deleted'
$$;
ALTER FUNCTION ti.user_memberships_v2(text) OWNER TO ti_membership_reader;
REVOKE CREATE ON SCHEMA ti FROM ti_membership_reader;
REVOKE ALL ON FUNCTION ti.user_memberships_v2(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ti.user_memberships_v2(text) TO ti_app;

-- @@ section 0005_tenant_status_contract as migrator
-- CONTRACT step, released only after every running API/worker version reads lifecycle and suspension
-- (docs/migration-design.md §3). Drops the v2.0 status column and its compatibility trigger and function.
-- New code always sets lifecycle; from here an omitted value means a fresh signup.
ALTER TABLE ti.tenant
  ALTER COLUMN lifecycle SET DEFAULT 'Provisioning',
  ALTER COLUMN lifecycle SET NOT NULL,
  ALTER COLUMN slug SET NOT NULL,
  ALTER COLUMN home_region SET NOT NULL;
DROP TRIGGER tenant_sync_legacy_status ON ti.tenant;
DROP FUNCTION ti.tenant_sync_legacy_status();
DROP FUNCTION ti.user_memberships(text);      -- replaced by user_memberships_v2 in 0004
ALTER TABLE ti.tenant DROP COLUMN status;
DROP TYPE ti.tenant_status;

-- Smoke checks for db/design/saas-schema-draft.sql. Run by db/design/run-saas-smoke.ps1, which interleaves
-- these sections with the draft's migration sections. Every check prints PASS or raises FAIL (ON_ERROR_STOP).
-- All identities and amounts are synthetic.

-- @@ section seed-v2 as superuser
-- v2.0-shaped tenants that exist before migration 0004: one Active, one Suspended.
CREATE SCHEMA smoke;
GRANT USAGE ON SCHEMA smoke TO PUBLIC;

CREATE FUNCTION smoke.expect_error(p_sql text, p_state text, p_label text) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE got text;
BEGIN
  BEGIN
    EXECUTE p_sql;
    got := 'none';
  EXCEPTION WHEN OTHERS THEN
    got := SQLSTATE;
  END;
  IF got IS DISTINCT FROM p_state THEN
    RAISE EXCEPTION 'FAIL % (expected SQLSTATE %, got %)', p_label, p_state, got;
  END IF;
  RAISE NOTICE 'PASS % (SQLSTATE %)', p_label, got;
END $$;

CREATE FUNCTION smoke.expect(p_ok boolean, p_label text) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
  IF p_ok IS NOT TRUE THEN
    RAISE EXCEPTION 'FAIL %', p_label;
  END IF;
  RAISE NOTICE 'PASS %', p_label;
END $$;

INSERT INTO ti.tenant (id, name, status, created_by) VALUES
  ('a0000000-0000-4000-8000-00000000000a', 'Synthetic A', 'Active', 'seed'),
  ('b0000000-0000-4000-8000-00000000000b', 'Synthetic B', 'Active', 'seed'),
  ('50000000-0000-4000-8000-000000000005', 'Synthetic Suspended', 'Suspended', 'seed');
INSERT INTO ti.audit_stream (tenant_id) VALUES
  ('a0000000-0000-4000-8000-00000000000a'), ('b0000000-0000-4000-8000-00000000000b'),
  ('50000000-0000-4000-8000-000000000005');
INSERT INTO ti.warehouse (tenant_id, code, name, timezone, created_by)
  VALUES ('a0000000-0000-4000-8000-00000000000a', 'WH-A', 'Warehouse A', 'UTC', 'seed');
INSERT INTO ti.membership (id, tenant_id, user_subject, created_by) VALUES
  ('aa000000-0000-4000-8000-0000000000a1', 'a0000000-0000-4000-8000-00000000000a', 'user-a-owner', 'seed'),
  ('aa000000-0000-4000-8000-0000000000a2', 'a0000000-0000-4000-8000-00000000000a', 'user-a-member', 'seed');

-- @@ section expand-checks as superuser
-- Between 0004 (expand) and 0005 (contract): old and new code paths must agree.
SELECT smoke.expect(lifecycle = 'Ready' AND ready_at IS NOT NULL AND slug ~ '^tenant-[0-9a-f]{12}$'
                    AND home_region = 'default' AND security_suspended_at IS NULL,
                    'x1 Active v2 tenant backfilled to Ready without suspension')
FROM ti.tenant WHERE id = 'a0000000-0000-4000-8000-00000000000a';
SELECT smoke.expect(security_suspended_at IS NOT NULL AND security_suspension_reason IS NOT NULL,
                    'x2 Suspended v2 tenant backfilled to a security suspension (not billing)')
FROM ti.tenant WHERE id = '50000000-0000-4000-8000-000000000005';

UPDATE ti.tenant SET status = 'Suspended' WHERE id = 'b0000000-0000-4000-8000-00000000000b';
SELECT smoke.expect(security_suspended_at IS NOT NULL, 'x3 old code suspending sets the security suspension')
FROM ti.tenant WHERE id = 'b0000000-0000-4000-8000-00000000000b';
UPDATE ti.tenant SET status = 'Active' WHERE id = 'b0000000-0000-4000-8000-00000000000b';
SELECT smoke.expect(security_suspended_at IS NULL, 'x4 old code reactivating clears it')
FROM ti.tenant WHERE id = 'b0000000-0000-4000-8000-00000000000b';
UPDATE ti.tenant SET security_suspended_at = now(), security_suspension_reason = 'incident', security_suspended_by = 'ops'
WHERE id = 'b0000000-0000-4000-8000-00000000000b';
SELECT smoke.expect(status = 'Suspended', 'x5 new code suspending is visible to old code as Suspended')
FROM ti.tenant WHERE id = 'b0000000-0000-4000-8000-00000000000b';
UPDATE ti.tenant SET security_suspended_at = NULL, security_suspension_reason = NULL, security_suspended_by = NULL
WHERE id = 'b0000000-0000-4000-8000-00000000000b';
INSERT INTO ti.tenant (id, name, created_by) VALUES ('c0000000-0000-4000-8000-00000000000c', 'Legacy insert', 'seed');
SELECT smoke.expect(lifecycle = 'Ready' AND slug IS NOT NULL AND status = 'Active',
                    'x6 old code inserting a tenant gets lifecycle, slug and status')
FROM ti.tenant WHERE id = 'c0000000-0000-4000-8000-00000000000c';

-- @@ section checks as superuser
-- After 0005 (contract). Arrange control-plane data as ti_control, which also proves its grants.
SET ROLE ti_control;
INSERT INTO cp.plan_version (id, plan_code, version, display_name, features, quotas, published_at, created_by) VALUES
  ('f1000000-0000-4000-8000-000000000001', 'starter', 1, 'Starter (fixture)', ARRAY['core_inventory'],
   '{"active_seats":5,"active_warehouses":1,"active_skus":1000,"active_devices":0,"attachment_bytes":1073741824}', now(), 'seed'),
  ('f1000000-0000-4000-8000-000000000002', 'growth', 1, 'Growth (fixture)', ARRAY['core_inventory','service_api'],
   '{"active_seats":25,"active_warehouses":5,"active_skus":10000,"active_devices":0,"attachment_bytes":5368709120}', NULL, 'seed');
INSERT INTO cp.price_version (id, plan_version_id, currency, minor_unit_amount, exponent, billing_interval, active_from, created_by) VALUES
  ('f2000000-0000-4000-8000-000000000001', 'f1000000-0000-4000-8000-000000000001', 'XTS', 1000, 2, 'Month', now(), 'seed'),
  ('f2000000-0000-4000-8000-000000000002', 'f1000000-0000-4000-8000-000000000001', 'XTS', 10000, 2, 'Year', now(), 'seed');
INSERT INTO cp.billing_account (id, tenant_id, billing_contact_email, business_legal_name, country, tax_reference, created_by) VALUES
  ('ba000000-0000-4000-8000-00000000000a', 'a0000000-0000-4000-8000-00000000000a', 'billing@a.example.invalid', 'Synthetic A Ltd', 'NG', 'TAX-A', 'seed'),
  ('ba000000-0000-4000-8000-00000000000b', 'b0000000-0000-4000-8000-00000000000b', 'billing@b.example.invalid', 'Synthetic B Ltd', 'GB', NULL, 'seed');
INSERT INTO cp.subscription (tenant_id, billing_account_id, plan_version_id, price_version_id, state, trial_start, trial_end,
                             period_start, period_end) VALUES
  ('a0000000-0000-4000-8000-00000000000a', 'ba000000-0000-4000-8000-00000000000a', 'f1000000-0000-4000-8000-000000000001',
   NULL, 'Trialing', now(), now() + interval '14 days', NULL, NULL),
  ('b0000000-0000-4000-8000-00000000000b', 'ba000000-0000-4000-8000-00000000000b', 'f1000000-0000-4000-8000-000000000001',
   'f2000000-0000-4000-8000-000000000001', 'Active', NULL, NULL, now(), now() + interval '1 month');
INSERT INTO cp.entitlement_snapshot (tenant_id, subscription_revision, entitlement_version, plan_version_id, features, limits, effective_from, is_current)
VALUES ('a0000000-0000-4000-8000-00000000000a', 1, 1, 'f1000000-0000-4000-8000-000000000001', ARRAY['core_inventory'],
        '{"active_seats":5,"active_warehouses":1,"active_skus":1000,"active_devices":0,"attachment_bytes":1073741824}', now(), true);
INSERT INTO cp.invoice (tenant_id, billing_account_id, provider, provider_invoice_ref, currency, exponent, lines, subtotal, total, status) VALUES
  ('a0000000-0000-4000-8000-00000000000a', 'ba000000-0000-4000-8000-00000000000a', 'simulator', 'inv-a-1', 'XTS', 2, '[]', 1000, 1000, 'Open'),
  ('b0000000-0000-4000-8000-00000000000b', 'ba000000-0000-4000-8000-00000000000b', 'simulator', 'inv-b-1', 'XTS', 2, '[]', 1000, 1000, 'Open');
INSERT INTO cp.webhook_inbox (provider, event_id, event_type, raw_body_digest, payload_encrypted)
  VALUES ('simulator', 'evt-1', 'invoice.paid', decode(repeat('ab', 32), 'hex'), '\x00');
RESET ROLE;


-- a: the control plane has no stock or catalog access (SAS-004)
SET ROLE ti_control;
SELECT smoke.expect_error('SELECT count(*) FROM ti.warehouse', '42501', 'a1 ti_control cannot read ti.warehouse');
SELECT smoke.expect_error('SELECT count(*) FROM ti.product', '42501', 'a2 ti_control cannot read ti.product');
SELECT smoke.expect_error('SELECT count(*) FROM ti.bin', '42501', 'a3 ti_control cannot read ti.bin');
SELECT smoke.expect_error('UPDATE ti.audit_event SET reason = ''x''', '42501', 'a4 ti_control cannot update audit events');
SELECT smoke.expect_error('UPDATE cp.terms_acceptance SET document_version = ''x''', '42501', 'a5 terms acceptance is append-only for ti_control');
RESET ROLE;

-- b: ti_app reads only its own tenant's billing rows, nothing without context, and never writes them
SET ROLE ti_app;
SELECT smoke.expect((SELECT count(*) FROM cp.subscription) = 0 AND (SELECT count(*) FROM cp.invoice) = 0,
                    'b1 no tenant context: no subscription or invoice rows');
BEGIN;
SELECT set_config('app.tenant_id', 'a0000000-0000-4000-8000-00000000000a', true);
SELECT smoke.expect((SELECT array_agg(DISTINCT tenant_id) FROM cp.subscription) = ARRAY['a0000000-0000-4000-8000-00000000000a'::uuid]
                AND (SELECT array_agg(DISTINCT tenant_id) FROM cp.invoice) = ARRAY['a0000000-0000-4000-8000-00000000000a'::uuid],
                    'b2 tenant A context sees only A subscription and invoices');
SELECT smoke.expect_error('INSERT INTO cp.subscription (tenant_id, billing_account_id, plan_version_id, state, cancelled_at)
  VALUES (''a0000000-0000-4000-8000-00000000000a'', ''ba000000-0000-4000-8000-00000000000a'', ''f1000000-0000-4000-8000-000000000001'', ''Cancelled'', now())',
  '42501', 'b3 ti_app cannot write subscriptions');
SELECT smoke.expect_error('SELECT tax_reference FROM cp.billing_account', '42501', 'b4 ti_app cannot read tax references');
SELECT smoke.expect((SELECT count(*) FROM cp.billing_account) = 1, 'b5 ti_app reads its own billing account without the tax column');
SELECT smoke.expect_error('SELECT count(*) FROM cp.webhook_inbox', '42501', 'b6 ti_app cannot read the webhook inbox');
SELECT smoke.expect_error('SELECT count(*) FROM cp.provisioning_job', '42501', 'b7 ti_app cannot read provisioning jobs');
COMMIT;
RESET ROLE;

-- c: cp.tenant_access() answers only for the current tenant context
SET ROLE ti_app;
SELECT smoke.expect((SELECT count(*) FROM cp.tenant_access()) = 0, 'c1 tenant_access without context returns nothing');
BEGIN;
SELECT set_config('app.tenant_id', 'a0000000-0000-4000-8000-00000000000a', true);
SELECT smoke.expect(tenant_id = 'a0000000-0000-4000-8000-00000000000a' AND lifecycle = 'Ready'
                    AND subscription_state = 'Trialing' AND trial_end IS NOT NULL AND entitlement_version = 1
                    AND features = ARRAY['core_inventory'] AND (limits->>'active_warehouses')::int = 1,
                    'c2 tenant_access returns A lifecycle, subscription clocks and current entitlements')
FROM cp.tenant_access();
SELECT smoke.expect_error('SELECT * FROM cp.tenant_access(''b0000000-0000-4000-8000-00000000000b''::uuid)', '42883',
                          'c3 tenant_access takes no tenant argument, so B cannot be asked for');
COMMIT;

RESET ROLE;
SELECT smoke.expect(NOT rolcanlogin, 'c5 cp_access_reader cannot log in') FROM pg_roles WHERE rolname = 'cp_access_reader';
SELECT smoke.expect(pg_get_userbyid(proowner) = 'cp_access_reader' AND prosecdef, 'c6 tenant_access is SECURITY DEFINER owned by cp_access_reader')
FROM pg_proc WHERE proname = 'tenant_access';
SELECT smoke.expect(NOT has_schema_privilege('cp_access_reader', 'cp', 'CREATE'), 'c7 cp_access_reader holds no CREATE on cp after hand-over');

-- d: slugs are normalised and globally unique
SET ROLE ti_control;
SELECT smoke.expect_error('UPDATE ti.tenant SET slug = ''Acme'' WHERE id = ''a0000000-0000-4000-8000-00000000000a''', '23514',
                          'd1 uppercase slug rejected');
UPDATE ti.tenant SET slug = 'acme' WHERE id = 'a0000000-0000-4000-8000-00000000000a';
SELECT smoke.expect_error('UPDATE ti.tenant SET slug = ''acme'' WHERE id = ''b0000000-0000-4000-8000-00000000000b''', '23505',
                          'd2 duplicate slug rejected');
SELECT smoke.expect_error('UPDATE ti.tenant SET slug = ''-acme'' WHERE id = ''b0000000-0000-4000-8000-00000000000b''', '23514',
                          'd3 slug must start with a letter or digit');

-- e/f/g/m: dedupe, one current snapshot, catalog immutability, one subscription per tenant
SELECT smoke.expect_error('INSERT INTO cp.webhook_inbox (provider, event_id, event_type, raw_body_digest, payload_encrypted)
  VALUES (''simulator'', ''evt-1'', ''invoice.paid'', decode(repeat(''ab'', 32), ''hex''), ''\x00'')', '23505',
  'e1 duplicate (provider, event_id) rejected');
SELECT smoke.expect_error('INSERT INTO cp.entitlement_snapshot (tenant_id, subscription_revision, entitlement_version, plan_version_id, features, limits, effective_from, is_current)
  VALUES (''a0000000-0000-4000-8000-00000000000a'', 2, 2, ''f1000000-0000-4000-8000-000000000001'', ARRAY[''core_inventory''], ''{}'', now(), true)',
  '23505', 'f1 second current entitlement snapshot rejected');
SELECT smoke.expect_error('UPDATE cp.price_version SET minor_unit_amount = 999 WHERE id = ''f2000000-0000-4000-8000-000000000001''', '23000',
                          'g1 referenced price amount is immutable');
SELECT smoke.expect_error('DELETE FROM cp.price_version WHERE id = ''f2000000-0000-4000-8000-000000000001''', '42501',
                          'g2 ti_control cannot delete prices at all');
UPDATE cp.price_version SET active_to = now() + interval '1 day' WHERE id = 'f2000000-0000-4000-8000-000000000001';
SELECT smoke.expect(true, 'g3 closing a referenced price''s availability window is allowed');
UPDATE cp.price_version SET minor_unit_amount = 12000 WHERE id = 'f2000000-0000-4000-8000-000000000002';
SELECT smoke.expect(true, 'g4 an unreferenced price can still be corrected');
SELECT smoke.expect_error('UPDATE cp.plan_version SET display_name = ''Changed'' WHERE id = ''f1000000-0000-4000-8000-000000000001''', '23000',
                          'g5 published plan version is immutable');
UPDATE cp.plan_version SET display_name = 'Growth draft' WHERE id = 'f1000000-0000-4000-8000-000000000002';
SELECT smoke.expect(true, 'g6 an unpublished plan version can be edited');
SELECT smoke.expect_error('INSERT INTO cp.subscription (tenant_id, billing_account_id, plan_version_id, state, cancelled_at)
  VALUES (''a0000000-0000-4000-8000-00000000000a'', ''ba000000-0000-4000-8000-00000000000a'', ''f1000000-0000-4000-8000-000000000001'', ''Cancelled'', now())',
  '23505', 'm1 one subscription per tenant');
SELECT smoke.expect_error('INSERT INTO cp.subscription (tenant_id, billing_account_id, plan_version_id, state, trial_start, trial_end)
  VALUES (''c0000000-0000-4000-8000-00000000000c'', ''ba000000-0000-4000-8000-00000000000a'', ''f1000000-0000-4000-8000-000000000001'', ''Trialing'', now(), now() + interval ''14 days'')',
  '23503', 'm2 a subscription cannot use another tenant''s billing account');

-- h/i: counters and money
SELECT smoke.expect_error('INSERT INTO ti.usage_counter (tenant_id, metric, used) VALUES (''a0000000-0000-4000-8000-00000000000a'', ''active_seats'', -1)',
                          '23514', 'h1 usage counter cannot go negative');
SELECT smoke.expect_error('INSERT INTO cp.invoice (tenant_id, billing_account_id, provider, provider_invoice_ref, currency, exponent, lines, subtotal, total, status)
  VALUES (''a0000000-0000-4000-8000-00000000000a'', ''ba000000-0000-4000-8000-00000000000a'', ''simulator'', ''inv-neg'', ''XTS'', 2, ''[]'', -100, -100, ''Open'')',
  '23514', 'i1 negative amounts rejected');
SELECT smoke.expect_error('INSERT INTO cp.invoice (tenant_id, billing_account_id, provider, provider_invoice_ref, currency, exponent, lines, subtotal, total, status)
  VALUES (''a0000000-0000-4000-8000-00000000000a'', ''ba000000-0000-4000-8000-00000000000a'', ''simulator'', ''inv-exp'', ''XTS'', 9, ''[]'', 100, 100, ''Open'')',
  '23514', 'i2 invalid currency exponent rejected');
SELECT smoke.expect_error('INSERT INTO cp.invoice (tenant_id, billing_account_id, provider, provider_invoice_ref, currency, exponent, lines, subtotal, total, status)
  VALUES (''a0000000-0000-4000-8000-00000000000a'', ''ba000000-0000-4000-8000-00000000000a'', ''simulator'', ''inv-cur'', ''usd'', 2, ''[]'', 100, 100, ''Open'')',
  '23514', 'i3 lowercase currency code rejected');
SELECT smoke.expect_error('INSERT INTO cp.invoice (tenant_id, billing_account_id, provider, provider_invoice_ref, currency, exponent, lines, subtotal, tax, total, status)
  VALUES (''a0000000-0000-4000-8000-00000000000a'', ''ba000000-0000-4000-8000-00000000000a'', ''simulator'', ''inv-sum'', ''XTS'', 2, ''[]'', 100, 10, 100, ''Open'')',
  '23514', 'i4 total must equal subtotal - discount + tax');

-- n: grantable roles
SELECT smoke.expect_error('INSERT INTO ti.role_grant (tenant_id, membership_id, grantee_subject, role, reason, granted_by, created_by)
  VALUES (''a0000000-0000-4000-8000-00000000000a'', ''aa000000-0000-4000-8000-0000000000a2'', ''user-a-member'', ''PlatformBilling'', ''r'', ''x'', ''x'')',
  '23514', 'n1 PlatformBilling cannot be granted inside a tenant');
RESET ROLE;

-- l: invitations are created by tenant administrators (ti_app, tenant context)
SET ROLE ti_app;
BEGIN;
SELECT set_config('app.tenant_id', 'a0000000-0000-4000-8000-00000000000a', true);
INSERT INTO ti.invitation (tenant_id, email_normalized, token_hash, roles, expires_at, invited_by, created_by)
  VALUES ('a0000000-0000-4000-8000-00000000000a', 'new.user@a.example.invalid', decode(repeat('cd', 32), 'hex'),
          ARRAY['Picker']::ti.role_name[], now() + interval '72 hours', 'user-a-owner', 'user-a-owner');
SELECT smoke.expect_error('INSERT INTO ti.invitation (tenant_id, email_normalized, token_hash, roles, expires_at, invited_by, created_by)
  VALUES (''a0000000-0000-4000-8000-00000000000a'', ''late@a.example.invalid'', decode(repeat(''ee'', 32), ''hex''),
          ARRAY[''Picker'']::ti.role_name[], now() + interval ''73 hours'', ''x'', ''x'')', '23514', 'l1 invitation lifetime over 72 h rejected');
SELECT smoke.expect_error('INSERT INTO ti.invitation (tenant_id, email_normalized, token_hash, roles, expires_at, invited_by, created_by)
  VALUES (''a0000000-0000-4000-8000-00000000000a'', ''owner@a.example.invalid'', decode(repeat(''ef'', 32), ''hex''),
          ARRAY[''TenantOwner'']::ti.role_name[], now() + interval ''1 hour'', ''x'', ''x'')', '23514', 'l2 owner role cannot be invited');
SELECT smoke.expect_error('INSERT INTO ti.invitation (tenant_id, email_normalized, token_hash, roles, expires_at, invited_by, created_by)
  VALUES (''a0000000-0000-4000-8000-00000000000a'', ''dup@a.example.invalid'', decode(repeat(''cd'', 32), ''hex''),
          ARRAY[''Picker'']::ti.role_name[], now() + interval ''1 hour'', ''x'', ''x'')', '23505', 'l3 token hashes are unique');
SELECT smoke.expect_error('INSERT INTO ti.invitation (tenant_id, email_normalized, token_hash, roles, expires_at, invited_by, created_by)
  VALUES (''b0000000-0000-4000-8000-00000000000b'', ''x@b.example.invalid'', decode(repeat(''aa'', 32), ''hex''),
          ARRAY[''Picker'']::ti.role_name[], now() + interval ''1 hour'', ''x'', ''x'')', '42501', 'l4 tenant A context cannot create an invitation for tenant B');
COMMIT;
BEGIN;
SELECT set_config('app.tenant_id', 'b0000000-0000-4000-8000-00000000000b', true);
SELECT smoke.expect((SELECT count(*) FROM ti.invitation) = 0, 'l5 tenant B cannot see tenant A invitations');
COMMIT;
RESET ROLE;
SET ROLE ti_control;
SELECT smoke.expect_error('INSERT INTO ti.invitation (tenant_id, email_normalized, token_hash, roles, expires_at, invited_by, created_by)
  VALUES (''a0000000-0000-4000-8000-00000000000a'', ''y@a.example.invalid'', decode(repeat(''bb'', 32), ''hex''),
          ARRAY[''Picker'']::ti.role_name[], now() + interval ''1 hour'', ''x'', ''x'')', '42501', 'l6 the control plane cannot create invitations, only accept them');
SELECT smoke.expect((SELECT count(*) FROM ti.invitation WHERE token_hash = decode(repeat('cd', 32), 'hex')) = 1, 'l7 the control plane finds an invitation by token hash for acceptance');
RESET ROLE;


-- o: provisioning path works for ti_control (tenant, owner membership, grant, audit stream, counters)
SET ROLE ti_control;
SELECT smoke.expect_error('INSERT INTO ti.tenant (id, name, created_by, lifecycle, slug, home_region)
  VALUES (''d0000000-0000-4000-8000-00000000000d'', ''No ready time'', ''x'', ''Ready'', ''no-ready'', ''default'')',
  '23514', 'o1 Ready tenant needs ready_at');
INSERT INTO cp.provisioning_job (id, signup_key, requested_by_subject, request_digest, request, tenant_id)
  VALUES ('e0000000-0000-4000-8000-00000000000e', 'user-new-owner|7f6b1c1e-0000-4000-8000-000000000001', 'user-new-owner',
          decode(repeat('01', 32), 'hex'), '{"businessName":"New Co"}', 'e1000000-0000-4000-8000-0000000000e1');
INSERT INTO ti.tenant (id, name, created_by, slug, home_region, provisioning_job_id)
  VALUES ('e1000000-0000-4000-8000-0000000000e1', 'New Co', 'user-new-owner', 'new-co', 'default', 'e0000000-0000-4000-8000-00000000000e');
INSERT INTO ti.audit_stream (tenant_id) VALUES ('e1000000-0000-4000-8000-0000000000e1');
INSERT INTO ti.membership (id, tenant_id, user_subject, created_by)
  VALUES ('ee000000-0000-4000-8000-0000000000e1', 'e1000000-0000-4000-8000-0000000000e1', 'user-new-owner', 'system:provisioning');
INSERT INTO ti.role_grant (tenant_id, membership_id, grantee_subject, role, reason, granted_by, created_by)
  VALUES ('e1000000-0000-4000-8000-0000000000e1', 'ee000000-0000-4000-8000-0000000000e1', 'user-new-owner', 'TenantOwner',
          'signup', 'system:provisioning', 'system:provisioning');
INSERT INTO ti.usage_counter (tenant_id, metric, used) VALUES ('e1000000-0000-4000-8000-0000000000e1', 'active_seats', 1);
SELECT smoke.expect((SELECT lifecycle FROM ti.tenant WHERE id = 'e1000000-0000-4000-8000-0000000000e1') = 'Provisioning',
                    'o2 new tenants start in Provisioning');
SELECT smoke.expect_error('INSERT INTO cp.provisioning_job (signup_key, requested_by_subject, request_digest, request, tenant_id)
  VALUES (''user-new-owner|7f6b1c1e-0000-4000-8000-000000000001'', ''user-new-owner'', decode(repeat(''01'', 32), ''hex''), ''{}'', gen_random_uuid())',
  '23505', 'o3 a retried signup key cannot create a second job (AT-ONB-01)');
RESET ROLE;

-- j: platform operations never read billing (SAS-004)
SET ROLE ti_platform;
SELECT smoke.expect_error('SELECT count(*) FROM cp.invoice', '42501', 'j1 ti_platform cannot read invoices');
SELECT smoke.expect_error('SELECT count(*) FROM cp.subscription', '42501', 'j2 ti_platform cannot read subscriptions');
SELECT smoke.expect_error('SELECT count(*) FROM cp.billing_account', '42501', 'j3 ti_platform cannot read billing accounts');
RESET ROLE;

-- k: contract step removed the legacy column; isolation still forced everywhere
SELECT smoke.expect(NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'ti' AND table_name = 'tenant' AND column_name = 'status'),
                    'k1 tenant.status dropped by the contract step');
SELECT smoke.expect(NOT EXISTS (
  SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE n.nspname IN ('ti','cp') AND c.relkind = 'r' AND c.relname <> 'schema_migration'
    AND NOT (c.relrowsecurity AND c.relforcerowsecurity)), 'k2 every ti and cp table has forced RLS');
SELECT smoke.expect(NOT EXISTS (
  SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE n.nspname IN ('ti','cp') AND c.relkind = 'r' AND pg_get_userbyid(c.relowner) <> 'ti_owner'), 'k3 every table is owned by ti_owner');

DO $$ BEGIN RAISE NOTICE 'ALL SAAS SMOKE CHECKS PASSED'; END $$;

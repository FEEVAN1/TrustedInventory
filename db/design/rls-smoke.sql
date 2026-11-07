-- RLS / integrity smoke test for db/design/schema-draft.sql (M0 evidence for TEN-001..003, STK-001, AUD-003).
-- Run on a fresh database after schema-draft.sql, as a superuser that switches roles:
--   psql -v ON_ERROR_STOP=1 -f db/design/rls-smoke.sql
-- Prints PASS lines; any failure raises and exits non-zero. All data is synthetic.

\set ON_ERROR_STOP on
\set QUIET on
SET client_min_messages = notice;
\o /dev/null

-- Assertion helpers live in this session's temp schema.
CREATE FUNCTION pg_temp.expect_eq(label text, actual bigint, expected bigint) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  IF actual IS DISTINCT FROM expected THEN
    RAISE EXCEPTION 'FAIL %: expected %, got %', label, expected, actual;
  END IF;
  RAISE NOTICE 'PASS %', label;
END $$;

CREATE FUNCTION pg_temp.expect_error(label text, stmt text, expected_state text) RETURNS void LANGUAGE plpgsql AS $$
DECLARE failed boolean := false; state text;
BEGIN
  BEGIN
    EXECUTE stmt;
  EXCEPTION WHEN OTHERS THEN
    failed := true; state := SQLSTATE;
  END;
  IF NOT failed THEN
    RAISE EXCEPTION 'FAIL %: statement succeeded: %', label, stmt;
  ELSIF state <> expected_state THEN
    RAISE EXCEPTION 'FAIL %: expected SQLSTATE %, got %', label, expected_state, state;
  END IF;
  RAISE NOTICE 'PASS % (SQLSTATE %)', label, state;
END $$;

CREATE FUNCTION pg_temp.affected(stmt text) RETURNS bigint LANGUAGE plpgsql AS $$
DECLARE n bigint;
BEGIN
  EXECUTE stmt;
  GET DIAGNOSTICS n = ROW_COUNT;
  RETURN n;
END $$;

-- ---------------------------------------------------------------------------
-- Seed: platform provisions two tenants; ti_app seeds each under its own context.
-- ---------------------------------------------------------------------------
SET ROLE ti_platform;
INSERT INTO ti.tenant (id, name, created_by) VALUES
  ('aaaaaaaa-0000-0000-0000-000000000001', 'Synthetic Tenant A', 'platform-admin'),
  ('bbbbbbbb-0000-0000-0000-000000000001', 'Synthetic Tenant B', 'platform-admin');
INSERT INTO ti.audit_stream (tenant_id) VALUES
  ('aaaaaaaa-0000-0000-0000-000000000001'), ('bbbbbbbb-0000-0000-0000-000000000001');
INSERT INTO ti.membership (id, tenant_id, user_subject, created_by) VALUES
  ('aaaaaaaa-0000-0000-0000-0000000000f1', 'aaaaaaaa-0000-0000-0000-000000000001', 'user-alice', 'platform-admin'),
  ('bbbbbbbb-0000-0000-0000-0000000000f1', 'bbbbbbbb-0000-0000-0000-000000000001', 'user-bob', 'platform-admin');
RESET ROLE;

SET ROLE ti_app;

CREATE FUNCTION pg_temp.seed_tenant(t uuid, prefix text) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  PERFORM set_config('app.tenant_id', t::text, true);
  INSERT INTO ti.unit (id, tenant_id, code, name, created_by)
    VALUES ((prefix || '-0000-0000-0000-000000000010')::uuid, t, 'EA', 'Each', 'seed');
  INSERT INTO ti.warehouse (id, tenant_id, code, name, timezone, created_by)
    VALUES ((prefix || '-0000-0000-0000-000000000020')::uuid, t, 'WH1', 'Warehouse 1', 'Africa/Lagos', 'seed');
  INSERT INTO ti.zone (id, tenant_id, warehouse_id, code, name, created_by)
    VALUES ((prefix || '-0000-0000-0000-000000000030')::uuid, t, (prefix || '-0000-0000-0000-000000000020')::uuid, 'AMB', 'Ambient', 'seed');
  INSERT INTO ti.bin (id, tenant_id, warehouse_id, zone_id, code, created_by)
    VALUES ((prefix || '-0000-0000-0000-000000000040')::uuid, t, (prefix || '-0000-0000-0000-000000000020')::uuid,
            (prefix || '-0000-0000-0000-000000000030')::uuid, 'A-01', 'seed');
  INSERT INTO ti.product (id, tenant_id, sku, name, handling_profile, tracking_mode, base_uom_id, base_precision, rotation_rule, created_by)
    VALUES ((prefix || '-0000-0000-0000-000000000050')::uuid, t, 'SKU-1', 'Synthetic widget', 'General', 'Quantity',
            (prefix || '-0000-0000-0000-000000000010')::uuid, 0, 'FIFO', 'seed');
  INSERT INTO ti.stock_bucket (id, tenant_id, warehouse_id, bin_id, product_id, quality_status, received_at, on_hand_qty, created_by)
    VALUES ((prefix || '-0000-0000-0000-000000000060')::uuid, t, (prefix || '-0000-0000-0000-000000000020')::uuid,
            (prefix || '-0000-0000-0000-000000000040')::uuid, (prefix || '-0000-0000-0000-000000000050')::uuid,
            'Available', now(), 100, 'seed');
  INSERT INTO ti.stock_movement (id, tenant_id, movement_type, operation_key, product_id, destination_bucket_id, base_qty,
                                 document_type, document_id, actor_subject, occurred_at)
    VALUES ((prefix || '-0000-0000-0000-000000000070')::uuid, t, 'Receipt', 'seed:receipt:1',
            (prefix || '-0000-0000-0000-000000000050')::uuid, (prefix || '-0000-0000-0000-000000000060')::uuid, 100,
            'Receipt', (prefix || '-0000-0000-0000-000000000099')::uuid, 'seed', now());
  INSERT INTO ti.audit_event (tenant_id, sequence, occurred_at, actor_subject, actor_type, event_type, object_type,
                              previous_hash, event_hash)
    VALUES (t, 1, now(), 'seed', 'System', 'Seeded', 'Tenant', decode(repeat('00', 32), 'hex'), sha256(convert_to(prefix, 'UTF8')));
END $$;

BEGIN; SELECT pg_temp.seed_tenant('aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa'); COMMIT;
BEGIN; SELECT pg_temp.seed_tenant('bbbbbbbb-0000-0000-0000-000000000001', 'bbbbbbbb'); COMMIT;

-- ---------------------------------------------------------------------------
-- (a) No tenant context: nothing visible, nothing insertable (TEN-003 fail-closed).
-- ---------------------------------------------------------------------------
SELECT pg_temp.expect_eq('a1 no context: products invisible', (SELECT count(*) FROM ti.product), 0);
SELECT pg_temp.expect_eq('a2 no context: stock invisible', (SELECT count(*) FROM ti.stock_bucket), 0);
SELECT pg_temp.expect_eq('a3 no context: tenants invisible', (SELECT count(*) FROM ti.tenant), 0);
SELECT pg_temp.expect_error('a4 no context: insert rejected',
  $s$INSERT INTO ti.unit (tenant_id, code, name, created_by) VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'KG', 'Kilogram', 'x')$s$,
  '42501');

-- ---------------------------------------------------------------------------
-- (b) Tenant A context cannot see or write tenant B rows (TEN-002).
-- ---------------------------------------------------------------------------
BEGIN;
SELECT set_config('app.tenant_id', 'aaaaaaaa-0000-0000-0000-000000000001', true);
SELECT pg_temp.expect_eq('b1 tenant A sees its own product', (SELECT count(*) FROM ti.product), 1);
SELECT pg_temp.expect_eq('b2 tenant A cannot see B product by id',
  (SELECT count(*) FROM ti.product WHERE id = 'bbbbbbbb-0000-0000-0000-000000000050'), 0);
SELECT pg_temp.expect_eq('b3 tenant A sees only its tenant row', (SELECT count(*) FROM ti.tenant), 1);
SELECT pg_temp.expect_eq('b4 update of B stock affects zero rows',
  pg_temp.affected($s$UPDATE ti.stock_bucket SET on_hand_qty = 0 WHERE id = 'bbbbbbbb-0000-0000-0000-000000000060'$s$), 0);
SELECT pg_temp.expect_error('b5 insert row stamped with tenant B rejected',
  $s$INSERT INTO ti.unit (tenant_id, code, name, created_by) VALUES ('bbbbbbbb-0000-0000-0000-000000000001', 'KG', 'Kilogram', 'x')$s$,
  '42501');

-- ---------------------------------------------------------------------------
-- (c) Composite FKs stop a tenant-A row from referencing tenant-B parents (TEN-001).
-- ---------------------------------------------------------------------------
SELECT pg_temp.expect_error('c1 A bucket referencing B product rejected',
  $s$INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, quality_status, received_at, created_by)
     VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000020',
             'aaaaaaaa-0000-0000-0000-000000000040', 'bbbbbbbb-0000-0000-0000-000000000050', 'Available', now(), 'x')$s$,
  '23503');
SELECT pg_temp.expect_error('c2 A bucket referencing B bin rejected',
  $s$INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, quality_status, received_at, created_by)
     VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'aaaaaaaa-0000-0000-0000-000000000020',
             'bbbbbbbb-0000-0000-0000-000000000040', 'aaaaaaaa-0000-0000-0000-000000000050', 'Available', now(), 'x')$s$,
  '23503');
SELECT pg_temp.expect_error('c3 movement referencing B bucket rejected',
  $s$INSERT INTO ti.stock_movement (tenant_id, movement_type, operation_key, product_id, destination_bucket_id, base_qty,
                                    document_type, document_id, actor_subject, occurred_at)
     VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'Receipt', 'c3', 'aaaaaaaa-0000-0000-0000-000000000050',
             'bbbbbbbb-0000-0000-0000-000000000060', 1, 'Receipt', gen_random_uuid(), 'x', now())$s$,
  '23503');

-- ---------------------------------------------------------------------------
-- (d) Append-only ledger and audit (STK-004, AUD-003): no UPDATE/DELETE for ti_app.
-- ---------------------------------------------------------------------------
SELECT pg_temp.expect_error('d1 update stock_movement denied',
  $s$UPDATE ti.stock_movement SET base_qty = 1$s$, '42501');
SELECT pg_temp.expect_error('d2 delete stock_movement denied',
  $s$DELETE FROM ti.stock_movement$s$, '42501');
SELECT pg_temp.expect_error('d3 update audit_event denied',
  $s$UPDATE ti.audit_event SET reason = 'tampered'$s$, '42501');
SELECT pg_temp.expect_error('d4 delete audit_event denied',
  $s$DELETE FROM ti.audit_event$s$, '42501');
SELECT pg_temp.expect_error('d5 duplicate operation key rejected (STK-007)',
  $s$INSERT INTO ti.stock_movement (tenant_id, movement_type, operation_key, product_id, destination_bucket_id, base_qty,
                                    document_type, document_id, actor_subject, occurred_at)
     VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'Receipt', 'seed:receipt:1', 'aaaaaaaa-0000-0000-0000-000000000050',
             'aaaaaaaa-0000-0000-0000-000000000060', 1, 'Receipt', gen_random_uuid(), 'x', now())$s$,
  '23505');
SELECT pg_temp.expect_error('d6 duplicate audit sequence rejected (AUD-002)',
  $s$INSERT INTO ti.audit_event (tenant_id, sequence, occurred_at, actor_subject, actor_type, event_type, object_type,
                                 previous_hash, event_hash)
     VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 1, now(), 'x', 'System', 'Dup', 'Tenant',
             decode(repeat('00', 32), 'hex'), sha256('dup'::bytea))$s$,
  '23505');

-- ---------------------------------------------------------------------------
-- (f) Stock arithmetic constraints (STK-001, DEC-014).
-- ---------------------------------------------------------------------------
SELECT pg_temp.expect_error('f1 reserved above on-hand rejected',
  $s$UPDATE ti.stock_bucket SET reserved_qty = 101 WHERE id = 'aaaaaaaa-0000-0000-0000-000000000060'$s$, '23514');
SELECT pg_temp.expect_error('f2 negative reserved rejected',
  $s$UPDATE ti.stock_bucket SET reserved_qty = -1 WHERE id = 'aaaaaaaa-0000-0000-0000-000000000060'$s$, '23514');
SELECT pg_temp.expect_error('f3 negative on-hand rejected',
  $s$UPDATE ti.stock_bucket SET on_hand_qty = -5 WHERE id = 'aaaaaaaa-0000-0000-0000-000000000060'$s$, '23514');
SELECT pg_temp.expect_error('f4 seven fractional digits rejected, not rounded',
  $s$UPDATE ti.stock_bucket SET on_hand_qty = 1.0000001 WHERE id = 'aaaaaaaa-0000-0000-0000-000000000060'$s$, '23514');
SELECT pg_temp.expect_error('f5 zero movement quantity rejected',
  $s$INSERT INTO ti.stock_movement (tenant_id, movement_type, operation_key, product_id, destination_bucket_id, base_qty,
                                    document_type, document_id, actor_subject, occurred_at)
     VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'Receipt', 'f5', 'aaaaaaaa-0000-0000-0000-000000000050',
             'aaaaaaaa-0000-0000-0000-000000000060', 0, 'Receipt', gen_random_uuid(), 'x', now())$s$,
  '23514');
SELECT pg_temp.expect_error('f6 medical product without lot tracking rejected',
  $s$INSERT INTO ti.product (tenant_id, sku, name, handling_profile, tracking_mode, base_uom_id, base_precision, rotation_rule, created_by)
     VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 'MED-X', 'Synthetic vaccine', 'Medical', 'Quantity',
             'aaaaaaaa-0000-0000-0000-000000000010', 0, 'FEFO', 'x')$s$,
  '23514');
SELECT pg_temp.expect_eq('f7 valid reservation update accepted',
  pg_temp.affected($s$UPDATE ti.stock_bucket SET reserved_qty = 30.5 WHERE id = 'aaaaaaaa-0000-0000-0000-000000000060'$s$), 1);
COMMIT;

-- ---------------------------------------------------------------------------
-- (e) Transaction-local context does not leak to the next transaction on the same session (TEN-003).
-- ---------------------------------------------------------------------------
BEGIN;
SELECT set_config('app.tenant_id', 'bbbbbbbb-0000-0000-0000-000000000001', true);
SELECT pg_temp.expect_eq('e1 tenant B context active inside transaction', (SELECT count(*) FROM ti.product), 1);
COMMIT;
BEGIN;
SELECT pg_temp.expect_eq('e2 next transaction on same session sees nothing', (SELECT count(*) FROM ti.product), 0);
COMMIT;
BEGIN;
SELECT set_config('app.tenant_id', 'aaaaaaaa-0000-0000-0000-000000000001', true);
SELECT pg_temp.expect_eq('e3 switching to tenant A shows only A rows',
  (SELECT count(*) FROM ti.product WHERE tenant_id = 'aaaaaaaa-0000-0000-0000-000000000001'), 1);
ROLLBACK;

-- ---------------------------------------------------------------------------
-- (g) Membership lookup returns only the given subject's memberships without tenant context.
-- ---------------------------------------------------------------------------
SELECT pg_temp.expect_eq('g1 user_memberships returns caller subject only',
  (SELECT count(*) FROM ti.user_memberships('user-alice')), 1);
SELECT pg_temp.expect_eq('g2 user_memberships does not open membership table',
  (SELECT count(*) FROM ti.membership), 0);

-- ---------------------------------------------------------------------------
-- (h) Owner and platform roles have no stock bypass (TEN-004).
-- ---------------------------------------------------------------------------
RESET ROLE;
SET ROLE ti_owner;
SELECT pg_temp.expect_eq('h1 owner sees no stock rows (FORCE RLS)', (SELECT count(*) FROM ti.stock_bucket), 0);
RESET ROLE;
SET ROLE ti_platform;
SELECT pg_temp.expect_error('h2 platform role has no stock privileges',
  $s$SELECT count(*) FROM ti.stock_bucket$s$, '42501');
RESET ROLE;

-- ---------------------------------------------------------------------------
-- (i) Append-only triggers hold even for a superuser session (defence in depth beside grants).
-- ---------------------------------------------------------------------------
SELECT pg_temp.expect_error('i1 superuser update of audit_event blocked by trigger',
  $s$UPDATE ti.audit_event SET reason = 'tampered'$s$, '42501');
SELECT pg_temp.expect_error('i2 superuser delete of stock_movement blocked by trigger',
  $s$DELETE FROM ti.stock_movement$s$, '42501');
SELECT pg_temp.expect_error('i3 superuser truncate of stock_movement blocked by trigger',
  $s$TRUNCATE ti.stock_movement CASCADE$s$, '42501');

\echo 'ALL RLS SMOKE CHECKS PASSED'

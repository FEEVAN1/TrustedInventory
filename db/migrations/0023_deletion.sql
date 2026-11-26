-- 0023 (S3 slice 3): approved deletion, the deletion register and restore replay (LFC-004, DEC-072).
-- Live data is removed only by cp.purge_tenant(), owned by the NOLOGIN role ti_purger (db/bootstrap/0002_purge.sql). The function
-- itself checks that the tenant is DeletionPending under an approved deletion record and has no retention hold, so no runtime
-- role can delete tenant data by any other path. Audit evidence (audit_event, audit_checkpoint, audit_stream), the tenant row and
-- control-plane billing records are retained (docs/tenant-offboarding.md).

-- ---------------------------------------------------------------------------------------------------------------
-- The purge marker: true only inside cp.purge_tenant() for the tenant being purged.
CREATE FUNCTION ti.purging(p_tenant uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
  SELECT current_user = 'ti_purger' AND current_setting('ti.purge_tenant', true) = p_tenant::text
$$;

-- Append-only live data may be deleted by the approved purge; audit evidence never (AUD-003).
CREATE OR REPLACE FUNCTION ti.reject_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' AND TG_TABLE_NAME NOT IN ('audit_event', 'audit_checkpoint') AND ti.purging(OLD.tenant_id) THEN
    RETURN OLD;
  END IF;
  RAISE EXCEPTION '% on %.% is not permitted: append-only table', TG_OP, TG_TABLE_SCHEMA, TG_TABLE_NAME
    USING ERRCODE = 'insufficient_privilege';
END $$;

CREATE OR REPLACE FUNCTION ti.purchase_order_line_draft_only() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  line ti.purchase_order_line := CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
  order_status ti.purchase_order_status;
BEGIN
  IF TG_OP = 'DELETE' AND ti.purging(OLD.tenant_id) THEN
    RETURN OLD;  -- approved purge (LFC-004)
  END IF;
  IF TG_OP = 'UPDATE'
     AND (NEW.purchase_order_id, NEW.line_number, NEW.product_id, NEW.uom_id, NEW.uom_factor_to_base,
          NEW.ordered_uom_qty, NEW.ordered_base_qty)
         IS NOT DISTINCT FROM
         (OLD.purchase_order_id, OLD.line_number, OLD.product_id, OLD.uom_id, OLD.uom_factor_to_base,
          OLD.ordered_uom_qty, OLD.ordered_base_qty) THEN
    RETURN NEW;  -- receiving progress and stamps only
  END IF;

  SELECT status INTO order_status FROM ti.purchase_order
  WHERE tenant_id = line.tenant_id AND id = line.purchase_order_id;
  IF order_status IS DISTINCT FROM 'Draft' THEN
    RAISE EXCEPTION 'purchase order lines can change only while the order is Draft (order is %)', order_status
      USING ERRCODE = 'check_violation';
  END IF;
  RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END $$;

CREATE OR REPLACE FUNCTION ti.outbound_order_line_draft_only() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  line ti.outbound_order_line := CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
  order_status ti.outbound_order_status;
BEGIN
  IF TG_OP = 'DELETE' AND ti.purging(OLD.tenant_id) THEN
    RETURN OLD;  -- approved purge (LFC-004)
  END IF;
  IF TG_OP = 'UPDATE'
     AND (NEW.order_id, NEW.line_number, NEW.product_id, NEW.requested_base_qty, NEW.min_remaining_shelf_life_days)
         IS NOT DISTINCT FROM
         (OLD.order_id, OLD.line_number, OLD.product_id, OLD.requested_base_qty, OLD.min_remaining_shelf_life_days) THEN
    RETURN NEW;  -- allocation, pick and dispatch counters and stamps only
  END IF;
  SELECT status INTO order_status FROM ti.outbound_order WHERE tenant_id = line.tenant_id AND id = line.order_id;
  IF order_status IS DISTINCT FROM 'Draft' THEN
    RAISE EXCEPTION 'outbound order lines can change only while the order is Draft (order is %)', order_status
      USING ERRCODE = 'check_violation';
  END IF;
  RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END $$;

-- ---------------------------------------------------------------------------------------------------------------
-- ti_purger: read and delete every tenant table except the retained audit evidence; read and update the tenant row and the
-- deletion record. It cannot log in; only the two functions below run as it.
DO $$
DECLARE t text;
BEGIN
  FOR t IN SELECT c.table_name FROM information_schema.columns c
           JOIN information_schema.tables x ON x.table_schema = c.table_schema AND x.table_name = c.table_name AND x.table_type = 'BASE TABLE'
           WHERE c.table_schema = 'ti' AND c.column_name = 'tenant_id'
             AND c.table_name NOT IN ('audit_event', 'audit_checkpoint', 'audit_stream')
  LOOP
    EXECUTE format('CREATE POLICY purge ON ti.%I TO ti_purger USING (true)', t);
    EXECUTE format('GRANT SELECT, DELETE ON ti.%I TO ti_purger', t);
  END LOOP;
END $$;
CREATE POLICY purge ON ti.tenant TO ti_purger USING (true) WITH CHECK (true);
GRANT SELECT, UPDATE ON ti.tenant TO ti_purger;
CREATE POLICY purge ON cp.deletion_record TO ti_purger USING (true) WITH CHECK (true);
GRANT SELECT, INSERT, UPDATE ON cp.deletion_record TO ti_purger;

-- ---------------------------------------------------------------------------------------------------------------
-- cp.purge_tenant(): removes every live row of one tenant. Preconditions are checked here, in the database, so the caller's
-- code cannot skip them: lifecycle DeletionPending, an approved DeletionPending record, no retention hold. Idempotent: a rerun
-- after a crash removes what is left. Returns the rows removed per table. Memberships, role grants and invitations are removed
-- with the rest, so no person keeps access (tokens are useless without a membership).
CREATE FUNCTION cp.purge_tenant(p_tenant uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
  v_lifecycle ti.tenant_lifecycle;
  v_hold boolean;
  v_tables text[];
  v_pending text[];
  v_table text;
  v_count bigint;
  v_counts jsonb := '{}'::jsonb;
  v_pass int := 0;
BEGIN
  SELECT lifecycle, retention_hold INTO v_lifecycle, v_hold FROM ti.tenant WHERE id = p_tenant FOR UPDATE;
  IF v_lifecycle IS DISTINCT FROM 'DeletionPending' THEN
    RAISE EXCEPTION 'tenant % is not DeletionPending (%)', p_tenant, v_lifecycle USING ERRCODE = 'insufficient_privilege';
  END IF;
  IF v_hold THEN
    RAISE EXCEPTION 'tenant % is under a retention hold', p_tenant USING ERRCODE = 'insufficient_privilege';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM cp.deletion_record
                 WHERE tenant_id = p_tenant AND status = 'DeletionPending' AND approved_at IS NOT NULL AND approved_by IS NOT NULL) THEN
    RAISE EXCEPTION 'tenant % has no approved deletion', p_tenant USING ERRCODE = 'insufficient_privilege';
  END IF;

  PERFORM set_config('ti.purge_tenant', p_tenant::text, true);
  SELECT array_agg(c.table_name::text ORDER BY c.table_name) INTO v_tables
  FROM information_schema.columns c
  JOIN information_schema.tables x ON x.table_schema = c.table_schema AND x.table_name = c.table_name AND x.table_type = 'BASE TABLE'
  WHERE c.table_schema = 'ti' AND c.column_name = 'tenant_id'
    AND c.table_name NOT IN ('audit_event', 'audit_checkpoint', 'audit_stream');

  -- Delete in passes: a table whose rows are still referenced is retried after its children are gone.
  v_pending := v_tables;
  WHILE coalesce(array_length(v_pending, 1), 0) > 0 LOOP
    v_pass := v_pass + 1;
    IF v_pass > 50 THEN
      RAISE EXCEPTION 'purge of tenant % could not finish; tables left: %', p_tenant, v_pending;
    END IF;
    v_tables := v_pending;
    v_pending := '{}';
    FOREACH v_table IN ARRAY v_tables LOOP
      BEGIN
        EXECUTE format('DELETE FROM ti.%I WHERE tenant_id = $1', v_table) USING p_tenant;
        GET DIAGNOSTICS v_count = ROW_COUNT;
        v_counts := jsonb_set(v_counts, ARRAY[v_table], to_jsonb(coalesce((v_counts ->> v_table)::bigint, 0) + v_count));
      EXCEPTION WHEN foreign_key_violation THEN
        v_pending := v_pending || v_table;
      END;
    END LOOP;
  END LOOP;
  PERFORM set_config('ti.purge_tenant', '', true);
  RETURN v_counts;
END $$;

-- cp.replay_deletion(): repeats a register entry against restored data (docs/tenant-offboarding.md, AT-LFC-02). Used by the
-- migrator's replay-deletions command before a restored environment is opened. A tenant missing from the restored data is
-- already gone; one present is set DeletionPending under the register's approval, purged, and marked Deleted.
CREATE FUNCTION cp.replay_deletion(p_tenant uuid, p_record uuid, p_approved_by text, p_approved_at timestamptz,
                                   p_completed_at timestamptz, p_backup_expiry_at timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
  v_counts jsonb;
BEGIN
  IF NOT EXISTS (SELECT 1 FROM ti.tenant WHERE id = p_tenant) THEN
    RETURN jsonb_build_object('tenant', 'absent');
  END IF;
  UPDATE ti.tenant SET lifecycle = 'DeletionPending', retention_hold = false, retention_hold_reason = NULL, retention_hold_reference = NULL,
         deletion_requested_at = coalesce(deletion_requested_at, p_approved_at), archived_at = coalesce(archived_at, p_approved_at),
         updated_at = now(), version = version + 1
  WHERE id = p_tenant AND lifecycle <> 'Deleted';
  -- The restored backup may predate the closure: the register's record is authoritative.
  UPDATE cp.deletion_record SET status = 'Withdrawn', updated_at = now(), version = version + 1
  WHERE tenant_id = p_tenant AND id <> p_record AND status IN ('Requested', 'ExportWindow', 'Blocked', 'DeletionPending');
  INSERT INTO cp.deletion_record (id, tenant_id, status, requested_by, requested_at, reason, reauth_reference, deletion_requested,
                                  approved_by, approved_at, created_at, updated_at)
  VALUES (p_record, p_tenant, 'DeletionPending', 'system:deletion-register', p_approved_at, 'Deletion register replay after restore',
          decode(repeat('00', 32), 'hex'), true, p_approved_by, p_approved_at, now(), now())
  ON CONFLICT (id) DO UPDATE SET status = 'DeletionPending', approved_by = p_approved_by, approved_at = p_approved_at,
                                 completed_at = NULL, updated_at = now(), version = cp.deletion_record.version + 1;
  IF (SELECT lifecycle FROM ti.tenant WHERE id = p_tenant) = 'Deleted' THEN
    UPDATE ti.tenant SET lifecycle = 'DeletionPending' WHERE id = p_tenant;
  END IF;
  v_counts := cp.purge_tenant(p_tenant);
  UPDATE ti.tenant SET lifecycle = 'Deleted', deleted_at = coalesce(deleted_at, p_completed_at), updated_at = now(), version = version + 1
  WHERE id = p_tenant;
  UPDATE cp.deletion_record SET status = 'Deleted', completed_at = p_completed_at, backup_expiry_at = p_backup_expiry_at,
         updated_at = now(), version = version + 1
  WHERE id = p_record;
  RETURN v_counts;
END $$;

-- Hand both functions to ti_purger (CREATE on the schemas only for the statement, DEC-028 pattern).
GRANT CREATE ON SCHEMA cp TO ti_purger;
ALTER FUNCTION cp.purge_tenant(uuid) OWNER TO ti_purger;
ALTER FUNCTION cp.replay_deletion(uuid, uuid, text, timestamptz, timestamptz, timestamptz) OWNER TO ti_purger;
REVOKE CREATE ON SCHEMA cp FROM ti_purger;
REVOKE ALL ON FUNCTION cp.purge_tenant(uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION cp.replay_deletion(uuid, uuid, text, timestamptz, timestamptz, timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cp.purge_tenant(uuid) TO ti_control;
GRANT EXECUTE ON FUNCTION cp.replay_deletion(uuid, uuid, text, timestamptz, timestamptz, timestamptz) TO ti_control;
GRANT EXECUTE ON FUNCTION ti.purging(uuid) TO PUBLIC;

-- 0031 (M6, section 29): complimentary access (DEC-094). Expand-only for tables; cp.tenant_access() is recreated with three added
-- columns (callers select by name, so the previous application keeps working).
-- A platform billing operator requests free access to a published plan for one workspace until an expiry; a different platform operator
-- approves it; either can revoke it. The plan's features and limits are copied at the request, so the grant shows exactly what was
-- approved. Whether a grant is in force at an instant is decided in C# with the application clock, as for every other access rule.
CREATE TABLE cp.complimentary_access (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id        uuid NOT NULL REFERENCES ti.tenant(id),
  plan_version_id  uuid NOT NULL REFERENCES cp.plan_version(id),
  features         text[] NOT NULL,
  limits           jsonb NOT NULL CHECK (jsonb_typeof(limits) = 'object'),
  expires_at       timestamptz NOT NULL,
  reason           ti.reason_text NOT NULL,
  requested_by     ti.subject_text NOT NULL,
  requested_at     timestamptz NOT NULL,
  approved_by      ti.subject_text,
  approved_at      timestamptz,
  approval_reason  ti.reason_text,
  revoked_by       ti.subject_text,
  revoked_at       timestamptz,
  revoke_reason    ti.reason_text,
  version          integer NOT NULL DEFAULT 1 CHECK (version > 0),
  CHECK (expires_at > requested_at AND expires_at <= requested_at + interval '366 days'),
  CHECK (features <@ ARRAY['core_inventory','service_api','advanced_reporting','medical_storage','telemetry']),
  CHECK ((approved_by IS NULL) = (approved_at IS NULL) AND (approved_by IS NULL) = (approval_reason IS NULL)),
  CHECK (approved_by IS NULL OR approved_by <> requested_by),                       -- independent approval (section 29)
  CHECK ((revoked_by IS NULL) = (revoked_at IS NULL) AND (revoked_by IS NULL) = (revoke_reason IS NULL))
);
CREATE INDEX complimentary_access_tenant_ix ON cp.complimentary_access (tenant_id, requested_at DESC);

-- A request, its approval and its revocation are each written once; nothing else changes.
CREATE FUNCTION cp.complimentary_access_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF (NEW.tenant_id, NEW.plan_version_id, NEW.features, NEW.limits, NEW.expires_at, NEW.reason, NEW.requested_by, NEW.requested_at)
     IS DISTINCT FROM
     (OLD.tenant_id, OLD.plan_version_id, OLD.features, OLD.limits, OLD.expires_at, OLD.reason, OLD.requested_by, OLD.requested_at) THEN
    RAISE EXCEPTION 'a complimentary access request cannot change' USING ERRCODE = 'check_violation';
  END IF;
  IF OLD.approved_by IS NOT NULL AND (NEW.approved_by, NEW.approved_at, NEW.approval_reason)
       IS DISTINCT FROM (OLD.approved_by, OLD.approved_at, OLD.approval_reason) THEN
    RAISE EXCEPTION 'an approval cannot change' USING ERRCODE = 'check_violation';
  END IF;
  IF OLD.revoked_by IS NOT NULL AND ROW(NEW.*) IS DISTINCT FROM ROW(OLD.*) THEN
    RAISE EXCEPTION 'a revoked grant is final' USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER complimentary_access_guard BEFORE UPDATE ON cp.complimentary_access FOR EACH ROW EXECUTE FUNCTION cp.complimentary_access_guard();
CREATE TRIGGER complimentary_access_no_delete BEFORE DELETE OR TRUNCATE ON cp.complimentary_access FOR EACH STATEMENT EXECUTE FUNCTION ti.reject_mutation();

ALTER TABLE cp.complimentary_access ENABLE ROW LEVEL SECURITY;
ALTER TABLE cp.complimentary_access FORCE ROW LEVEL SECURITY;
CREATE POLICY control_plane ON cp.complimentary_access TO ti_control USING (true) WITH CHECK (true);
GRANT SELECT, INSERT, UPDATE ON cp.complimentary_access TO ti_control;
CREATE POLICY access_reader ON cp.complimentary_access FOR SELECT TO cp_access_reader USING (true);
GRANT SELECT ON cp.complimentary_access TO cp_access_reader;

-- The decision function gains the latest approved, unrevoked grant; C# decides whether it is still in force.
DROP FUNCTION cp.tenant_access();
CREATE FUNCTION cp.tenant_access()
RETURNS TABLE (tenant_id uuid, lifecycle ti.tenant_lifecycle, security_suspended_at timestamptz,
               subscription_state cp.subscription_state, trial_end timestamptz, period_end timestamptz,
               grace_end timestamptz, cancel_at_period_end boolean, subscription_revision bigint,
               entitlement_version integer, features text[], limits jsonb, entitlement_effective_to timestamptz,
               complimentary_until timestamptz, complimentary_features text[], complimentary_limits jsonb)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
  SELECT t.id, t.lifecycle, t.security_suspended_at,
         s.state, s.trial_end, s.period_end, s.grace_end, s.cancel_at_period_end, s.revision,
         e.entitlement_version, e.features, e.limits, e.effective_to,
         g.expires_at, g.features, g.limits
  FROM ti.tenant t
  LEFT JOIN cp.subscription s ON s.tenant_id = t.id
  LEFT JOIN cp.entitlement_snapshot e ON e.tenant_id = t.id AND e.is_current
  LEFT JOIN LATERAL (
    SELECT c.expires_at, c.features, c.limits FROM cp.complimentary_access c
    WHERE c.tenant_id = t.id AND c.approved_at IS NOT NULL AND c.revoked_at IS NULL
    ORDER BY c.expires_at DESC LIMIT 1) g ON true
  WHERE t.id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
$$;
GRANT CREATE ON SCHEMA cp TO cp_access_reader;
ALTER FUNCTION cp.tenant_access() OWNER TO cp_access_reader;
REVOKE CREATE ON SCHEMA cp FROM cp_access_reader;
REVOKE ALL ON FUNCTION cp.tenant_access() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cp.tenant_access() TO ti_app;

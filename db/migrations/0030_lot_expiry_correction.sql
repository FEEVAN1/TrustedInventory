-- 0030 (M6, M5 carry-over): lot expiry correction (spec section 07, MED-003, IAM-003, DEC-090). Expand-only.
-- A lot's identity and expiry are fixed at receipt. The expiry changes only through an audited QA correction with the lot's stock
-- held and a signed ExpiryCorrection approval; this table is that record, and a trigger refuses any expiry change without a matching
-- correction row written in the same transaction, so no code path can change an expiry silently.
CREATE TABLE ti.lot_expiry_correction (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id        uuid NOT NULL REFERENCES ti.tenant(id),
  lot_id           uuid NOT NULL,
  previous_expiry  date,
  new_expiry       date NOT NULL,
  approval_id      uuid NOT NULL,
  reason           ti.reason_text NOT NULL,
  transaction_id   xid8 NOT NULL DEFAULT pg_current_xact_id(),
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       ti.subject_text NOT NULL,
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, approval_id),                 -- one correction per approval
  FOREIGN KEY (tenant_id, lot_id) REFERENCES ti.lot (tenant_id, id),
  FOREIGN KEY (tenant_id, approval_id) REFERENCES ti.approval (tenant_id, id),
  CHECK (previous_expiry IS DISTINCT FROM new_expiry)
);
CREATE INDEX lot_expiry_correction_lot_ix ON ti.lot_expiry_correction (tenant_id, lot_id, created_at);

CREATE FUNCTION ti.lot_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.product_id IS DISTINCT FROM OLD.product_id
     OR NEW.lot_number IS DISTINCT FROM OLD.lot_number OR NEW.handling_profile IS DISTINCT FROM OLD.handling_profile THEN
    RAISE EXCEPTION 'A lot''s product, number and handling profile cannot change' USING ERRCODE = 'check_violation';
  END IF;
  IF NEW.expiry_date IS DISTINCT FROM OLD.expiry_date AND NOT EXISTS (
       SELECT 1 FROM ti.lot_expiry_correction c
       WHERE c.tenant_id = NEW.tenant_id AND c.lot_id = NEW.id AND c.new_expiry = NEW.expiry_date
         AND c.previous_expiry IS NOT DISTINCT FROM OLD.expiry_date AND c.transaction_id = pg_current_xact_id()) THEN
    RAISE EXCEPTION 'A lot''s expiry changes only with an approved correction recorded in the same transaction (section 07)'
      USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER lot_guard BEFORE UPDATE ON ti.lot FOR EACH ROW EXECUTE FUNCTION ti.lot_guard();

DO $$
BEGIN
  EXECUTE 'CREATE TRIGGER lot_expiry_correction_append_only BEFORE UPDATE OR DELETE ON ti.lot_expiry_correction FOR EACH ROW EXECUTE FUNCTION ti.reject_mutation()';
  EXECUTE 'CREATE TRIGGER lot_expiry_correction_no_truncate BEFORE TRUNCATE ON ti.lot_expiry_correction FOR EACH STATEMENT EXECUTE FUNCTION ti.reject_mutation()';
  EXECUTE 'ALTER TABLE ti.lot_expiry_correction ENABLE ROW LEVEL SECURITY';
  EXECUTE 'ALTER TABLE ti.lot_expiry_correction FORCE ROW LEVEL SECURITY';
  EXECUTE $p$CREATE POLICY tenant_isolation ON ti.lot_expiry_correction TO ti_app
             USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
             WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)$p$;
  -- The approved purge (DEC-072) covers this table like every tenant table.
  EXECUTE 'CREATE POLICY purge ON ti.lot_expiry_correction TO ti_purger USING (true)';
  EXECUTE 'GRANT SELECT, DELETE ON ti.lot_expiry_correction TO ti_purger';
  EXECUTE 'GRANT SELECT, INSERT ON ti.lot_expiry_correction TO ti_app';
END $$;

-- What the signer approves (DEC-090): an expiry correction names its new date in the approval itself, so a signature cannot be reused
-- for another date. Present exactly for ExpiryCorrection; part of the binding the guard keeps unchangeable.
ALTER TABLE ti.approval ADD COLUMN proposal jsonb;
ALTER TABLE ti.approval ADD CONSTRAINT approval_proposal_ck CHECK (
  (subject_type = 'ExpiryCorrection') = (proposal IS NOT NULL)
  AND (proposal IS NULL OR (jsonb_typeof(proposal) = 'object' AND proposal ? 'expiryDate')));

CREATE OR REPLACE FUNCTION ti.approval_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF (NEW.tenant_id, NEW.subject_type, NEW.subject_id, NEW.subject_version, NEW.content_digest, NEW.action, NEW.request_reason,
      NEW.requester_subject, NEW.created_at, NEW.created_by, NEW.proposal)
     IS DISTINCT FROM
     (OLD.tenant_id, OLD.subject_type, OLD.subject_id, OLD.subject_version, OLD.content_digest, OLD.action, OLD.request_reason,
      OLD.requester_subject, OLD.created_at, OLD.created_by, OLD.proposal) THEN
    RAISE EXCEPTION 'an approval binding cannot change (IAM-003)' USING ERRCODE = 'check_violation';
  END IF;
  IF OLD.approver_subject IS NOT NULL AND (NEW.approver_subject, NEW.meaning, NEW.sign_reason, NEW.reauth_reference, NEW.reauth_verified_at,
                                    NEW.signed_at, NEW.expires_at)
     IS DISTINCT FROM (OLD.approver_subject, OLD.meaning, OLD.sign_reason, OLD.reauth_reference, OLD.reauth_verified_at,
                       OLD.signed_at, OLD.expires_at) THEN
    RAISE EXCEPTION 'a signature or rejection cannot change' USING ERRCODE = 'check_violation';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'Pending' AND NEW.status IN ('Approved','Rejected','Invalidated','Expired'))
    OR (OLD.status = 'Approved' AND NEW.status IN ('Consumed','Invalidated','Expired'))) THEN
    RAISE EXCEPTION 'approval cannot move from % to %', OLD.status, NEW.status USING ERRCODE = 'check_violation';
  END IF;
  IF OLD.status NOT IN ('Pending','Approved') AND ROW(NEW.*) IS DISTINCT FROM ROW(OLD.*) THEN
    RAISE EXCEPTION 'a % approval is final', OLD.status USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;

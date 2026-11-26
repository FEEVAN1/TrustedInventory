-- 0027 (M5 slices 3-4): devices, credentials, readings, excursions and their holds and shipment flags (TEL-001 to TEL-005, DEC-077).
-- Expand-only. ti.excursion_status, ti.excursion_disposition, ti.alert and ti.celsius exist since 0001.
CREATE TABLE ti.device (
  id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id           uuid NOT NULL REFERENCES ti.tenant(id),
  external_id         ti.code_text NOT NULL,
  name                text NOT NULL CHECK (char_length(name) BETWEEN 1 AND 200),
  active              boolean NOT NULL DEFAULT true,
  last_reading_at     timestamptz,                 -- received time of the latest reading (staleness, TEL-002)
  last_observed_at    timestamptz,                 -- latest observedAt (late detection)
  created_at          timestamptz NOT NULL DEFAULT now(),
  created_by          ti.subject_text NOT NULL,
  updated_at          timestamptz NOT NULL DEFAULT now(),
  version             integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, external_id)
);

-- Rotating credentials (TEL-001): only a SHA-256 of the secret. A credential is valid until revoked or expired; issuing a new one
-- sets the previous one's expiry to 24 hours, and its first use expires the previous one at once.
CREATE TABLE ti.device_credential (
  id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id     uuid NOT NULL REFERENCES ti.tenant(id),
  device_id     uuid NOT NULL,
  secret_sha256 ti.sha256 NOT NULL,
  expires_at    timestamptz,
  first_used_at timestamptz,
  created_at    timestamptz NOT NULL DEFAULT now(),
  created_by    ti.subject_text NOT NULL,
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, device_id) REFERENCES ti.device (tenant_id, id)
);
CREATE INDEX device_credential_device_ix ON ti.device_credential (tenant_id, device_id, created_at);

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
  received_at         timestamptz NOT NULL,
  temperature_c       ti.celsius NOT NULL,
  is_late             boolean NOT NULL,
  created_at          timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, device_id, external_event_id),   -- duplicate eventId is a no-op (TEL-001)
  FOREIGN KEY (tenant_id, device_id) REFERENCES ti.device (tenant_id, id),
  FOREIGN KEY (tenant_id, zone_id) REFERENCES ti.zone (tenant_id, id),
  CHECK (observed_at <= received_at + interval '60 seconds')  -- TEL-002 future skew
);
CREATE INDEX telemetry_reading_zone_ix ON ti.telemetry_reading (tenant_id, zone_id, observed_at);

CREATE TABLE ti.excursion (
  id                      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id               uuid NOT NULL REFERENCES ti.tenant(id),
  zone_id                 uuid NOT NULL,
  device_id               uuid NOT NULL,
  storage_profile_id      uuid NOT NULL,
  trigger_reading_id      uuid NOT NULL,
  min_c                   ti.celsius NOT NULL,       -- the profile's limits at the time
  max_c                   ti.celsius NOT NULL,
  started_at              timestamptz NOT NULL,
  ended_at                timestamptz,
  first_abnormal_c        ti.celsius NOT NULL,
  last_abnormal_c         ti.celsius NOT NULL,
  is_historical           boolean NOT NULL DEFAULT false,   -- raised from a late reading (TEL-004)
  status                  ti.excursion_status NOT NULL DEFAULT 'Open',
  acknowledged_by         ti.subject_text,
  acknowledged_at         timestamptz,
  disposition             ti.excursion_disposition,
  disposition_approval_id uuid,
  resolved_by             ti.subject_text,
  resolved_at             timestamptz,
  last_modified_by        ti.subject_text NOT NULL,
  created_at              timestamptz NOT NULL DEFAULT now(),
  created_by              ti.subject_text NOT NULL,
  updated_at              timestamptz NOT NULL DEFAULT now(),
  version                 integer NOT NULL DEFAULT 1 CHECK (version > 0),
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, zone_id) REFERENCES ti.zone (tenant_id, id),
  FOREIGN KEY (tenant_id, device_id) REFERENCES ti.device (tenant_id, id),
  FOREIGN KEY (tenant_id, storage_profile_id) REFERENCES ti.storage_profile (tenant_id, id),
  FOREIGN KEY (tenant_id, trigger_reading_id) REFERENCES ti.telemetry_reading (tenant_id, id),
  FOREIGN KEY (tenant_id, disposition_approval_id) REFERENCES ti.approval (tenant_id, id),
  CHECK ((status = 'Resolved') = (disposition IS NOT NULL AND disposition_approval_id IS NOT NULL AND resolved_at IS NOT NULL AND resolved_by IS NOT NULL)),
  CHECK (status = 'Open' OR acknowledged_at IS NOT NULL)
);
-- One unresolved excursion per zone and profile: further abnormal readings extend it instead of opening another (AT-TEL-01).
CREATE UNIQUE INDEX excursion_unresolved_uq ON ti.excursion (tenant_id, zone_id, storage_profile_id) WHERE status <> 'Resolved';

CREATE TABLE ti.excursion_bucket (
  id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id      uuid NOT NULL REFERENCES ti.tenant(id),
  excursion_id   uuid NOT NULL,
  bucket_id      uuid NOT NULL,                 -- the Hold bucket the excursion moved stock into
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
  created_at        timestamptz NOT NULL DEFAULT now(),
  created_by        ti.subject_text NOT NULL,
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, excursion_id, shipment_line_id),
  FOREIGN KEY (tenant_id, excursion_id) REFERENCES ti.excursion (tenant_id, id),
  FOREIGN KEY (tenant_id, shipment_line_id) REFERENCES ti.shipment_line (tenant_id, id)
);

-- A monitored zone is stale when no active device assigned to it now has reported within the window (TEL-002, TEL-003).
CREATE FUNCTION ti.zone_telemetry_stale(p_zone uuid, p_now timestamptz, p_stale_seconds integer DEFAULT 300) RETURNS boolean
LANGUAGE sql STABLE AS $$
  SELECT EXISTS (
           SELECT 1 FROM ti.device_zone_assignment a JOIN ti.device d ON d.tenant_id = a.tenant_id AND d.id = a.device_id AND d.active
           WHERE a.zone_id = p_zone AND a.effective_from <= p_now AND (a.effective_to IS NULL OR a.effective_to > p_now))
     AND NOT EXISTS (
           SELECT 1 FROM ti.device_zone_assignment a JOIN ti.device d ON d.tenant_id = a.tenant_id AND d.id = a.device_id AND d.active
           WHERE a.zone_id = p_zone AND a.effective_from <= p_now AND (a.effective_to IS NULL OR a.effective_to > p_now)
             AND d.last_reading_at > p_now - make_interval(secs => p_stale_seconds))
$$;

-- Credential lookup before tenant context exists (TEL-001). Returns only what the caller needs to verify the secret.
CREATE POLICY credential_lookup ON ti.device_credential FOR SELECT TO ti_membership_reader USING (true);
CREATE POLICY credential_lookup ON ti.device FOR SELECT TO ti_membership_reader USING (true);
GRANT SELECT ON ti.device_credential, ti.device TO ti_membership_reader;
CREATE FUNCTION ti.device_for_credential(p_credential uuid)
RETURNS TABLE (tenant_id uuid, device_id uuid, secret_sha256 bytea, expires_at timestamptz, device_active boolean)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
  SELECT c.tenant_id, c.device_id, c.secret_sha256, c.expires_at, d.active
  FROM ti.device_credential c JOIN ti.device d ON d.tenant_id = c.tenant_id AND d.id = c.device_id
  WHERE c.id = p_credential
$$;
GRANT CREATE ON SCHEMA ti TO ti_membership_reader;
ALTER FUNCTION ti.device_for_credential(uuid) OWNER TO ti_membership_reader;
REVOKE CREATE ON SCHEMA ti FROM ti_membership_reader;
REVOKE ALL ON FUNCTION ti.device_for_credential(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ti.device_for_credential(uuid) TO ti_app;

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['telemetry_reading', 'excursion_bucket', 'excursion_shipment_flag'] LOOP
    EXECUTE format('CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON ti.%I FOR EACH ROW EXECUTE FUNCTION ti.reject_mutation()', t || '_append_only', t);
    EXECUTE format('CREATE TRIGGER %I BEFORE TRUNCATE ON ti.%I FOR EACH STATEMENT EXECUTE FUNCTION ti.reject_mutation()', t || '_no_truncate', t);
  END LOOP;
  FOREACH t IN ARRAY ARRAY['device', 'device_credential', 'device_zone_assignment', 'telemetry_reading', 'excursion', 'excursion_bucket',
                           'excursion_shipment_flag'] LOOP
    EXECUTE format('ALTER TABLE ti.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE ti.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format($p$CREATE POLICY tenant_isolation ON ti.%I TO ti_app
                     USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                     WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)$p$, t);
    EXECUTE format('CREATE POLICY purge ON ti.%I TO ti_purger USING (true)', t);
    EXECUTE format('GRANT SELECT, DELETE ON ti.%I TO ti_purger', t);
    IF t IN ('telemetry_reading', 'excursion_bucket', 'excursion_shipment_flag') THEN
      EXECUTE format('GRANT SELECT, INSERT ON ti.%I TO ti_app', t);
    ELSE
      EXECUTE format('GRANT SELECT, INSERT, UPDATE ON ti.%I TO ti_app', t);
    END IF;
  END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION ti.zone_telemetry_stale(uuid, timestamptz, integer) TO ti_app;

-- 0028 (M6 slice 1): operations metrics as aggregates across tenants (NFR-MONITOR, DEC-079). Expand-only.
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['alert', 'outbox_event', 'device', 'device_zone_assignment', 'stock_bucket', 'excursion'] LOOP
    EXECUTE format('CREATE POLICY metrics_reader ON ti.%I FOR SELECT TO ti_metrics_reader USING (true)', t);
    EXECUTE format('GRANT SELECT ON ti.%I TO ti_metrics_reader', t);
  END LOOP;
END $$;

CREATE FUNCTION ti.operations_metrics(p_now timestamptz)
RETURNS TABLE (ledger_mismatch_alerts bigint, outbox_max_age_seconds double precision, outbox_dead_lettered bigint, stale_devices bigint,
               held_buckets bigint, held_quantity numeric, open_excursions bigint)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
  SELECT
    (SELECT count(*) FROM ti.alert WHERE type = 'ReconciliationMismatch' AND status <> 'Closed'),
    (SELECT coalesce(extract(epoch FROM p_now - min(created_at)), 0) FROM ti.outbox_event WHERE sent_at IS NULL AND dead_lettered_at IS NULL),
    (SELECT count(*) FROM ti.outbox_event WHERE dead_lettered_at IS NOT NULL),
    (SELECT count(*) FROM ti.device d WHERE d.active
       AND EXISTS (SELECT 1 FROM ti.device_zone_assignment a WHERE a.tenant_id = d.tenant_id AND a.device_id = d.id
                   AND a.effective_from <= p_now AND (a.effective_to IS NULL OR a.effective_to > p_now))
       AND coalesce(d.last_reading_at, d.created_at) <= p_now - interval '300 seconds'),
    (SELECT count(*) FROM ti.stock_bucket WHERE quality_status = 'Hold' AND on_hand_qty > 0),
    (SELECT coalesce(sum(on_hand_qty), 0) FROM ti.stock_bucket WHERE quality_status = 'Hold'),
    (SELECT count(*) FROM ti.excursion WHERE status <> 'Resolved')
$$;
GRANT CREATE ON SCHEMA ti TO ti_metrics_reader;
ALTER FUNCTION ti.operations_metrics(timestamptz) OWNER TO ti_metrics_reader;
REVOKE CREATE ON SCHEMA ti FROM ti_metrics_reader;
REVOKE ALL ON FUNCTION ti.operations_metrics(timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ti.operations_metrics(timestamptz) TO ti_control;

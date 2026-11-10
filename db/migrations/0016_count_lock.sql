-- 0016 (M4): stock counts lock their bins against every quantity movement (EXC-003, DEC-008, DEC-059).
ALTER TABLE ti.stock_count ADD COLUMN last_modified_by ti.subject_text;
UPDATE ti.stock_count SET last_modified_by = created_by;
ALTER TABLE ti.stock_count ALTER COLUMN last_modified_by SET NOT NULL;

-- One place covers putaway, pick, unpick, dispatch, transfers, adjustments and receipts. Protective quality changes
-- (to Hold or Recalled) stay allowed because they change no physical quantity (DEC-008).
CREATE FUNCTION ti.stock_movement_count_lock() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  locked_bin uuid;
BEGIN
  IF NEW.movement_type = 'QualityChange' AND EXISTS (
       SELECT 1 FROM ti.stock_bucket b WHERE b.tenant_id = NEW.tenant_id AND b.id = NEW.destination_bucket_id
         AND b.quality_status IN ('Hold', 'Recalled')) THEN
    RETURN NEW;
  END IF;
  SELECT cb.bin_id INTO locked_bin
  FROM ti.stock_bucket b
  JOIN ti.stock_count_bin cb ON cb.tenant_id = b.tenant_id AND cb.bin_id = b.bin_id AND cb.lock_active
  WHERE b.tenant_id = NEW.tenant_id AND b.id IN (NEW.source_bucket_id, NEW.destination_bucket_id)
  LIMIT 1;
  IF locked_bin IS NOT NULL THEN
    RAISE EXCEPTION 'bin % is locked by a stock count', locked_bin USING ERRCODE = 'TI001';
  END IF;
  RETURN NEW;
END $$;

CREATE TRIGGER stock_movement_count_lock BEFORE INSERT ON ti.stock_movement
  FOR EACH ROW EXECUTE FUNCTION ti.stock_movement_count_lock();

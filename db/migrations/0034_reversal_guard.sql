-- 0034 (M6, M2 carry-over): reversal limits in the ledger (spec section 09 "Precision and reversals", STK-PRECISION, DEC-101).
-- A reversal refers to the original movement and cannot reverse more than its unreversed quantity. No application command posts
-- reversals yet (corrections use adjustments and returns); this guard makes the rule hold for any path that ever does, under
-- concurrency: reversals of one movement are serialized by a transaction-scoped advisory lock on the original movement.
CREATE FUNCTION ti.stock_movement_reversal_guard() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  v_original ti.stock_movement%ROWTYPE;
  v_reversed numeric;
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('reversal:' || NEW.tenant_id::text || ':' || NEW.reversal_of_id::text, 0));
  SELECT * INTO v_original FROM ti.stock_movement WHERE tenant_id = NEW.tenant_id AND id = NEW.reversal_of_id;
  IF v_original.movement_type = 'Reversal' THEN
    RAISE EXCEPTION 'A reversal cannot itself be reversed; post a new movement instead' USING ERRCODE = 'check_violation';
  END IF;
  IF v_original.product_id IS DISTINCT FROM NEW.product_id THEN
    RAISE EXCEPTION 'A reversal moves the product of the movement it reverses' USING ERRCODE = 'check_violation';
  END IF;
  SELECT coalesce(sum(base_qty), 0) INTO v_reversed FROM ti.stock_movement
  WHERE tenant_id = NEW.tenant_id AND reversal_of_id = NEW.reversal_of_id;
  IF v_reversed + NEW.base_qty > v_original.base_qty THEN
    RAISE EXCEPTION 'A reversal cannot exceed the unreversed quantity (% of % already reversed)', v_reversed, v_original.base_qty
      USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER stock_movement_reversal_guard BEFORE INSERT ON ti.stock_movement
  FOR EACH ROW WHEN (NEW.movement_type = 'Reversal') EXECUTE FUNCTION ti.stock_movement_reversal_guard();

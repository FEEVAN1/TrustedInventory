-- 0007 (M2): purchase order lines change only while the order is Draft (INB-001, DEC-047).
-- updatePurchaseOrder replaces all lines of a draft, which needs DELETE; the trigger makes that privilege no wider
-- than the rule: inserts, deletes and changes to ordered fields are refused once the order leaves Draft.
-- received_base_qty (and the row stamps) stay updatable in any status so receiving can record progress.

CREATE FUNCTION ti.purchase_order_line_draft_only() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  line ti.purchase_order_line := CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
  order_status ti.purchase_order_status;
BEGIN
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

CREATE TRIGGER purchase_order_line_draft_only
  BEFORE INSERT OR UPDATE OR DELETE ON ti.purchase_order_line
  FOR EACH ROW EXECUTE FUNCTION ti.purchase_order_line_draft_only();

GRANT DELETE ON ti.purchase_order_line TO ti_app;

-- 0011 (M3): reconciliation also compares reserved quantities with live reservations (STK-004, DEC-053).
-- A block row now records both measures; at least one must differ.
ALTER TABLE ti.stock_bucket_block
  ADD COLUMN expected_reserved_qty numeric,
  ADD COLUMN stored_reserved_qty   numeric;
ALTER TABLE ti.stock_bucket_block DROP CONSTRAINT stock_bucket_block_check;
ALTER TABLE ti.stock_bucket_block
  ADD CONSTRAINT stock_bucket_block_mismatch CHECK (
    expected_qty IS DISTINCT FROM stored_qty
    OR expected_reserved_qty IS DISTINCT FROM stored_reserved_qty);

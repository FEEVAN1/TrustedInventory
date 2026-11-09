-- 0012 (M3): the contract allows a 100-character dispatch reference; code_text stopped at 64 (DEC-055).
ALTER TABLE ti.shipment ALTER COLUMN dispatch_reference TYPE ti.identifier_text;

-- 0002: one ACTIVE conversion per product and alternate unit (DEC-029).
-- 0001 counted retired rows too, so a retired conversion could never be recreated. Same pattern as
-- product_barcode_active_gtin_uq. Expand-only change: the new index is stricter on active rows only.

CREATE UNIQUE INDEX product_conversion_active_uq
  ON ti.product_conversion (tenant_id, product_id, alternate_uom_id) WHERE active;

ALTER TABLE ti.product_conversion
  DROP CONSTRAINT product_conversion_tenant_id_product_id_alternate_uom_id_key;

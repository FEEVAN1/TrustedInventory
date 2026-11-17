-- 0029 (M6 slice 3): read paths found slow by the section 18 benchmark (NFR-PERF, DEC-081). Expand-only, indexes only.
-- Latest movement per bucket (stock explorer) and newest-first movement lists, overall and by product.
CREATE INDEX stock_movement_source_recent_ix ON ti.stock_movement (tenant_id, source_bucket_id, occurred_at DESC) WHERE source_bucket_id IS NOT NULL;
CREATE INDEX stock_movement_destination_recent_ix ON ti.stock_movement (tenant_id, destination_bucket_id, occurred_at DESC)
  WHERE destination_bucket_id IS NOT NULL;
CREATE INDEX stock_movement_recorded_ix ON ti.stock_movement (tenant_id, recorded_at DESC, id DESC);
CREATE INDEX stock_movement_product_recorded_ix ON ti.stock_movement (tenant_id, product_id, recorded_at DESC, id DESC);

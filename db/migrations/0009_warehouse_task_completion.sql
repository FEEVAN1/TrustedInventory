-- 0009 (M2): who completed a warehouse task and how an exception was resolved (INB-005/006, DEC-050).
-- The contract's WarehouseTask exposes completedBy and resolution; 0006 had neither.
ALTER TABLE ti.warehouse_task
  ADD COLUMN completed_by ti.subject_text,
  ADD COLUMN resolution   text CHECK (resolution IS NULL OR char_length(resolution) BETWEEN 1 AND 2000);
ALTER TABLE ti.warehouse_task
  ADD CONSTRAINT warehouse_task_completion
  CHECK ((status = 'Completed') = (completed_at IS NOT NULL) AND (completed_at IS NULL) = (completed_by IS NULL));

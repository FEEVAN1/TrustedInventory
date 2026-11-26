-- 0025 (M5 slice 1): quality dispositions that need approval bind one bucket (DEC-075). Expand-only.
-- The new label is not used in this script (PostgreSQL forbids using a value added in the same transaction).
ALTER TYPE ti.approval_subject_type ADD VALUE IF NOT EXISTS 'QualityDisposition';

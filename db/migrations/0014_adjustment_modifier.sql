-- 0014 (M4): who last changed an adjustment, so its signer can differ from them (SOD-1, DEC-057).
ALTER TABLE ti.adjustment ADD COLUMN last_modified_by ti.subject_text;
UPDATE ti.adjustment SET last_modified_by = created_by;
ALTER TABLE ti.adjustment ALTER COLUMN last_modified_by SET NOT NULL;

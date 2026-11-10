-- 0015 (M4): who last changed a transfer, so a transit disposition signer can differ from them (SOD-1, DEC-058).
ALTER TABLE ti.transfer ADD COLUMN last_modified_by ti.subject_text;
UPDATE ti.transfer SET last_modified_by = created_by;
ALTER TABLE ti.transfer ALTER COLUMN last_modified_by SET NOT NULL;

-- 0020 (S2 slice 5): reconciliation evidence and payment reminders (DEC-066). Expand-only.

-- How an inbox event arrived: a signed webhook, or the reconciliation catch-up over the provider's authenticated API.
ALTER TABLE cp.webhook_inbox ADD COLUMN received_via text NOT NULL DEFAULT 'Webhook'
  CHECK (received_via IN ('Webhook','Reconciliation'));

ALTER TYPE cp.billing_notice_kind ADD VALUE IF NOT EXISTS 'PaymentReminder';

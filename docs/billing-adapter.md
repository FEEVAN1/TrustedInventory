# Billing adapter

How TrustedInventory talks to a subscription payment provider (spec v3.0 section 25, DEC-039). The
provider is an **owner decision (D09)**; until it is made, the only implementation is the **simulator**
described below, and live checkout is disabled by configuration. Schema: `db/design/saas-schema-draft.sql`
(schema `cp`); flows: `docs/migration-design.md` §10.

## Principles

1. **The provider holds card data; we never do.** Checkout and payment-method management are
   provider-hosted pages. The API never receives or stores PAN or CVV (BIL-002).
2. **Only verified provider events change billing state.** A browser returning from checkout is
   navigation, not proof of payment (BIL-002, AT-BIL-01). The UI shows *Pending* and polls until the
   server has processed a verified event (SUI-001).
3. **Money is integer minor units** with an ISO 4217 currency and exponent; amounts are snapshots and are
   never recalculated from catalog values (BIL-001, ENT-001). No currency conversion, no proration in R1.
4. **Tax comes from the provider** under owner-approved configuration; the application never infers
   rates (BIL-001).
5. **Billing never changes inventory.** Counters, invoices and webhooks touch schema `cp` only; restriction
   is computed from subscription state (DEC-033), and the control-plane role cannot read stock (SAS-004).

## Adapter contract (`IBillingProvider`)

All calls are made by the control plane (`ti_control`). Every mutating call carries an idempotency key
derived from our own record id, so a retried call never creates a second provider object.

| Operation | Input | Output | Idempotency key |
|-----------|-------|--------|-----------------|
| `EnsureCustomer` | billing account (legal name, country, contact) | `providerCustomerId` | `customer:{billingAccountId}` |
| `CreateCheckoutSession` | customer, provider price ref, accepted preview digest, success/cancel URLs from the redirect allowlist | `providerSessionRef`, `hostedUrl`, `expiresAt` | `checkout:{checkoutSessionId}` |
| `CreatePortalSession` | customer, return URL from the allowlist | short-lived `url` | `portal:{billingAccountId}:{minute}` |
| `ScheduleChange` | provider subscription ref, target price ref, effective date | provider change ref | `change:{subscriptionChangeId}` |
| `CancelScheduledChange` | provider subscription ref | — | `change-cancel:{subscriptionChangeId}` |
| `CancelAtPeriodEnd` / `Resume` | provider subscription ref | provider state | `cancel:{subscriptionId}:{revision}` / `resume:{subscriptionId}:{revision}` |
| `VerifyWebhook` | raw body bytes, headers | verified event envelope or rejection | — |
| `FetchSubscription` / `FetchInvoice` | provider ref | authoritative current resource incl. provider revision | — |
| `ListChangedSince` | cursor | page of subscriptions and invoices (reconciliation) | — |

`VerifyWebhook` rules (BIL-004):
- Verify the provider signature over the **raw request body** exactly as received, before any parsing.
- Enforce the provider's replay window: reject timestamps outside the tolerance (simulator: 5 minutes).
- Compare signatures in constant time; support two active secrets during rotation.
- Invalid signature → 401, malformed payload → 400, no inbox row, no entitlement (AT-BIL-02).

## Webhook inbox

`POST /api/v1/webhooks/billing/{provider}` needs no tenant header and no user token.

1. Verify the signature (above).
2. Insert into `cp.webhook_inbox` with `raw_body_digest`, the **encrypted** payload, a redacted summary and
   state `Received`. `UNIQUE (provider, event_id)` makes duplicates no-ops: a duplicate returns 200 without
   reprocessing.
3. Return 200 only after the row is committed. If persistence fails, return 503 so the provider retries.
4. A worker leases `Received`/`Failed` rows (`FOR UPDATE SKIP LOCKED`) and processes each in its own
   transaction:
   - Resolve the tenant from **our** records (`provider_customer_id`, `provider_subscription_ref`,
     `provider_invoice_ref`), never from a tenant id in the payload. An unknown reference →
     `Quarantined` plus an operator task (AT-BIL-02).
   - Fetch the **authoritative** resource from the provider (or use its monotonic revision) and apply it
     only if it is newer than what we hold (`provider_revision` / provider timestamps). An old
     `invoice.payment_failed` arriving after `invoice.paid` cannot regress a paid period (BIL-005).
   - Commit subscription state, the new entitlement snapshot (if plan or period changed), invoice/payment
     rows, audit events and billing notices together (section 27 consistency rules). Notices go to `cp.billing_notice`
     and a tenant-context worker job relays them to the outbox, because `ti_control` has no outbox privilege (DEC-064).
   - Mark `Processed` (or `Ignored` for event types we do not use). Failures retry with backoff
     (`min(30 s × 2^attempts, 1 h)`) up to 10 attempts, then stay `Failed`; the `WebhookBacklog` alert arrives with slice 5.
   - Payload at rest: AES-256-GCM with `Billing:InboxKey`, bound to provider and event id (DEC-064).
5. Lifecycle precedence still applies: a payment can settle dues but never clears a security suspension,
   and no event recreates or reactivates a `Deleted` tenant (LFC-001, AT-BIL-04).

Never log full payloads; logs carry the event id, type, tenant id (once resolved) and trace id only.

## State changes driven by verified events

| Verified event | Effect |
|----------------|--------|
| Initial invoice paid for a checkout | Subscription `Trialing → Active`, period set, new entitlement snapshot; checkout `Completed` |
| Renewal invoice paid | Period advanced; `PastDue → Active`; grace cleared |
| Renewal payment failed | `Active → PastDue` once; `grace_end = due date + 7 days` (D10). Repeated failures do not extend grace (BIL-007) |
| Dispute, chargeback, refund | Payment/refund rows and an **operator review task**; no automatic restriction or deletion (BIL-008) |
| Subscription deleted by the provider (after a cancellation) | `Cancelled`; any open change cancelled (DEC-065); access was already restricted from `period_end` by computation, not by this event |

## Plan changes (DEC-065)

A change takes effect at the current `period_end`. It is not a webhook effect: the worker's `PlanChangeJob` (every
`Worker:PlanChangeIntervalSeconds`, default 60) rechecks each due change: stock blockers in the tenant's transaction, then the
quota blockers again under the tenant's usage counter locks. A blocked change is cancelled at the provider, marked `Blocked`
with its blockers and the owner is notified; an upgrade waits for the paid invoice of its first period; otherwise the
subscription moves to the target plan and price with a new entitlement snapshot.

## Reconciliation

A daily job (and one after any provider outage) lists provider subscriptions and invoices changed since
its last cursor and compares them with `cp.subscription`, `cp.invoice` and current entitlement snapshots.
Differences are corrected through the same apply path as webhooks and recorded as audit events; anything
it cannot resolve becomes a `BillingReconciliation` alert. During a provider outage the last verified
entitlement stays in force; tenants are **not** restricted because the provider is unreachable (BIL-005,
SOP-003, AT-SOP-01). Alert when the oldest unprocessed inbox row is older than five minutes.

## Simulator (development and tests only)

Implementation name: `SimulatedBillingProvider`, labelled `[SIMULATED BILLING]` in logs and UI.

- Signs webhooks with HMAC-SHA256 using `Billing:Simulator:SigningSecret` (required, at least 32 characters; startup fails
  without it) in the header `Simulator-Signature: t=<unix seconds>,v1=<hex>` over `"{t}.{raw body}"`, so the real
  verification path is exercised. `Billing:Simulator:PreviousSigningSecret` is accepted during rotation.
- Its state lives in memory in the process that hosts it (DEC-063). Hosted checkout and portal links are
  `https://billing-simulator.invalid/...` URLs that cannot be opened; outcomes are driven through the test console
  (a browsable console is UI-milestone work). The `Outage` switch and the payment actions below are available now (DEC-064); events are
  delivered by posting the signed `SimEvent` to the webhook route.
- Catalog prices without a provider reference are registered on first checkout as `sim_price_{priceVersionId}`.
- Test console actions (also callable from integration tests):
  - `PayCheckout`, `FailCheckout`, `PayRenewal`, `FailRenewal`, `Dispute` (or chargeback), `Refund`; `Resend` repeats an
    event under a new id. Each returns a signed `SimEvent` (exact bytes and signature header).
  - **Deliver events out of order**, **duplicate** an event, **forge** a signature, send an **unknown
    reference**.
  - **Outage**: make every adapter call fail until cleared, to test retained access and reconciliation.
  - **Advance the clock** for trial end, renewal and grace boundaries (controllable `TimeProvider`).
- Prices come from `docs/entitlement-catalog.json` (currency XTS, fixtures).

Configuration: `Billing:Provider` = `simulator` in development and tests. Any other value requires an
owner-approved provider configuration, and `Billing:LiveCheckoutEnabled` stays `false` until then. Startup
refuses any other provider and live checkout switched on. `Billing:ReturnUrl` (absolute http(s), default
`http://localhost:5173/billing/return`) is where hosted pages send the browser back: navigation only.

## Must never happen

- Storing or logging card numbers, CVV or full provider payloads.
- Treating a return URL, client-side state or browser storage as proof of payment or entitlement (SUI-001).
- Running live charges, real provider calls or destructive deletion in automated tests (section 30).
- Trusting a tenant id supplied by a client or a webhook payload.
- Restricting tenants because the billing provider is down.

## Evidence required before commercial activation (section 30)

| Evidence | Test |
|----------|------|
| Billing sandbox run (simulator now; real provider sandbox once chosen) | S2 suite |
| Signed-webhook replay, duplicate and forgery | AT-BIL-01, AT-BIL-02 (`WebhookTests`, passing on the simulator) |
| Out-of-order events cannot regress a paid period | AT-BIL-02 |
| Provider outage keeps last verified access; reconciliation converges | AT-SOP-01 |
| Trial and grace boundaries restrict without workers; safety still works | AT-BIL-03 |
| Payment during security suspension, payment after deletion | AT-BIL-04 |
| Concurrent quota reservations | AT-ENT-01, AT-ENT-03 |
| Invoice reconciliation | S2 reconciliation tests |

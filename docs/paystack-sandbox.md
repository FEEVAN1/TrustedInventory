# Paystack test-mode run (S2 gate)

The S2 gate needs AT-BIL and AT-ENT evidence against the real provider's sandbox (DEC-067, DEC-068). Automated tests cover
the adapter against a labelled Paystack API simulator; this run checks the adapter against **Paystack test mode** itself.
It is run by the owner, with test keys that never enter the repository. **Never use a live key** (`sk_live_…`): startup
refuses it, and `Billing__LiveCheckoutEnabled` must stay `false`.

## 1. Prepare

1. In the Paystack dashboard, switch to **Test Mode** and copy the **test secret key** (`sk_test_…`) from
   *Settings → API Keys & Webhooks*.
2. In your local `.env` (never committed), add:
   ```
   TI_BILLING_PROVIDER=paystack
   TI_PAYSTACK_SECRET_KEY=sk_test_...
   ```
   Leave `TI_BILLING_SIM_SECRET` and `TI_INBOX_KEY` set as before.
3. Give one synthetic Keycloak user the platform billing role: set `Platform__BillingSubjects__0` (API service in
   `docker-compose.yml`) to that user's subject. Use a different user from `platform-admin` (SOD-8).
4. Start the stack (`docker compose up -d`) and expose the API publicly with a tunnel of your choice to port 8081.
5. In *Settings → API Keys & Webhooks* (test mode) set the **Webhook URL** to
   `https://<your-tunnel>/api/v1/webhooks/billing/paystack`.
6. Optional: set `Billing__Paystack__WebhookSourceIps` to Paystack's published webhook addresses to check the allowlist; leave
   it empty if your tunnel hides the caller's address.

## 2. Run

Use the web app or HTTP calls with the synthetic users. Record each result in the table below.

| # | Step | Expected | Evidence for |
|---|------|----------|--------------|
| 1 | Platform billing: create a plan version, add an NGN monthly price (e.g. `minorUnits: 500000`, exponent 2), publish | 201/200; the price shows `displayApproved: true` | D09, ENT-001 |
| 2 | Sign up a new workspace (trial) as a synthetic owner | `Trialing` | ONB, BIL-003 |
| 3 | Preview checkout for the NGN price, then create the checkout | `hostedUrl` on `checkout.paystack.com`; the Paystack dashboard shows the plan and customer | BIL-002, BIL-003 |
| 4 | Open the hosted page and **close it without paying**; poll `GET /saas/checkout/{id}` and `GET /saas/subscription` | Session `Open`, subscription `Trialing` | AT-BIL-01 |
| 5 | Create a new checkout and **pay with a Paystack test card** (from Paystack's current test-card list) | Within a minute: subscription `Active`, `periodEnd` = Paystack's next payment date, one paid NGN invoice | AT-BIL-01, BIL-001 |
| 6 | In the dashboard, **resend** the `charge.success` webhook | 200 `duplicate: true`; still one activation in the audit trail | AT-BIL-01, BIL-004 |
| 7 | Send a request to the webhook URL with a wrong `x-paystack-signature` | 401; nothing in `listWebhookEvents` | AT-BIL-02 |
| 8 | Open the billing portal (`POST /saas/billing-portal`) | A Paystack manage-subscription link | BIL-002 |
| 9 | Preview and schedule a change to a second NGN plan | 202 `Scheduled`; in Paystack a new subscription starting at the period end, the old one cancelled | ENT-005 |
| 10 | Cancel the subscription as the owner (`POST /saas/subscription/cancel`) | `cancelAtPeriodEnd: true`; Paystack subscription disabled; the `subscription.disable` webhook leaves access `Normal` until the period end | BIL-008 |
| 11 | Resume (`POST /saas/subscription/resume`) | `cancelAtPeriodEnd: false`; Paystack subscription active again | BIL-008 |
| 12 | Platform billing: `GET /platform/v1/billing/webhook-events` | Every event `Processed` or `Ignored`; none `Failed` or `Quarantined` | BIL-004, SOP-003 |

Renewal success and failure, disputes and refunds cannot be triggered on demand in test mode. They are covered by the
simulator tests (`PaystackTests`, `WebhookTests`); record them as "not run" unless Paystack test mode produces them.

## 3. Report

Send the table with results and any `[PAYSTACK TEST MODE]` log lines for failing steps. Logs contain method, path and
status only: no keys, cards or payloads. Then remove the test key from `.env` or rotate it in the dashboard.

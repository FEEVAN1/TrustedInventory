# SaaS state matrix

What a tenant may do in each state (spec v3.0 sections 06, 22, 26; DEC-033, DEC-034). This file is the
single source for lifecycle gating: every operation in `openapi.yaml` declares one access category
(`x-access`), and the request pipeline allows or rejects it using the table below. Endpoints never add
their own lifecycle checks.

## Stored state versus computed access mode

| Stored on the tenant (control plane) | Values |
|--------------------------------------|--------|
| `lifecycle` | `Provisioning`, `Ready`, `Archived`, `DeletionPending`, `Deleted` |
| security suspension | none, or `{reason, at, by}` set by platform operations |

| Stored on the subscription | Values |
|----------------------------|--------|
| `state` | `Trialing`, `Active`, `PastDue`, `Cancelled` |
| clocks | `trial_end`, `period_end`, `grace_end`, `cancel_at_period_end` |

**Billing restriction is computed, never stored.** At request time `now` it applies when:
- `Trialing` and `now ≥ trial_end`; or
- `PastDue` and `now ≥ grace_end`; or
- `Cancelled`; or `cancel_at_period_end` and `now ≥ period_end`.

`PastDue` before `grace_end` is **not** restricted (normal access plus a billing banner).

### Access mode (LFC-001 precedence, highest first)

| Mode | Condition |
|------|-----------|
| `Deleted` | lifecycle = Deleted |
| `Closed` | lifecycle = Archived or DeletionPending |
| `SecuritySuspended` | security suspension present |
| `BillingRestricted` | billing restriction computed above |
| `Provisioning` | lifecycle = Provisioning |
| `Normal` | lifecycle = Ready and none of the above |

A tenant can carry several reasons at once (for example security-suspended **and** unpaid). The mode is
the highest; `restrictionReasons` lists all of them. Paying never clears a security suspension
(BIL-007, AT-BIL-04); no billing event can move a tenant out of `Deleted` (LFC-001).

## Access categories

| Category | Covers |
|----------|--------|
| `identity` | `GET /me`, workspace switching |
| `setup-status` | Provisioning status, closure status |
| `read` | Every operational read: catalog, locations, stock, documents, audit, verification |
| `export` | Create and download exports, including the tenant archive (LFC-003) |
| `routine-write` | Every create, update, retire or command not listed in another category, including receipts, putaway, allocation, picking, dispatch, transfers, counts, adjustments, release-to-available, and all master-data changes |
| `safety` | Quality holds and damage/disposal dispositions, recall create/outreach/close, alert and excursion acknowledge/resolve without release, reservation deallocation, unpick, order cancellation (which only releases stock) |
| `team-admin` | Invite, accept invitation, create membership, grant role |
| `protective-admin` | Deactivate membership, revoke role grant, revoke invitation (reducing access is always allowed) |
| `billing` | Subscription and entitlement views, usage, checkout, plan change, invoices, billing portal |
| `ownership` | Cancel or resume subscription, ownership transfer, closure request |
| `telemetry-ingest` | Device readings (service principals only) |

Platform routes (`/platform/v1/*`), public plan reads and billing webhooks are outside tenant gating.

## Matrix (human and service API calls)

✓ allowed (still subject to membership, permission, entitlement and quota) · ✗ rejected

| Category | Normal | Provisioning | BillingRestricted | SecuritySuspended | Closed | Deleted |
|----------|:------:|:------------:|:-----------------:|:-----------------:|:------:|:-------:|
| identity | ✓ | ✓ | ✓ | ✓ ¹ | ✓ | ✗ |
| setup-status | ✓ | ✓ ² | ✓ | ✗ | ✓ ² | ✗ |
| read | ✓ | ✗ | ✓ | ✗ | ✗ | ✗ |
| export | ✓ | ✗ | ✓ | ✗ ³ | ✓ ² | ✗ |
| routine-write | ✓ | ✗ | ✗ | ✗ | ✗ | ✗ |
| safety | ✓ | ✗ | ✓ | ✗ ⁴ | ✓ ⁵ | ✗ |
| team-admin | ✓ | ✗ | ✗ | ✗ | ✗ | ✗ |
| protective-admin | ✓ | ✗ | ✓ | ✗ | ✓ | ✗ |
| billing | ✓ | ✗ | ✓ | ✗ | ✓ ⁶ | ✗ |
| ownership | ✓ | ✗ | ✓ | ✗ | ✗ ⁷ | ✗ |
| telemetry-ingest | ✓ | ✗ | ✓ | ✓ ⁴ | ✓ ⁵ | ✗ |

1. Only to see the restriction and reach identity recovery; tenant data is not returned.
2. Owner only.
3. Export during a security suspension needs a security review through platform operations.
4. Human users are blocked. Safety **workers** and device ingestion continue under least-privilege
   service roles: readings are stored, excursions open, holds are placed and alerts recorded.
5. While controlled stock, recalls or retention obligations remain (section 26).
6. Read invoices and settle outstanding dues during closure review; no new checkout.
7. Closure status is `setup-status`; cancelling a closure is a platform-operations action.

**Rejections:** `Provisioning` → 409 `INVALID_STATE` ("workspace is still being set up");
`BillingRestricted` → 403 `SUBSCRIPTION_RESTRICTED` with the recovery action; `SecuritySuspended` → 403
`TENANT_SECURITY_SUSPENDED`; `Closed` → 403 `SUBSCRIPTION_RESTRICTED` naming the closure;
`Deleted` → 403 `FORBIDDEN` (membership is revoked, so the request normally fails earlier). A billing lock
is never reported as a stock shortage (section 29).

## Rules that cross categories

- **Safety ignores entitlements.** A `safety` operation is never refused with `FEATURE_NOT_ENABLED`,
  even after a downgrade removed `medical_storage` or `telemetry` (ENT-006, SUI-003).
- **Quotas apply only to `routine-write` and `team-admin` creations** (ENT-004): reads, edits, stock
  operations on existing records and every safety action stay allowed at the limit.
- **Outcome-dependent operations.** An excursion resolution or quality disposition that makes stock
  *available* is `routine-write` in effect: the service rejects that outcome in `BillingRestricted` and
  `Closed` with `SUBSCRIPTION_RESTRICTED`, while hold, damage and disposal outcomes stay allowed.

## Background work by mode

| Job | Normal / Provisioning | BillingRestricted | SecuritySuspended | Closed | Deleted |
|-----|:--:|:--:|:--:|:--:|:--:|
| Outbox delivery | ✓ | ✓ | ✓ | ✓ | ✗ |
| Telemetry evaluation, stale detection, excursion holds | ✓ | ✓ | ✓ | ✓ ⁵ | ✗ |
| Reservation expiry (releases stock, never consumes it) | ✓ | ✓ | ✓ | ✓ | ✗ |
| Export generation (exports already requested; new requests follow the `export` row) | ✓ | ✓ | ✓ | ✓ | ✗ |
| Ledger reconciliation and audit verification | ✓ | ✓ | ✓ | ✓ | ✗ |
| Audit checkpoints (sign the chain head) | ✓ | ✓ | ✓ | ✓ | ✓ (retained chain only, as deletion evidence; DEC-072) |
| Idempotency cleanup | ✓ | ✓ | ✓ | ✓ | ✗ |
| Billing reminders and reconciliation | ✓ | ✓ | ✓ | ✓ | ✗ |
| Approved deletion (control plane; owner request, platform approval, export window ended, no hold, no safety obligation) | ✗ | ✗ | ✗ | ✓ | ✗ |

Approved deletion is a control-plane job, not a per-tenant worker row in `BackgroundWork`: it acts only on `Archived` or
`DeletionPending` tenants, and `cp.purge_tenant()` checks the approval again in the database (DEC-072).

## Evidence

AT-BIL-03, AT-BIL-04, AT-LFC-01 and AT-SAS-01 exercise this matrix. A table-driven integration test runs
every category in every mode against one endpoint per category.

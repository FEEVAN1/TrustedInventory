# Tenant offboarding

Cancellation, closure, export and deletion for a subscribing business (spec v3.0 section 26, LFC-001 to
LFC-004). Windows below are **proposed defaults (D10)** and must be approved before launch; legal holds and
record-specific retention always override them. Schema: `cp.deletion_record`, `ti.tenant` lifecycle
columns (`db/design/saas-schema-draft.sql`). Access rules per state: `docs/saas-state-matrix.md`.

## Three separate actions

| Action | Who | What it does | What it never does |
|--------|-----|--------------|--------------------|
| **Cancel subscription** | Tenant owner (`subscription.cancel`) | Sets `cancel_at_period_end`. Paid access continues until `period_end`, then the tenant is billing-restricted (read, export, safety, billing recovery). `resume` clears a still-pending cancellation. | Delete data, refund, or remove users (BIL-008) |
| **Request closure** | Tenant owner (`tenant.close`) + re-authentication ≤ 5 min | Runs the closure checks below; on success moves the tenant to `Archived` and opens the export window | Delete anything immediately |
| **Delete** | Explicit owner request inside closure, or an approved retention policy | Moves `Archived → DeletionPending → Deleted` once no blocker remains | Happen automatically because a trial expired (LFC-002) |

The UI keeps *cancel subscription* and *delete workspace* as different actions with different
confirmations (section 29). A tenant administrator alone can do none of them (ONB-006).

## Closure request (`POST /api/v1/saas/closure`)

Input: `reason`, `requestedDeletion` (true/false), explicit confirmation, fresh re-authentication.

Checks, all recorded in `cp.deletion_record.hold_results`:

1. **Outstanding billing:** unpaid invoices are listed; the owner can still settle them (billing category
   stays available while closing).
2. **Controlled stock and safety obligations:** medical stock on hand or in transit, open recalls,
   unresolved excursions, active holds. Any of these returns **409 `RETENTION_BLOCKED`** with the
   blockers the caller is authorised to see; safety monitoring continues.
3. **Retention and legal hold:** `ti.tenant.retention_hold` or a record-class retention rule (D05) blocks
   deletion, not closure.
4. **Export offer:** the response links the tenant archive export (below).

On success: subscription set to end at period end (if not already), `lifecycle = Archived`,
`archived_at` set, deletion record `ExportWindow` with `export_window_ends_at = subscription end + 90
days` (proposed). Routine operations stop; the owner keeps access to exports and closure status.

## Tenant archive export (LFC-003)

`POST /api/v1/exports` with `type = TenantArchive`, owner only. Generated asynchronously; download through
signed links that expire after 15 minutes; scope is enforced both when generating and when downloading.

Contents (JSON and CSV, one tenant only):
- Catalog: units, products, conversions, barcodes, storage profiles, partners.
- Locations: warehouses, zones, bins.
- Stock: current balances by bucket, the full movement ledger, lots and serials with lineage
  (receipt → movements → shipment).
- Orders and documents: purchase orders, receipts, outbound orders, shipments, transfers, counts,
  adjustments, returns, recalls.
- Approvals and signatures (meanings, digests, signers; never credentials).
- Audit: event list plus an **integrity manifest** (stream range, last hash, checkpoint signatures) so the
  customer can verify the chain offline.
- Attachments: manifest (name, type, size, SHA-256) and the files the requester is authorised to read.
- `manifest.json`: tenant id, generation time, filter criteria, file list with SHA-256 per file.

Never included: other tenants' data, provider payloads, tax references the requester may not see, secrets.

## Deletion (LFC-004)

Preconditions: closure exists, export window ended or the owner explicitly waived it, no blocker, approval
recorded (`approved_by`, `approved_at`).

Steps (one control-plane job, each step idempotent and recorded):
1. `lifecycle = DeletionPending`; revoke all memberships, invitations and service credentials; invalidate
   sessions and tokens.
2. Remove eligible live data: inventory, catalog, documents, attachments and exports for the tenant.
3. Keep what must be kept, with restricted access and a documented reason:
   - billing records required by law (invoices, payments),
   - audit evidence required by the approved retention schedule,
   - minimal deletion evidence: the tenant row (`lifecycle = Deleted`, `deleted_at`) and its
     `cp.deletion_record`.
4. `lifecycle = Deleted`; deletion record `Deleted` with `completed_at` and `backup_expiry_at` (the date the
   last backup containing the data ages out under the approved backup schedule).

We never claim immediate erasure from backups. A deleted dataset is never reactivated; a late billing event
cannot recreate it (LFC-001). There is no seven-year blanket purge: legal holds and record-specific
policies prevail.

## Deletion register and backup restore

`cp.deletion_record` rows with status `Deleted` form the **deletion register**. Every restore (disaster
recovery or drill, AT-OPS-01) must, **before any tenant access is opened**:

1. Restore into an isolated environment with API and workers stopped.
2. Replay the register from the live control plane (or its own retained copy) against the restored data:
   for each deleted tenant, repeat deletion steps 1–4.
3. Verify no deleted tenant has memberships, credentials or live data; record the verification.
4. Only then open access. AT-LFC-02 proves a deleted tenant stays inaccessible after restore.

## Platform operations steps

| Situation | Action |
|-----------|--------|
| Closure blocked by safety obligations | Nothing to override; the customer's QA resolves stock, recalls or excursions |
| Legal hold | Set `retention_hold`; deletion waits; record the instruction reference |
| Owner unreachable during closure | Follow the approved retention policy; never delete without it |
| Security suspension during closure | Export needs security review (state matrix note 3) |
| Withdraw closure | Platform operations sets the record to `Withdrawn` and the tenant back to `Ready` with a reason (audited) |

## Acceptance tests

| Test | Proves |
|------|--------|
| AT-LFC-01 | Cancel keeps access until the boundary, then restricts; ownership transfer cannot leave zero owners; deletion blocked by controlled stock or legal hold |
| AT-LFC-02 | Export contains only the requester's tenant and complete lineage; expired or revoked links fail; restore with the deletion register keeps a deleted tenant inaccessible |
| AT-BIL-04 | Payment after deletion never recreates the tenant |

Destructive deletion is never run as an automated acceptance test against shared or production data
(section 30); tests use throwaway databases.

# Operations

Status: **M1 drafts.** The runbooks below describe the components built so far and were written before
any production deployment exists. Commands are the development equivalents; production tooling (D06)
replaces them. Final runbooks are an M6 gate item.

## Proposed targets (spec section 18, not yet measured)

| Area | Target | Evidence required |
|------|--------|-------------------|
| Performance | p95 stock query ≤ 1 s, stock command ≤ 2 s at 50 users / 1 M movements | Staging benchmark report (M6) |
| Availability | 99.9 % monthly | Redundant API + managed DB failover (D06) |
| Recovery | RPO ≤ 15 min, RTO ≤ 4 h | AT-OPS-01 restore drill |
| Jobs | Leased per tenant/task, idempotent, 10 retries then dead letter | NFR-JOBS tests |
| Exports | Async, 15-minute download links | NFR-EXPORTS tests |

## Health endpoints

| Route | Meaning |
|-------|---------|
| `GET /health/live` | Process is running (no dependency checks) |
| `GET /health/ready` | Database reachable and the schema version this build requires is applied |

## Alerts that block stock (OPS-001)

A ledger reconciliation mismatch or an audit-chain verification failure raises a High alert and blocks
stock mutations on the affected buckets / tenant stream until an operator completes the audited repair
procedure. Never repair a balance with an unexplained direct update (STK-004). In M1 the audit-chain
alert is raised; blocking stock mutations arrives with the ledger in M2.

## Runbooks

### Deployment and rollback (draft)
1. Build images from a tagged commit; restores run in locked mode, so a lock-file drift fails the build.
2. Run the migrator (`TrustedInventory.Migrator migrate`) with the migration identity **before** new API or
   worker instances start. It applies pending scripts in order and refuses to run if an applied script was
   edited (checksum mismatch).
3. Start new API instances; they report ready only when `/health/ready` sees the required schema version.
4. Roll back by redeploying the previous images. Migrations are expand-only within a release
   (docs/migration-design.md §3), so the previous version runs against the newer schema. Never roll back a
   migration by hand.

### Migration recovery (draft)
- **Script failed:** each script runs in its own transaction, so nothing from it is applied and
  `ti.schema_migration` has no row for it. Fix forward with a corrected script under a new number if the
  failed one was already released; otherwise correct it before release.
- **Checksum mismatch:** an applied script was edited. Restore the released file from version control; put
  the change in a new migration.
- **Concurrent migrators:** an advisory lock serialises them; a second instance waits.

### Audit-chain verification failure (draft)
1. A High `AuditChainFailure` alert opens (from `POST /audit/verify` or the daily check).
2. Preserve evidence: snapshot the database and the external checkpoint files before any change.
3. Identify the first failing sequence (the verify response lists `HashMismatch` / `SequenceGap`).
4. Compare with the last signed checkpoint at or before that sequence; rows after it are suspect.
5. Treat as a security incident: rows cannot be changed by the application role, so a mismatch means
   privileged database access. Repair only through an audited operator procedure; never rewrite history.

### Stuck outbox (draft)
- **Symptoms:** rising outbox age, or `OutboxDeadLetter` alerts.
- Leases expire on their own: a crashed worker's batch is picked up by another after `lease_expires_at`.
- A dead-lettered event (10 failed attempts) stays in `ti.outbox_event` with `dead_lettered_at` and
  `last_error`. Fix the cause, then replay by clearing `dead_lettered_at` and resetting `attempts` and
  `next_attempt_at` through an audited operator procedure. Consumers deduplicate on `event_id`, so a
  replay is safe.

### Tenant suspension (draft; replaced by the v3.0 lifecycle matrix)
- A platform administrator suspends or reactivates a tenant through `/platform/tenants/{id}/suspend` and
  `/reactivate`. Both are audited in the tenant's own chain.
- While suspended, tenant users can read but every mutation returns 403.
- Spec v3.0 section 26 replaces this with reason-specific restrictions (billing versus security). This
  runbook is rewritten in S3.

### Not yet written
| Runbook | Milestone |
|---------|-----------|
| Backup restore | M6 |
| Compromised integration credential | M5 |
| Telemetry outage | M5 |
| Recall initiation | M5 |
| Ledger reconciliation failure | M2 |
| Controlled export and retention | M4 |
| Billing provider outage, webhook backlog, tenant closure and deletion | S2 / S3 |

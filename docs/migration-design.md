# Migration and persistence design

How the M0 schema draft (`db/design/schema-draft.sql`) becomes versioned migrations, and how the
runtime uses the database safely. Requirements: TEN-001..005, ARC-002, ARC-003, STK-004..008,
AUD-002..003 and section 18 (Migrations). Decisions: DEC-013, DEC-014.

## 1. Database identities

| Role | Login | Owns objects | RLS | Used by |
|------|-------|--------------|-----|---------|
| `ti_owner` | no | yes, all of schema `ti` | forced on its own tables, and it has **no policy**, so it sees no tenant rows | Object ownership only |
| `ti_migrator` | yes | via `SET ROLE ti_owner` | as `ti_owner` | Migration job only. Its credentials are never given to the API or worker (TEN-002). |
| `ti_app` | yes | nothing | `tenant_isolation` policy on every tenant table | API and worker runtime |
| `ti_platform` | yes | nothing | Policies only on `tenant`, `membership`, `role_grant`, `audit_stream`, and INSERT on `audit_event` | Tenant provisioning and suspension |
| `ti_membership_reader` | no | `ti.user_memberships()` only | SELECT policy on `tenant`, `membership`, `role_grant` | Never logs in; it exists to own the definer function |

No role has `BYPASSRLS` and no runtime role owns a table, so `FORCE ROW LEVEL SECURITY` plus
missing grants give **no universal stock bypass** (TEN-004). The superuser is reserved for
creating the cluster, roles and extensions (`btree_gist`); it is not a migration identity.

## 2. Tooling and ordering

Two kinds of migration, applied by one migration runner in a single ordered history:

1. **EF Core migrations** (`TrustedInventory.Infrastructure/Persistence/Migrations`) for tables,
   columns, indexes, enums (Npgsql enum mapping) and constraints EF can express. They are generated
   from the EF model, and **reviewed SQL scripts** (`dotnet ef migrations script --idempotent`) are
   the deployable artefact.
2. **Raw SQL migrations** (`db/migrations/NNNN_description.sql`, embedded as EF migration
   `migrationBuilder.Sql(...)` steps so both share `__EFMigrationsHistory`) for whatever EF
   cannot model: roles and grants, domains, `ENABLE`/`FORCE` RLS and policies, composite FKs that
   include non-key columns, partial unique indexes with `NULLS NOT DISTINCT`, exclusion constraints,
   triggers, `SECURITY DEFINER` functions and `ALTER … OWNER`.

Ordering rules:

- Roles and extensions come first, in a bootstrap script run once per cluster by the platform
  operator. Everything after that runs as `ti_migrator` with `SET ROLE ti_owner`.
- Every migration that creates a tenant table must, **in the same migration**, enable and force RLS,
  create the `tenant_isolation` policy and grant `ti_app`. A CI test lists every table in schema `ti`
  that has a `tenant_id` column and fails if any lacks forced RLS, the policy, or `UNIQUE (tenant_id, id)`.
- Append-only tables get their trigger and their reduced grant in the same migration that creates them.
- Migrations never contain tenant data. Seeds (`db/seed`) run through the application as `ti_app`
  with a tenant context, exactly like production writes, so seeds exercise RLS too.

## 3. Zero-downtime rules (section 18)

Deployments are rolling: version N and N+1 of the API run at the same time against one schema.

- **Expand, then contract.** Add nullable columns, new tables or new enum values first. Deploy code
  that writes both shapes. Backfill in tenant-scoped batches. Add NOT NULL or constraints with
  `NOT VALID` followed by `VALIDATE CONSTRAINT`. Drop old columns only in a later release, once no
  running version reads them.
- No destructive drops or renames inside one release. A rename is add, copy, switch reads, then drop
  later.
- Indexes on large tables use `CREATE INDEX CONCURRENTLY`, in their own non-transactional migration step.
- Enum values are only appended (`ALTER TYPE … ADD VALUE`), never removed or reordered. A removal
  needs a new type plus expand/contract.
- Migrations run before new pods start; the readiness check (ARC-004) verifies the expected migration
  ID is applied.
- Every release is rehearsed against two databases: a clean install and an upgrade from a restored copy
  of the previous release (section 18, M6 migration rehearsal).

## 4. Tenant context per transaction (DEC-013, TEN-003)

```
HTTP request
  → authenticate (OIDC token validated: issuer, audience, lifetime, keys)    IAM-002
  → read X-Tenant-Id
  → membership check: SELECT * FROM ti.user_memberships(@sub)               (no tenant context needed)
      → 403 unless an active membership exists for that tenant in an Active tenant
  → resolve permissions + warehouse scope from the role grants             IAM-001
  → open transaction (Npgsql, ti_app connection from the pool)
      SELECT set_config('app.tenant_id', @tenant, true);                    -- transaction-local
      … all reads and writes …
  → commit
```

- `set_config(..., true)` is transaction-local, so it ends at COMMIT or ROLLBACK and **cannot leak
  across pooled connections**. Session-level `SET` is forbidden. A unit test greps the codebase for
  `set_config(` with `false` and for session `SET app.`, and fails if it finds either.
- EF Core: a `DbConnectionInterceptor` cannot be used, because it fires per connection, not per
  transaction. Instead the Application-layer unit of work (`ITenantTransaction`) opens a transaction and
  runs `set_config` as its first command. Repositories accept only that unit of work, so a query
  without a context cannot be constructed. Even if one ran, RLS returns zero rows (fail-closed,
  smoke check a1–a4).
- Read-only queries also run inside a transaction with the context set.
- Background jobs (TEN-004) iterate tenants through `ti_platform` (tenant IDs only) or through work
  items that carry `tenant_id` (outbox, reservations), and open one tenant transaction per item.
- **Required pool test (TEN-003):** with `MaxPoolSize=1`, run tenant A's request, then tenant B's
  request, then a request with no context, on the same physical connection. Assert that each sees
  only its own rows and the last sees none. `db/design/rls-smoke.sql` e1–e3 proves the database half.
  The M1 integration test proves it through Npgsql and the API.
- **Membership lookup safety.** `ti.user_memberships(subject)` is `SECURITY DEFINER`, owned by
  `ti_membership_reader`, with a fixed `search_path`. It returns only rows where
  `membership.user_subject = subject AND active`. The caller passes the **validated token `sub`**.
  The function exposes membership metadata (tenant ID, name, status, roles, warehouse scope), never
  stock. The owning role has SELECT on just those three tables and cannot log in. A compromised API
  could query other subjects' memberships, but it already holds `ti_app`; it still could not read
  another tenant's stock, because RLS on stock tables does not depend on this function.

## 5. Allocation locking (STK-005, STK-006)

All inside one transaction, after the tenant context is set:

1. Lock the order line rows: `SELECT … FROM outbound_order_line WHERE order_id = @id ORDER BY line_number FOR UPDATE`.
   This serializes concurrent allocations of the same order.
2. For each line in `line_number` order, select candidate buckets with eligibility computed in SQL
   (quality `Available`; bin kind `Storage` and active; zone compatible; lot not expired at the
   warehouse-local effective dispatch date; shelf life `expiry_date - effective_date >= required_days`
   and `expiry_date > effective_date`; no open recall item, no open excursion, monitored zone not stale;
   no active count lock on the bin), then lock them **in deterministic order**:

   | Rule | ORDER BY |
   |------|----------|
   | FEFO | `lot.expiry_date ASC, bucket.received_at ASC, bucket.lot_id ASC, bucket.id ASC` |
   | FIFO | `bucket.received_at ASC, bucket.id ASC` |
   | LIFO | `bucket.received_at DESC, bucket.id ASC` |

   ```sql
   SELECT b.id, b.on_hand_qty, b.reserved_qty
   FROM ti.stock_bucket b JOIN ti.lot l ON l.tenant_id = b.tenant_id AND l.id = b.lot_id
   WHERE … eligibility …
   ORDER BY l.expiry_date, b.received_at, b.lot_id, b.id
   FOR UPDATE OF b;
   ```
3. After locking, **revalidate** quantities on the locked rows (`on_hand - reserved`). A concurrent
   transaction may have committed between snapshot and lock; in READ COMMITTED, `FOR UPDATE` returns the
   latest committed row version.
4. Insert reservations (state `Active`, `expires_at = now() + 24 h`), increase `reserved_qty`, update
   line counters, write audit and outbox rows, store the idempotency record, then commit.
5. All-or-nothing: on any shortage when `allowPartial = false`, roll back the whole transaction (409
   `INSUFFICIENT_STOCK`, no reservation; AT-OUT-01). In partial mode, commit what was reserved and
   return exact shortages.
6. On `40P01` (deadlock) or `40001` (serialization failure), retry the whole transaction at most **three**
   times with jittered backoff, then return 409 `RETRYABLE_CONFLICT`.

Cross-line ordering: because candidate buckets for different lines can overlap, lines are processed
in `line_number` order and buckets in the rule order above. Two concurrent allocations of
**different** orders can still lock overlapping buckets in different interleavings, which is what
the bounded retry handles. AT-STK-01 (20 concurrent requests for 10 units against 100) is the gate test.

Other stock commands lock in this fixed global order to avoid deadlocks: **document row → reservation rows
(by id) → stock buckets (by id) → audit_stream row**. The audit stream row is always last, because it is
the per-tenant serialization point.

## 6. Idempotency flow (STK-007, API-002)

```
POST with Idempotency-Key K, body B
 1. digest = SHA-256(canonical JSON of B)
 2. BEGIN; set tenant context
 3. SELECT … FROM idempotency_record WHERE (tenant, principal, route, key) = (…, K)
      found, same digest      → ROLLBACK; replay stored status + body (no side effects)
      found, different digest → ROLLBACK; 409 IDEMPOTENCY_MISMATCH
 4. execute command (locks, movements, audit, outbox)
 5. INSERT idempotency_record (…, digest, response_status, response_body, expires_at = now() + 7 days)
 6. COMMIT
```

- Two concurrent requests with the same key both reach step 5. The unique constraint blocks the second
  inserter until the first commits, then it raises `23505`. The handler catches that, rolls back
  (discarding its own side effects), re-reads the stored record and replays it, or returns 409 on a
  digest mismatch. No stock is posted twice.
- Only committed outcomes are stored: success, or a 4xx business rejection that is deterministic for
  that payload. Transient failures (503, retryable conflict) are not stored, so the client's retry runs again.
- After the 7-day expiry, duplicate posting is still impossible: every movement has a unique
  `operation_key` derived from the document and line (`receipt:{receiptLineKey}`, `pick:{confirmationId}`),
  and whole commands that could otherwise repeat use `document_operation`
  (`dispatch:{orderId}:{dispatchReference}`). A late retry hits `23505` and is answered from
  `document_operation.result_reference`.
- A cleanup job deletes expired idempotency rows per tenant. This is the only DELETE `ti_app` holds.

## 7. Audit hash chain (AUD-001..004)

Written in the **same transaction** as the change it records (ARC-003):

```
1. SELECT last_sequence, last_hash FROM ti.audit_stream WHERE tenant_id = @t FOR UPDATE;   -- per-tenant serialization
2. seq  = last_sequence + 1
3. body = canonical JSON of {tenantId, sequence, occurredAt (UTC, ms precision, Z), actorSubject, actorType,
                              requestId, correlationId, eventType, objectType, objectId, objectVersion,
                              reason, before, after}
4. hash = SHA-256( previous_hash_bytes || UTF-8(body) )
5. INSERT ti.audit_event (… sequence = seq, previous_hash = last_hash, event_hash = hash)
6. UPDATE ti.audit_stream SET last_sequence = seq, last_hash = hash, updated_at = now()
```

- **Canonical JSON** follows RFC 8785 (JCS): keys sorted by UTF-16 code units, no insignificant
  whitespace, minimal number form, and decimals encoded as strings exactly as stored (quantities are
  already decimal strings). Before/after snapshots are redacted **before** hashing, so secrets never
  enter the chain. A golden-file unit test pins the canonical form.
- **Serialization:** the row lock on `audit_stream` (step 1) makes sequence assignment and chaining
  strictly serial per tenant, and `UNIQUE (tenant_id, sequence)` is the backstop. A row lock is preferred
  to `pg_advisory_xact_lock` because it is visible in `pg_locks` together with the data, and it follows the
  same lock-ordering rule (always last).
- **Throughput:** one audit write per tenant at a time. This fits the section 18 budget (50 users per
  tenant). If it ever becomes a bottleneck, the documented alternative is batching events within a
  command, not parallel chains.
- **Tamper evidence:** `ti_app` has no UPDATE/DELETE on `audit_event` and triggers block even a
  superuser session (smoke i1–i3). The daily checkpoint job signs `{tenantId, date, sequence, event_hash}`
  with a key held in the secret manager and writes the result to `audit_checkpoint` **and** to
  retention-protected external storage (AUD-003).
- **Verification** (`POST /audit/verify`, and also daily): recompute the chain from sequence 1 (or from the
  last verified checkpoint), checking for no gaps, `previous_hash` continuity, recomputed `event_hash`
  equality and a checkpoint signature match. On failure: raise a High `AuditChainFailure` alert and block
  stock mutations for the tenant (OPS-001; AT-AUD-01).

## 8. Reconciliation (STK-004, OPS-001)

For each bucket: `on_hand = Σ base_qty where destination = bucket − Σ base_qty where source = bucket`,
plus a check that `reserved` equals the sum of Active and Picked reservations on the bucket. The job runs
per tenant in a `REPEATABLE READ` read-only transaction, so it sees one consistent snapshot. A mismatch
creates a High `ReconciliationMismatch` alert and marks the affected buckets blocked until an audited
operator procedure resolves them. The job never "fixes" a balance directly.

## 9. Turning the draft into real migrations (M1)

1. Create the EF Core model in `TrustedInventory.Infrastructure` that matches the draft table for table,
   with Npgsql enum mappings named as in `docs/enums.md` and decimal value converters that reject
   scale > 6.
2. Generate `0001_Initial` for the organization, catalog and audit/idempotency tables needed in M1, then
   append raw SQL for domains, RLS, policies, grants, triggers and `user_memberships`. Stock, outbound,
   exception and medical tables arrive in the migrations of the milestones that use them (M2–M5), each
   carrying its own RLS, grants and triggers (AGT-001).
3. Port `db/design/rls-smoke.sql` into the integration test suite (Testcontainers `postgres:16`). Each
   check becomes an xUnit test tagged with its requirement ID. The SQL file stays as a quick operator smoke check.
4. Add the schema-conformance test from section 2 (every tenant table has forced RLS, a policy and
   `UNIQUE (tenant_id, id)`, and no table is owned by a runtime role).
5. Diff the migrated schema against the draft with `pg_dump --schema-only`. Differences are either fixed or
   recorded in DECISIONS.md, and the draft is then retired in favour of the migrations.

## 10. Validation evidence (M0)

`db/design/schema-draft.sql` and `db/design/rls-smoke.sql` were run on a fresh `postgres:16` container:
the schema applies with no errors (58 tables, all with forced RLS, all owned by `ti_owner`), and all 35
smoke checks pass, covering
no-context fail-closed, cross-tenant read and write denial, composite-FK cross-tenant rejection,
append-only grants and triggers, duplicate operation and audit-sequence keys, quantity CHECKs including
excess-precision rejection, transaction-local context non-leakage, the membership definer function, and the
absence of owner or platform stock bypass. This is design evidence only. The M1 gate needs the same checks
through the real application stack.

## 11. v3.0 control plane (S1/S2)

Design draft `db/design/saas-schema-draft.sql`; checked by `db/design/run-saas-smoke.ps1` (bootstrap,
0001, 0002, then the draft's sections applied exactly as the migrator would, with smoke stages between
them: 59 checks pass on PostgreSQL 16). Decisions DEC-032 to DEC-040; access rules
`docs/saas-state-matrix.md`.

### 11.1 Identities and schema

| Role | Login | Purpose |
|------|-------|---------|
| `ti_control` | yes | Control plane: signup, provisioning jobs, invitation acceptance, billing adapter, webhook inbox, reconciliation, platform billing console. Full access to schema `cp`; on `ti` only tenant, membership, role_grant, audit_stream, audit_event (insert/read), usage_counter and invitation (read/update). **No catalog, location or stock privileges** (SAS-004). |
| `cp_access_reader` | no | Owns `cp.tenant_access()` only (same pattern as `ti_membership_reader`). |

Both roles and schema `cp` are created by a **bootstrap addendum** (superuser, once per cluster), because
role creation needs a superuser. `ti_platform` gets no USAGE on `cp`. Schema `cp` is owned by `ti_owner`,
every `cp` table has forced RLS, and `ti_app` sees only its own tenant's tenant-facing rows (subscription,
invoice, payment, entitlement snapshot, checkout, change, billing account without `tax_reference`).

### 11.2 Migration files

| File | Content | Why separate |
|------|---------|--------------|
| `0003_saas_enum_values` | New labels on `ti.role_name`, `ti.export_type`, `ti.alert_type` | `ALTER TYPE … ADD VALUE` must commit before any later statement uses the label |
| `0004_saas_control_plane` | Tenant **expand**, `ti` SaaS tables, schema `cp` tables, RLS, grants, `cp.tenant_access()`, `ti.user_memberships_v2()` | One atomic expand step |
| `0005_tenant_status_contract` | Lifecycle/slug/region NOT NULL, lifecycle default, drop compatibility trigger, `ti.user_memberships`, `tenant.status` and its type | Released with M2 (8 Nov 2026) once no API or worker code read `status` (DEC-043) |
| `0006_inbound_ledger` | Lots, serials, stock buckets, purchase orders and lines, receipts, receipt lines and serials, warehouse tasks, the append-only movement ledger, permanent document operation keys; forced RLS, append-only triggers, `ti_app` grants (no DELETE; no UPDATE on append-only tables); no privileges for `ti_control` or `ti_platform` | M2 schema; the movement → reservation foreign key arrives with the M3 reservation table |
| `0007_purchase_order_draft_lines` | `DELETE` on `ti.purchase_order_line` for `ti_app`, guarded by trigger `purchase_order_line_draft_only` (lines change only while the order is Draft; received quantity stays updatable) | Draft edits replace lines (DEC-047); the privilege is no wider than the rule |
| `0008_stock_bucket_block` | Append-only `ti.stock_bucket_block` (bucket, alert, expected and stored on hand) with forced RLS; `ti_app` SELECT and INSERT | Reconciliation blocks (OPS-001, DEC-049); a block holds while its alert is open |
| `0009_warehouse_task_completion` | `warehouse_task.completed_by` and `resolution`, with a CHECK tying `Completed` to both completion columns | The contract's WarehouseTask exposes them (DEC-050) |
| `0010_outbound` | Outbound orders and lines, reservations, picks, pick confirmations, shipments and lines (M0 draft); movement → reservation foreign key deferred from 0006; Draft-only trigger on order lines with `ti_app` DELETE for draft replacement; append-only pick confirmations and shipments; forced RLS and grants | M3 schema (DEC-051) |
| `0011_reserved_reconciliation` | `stock_bucket_block.expected_reserved_qty` and `stored_reserved_qty`; the mismatch CHECK now requires on hand **or** reserved to differ | Reconciliation compares reserved with live reservations (DEC-053) |
| `0012_dispatch_reference` | `shipment.dispatch_reference` becomes `identifier_text` (1–100, trimmed) | The contract allows 100 characters (DEC-055) |

### 11.3 Tenant expand/contract without breaking M1

1. **0004 (expand):** add the new columns nullable, backfill v2.0 rows (`Active → Ready`, `Suspended →
   Ready + security suspension`, generated slug, region `default`), and install
   `ti.tenant_sync_legacy_status`. The trigger keeps both shapes consistent in both directions: old code
   that writes `status` updates the security suspension, new code that writes the suspension updates
   `status`, and an old-style insert (no lifecycle) is treated as `Ready`. **No lifecycle default** is set
   yet, so an old insert can be told apart from a new one.
2. **Release the new code** (reads `lifecycle` and the suspension; uses `user_memberships_v2`). M1 code and
   new code can run side by side against 0004 (smoke checks x1–x6).
3. **0005 (contract):** set `lifecycle DEFAULT 'Provisioning'` and NOT NULL, drop the trigger, the old
   membership function, `status` and `ti.tenant_status`.

**Backfills under forced RLS.** `ti_owner` has no policy on tenant tables (TEN-004), so with FORCE RLS a
migration's `UPDATE ti.tenant` affects zero rows. A data backfill therefore wraps the statement in
`ALTER TABLE … NO FORCE ROW LEVEL SECURITY` / `FORCE ROW LEVEL SECURITY` inside the same migration
transaction. Only the migration identity can do this; the schema conformance test asserts FORCE is set on
every table after migrating. This applies to every future data migration on a tenant table.

### 11.4 Access decision per request

The unit of work, after `set_config('app.tenant_id', …)`, calls `cp.tenant_access()`. The function takes no
argument and reads `app.tenant_id`, so a tenant request can never ask about another tenant, and without
context it returns no row (fail closed). The C# access-mode calculator applies the matrix in
`docs/saas-state-matrix.md` to that row and the request time. SQL deliberately does not duplicate that
logic, so there is exactly one implementation to test. Because the row is read inside the command's own
transaction, a boundary such as `trial_end` takes effect on the first request after it passes, with no job
involved (section 26, AT-BIL-03).

### 11.5 Quota reservation (ENT-003, AT-ENT-01, AT-ENT-03)

Inside the transaction that creates the counted resource, after the access check and before the insert:

```
SELECT used FROM ti.usage_counter WHERE metric = @metric FOR UPDATE;      -- RLS scopes to the tenant
limit = (cp.tenant_access()).limits ->> @metric                           -- same transaction
IF used + amount > limit → rollback; 409 PLAN_LIMIT_REACHED {quota, used, limit, upgradeAction}
UPDATE ti.usage_counter SET used = used + amount, version = version + 1 WHERE metric = @metric;
INSERT … the resource …
```

- Retiring or deactivating the resource decrements the counter in its own transaction.
- Concurrent creations serialise on the counter row, so the last slot is taken once.
- Lock order: the counter row is locked after document rows and before stock buckets and the audit stream
  (extends §5).
- Attachment uploads insert a `usage_reservation` and increment `attachment_bytes` up front. A worker
  releases expired reservations, and a failed upload releases its own.
- A daily job reconciles counters against authoritative active-record counts and alerts on any difference
  (`EntitlementMismatch`). It never silently rewrites usage to unblock a tenant.

### 11.6 Provisioning job (ONB-002, AT-ONB-01)

- `POST /saas/signup` requires an `Idempotency-Key`.
- The job row is created with `signup_key = subject + '|' + key`, a request digest and a tenant id
  allocated up front:
  - The same key with the same body returns the existing job.
  - The same key with a different body is `IDEMPOTENCY_MISMATCH`.
- The control-plane worker leases runnable jobs (`FOR UPDATE SKIP LOCKED`) and runs the steps in order. Each
  step checks whether its record already exists before creating it, and records its outcome in
  `step_outcomes` / `completed_steps`:

| Step | Creates (idempotently) |
|------|------------------------|
| AllocateTenant | `ti.tenant` with the job's tenant id, lifecycle `Provisioning`, slug, region; audit stream |
| CreateOwnerMembership | Owner membership and `TenantOwner` + `TenantAdministrator` grants; seat counter = 1 |
| CreateBillingAccount | `cp.billing_account` |
| AttachTrial | `cp.subscription` (Trialing, plan version from the request, `trial_*` set at Ready time) and the first entitlement snapshot; one trial per tenant, repeat identities flagged for abuse review |
| InitializeConfiguration | Usage counters for every quota key |
| MarkReady | `lifecycle = Ready`, `ready_at`; trial clock starts now (ONB-003) |

- A failure stores `failure_code`, backs off, and retries the same step; it never re-runs a completed one.
- Until `MarkReady`, the tenant is `Provisioning`, so nothing usable is exposed.
- The trial starts only at Ready, so a resumed job never starts a second trial.

### 11.7 Webhook inbox and out-of-order events (BIL-004, BIL-005)

The flow is in `docs/billing-adapter.md`. Database guarantees:
- `UNIQUE (provider, event_id)` makes duplicates no-ops.
- Each apply runs in one transaction that locks the subscription row and compares the provider's revision or
  authoritative resource with `provider_revision`. Older state is ignored, so a late failure cannot regress
  a paid period.
- The same transaction bumps `revision`, writes a new current entitlement snapshot when plan or period
  change (the partial unique index guarantees one current row), and writes audit and outbox rows.
- Lifecycle precedence is checked in that transaction: no event can change a `Deleted` tenant or clear a
  security suspension.
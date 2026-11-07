# Agent and contributor guide

Read this before changing code. The specification is
`TrustedInventory_Implementation_Specification.pdf` (v2.0, 5 November 2026); requirement IDs below refer to it.

## Operating rules (spec section 01)

1. **Current milestone only (AGT-001).** Check the milestone table below. Do not build features of a
   later milestone, even partially.
2. **Requirement IDs in tests.** Every test that provides evidence for a requirement carries
   `[Trait("Requirement", "<ID>")]` (repeat the attribute for several IDs). Update
   `docs/requirement-test-matrix.csv` in the same change: `planned_tests`, then `evidence_status`
   (`Planned` → `Partial` → `Passing`).
3. **Controls are never weakened silently (AGT-003).** Tenant isolation, stock integrity and audit
   history win every conflict. Record the conflict and the resolution in `DECISIONS.md` as a new
   `DEC-nnn` entry before writing the code.
4. **Synthetic data and simulated integrations only (AGT-004).** No secrets in the repository; only
   `.env.example` with placeholders. Mark simulators visibly. Passing tests are not regulatory validation.
5. **Contract first (DEC-003).** Change `docs/openapi.yaml` before changing an endpoint. Enum values
   come from `docs/enums.md`; permission names from `docs/permissions.md`.

## Milestones (spec section 20)

| Milestone | Status | Exit gate |
|-----------|--------|-----------|
| M0 contracts | **Gate met** (6 Nov 2026); owner decisions in DECISIONS.md still open | No unresolved stock or isolation contract conflict |
| M1 foundation | **Gate met** (7 Nov 2026): 30 unit + 53 integration tests on PostgreSQL 16 | Isolation and authorization suites pass on real PostgreSQL |
| M2 inbound ledger | Not started | Receiving retries, precision, serial uniqueness, reconciliation pass |
| M3 outbound | Not started | Concurrency and stock arithmetic scenarios pass |
| M4 general operations | Not started | Stock conservation, independent approvals, end-to-end general flow pass |
| M5 medical | Not started | Medical blocking, historical traceability, audit evidence pass |
| M6 release readiness | Not started | Owner reviews evidence and approves production policies |

**Definition of done (every milestone):** builds; migrations apply; relevant tests pass; contract
matches actual responses; server authorization and error cases implemented; no mocked success in live
workflows; docs and example config updated; UI evidence for UI work; known limitations listed.

## Code conventions

- **Layers (section 04, enforced by `ArchitectureTests`):** Domain → nothing; Application → Domain;
  Infrastructure → Application + Domain; Api/Worker → Application + Infrastructure.
- **Stock arithmetic** lives in Domain/Application only. Controllers and endpoint handlers never do
  stock arithmetic or open transactions (ARC-002).
- **One transaction per command** containing stock changes, reservations, audit, idempotency record
  and outbox (ARC-003). Lock-sensitive paths use explicit SQL with the lock order in
  `docs/migration-design.md` (STK-005).
- **Quantities** are `decimal` in C#, `numeric(20,6)` in SQL, decimal strings in JSON. Never `double`
  or `float` (DEC-014).
- **Time:** inject a clock abstraction; never call `DateTime.UtcNow` / `DateTimeOffset.UtcNow`
  directly in Domain or Application code. Expiry evaluates in the warehouse's IANA timezone.
- **Tenant context** is set transaction-locally from validated membership (DEC-013). No code path may
  query tenant tables without it.
- **Packages:** versions only in `Directory.Packages.props`, exact versions, lock files committed.
  Add a package in the milestone that first needs it.
- **Warnings are errors.** Analyzer suppressions need a comment explaining why.
- **Tests:** real PostgreSQL 16 for anything touching transactions or RLS; in-memory providers are not
  evidence (section 19). Use a controllable clock for expiry, signatures and telemetry.

## Progress reports

Each report states: milestone, completed requirement IDs, commands run with results, changed decisions,
blockers. A buildable repository is not evidence that requirements pass.

## Environment notes

- The repository path contains two non-breaking spaces (U+00A0) in `Evans  Felix`, so typed paths
  don't match. Quote every path and prefer the 8.3 short path
  (`C:\Users\EVANSF~1\DOCUME~1\PERSON~1\NEWFOL~1\Projects\TRUSTE~1`) in tools and scripts.

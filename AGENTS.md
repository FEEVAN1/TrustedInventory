# Agent and contributor guide

Read this before changing code. The specification is
`TrustedInventory_Implementation_Specification_updated.pdf` (**v3.0 SaaS**, 7 November 2026); requirement IDs
below refer to it. v2.0 (`TrustedInventory_Implementation_Specification.pdf`) is superseded but kept for
traceability of DEC-001 to DEC-031. Read spec sections 22–31 and `docs/saas-state-matrix.md` first:
SaaS access, billing and lifecycle rules apply to every module.

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
   come from `docs/enums.md`; permission names from `docs/permissions.md`. Every operation declares
   `x-permission` and `x-access`, plus `x-entitlement` / `x-quota` where they apply (DEC-034, DEC-038).
6. **No live commerce (DEC-039, DEC-040, DEC-067, DEC-068).** Development and automated tests run billing only
   against the labelled simulators. Paystack (owner decision D09) runs only in test mode, with keys held outside the
   repository, for the owner's sandbox run; live keys and live checkout stay refused until a live configuration is
   approved. Never charge customers or delete production data from development or tests.

## Milestones (spec sections 20 and 31)

| Milestone | Status | Exit gate |
|-----------|--------|-----------|
| M0 contracts | **Gate met** (6 Nov 2026); owner decisions in DECISIONS.md still open | No unresolved stock or isolation contract conflict |
| M1 foundation | **Gate met** (7 Nov 2026): 30 unit + 53 integration tests on PostgreSQL 16 | Isolation and authorization suites pass on real PostgreSQL |
| M2 inbound ledger | **Gate met** (8 Nov 2026, owner sign-off): receiving retries, precision, serial uniqueness and reconciliation pass; 195 unit + 145 integration tests on PostgreSQL 16; migrations 0005-0009. Partial carry-overs: STK-001 (M4/M5 checks), STK-PRECISION (reversals), INB-003 (M5 medical), STATE-13, DATA-08 | Receiving retries, precision, serial uniqueness, reconciliation pass |
| M3 outbound | **Gate met** (9 Nov 2026, owner sign-off): AT-STK-01, AT-STK-02, AT-STK-03 and AT-OUT-01 pass; 217 unit + 169 integration tests on PostgreSQL 16; migrations 0010-0012. Partial carry-overs: STK-005 (retry exhaustion), OUT-006 and AT-OUT-02 (recall and excursion holds, M5) | Concurrency and stock arithmetic scenarios pass |
| M4 general operations | **Gate met** (10 Nov 2026, owner sign-off): stock conservation, independent approvals and the GeneralFlowTests end-to-end flow pass (AT-EXC-01..04, AT-IAM-01); 235 unit + 192 integration tests on PostgreSQL 16; migrations 0013-0016. Open owner default: export retention 30 days (D05). Carry-overs: medical return release and Traceability export (M5), TenantArchive export and orphan-object cleanup (S3) | Stock conservation, independent approvals, end-to-end general flow pass |
| M5 medical | **Code complete; gate not yet assessed** (15 Nov 2026, DEC-075 to DEC-078): quality hold, release and disposition with MED-001 checks; recalls with immediate blocking, impact, outreach and QA closure (AT-OUT-02); devices with rotating credentials, telemetry ingestion, excursions with holds, stale-zone blocking, late-reading shipment flags and tenant alerts (AT-TEL-01, AT-TEL-02); lot traceability report and export; AT-MED-01 and AT-MED-02 pass; 290 unit + 285 integration tests on PostgreSQL 16; migrations 0025-0027. Closed in M6: AT-AUD-01 (DEC-093), medical return release through the return workflow (DEC-091), lot expiry correction (DEC-090). Open for the gate: CFG-002 evidence, INB-003 medical receiving validation; medical policies (D04) need QA validation before live use | Medical blocking, historical traceability, audit evidence pass |
| M6 release readiness | **In progress** (16-21 Nov 2026, DEC-079 to DEC-101): structured logs and Prometheus metrics (NFR-MONITOR); backup restore drill with register replay, ledger reconciliation and audit verification (AT-OPS-01, development); opt-in section 18 benchmark that found and fixed two slow read paths (migration 0029); web app for receive-to-dispatch, QA inbox and alerts with PKCE sign-in and scanner input; AT-UI-01 Playwright receive-to-dispatch passes at handheld and desktop widths against the Compose stack (17 Nov 2026; the run found and fixed two screen defects and two Compose configuration gaps, DEC-084); remaining screens of the section 17 inventory and section 29 tenant surfaces, with AT-UI-02 (every menu screen loads; setup, audit and export writes) passing at both widths on 18 Nov 2026; task endpoints implemented (DEC-085); automated WCAG A and AA rule checks with axe-core on every screen (AT-UI-03), a lost-answer retry in AT-UI-01, an adjustment approved by a second person after re-authentication (AT-UI-04) and team, settings and device administration (AT-UI-05) pass at both widths on 19 Nov 2026; those runs found and fixed a root-owned object store volume (uploads and exports failed), a missing re-authentication audience (no approval could be signed in the browser), an approved adjustment that could not be posted from its screen, order buttons active before the order loaded, invitation and role revocations sent without the required reason, and an inaccessible picker (DEC-086); browser tests for the remaining write paths (AT-UI-06 transfers and counts, AT-UI-07 returns, tasks and bin moves, AT-UI-08 lot putaway, recalls and traceability, AT-UI-09 billing and ownership), which found and fixed three contract operations that were never mapped (listLots, getLot, listSerials; a new conformance test now checks every contract operation is mapped), a count reason the contract called optional, putaway without the lot scan or off-recommendation reason, and subscription cancellation without its reason (DEC-087); deployment checklist and release evidence pack. Then (DEC-088 to DEC-094): public product, plans and signup pages (AT-UI-10), platform operations and billing consoles (AT-UI-11), lot expiry correction with a QA approval bound to the date (AT-UI-12), medical return release through the return workflow, invoice PDF documents and fixed screen downloads, audit reordering and external checkpoint verification (AT-AUD-01), and complimentary access with independent approval (AT-UI-13). Then (DEC-095 to DEC-101): medical return release (AT-UI-14), workspace closure (AT-UI-15) and restrictions and plan limits (AT-UI-16) in the browser; opt-in sample data and CSV product import for onboarding (AT-UI-17); checkpoint storage and genesis fixes, release only after putaway, safety reads without entitlement, a storage profile required for medical products, ledger reversal limits; tests for configuration, CORS, audit of re-authenticated actions, state transitions and request bodies. 312 unit + 314 integration tests on PostgreSQL 16 (21 Nov 2026; the benchmark is opt-in and skipped by default). Migrations 0028-0034, bootstrap 0003. Then (DEC-106, 26 Nov 2026): account creation from the web app with email verification through a labelled mail simulator and authenticator enrolment (AT-UI-18). Outstanding: owner decision on the development identity provider amr max age (DEC-087: browser sign-ins fail intermittently until then), manual screen-reader and keyboard review, browser coverage of workspace closure and checkout (blocked by the shared tenant and D09), legal invoice details (D09), whether users need a reversal command (DEC-101), putaway following stock held in staging (DEC-096), staging performance and production recovery evidence, owner approval of production policies | Owner reviews evidence and approves production policies |
| v3.0 contract pass | **Gate met** (7 Nov 2026): contract lints clean, SaaS schema draft passes 59 checks | SaaS contracts, state matrix, control-plane design; no unresolved isolation or stock conflict |
| S1 provisioning | **Gate met** (8 Nov 2026, owner sign-off): AT-ONB-01, AT-ONB-02, AT-SAS-01 pass; 133 unit + 103 integration tests on PostgreSQL 16. Partial carry-overs: ONB-004 (sample data M2, screen with first UI), LFC-001 (S2), SOD-6 (S3), SOD-7 (S3/M4) | AT-ONB and AT-SAS pass |
| S2 commercial control | **In progress** (DEC-062 to DEC-067): slices 1-6 done on the labelled simulator: AT-BIL-01..04 and AT-ENT-01..03 pass, plus the CommercialFlowTests end-to-end lifecycle; 265 unit + 257 integration tests on PostgreSQL 16; migrations 0017-0021. Slice 7 done (DEC-068, migration 0021): NGN prices set in the platform catalog, and the Paystack adapter in test mode, verified against a labelled Paystack API simulator (PaystackTests). **Waiting for the owner's Paystack test-mode run** (docs/paystack-sandbox.md); the gate is assessed after it. No live charging: startup refuses any provider but the simulator and live checkout. D10 grace and reminders approved; still open: D09 plan names, quotas, trials and taxes. Partial carry-overs: ENT-003, ENT-004 (devices, M5), ENT-006 (recall and excursion blockers, M5); SOP-003 and AT-SOP-01 were completed in S3. The owner set the Paystack run aside on 13 Nov 2026 (DEC-069); S2 stays open | AT-BIL and AT-ENT pass in provider sandbox; no live charging |
| S3 lifecycle and service | **Code complete; gate waits for the owner to approve commercial defaults** (13-14 Nov 2026, DEC-069 to DEC-074; started while the S2 gate waits for the Paystack run, by owner instruction): AT-LFC-01, AT-LFC-02 and AT-SOP-01 pass; 283 unit + 272 integration tests on PostgreSQL 16; migrations 0022-0024 and bootstrap 0002 (ti_purger). Owner approvals needed for the gate: export window (90 days, D10), backup retention schedule (35 days, D06), rate budgets (120/600 per minute, two exports), retention over deletion (D10), plus the open D09 items. Partial carry-overs: SOP-002 (section 18 workload load test on staging, M6), SUI-001 and SUI-003 browser evidence (web app, M6), recall and excursion closure blockers (M5) | AT-LFC and AT-SOP pass; owner approves commercial defaults |

Order: v3.0 contract pass → S1 → M2 → M3 → M4, with S2 and S3 before any commercial activation. R1A
launch needs M0–M4, S1–S3 and release readiness; R1B also needs M5 and medical validation.

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

## Handoff documents (spec section 20)

Keep these current in the same change that affects them: `README.md`, `AGENTS.md`, `DECISIONS.md`,
`docs/openapi.yaml`, `docs/data-dictionary.md`, `docs/requirement-test-matrix.csv`,
`docs/operations.md`, and for SaaS `docs/saas-state-matrix.md`, `docs/entitlement-catalog.json`,
`docs/billing-adapter.md`, `docs/tenant-offboarding.md`.

## Progress reports

Each report states: milestone, completed requirement IDs, commands run with results, changed decisions,
blockers. A buildable repository is not evidence that requirements pass.

## Environment notes

- The repository path contains two non-breaking spaces (U+00A0) in `Evans  Felix`, so typed paths
  don't match. Quote every path and prefer the 8.3 short path
  (`C:\Users\EVANSF~1\DOCUME~1\PERSON~1\NEWFOL~1\Projects\TRUSTE~1`) in tools and scripts.

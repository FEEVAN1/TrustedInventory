# Release evidence pack

SUCCESS-02 (spec sections 02, 19, 20, 31; DEC-083). This pack links each launch gate to its evidence and lists every gap. It is the input
to the owner's M6 review; it is not an approval. Passing tests are engineering evidence, not regulatory validation (AGT-004).
Requirement-level status: `docs/requirement-test-matrix.csv`. Decisions: `DECISIONS.md`.

## Gates

| Gate | Status | Evidence | Still needed |
|------|--------|----------|--------------|
| M0 contracts | Met (6 Nov 2026) | Contract lint clean; DEC-001 to DEC-031 | Open owner decisions D01-D10 |
| M1 foundation | Met (7 Nov 2026) | Isolation and authorization suites on PostgreSQL 16 | AT-AUD-01 reordering and external checkpoint signature check |
| M2 inbound ledger | Met (8 Nov 2026, owner sign-off) | Receiving, precision, serials, reconciliation | — |
| M3 outbound | Met (9 Nov 2026, owner sign-off) | AT-STK-01..03, AT-OUT-01 | — |
| M4 general operations | Met (10 Nov 2026, owner sign-off) | AT-EXC-01..04, AT-IAM-01, GeneralFlowTests | — |
| S1 provisioning | Met (8 Nov 2026, owner sign-off) | AT-ONB-01/02, AT-SAS-01 | — |
| S2 commercial control | **Open** | AT-BIL-01..04, AT-ENT-01..03 on the simulator; Paystack adapter against a Paystack-format simulator | Owner's Paystack test-mode run (`docs/paystack-sandbox.md`); D09 plan names, quotas, trials, taxes |
| S3 lifecycle and service | **Code complete; owner approval open** | AT-LFC-01/02, AT-SOP-01 | Owner approval of export window, backup retention, rate budgets, retention over deletion |
| M5 medical (R1B only) | **Code complete; gate open** | AT-MED-01/02, AT-TEL-01/02, AT-OUT-02, EXC-006/007 | AT-AUD-01, CFG-002 evidence, INB-003, medical return release; QA validation of D04 policies; real devices (D07) |
| M6 release readiness | **Evidence prepared; owner review open** | Sections below | Staging and production evidence; owner approval of production policies |

R1A needs M0-M4, S1-S3 and M6. R1B also needs M5 and medical validation.

## M6 evidence

| Requirement | Evidence | Status |
|-------------|----------|--------|
| NFR-MONITOR | ObservabilityTests: JSON logs with trace and tenant ids, no token/link/reauth secret in logs; Prometheus metrics for operators with no tenant ids (DEC-079) | Passing |
| AT-OPS-01, NFR-RECOVERY | RecoveryDrillTests: real pg_dump restored, deletion register replayed, ledger reconciled, audit chain verified on the restored database, stock identical; restore to verified service 5.0 s on a workstation (DEC-080) | Development drill passing; production drill with point-in-time recovery (RPO 15 min) owed under D06 |
| NFR-PERF, SOP-002 | PerformanceTests (opt-in): 1M movements, 50 users. Found and fixed two slow read paths (migration 0029). Workstation: queries p95 1.00 s, commands p95 1.33 s, no errors, allocation over 100 lots 0.65 s (DEC-081) | Partial: staging run required |
| AT-UI-01 | Playwright receive-to-dispatch at handheld and desktop widths against the Compose stack (DEC-082, DEC-084) | Passing (17 Nov 2026) |
| UI-001, UI-002 | Same run: keyboard-wedge scans, scanned vs server-confirmed, server validation with field and trace id, scan errors, offline banner | Partial: timeout retry not driven in the browser; no automated accessibility rule checks |
| UI-SCREENS | Receive, putaway, orders/picking/dispatch, QA inbox, alerts, stock explorer, workspace switcher | Partial: catalog/locations, counts, transfers, returns, recalls, traceability, administration, audit and billing screens are API-only |
| SEC-TRANSPORT, NFR-AVAIL, SOP-004, SOP-005 | `docs/deployment-checklist.md` | Prepared; environment evidence and owner decisions (D02, D06) needed |
| SUCCESS-02 | This document | Prepared for owner review |

## Browser evidence

Run `npm run test:e2e` in `web/` against `docker compose up` (see README).

| Date | Stack | Result |
|------|-------|--------|
| 17 Nov 2026 | Compose development stack, Chromium (Playwright 1.64.1), workstation | handheld (390×844, touch) passed in 11 s; desktop (1366×900) passed in 58 s. Both include one wait for a fresh one-time-code window |
| 18 Nov 2026 | Same stack, unchanged code | handheld passed in 43 s; desktop passed in 51 s. Earlier attempts that night are not counted: one was refused at login after deliberate failed-login probes (realm brute-force protection), one ran across a laptop sleep, and one setup token was refused as not multi-factor (not reproduced; under watch) |

The first run found two screen defects and two stack configuration gaps, all fixed before this result (DEC-082, DEC-084).

## Known limitations at this release candidate

- Object storage, malware scanning, billing (simulator), devices and the outbox delivery sink are labelled simulators in development.
- Medical policies and device validation (D04, D07) are proposed defaults; QA must validate them before any medical use (R1B).
- Accessibility is designed in (labels, focus, touch targets, small-screen layout) but not yet checked with an automated rule engine.
- Performance and availability targets are measured only on a workstation; staging evidence is required.

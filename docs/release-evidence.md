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
| AT-UI-02 | Playwright: every screen in the menu loads without a failure state; unit, warehouse, zone and bin added; audit chain verified; export queued; billing and checklist load, at handheld and desktop widths (DEC-085) | Passing (18 Nov 2026) |
| AT-UI-03 | Playwright with axe-core 4.14.0: WCAG 2.0, 2.1 and 2.2 A and AA rules on the start page, the handheld menu, every menu screen with its forms opened, and four interaction states, at handheld and desktop widths (DEC-086) | Passing (19 Nov 2026) |
| AT-UI-04 | Playwright, two browsers: adjustment with evidence requested by alice, absent from her own inbox, signed by quinn after Keycloak re-authentication, posted by alice; ledger changes once (DEC-086) | Passing (19 Nov 2026) |
| AT-UI-05 | Playwright: invitation sent and revoked, scoped role granted and revoked, settings version saved, simulated device registered, credential shown once, device retired (DEC-086) | Passing (19 Nov 2026) |
| AT-UI-06 | Playwright: transfer created, approved, dispatched, part-received, shortage written off with a second signature; blind count with scanned bin, server variance, independent approval and posting; ledger agrees (DEC-087) | Passing (19 Nov 2026) |
| AT-UI-07 | Playwright: customer return into quarantine, inspection refused to the receiver and released by QA; exception task refused while its stock is in staging, stock moved from the drilldown, task completed (DEC-087) | Passing (19 Nov 2026) |
| AT-UI-08 | Playwright: lot putaway with lot scan; recall blocking stock, outreach, disposal and closure each with a second QA signature; lot traced; owner export completes, QA export refused (DEC-087) | Passing (19 Nov 2026) |
| AT-UI-09 | Playwright: billing refusals (cancellation reason, unlinked subscription), no checkout without an approved price (D09), simulator portal link; ownership offered after re-authentication and withdrawn (DEC-087) | Passing (19 Nov 2026) |
| API-16 | ContractConformanceTests now also checks that every contract operation is mapped (four named exceptions); lots and serials endpoints added with an integration test (DEC-087) | Partial: request bodies are not checked against the contract schemas |
| UI-001 | AT-UI-01: keyboard-wedge scans, scanned vs server-confirmed, and a receipt answer held past the 15 s limit retried with the same Idempotency-Key, received once (DEC-086) | Passing |
| UI-002 | AT-UI-01 to AT-UI-03: server validation with field and trace id, scan errors, offline banner, timeout state, automated WCAG rule checks | Partial: manual screen-reader and keyboard review not yet done |
| UI-SCREENS | Every section 17 screen and the section 29 tenant surfaces are built (DEC-082, DEC-085); browser writes for every screen except those below (DEC-086, DEC-087) | Partial: workspace closure (shared synthetic tenant; API evidence AT-LFC-01/02) and checkout or plan change (no display-approved price, D09) are not driven in the browser; platform consoles and the public signup page are not built |
| SEC-TRANSPORT, NFR-AVAIL, SOP-004, SOP-005 | `docs/deployment-checklist.md` | Prepared; environment evidence and owner decisions (D02, D06) needed |
| SUCCESS-02 | This document | Prepared for owner review |

## Browser evidence

Run `npm run test:e2e` in `web/` against `docker compose up` (see README).

| Date | Stack | Result |
|------|-------|--------|
| 17 Nov 2026 | Compose development stack, Chromium (Playwright 1.64.1), workstation | handheld (390×844, touch) passed in 11 s; desktop (1366×900) passed in 58 s. Both include one wait for a fresh one-time-code window |
| 18 Nov 2026 | Same stack, unchanged code | handheld passed in 43 s; desktop passed in 51 s. Earlier attempts that night are not counted: one was refused at login after deliberate failed-login probes (realm brute-force protection), one ran across a laptop sleep, and one setup token was refused as not multi-factor (not reproduced; under watch) |

| 18 Nov 2026 (later) | Same stack with the remaining screens (DEC-085) | AT-UI-01 and AT-UI-02 passed at both widths in one run of 3.5 min. Earlier runs that day found and fixed three defects: the task endpoints were in the contract but not mapped, a queued export blanked the app, and refused requests were logged as 200. One browser sign-in was refused as not multi-factor before the fix runs (not reproduced in the next six sign-ins; under watch) |

| 19 Nov 2026 | Same stack with DEC-086 | AT-UI-01 to AT-UI-05 passed at handheld and desktop widths, run file by file after the fixes (AT-UI-03 rerun after the last screen change). A full-suite run was stopped for low memory on the workstation and is not counted |
| 19 Nov 2026 (later) | Fresh stack (`down -v`) with DEC-087, run file by file | AT-UI-01 to AT-UI-09 passed at handheld and desktop widths. First attempts that failed and were rerun: AT-UI-07 (a role left by AT-UI-08; each test now sets and restores its roles), AT-UI-03 (relied on leftover orders; it now creates its own), AT-UI-08 desktop twice (once the empty-`amr` sign-in refusal, confirmed from the trace; once an approval that stayed in the inbox, not diagnosed). AT-UI-08 handheld passed before its role clean-up was added; the desktop pass includes it |

The first run found two screen defects and two stack configuration gaps, all fixed before this result (DEC-082, DEC-084).

## Known limitations at this release candidate

- Object storage, malware scanning, billing (simulator), devices and the outbox delivery sink are labelled simulators in development.
- Medical policies and device validation (D04, D07) are proposed defaults; QA must validate them before any medical use (R1B).
- Accessibility passes the automated WCAG A and AA rules (axe-core); a manual screen-reader and keyboard review has not been done.
- Performance and availability targets are measured only on a workstation; staging evidence is required.
- Browser sign-in to the development Keycloak fails intermittently: the realm names sign-in methods (`amr`) only in tokens issued in the same second as the login step, and the API correctly refuses tokens without them. A fix is proposed and waits for the owner (DEC-087). Production uses the owner's identity provider (D02).
- Not implemented: lot expiry correction (needs approvals for lot corrections) and invoice documents (no PDF is produced yet); the billing screen says so.

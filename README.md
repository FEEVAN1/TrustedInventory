# TrustedInventory

Multi-tenant warehouse and inventory management for general goods and pharmaceutical operations.
Built to `TrustedInventory_Implementation_Specification_updated.pdf` (v3.0 SaaS, 7 November 2026); v2.0 is
kept for traceability of DEC-001 to DEC-031.

**Status: M3 (outbound) gate met; M4 (general operations) gate met.** Sign-in with OIDC and MFA, tenants and memberships,
roles and permissions, row-level tenant isolation, warehouses/zones/bins, the product catalog, configuration
versions, the hash-chained audit trail, idempotency and the outbox worker (M1); self-service signup with a
resumable provisioning job, invitations and ownership transfer, the access-mode pipeline, entitlement and quota
gates, abuse limits and the setup checklist API (S1); and purchase orders, GS1 receiving into quarantine or
staging, the append-only stock ledger with eligibility and scheduled reconciliation, and scan-confirmed
putaway (M2); and outbound orders, FEFO/FIFO/LIFO allocation into reservations, scan-confirmed picking with
unpick, dispatch with in-transaction rechecks and rework tasks, and the reservation-expiry worker (M3); and
independent approvals with re-authentication, adjustments, bin and warehouse transfers, blind counts with bin
locks, customer returns, evidence attachments and signed asynchronous exports (M4) are implemented. Object
storage and malware scanning are labelled simulators. Recall, excursion and telemetry holds arrived with M5.
**M5 (medical) code complete; gate not yet assessed** (DEC-075 to DEC-078): QA quality hold, release and disposition with MED-001 eligibility checks, recalls that block stock at once and close only with QA approval, telemetry devices with rotating credentials, excursions with automatic holds, stale-telemetry blocking, late-reading shipment flags, tenant alerts, and lot traceability. Devices are simulated (D07); medical policies (D04) need QA validation.
**S3 (lifecycle and service) code complete; its gate waits for the owner to approve the commercial defaults** (DEC-069 to
DEC-074): workspace closure with re-authentication and safety blockers, retention holds and withdrawal, the owner-only tenant
archive export, approved deletion through a database-checked purge role with retained audit evidence, the deletion register and
its replay after a backup restore, tenant-aware rate limits, export concurrency and fair export scheduling, persisted platform
alerts and health signals, the public service status, and per-recipient billing notices.
**S2 (commercial control) stays open:** complete on the labelled simulator, and the Paystack adapter (owner decision D09: Paystack, NGN, prices set in the platform catalog) works in test mode against a labelled Paystack API simulator. The owner's Paystack test-mode run (`docs/paystack-sandbox.md`) is set aside for now (DEC-069). Against the labelled provider simulator: the platform plan catalog; subscription and invoice reads; consented hosted checkout and the billing portal; signed webhooks into an encrypted inbox and the worker that applies them (activation, renewal, past-due grace, disputes and refunds, with security suspension and deletion taking precedence); plan changes at the next billing cycle with downgrade blockers for usage and medical stock; cancellation at period end and resume; daily reconciliation, retained access during a provider outage, payment reminders and the platform billing consoles. Nothing can charge: startup refuses live Paystack keys and live checkout. Passing builds and tests are not evidence that product requirements are met; see
`docs/requirement-test-matrix.csv`.

## Layout

| Path | Contents |
|------|----------|
| `src/TrustedInventory.Domain` | Entities and invariants (no infrastructure dependency) |
| `src/TrustedInventory.Application` | Application services (one per module, called directly by endpoints; DEC-015), validators, access checks |
| `src/TrustedInventory.Infrastructure` | EF Core mapping, tenant unit of work (RLS context), audit chain, idempotency, jobs |
| `src/TrustedInventory.Api` | HTTP API (`/api/v1`), authentication, tenant context, health checks |
| `src/TrustedInventory.Worker` | Outbox delivery, idempotency cleanup, daily audit checkpoints |
| `src/TrustedInventory.Migrator` | Applies `db/migrations` (`migrate`), dev cluster setup (`bootstrap`), synthetic seed (`seed`), deletion register export and replay after a restore (`export-deletion-register`, `replay-deletions`) |
| `db/bootstrap`, `db/migrations` | Cluster bootstrap (superuser, once) and versioned SQL migrations (DEC-025) |
| `db/design/` | M0 draft schema and RLS smoke test (design artefacts, superseded by migrations) |
| `deploy/` | Dockerfiles, Keycloak development realm, stack verification script |
| `web/` | React + TypeScript PWA (Vite); placeholder page until UI milestones |
| `tests/` | Unit tests and integration tests on real PostgreSQL 16 (Testcontainers) |
| `docs/openapi.yaml` | API contract (OpenAPI 3.1, contract-first) |
| `docs/` | Data dictionary, ER model, migration design, enums, permissions, test matrix, operations |
| `DECISIONS.md` | Engineering decisions and open owner decisions |
| `AGENTS.md` | Conventions, milestone gates, rules for contributors and coding agents |

## Prerequisites

- .NET SDK 10.0.201 or a later 10.0.2xx patch (pinned in `global.json`)
- Node.js 24 with npm
- Docker (integration tests start a PostgreSQL 16 container; the dev stack runs in Compose)

## Build and test

All commands run from the repository root. Each was run successfully during M1.

```powershell
dotnet build TrustedInventory.slnx          # warnings are errors
dotnet test TrustedInventory.slnx           # unit + integration; Docker must be running

npx --yes @redocly/cli@2.0.8 lint docs/openapi.yaml

cd web; npm ci; npm run build; cd ..
```

Build into the repository's own `bin`/`obj` folders. On this machine, `--artifacts-path` pointing under the
user profile fails (the profile path contains non-breaking spaces and becomes too long), and tests that
locate the repository root fail when built outside it.

## Development stack (Docker Compose)

Starts PostgreSQL 16, cluster bootstrap, migrator, a synthetic seed, Keycloak (realm with OTP-required
synthetic users), the API, the worker and the web app.

```powershell
# 1. Create .env from the template and replace every placeholder with a random value
#    (keys: [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
Copy-Item .env.example .env

# 2. Start everything
docker compose up -d --build

# 3. Verify: Keycloak login with password + TOTP, token claims, /me, tenant isolation
powershell -NoProfile -ExecutionPolicy Bypass -File deploy/verify-stack.ps1 -EnvFile .env -User alice

# 4. Stop and remove data
docker compose down -v
```

| Service | URL |
|---------|-----|
| API | http://localhost:8081 (`/health/ready`, `/api/v1/...`) |
| Keycloak | http://localhost:8080 (realm `trustedinventory`) |
| Web | http://localhost:5173 |
| PostgreSQL | localhost:55432 |

Synthetic users: `platform-admin`, `alice`, `quinn`, `bob`, sharing the password and TOTP seed from `.env`.
For a browser login from the host, map `127.0.0.1 keycloak` in the hosts file (DEC-031). All data is
synthetic; the outbox sink and checkpoint retention store are simulations.

## Database checks

```powershell
# Apply the M0 draft schema to a throwaway PostgreSQL 16 container and run the RLS smoke checks
powershell -NoProfile -ExecutionPolicy Bypass -File db/design/run-smoke.ps1
```

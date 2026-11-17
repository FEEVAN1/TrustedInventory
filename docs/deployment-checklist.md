# Production deployment checklist

Release readiness (M6) for SEC-TRANSPORT, NFR-AVAIL, SOP-004 and SOP-005 (spec sections 06, 18, 30; DEC-083). Every item needs evidence
from the target environment before launch; this repository can only prepare it. Region, provider, failover and recovery budget are
owner decision D06; nothing here is approved yet.

Status key: **Ready** (code or document in this repository), **Needs environment evidence**, **Owner decision**.

## Transport, storage and secrets (SEC-TRANSPORT)

| Item | Status | How |
|------|--------|-----|
| TLS 1.3 (1.2 minimum) at the load balancer; HTTP redirects to HTTPS | Needs environment evidence | The API enables HSTS and HTTPS redirection outside Development (`Program.cs`); terminate TLS at the load balancer and record the TLS scan result |
| CORS allow-list is the production web origin only | Needs environment evidence | `Cors:AllowedOrigins`; the API never allows `*` |
| Encryption at rest: database, backups, object storage | Owner decision (D06) | Managed PostgreSQL with storage encryption; encrypted backups; private object storage with server-side encryption |
| Secrets come from a secret manager, never from files in images | Needs environment evidence | Required at startup (fail closed): `ConnectionStrings:App`, `:Platform`, `:Control`; `Paging:CursorKey`; `Storage:LinkKey`; `Audit:CheckpointKey`; `Billing:InboxKey`; `Billing:Simulator:SigningSecret` or `Billing:Paystack:SecretKey`; `Invitations:TokenKey`. Rotate each on a schedule and after any exposure |
| Live billing keys and live checkout | Owner decision (D09) | Startup refuses `sk_live_` keys and `Billing:LiveCheckoutEnabled` until a live configuration is approved (DEC-040, DEC-068) |
| Identity provider: MFA required, production issuer | Owner decision (D02) | `Auth:Authority`, `Auth:Audience`, `Auth:ReauthAudiences`; Keycloak in this repository is development only |

## Availability (NFR-AVAIL)

| Item | Status | How |
|------|--------|-----|
| At least two API instances behind a load balancer | Needs environment evidence | The API is stateless: tenant context, idempotency and rate limits per instance; readiness `/health/ready` checks the schema version |
| Worker redundancy | Ready | Jobs lease work (`FOR UPDATE SKIP LOCKED`, lease columns) and handlers are idempotent, so two workers can run |
| Managed database with automatic failover | Owner decision (D06) | One primary region in R1 (SOP-004); no residency or cross-region promise |
| 99.9 % monthly availability target | Owner decision | A target, not a published SLA, until approved (section 18) |
| Monitoring wired to the metrics route | Needs environment evidence | Scrape `GET /platform/v1/metrics` with a platform operations identity; alert on `ti_outbox_max_age_seconds`, `ti_ledger_mismatch_alerts`, `ti_stale_devices`, platform alerts (DEC-074, DEC-079) |

## Environments and data separation (SOP-004)

| Item | Status | How |
|------|--------|-----|
| Separate staging and production: secrets, databases, object storage, identity realms | Needs environment evidence | No configuration value is shared; `.env.example` shows placeholders only |
| Private object storage, tenant-prefixed keys | Ready (code) | Keys start with the tenant id (database CHECK); the simulated store must be replaced with the production store (D06) |
| Backups: encrypted, point-in-time recovery, retention per approved schedule | Owner decision (D06) | Drill procedure and timings: DEC-080, `docs/operations.md` |
| Cluster roles bootstrapped once by an operator | Ready | `db/bootstrap/0000`-`0003` (owner, runtime roles, `ti_purger`, `ti_metrics_reader`); runtime roles have no CREATE anywhere |

## Rollout (SOP-005)

| Item | Status | How |
|------|--------|-----|
| Expand / migrate / contract | Ready | Migrations 0017-0029 are expand-only; earlier contract steps (0005, 0012, 0014-0016) ran only after the code stopped needing the old shape (DEC-042, DEC-043). A release may be rolled back to the previous build without reverting the schema |
| Canary | Needs environment evidence | Deploy one API instance with the new build, watch `ti_transaction_retries_total`, `ti_allocation_failures_total`, 5xx rate and billing alerts for 30 minutes, then roll out |
| Rollback drill | Needs environment evidence | Roll the canary back to the previous image against the new schema and run the smoke checks (`deploy/verify-stack.ps1`) |
| Feature flags never bypass entitlements or medical controls | Ready | There are no feature flags that skip access, entitlement or medical checks; platform plans only add features |

## Before go-live (owner)

Terms, privacy notice, subscription terms, billing and tax set-up, data processing terms, retention schedules, incident contacts and the
regional deployment policy (spec section 30, launch checklist). These are launch dependencies, not engineering outputs.

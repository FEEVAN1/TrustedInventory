# Permission catalogue

Authorization is deny-by-default and permission-based (IAM-001, DEC-007). Each endpoint in
`openapi.yaml` declares its permission with `x-permission`. Permissions marked **W** are
warehouse-scoped: a role grant with a `warehouse_id` limits them to that warehouse; a grant without
one applies to all warehouses of the tenant.

Separation-of-duty rules apply regardless of how many roles a user holds (section 05).

## Permissions

| Permission | Scope | Used by |
|------------|-------|---------|
| `platform.tenants.manage` | platform | Provision, suspend, reactivate tenants |
| `platform.health.read` | platform | Service health |
| `tenant.members.manage` | tenant | Memberships and role grants |
| `tenant.config.manage` | tenant | Configuration versions, rotation rule, storage profiles |
| `catalog.manage` | tenant | Products, units, conversions, barcodes, partners |
| `catalog.read` | tenant | Read master data |
| `warehouses.manage` | W | Warehouses, zones, bins |
| `stock.read` | W | Stock, movements, traceability |
| `purchase-orders.create` | W | Create/edit draft purchase orders |
| `purchase-orders.approve` | W | Approve, short-close |
| `receipts.post` | W | `POST /inventory/receive` |
| `putaway.confirm` | W | Putaway tasks |
| `orders.create` | W | Create/edit/confirm/cancel outbound orders |
| `orders.allocate` | W | Allocate, deallocate |
| `picks.confirm` | W | View assigned picks, confirm, unpick |
| `dispatch.post` | W | Dispatch |
| `transfers.manage` | W | Create, approve, dispatch, receive transfers |
| `counts.perform` | W | Create, start, submit counts (blind) |
| `counts.manage` | W | Cancel counts, view expected quantities |
| `adjustments.request` | W | Request adjustments |
| `adjustments.post` | W | Post an approved adjustment |
| `returns.manage` | W | Record returns |
| `returns.inspect` | W | Inspect and disposition returns (medical release also needs `quality.release`) |
| `approvals.request` | tenant | Create approval requests |
| `approvals.sign.inventory` | tenant | Sign adjustments, count variances, transit dispositions |
| `approvals.sign.quality` | tenant | Sign quality release, hold release, return release, excursion and recall dispositions, expiry corrections; also required for medical adjustments |
| `quality.release` | W | Execute release with a signed approval |
| `quality.hold` | W | Place holds; record dispositions |
| `recalls.manage` | tenant | Open recalls, record outreach |
| `recalls.close` | tenant | Close recall with approval |
| `tasks.manage` | W | Complete rework/exception tasks |
| `alerts.read` | W | Alerts and excursions |
| `alerts.acknowledge` | W | Acknowledge alerts and excursions |
| `devices.manage` | tenant | Register, retire, rotate device credentials |
| `telemetry.ingest` | device | `POST /telemetry/readings` (service principal only) |
| `audit.read` | tenant | Audit events, verification |
| `exports.create` | tenant | Create and download exports |

## Role bundles (default; owner to confirm under D03)

| Role | Permissions |
|------|-------------|
| PlatformAdministrator | `platform.*` only. No tenant stock access. |
| TenantAdministrator | `tenant.members.manage`, `tenant.config.manage`, `catalog.manage`, `catalog.read`, `warehouses.manage`, `devices.manage`, `stock.read` |
| WarehouseManager | `catalog.read`, `stock.read`, `purchase-orders.create`, `purchase-orders.approve`, `orders.create`, `orders.allocate`, `transfers.manage`, `counts.perform`, `counts.manage`, `adjustments.request`, `adjustments.post`, `returns.manage`, `approvals.request`, `tasks.manage`, `alerts.read`, `alerts.acknowledge`, `quality.hold` |
| Receiver | `catalog.read`, `stock.read`, `receipts.post`, `putaway.confirm`, `returns.manage`, `counts.perform` |
| Picker | `catalog.read`, `stock.read`, `picks.confirm` |
| Dispatcher | `catalog.read`, `stock.read`, `dispatch.post` |
| QaOfficer | `catalog.read`, `stock.read`, `approvals.request`, `approvals.sign.quality`, `quality.release`, `quality.hold`, `returns.inspect`, `recalls.manage`, `recalls.close`, `alerts.read`, `alerts.acknowledge` |
| InventoryApprover | `catalog.read`, `stock.read`, `approvals.sign.inventory` |
| Auditor | `catalog.read`, `stock.read`, `audit.read`, `exports.create`, `alerts.read` |
| TelemetryIntegration | `telemetry.ingest` (service principal bound to its devices) |

## Separation-of-duty rules (server-enforced)

- **SOD-1** The signer of an approval must differ from the approval requester and from the user who
  created or last modified the subject (IAM-003, EXC-004). Violation: 409 `APPROVAL_SELF`.
- **SOD-2** Medical adjustments need `approvals.sign.inventory` **and** `approvals.sign.quality` on the
  signer (section 05).
- **SOD-3** A tenant administrator cannot grant themselves `approvals.sign.quality`. A role grant
  that adds QA permissions must be made by a different administrator and is audited.
- **SOD-4** Signing requires an active membership at signing time and at posting time (IAM-004).

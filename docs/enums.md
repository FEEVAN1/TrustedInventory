# Canonical enumerations

Single source for enum names and values used by the database (PostgreSQL enum types or checked text),
C# domain types and `openapi.yaml`. Values are case-sensitive strings on the wire (section 15).
Changing a value requires a DECISIONS.md entry and a migration.

| Enum | Values | Notes |
|------|--------|-------|
| TenantLifecycle | `Provisioning`, `Ready`, `Archived`, `DeletionPending`, `Deleted` | v3.0 section 26; replaces v2.0 TenantStatus (DEC-033) |
| HandlingProfile | `General`, `Medical` | Medical requires lot tracking and expiry |
| TrackingMode | `Quantity`, `Lot`, `Serial`, `LotAndSerial` | |
| RotationRule | `FIFO`, `LIFO`, `FEFO` | Medical products: FEFO only |
| PartnerType | `Supplier`, `Customer`, `Both` | |
| BinKind | `Storage`, `Staging`, `Quarantine`, `Transit` | Transit is system-managed (DEC-010) |
| QualityStatus | `Quarantine`, `Available`, `Hold`, `Recalled`, `Damaged` | DEC-004; "Expired" is derived, never stored |
| PurchaseOrderStatus | `Draft`, `Approved`, `PartReceived`, `Received`, `ShortClosed`, `Cancelled` | INB-001 |
| ReceiptStatus | `Draft`, `Posted`, `Closed`, `Cancelled` | R1 reaches only Posted, Closed (DEC-005) |
| OutboundOrderStatus | `Draft`, `Confirmed`, `PartAllocated`, `Allocated`, `PartPicked`, `Picked`, `PartDispatched`, `Dispatched`, `Closed`, `Cancelled` | Aggregate derived from lines |
| ReservationState | `Active`, `Picked`, `Consumed`, `Released`, `Expired` | |
| ShipmentStatus | `Dispatched` | Shipments are immutable records |
| TaskType | `Putaway`, `Rework`, `Exception` | DEC-011 |
| TaskStatus | `Open`, `InProgress`, `Completed`, `Cancelled` | |
| TransferStatus | `Draft`, `Approved`, `InTransit`, `PartReceived`, `Received`, `Cancelled` | EXC-001 |
| CountStatus | `Draft`, `Counting`, `Submitted`, `Approved`, `Posted`, `Closed`, `Cancelled` | Reject creates a new revision in `Counting` |
| AdjustmentStatus | `Requested`, `Approved`, `Posted`, `Rejected`, `Cancelled` | |
| ReturnStatus | `Received`, `Inspected`, `Closed` | Returned stock enters Quarantine |
| ReturnDisposition | `Release`, `Damaged`, `SupplierReturn`, `Disposal` | |
| RecallStatus | `Open`, `Closed` | |
| ApprovalStatus | `Pending`, `Approved`, `Rejected`, `Expired`, `Invalidated`, `Consumed` | |
| ApprovalSubjectType | `Adjustment`, `CountVariance`, `QualityRelease`, `HoldRelease`, `ReturnRelease`, `RecallClosure`, `TransitDisposition`, `ExpiryCorrection`, `ExcursionDisposition` | |
| SignatureMeaning | `Approval`, `Rejection`, `QualityRelease`, `Disposition` | |
| ExcursionStatus | `Open`, `Acknowledged`, `Resolved` | |
| ExcursionDisposition | `Release`, `KeepOnHold`, `Damaged` | QA decision on resolution |
| AlertType | `Excursion`, `StaleDevice`, `ExpiryApproaching`, `ReconciliationMismatch`, `AuditChainFailure`, `OutboxDeadLetter`, `PutawayException`, `ProvisioningFailed`, `WebhookBacklog`, `EntitlementMismatch`, `BillingReconciliation` | SaaS alerts are platform-level (SOP-003) |
| AlertSeverity | `Info`, `Warning`, `High` | |
| AlertStatus | `Open`, `Acknowledged`, `Closed` | |
| ExportType | `Audit`, `Movements`, `Traceability`, `StockSnapshot`, `TenantArchive` | TenantArchive: LFC-003 |
| ExportStatus | `Queued`, `Running`, `Completed`, `Failed`, `Expired` | |
| AttachmentScanStatus | `Pending`, `Clean`, `Rejected` | |
| ApprovalAction | `Post`, `Release`, `Close`, `Correct`, `Dispose` | What the consuming command will do |
| InspectionResult | `Passed`, `Failed` | Receive item (DEC-021) |
| IneligibilityReason | `Quarantine`, `Hold`, `Recalled`, `Damaged`, `Expired`, `StorageIncompatible`, `StaleTelemetry`, `OpenExcursion`, `CountLocked`, `BinInactive`, `TransitLocation`, `AwaitingPutaway`, `Blocked` | Why a bucket's allocatable quantity is zero (STK-001) |
| DocumentType | `Receipt`, `PutawayTask`, `BinTransfer`, `OutboundOrder`, `Shipment`, `Transfer`, `Count`, `Adjustment`, `Return`, `Recall`, `QualityDecision`, `Excursion`, `ExpiryCorrection` | Movement document reference |
| TransitDispositionType | `Lost`, `Damaged` | STK-003 |
| QualityDispositionType | `Damaged`, `SupplierReturn`, `Disposal` | |
| RecallOutreachStatus | `Pending`, `Contacted`, `Acknowledged`, `Completed` | Manual outreach record (EXC-006) |
| ReadingProcessingStatus | `Stored`, `ExcursionOpened`, `LateReviewQueued` | |
| AuditVerificationFailure | `HashMismatch`, `SequenceGap`, `CheckpointMismatch`, `MissingCheckpoint` | |
| ActorType | `User`, `Service`, `Device`, `System` | |
| Role | `PlatformAdministrator`, `TenantAdministrator`, `WarehouseManager`, `Receiver`, `Picker`, `Dispatcher`, `QaOfficer`, `InventoryApprover`, `Auditor`, `TelemetryIntegration`, `TenantOwner`, `BillingAdministrator`, `PlatformBilling` | Bundles in `permissions.md`; TenantOwner only via signup or transfer (DEC-036) |

## SaaS control plane (v3.0 sections 23–28)

| Enum | Values | Notes |
|------|--------|-------|
| AccessMode | `Normal`, `Provisioning`, `BillingRestricted`, `SecuritySuspended`, `Closed`, `Deleted` | Computed per request (DEC-033, saas-state-matrix.md) |
| RestrictionReason | `Provisioning`, `TrialEnded`, `PaymentOverdue`, `SubscriptionEnded`, `SecuritySuspended`, `Archived`, `DeletionPending`, `Deleted` | Every active reason, highest precedence first |
| AccessCategory | `identity`, `setup-status`, `read`, `export`, `routine-write`, `safety`, `team-admin`, `protective-admin`, `billing`, `ownership`, `telemetry-ingest` | `x-access` on each operation (DEC-034) |
| SubscriptionState | `Trialing`, `Active`, `PastDue`, `Cancelled` | Initial unpaid checkout lives in CheckoutSession |
| SubscriptionChangeState | `Pending`, `Scheduled`, `Blocked`, `Applied`, `Cancelled` | ENT-005, ENT-006 |
| BillingInterval | `Month`, `Year` | |
| CheckoutStatus | `Open`, `Completed`, `Expired` | A return URL never completes it (BIL-002) |
| SetupItemKey | `business-profile`, `warehouse-timezone`, `zones-bins`, `products-units`, `team-invitations`, `import` | Setup checklist, display order (ONB-004, DEC-046) |
| SetupItemStatus | `Done`, `Pending`, `Unavailable` | Computed per read; `Unavailable` until the capability exists |
| InvoiceStatus | `Draft`, `Open`, `Paid`, `Void`, `Uncollectible` | BIL-006 |
| PaymentStatus | `Pending`, `Succeeded`, `Failed`, `Refunded` | BIL-006 |
| ProvisioningStatus | `Pending`, `Provisioning`, `Ready`, `Failed` | ONB-007 |
| ProvisioningStep | `AllocateTenant`, `CreateOwnerMembership`, `CreateBillingAccount`, `AttachTrial`, `InitializeConfiguration`, `MarkReady` | ONB-002, in this order |
| InvitationStatus | `Pending`, `Accepted`, `Revoked`, `Expired` | ONB-005 |
| WebhookProcessingState | `Received`, `Processed`, `Ignored`, `Quarantined`, `Failed` | Quarantined: unknown billing reference (AT-BIL-02) |
| ClosureStatus | `Requested`, `ExportWindow`, `Blocked`, `DeletionPending`, `Deleted`, `Withdrawn` | LFC-002 |
| ClosureBlocker code | `MEDICAL_STOCK_ON_HAND`, `MEDICAL_IN_TRANSIT`, `ACTIVE_HOLDS` (recall and excursion codes join with M5) | 409 `RETENTION_BLOCKED` blockers (DEC-070) |
| PlatformAlertKind | `WebhookBacklog`, `EntitlementMismatch`, `ProvisioningFailures`, `ReconciliationBacklog`, `UnpaidInvoices` | SOP-003, DEC-074 |
| PlatformAlertStatus | `Open`, `Acknowledged`, `Closed` | Closed only by the monitor when the condition clears |
| ServiceStatus | `Operational`, `Degraded`, `Unavailable` | `GET /service-status` (SOP-006) |
| Problem.ineligibility code | `NotReleasable`, `MissingIdentifiers`, `Expired`, `InspectionNotPassed`, `StorageIncompatible`, `Recalled`, `OpenExcursion` | 422 `RELEASE_INELIGIBLE` (MED-001, DEC-075) |
| ApprovalSubjectType (added) | `QualityDisposition` | Disposition of one bucket (DEC-075, migration 0025) |
| ReadingProcessingStatus | `Stored`, `ExcursionOpened`, `LateReviewQueued` | TEL-001 to TEL-004 |
| ClosureBlocker / downgrade blocker (added) | `OPEN_RECALLS`, `MEDICAL_OPEN_RECALLS` | DEC-076 |
| FeatureKey | `core_inventory`, `service_api`, `advanced_reporting`, `medical_storage`, `telemetry` | Wire values as written (ENT-002) |
| QuotaKey | `active_seats`, `active_warehouses`, `active_skus`, `active_devices`, `attachment_bytes` | ENT-002, ENT-003 |
| OwnershipTransferStatus | `Pending`, `Accepted`, `Cancelled`, `Expired` | ONB-006 |
| PlanVersionStatus | `Draft`, `Published`, `Retired` | ENT-001; immutable once referenced |
| ReconciliationTaskKind | `SubscriptionMismatch`, `UnpaidInvoice`, `EntitlementMismatch`, `UnknownBillingReference`, `Dispute`, `Refund`, `Chargeback` | BIL-006, BIL-008 |
| ReconciliationTaskStatus | `Open`, `Resolved` | |
| SampleDataStatus | `Active`, `Removed` | DEC-098 |
| SampleDataObjectType | `Warehouse`, `Zone`, `Bin`, `Unit`, `Product`, `Partner` | DEC-098 |
| ComplimentaryAccessStatus | `Pending`, `Active`, `Expired`, `Revoked` | Computed at read time from approval, revocation and expiry (DEC-094) |

## Stock movement types

| MovementType | Source → destination | Requirement |
|--------------|----------------------|-------------|
| `Receipt` | external → staging bucket | INB-004 |
| `Putaway` | bucket → bucket (same quality) | INB-006 |
| `BinTransfer` | bucket → bucket, same warehouse | EXC-002 |
| `Pick` | storage bucket → staging bucket, carries reservation | STK-002, OUT-004 |
| `Unpick` | staging bucket → storage bucket, reservation back to Active | OUT-006 |
| `Dispatch` | staging bucket → external (shipment) | OUT-005 |
| `TransferDispatch` | source bucket → source Transit bucket | EXC-001 |
| `TransferReceipt` | Transit bucket → destination staging bucket | EXC-001 |
| `TransitDisposition` | Transit bucket → external (lost/damaged, approved) | STK-003 |
| `QualityChange` | bucket → bucket with different quality status | MED-002 |
| `AdjustmentIncrease` | external → bucket (approved) | EXC-004 |
| `AdjustmentDecrease` | bucket → external (approved) | EXC-004 |
| `ReturnReceipt` | external → Quarantine bucket | EXC-005 |
| `Disposal` | bucket → external (reasoned) | EXC-007 |
| `SupplierReturn` | bucket → external (reasoned) | EXC-007 |
| `Reversal` | inverse of `reversal_of_id`, ≤ unreversed quantity | section 09 |

Every movement has `base_qty > 0`; direction comes from source/destination, never from a negative quantity.

## Error codes (section 15)

| HTTP | code |
|------|------|
| 400 | `MALFORMED_REQUEST`, `INVALID_CURSOR`, `IDEMPOTENCY_KEY_REQUIRED` |
| 401 | `UNAUTHENTICATED` |
| 403 | `FORBIDDEN`, `REAUTH_REQUIRED`, `SUBSCRIPTION_RESTRICTED`, `TENANT_SECURITY_SUSPENDED`, `FEATURE_NOT_ENABLED` |
| 404 | `NOT_FOUND` |
| 409 | `INSUFFICIENT_STOCK`, `STOCK_BLOCKED`, `INVALID_STATE`, `IDEMPOTENCY_MISMATCH`, `APPROVAL_STALE`, `APPROVAL_SELF`, `COUNT_LOCKED`, `RETRYABLE_CONFLICT`, `TRIAL_NOT_ELIGIBLE`, `PLAN_LIMIT_REACHED`, `DOWNGRADE_BLOCKED`, `RETENTION_BLOCKED` |
| 412 | `VERSION_CONFLICT` |
| 428 | `PRECONDITION_REQUIRED` |
| 422 | `VALIDATION_FAILED`, `OVER_RECEIPT`, `INCOMPATIBLE_BIN`, `BARCODE_CONFLICT`, `UNKNOWN_BARCODE`, `MISSING_MEDICAL_EVIDENCE`, `RELEASE_INELIGIBLE`, `DUPLICATE_SERIAL`, `RETURN_EXCEEDS_SHIPPED` |
| 429 | `RATE_LIMITED` |
| 503 | `SERVICE_UNAVAILABLE` |

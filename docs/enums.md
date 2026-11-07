# Canonical enumerations

Single source for enum names and values used by the database (PostgreSQL enum types or checked text),
C# domain types and `openapi.yaml`. Values are case-sensitive strings on the wire (section 15).
Changing a value requires a DECISIONS.md entry and a migration.

| Enum | Values | Notes |
|------|--------|-------|
| TenantStatus | `Active`, `Suspended` | Suspension blocks tenant mutations and telemetry ingestion |
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
| AlertType | `Excursion`, `StaleDevice`, `ExpiryApproaching`, `ReconciliationMismatch`, `AuditChainFailure`, `OutboxDeadLetter`, `PutawayException` | |
| AlertSeverity | `Info`, `Warning`, `High` | |
| AlertStatus | `Open`, `Acknowledged`, `Closed` | |
| ExportType | `Audit`, `Movements`, `Traceability`, `StockSnapshot` | |
| ExportStatus | `Queued`, `Running`, `Completed`, `Failed`, `Expired` | |
| AttachmentScanStatus | `Pending`, `Clean`, `Rejected` | |
| ApprovalAction | `Post`, `Release`, `Close`, `Correct`, `Dispose` | What the consuming command will do |
| InspectionResult | `Passed`, `Failed` | Receive item (DEC-021) |
| IneligibilityReason | `Quarantine`, `Hold`, `Recalled`, `Damaged`, `Expired`, `StorageIncompatible`, `StaleTelemetry`, `OpenExcursion`, `CountLocked`, `BinInactive`, `TransitLocation` | Why a bucket's allocatable quantity is zero (STK-001) |
| DocumentType | `Receipt`, `PutawayTask`, `BinTransfer`, `OutboundOrder`, `Shipment`, `Transfer`, `Count`, `Adjustment`, `Return`, `Recall`, `QualityDecision`, `Excursion`, `ExpiryCorrection` | Movement document reference |
| TransitDispositionType | `Lost`, `Damaged` | STK-003 |
| QualityDispositionType | `Damaged`, `SupplierReturn`, `Disposal` | |
| RecallOutreachStatus | `Pending`, `Contacted`, `Acknowledged`, `Completed` | Manual outreach record (EXC-006) |
| ReadingProcessingStatus | `Stored`, `ExcursionOpened`, `LateReviewQueued` | |
| AuditVerificationFailure | `HashMismatch`, `SequenceGap`, `CheckpointMismatch`, `MissingCheckpoint` | |
| ActorType | `User`, `Service`, `Device`, `System` | |
| Role | `PlatformAdministrator`, `TenantAdministrator`, `WarehouseManager`, `Receiver`, `Picker`, `Dispatcher`, `QaOfficer`, `InventoryApprover`, `Auditor`, `TelemetryIntegration` | Bundles in `permissions.md` |

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
| 403 | `FORBIDDEN`, `REAUTH_REQUIRED` |
| 404 | `NOT_FOUND` |
| 409 | `INSUFFICIENT_STOCK`, `STOCK_BLOCKED`, `INVALID_STATE`, `IDEMPOTENCY_MISMATCH`, `APPROVAL_STALE`, `APPROVAL_SELF`, `COUNT_LOCKED`, `RETRYABLE_CONFLICT` |
| 412 | `VERSION_CONFLICT` |
| 428 | `PRECONDITION_REQUIRED` |
| 422 | `VALIDATION_FAILED`, `OVER_RECEIPT`, `INCOMPATIBLE_BIN`, `BARCODE_CONFLICT`, `UNKNOWN_BARCODE`, `MISSING_MEDICAL_EVIDENCE`, `DUPLICATE_SERIAL`, `RETURN_EXCEEDS_SHIPPED` |
| 429 | `RATE_LIMITED` |
| 503 | `SERVICE_UNAVAILABLE` |

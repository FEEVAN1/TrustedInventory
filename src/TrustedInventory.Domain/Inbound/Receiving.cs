using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Common;

namespace TrustedInventory.Domain.Inbound;

public enum QualityStatus
{
    Quarantine,
    Available,
    Hold,
    Recalled,
    Damaged,
}

public enum InspectionResult
{
    Passed,
    Failed,
}

public enum ReceiptStatus
{
    Draft,
    Posted,
    Closed,
    Cancelled,
}

/// <summary>ti.movement_type (docs/enums.md); direction rules are CHECKs in 0006.</summary>
public enum MovementType
{
    Receipt, Putaway, BinTransfer, Pick, Unpick, Dispatch, TransferDispatch, TransferReceipt, TransitDisposition,
    QualityChange, AdjustmentIncrease, AdjustmentDecrease, ReturnReceipt, Disposal, SupplierReturn, Reversal,
}

/// <summary>ti.document_type (docs/enums.md): the document a movement belongs to.</summary>
public enum DocumentType
{
    Receipt, PutawayTask, BinTransfer, OutboundOrder, Shipment, Transfer, Count, Adjustment, Return, Recall, QualityDecision,
    Excursion, ExpiryCorrection,
}

/// <summary>Initial quality of received stock (INB-003, INB-004, DEC-048).</summary>
public static class ReceivingRules
{
    /// <param name="exceptional">The arrival is expired or outside the product's temperature range.</param>
    public static QualityStatus InitialQuality(HandlingProfile profile, InspectionResult inspection, bool exceptional) =>
        profile == HandlingProfile.Medical ? QualityStatus.Quarantine
        : inspection == InspectionResult.Failed || exceptional ? QualityStatus.Hold
        : QualityStatus.Available;

    /// <summary>A reason is required for failed inspections and for expired or out-of-range arrivals.</summary>
    public static bool NeedsReason(InspectionResult inspection, bool exceptional) =>
        inspection == InspectionResult.Failed || exceptional;

    public static bool IsExpired(DateOnly? expiry, DateOnly warehouseToday) => expiry is { } e && e < warehouseToday;

    /// <summary>Allocation, picking and dispatch block a lot from its expiry day on, in the warehouse time zone (STK-001, AT-MED-01 rule, DEC-055).</summary>
    public static bool IsShippable(DateOnly? expiry, DateOnly warehouseToday) => expiry is not { } e || e > warehouseToday;

    public static bool IsOutOfRange(decimal? measuredC, decimal? profileMinC, decimal? profileMaxC) =>
        measuredC is { } t && profileMinC is { } min && profileMaxC is { } max && (t < min || t > max);
}

/// <summary>A posted receipt (DEC-005: created and posted by one command; Closed once putaway completes).</summary>
public sealed class Receipt : TenantEntity
{
    private Receipt() { }

    public Guid PurchaseOrderId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public ReceiptStatus Status { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }

    public static Receipt Post(Guid tenantId, Guid purchaseOrderId, Guid warehouseId, string actor, DateTimeOffset now)
    {
        var receipt = new Receipt
        {
            PurchaseOrderId = purchaseOrderId,
            WarehouseId = warehouseId,
            Status = ReceiptStatus.Posted,
            ReceivedAt = now,
        };
        receipt.Stamp(tenantId, actor, now);
        return receipt;
    }

    /// <summary>Posted → Closed once every putaway task of the receipt is complete (DEC-005).</summary>
    public void Close(DateTimeOffset now)
    {
        if (Status != ReceiptStatus.Posted)
            throw new DomainException("Only a posted receipt can be closed.");
        Status = ReceiptStatus.Closed;
        ClosedAt = now;
        Touch(now);
    }
}

/// <summary>Append-only receipt line: what arrived, where it landed and in which quality (INB-004).</summary>
public sealed class ReceiptLine
{
    private ReceiptLine() { }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid TenantId { get; private set; }
    public Guid ReceiptId { get; private set; }
    public Guid PurchaseOrderLineId { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid? LotId { get; private set; }
    public Guid UomId { get; private set; }
    public decimal UomFactorToBase { get; private set; }
    public decimal UomQty { get; private set; }
    public decimal BaseQty { get; private set; }
    public decimal? MeasuredTemperatureC { get; private set; }
    public Guid StagingBinId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public QualityStatus QualityStatus { get; private set; }
    public InspectionResult InspectionResult { get; private set; }
    public string? SupplierReference { get; private set; }
    public string? ScannedBarcode { get; private set; }
    public string? QualityReason { get; private set; }
    public Guid BucketId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = "";

    public static ReceiptLine Create(Guid tenantId, Guid receiptId, Guid purchaseOrderLineId, Guid productId, Guid? lotId, Guid uomId,
        decimal factorToBase, decimal uomQty, decimal baseQty, decimal? temperatureC, Guid stagingBinId, Guid warehouseId,
        QualityStatus quality, InspectionResult inspection, string? supplierReference, string? scannedBarcode, string? qualityReason,
        Guid bucketId, string actor, DateTimeOffset now)
    {
        if (quality == QualityStatus.Hold && string.IsNullOrWhiteSpace(qualityReason))
            throw new DomainException("Stock received on hold needs a reason.");
        if (inspection == InspectionResult.Failed && quality == QualityStatus.Available)
            throw new DomainException("Stock that failed inspection cannot be available.");
        return new ReceiptLine
        {
            TenantId = tenantId, ReceiptId = receiptId, PurchaseOrderLineId = purchaseOrderLineId, ProductId = productId, LotId = lotId,
            UomId = uomId, UomFactorToBase = factorToBase, UomQty = uomQty, BaseQty = baseQty, MeasuredTemperatureC = temperatureC,
            StagingBinId = stagingBinId, WarehouseId = warehouseId, QualityStatus = quality, InspectionResult = inspection,
            SupplierReference = supplierReference, ScannedBarcode = scannedBarcode, QualityReason = qualityReason, BucketId = bucketId,
            CreatedAt = now, CreatedBy = actor,
        };
    }
}

/// <summary>Links a received serial to its receipt line; a serial is received once (INB-004).</summary>
public sealed class ReceiptLineSerial
{
    private ReceiptLineSerial() { }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid TenantId { get; private set; }
    public Guid ReceiptLineId { get; private set; }
    public Guid SerialId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = "";

    public static ReceiptLineSerial Create(Guid tenantId, Guid receiptLineId, Guid serialId, string actor, DateTimeOffset now) =>
        new() { TenantId = tenantId, ReceiptLineId = receiptLineId, SerialId = serialId, CreatedAt = now, CreatedBy = actor };
}

/// <summary>A physical serial unit of a product, unique per product.</summary>
public sealed class Serial
{
    private Serial() { }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid TenantId { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid? LotId { get; private set; }
    public string SerialNumber { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = "";

    public static Serial Create(Guid tenantId, Guid productId, Guid? lotId, string serialNumber, string actor, DateTimeOffset now) =>
        new() { TenantId = tenantId, ProductId = productId, LotId = lotId, SerialNumber = serialNumber, CreatedAt = now, CreatedBy = actor };
}

/// <summary>Append-only ledger entry (STK-004). Buckets are the projection; this is the authority.</summary>
public sealed class StockMovement
{
    private StockMovement() { }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid TenantId { get; private set; }
    public MovementType MovementType { get; private set; }
    public string OperationKey { get; private set; } = "";
    public Guid ProductId { get; private set; }
    public Guid? LotId { get; private set; }
    public Guid? SerialId { get; private set; }
    public Guid? SourceBucketId { get; private set; }
    public Guid? DestinationBucketId { get; private set; }
    public decimal BaseQty { get; private set; }
    public DocumentType DocumentType { get; private set; }
    public Guid DocumentId { get; private set; }
    public Guid? DocumentLineId { get; private set; }
    public Guid? ReservationId { get; private set; }
    public Guid? ReversalOfId { get; private set; }
    public string? Reason { get; private set; }
    public string ActorSubject { get; private set; } = "";
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }
    public string? CorrelationId { get; private set; }

    /// <summary>
    /// A customer return step (EXC-005, DEC-060): receipt into quarantine (destination only), a quality decision between
    /// buckets (release or damage), or goods leaving stock (supplier return or disposal, source only).
    /// </summary>
    public static StockMovement Return(MovementType type, Guid tenantId, string operationKey, Guid productId, Guid? lotId, Guid? serialId,
        Guid? sourceBucketId, Guid? destinationBucketId, decimal baseQty, Guid returnId, string? reason, string actor, DateTimeOffset now,
        string? correlationId)
    {
        var shapeOk = type switch
        {
            MovementType.ReturnReceipt => sourceBucketId is null && destinationBucketId is not null,
            MovementType.QualityChange => sourceBucketId is not null && destinationBucketId is not null && sourceBucketId != destinationBucketId,
            MovementType.SupplierReturn or MovementType.Disposal => sourceBucketId is not null && destinationBucketId is null,
            _ => false,
        };
        if (!shapeOk)
            throw new DomainException("Not a valid return movement.");
        if (baseQty <= 0)
            throw new DomainException("A movement quantity must be positive.");
        return new StockMovement
        {
            TenantId = tenantId, MovementType = type, OperationKey = operationKey, ProductId = productId, LotId = lotId, SerialId = serialId,
            SourceBucketId = sourceBucketId, DestinationBucketId = destinationBucketId, BaseQty = baseQty, DocumentType = DocumentType.Return,
            DocumentId = returnId, Reason = reason, ActorSubject = actor, OccurredAt = now, RecordedAt = now, CorrelationId = correlationId,
        };
    }

    /// <summary>
    /// An approved adjustment (EXC-004, DEC-057): an increase enters the bucket from outside the ledger, a reduction
    /// leaves it. The quantity on the movement is always positive; the type carries the direction.
    /// </summary>
    /// <summary>
    /// A quality decision (DEC-075): a status change between buckets (QualityChange) or goods leaving stock (SupplierReturn,
    /// Disposal). The document is the quality decision or recall that caused it.
    /// </summary>
    public static StockMovement Quality(MovementType type, DocumentType document, Guid tenantId, string operationKey, Guid productId, Guid? lotId,
        Guid? serialId, Guid sourceBucketId, Guid? destinationBucketId, decimal baseQty, Guid documentId, string reason, string actor, DateTimeOffset now,
        string? correlationId)
    {
        var shapeOk = type switch
        {
            MovementType.QualityChange => destinationBucketId is { } d && d != sourceBucketId,
            MovementType.SupplierReturn or MovementType.Disposal => destinationBucketId is null,
            _ => false,
        };
        if (!shapeOk || document is not (DocumentType.QualityDecision or DocumentType.Recall or DocumentType.Excursion))
            throw new DomainException("Not a valid quality movement.");
        if (baseQty <= 0)
            throw new DomainException("A movement quantity must be positive.");
        return new StockMovement
        {
            TenantId = tenantId, MovementType = type, OperationKey = operationKey, ProductId = productId, LotId = lotId, SerialId = serialId,
            SourceBucketId = sourceBucketId, DestinationBucketId = destinationBucketId, BaseQty = baseQty, DocumentType = document,
            DocumentId = documentId, Reason = reason, ActorSubject = actor, OccurredAt = now, RecordedAt = now, CorrelationId = correlationId,
        };
    }

    public static StockMovement Adjustment(Guid tenantId, string operationKey, Guid productId, Guid? lotId, Guid? serialId, Guid bucketId, decimal delta,
        Guid adjustmentId, string reason, string actor, DateTimeOffset now, string? correlationId)
    {
        if (delta == 0)
            throw new DomainException("An adjustment changes stock by a non-zero quantity.");
        var increase = delta > 0;
        return new StockMovement
        {
            TenantId = tenantId, MovementType = increase ? MovementType.AdjustmentIncrease : MovementType.AdjustmentDecrease, OperationKey = operationKey,
            ProductId = productId, LotId = lotId, SerialId = serialId, SourceBucketId = increase ? null : bucketId,
            DestinationBucketId = increase ? bucketId : null, BaseQty = Math.Abs(delta), DocumentType = DocumentType.Adjustment, DocumentId = adjustmentId,
            Reason = reason, ActorSubject = actor, OccurredAt = now, RecordedAt = now, CorrelationId = correlationId,
        };
    }

    /// <summary>An atomic move between bins of one warehouse; lot, serial and quality preserved (EXC-002, DEC-058).</summary>
    public static StockMovement BinTransfer(Guid tenantId, string operationKey, Guid productId, Guid? lotId, Guid? serialId, Guid sourceBucketId,
        Guid destinationBucketId, decimal baseQty, Guid operationId, string? reason, string actor, DateTimeOffset now, string? correlationId)
    {
        if (baseQty <= 0)
            throw new DomainException("A movement quantity must be positive.");
        if (sourceBucketId == destinationBucketId)
            throw new DomainException("A bin transfer must move stock to a different bucket.");
        return new StockMovement
        {
            TenantId = tenantId, MovementType = MovementType.BinTransfer, OperationKey = operationKey, ProductId = productId, LotId = lotId, SerialId = serialId,
            SourceBucketId = sourceBucketId, DestinationBucketId = destinationBucketId, BaseQty = baseQty, DocumentType = DocumentType.BinTransfer,
            DocumentId = operationId, Reason = reason, ActorSubject = actor, OccurredAt = now, RecordedAt = now, CorrelationId = correlationId,
        };
    }

    /// <summary>
    /// A warehouse transfer step (STK-003, DEC-058): dispatch into the source Transit bin, receipt out of it, or an approved
    /// disposition of lost or damaged transit stock (no destination).
    /// </summary>
    public static StockMovement Transfer(MovementType type, Guid tenantId, string operationKey, Guid productId, Guid? lotId, Guid? serialId,
        Guid sourceBucketId, Guid? destinationBucketId, decimal baseQty, Guid transferId, Guid lineId, string? reason, string actor, DateTimeOffset now,
        string? correlationId)
    {
        if (type is not (MovementType.TransferDispatch or MovementType.TransferReceipt or MovementType.TransitDisposition))
            throw new DomainException("Not a transfer movement.");
        if (baseQty <= 0)
            throw new DomainException("A movement quantity must be positive.");
        if ((type == MovementType.TransitDisposition) != (destinationBucketId is null))
            throw new DomainException("Only a disposition leaves transit without a destination.");
        return new StockMovement
        {
            TenantId = tenantId, MovementType = type, OperationKey = operationKey, ProductId = productId, LotId = lotId, SerialId = serialId,
            SourceBucketId = sourceBucketId, DestinationBucketId = destinationBucketId, BaseQty = baseQty, DocumentType = DocumentType.Transfer,
            DocumentId = transferId, DocumentLineId = lineId, Reason = reason, ActorSubject = actor, OccurredAt = now, RecordedAt = now,
            CorrelationId = correlationId,
        };
    }

    /// <summary>Picked stock leaving the tenant's stock from its staging bucket (OUT-005, DEC-055).</summary>
    public static StockMovement Dispatch(Guid tenantId, string operationKey, Guid productId, Guid? lotId, Guid? serialId, Guid stagingBucketId,
        decimal baseQty, Guid shipmentId, Guid shipmentLineId, Guid reservationId, string actor, DateTimeOffset now, string? correlationId)
    {
        if (baseQty <= 0)
            throw new DomainException("A movement quantity must be positive.");
        return new StockMovement
        {
            TenantId = tenantId, MovementType = MovementType.Dispatch, OperationKey = operationKey, ProductId = productId, LotId = lotId,
            SerialId = serialId, SourceBucketId = stagingBucketId, BaseQty = baseQty, DocumentType = DocumentType.Shipment, DocumentId = shipmentId,
            DocumentLineId = shipmentLineId, ReservationId = reservationId, ActorSubject = actor, OccurredAt = now, RecordedAt = now,
            CorrelationId = correlationId,
        };
    }

    /// <summary>Staging to storage within tenant stock (INB-006): both buckets, same product, lot, serial and quality.</summary>
    public static StockMovement Putaway(Guid tenantId, string operationKey, Guid productId, Guid? lotId, Guid? serialId, Guid sourceBucketId,
        Guid destinationBucketId, decimal baseQty, Guid taskId, Guid receiptLineId, string? reason, string actor, DateTimeOffset now, string? correlationId)
    {
        if (baseQty <= 0)
            throw new DomainException("A movement quantity must be positive.");
        if (sourceBucketId == destinationBucketId)
            throw new DomainException("Putaway must move stock to a different bucket.");
        return new StockMovement
        {
            TenantId = tenantId, MovementType = MovementType.Putaway, OperationKey = operationKey, ProductId = productId, LotId = lotId,
            SerialId = serialId, SourceBucketId = sourceBucketId, DestinationBucketId = destinationBucketId, BaseQty = baseQty,
            DocumentType = DocumentType.PutawayTask, DocumentId = taskId, DocumentLineId = receiptLineId, Reason = reason, ActorSubject = actor,
            OccurredAt = now, RecordedAt = now, CorrelationId = correlationId,
        };
    }

    /// <summary>
    /// Pick (storage → staging) or unpick (staging → storage) of reserved stock (OUT-004, section 13): both buckets, same
    /// product, lot, serial and quality, and always the reservation segment that moved (STK-002).
    /// </summary>
    public static StockMovement Outbound(MovementType type, Guid tenantId, string operationKey, Guid productId, Guid? lotId, Guid? serialId,
        Guid sourceBucketId, Guid destinationBucketId, decimal baseQty, Guid orderId, Guid orderLineId, Guid reservationId, string? reason, string actor,
        DateTimeOffset now, string? correlationId)
    {
        if (type is not (MovementType.Pick or MovementType.Unpick))
            throw new DomainException("Only pick and unpick move reserved stock between bins.");
        if (baseQty <= 0)
            throw new DomainException("A movement quantity must be positive.");
        if (sourceBucketId == destinationBucketId)
            throw new DomainException("A pick or unpick must move stock to a different bucket.");
        return new StockMovement
        {
            TenantId = tenantId, MovementType = type, OperationKey = operationKey, ProductId = productId, LotId = lotId, SerialId = serialId,
            SourceBucketId = sourceBucketId, DestinationBucketId = destinationBucketId, BaseQty = baseQty, DocumentType = DocumentType.OutboundOrder,
            DocumentId = orderId, DocumentLineId = orderLineId, ReservationId = reservationId, Reason = reason, ActorSubject = actor, OccurredAt = now,
            RecordedAt = now, CorrelationId = correlationId,
        };
    }

    /// <summary>Goods entering stock from outside (no source bucket).</summary>
    public static StockMovement Receipt(Guid tenantId, string operationKey, Guid productId, Guid? lotId, Guid? serialId, Guid destinationBucketId,
        decimal baseQty, Guid receiptId, Guid receiptLineId, string actor, DateTimeOffset now, string? correlationId)
    {
        if (baseQty <= 0)
            throw new DomainException("A movement quantity must be positive.");
        return new StockMovement
        {
            TenantId = tenantId, MovementType = MovementType.Receipt, OperationKey = operationKey, ProductId = productId, LotId = lotId, SerialId = serialId,
            DestinationBucketId = destinationBucketId, BaseQty = baseQty, DocumentType = DocumentType.Receipt, DocumentId = receiptId,
            DocumentLineId = receiptLineId, ActorSubject = actor, OccurredAt = now, RecordedAt = now, CorrelationId = correlationId,
        };
    }
}

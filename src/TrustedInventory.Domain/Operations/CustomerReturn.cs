using TrustedInventory.Domain.Common;

namespace TrustedInventory.Domain.Operations;

public enum ReturnStatus
{
    Received, Inspected, Closed,
}

public enum ReturnDisposition
{
    Release, Damaged, SupplierReturn, Disposal,
}

/// <summary>
/// A customer return against a dispatched shipment line (EXC-005, DEC-060). Goods enter Quarantine; inspections may split
/// the quantity, and the return is Inspected once everything has a decision.
/// </summary>
public sealed class CustomerReturn : TenantEntity
{
    private CustomerReturn() { }

    public Guid WarehouseId { get; private set; }
    public Guid ShipmentLineId { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid? SerialId { get; private set; }
    public decimal ReturnedQty { get; private set; }
    public Guid QuarantineBinId { get; private set; }
    public Guid BucketId { get; private set; }
    public Guid ReceiptMovementId { get; private set; }
    public string Reason { get; private set; } = "";
    public ReturnStatus Status { get; private set; }
    public decimal InspectedQty { get; private set; }
    public ReturnDisposition? Disposition { get; private set; }
    public Guid? ApprovalId { get; private set; }

    public decimal RemainingToInspect => ReturnedQty - InspectedQty;

    public static CustomerReturn Receive(Guid id, Guid tenantId, Guid warehouseId, Guid shipmentLineId, Guid productId, Guid? serialId, decimal qty,
        Guid quarantineBinId, Guid bucketId, Guid receiptMovementId, string reason, string actor, DateTimeOffset now)
    {
        if (qty <= 0)
            throw new DomainException("A return quantity must be positive.");
        if (serialId is not null && qty != 1)
            throw new DomainException("A serial-tracked return is one unit.");
        var ret = new CustomerReturn
        {
            Id = id, WarehouseId = warehouseId, ShipmentLineId = shipmentLineId, ProductId = productId, SerialId = serialId, ReturnedQty = qty,
            QuarantineBinId = quarantineBinId, BucketId = bucketId, ReceiptMovementId = receiptMovementId, Reason = reason, Status = ReturnStatus.Received,
        };
        ret.Stamp(tenantId, actor, now);
        return ret;
    }

    /// <summary>Records an inspection; a medical release names the QA approval it used (DEC-091).</summary>
    public void Inspect(ReturnDisposition disposition, decimal qty, DateTimeOffset now, Guid? approvalId = null)
    {
        if (Status != ReturnStatus.Received)
            throw new DomainException("This return is already fully inspected.");
        if (qty <= 0 || qty > RemainingToInspect)
            throw new DomainException("An inspection cannot exceed what remains to inspect.");
        InspectedQty += qty;
        Disposition = disposition;
        if (approvalId is not null)
            ApprovalId = approvalId;
        if (InspectedQty == ReturnedQty)
            Status = ReturnStatus.Inspected;
        Touch(now);
    }
}

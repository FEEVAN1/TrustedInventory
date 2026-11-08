using TrustedInventory.Domain.Common;

namespace TrustedInventory.Domain.Inbound;

public enum PurchaseOrderStatus
{
    Draft,
    Approved,
    PartReceived,
    Received,
    ShortClosed,
    Cancelled,
}

/// <summary>
/// Purchase order (INB-001, DEC-047). Draft orders are edited freely; approval makes the order receivable and
/// freezes its lines. An order closes only when fully received, short-closed with a reason, or cancelled before
/// anything was received.
/// </summary>
public sealed class PurchaseOrder : TenantEntity
{
    private PurchaseOrder() { }

    public Guid SupplierId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public string Reference { get; private set; } = "";
    public PurchaseOrderStatus Status { get; private set; }
    public string? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public string? ShortCloseReason { get; private set; }

    public static PurchaseOrder Create(Guid tenantId, Guid supplierId, Guid warehouseId, string reference, string actor, DateTimeOffset now)
    {
        var order = new PurchaseOrder
        {
            SupplierId = supplierId,
            WarehouseId = warehouseId,
            Reference = reference,
            Status = PurchaseOrderStatus.Draft,
        };
        order.Stamp(tenantId, actor, now);
        return order;
    }

    public bool IsFinal => Status is PurchaseOrderStatus.Received or PurchaseOrderStatus.ShortClosed or PurchaseOrderStatus.Cancelled;

    /// <summary>Changes the reference and/or replaces the lines (the caller swaps the line rows).</summary>
    public void EditDraft(string? reference, DateTimeOffset now)
    {
        if (Status != PurchaseOrderStatus.Draft)
            throw new DomainException("Only a draft purchase order can be edited.");
        Reference = reference ?? Reference;
        Touch(now);
    }

    public void Approve(string actor, DateTimeOffset now)
    {
        if (Status != PurchaseOrderStatus.Draft)
            throw new DomainException("Only a draft purchase order can be approved.");
        Status = PurchaseOrderStatus.Approved;
        ApprovedBy = actor;
        ApprovedAt = now;
        Touch(now);
    }

    /// <summary>Cancels an order that never received anything (a received quantity means short-close instead).</summary>
    public void Cancel(bool anythingReceived, DateTimeOffset now)
    {
        if (Status is not (PurchaseOrderStatus.Draft or PurchaseOrderStatus.Approved) || anythingReceived)
            throw new DomainException("Only a draft or approved purchase order with no receipts can be cancelled.");
        Status = PurchaseOrderStatus.Cancelled;
        Touch(now);
    }

    public void ShortClose(string reason, DateTimeOffset now)
    {
        if (Status is not (PurchaseOrderStatus.Approved or PurchaseOrderStatus.PartReceived))
            throw new DomainException("Only an approved or partly received purchase order can be short-closed.");
        Status = PurchaseOrderStatus.ShortClosed;
        ShortCloseReason = reason;
        Touch(now);
    }
}

/// <summary>An ordered product in a unit, with the unit's factor to the base unit frozen at ordering time.</summary>
public sealed class PurchaseOrderLine : TenantEntity
{
    private PurchaseOrderLine() { }

    public Guid PurchaseOrderId { get; private set; }
    public int LineNumber { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid UomId { get; private set; }
    public decimal UomFactorToBase { get; private set; }
    public decimal OrderedUomQty { get; private set; }
    public decimal OrderedBaseQty { get; private set; }
    public decimal ReceivedBaseQty { get; private set; }

    public decimal OutstandingBaseQty => OrderedBaseQty - ReceivedBaseQty;

    public static PurchaseOrderLine Create(Guid tenantId, Guid purchaseOrderId, int lineNumber, Guid productId, Guid uomId,
        decimal factorToBase, decimal orderedUomQty, decimal orderedBaseQty, string actor, DateTimeOffset now)
    {
        if (lineNumber < 1)
            throw new DomainException("Line numbers start at 1.");
        if (factorToBase <= 0 || orderedUomQty <= 0 || orderedBaseQty <= 0)
            throw new DomainException("Ordered quantities and factors must be positive.");
        var line = new PurchaseOrderLine
        {
            PurchaseOrderId = purchaseOrderId,
            LineNumber = lineNumber,
            ProductId = productId,
            UomId = uomId,
            UomFactorToBase = factorToBase,
            OrderedUomQty = orderedUomQty,
            OrderedBaseQty = orderedBaseQty,
        };
        line.Stamp(tenantId, actor, now);
        return line;
    }
}

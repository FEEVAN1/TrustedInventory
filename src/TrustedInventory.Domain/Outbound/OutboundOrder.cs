using TrustedInventory.Domain.Common;

namespace TrustedInventory.Domain.Outbound;

public enum OutboundOrderStatus
{
    Draft,
    Confirmed,
    PartAllocated,
    Allocated,
    PartPicked,
    Picked,
    PartDispatched,
    Dispatched,
    Closed,
    Cancelled,
}

public enum ReservationState
{
    Active,
    Picked,
    Consumed,
    Released,
    Expired,
}

/// <summary>Line quantities that drive the order's aggregate status.</summary>
public readonly record struct LineProgress(decimal Requested, decimal Allocated, decimal Picked, decimal Dispatched);

/// <summary>
/// Outbound order (OUT-001, DEC-052). Draft and Cancelled are stored decisions; every confirmed status is derived from
/// line quantities (section 13) through <see cref="Recalculate"/>.
/// </summary>
public sealed class OutboundOrder : TenantEntity
{
    private OutboundOrder() { }

    public Guid CustomerId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public string Reference { get; private set; } = "";
    public DateOnly PlannedDispatchDate { get; private set; }
    public OutboundOrderStatus Status { get; private set; }
    public string? ConfirmedBy { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }
    public string? CancelReason { get; private set; }

    public static OutboundOrder Create(Guid tenantId, Guid customerId, Guid warehouseId, string reference, DateOnly plannedDispatchDate,
        string actor, DateTimeOffset now)
    {
        var order = new OutboundOrder
        {
            CustomerId = customerId, WarehouseId = warehouseId, Reference = reference, PlannedDispatchDate = plannedDispatchDate,
            Status = OutboundOrderStatus.Draft,
        };
        order.Stamp(tenantId, actor, now);
        return order;
    }

    public void EditDraft(string? reference, DateOnly? plannedDispatchDate, DateTimeOffset now)
    {
        if (Status != OutboundOrderStatus.Draft)
            throw new DomainException("Only a draft order can be edited.");
        Reference = reference ?? Reference;
        PlannedDispatchDate = plannedDispatchDate ?? PlannedDispatchDate;
        Touch(now);
    }

    public void Confirm(string actor, DateTimeOffset now)
    {
        if (Status != OutboundOrderStatus.Draft)
            throw new DomainException("Only a draft order can be confirmed.");
        Status = OutboundOrderStatus.Confirmed;
        ConfirmedBy = actor;
        ConfirmedAt = now;
        Touch(now);
    }

    /// <summary>OUT-006: only once nothing is picked or dispatched; the caller has released the active reservations.</summary>
    public void Cancel(string reason, IEnumerable<LineProgress> lines, DateTimeOffset now)
    {
        if (Status is OutboundOrderStatus.Cancelled or OutboundOrderStatus.Closed or OutboundOrderStatus.Dispatched)
            throw new DomainException("The order is already closed.");
        if (lines.Any(l => l.Picked > 0 || l.Dispatched > 0))
            throw new DomainException("Picked stock must be unpicked before the order can be cancelled.");
        Status = OutboundOrderStatus.Cancelled;
        CancelReason = reason;
        Touch(now);
    }

    /// <summary>Re-derives a confirmed order's status from its lines (section 13); Draft and Cancelled are left alone.</summary>
    public void Recalculate(IReadOnlyCollection<LineProgress> lines, DateTimeOffset now)
    {
        if (Status is OutboundOrderStatus.Draft or OutboundOrderStatus.Cancelled or OutboundOrderStatus.Closed)
            return;
        var derived = Derive(lines);
        if (derived != Status)
        {
            Status = derived;
            Touch(now);
        }
    }

    public static OutboundOrderStatus Derive(IReadOnlyCollection<LineProgress> lines) =>
        lines.Count > 0 && lines.All(l => l.Dispatched == l.Requested) ? OutboundOrderStatus.Dispatched
        : lines.Any(l => l.Dispatched > 0) ? OutboundOrderStatus.PartDispatched
        : lines.Count > 0 && lines.All(l => l.Picked == l.Requested) ? OutboundOrderStatus.Picked
        : lines.Any(l => l.Picked > 0) ? OutboundOrderStatus.PartPicked
        : lines.Count > 0 && lines.All(l => l.Allocated == l.Requested) ? OutboundOrderStatus.Allocated
        : lines.Any(l => l.Allocated > 0) ? OutboundOrderStatus.PartAllocated
        : OutboundOrderStatus.Confirmed;
}

public sealed class OutboundOrderLine : TenantEntity
{
    private OutboundOrderLine() { }

    public Guid OrderId { get; private set; }
    public int LineNumber { get; private set; }
    public Guid ProductId { get; private set; }
    public decimal RequestedBaseQty { get; private set; }
    public int MinRemainingShelfLifeDays { get; private set; }
    public decimal AllocatedBaseQty { get; private set; }
    public decimal PickedBaseQty { get; private set; }
    public decimal DispatchedBaseQty { get; private set; }

    public LineProgress Progress => new(RequestedBaseQty, AllocatedBaseQty, PickedBaseQty, DispatchedBaseQty);

    public static OutboundOrderLine Create(Guid tenantId, Guid orderId, int lineNumber, Guid productId, decimal requested, int minShelfLifeDays,
        string actor, DateTimeOffset now)
    {
        if (lineNumber < 1 || requested <= 0 || minShelfLifeDays < 0)
            throw new DomainException("Invalid order line.");
        var line = new OutboundOrderLine
        {
            OrderId = orderId, LineNumber = lineNumber, ProductId = productId, RequestedBaseQty = requested,
            MinRemainingShelfLifeDays = minShelfLifeDays,
        };
        line.Stamp(tenantId, actor, now);
        return line;
    }

    /// <summary>Releases unpicked allocation (cancellation, deallocation, expiry).</summary>
    public void ReleaseAllocation(decimal qty, DateTimeOffset now)
    {
        if (qty <= 0 || qty > AllocatedBaseQty - PickedBaseQty)
            throw new DomainException("Only unpicked allocation can be released.");
        AllocatedBaseQty -= qty;
        Touch(now);
    }
}

/// <summary>A stock commitment segment (section 13, STK-002, STK-008).</summary>
public sealed class Reservation : TenantEntity
{
    private Reservation() { }

    public Guid OrderLineId { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid BucketId { get; private set; }
    public decimal Qty { get; private set; }
    public ReservationState State { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public Guid? ParentReservationId { get; private set; }

    /// <summary>Active → Released (cancellation, deallocation). The caller returns the quantity to the bucket.</summary>
    public void Release(DateTimeOffset now)
    {
        if (State != ReservationState.Active)
            throw new DomainException("Only an active reservation can be released.");
        State = ReservationState.Released;
        ExpiresAt = null;
        Touch(now);
    }
}

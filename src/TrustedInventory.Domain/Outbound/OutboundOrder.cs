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

    /// <summary>Allocation applies to confirmed orders that are not finished (STK-006).</summary>
    public bool IsAllocatable => Status is OutboundOrderStatus.Confirmed or OutboundOrderStatus.PartAllocated or OutboundOrderStatus.Allocated
        or OutboundOrderStatus.PartPicked or OutboundOrderStatus.Picked or OutboundOrderStatus.PartDispatched;

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

    public decimal Unallocated => RequestedBaseQty - AllocatedBaseQty;

    /// <summary>Allocates part of the unreserved remainder (STK-006).</summary>
    public void Allocate(decimal qty, DateTimeOffset now)
    {
        if (qty <= 0 || qty > Unallocated)
            throw new DomainException("Allocation cannot exceed the unreserved remainder of the line.");
        AllocatedBaseQty += qty;
        Touch(now);
    }

    /// <summary>Records picked quantity (OUT-004); it never exceeds what is allocated.</summary>
    public void Pick(decimal qty, DateTimeOffset now)
    {
        if (qty <= 0 || PickedBaseQty + qty > AllocatedBaseQty)
            throw new DomainException("Picked quantity cannot exceed the allocated quantity of the line.");
        PickedBaseQty += qty;
        Touch(now);
    }

    /// <summary>Returns picked quantity to allocated-but-unpicked (unpick, section 13).</summary>
    public void Unpick(decimal qty, DateTimeOffset now)
    {
        if (qty <= 0 || qty > PickedBaseQty - DispatchedBaseQty)
            throw new DomainException("Only picked, undispatched quantity can be unpicked.");
        PickedBaseQty -= qty;
        Touch(now);
    }

    /// <summary>Records dispatched quantity (OUT-005); only picked quantity ships.</summary>
    public void Dispatch(decimal qty, DateTimeOffset now)
    {
        if (qty <= 0 || DispatchedBaseQty + qty > PickedBaseQty)
            throw new DomainException("Dispatched quantity cannot exceed the picked quantity of the line.");
        DispatchedBaseQty += qty;
        Touch(now);
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

    /// <summary>A new Active commitment on a bucket; unpicked reservations expire (STK-008).</summary>
    public static Reservation Create(Guid tenantId, Guid orderLineId, Guid productId, Guid bucketId, decimal qty, DateTimeOffset expiresAt,
        string actor, DateTimeOffset now)
    {
        if (qty <= 0)
            throw new DomainException("A reservation quantity must be positive.");
        if (expiresAt <= now)
            throw new DomainException("A reservation must expire in the future.");
        var reservation = new Reservation
        {
            OrderLineId = orderLineId, ProductId = productId, BucketId = bucketId, Qty = qty, State = ReservationState.Active, ExpiresAt = expiresAt,
        };
        reservation.Stamp(tenantId, actor, now);
        return reservation;
    }

    /// <summary>The Picked child split off a pick's own segment on the staging bucket (DEC-054).</summary>
    public static Reservation CreatePicked(Guid tenantId, Guid orderLineId, Guid productId, Guid stagingBucketId, decimal qty, Guid parentId,
        string actor, DateTimeOffset now)
    {
        if (qty <= 0)
            throw new DomainException("A reservation quantity must be positive.");
        var reservation = new Reservation
        {
            OrderLineId = orderLineId, ProductId = productId, BucketId = stagingBucketId, Qty = qty, State = ReservationState.Picked,
            ParentReservationId = parentId,
        };
        reservation.Stamp(tenantId, actor, now);
        return reservation;
    }

    /// <summary>Moves part of the quantity out of this live segment; the segment never becomes empty.</summary>
    public void Shrink(decimal qty, DateTimeOffset now)
    {
        if (State is not (ReservationState.Active or ReservationState.Picked) || qty <= 0 || qty >= Qty)
            throw new DomainException("A segment can only give up part of its quantity.");
        Qty -= qty;
        Touch(now);
    }

    /// <summary>Adds quantity to this live segment on the same bucket.</summary>
    public void Grow(decimal qty, DateTimeOffset now)
    {
        if (State is not (ReservationState.Active or ReservationState.Picked) || qty <= 0)
            throw new DomainException("Only a live segment can grow by a positive quantity.");
        Qty += qty;
        Touch(now);
    }

    /// <summary>Active → Picked on the staging bucket, absorbing a folded child's quantity (DEC-054). Picked never expires.</summary>
    public void MoveToStaging(Guid stagingBucketId, decimal absorbedQty, DateTimeOffset now)
    {
        if (State != ReservationState.Active || absorbedQty < 0)
            throw new DomainException("Only an active reservation can be picked.");
        State = ReservationState.Picked;
        BucketId = stagingBucketId;
        Qty += absorbedQty;
        ExpiresAt = null;
        Touch(now);
    }

    /// <summary>Picked → Active on a storage bucket with the returned quantity (unpick, section 13).</summary>
    public void ReturnToActive(Guid bucketId, decimal qty, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        if (State != ReservationState.Picked || qty <= 0 || qty > Qty)
            throw new DomainException("Only picked quantity can be returned.");
        if (expiresAt <= now)
            throw new DomainException("A reservation must expire in the future.");
        State = ReservationState.Active;
        BucketId = bucketId;
        Qty = qty;
        ExpiresAt = expiresAt;
        Touch(now);
    }

    /// <summary>Picked → Released when a child folds into its parent or is emptied; the quantity stays as history.</summary>
    public void Fold(DateTimeOffset now)
    {
        if (State != ReservationState.Picked || ParentReservationId is null)
            throw new DomainException("Only a picked child segment can fold into its parent.");
        State = ReservationState.Released;
        Touch(now);
    }

    /// <summary>
    /// Releases the Active remainder of a part-picked own segment: it takes over its picked child on the staging bucket
    /// (Active → Picked, quantity = the child's) and the child folds in (DEC-054). The caller returns the remainder to the bucket.
    /// </summary>
    public void ReleaseKeepingPicked(Reservation child, DateTimeOffset now)
    {
        if (State != ReservationState.Active || child.State != ReservationState.Picked || child.ParentReservationId != Id)
            throw new DomainException("Only an active segment with a picked child can keep what was picked.");
        State = ReservationState.Picked;
        BucketId = child.BucketId;
        Qty = child.Qty;
        ExpiresAt = null;
        Touch(now);
        child.Fold(now);
    }

    /// <summary>Picked → Consumed by dispatch (OUT-005). Only a picked segment can be consumed (STK-008).</summary>
    public void Consume(DateTimeOffset now)
    {
        if (State != ReservationState.Picked)
            throw new DomainException("Only a picked reservation can be dispatched.");
        State = ReservationState.Consumed;
        Touch(now);
    }

    /// <summary>Active → Expired by the expiry worker (STK-008). The caller returns the quantity to the bucket.</summary>
    public void Expire(DateTimeOffset now)
    {
        if (State != ReservationState.Active || ExpiresAt > now)
            throw new DomainException("Only an active reservation past its expiry can expire.");
        State = ReservationState.Expired;
        ExpiresAt = null;
        Touch(now);
    }

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

/// <summary>
/// A reservation-level pick task (OUT-003, DEC-054): <c>TaskQty = PickedQty + qty of its own segment while Active</c>.
/// </summary>
public sealed class Pick : TenantEntity
{
    private Pick() { }

    public Guid ReservationId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid SourceBinId { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid? LotId { get; private set; }
    public decimal TaskQty { get; private set; }
    public decimal PickedQty { get; private set; }
    public string? AssigneeSubject { get; private set; }
    public Inbound.TaskStatus Status { get; private set; }

    public decimal RemainingQty => TaskQty - PickedQty;

    public bool IsOpen => Status is Inbound.TaskStatus.Open or Inbound.TaskStatus.InProgress;

    public static Pick Create(Guid tenantId, Guid reservationId, Guid warehouseId, Guid sourceBinId, Guid productId, Guid? lotId, decimal qty,
        string actor, DateTimeOffset now)
    {
        if (qty <= 0)
            throw new DomainException("A pick quantity must be positive.");
        var pick = new Pick
        {
            ReservationId = reservationId, WarehouseId = warehouseId, SourceBinId = sourceBinId, ProductId = productId, LotId = lotId, TaskQty = qty,
            Status = Inbound.TaskStatus.Open,
        };
        pick.Stamp(tenantId, actor, now);
        return pick;
    }

    public void Confirm(decimal qty, DateTimeOffset now)
    {
        if (!IsOpen)
            throw new DomainException("This pick task is closed.");
        if (qty <= 0 || qty > RemainingQty)
            throw new DomainException("A pick cannot exceed the remaining quantity.");
        PickedQty += qty;
        Status = RemainingQty == 0 ? Inbound.TaskStatus.Completed : Inbound.TaskStatus.InProgress;
        Touch(now);
    }

    /// <summary>Returned stock becomes pickable again from <paramref name="sourceBinId"/> (section 13).</summary>
    public void Unpick(decimal qty, Guid sourceBinId, DateTimeOffset now)
    {
        if (Status == Inbound.TaskStatus.Cancelled || qty <= 0 || qty > PickedQty)
            throw new DomainException("Only picked quantity can be returned.");
        PickedQty -= qty;
        SourceBinId = sourceBinId;
        Status = PickedQty == 0 ? Inbound.TaskStatus.Open : Inbound.TaskStatus.InProgress;
        Touch(now);
    }

    /// <summary>The own segment's Active remainder was released: keep what was picked, or cancel an untouched task.</summary>
    public void CloseRemainder(DateTimeOffset now)
    {
        if (!IsOpen)
            throw new DomainException("This pick task is closed.");
        if (PickedQty > 0)
        {
            TaskQty = PickedQty;
            Status = Inbound.TaskStatus.Completed;
        }
        else
        {
            Status = Inbound.TaskStatus.Cancelled;
        }
        Touch(now);
    }
}

/// <summary>
/// A dispatch of picked stock (OUT-005, DEC-055). Append-only: the row is never updated, so the contract's version is
/// always 1 and its update time is the creation time.
/// </summary>
public sealed class Shipment : TenantEntity
{
    private Shipment() { }

    public Guid OrderId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public string DispatchReference { get; private set; } = "";
    public DateTimeOffset DispatchedAt { get; private set; }

    public static Shipment Create(Guid tenantId, Guid orderId, Guid warehouseId, string dispatchReference, string actor, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(dispatchReference))
            throw new DomainException("A dispatch reference is required.");
        var shipment = new Shipment { OrderId = orderId, WarehouseId = warehouseId, DispatchReference = dispatchReference, DispatchedAt = now };
        shipment.Stamp(tenantId, actor, now);
        return shipment;
    }
}

/// <summary>One dispatched reservation segment: product, lot, serial and quantity, plus returns against it (EXC-005).</summary>
public sealed class ShipmentLine : TenantEntity
{
    private ShipmentLine() { }

    public Guid ShipmentId { get; private set; }
    public Guid OrderLineId { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid? LotId { get; private set; }
    public Guid? SerialId { get; private set; }
    public decimal BaseQty { get; private set; }
    public decimal ReturnedQty { get; private set; }
    public Guid MovementId { get; private set; }

    public static ShipmentLine Create(Guid id, Guid tenantId, Guid shipmentId, Guid orderLineId, Guid productId, Guid? lotId, Guid? serialId, decimal qty,
        Guid movementId, string actor, DateTimeOffset now)
    {
        if (qty <= 0)
            throw new DomainException("A shipment line quantity must be positive.");
        var line = new ShipmentLine
        {
            Id = id, ShipmentId = shipmentId, OrderLineId = orderLineId, ProductId = productId, LotId = lotId, SerialId = serialId, BaseQty = qty,
            MovementId = movementId,
        };
        line.Stamp(tenantId, actor, now);
        return line;
    }
}

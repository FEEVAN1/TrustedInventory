using TrustedInventory.Domain.Common;
using TrustedInventory.Domain.Locations;

namespace TrustedInventory.Domain.Inbound;

public enum TaskType
{
    Putaway,
    Rework,
    Exception,
}

public enum TaskStatus
{
    Open,
    InProgress,
    Completed,
    Cancelled,
}

/// <summary>Putaway, rework and exception tasks (DEC-011, DEC-050).</summary>
public sealed class WarehouseTask : TenantEntity
{
    private WarehouseTask() { }

    public Guid WarehouseId { get; private set; }
    public TaskType Type { get; private set; }
    public TaskStatus Status { get; private set; }
    public Guid? ReceiptLineId { get; private set; }
    public Guid? ProductId { get; private set; }
    public Guid? LotId { get; private set; }
    public Guid? SourceBinId { get; private set; }
    public Guid? RecommendedBinId { get; private set; }
    public decimal? TaskQty { get; private set; }
    public decimal? RemainingQty { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? CompletedBy { get; private set; }
    public string? Resolution { get; private set; }
    public string? RelatedType { get; private set; }
    public Guid? RelatedId { get; private set; }

    public bool IsOpen => Status is TaskStatus.Open or TaskStatus.InProgress;

    /// <summary>
    /// A rework task for picked stock that became blocked before dispatch (OUT-006, DEC-055): unpick or segregate the
    /// staged quantity of the pick.
    /// </summary>
    public static WarehouseTask ForRework(Guid tenantId, Guid warehouseId, Guid pickId, Guid productId, Guid? lotId, Guid stagingBinId, decimal quantity,
        string reason, string actor, DateTimeOffset now)
    {
        if (quantity <= 0)
            throw new DomainException("A task quantity must be positive.");
        var task = new WarehouseTask
        {
            WarehouseId = warehouseId, Type = TaskType.Rework, Status = TaskStatus.Open, ProductId = productId, LotId = lotId, SourceBinId = stagingBinId,
            TaskQty = quantity, RemainingQty = quantity, Reason = reason, RelatedType = "Pick", RelatedId = pickId,
        };
        task.Stamp(tenantId, actor, now);
        return task;
    }

    /// <summary>A putaway task for a receipt line (INB-005), or an exception task when no bin is suitable.</summary>
    public static WarehouseTask ForReceiptLine(Guid tenantId, Guid warehouseId, Guid receiptLineId, Guid productId, Guid? lotId, Guid stagingBinId,
        decimal quantity, Guid? recommendedBinId, string actor, DateTimeOffset now)
    {
        if (quantity <= 0)
            throw new DomainException("A task quantity must be positive.");
        var task = new WarehouseTask
        {
            WarehouseId = warehouseId,
            Type = recommendedBinId is null ? TaskType.Exception : TaskType.Putaway,
            Status = TaskStatus.Open,
            ReceiptLineId = receiptLineId,
            ProductId = productId,
            LotId = lotId,
            SourceBinId = stagingBinId,
            RecommendedBinId = recommendedBinId,
            TaskQty = quantity,
            RemainingQty = quantity,
            Reason = recommendedBinId is null ? "No compatible bin with enough capacity; resolve with a suitable bin (INB-005)." : null,
        };
        task.Stamp(tenantId, actor, now);
        return task;
    }

    /// <summary>Records a confirmed move of <paramref name="quantity"/>; completes the task at zero remaining.</summary>
    public void Confirm(decimal quantity, string actor, string? reason, DateTimeOffset now)
    {
        if (!IsOpen)
            throw new DomainException("The task is not open.");
        if (quantity <= 0 || quantity > RemainingQty)
            throw new DomainException("The quantity exceeds what remains on the task.");
        RemainingQty -= quantity;
        if (RemainingQty == 0)
        {
            Status = TaskStatus.Completed;
            CompletedAt = now;
            CompletedBy = actor;
            if (Type is TaskType.Exception or TaskType.Rework)
                Resolution = reason;
        }
        else
            Status = TaskStatus.InProgress;
        Touch(now);
    }

    /// <summary>
    /// Closes a rework or exception task by hand after its stock was moved another way (DEC-085). Putaway tasks close only
    /// through putaway confirmation. The caller checks that nothing of the task is left in its source bin.
    /// </summary>
    public void Complete(string resolution, string actor, DateTimeOffset now)
    {
        if (Type == TaskType.Putaway)
            throw new DomainException("A putaway task completes through putaway confirmation.");
        if (!IsOpen)
            throw new DomainException("The task is not open.");
        if (string.IsNullOrWhiteSpace(resolution))
            throw new DomainException("A resolution is required.");
        RemainingQty = 0;
        Status = TaskStatus.Completed;
        CompletedAt = now;
        CompletedBy = actor;
        Resolution = resolution;
        Touch(now);
    }
}

/// <summary>Everything the planner needs about one bin of the warehouse.</summary>
/// <param name="QuantityInCapacityUnits">The moved quantity in the bin's capacity unit; null when it cannot be expressed.</param>
/// <param name="LoadInCapacityUnits">Current load in the capacity unit; null when some stock in the bin cannot be expressed.</param>
public sealed record BinCandidate(
    Guid BinId, string Code, BinKind Kind, bool Active, int PickPriority, bool ZoneCompatible, decimal? Capacity,
    decimal? QuantityInCapacityUnits, decimal? LoadInCapacityUnits, bool HoldsSameProductAndLot);

public sealed record RankedBin(Guid BinId, string Code, int Rank, string Reason);

/// <summary>
/// Putaway recommendation (INB-005, DEC-050): hard constraints first (active, compatible zone, bin kind for the quality,
/// capacity), then ranking by same product and lot, pick priority and bin code.
/// </summary>
public static class PutawayPlanner
{
    public static bool IsSuitable(BinCandidate bin, QualityStatus quality)
    {
        var kindOk = quality == QualityStatus.Available ? bin.Kind == BinKind.Storage : bin.Kind is BinKind.Storage or BinKind.Quarantine;
        var capacityOk = bin.Capacity is not { } capacity
            || (bin.QuantityInCapacityUnits is { } qty && bin.LoadInCapacityUnits is { } load && load + qty <= capacity);
        return bin.Active && bin.ZoneCompatible && kindOk && capacityOk;
    }

    public static IReadOnlyList<RankedBin> Rank(IEnumerable<BinCandidate> bins, QualityStatus quality) =>
        bins.Where(b => IsSuitable(b, quality))
            .OrderByDescending(b => b.HoldsSameProductAndLot)
            .ThenBy(b => b.PickPriority)
            .ThenBy(b => b.Code, StringComparer.Ordinal)
            .Select((b, i) => new RankedBin(b.BinId, b.Code, i + 1,
                b.HoldsSameProductAndLot ? "Already holds this product and lot" : $"Pick priority {b.PickPriority}"))
            .ToList();
}

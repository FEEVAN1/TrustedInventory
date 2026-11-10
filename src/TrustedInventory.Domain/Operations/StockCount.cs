using TrustedInventory.Domain.Common;

namespace TrustedInventory.Domain.Operations;

public enum CountStatus
{
    Draft, Counting, Submitted, Approved, Posted, Closed, Cancelled,
}

/// <summary>
/// A stock count (EXC-003, DEC-008, DEC-059): Draft → Counting (bins locked, expected snapshot) → Submitted → Approved →
/// Posted, or Closed when nothing differed. Reject opens a new revision; cancellation unlocks and keeps every observation.
/// </summary>
public sealed class StockCount : TenantEntity
{
    private StockCount() { }

    public Guid WarehouseId { get; private set; }
    public string Reason { get; private set; } = "";
    public CountStatus Status { get; private set; }
    public int Revision { get; private set; }
    public long? SnapshotSequence { get; private set; }
    public DateTimeOffset? LockedAt { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }
    public Guid? ApprovalId { get; private set; }
    public string LastModifiedBy { get; private set; } = "";

    public bool IsOpen => Status is CountStatus.Draft or CountStatus.Counting or CountStatus.Submitted or CountStatus.Approved;

    public static StockCount Create(Guid tenantId, Guid warehouseId, string reason, string actor, DateTimeOffset now)
    {
        var count = new StockCount { WarehouseId = warehouseId, Reason = reason, Status = CountStatus.Draft, Revision = 1, LastModifiedBy = actor };
        count.Stamp(tenantId, actor, now);
        return count;
    }

    public void Start(long snapshotSequence, string actor, DateTimeOffset now)
    {
        Require(CountStatus.Draft, "Only a draft count can start.");
        Status = CountStatus.Counting;
        SnapshotSequence = snapshotSequence;
        LockedAt = now;
        Modified(actor, now);
    }

    public void Submit(string actor, DateTimeOffset now)
    {
        Require(CountStatus.Counting, "Only a count in progress can be submitted.");
        Status = CountStatus.Submitted;
        SubmittedAt = now;
        Modified(actor, now);
    }

    public void Approve(Guid approvalId, string actor, DateTimeOffset now)
    {
        Require(CountStatus.Submitted, "Only a submitted count can be approved.");
        Status = CountStatus.Approved;
        ApprovalId = approvalId;
        Modified(actor, now);
    }

    /// <summary>Back to counting as a new revision; earlier observations stay as history (section 13).</summary>
    public void Reject(string actor, DateTimeOffset now)
    {
        if (Status is not (CountStatus.Submitted or CountStatus.Approved))
            throw new DomainException("Only a submitted or approved count can be rejected.");
        Status = CountStatus.Counting;
        Revision++;
        ApprovalId = null;
        Modified(actor, now);
    }

    public void Post(bool anyVariance, string actor, DateTimeOffset now)
    {
        Require(CountStatus.Approved, "Only an approved count can be posted.");
        Status = anyVariance ? CountStatus.Posted : CountStatus.Closed;
        Modified(actor, now);
    }

    public void Cancel(string actor, DateTimeOffset now)
    {
        if (!IsOpen)
            throw new DomainException("This count is already finished.");
        Status = CountStatus.Cancelled;
        ApprovalId = null;
        Modified(actor, now);
    }

    private void Require(CountStatus status, string message)
    {
        if (Status != status)
            throw new DomainException(message);
    }

    private void Modified(string actor, DateTimeOffset now)
    {
        LastModifiedBy = actor;
        Touch(now);
    }
}

/// <summary>A counted bin; <see cref="LockActive"/> blocks every quantity movement in it while counting (DEC-008).</summary>
public sealed class StockCountBin : TenantEntity
{
    private StockCountBin() { }

    public Guid CountId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid BinId { get; private set; }
    public bool LockActive { get; private set; }

    public static StockCountBin Create(Guid tenantId, Guid countId, Guid warehouseId, Guid binId, string actor, DateTimeOffset now)
    {
        var bin = new StockCountBin { CountId = countId, WarehouseId = warehouseId, BinId = binId };
        bin.Stamp(tenantId, actor, now);
        return bin;
    }

    public void Lock(DateTimeOffset now)
    {
        LockActive = true;
        Touch(now);
    }

    public void Unlock(DateTimeOffset now)
    {
        if (!LockActive)
            return;
        LockActive = false;
        Touch(now);
    }
}

/// <summary>One counted dimension of one revision: expected snapshot (never shown to blind counters) and observation.</summary>
public sealed class StockCountLine : TenantEntity
{
    private StockCountLine() { }

    public Guid CountId { get; private set; }
    public int Revision { get; private set; }
    public Guid BinId { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid? LotId { get; private set; }
    public Guid? SerialId { get; private set; }
    public decimal ExpectedQty { get; private set; }
    public decimal? ObservedQty { get; private set; }
    public string? CountedBy { get; private set; }
    public DateTimeOffset? CountedAt { get; private set; }

    /// <summary>observed − expected, or null until observed.</summary>
    public decimal? Variance => ObservedQty - ExpectedQty;

    public static StockCountLine Create(Guid tenantId, Guid countId, int revision, Guid binId, Guid productId, Guid? lotId, Guid? serialId, decimal expected,
        string actor, DateTimeOffset now)
    {
        if (expected < 0)
            throw new DomainException("An expected quantity cannot be negative.");
        var line = new StockCountLine
        {
            CountId = countId, Revision = revision, BinId = binId, ProductId = productId, LotId = lotId, SerialId = serialId, ExpectedQty = expected,
        };
        line.Stamp(tenantId, actor, now);
        return line;
    }

    public void Observe(decimal observed, string counter, DateTimeOffset now)
    {
        if (observed < 0)
            throw new DomainException("An observed quantity cannot be negative.");
        if (ObservedQty is not null)
            throw new DomainException("This line was already counted in this revision.");
        ObservedQty = observed;
        CountedBy = counter;
        CountedAt = now;
        Touch(now);
    }
}

using TrustedInventory.Domain.Common;
using TrustedInventory.Domain.Inbound;

namespace TrustedInventory.Domain.Operations;

public enum AdjustmentStatus
{
    Requested, Approved, Posted, Rejected, Cancelled,
}

/// <summary>
/// A stock adjustment request (EXC-004, DEC-057): signed delta on one stock dimension, posted once against an
/// independent approval of its exact version.
/// </summary>
public sealed class Adjustment : TenantEntity
{
    private Adjustment() { }

    public Guid WarehouseId { get; private set; }
    public Guid? BucketId { get; private set; }
    public Guid BinId { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid? LotId { get; private set; }
    public Guid? SerialId { get; private set; }
    public QualityStatus QualityStatus { get; private set; }
    public decimal DeltaQty { get; private set; }
    public string ReasonCode { get; private set; } = "";
    public string Narrative { get; private set; } = "";
    public Guid? CountId { get; private set; }
    public AdjustmentStatus Status { get; private set; }
    public Guid? ApprovalId { get; private set; }
    public Guid? PostedMovementId { get; private set; }
    public string LastModifiedBy { get; private set; } = "";

    /// <summary>Stored status moves Requested → Posted, Rejected or Cancelled; "Approved" is read from a live signed approval (DEC-057).</summary>
    public bool IsOpen => Status == AdjustmentStatus.Requested;

    public static Adjustment Request(Guid tenantId, Guid warehouseId, Guid? bucketId, Guid binId, Guid productId, Guid? lotId, Guid? serialId,
        QualityStatus quality, decimal delta, string reasonCode, string narrative, Guid? countId, string actor, DateTimeOffset now)
    {
        if (delta == 0)
            throw new DomainException("An adjustment changes stock by a non-zero quantity.");
        if (delta < 0 && bucketId is null)
            throw new DomainException("A reduction must target existing stock.");
        var adjustment = new Adjustment
        {
            WarehouseId = warehouseId, BucketId = bucketId, BinId = binId, ProductId = productId, LotId = lotId, SerialId = serialId,
            QualityStatus = quality, DeltaQty = delta, ReasonCode = reasonCode, Narrative = narrative, CountId = countId,
            Status = AdjustmentStatus.Requested, LastModifiedBy = actor,
        };
        adjustment.Stamp(tenantId, actor, now);
        return adjustment;
    }

    /// <summary>Any change sends the request back for a new approval (IAM-003, AT-EXC-02).</summary>
    public void Edit(decimal? delta, string? reasonCode, string? narrative, string actor, DateTimeOffset now)
    {
        if (!IsOpen)
            throw new DomainException("Only a requested or approved adjustment can be changed.");
        var newDelta = delta ?? DeltaQty;
        if (newDelta == 0 || (newDelta < 0 && BucketId is null))
            throw new DomainException("A reduction must target existing stock.");
        DeltaQty = newDelta;
        ReasonCode = reasonCode ?? ReasonCode;
        Narrative = narrative ?? Narrative;
        Touched(actor, now);
    }

    /// <summary>A change that is not a field edit (the evidence list): new version, new approval needed.</summary>
    public void Touched(string actor, DateTimeOffset now)
    {
        if (!IsOpen)
            throw new DomainException("Only a requested adjustment can be changed.");
        LastModifiedBy = actor;
        Touch(now);
    }

    public void Reject(DateTimeOffset now)
    {
        if (Status != AdjustmentStatus.Requested)
            throw new DomainException("Only a requested adjustment can be rejected.");
        Status = AdjustmentStatus.Rejected;
        Touch(now);
    }

    public void Cancel(string actor, DateTimeOffset now)
    {
        if (!IsOpen)
            throw new DomainException("Only a requested or approved adjustment can be cancelled.");
        Status = AdjustmentStatus.Cancelled;
        LastModifiedBy = actor;
        Touch(now);
    }

    /// <summary>Posted once, against the approval that signed this version (EXC-004).</summary>
    public void Post(Guid approvalId, Guid bucketId, Guid movementId, DateTimeOffset now)
    {
        if (Status != AdjustmentStatus.Requested)
            throw new DomainException("Only a requested adjustment can be posted.");
        ApprovalId = approvalId;
        BucketId = bucketId;
        PostedMovementId = movementId;
        Status = AdjustmentStatus.Posted;
        Touch(now);
    }
}

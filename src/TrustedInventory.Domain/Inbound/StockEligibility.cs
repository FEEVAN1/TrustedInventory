using TrustedInventory.Domain.Locations;

namespace TrustedInventory.Domain.Inbound;

/// <summary>Why a bucket's allocatable quantity is zero (contract IneligibilityReason, docs/enums.md).</summary>
public enum IneligibilityReason
{
    Quarantine,
    Hold,
    Recalled,
    Damaged,
    Expired,
    StorageIncompatible,
    StaleTelemetry,
    OpenExcursion,
    CountLocked,
    BinInactive,
    TransitLocation,
    AwaitingPutaway,
    Blocked,
}

/// <summary>Everything the eligibility rule looks at for one bucket.</summary>
public sealed record BucketFacts(
    QualityStatus Quality, decimal OnHand, decimal Reserved, DateOnly? LotExpiry, DateOnly WarehouseToday, BinKind BinKind, bool BinActive,
    bool StorageCompatible, bool Blocked);

/// <summary>
/// STK-001 (DEC-049): allocatable = on hand − reserved only when the bucket is released (Available), in an active
/// Storage bin whose zone is compatible, unexpired in the warehouse's time zone and not blocked; otherwise 0.
/// Telemetry, excursion and count checks join when those modules exist (M4, M5).
/// </summary>
public static class StockEligibility
{
    public static IReadOnlyList<IneligibilityReason> Reasons(BucketFacts f)
    {
        var reasons = new List<IneligibilityReason>();
        switch (f.Quality)
        {
            case QualityStatus.Quarantine: reasons.Add(IneligibilityReason.Quarantine); break;
            case QualityStatus.Hold: reasons.Add(IneligibilityReason.Hold); break;
            case QualityStatus.Recalled: reasons.Add(IneligibilityReason.Recalled); break;
            case QualityStatus.Damaged: reasons.Add(IneligibilityReason.Damaged); break;
        }
        if (ReceivingRules.IsExpired(f.LotExpiry, f.WarehouseToday))
            reasons.Add(IneligibilityReason.Expired);
        if (!f.StorageCompatible)
            reasons.Add(IneligibilityReason.StorageIncompatible);
        if (!f.BinActive)
            reasons.Add(IneligibilityReason.BinInactive);
        if (f.BinKind == BinKind.Transit)
            reasons.Add(IneligibilityReason.TransitLocation);
        else if (f.BinKind is BinKind.Staging or BinKind.Quarantine)
            reasons.Add(IneligibilityReason.AwaitingPutaway);
        if (f.Blocked)
            reasons.Add(IneligibilityReason.Blocked);
        return reasons;
    }

    public static decimal Allocatable(BucketFacts f) => Reasons(f).Count == 0 ? f.OnHand - f.Reserved : 0m;
}

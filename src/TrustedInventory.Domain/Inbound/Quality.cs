using System.Security.Cryptography;
using System.Text;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Operations;

namespace TrustedInventory.Domain.Inbound;

/// <summary>Why a bucket cannot be released (Problem.ineligibility codes, DEC-075).</summary>
public enum ReleaseIneligibility
{
    NotReleasable,
    MissingIdentifiers,
    Expired,
    InspectionNotPassed,
    StorageIncompatible,
    Recalled,
    OpenExcursion,
}

/// <summary>Contract QualityDispositionType: Damaged is a status change; SupplierReturn and Disposal leave the ledger.</summary>
public enum QualityDispositionType
{
    Damaged,
    SupplierReturn,
    Disposal,
}

/// <summary>Everything release eligibility looks at for one bucket (MED-001, MED-003).</summary>
public sealed record ReleaseFacts(
    QualityStatus Quality, HandlingProfile Profile, TrackingMode Tracking, bool HasLot, DateOnly? Expiry, bool HasSerial, DateOnly WarehouseToday,
    bool InspectionsPassed, bool StorageCompatible, bool Recalled, bool OpenExcursion);

/// <summary>Quality transitions (section 13, MED-001 to MED-003, EXC-007, DEC-075), free of persistence.</summary>
public static class QualityRules
{
    /// <summary>Protective holds apply to released or quarantined stock; Hold, Recalled and Damaged are already restricted.</summary>
    public static bool Holdable(QualityStatus q) => q is QualityStatus.Available or QualityStatus.Quarantine;

    /// <summary>The approval a release needs; null when the status cannot be released at all (Recalled never by ordinary release).</summary>
    public static ApprovalSubjectType? ReleaseSubject(QualityStatus q) => q switch
    {
        QualityStatus.Quarantine => ApprovalSubjectType.QualityRelease,
        QualityStatus.Hold => ApprovalSubjectType.HoldRelease,
        _ => null,
    };

    /// <summary>Every failed check; empty means releasable. No signer can override these (MED-001).</summary>
    public static IReadOnlyList<(ReleaseIneligibility Code, string Detail)> ReleaseProblems(ReleaseFacts f)
    {
        var problems = new List<(ReleaseIneligibility, string)>();
        if (ReleaseSubject(f.Quality) is null)
            problems.Add((ReleaseIneligibility.NotReleasable, f.Quality == QualityStatus.Recalled
                ? "Recalled stock cannot become available through ordinary release (EXC-007)."
                : $"{f.Quality} stock is not released."));
        var needsLot = f.Tracking is TrackingMode.Lot or TrackingMode.LotAndSerial || f.Profile == HandlingProfile.Medical;
        var needsSerial = f.Tracking is TrackingMode.Serial or TrackingMode.LotAndSerial;
        if ((needsLot && (!f.HasLot || f.Expiry is null && f.Profile == HandlingProfile.Medical)) || (needsSerial && !f.HasSerial))
            problems.Add((ReleaseIneligibility.MissingIdentifiers, "Lot, expiry or serial identifiers required by the product are missing."));
        if (!ReceivingRules.IsShippable(f.Expiry, f.WarehouseToday))
            problems.Add((ReleaseIneligibility.Expired, $"The lot expires on {f.Expiry:yyyy-MM-dd}; from that day it cannot be released (MED-003)."));
        if (!f.InspectionsPassed)
            problems.Add((ReleaseIneligibility.InspectionNotPassed, "A receiving inspection of this stock did not pass."));
        if (!f.StorageCompatible)
            problems.Add((ReleaseIneligibility.StorageIncompatible, "The bin's zone does not suit the product's storage profile."));
        if (f.Recalled)
            problems.Add((ReleaseIneligibility.Recalled, "An open recall covers this stock."));
        if (f.OpenExcursion)
            problems.Add((ReleaseIneligibility.OpenExcursion, "An unresolved temperature excursion covers this stock."));
        return problems;
    }

    /// <summary>Medical and recalled stock need an independent QA approval for any disposition (EXC-007, SOD-2).</summary>
    public static bool DispositionNeedsApproval(HandlingProfile profile, QualityStatus quality) =>
        profile == HandlingProfile.Medical || quality == QualityStatus.Recalled;

    /// <summary>Dispositions apply to restricted stock only; available stock is held first.</summary>
    public static bool Dispositionable(QualityStatus q) => q is QualityStatus.Quarantine or QualityStatus.Hold or QualityStatus.Recalled or QualityStatus.Damaged;

    /// <summary>The approval digest of a bucket: any change to what it holds or where makes an earlier approval stale (IAM-003).</summary>
    public static byte[] BucketDigest(Guid bucketId, QualityStatus quality, Guid binId, Guid productId, Guid? lotId, Guid? serialId, decimal onHand) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', bucketId.ToString("N"), quality, binId.ToString("N"), productId.ToString("N"),
            lotId?.ToString("N") ?? "-", serialId?.ToString("N") ?? "-", onHand.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture))));
}

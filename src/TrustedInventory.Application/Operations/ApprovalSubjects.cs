using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Operations;
using TrustedInventory.Domain.Organization;

namespace TrustedInventory.Application.Operations;

/// <summary>What an approval binds and who may sign it, for one subject at its current version (IAM-003, SOD-1, SOD-2).</summary>
internal sealed record SubjectState(
    ApprovalSubjectType Type, Guid Id, int Version, byte[] Digest, string CreatedBy, string LastModifiedBy, IReadOnlyList<string> SignPermissions,
    bool Open, ApprovalAction Action, string Name, IReadOnlyList<string>? OtherAuthors = null)
{
    /// <summary>Created, last modified or (for counts) counted the subject (SOD-1).</summary>
    public bool IsAuthor(string subject) => subject == CreatedBy || subject == LastModifiedBy || (OtherAuthors?.Contains(subject) ?? false);
}

/// <summary>
/// The approval subject registry (DEC-057, DEC-058): locks a subject and describes it for binding, signing and posting.
/// Lock order: subject row (and its lines) → approval row → stock → audit stream.
/// </summary>
internal static class ApprovalSubjects
{
    public static async Task<SubjectState> LockAsync(ITenantSession s, ApprovalSubjectType type, Guid id, CancellationToken ct) => type switch
    {
        ApprovalSubjectType.Adjustment => await AdjustmentAsync(s, await AdjustmentRules.LockAsync(s, id, ct), ct),
        ApprovalSubjectType.TransitDisposition => await TransferAsync(s, await TransferRules.LockAsync(s, id, ct), ct),
        ApprovalSubjectType.CountVariance => await CountAsync(s, await CountRules.LockAsync(s, id, ct), ct),
        ApprovalSubjectType.QualityRelease or ApprovalSubjectType.HoldRelease or ApprovalSubjectType.QualityDisposition => await BucketAsync(s, type, id, ct),
        ApprovalSubjectType.RecallClosure => await RecallAsync(s, id, ct),
        ApprovalSubjectType.ExcursionDisposition => await ExcursionAsync(s, id, ct),
        ApprovalSubjectType.ExpiryCorrection => await LotAsync(s, id, ct),
        ApprovalSubjectType.ReturnRelease => await ReturnAsync(s, id, ct),
        _ => throw AppException.Validation("subjectType", "This subject type is not supported yet; it arrives with its workflow."),
    };

    public static async Task<SubjectState> AdjustmentAsync(ITenantSession s, Adjustment a, CancellationToken ct) => new(
        ApprovalSubjectType.Adjustment, a.Id, a.Version, await AdjustmentRules.DigestAsync(s, a, ct), a.CreatedBy, a.LastModifiedBy,
        await AdjustmentRules.SignPermissionsAsync(s, a, ct), a.IsOpen, ApprovalAction.Post, "adjustment");

    public static async Task<SubjectState> TransferAsync(ITenantSession s, (Transfer Transfer, List<TransferLine> Lines) t, CancellationToken ct) => new(
        ApprovalSubjectType.TransitDisposition, t.Transfer.Id, t.Transfer.Version, TransferRules.Digest(t.Transfer, t.Lines), t.Transfer.CreatedBy,
        t.Transfer.LastModifiedBy, await TransferRules.SignPermissionsAsync(s, t.Lines, ct),
        t.Transfer.Status is TransferStatus.InTransit or TransferStatus.PartReceived && t.Lines.Any(l => l.InTransitQty > 0), ApprovalAction.Dispose,
        "transfer");

    public static async Task<SubjectState> CountAsync(ITenantSession s, (StockCount Count, List<StockCountLine> Lines) c, CancellationToken ct) => new(
        ApprovalSubjectType.CountVariance, c.Count.Id, c.Count.Version, CountRules.Digest(c.Count, c.Lines), c.Count.CreatedBy, c.Count.LastModifiedBy,
        await CountRules.SignPermissionsAsync(s, c.Lines, ct), c.Count.Status == CountStatus.Submitted, ApprovalAction.Post, "count",
        c.Lines.Where(l => l.CountedBy is not null).Select(l => l.CountedBy!).Distinct().ToList());

    /// <summary>
    /// A stock bucket as an approval subject (DEC-075): bound at its version and content. Authors are whoever created it and whoever
    /// last moved stock in or out, so the person who received, held or moved the goods cannot also sign their release (SOD-1). Only QA
    /// signs (SOD-2).
    /// </summary>
    public static async Task<SubjectState> BucketAsync(ITenantSession s, ApprovalSubjectType type, Guid id, CancellationToken ct)
    {
        var rows = await s.Db.Database.SqlQuery<BucketSubjectRow>($"""
            SELECT b.id AS "Id", b.version AS "Version", b.quality_status::text AS "Quality", b.bin_id AS "BinId", b.product_id AS "ProductId",
                   b.lot_id AS "LotId", b.serial_id AS "SerialId", b.on_hand_qty AS "OnHand", b.created_by AS "CreatedBy",
                   (SELECT m.actor_subject FROM ti.stock_movement m WHERE m.source_bucket_id = b.id OR m.destination_bucket_id = b.id
                    ORDER BY m.recorded_at DESC, m.id DESC LIMIT 1) AS "LastMovedBy"
            FROM ti.stock_bucket b WHERE b.id = {id} FOR UPDATE
            """).ToListAsync(ct);
        var b = rows.SingleOrDefault() ?? throw AppException.NotFound("Stock bucket");
        var quality = Enum.Parse<Domain.Inbound.QualityStatus>(b.Quality);
        var open = type switch
        {
            ApprovalSubjectType.QualityRelease => quality == Domain.Inbound.QualityStatus.Quarantine && b.OnHand > 0,
            ApprovalSubjectType.HoldRelease => quality == Domain.Inbound.QualityStatus.Hold && b.OnHand > 0,
            _ => Domain.Inbound.QualityRules.Dispositionable(quality) && b.OnHand > 0,
        };
        return new SubjectState(type, b.Id, b.Version,
            Domain.Inbound.QualityRules.BucketDigest(b.Id, quality, b.BinId, b.ProductId, b.LotId, b.SerialId, b.OnHand), b.CreatedBy,
            b.LastMovedBy ?? b.CreatedBy, [Domain.Security.Permissions.ApprovalsSignQuality], open,
            type == ApprovalSubjectType.QualityDisposition ? ApprovalAction.Dispose : ApprovalAction.Release, "stock bucket");
    }

    /// <summary>A recall as the subject of its closure (EXC-007, DEC-076): QA signs; the opener and last editor cannot.</summary>
    public static async Task<SubjectState> RecallAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        var recall = await Inbound.RecallService.LockAsync(s, id, ct);
        var outstanding = await Inbound.RecallService.OutstandingAsync(s, id, ct);
        return new SubjectState(ApprovalSubjectType.RecallClosure, recall.Id, recall.Version, Inbound.RecallService.Digest(recall, outstanding),
            recall.CreatedBy, recall.LastModifiedBy, [Domain.Security.Permissions.ApprovalsSignQuality], recall.Status == "Open", ApprovalAction.Close, "recall");
    }

    /// <summary>
    /// A lot as the subject of an expiry correction (section 07, DEC-090): QA signs; whoever received the lot or last corrected it
    /// cannot. The digest binds the lot and how each unit is held, so any stock change after the request makes the approval stale.
    /// Open whatever the stock: holding is checked when approval is requested and again when the correction is applied.
    /// </summary>
    public static async Task<SubjectState> LotAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        var lot = await Inbound.LotService.LockAsync(s, id, ct);
        var hold = await Inbound.LotService.HoldStateAsync(s, id, ct);
        var lastCorrector = await s.Db.Database.SqlQuery<string>(
            $"SELECT created_by AS \"Value\" FROM ti.lot_expiry_correction WHERE lot_id = {id} ORDER BY created_at DESC, id DESC LIMIT 1").ToListAsync(ct);
        return new SubjectState(ApprovalSubjectType.ExpiryCorrection, lot.Id, lot.Version, Inbound.LotService.Digest(lot, hold), lot.CreatedBy,
            lastCorrector.FirstOrDefault() ?? lot.CreatedBy, [Domain.Security.Permissions.ApprovalsSignQuality], true, ApprovalAction.Correct, "lot");
    }

    /// <summary>
    /// A customer return as the subject of releasing its medical goods (EXC-005, DEC-091): QA signs; whoever received the return cannot.
    /// The digest binds the return's state and its quarantined stock, so any inspection or movement after the request makes it stale.
    /// </summary>
    public static async Task<SubjectState> ReturnAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        if ((await s.Db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.customer_return WHERE id = {id} FOR UPDATE").ToListAsync(ct)).Count == 0)
            throw AppException.NotFound("Return");
        var ret = await s.Db.Set<CustomerReturn>().AsNoTracking().SingleAsync(r => r.Id == id, ct);
        var bucket = await s.Db.Set<Domain.Inbound.StockBucket>().AsNoTracking().SingleAsync(b => b.Id == ret.BucketId, ct);
        var text = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"return|{ret.Id:D}|{ret.Version}|{ret.ProductId:D}|{ret.Status}|{Quantity.Format(ret.ReturnedQty)}|{Quantity.Format(ret.InspectedQty)}|{ret.BucketId:D}|{bucket.QualityStatus}|{Quantity.Format(bucket.OnHandQty)}|{Quantity.Format(bucket.ReservedQty)}");
        return new SubjectState(ApprovalSubjectType.ReturnRelease, ret.Id, ret.Version,
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)), ret.CreatedBy, ret.CreatedBy,
            [Domain.Security.Permissions.ApprovalsSignQuality], ret.Status == ReturnStatus.Received, ApprovalAction.Release, "return");
    }

    /// <summary>An expiry correction may be requested only for held stock and a date that differs from the lot's (section 07, DEC-090).</summary>
    public static async Task EnsureProposalAsync(ITenantSession s, SubjectState subject, ApprovalProposal proposal, CancellationToken ct)
    {
        var lot = await s.Db.Set<Domain.Inbound.Lot>().AsNoTracking().SingleAsync(l => l.Id == subject.Id, ct);
        if (lot.ExpiryDate == proposal.ExpiryDate)
            throw AppException.Validation("proposal.expiryDate", "The lot already has this expiry date.");
        if (!(await Inbound.LotService.HoldStateAsync(s, subject.Id, ct)).AllHeld)
            throw AppException.InvalidState("Hold all stock of this lot first: nothing may be available, reserved or in transit (section 07).");
    }

    /// <summary>An excursion as the subject of its QA disposition (TEL-003, DEC-077): QA signs; whoever last changed it cannot.</summary>
    public static async Task<SubjectState> ExcursionAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        var row = await Inbound.ExcursionService.LockAsync(s, id, ct);
        return new SubjectState(ApprovalSubjectType.ExcursionDisposition, row.Id, row.Version, await Inbound.ExcursionService.DigestAsync(s, row, ct),
            row.CreatedBy, row.LastModifiedBy, [Domain.Security.Permissions.ApprovalsSignQuality], row.Status != "Resolved", ApprovalAction.Dispose,
            "excursion");
    }

    private sealed record BucketSubjectRow(Guid Id, int Version, string Quality, Guid BinId, Guid ProductId, Guid? LotId, Guid? SerialId, decimal OnHand,
        string CreatedBy, string? LastMovedBy);

    /// <summary>A rejected approval closes an adjustment; a transfer carries on (DEC-057, DEC-058).</summary>
    public static async Task OnRejectedAsync(ITenantSession s, SubjectState subject, CancellationToken ct)
    {
        if (subject.Type != ApprovalSubjectType.Adjustment)
            return;
        var adjustment = await s.Db.Set<Adjustment>().SingleAsync(a => a.Id == subject.Id, ct);
        if (adjustment.IsOpen)
            DomainGuard.Run(() => adjustment.Reject(s.Now));
    }

    /// <summary>A changed or withdrawn subject voids its live approvals (IAM-003).</summary>
    public static async Task<List<Guid>> InvalidateLiveAsync(ITenantSession s, ApprovalSubjectType type, Guid id, string reason, CancellationToken ct)
    {
        var live = await s.Db.Set<Approval>()
            .Where(x => x.SubjectType == type && x.SubjectId == id && (x.Status == ApprovalStatus.Pending || x.Status == ApprovalStatus.Approved))
            .ToListAsync(ct);
        // Tracked rows may already have moved on in this transaction (a disposition consumes its own approval first).
        live = live.Where(x => x.Status is ApprovalStatus.Pending or ApprovalStatus.Approved).ToList();
        foreach (var approval in live)
            DomainGuard.Run(() => approval.Invalidate(reason, s.Now));
        return live.Select(x => x.Id).ToList();
    }

    /// <summary>The live signed approval binding this exact version and content, if any (for reads).</summary>
    public static async Task<Approval?> BindingAsync(ITenantSession s, SubjectState subject, CancellationToken ct)
    {
        var now = s.Now;
        var signed = await s.Db.Set<Approval>().AsNoTracking()
            .Where(x => x.SubjectType == subject.Type && x.SubjectId == subject.Id && x.Status == ApprovalStatus.Approved
                && x.SubjectVersion == subject.Version && x.ExpiresAt > now)
            .ToListAsync(ct);
        return signed.FirstOrDefault(x => x.ContentDigest.AsSpan().SequenceEqual(subject.Digest));
    }

    /// <summary>
    /// Posting rechecks (IAM-003, SOD-1, SOD-4): the approval is for this subject, signed, unexpired, binds the current
    /// version and digest, its signer wrote none of it and still holds an active membership with the sign permissions.
    /// </summary>
    public static async Task<Approval> RequireBindingAsync(ITenantSession s, SubjectState subject, Guid approvalId, CancellationToken ct,
        bool checkVersion = true)
    {
        await s.Db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.approval WHERE id = {approvalId} FOR UPDATE").ToListAsync(ct);
        var approval = await s.Db.Set<Approval>().SingleOrDefaultAsync(a => a.Id == approvalId, ct);
        if (approval is null || approval.SubjectType != subject.Type || approval.SubjectId != subject.Id)
            throw AppException.Validation("approvalId", $"The approval is not for this {subject.Name}.");
        if (approval.Status != ApprovalStatus.Approved)
            throw Stale($"The approval is {approval.EffectiveStatus(s.Now)}, not signed.");
        if (s.Now >= approval.ExpiresAt)
            throw Stale("The approval expired 30 minutes after signing; request a new one.");
        if ((checkVersion && approval.SubjectVersion != subject.Version) || !approval.ContentDigest.AsSpan().SequenceEqual(subject.Digest))
            throw Stale($"The {subject.Name} changed after it was approved (IAM-003).");
        if (subject.IsAuthor(approval.ApproverSubject!))
            throw new AppException(ErrorCode.ApprovalSelf, $"The approver also wrote this {subject.Name} (SOD-1).");
        if (!await ApproverStillEntitledAsync(s, approval.ApproverSubject!, subject.SignPermissions, ct))
            throw Stale("The approver is no longer an active member allowed to sign (SOD-4).");
        return approval;
    }

    /// <summary>SOD-4: the signer still holds an active membership with the signing permissions.</summary>
    public static async Task<bool> ApproverStillEntitledAsync(ITenantSession s, string approver, IReadOnlyList<string> permissions, CancellationToken ct)
    {
        var membership = await s.Db.Set<Membership>().AsNoTracking().Where(m => m.UserSubject == approver && m.Active).Select(m => (Guid?)m.Id)
            .FirstOrDefaultAsync(ct);
        if (membership is null)
            return false;
        var roles = await s.Db.Set<RoleGrant>().AsNoTracking().Where(g => g.MembershipId == membership && g.RevokedAt == null)
            .Select(g => new { g.Role, g.WarehouseId }).ToListAsync(ct);
        var grants = CallerContext.GrantsFrom(roles.Select(r => (r.Role, r.WarehouseId)));
        return permissions.All(grants.ContainsKey);
    }

    private static AppException Stale(string detail) => new(ErrorCode.ApprovalStale, detail);
}

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

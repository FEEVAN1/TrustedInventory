using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Administration;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Operations;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Operations;

public sealed record ApprovalDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, ApprovalSubjectType SubjectType, Guid SubjectId,
    int SubjectVersion, ApprovalAction Action, string Reason, string ContentDigest, ApprovalStatus Status, string RequesterSubject,
    string? ApproverSubject, SignatureMeaning? Meaning, string? SignatureReason, DateTimeOffset? ReauthVerifiedAt, DateTimeOffset? SignedAt,
    DateTimeOffset? ExpiresAt, DateTimeOffset? ConsumedAt, string? InvalidatedReason) : IVersioned;

public sealed record CreateApproval(ApprovalSubjectType SubjectType, Guid SubjectId, int SubjectVersion, ApprovalAction Action, string Reason);

public sealed record SignApproval(string ReauthToken, SignatureMeaning Meaning, string Reason);

public sealed record ApprovalFilter(ApprovalStatus? Status, ApprovalSubjectType? SubjectType, Guid? SubjectId, bool AwaitingMySignature);

public sealed class CreateApprovalValidator : AbstractValidator<CreateApproval>
{
    public CreateApprovalValidator()
    {
        RuleFor(x => x.SubjectId).NotEmpty();
        RuleFor(x => x.SubjectVersion).GreaterThan(0);
        RuleFor(x => x.Reason).ValidReason();
    }
}

public sealed class SignApprovalValidator : AbstractValidator<SignApproval>
{
    public SignApprovalValidator()
    {
        RuleFor(x => x.ReauthToken).NotEmpty().Length(20, 8192);
        RuleFor(x => x.Reason).ValidReason();
        RuleFor(x => x.Meaning).Must(m => m != SignatureMeaning.Rejection).WithMessage("Use the reject operation to reject.");
    }
}

/// <summary>
/// Approvals (IAM-003, IAM-004, SOD-1, SOD-2, DEC-009, DEC-057). An approval binds one subject version and content
/// digest; signing needs fresh re-authentication and a signer independent of the subject's authors. Lock order:
/// subject row → approval row → audit stream. Signing never writes the subject (that would change its version).
/// </summary>
public sealed class ApprovalService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IReauthVerifier reauth,
    IValidator<CreateApproval> createValidator,
    IValidator<SignApproval> signValidator,
    IValidator<ReasonRequest> reasonValidator)
{
    public Task<ApprovalDto> CreateAsync(CreateApproval cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.ApprovalsRequest);
        caller.RequireMutable();
        createValidator.ValidateOrThrow(cmd);
        if (cmd.SubjectType != ApprovalSubjectType.Adjustment)
            throw AppException.Validation("subjectType", "Only adjustments can be approved so far; other subjects arrive with their workflows.");
        if (cmd.Action != ApprovalAction.Post)
            throw AppException.Validation("action", "An adjustment approval authorises posting.");
        return uow.WriteAsync(async (s, c) =>
        {
            var adjustment = await AdjustmentRules.LockAsync(s, cmd.SubjectId, c);
            if (!adjustment.IsOpen)
                throw AppException.InvalidState("Only a requested adjustment can be approved.");
            if (adjustment.Version != cmd.SubjectVersion)
                throw new AppException(ErrorCode.ApprovalStale, $"The adjustment is at version {adjustment.Version}; request approval of the current version.");
            var existing = await s.Db.Set<Approval>()
                .Where(x => x.SubjectType == ApprovalSubjectType.Adjustment && x.SubjectId == adjustment.Id
                    && (x.Status == ApprovalStatus.Pending || x.Status == ApprovalStatus.Approved))
                .ToListAsync(c);
            if (existing.Any(x => x.IsLive(s.Now)))
                throw AppException.InvalidState("This adjustment already has a pending or signed approval.");
            foreach (var lapsed in existing)
                DomainGuard.Run(() => lapsed.Expire(s.Now));

            var digest = await AdjustmentRules.DigestAsync(s, adjustment, c);
            var approval = DomainGuard.Run(() => Approval.Request(caller.TenantId, cmd.SubjectType, adjustment.Id, adjustment.Version, digest, cmd.Action,
                cmd.Reason, caller.Subject, s.Now));
            s.Db.Add(approval);
            var dto = Dto(approval, s.Now);
            s.Audit(new AuditEntry("ApprovalRequested", "Approval", approval.Id, approval.Version, cmd.Reason, null, dto));
            s.Publish("ApprovalRequested", new { approvalId = approval.Id, subjectType = cmd.SubjectType.ToString(), subjectId = adjustment.Id });
            await s.Db.SaveChangesAsync(c);
            return dto;
        }, ct);
    }

    /// <summary>Signs with fresh re-authentication (IAM-004, DEC-009); the signer is independent of the subject (SOD-1, SOD-2).</summary>
    public async Task<ApprovalDto> SignAsync(Guid id, int expectedVersion, SignApproval cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        if (!caller.Has(Permissions.ApprovalsSignInventory) && !caller.Has(Permissions.ApprovalsSignQuality))
            throw AppException.Forbidden();
        caller.RequireMutable();
        signValidator.ValidateOrThrow(cmd);
        var reauthDigest = await reauth.VerifyAsync(cmd.ReauthToken, caller.Subject, ct); // 403 REAUTH_REQUIRED before anything else
        return await uow.WriteAsync(async (s, c) =>
        {
            var (approval, adjustment) = await LockAsync(s, id, c);
            Versioning.EnsureVersion(approval.Version, expectedVersion);
            await EnsureMaySignAsync(s, caller, approval, adjustment, c);
            if (approval.EffectiveStatus(s.Now) != ApprovalStatus.Pending)
                throw AppException.InvalidState($"The approval is {approval.EffectiveStatus(s.Now)}; only a pending approval can be signed.");
            var current = await AdjustmentRules.DigestAsync(s, adjustment, c);
            if (!adjustment.IsOpen || adjustment.Version != approval.SubjectVersion || !approval.ContentDigest.AsSpan().SequenceEqual(current))
                throw new AppException(ErrorCode.ApprovalStale, "The adjustment changed after approval was requested (IAM-003).");
            var before = Dto(approval, s.Now);
            DomainGuard.Run(() => approval.Sign(caller.Subject, cmd.Meaning, cmd.Reason, reauthDigest, s.Now, s.Now));
            var after = Dto(approval, s.Now);
            s.Audit(new AuditEntry("ApprovalSigned", "Approval", approval.Id, approval.Version, cmd.Reason, before, after));
            s.Publish("ApprovalSigned", new { approvalId = approval.Id, subjectId = approval.SubjectId });
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    public Task<ApprovalDto> RejectAsync(Guid id, int expectedVersion, ReasonRequest cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        if (!caller.Has(Permissions.ApprovalsSignInventory) && !caller.Has(Permissions.ApprovalsSignQuality))
            throw AppException.Forbidden();
        caller.RequireMutable();
        reasonValidator.ValidateOrThrow(cmd);
        return uow.WriteAsync(async (s, c) =>
        {
            var (approval, adjustment) = await LockAsync(s, id, c);
            Versioning.EnsureVersion(approval.Version, expectedVersion);
            await EnsureMaySignAsync(s, caller, approval, adjustment, c);
            if (approval.EffectiveStatus(s.Now) != ApprovalStatus.Pending)
                throw AppException.InvalidState($"The approval is {approval.EffectiveStatus(s.Now)}; only a pending approval can be rejected.");
            var before = Dto(approval, s.Now);
            DomainGuard.Run(() => approval.Reject(caller.Subject, cmd.Reason, s.Now));
            if (adjustment.IsOpen)
                DomainGuard.Run(() => adjustment.Reject(s.Now));
            var after = Dto(approval, s.Now);
            s.Audit(new AuditEntry("ApprovalRejected", "Approval", approval.Id, approval.Version, cmd.Reason, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    public Task<ApprovalDto> GetAsync(Guid id, CancellationToken ct)
    {
        RequireInbox(callers.Caller);
        return uow.ReadAsync(async (s, c) =>
            Dto(await s.Db.Set<Approval>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, c) ?? throw AppException.NotFound("Approval"), s.Now), ct);
    }

    public Task<Page<ApprovalDto>> ListAsync(ApprovalFilter filter, PageRequest page, CancellationToken ct)
    {
        var caller = callers.Caller;
        RequireInbox(caller);
        return uow.ReadAsync(async (s, c) =>
        {
            var now = s.Now;
            var query = s.Db.Set<Approval>().AsNoTracking();
            query = filter.Status switch
            {
                ApprovalStatus.Approved => query.Where(a => a.Status == ApprovalStatus.Approved && a.ExpiresAt > now),
                ApprovalStatus.Expired => query.Where(a => a.Status == ApprovalStatus.Expired || (a.Status == ApprovalStatus.Approved && a.ExpiresAt <= now)),
                { } status => query.Where(a => a.Status == status),
                null => query,
            };
            if (filter.SubjectType is { } type)
                query = query.Where(a => a.SubjectType == type);
            if (filter.SubjectId is { } subject)
                query = query.Where(a => a.SubjectId == subject);
            if (filter.AwaitingMySignature)
            {
                var canSign = caller.Has(Permissions.ApprovalsSignInventory) || caller.Has(Permissions.ApprovalsSignQuality);
                var me = caller.Subject;
                query = canSign ? query.Where(a => a.Status == ApprovalStatus.Pending && a.RequesterSubject != me) : query.Where(_ => false);
            }
            var rows = await query.ToPageAsync(page, $"approvals|{filter.Status}|{filter.SubjectType}|{filter.SubjectId}|{filter.AwaitingMySignature}",
                cursors, a => Dto(a, now), c);
            return rows;
        }, ct);
    }

    private static void RequireInbox(CallerContext caller)
    {
        if (!caller.Has(Permissions.ApprovalsRequest) && !caller.Has(Permissions.ApprovalsSignInventory) && !caller.Has(Permissions.ApprovalsSignQuality))
            throw AppException.Forbidden();
    }

    private static async Task<(Approval Approval, Domain.Operations.Adjustment Adjustment)> LockAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        var subject = await s.Db.Set<Approval>().AsNoTracking().Where(a => a.Id == id).Select(a => new { a.SubjectType, a.SubjectId }).SingleOrDefaultAsync(ct)
            ?? throw AppException.NotFound("Approval");
        if (subject.SubjectType != ApprovalSubjectType.Adjustment)
            throw AppException.InvalidState("This approval's subject type is not supported yet.");
        var adjustment = await AdjustmentRules.LockAsync(s, subject.SubjectId, ct);
        await s.Db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.approval WHERE id = {id} FOR UPDATE").ToListAsync(ct);
        var approval = await s.Db.Set<Approval>().SingleAsync(a => a.Id == id, ct);
        return (approval, adjustment);
    }

    /// <summary>SOD-1: not the requester, creator or last modifier; SOD-2: the subject's sign permissions.</summary>
    private static async Task EnsureMaySignAsync(ITenantSession s, CallerContext caller, Approval approval, Domain.Operations.Adjustment adjustment,
        CancellationToken ct)
    {
        foreach (var permission in await AdjustmentRules.SignPermissionsAsync(s, adjustment, ct))
            if (!caller.Has(permission))
                throw AppException.Forbidden(permission == Permissions.ApprovalsSignQuality
                    ? "Medical adjustments also need quality signing authority (SOD-2)."
                    : "You may not sign inventory approvals.");
        if (caller.Subject == approval.RequesterSubject || caller.Subject == adjustment.CreatedBy || caller.Subject == adjustment.LastModifiedBy)
            throw new AppException(ErrorCode.ApprovalSelf, "You requested or wrote this adjustment, so someone else must sign it (SOD-1).");
    }

    internal static ApprovalDto Dto(Approval a, DateTimeOffset now) => new(
        a.Id, a.Version, a.CreatedAt, a.CreatedBy, a.UpdatedAt, a.SubjectType, a.SubjectId, a.SubjectVersion, a.Action, a.RequestReason,
        Convert.ToHexStringLower(a.ContentDigest), a.EffectiveStatus(now), a.RequesterSubject, a.ApproverSubject, a.Meaning, a.SignReason,
        a.ReauthVerifiedAt, a.SignedAt, a.ExpiresAt, a.ConsumedAt, a.InvalidatedReason);
}

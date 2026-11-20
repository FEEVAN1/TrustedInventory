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
    DateTimeOffset? ExpiresAt, DateTimeOffset? ConsumedAt, string? InvalidatedReason, ApprovalProposal? Proposal) : IVersioned;

/// <summary>The change a signer approves (DEC-090): an expiry correction names its new date in the approval.</summary>
public sealed record ApprovalProposal(DateOnly ExpiryDate)
{
    /// <summary>Canonical JSON kept with the approval; the database guard keeps it unchangeable.</summary>
    public string ToJson() => $"{{\"expiryDate\":\"{ExpiryDate:yyyy-MM-dd}\"}}";

    public static ApprovalProposal? FromJson(string? json) =>
        json is null ? null : new(DateOnly.Parse(System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("expiryDate").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture));
}

public sealed record CreateApproval(ApprovalSubjectType SubjectType, Guid SubjectId, int SubjectVersion, ApprovalAction Action, string Reason,
    ApprovalProposal? Proposal = null);

public sealed record SignApproval(string ReauthToken, SignatureMeaning Meaning, string Reason);

public sealed record ApprovalFilter(ApprovalStatus? Status, ApprovalSubjectType? SubjectType, Guid? SubjectId, bool AwaitingMySignature);

public sealed class CreateApprovalValidator : AbstractValidator<CreateApproval>
{
    public CreateApprovalValidator()
    {
        RuleFor(x => x.SubjectId).NotEmpty();
        RuleFor(x => x.SubjectVersion).GreaterThan(0);
        RuleFor(x => x.Reason).ValidReason();
        RuleFor(x => x.Proposal).NotNull().When(x => x.SubjectType == ApprovalSubjectType.ExpiryCorrection)
            .WithMessage("An expiry correction names the proposed expiry date.");
        RuleFor(x => x.Proposal).Null().When(x => x.SubjectType != ApprovalSubjectType.ExpiryCorrection)
            .WithMessage("Only an expiry correction carries a proposal.");
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
        return uow.WriteAsync(async (s, c) =>
        {
            var subject = await ApprovalSubjects.LockAsync(s, cmd.SubjectType, cmd.SubjectId, c);
            if (cmd.Action != subject.Action)
                throw AppException.Validation("action", $"A {subject.Name} approval authorises {subject.Action}.");
            if (!subject.Open)
                throw AppException.InvalidState($"This {subject.Name} has nothing awaiting approval.");
            if (subject.Version != cmd.SubjectVersion)
                throw new AppException(ErrorCode.ApprovalStale, $"The {subject.Name} is at version {subject.Version}; request approval of the current version.");
            var existing = await s.Db.Set<Approval>()
                .Where(x => x.SubjectType == subject.Type && x.SubjectId == subject.Id
                    && (x.Status == ApprovalStatus.Pending || x.Status == ApprovalStatus.Approved))
                .ToListAsync(c);
            if (existing.Any(x => x.IsLive(s.Now)))
                throw AppException.InvalidState($"This {subject.Name} already has a pending or signed approval.");
            foreach (var lapsed in existing)
                DomainGuard.Run(() => lapsed.Expire(s.Now));

            if (cmd.Proposal is { } proposal && subject.Type == ApprovalSubjectType.ExpiryCorrection)
                await ApprovalSubjects.EnsureProposalAsync(s, subject, proposal, c);
            var approval = DomainGuard.Run(() => Approval.Request(caller.TenantId, subject.Type, subject.Id, subject.Version, subject.Digest, cmd.Action,
                cmd.Reason, caller.Subject, s.Now, cmd.Proposal?.ToJson()));
            s.Db.Add(approval);
            var dto = Dto(approval, s.Now);
            s.Audit(new AuditEntry("ApprovalRequested", "Approval", approval.Id, approval.Version, cmd.Reason, null, dto));
            s.Publish("ApprovalRequested", new { approvalId = approval.Id, subjectType = subject.Type.ToString(), subjectId = subject.Id });
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
            var (approval, subject) = await LockAsync(s, id, c);
            Versioning.EnsureVersion(approval.Version, expectedVersion);
            EnsureMaySign(caller, approval, subject);
            if (approval.EffectiveStatus(s.Now) != ApprovalStatus.Pending)
                throw AppException.InvalidState($"The approval is {approval.EffectiveStatus(s.Now)}; only a pending approval can be signed.");
            if (!subject.Open || subject.Version != approval.SubjectVersion || !approval.ContentDigest.AsSpan().SequenceEqual(subject.Digest))
                throw new AppException(ErrorCode.ApprovalStale, $"The {subject.Name} changed after approval was requested (IAM-003).");
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
            var (approval, subject) = await LockAsync(s, id, c);
            Versioning.EnsureVersion(approval.Version, expectedVersion);
            EnsureMaySign(caller, approval, subject);
            if (approval.EffectiveStatus(s.Now) != ApprovalStatus.Pending)
                throw AppException.InvalidState($"The approval is {approval.EffectiveStatus(s.Now)}; only a pending approval can be rejected.");
            var before = Dto(approval, s.Now);
            DomainGuard.Run(() => approval.Reject(caller.Subject, cmd.Reason, s.Now));
            await ApprovalSubjects.OnRejectedAsync(s, subject, c);
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

    private static async Task<(Approval Approval, SubjectState Subject)> LockAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        var target = await s.Db.Set<Approval>().AsNoTracking().Where(a => a.Id == id).Select(a => new { a.SubjectType, a.SubjectId }).SingleOrDefaultAsync(ct)
            ?? throw AppException.NotFound("Approval");
        var subject = await ApprovalSubjects.LockAsync(s, target.SubjectType, target.SubjectId, ct);
        await s.Db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.approval WHERE id = {id} FOR UPDATE").ToListAsync(ct);
        var approval = await s.Db.Set<Approval>().SingleAsync(a => a.Id == id, ct);
        return (approval, subject);
    }

    /// <summary>SOD-2: the subject's sign permissions; SOD-1: not the requester, creator or last modifier.</summary>
    private static void EnsureMaySign(CallerContext caller, Approval approval, SubjectState subject)
    {
        foreach (var permission in subject.SignPermissions)
            if (!caller.Has(permission))
                throw AppException.Forbidden(permission == Permissions.ApprovalsSignQuality
                    ? "Medical stock also needs quality signing authority (SOD-2)."
                    : "You may not sign inventory approvals.");
        if (caller.Subject == approval.RequesterSubject || subject.IsAuthor(caller.Subject))
            throw new AppException(ErrorCode.ApprovalSelf, $"You requested or wrote this {subject.Name}, so someone else must sign it (SOD-1).");
    }

    internal static ApprovalDto Dto(Approval a, DateTimeOffset now) => new(
        a.Id, a.Version, a.CreatedAt, a.CreatedBy, a.UpdatedAt, a.SubjectType, a.SubjectId, a.SubjectVersion, a.Action, a.RequestReason,
        Convert.ToHexStringLower(a.ContentDigest), a.EffectiveStatus(now), a.RequesterSubject, a.ApproverSubject, a.Meaning, a.SignReason,
        a.ReauthVerifiedAt, a.SignedAt, a.ExpiresAt, a.ConsumedAt, a.InvalidatedReason, ApprovalProposal.FromJson(a.Proposal));
}

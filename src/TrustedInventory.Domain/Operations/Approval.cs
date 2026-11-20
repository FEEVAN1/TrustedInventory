using TrustedInventory.Domain.Common;

namespace TrustedInventory.Domain.Operations;

/// <summary>ti.approval_subject_type (docs/enums.md).</summary>
public enum ApprovalSubjectType
{
    Adjustment, CountVariance, QualityRelease, HoldRelease, ReturnRelease, RecallClosure, TransitDisposition, ExpiryCorrection, ExcursionDisposition,
    QualityDisposition,
}

/// <summary>ti.approval_action: what the consuming command does.</summary>
public enum ApprovalAction
{
    Post, Release, Close, Correct, Dispose,
}

public enum ApprovalStatus
{
    Pending, Approved, Rejected, Expired, Invalidated, Consumed,
}

public enum SignatureMeaning
{
    Approval, Rejection, QualityRelease, Disposition,
}

/// <summary>
/// An approval bound to one subject version and content digest (IAM-003, DEC-056, DEC-057). Signed approvals last
/// 30 minutes and allow exactly one posting.
/// </summary>
public sealed class Approval : TenantEntity
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private Approval() { }

    public ApprovalSubjectType SubjectType { get; private set; }
    public Guid SubjectId { get; private set; }
    public int SubjectVersion { get; private set; }
    public byte[] ContentDigest { get; private set; } = [];
    public ApprovalAction Action { get; private set; }
    public string RequestReason { get; private set; } = "";
    public string RequesterSubject { get; private set; } = "";
    public ApprovalStatus Status { get; private set; }
    public string? ApproverSubject { get; private set; }
    public SignatureMeaning? Meaning { get; private set; }
    public string? SignReason { get; private set; }
    public byte[]? ReauthReference { get; private set; }
    public DateTimeOffset? ReauthVerifiedAt { get; private set; }
    public DateTimeOffset? SignedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }
    public string? ConsumedOperation { get; private set; }
    public string? InvalidatedReason { get; private set; }

    /// <summary>The change the signer approves, as canonical JSON (DEC-090); only ExpiryCorrection carries one.</summary>
    public string? Proposal { get; private set; }

    public static Approval Request(Guid tenantId, ApprovalSubjectType subjectType, Guid subjectId, int subjectVersion, byte[] digest,
        ApprovalAction action, string reason, string requester, DateTimeOffset now, string? proposal = null)
    {
        if (digest.Length != 32)
            throw new DomainException("A content digest is a SHA-256 value.");
        if ((subjectType == ApprovalSubjectType.ExpiryCorrection) != (proposal is not null))
            throw new DomainException("An expiry correction names its proposed date, and only an expiry correction carries a proposal.");
        var approval = new Approval
        {
            SubjectType = subjectType, SubjectId = subjectId, SubjectVersion = subjectVersion, ContentDigest = digest, Action = action,
            RequestReason = reason, RequesterSubject = requester, Status = ApprovalStatus.Pending, Proposal = proposal,
        };
        approval.Stamp(tenantId, requester, now);
        return approval;
    }

    /// <summary>Pending, or Approved and not yet expired.</summary>
    public bool IsLive(DateTimeOffset now) => Status == ApprovalStatus.Pending || (Status == ApprovalStatus.Approved && now < ExpiresAt);

    /// <summary>The status a reader sees: an Approved approval past its expiry reads as Expired.</summary>
    public ApprovalStatus EffectiveStatus(DateTimeOffset now) =>
        Status == ApprovalStatus.Approved && now >= ExpiresAt ? ApprovalStatus.Expired : Status;

    public void Sign(string approver, SignatureMeaning meaning, string reason, byte[] reauthDigest, DateTimeOffset reauthVerifiedAt, DateTimeOffset now)
    {
        if (Status != ApprovalStatus.Pending)
            throw new DomainException("Only a pending approval can be signed.");
        if (approver == RequesterSubject)
            throw new DomainException("The requester cannot sign their own approval.");
        if (meaning == SignatureMeaning.Rejection)
            throw new DomainException("Use rejection to reject an approval.");
        Status = ApprovalStatus.Approved;
        ApproverSubject = approver;
        Meaning = meaning;
        SignReason = reason;
        ReauthReference = reauthDigest;
        ReauthVerifiedAt = reauthVerifiedAt;
        SignedAt = now;
        ExpiresAt = now + Lifetime;
        Touch(now);
    }

    public void Reject(string approver, string reason, DateTimeOffset now)
    {
        if (Status != ApprovalStatus.Pending)
            throw new DomainException("Only a pending approval can be rejected.");
        if (approver == RequesterSubject)
            throw new DomainException("The requester cannot reject their own approval.");
        Status = ApprovalStatus.Rejected;
        ApproverSubject = approver;
        Meaning = SignatureMeaning.Rejection;
        SignReason = reason;
        Touch(now);
    }

    /// <summary>The subject changed or was withdrawn (IAM-003).</summary>
    public void Invalidate(string reason, DateTimeOffset now)
    {
        if (Status is not (ApprovalStatus.Pending or ApprovalStatus.Approved))
            throw new DomainException("Only a pending or approved approval can be invalidated.");
        Status = ApprovalStatus.Invalidated;
        InvalidatedReason = reason;
        Touch(now);
    }

    /// <summary>Records the lapse of a signed approval once it is past its expiry.</summary>
    public void Expire(DateTimeOffset now)
    {
        if (Status != ApprovalStatus.Approved || now < ExpiresAt)
            throw new DomainException("Only a signed approval past its expiry can expire.");
        Status = ApprovalStatus.Expired;
        Touch(now);
    }

    /// <summary>One approval, one posting: the operation name is unique per tenant.</summary>
    public void Consume(string operation, DateTimeOffset now)
    {
        if (Status != ApprovalStatus.Approved || now >= ExpiresAt)
            throw new DomainException("Only a signed, unexpired approval can be used.");
        Status = ApprovalStatus.Consumed;
        ConsumedAt = now;
        ConsumedOperation = operation;
        Touch(now);
    }
}

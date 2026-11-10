using TrustedInventory.Domain.Common;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Operations;

namespace TrustedInventory.UnitTests;

/// <summary>Approval lifecycle and adjustment rules (IAM-003, SOD-1, EXC-004, DEC-057).</summary>
public class ApprovalRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly byte[] Digest = new byte[32];

    private static Approval Pending() =>
        Approval.Request(Tenant, ApprovalSubjectType.Adjustment, Guid.NewGuid(), 1, Digest, ApprovalAction.Post, "please", "requester", Now);

    [Fact]
    [Trait("Requirement", "SOD-1")]
    [Trait("Requirement", "IAM-003")]
    public void A_signature_comes_from_someone_else_lasts_30_minutes_and_is_used_once()
    {
        var approval = Pending();
        Assert.Throws<DomainException>(() => approval.Sign("requester", SignatureMeaning.Approval, "ok", Digest, Now, Now));
        approval.Sign("signer", SignatureMeaning.Approval, "ok", Digest, Now, Now);
        Assert.Equal(Now.AddMinutes(30), approval.ExpiresAt);
        Assert.True(approval.IsLive(Now.AddMinutes(29)));
        Assert.Equal(ApprovalStatus.Expired, approval.EffectiveStatus(Now.AddMinutes(30)));
        Assert.Throws<DomainException>(() => approval.Consume("op", Now.AddMinutes(30)));
        approval.Consume("op", Now.AddMinutes(10));
        Assert.Throws<DomainException>(() => approval.Consume("op-2", Now.AddMinutes(11)));
        Assert.Throws<DomainException>(() => approval.Invalidate("changed", Now));
    }

    [Fact]
    [Trait("Requirement", "IAM-003")]
    public void Only_pending_approvals_are_signed_or_rejected_and_rejection_is_not_a_signature()
    {
        var rejected = Pending();
        Assert.Throws<DomainException>(() => rejected.Sign("signer", SignatureMeaning.Rejection, "no", Digest, Now, Now));
        rejected.Reject("signer", "missing evidence", Now);
        Assert.Equal((ApprovalStatus.Rejected, SignatureMeaning.Rejection, (DateTimeOffset?)null), (rejected.Status, rejected.Meaning, rejected.SignedAt));
        Assert.Throws<DomainException>(() => rejected.Sign("signer", SignatureMeaning.Approval, "ok", Digest, Now, Now));

        var invalidated = Pending();
        invalidated.Invalidate("subject changed", Now);
        Assert.False(invalidated.IsLive(Now));
    }

    [Fact]
    [Trait("Requirement", "EXC-004")]
    public void Adjustments_reduce_only_existing_stock_and_post_once()
    {
        Assert.Throws<DomainException>(() => Adjustment.Request(Tenant, Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), null, null,
            QualityStatus.Available, -1, "LOSS", "x", null, "requester", Now));
        var adjustment = Adjustment.Request(Tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null,
            QualityStatus.Available, -2, "LOSS", "x", null, "requester", Now);
        adjustment.Edit(-3, null, null, "editor", Now);
        Assert.Equal((2, "editor", -3m), (adjustment.Version, adjustment.LastModifiedBy, adjustment.DeltaQty));
        adjustment.Post(Guid.NewGuid(), adjustment.BucketId!.Value, Guid.NewGuid(), Now);
        Assert.Throws<DomainException>(() => adjustment.Post(Guid.NewGuid(), adjustment.BucketId!.Value, Guid.NewGuid(), Now));
        Assert.Throws<DomainException>(() => adjustment.Edit(-1, null, null, "editor", Now));
    }
}

using TrustedInventory.Domain.Common;

namespace TrustedInventory.Domain.Organization;

public enum OwnershipTransferStatus
{
    Pending,
    Accepted,
    Cancelled,
    Expired,
}

/// <summary>
/// Moves tenant ownership to another active member (ONB-006). The current owner starts it after recent
/// re-authentication (SOD-7); it takes effect only when the recipient accepts. At most one is pending.
/// </summary>
public sealed class OwnershipTransfer : TenantEntity
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);

    private OwnershipTransfer() { }

    public string FromSubject { get; private set; } = "";
    public Guid ToMembershipId { get; private set; }
    public string ToSubject { get; private set; } = "";
    public OwnershipTransferStatus Status { get; private set; }
    public byte[] ReauthReference { get; private set; } = [];
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }

    public static OwnershipTransfer Start(Guid tenantId, string fromSubject, Membership recipient, byte[] reauthReference,
        DateTimeOffset now)
    {
        if (!recipient.Active)
            throw new DomainException("Ownership can only be transferred to an active member.");
        if (recipient.UserSubject == fromSubject)
            throw new DomainException("You already own this workspace.");
        var transfer = new OwnershipTransfer
        {
            FromSubject = fromSubject,
            ToMembershipId = recipient.Id,
            ToSubject = recipient.UserSubject,
            Status = OwnershipTransferStatus.Pending,
            ReauthReference = reauthReference,
            ExpiresAt = now + Lifetime,
        };
        transfer.Stamp(tenantId, fromSubject, now);
        return transfer;
    }

    public OwnershipTransferStatus EffectiveStatus(DateTimeOffset now) =>
        Status == OwnershipTransferStatus.Pending && now >= ExpiresAt ? OwnershipTransferStatus.Expired : Status;

    public void Accept(string subject, DateTimeOffset now)
    {
        if (subject != ToSubject)
            throw new DomainException("Only the named recipient can accept this transfer.");
        if (EffectiveStatus(now) != OwnershipTransferStatus.Pending)
            throw new DomainException("The transfer is no longer pending.");
        Status = OwnershipTransferStatus.Accepted;
        AcceptedAt = now;
        Touch(now);
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status != OwnershipTransferStatus.Pending)
            throw new DomainException("Only a pending transfer can be cancelled.");
        Status = OwnershipTransferStatus.Cancelled;
        CancelledAt = now;
        Touch(now);
    }

    /// <summary>Marks a lapsed transfer so a new one can start (the database allows one Pending per tenant).</summary>
    public void Expire(DateTimeOffset now)
    {
        if (EffectiveStatus(now) != OwnershipTransferStatus.Expired || Status != OwnershipTransferStatus.Pending)
            throw new DomainException("The transfer has not expired.");
        Status = OwnershipTransferStatus.Expired;
        Touch(now);
    }
}

using TrustedInventory.Domain.Common;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Domain.Organization;

public enum InvitationStatus
{
    Pending,
    Accepted,
    Revoked,
    Expired,
}

/// <summary>
/// A single-use invitation bound to one email address, roles and warehouse scope (ONB-005). Only the SHA-256 of
/// the token is stored. Roles and warehouse ids are parallel arrays: <c>WarehouseIds[i]</c> scopes
/// <c>Roles[i]</c>, null meaning every warehouse.
/// </summary>
public sealed class Invitation : TenantEntity
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);

    /// <summary>Roles an invitation can never carry (DEC-036, SOD-5, section 05).</summary>
    public static readonly IReadOnlySet<RoleName> ForbiddenRoles = new HashSet<RoleName>
    {
        RoleName.TenantOwner, RoleName.PlatformAdministrator, RoleName.PlatformBilling, RoleName.TelemetryIntegration,
    };

    private Invitation() { }

    public string EmailNormalized { get; private set; } = "";
    public byte[] TokenHash { get; private set; } = [];
    public RoleName[] Roles { get; private set; } = [];
    public Guid?[] WarehouseIds { get; private set; } = [];
    public InvitationStatus Status { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public string InvitedBy { get; private set; } = "";
    public string? AcceptedBySubject { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public Guid? MembershipId { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevokedBy { get; private set; }

    public static Invitation Create(Guid id, Guid tenantId, string emailNormalized, byte[] tokenHash,
        IReadOnlyList<(RoleName Role, Guid? WarehouseId)> scopes, string actor, DateTimeOffset now)
    {
        if (scopes.Count == 0)
            throw new DomainException("An invitation needs at least one role.");
        if (scopes.Any(s => ForbiddenRoles.Contains(s.Role)))
            throw new DomainException("This role cannot be granted by invitation.");
        var invitation = new Invitation
        {
            Id = id,
            EmailNormalized = emailNormalized,
            TokenHash = tokenHash,
            Roles = scopes.Select(s => s.Role).ToArray(),
            WarehouseIds = scopes.Select(s => s.WarehouseId).ToArray(),
            Status = InvitationStatus.Pending,
            ExpiresAt = now + Lifetime,
            InvitedBy = actor,
        };
        invitation.Stamp(tenantId, actor, now);
        return invitation;
    }

    public IReadOnlyList<(RoleName Role, Guid? WarehouseId)> Scopes => Roles.Zip(WarehouseIds).ToList();

    /// <summary>Pending and unexpired at <paramref name="now"/>.</summary>
    public bool IsUsable(DateTimeOffset now) => Status == InvitationStatus.Pending && now < ExpiresAt;

    /// <summary>Status as seen by callers: a Pending invitation past its expiry reads as Expired.</summary>
    public InvitationStatus EffectiveStatus(DateTimeOffset now) =>
        Status == InvitationStatus.Pending && now >= ExpiresAt ? InvitationStatus.Expired : Status;

    public void Accept(string subject, Guid membershipId, DateTimeOffset now)
    {
        if (!IsUsable(now))
            throw new DomainException("The invitation is no longer valid.");
        Status = InvitationStatus.Accepted;
        AcceptedBySubject = subject;
        AcceptedAt = now;
        MembershipId = membershipId;
        Touch(now);
    }

    public void Revoke(string actor, DateTimeOffset now)
    {
        if (Status != InvitationStatus.Pending)
            throw new DomainException($"Only pending invitations can be revoked; this one is {Status}.");
        Status = InvitationStatus.Revoked;
        RevokedAt = now;
        RevokedBy = actor;
        Touch(now);
    }
}

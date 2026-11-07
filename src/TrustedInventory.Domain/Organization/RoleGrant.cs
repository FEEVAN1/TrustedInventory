using TrustedInventory.Domain.Common;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Domain.Organization;

/// <summary>
/// A role granted to a membership, optionally limited to one warehouse (section 05). Grants are never edited:
/// they are revoked and, if needed, granted again, so the history stays complete.
/// </summary>
public sealed class RoleGrant : TenantEntity
{
    private RoleGrant() { }

    public Guid MembershipId { get; private set; }
    public string GranteeSubject { get; private set; } = "";
    public RoleName Role { get; private set; }
    public Guid? WarehouseId { get; private set; }
    public string Reason { get; private set; } = "";
    public string GrantedBy { get; private set; } = "";
    public DateTimeOffset GrantedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevokedBy { get; private set; }

    public bool Active => RevokedAt is null;

    public static RoleGrant Create(Guid tenantId, Membership membership, RoleName role, Guid? warehouseId, string reason,
        string actor, DateTimeOffset now)
    {
        if (role == RoleName.PlatformAdministrator)
            throw new DomainException("PlatformAdministrator cannot be granted within a tenant.");
        if (!membership.Active)
            throw new DomainException("Roles can only be granted to active memberships.");
        // SOD-3: nobody grants QA signing authority to themselves.
        if (actor == membership.UserSubject && RoleBundles.PermissionsOf(role).Contains(Permissions.ApprovalsSignQuality))
            throw new SeparationOfDutyException("You cannot grant QA signing permissions to yourself.");

        var grant = new RoleGrant
        {
            MembershipId = membership.Id,
            GranteeSubject = membership.UserSubject,
            Role = role,
            WarehouseId = warehouseId,
            Reason = reason,
            GrantedBy = actor,
            GrantedAt = now,
        };
        grant.Stamp(tenantId, actor, now);
        return grant;
    }

    public void Revoke(string actor, DateTimeOffset now)
    {
        if (!Active)
            throw new DomainException("Role grant is already revoked.");
        RevokedAt = now;
        RevokedBy = actor;
        Touch(now);
    }
}

/// <summary>A separation-of-duty rule was broken (maps to 409 APPROVAL_SELF).</summary>
public sealed class SeparationOfDutyException(string message) : Exception(message);

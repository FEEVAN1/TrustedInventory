using TrustedInventory.Domain.Common;

namespace TrustedInventory.Domain.Organization;

/// <summary>A user's access to one tenant (section 05). Deactivation removes all access immediately.</summary>
public sealed class Membership : TenantEntity
{
    private Membership() { }

    public string UserSubject { get; private set; } = "";
    public string? DisplayName { get; private set; }
    public bool Active { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevokedBy { get; private set; }

    public static Membership Create(Guid tenantId, string userSubject, string? displayName, string actor, DateTimeOffset now)
    {
        var membership = new Membership { UserSubject = userSubject, DisplayName = displayName, Active = true };
        membership.Stamp(tenantId, actor, now);
        return membership;
    }

    public void Deactivate(string actor, DateTimeOffset now)
    {
        if (!Active)
            throw new DomainException("Membership is already inactive.");
        if (actor == UserSubject)
            throw new DomainException("You cannot deactivate your own membership.");
        Active = false;
        RevokedAt = now;
        RevokedBy = actor;
        Touch(now);
    }
}

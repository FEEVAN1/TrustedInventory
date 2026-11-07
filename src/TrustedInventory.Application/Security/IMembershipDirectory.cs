using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Security;

/// <summary>One active membership role row from ti.user_memberships_v2 (Role is null when no grant exists).</summary>
public sealed record MembershipRole(
    Guid TenantId, string TenantName, string TenantSlug, TenantLifecycle Lifecycle, bool SecuritySuspended,
    Guid MembershipId, RoleName? Role, Guid? WarehouseId)
{
    /// <summary>v2.0 view kept for deprecated response fields until migration 0005 (DEC-041, DEC-042).</summary>
    public TenantStatus TenantStatus => SecuritySuspended ? TenantStatus.Suspended : TenantStatus.Active;
}

/// <summary>Looks up a subject's active memberships before any tenant context exists (IAM-002, DEC-013).</summary>
public interface IMembershipDirectory
{
    Task<IReadOnlyList<MembershipRole>> ForSubjectAsync(string subject, CancellationToken ct);
}

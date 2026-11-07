using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Security;

/// <summary>One active membership role row from ti.user_memberships (Role is null when no grant exists).</summary>
public sealed record MembershipRole(
    Guid TenantId, string TenantName, TenantStatus TenantStatus, Guid MembershipId, RoleName? Role, Guid? WarehouseId);

/// <summary>Looks up a subject's active memberships before any tenant context exists (IAM-002, DEC-013).</summary>
public interface IMembershipDirectory
{
    Task<IReadOnlyList<MembershipRole>> ForSubjectAsync(string subject, CancellationToken ct);
}

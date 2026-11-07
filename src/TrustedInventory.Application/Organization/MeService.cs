using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Organization;

public sealed record MeRole(RoleName Role, Guid? WarehouseId);

/// <summary>A permission and where it applies; null warehouse ids mean every warehouse of the tenant.</summary>
public sealed record MePermissionScope(string Permission, IReadOnlyList<Guid>? WarehouseIds);

public sealed record MeMembership(
    Guid MembershipId, Guid TenantId, string TenantName, TenantStatus TenantStatus,
    IReadOnlyList<MeRole> Roles, IReadOnlyList<MePermissionScope> Permissions);

public sealed record MeDto(string Subject, string? DisplayName, DateTimeOffset AuthTime, IReadOnlyList<MeMembership> Memberships);

/// <summary>
/// The caller's own identity and active memberships for the tenant selector (section 17, IAM-002). Uses only
/// the validated token subject; permissions are computed exactly as tenant requests compute them.
/// </summary>
public sealed class MeService(IMembershipDirectory memberships)
{
    public async Task<MeDto> GetAsync(string subject, string? displayName, DateTimeOffset authTime, CancellationToken ct)
    {
        var rows = await memberships.ForSubjectAsync(subject, ct);
        var items = rows
            .GroupBy(r => r.MembershipId)
            .Select(g =>
            {
                var first = g.First();
                var roles = g.Where(r => r.Role is not null).Select(r => (r.Role!.Value, r.WarehouseId)).ToList();
                var permissions = CallerContext.GrantsFrom(roles)
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => new MePermissionScope(p.Key, p.Value.AllWarehouses ? null : p.Value.Warehouses.Order().ToList()))
                    .ToList();
                return new MeMembership(first.MembershipId, first.TenantId, first.TenantName, first.TenantStatus,
                    roles.Select(r => new MeRole(r.Item1, r.WarehouseId)).ToList(), permissions);
            })
            .OrderBy(m => m.TenantName, StringComparer.Ordinal)
            .ToList();
        return new MeDto(subject, displayName, authTime, items);
    }
}

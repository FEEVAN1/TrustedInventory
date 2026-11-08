using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Organization;

public sealed record MeRole(RoleName Role, Guid? WarehouseId);

/// <summary>A permission and where it applies; null warehouse ids mean every warehouse of the tenant.</summary>
public sealed record MePermissionScope(string Permission, IReadOnlyList<Guid>? WarehouseIds);

public sealed record MeMembership(
    Guid MembershipId, Guid TenantId, string TenantName, TenantStatus TenantStatus,
    IReadOnlyList<MeRole> Roles, IReadOnlyList<MePermissionScope> Permissions,
    string TenantSlug, TenantLifecycle Lifecycle, AccessMode AccessMode, IReadOnlyList<RestrictionReason> RestrictionReasons,
    string? PlanCode);

public sealed record MeDto(string Subject, string? DisplayName, DateTimeOffset AuthTime, IReadOnlyList<MeMembership> Memberships);

/// <summary>
/// The caller's own identity and active memberships for the workspace switcher (sections 17 and 29, IAM-002).
/// Uses only the validated token subject; permissions are computed exactly as tenant requests compute them, and
/// each workspace shows its computed access mode so restrictions are visible before a tenant is chosen.
/// Deleted tenants never appear (ti.user_memberships_v2 excludes them).
/// </summary>
public sealed class MeService(IMembershipDirectory memberships, IWorkspaceStatusReader workspaces, TimeProvider clock)
{
    public async Task<MeDto> GetAsync(string subject, string? displayName, DateTimeOffset authTime, CancellationToken ct)
    {
        var rows = await memberships.ForSubjectAsync(subject, ct);
        var now = clock.GetUtcNow();
        var items = new List<MeMembership>();
        foreach (var g in rows.GroupBy(r => r.MembershipId))
        {
            var first = g.First();
            var roles = g.Where(r => r.Role is not null).Select(r => (r.Role!.Value, r.WarehouseId)).ToList();
            var permissions = CallerContext.GrantsFrom(roles)
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new MePermissionScope(p.Key, p.Value.AllWarehouses ? null : p.Value.Warehouses.Order().ToList()))
                .ToList();
            var (state, plan) = await workspaces.ReadAsync(first.TenantId, ct);
            if (state is null || state.Mode(now) == AccessMode.Deleted)
                continue;
            items.Add(new MeMembership(first.MembershipId, first.TenantId, first.TenantName, first.TenantStatus,
                roles.Select(r => new MeRole(r.Item1, r.WarehouseId)).ToList(), permissions,
                first.TenantSlug, first.Lifecycle, state.Mode(now), state.Reasons(now), plan));
        }
        return new MeDto(subject, displayName, authTime, items.OrderBy(m => m.TenantName, StringComparer.Ordinal).ToList());
    }
}

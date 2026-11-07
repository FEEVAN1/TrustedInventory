using TrustedInventory.Application.Errors;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Security;

public enum ActorType
{
    User,
    Service,
    Device,
    System,
}

/// <summary>Where a permission applies: everywhere in the tenant, or only in listed warehouses.</summary>
public sealed record PermissionGrant(bool AllWarehouses, IReadOnlySet<Guid> Warehouses)
{
    public bool Covers(Guid warehouseId) => AllWarehouses || Warehouses.Contains(warehouseId);
}

/// <summary>
/// The authenticated principal acting inside one validated tenant membership (IAM-001, IAM-002).
/// Built per request from the token subject and ti.user_memberships; never from request bodies (API-002).
/// </summary>
public sealed class CallerContext(
    string subject,
    ActorType actorType,
    Guid tenantId,
    TenantStatus tenantStatus,
    IReadOnlyDictionary<string, PermissionGrant> grants,
    string? requestId = null,
    string? correlationId = null)
{
    public string Subject { get; } = subject;
    public ActorType ActorType { get; } = actorType;
    public Guid TenantId { get; } = tenantId;
    public TenantStatus TenantStatus { get; } = tenantStatus;
    public IReadOnlyDictionary<string, PermissionGrant> Grants { get; } = grants;
    public string? RequestId { get; } = requestId;
    public string? CorrelationId { get; } = correlationId;

    public bool Has(string permission) => Grants.ContainsKey(permission);

    /// <summary>The caller holds the permission in at least one scope (endpoint-level gate).</summary>
    public void Require(string permission)
    {
        if (!Has(permission))
            throw AppException.Forbidden();
    }

    /// <summary>The caller holds the permission for every warehouse of the tenant (e.g. creating warehouses).</summary>
    public void RequireTenantWide(string permission)
    {
        if (!Grants.TryGetValue(permission, out var grant) || !grant.AllWarehouses)
            throw AppException.Forbidden();
    }

    public void RequireWarehouse(string permission, Guid warehouseId)
    {
        if (!CanAccess(permission, warehouseId))
            throw AppException.Forbidden();
    }

    public bool CanAccess(string permission, Guid warehouseId) =>
        Grants.TryGetValue(permission, out var grant) && grant.Covers(warehouseId);

    /// <summary>For list filtering: null means all warehouses, otherwise only these ids.</summary>
    public IReadOnlySet<Guid>? WarehouseFilter(string permission)
    {
        if (!Grants.TryGetValue(permission, out var grant))
            throw AppException.Forbidden();
        return grant.AllWarehouses ? null : grant.Warehouses;
    }

    /// <summary>Suspension blocks tenant mutations and integration ingestion (section 06).</summary>
    public void RequireMutable()
    {
        if (TenantStatus != TenantStatus.Active)
            throw AppException.Forbidden("The tenant is suspended; changes are not permitted.");
    }

    public static IReadOnlyDictionary<string, PermissionGrant> GrantsFrom(IEnumerable<(RoleName Role, Guid? WarehouseId)> roles)
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        var scoped = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        foreach (var (role, warehouseId) in roles)
        {
            foreach (var permission in RoleBundles.PermissionsOf(role))
            {
                // A warehouse-scoped grant only narrows warehouse-scoped permissions; tenant-level permissions
                // in the bundle still apply tenant-wide (docs/permissions.md).
                if (warehouseId is { } wid && Permissions.Scopes[permission] == PermissionScope.Warehouse)
                {
                    if (!scoped.TryGetValue(permission, out var set))
                        scoped[permission] = set = [];
                    set.Add(wid);
                }
                else
                {
                    all.Add(permission);
                }
            }
        }

        var result = new Dictionary<string, PermissionGrant>(StringComparer.Ordinal);
        foreach (var p in all)
            result[p] = new PermissionGrant(true, new HashSet<Guid>());
        foreach (var (p, set) in scoped)
            if (!all.Contains(p))
                result[p] = new PermissionGrant(false, set);
        return result;
    }
}

/// <summary>Request-scoped access to the current caller. Throws when no tenant context was established.</summary>
public interface ICallerAccessor
{
    CallerContext Caller { get; }
}

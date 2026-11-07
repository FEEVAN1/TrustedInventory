using System.Diagnostics;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Security;

namespace TrustedInventory.Api.Security;

/// <summary>Request-scoped holder for the caller resolved by <see cref="TenantContextFilter"/>.</summary>
public sealed class HttpCallerAccessor : ICallerAccessor
{
    private CallerContext? _caller;

    public CallerContext Caller =>
        _caller ?? throw new InvalidOperationException("No tenant context: the endpoint is not in the tenant route group.");

    public bool HasCaller => _caller is not null;

    public void Set(CallerContext caller) => _caller = caller;
}

/// <summary>Marks an endpoint (or route group) as tenant-scoped: it runs only inside a validated membership.</summary>
public sealed class TenantScopedMetadata
{
    public static readonly TenantScopedMetadata Instance = new();
}

/// <summary>
/// Establishes the tenant context for tenant routes (API-001, IAM-002) and enforces the endpoint permission
/// (IAM-001). Runs as middleware after authorization and before parameter binding, so an unauthorized caller
/// learns nothing about request validation. X-Tenant-Id must name a tenant where the token subject has an
/// active membership; permissions come from that membership's role grants only. A tenant the caller does not
/// belong to gets the same 403 as a non-existent one (no existence leak).
/// </summary>
public sealed class TenantContextMiddleware(RequestDelegate next)
{
    public const string TenantHeader = "X-Tenant-Id";

    public async Task InvokeAsync(HttpContext http, IMembershipDirectory memberships, HttpCallerAccessor accessor)
    {
        var endpoint = http.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<TenantScopedMetadata>() is null)
        {
            await next(http);
            return;
        }

        var subject = http.User.FindFirst("sub")?.Value
            ?? throw new AppException(ErrorCode.Unauthenticated, "The token has no subject.");

        if (!http.Request.Headers.TryGetValue(TenantHeader, out var raw) || raw.Count != 1)
            throw new AppException(ErrorCode.MalformedRequest, $"Exactly one {TenantHeader} header is required.");
        if (!Guid.TryParseExact(raw[0], "D", out var tenantId))
            throw new AppException(ErrorCode.MalformedRequest, $"{TenantHeader} must be a UUID.");

        var rows = (await memberships.ForSubjectAsync(subject, http.RequestAborted))
            .Where(r => r.TenantId == tenantId)
            .ToList();
        if (rows.Count == 0)
            throw AppException.Forbidden("You are not a member of this tenant.");

        var grants = CallerContext.GrantsFrom(rows.Where(r => r.Role is not null).Select(r => (r.Role!.Value, r.WarehouseId)));
        var caller = new CallerContext(subject, ActorType.User, tenantId, rows[0].TenantStatus, grants,
            requestId: Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier,
            correlationId: http.Request.Headers["X-Correlation-Id"].FirstOrDefault() is { Length: > 0 and <= 100 } cid ? cid : null);

        // Deny by default: a tenant endpoint without a declared permission never runs (IAM-001).
        var permission = endpoint.Metadata.GetMetadata<PermissionMetadata>()?.Permission
            ?? throw new InvalidOperationException($"Tenant endpoint {endpoint.DisplayName} declares no permission.");
        caller.Require(permission);
        accessor.Set(caller);

        await next(http);
    }
}

/// <summary>Endpoint metadata naming the permission an operation requires (matches openapi x-permission).</summary>
public sealed record PermissionMetadata(string Permission);

public static class PermissionEndpointExtensions
{
    /// <summary>
    /// Declares the endpoint permission (IAM-001). <see cref="TenantContextMiddleware"/> enforces it before the
    /// request body is read; services repeat the check with warehouse scope. Tests compare it with the contract.
    /// </summary>
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permission) =>
        builder.WithMetadata(new PermissionMetadata(permission));
}

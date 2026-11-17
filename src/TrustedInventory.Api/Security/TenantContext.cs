using System.Diagnostics;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Saas;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Api.Security;

/// <summary>Request-scoped holder for the caller resolved by <see cref="TenantContextMiddleware"/>.</summary>
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
/// The tenant half of the request pipeline (SAS-005), run as middleware after authentication and before
/// parameter binding, so a refused caller learns nothing about request validation:
/// membership (API-001, IAM-002) → access mode against the operation's category (state matrix, DEC-034) →
/// permission (IAM-001) → feature entitlement (DEC-038). Quota is reserved later, inside the command
/// transaction. A tenant the caller does not belong to gets the same 403 as a non-existent one.
/// </summary>
public sealed class TenantContextMiddleware(RequestDelegate next)
{
    public const string TenantHeader = "X-Tenant-Id";

    public async Task InvokeAsync(HttpContext http, IMembershipDirectory memberships, ITenantAccessReader accessReader,
        TimeProvider clock, HttpCallerAccessor accessor, ILogger<TenantContextMiddleware> log)
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

        // Deny by default: a tenant endpoint without a declared permission or access category never runs.
        var permission = endpoint.Metadata.GetMetadata<PermissionMetadata>()?.Permission
            ?? throw new InvalidOperationException($"Tenant endpoint {endpoint.DisplayName} declares no permission.");
        var access = endpoint.Metadata.GetMetadata<AccessMetadata>()
            ?? throw new InvalidOperationException($"Tenant endpoint {endpoint.DisplayName} has no x-access policy.");

        var state = await accessReader.ReadAsync(tenantId, http.RequestAborted)
            ?? throw AppException.Forbidden("You are not a member of this tenant.");
        var isOwner = rows.Any(r => r.Role == RoleName.TenantOwner);
        var now = clock.GetUtcNow();

        var grants = CallerContext.GrantsFrom(rows.Where(r => r.Role is not null).Select(r => (r.Role!.Value, r.WarehouseId)));
        var caller = new CallerContext(subject, ActorType.User, tenantId, rows[0].TenantStatus, grants,
            requestId: Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier,
            correlationId: http.Request.Headers["X-Correlation-Id"].FirstOrDefault() is { Length: > 0 and <= 100 } cid ? cid : null,
            access: state, isOwner: isOwner);

        AccessGate.Enforce(state, now, access.Category, isOwner, feature: null);
        RequirePermission(caller, permission);
        AccessGate.Enforce(state, now, access.Category, isOwner, endpoint.Metadata.GetMetadata<EntitlementMetadata>()?.Feature);
        accessor.Set(caller);

        // The validated tenant joins the request's log scope (DEC-079); the subject is not logged.
        http.Items[Http.RequestLogScope.TenantItem] = tenantId;
        using var scope = log.BeginScope(new Dictionary<string, object?> { ["tenantId"] = tenantId });
        await next(http);
    }

    /// <summary>Contract permission forms: a name, <c>a|b</c> (either), or <c>member</c> (any active member).</summary>
    private static void RequirePermission(CallerContext caller, string permission)
    {
        if (permission == Permissions.Member)
            return;
        if (!permission.Split('|').Any(caller.Has))
            throw AppException.Forbidden();
    }
}

/// <summary>The operation's `x-access` category (DEC-034).</summary>
public sealed record AccessMetadata(AccessCategory Category);

/// <summary>The operation's `x-entitlement` feature key (DEC-038).</summary>
public sealed record EntitlementMetadata(string Feature);

/// <summary>The operation's `x-quota` key; the service reserves it inside its transaction (DEC-038).</summary>
public sealed record QuotaMetadata(QuotaKey Quota);

/// <summary>Attaches contract policies (generated from docs/openapi.yaml) to endpoints by operationId.</summary>
public static class OperationPolicyConvention
{
    public static void Apply(EndpointBuilder builder)
    {
        var name = builder.Metadata.OfType<IEndpointNameMetadata>().LastOrDefault()?.EndpointName;
        if (name is null || !Contract.OperationPolicies.ByOperationId.TryGetValue(name, out var policy))
            return;
        if (policy.Access is not ("none" or "platform"))
            builder.Metadata.Add(new AccessMetadata(SaasWire.ParseCategory(policy.Access)));
        if (policy.Entitlement is { } feature)
            builder.Metadata.Add(new EntitlementMetadata(feature));
        if (policy.Quota is { } quota)
            builder.Metadata.Add(new QuotaMetadata(SaasWire.ParseQuota(quota)));
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

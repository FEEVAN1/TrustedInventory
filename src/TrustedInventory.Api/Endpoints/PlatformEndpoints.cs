using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TrustedInventory.Api.Http;
using TrustedInventory.Api.Security;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Organization;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Api.Endpoints;

/// <summary>
/// Platform console under <c>/platform/v1</c> (SAS-003, DEC-026, DEC-037). Operations callers must be listed in
/// <c>Platform:AdministratorSubjects</c>, billing callers in <c>Platform:BillingSubjects</c>; the authorization
/// policy rejects everyone else before the body is read, and the service checks again. No tenant data or stock
/// is reachable from here (SAS-004, SOP-006).
/// </summary>
public static class PlatformEndpoints
{
    public const string AdministratorsKey = "Platform:AdministratorSubjects";
    public const string BillingKey = "Platform:BillingSubjects";

    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        var platform = app.MapGroup("/platform/v1").RequireAuthorization(p => PlatformPolicy(p, AdministratorsKey));

        var tenants = platform.MapGroup("/tenants");
        tenants.MapGet("", (HttpContext http, PlatformTenantService svc, [AsParameters] ListQuery q, [FromQuery] TenantLifecycle? lifecycle,
                [FromQuery] AccessMode? accessMode, [FromQuery] string? search, CancellationToken ct) =>
                svc.ListAsync(Caller(http), new PlatformTenantFilter(lifecycle, accessMode, search), q.ToPage(), ct))
            .WithName("listPlatformTenants").RequirePermission(Permissions.PlatformTenantsManage);
        tenants.MapGet("/{id:guid}", async (HttpContext http, PlatformTenantService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(Caller(http), id, ct)))
            .WithName("getPlatformTenant").RequirePermission(Permissions.PlatformTenantsManage);
        tenants.MapPost("/{id:guid}/security-suspension", async (HttpContext http, PlatformTenantService svc, Guid id,
                SecuritySuspensionRequest body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.SuspendAsync(Caller(http), id, Conventions.IfMatch(http), body, ct)))
            .WithName("suspendTenantForSecurity").RequirePermission(Permissions.PlatformTenantsManage);
        tenants.MapPost("/{id:guid}/security-suspension/lift", async (HttpContext http, PlatformTenantService svc, Guid id,
                SecuritySuspensionRequest body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.LiftAsync(Caller(http), id, Conventions.IfMatch(http), body, ct)))
            .WithName("liftTenantSecuritySuspension").RequirePermission(Permissions.PlatformTenantsManage);

        var jobs = platform.MapGroup("/provisioning-jobs");
        jobs.MapGet("", (HttpContext http, PlatformTenantService svc, [AsParameters] ListQuery q, [FromQuery] ProvisioningStatusFilter? status,
                CancellationToken ct) => svc.ListProvisioningJobsAsync(Caller(http), status?.ToString(), q.ToPage(), ct))
            .WithName("listProvisioningJobs").RequirePermission(Permissions.PlatformTenantsManage);
        jobs.MapPost("/{id:guid}/retry", async (HttpContext http, PlatformTenantService svc, Guid id, CancellationToken ct) =>
            {
                // Resetting a Failed job is naturally idempotent; the key is still required by the contract (API-002).
                if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"].FirstOrDefault(), out _))
                    throw new AppException(ErrorCode.IdempotencyKeyRequired, "An Idempotency-Key UUID header is required.");
                return Results.Accepted(value: await svc.RetryProvisioningJobAsync(Caller(http), id, ct));
            })
            .WithName("retryProvisioningJob").RequirePermission(Permissions.PlatformTenantsManage);

        return app;
    }

    /// <summary>MFA plus membership of the configured subject list (DEC-026); PlatformBilling routes use <see cref="BillingKey"/>.</summary>
    public static AuthorizationPolicyBuilder PlatformPolicy(AuthorizationPolicyBuilder p, params string[] subjectsKeys) =>
        p.RequireAuthenticatedUser().RequireClaim("sub").AddRequirements(new MfaRequirement())
            .RequireAssertion(ctx => ctx.Resource is HttpContext http && subjectsKeys.Any(k => IsListed(http, k)));

    public static PlatformCaller Caller(HttpContext http) =>
        new(http.User.FindFirst("sub")!.Value, IsListed(http, AdministratorsKey), Problems.TraceId(http), IsListed(http, BillingKey));

    private static bool IsListed(HttpContext http, string key)
    {
        var subject = http.User.FindFirst("sub")?.Value;
        var listed = http.RequestServices.GetRequiredService<IConfiguration>().GetSection(key).Get<string[]>() ?? [];
        return subject is not null && listed.Contains(subject, StringComparer.Ordinal);
    }
}

/// <summary>ProvisioningStatus query values (docs/enums.md).</summary>
public enum ProvisioningStatusFilter
{
    Pending,
    Provisioning,
    Ready,
    Failed,
}

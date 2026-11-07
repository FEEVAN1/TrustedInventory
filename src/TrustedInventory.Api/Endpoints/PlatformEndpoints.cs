using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TrustedInventory.Api.Http;
using TrustedInventory.Api.Security;
using TrustedInventory.Application.Organization;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Api.Endpoints;

/// <summary>
/// Platform tenant administration (TEN-004, DEC-026). Callers must be listed in
/// <c>Platform:AdministratorSubjects</c>; the authorization policy rejects others before the body is read, and
/// the service checks again.
/// </summary>
public static class PlatformEndpoints
{
    public static RouteGroupBuilder MapPlatformEndpoints(this RouteGroupBuilder api)
    {
        var tenants = api.MapGroup("/platform/tenants")
            .RequireAuthorization(p => p.RequireAuthenticatedUser().RequireClaim("sub")
                .AddRequirements(new MfaRequirement())
                .RequireAssertion(ctx => ctx.Resource is HttpContext http && IsAdministrator(http)));

        tenants.MapGet("", (HttpContext http, PlatformTenantService svc, [AsParameters] ListQuery q, [FromQuery] TenantStatus? status,
                CancellationToken ct) => svc.ListAsync(Caller(http), status, q.ToPage(), ct))
            .WithName("listTenants").RequirePermission(Permissions.PlatformTenantsManage);
        tenants.MapPost("", async (HttpContext http, PlatformTenantService svc, CreateTenant body, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(Caller(http), body, ct);
                return Conventions.Created(http, $"/api/v1/platform/tenants/{created.Id}", created);
            })
            .WithName("createTenant").RequirePermission(Permissions.PlatformTenantsManage);
        tenants.MapGet("/{id:guid}", async (HttpContext http, PlatformTenantService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(Caller(http), id, ct)))
            .WithName("getTenant").RequirePermission(Permissions.PlatformTenantsManage);
        tenants.MapPost("/{id:guid}/suspend", async (HttpContext http, PlatformTenantService svc, Guid id, TenantStatusChange body,
                CancellationToken ct) => Conventions.Ok(http, await svc.SuspendAsync(Caller(http), id, Conventions.IfMatch(http), body, ct)))
            .WithName("suspendTenant").RequirePermission(Permissions.PlatformTenantsManage);
        tenants.MapPost("/{id:guid}/reactivate", async (HttpContext http, PlatformTenantService svc, Guid id, TenantStatusChange body,
                CancellationToken ct) => Conventions.Ok(http, await svc.ReactivateAsync(Caller(http), id, Conventions.IfMatch(http), body, ct)))
            .WithName("reactivateTenant").RequirePermission(Permissions.PlatformTenantsManage);
        return api;
    }

    private static PlatformCaller Caller(HttpContext http) =>
        new(http.User.FindFirst("sub")!.Value, IsAdministrator(http), Problems.TraceId(http));

    private static bool IsAdministrator(HttpContext http)
    {
        var subject = http.User.FindFirst("sub")?.Value;
        var administrators = http.RequestServices.GetRequiredService<IConfiguration>()
            .GetSection("Platform:AdministratorSubjects").Get<string[]>() ?? [];
        return subject is not null && administrators.Contains(subject, StringComparer.Ordinal);
    }
}

using Microsoft.AspNetCore.Mvc;
using TrustedInventory.Api.Http;
using TrustedInventory.Api.Security;
using TrustedInventory.Application.Administration;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Api.Endpoints;

/// <summary>Memberships, role grants and configuration versions (tags Administration, Configuration).</summary>
public static class AdministrationEndpoints
{
    public static RouteGroupBuilder MapAdministrationEndpoints(this RouteGroupBuilder api)
    {
        var memberships = api.MapGroup("/memberships");
        memberships.MapGet("", (MembershipService svc, [AsParameters] ListQuery q, [FromQuery] bool? active, CancellationToken ct) =>
                svc.ListAsync(active, q.ToPage(), ct))
            .WithName("listMemberships").RequirePermission(Permissions.TenantMembersManage);
        memberships.MapPost("", async (HttpContext http, MembershipService svc, CreateMembership body, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(body, ct);
                return Conventions.Created(http, $"/api/v1/memberships/{created.Id}", created);
            })
            .WithName("createMembership").RequirePermission(Permissions.TenantMembersManage);
        memberships.MapGet("/{id:guid}", async (HttpContext http, MembershipService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getMembership").RequirePermission(Permissions.TenantMembersManage);
        memberships.MapPost("/{id:guid}/deactivate", async (HttpContext http, MembershipService svc, Guid id, ReasonRequest body,
                CancellationToken ct) => Conventions.Ok(http, await svc.DeactivateAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("deactivateMembership").RequirePermission(Permissions.TenantMembersManage);
        memberships.MapGet("/{id:guid}/role-grants", (MembershipService svc, Guid id, [AsParameters] ListQuery q,
                [FromQuery] bool? active, CancellationToken ct) => svc.ListGrantsAsync(id, active, q.ToPage(), ct))
            .WithName("listRoleGrants").RequirePermission(Permissions.TenantMembersManage);
        memberships.MapPost("/{id:guid}/role-grants", async (HttpContext http, MembershipService svc, Guid id, CreateRoleGrant body,
                CancellationToken ct) =>
            {
                var created = await svc.GrantAsync(id, body, ct);
                return Conventions.Created(http, $"/api/v1/memberships/{id}/role-grants", created);
            })
            .WithName("createRoleGrant").RequirePermission(Permissions.TenantMembersManage);

        api.MapPost("/role-grants/{id:guid}/revoke", async (HttpContext http, MembershipService svc, Guid id, ReasonRequest body,
                CancellationToken ct) => Conventions.Ok(http, await svc.RevokeAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("revokeRoleGrant").RequirePermission(Permissions.TenantMembersManage);

        var configuration = api.MapGroup("/configuration");
        configuration.MapGet("", async (HttpContext http, ConfigurationService svc, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetCurrentAsync(ct)))
            .WithName("getConfiguration").RequirePermission(Permissions.CatalogRead);
        configuration.MapGet("/versions", (ConfigurationService svc, [AsParameters] ListQuery q, CancellationToken ct) =>
                svc.ListVersionsAsync(q.ToPage(), ct))
            .WithName("listConfigurationVersions").RequirePermission(Permissions.TenantConfigManage);
        configuration.MapPost("/versions", async (HttpContext http, ConfigurationService svc, CreateConfigurationVersion body,
                CancellationToken ct) =>
            {
                var created = await svc.CreateVersionAsync(Conventions.IfMatch(http), body, ct);
                return Conventions.Created(http, "/api/v1/configuration", created);
            })
            .WithName("createConfigurationVersion").RequirePermission(Permissions.TenantConfigManage);
        return api;
    }
}

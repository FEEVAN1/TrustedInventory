using Microsoft.AspNetCore.Mvc;
using TrustedInventory.Api.Http;
using TrustedInventory.Api.Security;
using TrustedInventory.Application.Administration;
using TrustedInventory.Application.Saas;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Api.Endpoints;

/// <summary>Invitations, ownership transfer and entitlements (tags Team, Subscription).</summary>
public static class TeamEndpoints
{
    /// <summary>Tenant-scoped routes (X-Tenant-Id required).</summary>
    public static RouteGroupBuilder MapTeamEndpoints(this RouteGroupBuilder tenant)
    {
        var invitations = tenant.MapGroup("/invitations");
        invitations.MapGet("", (InvitationService svc, [AsParameters] ListQuery q, [FromQuery] InvitationStatus? status,
                CancellationToken ct) => svc.ListAsync(status, q.ToPage(), ct))
            .WithName("listInvitations").RequirePermission(Permissions.TenantMembersManage);
        invitations.MapPost("", async (HttpContext http, InvitationService svc, CreateInvitation body, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(body, ct);
                // No single-invitation route exists; Location names the collection that lists it.
                return Conventions.Created(http, "/api/v1/invitations", created);
            })
            .WithName("createInvitation").RequirePermission(Permissions.TenantMembersManage);
        invitations.MapPost("/{id:guid}/revoke", async (HttpContext http, InvitationService svc, Guid id, ReasonRequest body,
                CancellationToken ct) => Conventions.Ok(http, await svc.RevokeAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("revokeInvitation").RequirePermission(Permissions.TenantMembersManage);

        var transfers = tenant.MapGroup("/saas/ownership-transfers");
        transfers.MapPost("", async (HttpContext http, OwnershipTransferService svc, CreateOwnershipTransfer body, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(body, ct);
                return Conventions.Created(http, $"/api/v1/saas/ownership-transfers/{created.Id}", created);
            })
            .WithName("createOwnershipTransfer").RequirePermission(Permissions.TenantOwnershipTransfer);
        transfers.MapPost("/{id:guid}/accept", async (HttpContext http, OwnershipTransferService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.AcceptAsync(id, Conventions.IfMatch(http), ct)))
            .WithName("acceptOwnershipTransfer").RequirePermission(Permissions.Member);
        transfers.MapPost("/{id:guid}/cancel", async (HttpContext http, OwnershipTransferService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.CancelAsync(id, Conventions.IfMatch(http), ct)))
            .WithName("cancelOwnershipTransfer").RequirePermission(Permissions.TenantOwnershipTransfer);

        tenant.MapGet("/saas/entitlements", (EntitlementService svc, CancellationToken ct) => svc.GetAsync(ct))
            .WithName("getEntitlements").RequirePermission(Permissions.Member);
        // Setup progress for any member once Ready (ONB-004, DEC-046).
        tenant.MapGet("/saas/setup-checklist", (SetupChecklistService svc, CancellationToken ct) => svc.GetAsync(ct))
            .WithName("getSetupChecklist").RequirePermission(Permissions.Member);
        // Opt-in, labelled sample data that never holds stock (ONB-004, DEC-098).
        tenant.MapGet("/saas/sample-data", async (HttpContext http, SampleDataService svc, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(ct)))
            .WithName("getSampleData").RequirePermission(Permissions.CatalogRead);
        tenant.MapPost("/saas/sample-data", async (HttpContext http, SampleDataService svc, CreateSampleData body, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(body, ct);
                return Conventions.Created(http, "/api/v1/saas/sample-data", created);
            })
            .WithName("createSampleData").RequirePermission(Permissions.TenantConfigManage);
        tenant.MapPost("/saas/sample-data/{id:guid}/remove", async (HttpContext http, SampleDataService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.RemoveAsync(id, Conventions.IfMatch(http), ct)))
            .WithName("removeSampleData").RequirePermission(Permissions.TenantConfigManage);
        return tenant;
    }

    /// <summary>
    /// Invitation acceptance: authenticated, but no tenant header, because the caller is not a member yet. The
    /// service applies the target tenant's lifecycle gate itself (team-admin category).
    /// </summary>
    public static RouteGroupBuilder MapInvitationAcceptance(this RouteGroupBuilder api)
    {
        api.MapPost("/invitations/accept", async (HttpContext http, InvitationService svc, AcceptInvitation body, CancellationToken ct) =>
            {
                var user = http.User;
                var joined = await svc.AcceptAsync(user.FindFirst("sub")!.Value, user.FindFirst("email")?.Value,
                    string.Equals(user.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase), body, ct);
                return Results.Created($"/api/v1/me", joined);
            })
            .WithName("acceptInvitation");
        return api;
    }
}

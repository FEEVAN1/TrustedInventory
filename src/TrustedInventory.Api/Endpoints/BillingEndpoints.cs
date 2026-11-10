using Microsoft.AspNetCore.Mvc;
using TrustedInventory.Api.Http;
using TrustedInventory.Api.Security;
using TrustedInventory.Application.Billing;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Api.Endpoints;

/// <summary>Billing reads for tenants and the platform plan catalog (docs/openapi.yaml tags Billing and PlatformBilling, DEC-062).</summary>
public static class BillingEndpoints
{
    public static RouteGroupBuilder MapBillingEndpoints(this RouteGroupBuilder tenant)
    {
        tenant.MapGet("/saas/subscription", async (HttpContext http, SubscriptionService svc, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(ct)))
            .WithName("getSubscription").RequirePermission(Permissions.BillingRead);
        tenant.MapGet("/saas/invoices", (SubscriptionService svc, [AsParameters] ListQuery q, [FromQuery] InvoiceStatus? status, CancellationToken ct) =>
                svc.ListInvoicesAsync(status, q.ToPage(), ct))
            .WithName("listInvoices").RequirePermission(Permissions.BillingRead);
        tenant.MapGet("/saas/invoices/{id:guid}", (SubscriptionService svc, Guid id, CancellationToken ct) => svc.GetInvoiceAsync(id, ct))
            .WithName("getInvoice").RequirePermission(Permissions.BillingRead);
        return tenant;
    }

    /// <summary>
    /// Plan catalog under /platform/v1 (DEC-037). Changes need PlatformBilling (<see cref="PlatformEndpoints.BillingKey"/>); the
    /// catalog view also admits platform operations. The service checks the role again.
    /// </summary>
    public static IEndpointRouteBuilder MapPlatformBillingEndpoints(this IEndpointRouteBuilder app)
    {
        var view = app.MapGroup("/platform/v1/plan-versions")
            .RequireAuthorization(p => PlatformEndpoints.PlatformPolicy(p, PlatformEndpoints.AdministratorsKey, PlatformEndpoints.BillingKey));
        view.MapGet("", (HttpContext http, CatalogService svc, [AsParameters] ListQuery q, [FromQuery] PlanVersionStatus? status,
                [FromQuery] string? planCode, CancellationToken ct) =>
                svc.ListAsync(PlatformEndpoints.Caller(http), status, planCode, q.ToPage(), ct))
            .WithName("listPlanVersions").RequirePermission($"{Permissions.PlatformTenantsManage}|{Permissions.PlatformBillingManage}");

        var billing = app.MapGroup("/platform/v1/plan-versions")
            .RequireAuthorization(p => PlatformEndpoints.PlatformPolicy(p, PlatformEndpoints.BillingKey));
        billing.MapPost("", async (HttpContext http, CatalogService svc, CreatePlanVersionRequest body, CancellationToken ct) =>
            {
                var created = await svc.CreatePlanAsync(PlatformEndpoints.Caller(http), body, ct);
                Conventions.SetETag(http, created.Revision);
                return Results.Created($"/platform/v1/plan-versions/{created.Plan.Id}", created.Plan);
            })
            .WithName("createPlanVersion").RequirePermission(Permissions.PlatformBillingManage);
        billing.MapPost("/{id:guid}/price-versions", async (HttpContext http, CatalogService svc, Guid id, CreatePriceVersionRequest body,
                CancellationToken ct) =>
            {
                var (price, plan) = await svc.AddPriceAsync(PlatformEndpoints.Caller(http), id, Conventions.IfMatch(http), body, ct);
                // The plan's new revision, for the next If-Match on this draft.
                Conventions.SetETag(http, plan.Revision);
                return Results.Created($"/platform/v1/plan-versions/{id}", price);
            })
            .WithName("createPriceVersion").RequirePermission(Permissions.PlatformBillingManage);
        billing.MapPost("/{id:guid}/publish", async (HttpContext http, CatalogService svc, Guid id, PublishPlanVersionRequest body,
                CancellationToken ct) =>
            {
                var published = await svc.PublishAsync(PlatformEndpoints.Caller(http), id, Conventions.IfMatch(http), body, ct);
                Conventions.SetETag(http, published.Revision);
                return Results.Ok(published.Plan);
            })
            .WithName("publishPlanVersion").RequirePermission(Permissions.PlatformBillingManage);
        return app;
    }
}

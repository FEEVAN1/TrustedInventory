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

        tenant.MapPost("/saas/checkout/preview", (CheckoutService svc, CheckoutPreviewRequest body, CancellationToken ct) => svc.PreviewAsync(body, ct))
            .WithName("previewCheckout").RequirePermission(Permissions.BillingManage);
        tenant.MapPost("/saas/checkout", async (HttpContext http, CheckoutService svc, CheckoutRequest body, CancellationToken ct) =>
            {
                var session = await svc.CreateAsync(Conventions.IdempotencyKey(http), body, ct);
                return Results.Created($"/api/v1/saas/checkout/{session.CheckoutId}", session);
            })
            .WithName("createCheckout").RequirePermission(Permissions.BillingManage);
        tenant.MapGet("/saas/checkout/{id:guid}", (CheckoutService svc, Guid id, CancellationToken ct) => svc.GetAsync(id, ct))
            .WithName("getCheckout").RequirePermission(Permissions.BillingManage);
        tenant.MapPost("/saas/subscription/change/preview", (PlanChangeService svc, SubscriptionChangePreviewRequest body, CancellationToken ct) =>
                svc.PreviewAsync(body, ct))
            .WithName("previewSubscriptionChange").RequirePermission(Permissions.BillingManage);
        tenant.MapPost("/saas/subscription/change", async (HttpContext http, PlanChangeService svc, SubscriptionChangeRequest body, CancellationToken ct) =>
            {
                var key = Conventions.IdempotencyKey(http);
                var (change, revision) = await svc.ChangeAsync(key, Conventions.IfMatch(http), body, ct);
                Conventions.SetETag(http, revision);
                return Results.Accepted((string?)null, change);
            })
            .WithName("changeSubscription").RequirePermission(Permissions.BillingManage);
        tenant.MapPost("/saas/subscription/cancel", async (HttpContext http, PlanChangeService svc, CancelSubscriptionRequest body, CancellationToken ct) =>
            {
                var subscription = await svc.CancelAsync(Conventions.IfMatch(http), body, ct);
                Conventions.SetETag(http, subscription.Version);
                return Results.Ok(subscription);
            })
            .WithName("cancelSubscription").RequirePermission(Permissions.SubscriptionCancel);
        tenant.MapPost("/saas/subscription/resume", async (HttpContext http, PlanChangeService svc, CancellationToken ct) =>
            {
                var subscription = await svc.ResumeAsync(Conventions.IfMatch(http), ct);
                Conventions.SetETag(http, subscription.Version);
                return Results.Ok(subscription);
            })
            .WithName("resumeSubscription").RequirePermission(Permissions.SubscriptionCancel);
        tenant.MapPost("/saas/billing-portal", async (CheckoutService svc, CancellationToken ct) =>
                Results.Created((string?)null, await svc.PortalAsync(ct)))
            .WithName("createBillingPortalSession").RequirePermission(Permissions.BillingManage);
        return tenant;
    }

    /// <summary>
    /// POST /webhooks/billing/{provider} (BIL-004, DEC-064): no token and no tenant header; the signature over the raw body is the
    /// authentication. The body is read as bytes, never model-bound, so verification sees exactly what the provider signed.
    /// </summary>
    public static RouteGroupBuilder MapBillingWebhookEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/webhooks/billing/{provider}", async (HttpContext http, WebhookService svc, string provider, CancellationToken ct) =>
            {
                using var buffer = new MemoryStream();
                var limit = WebhookService.MaxBodyBytes + 1;
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = await http.Request.Body.ReadAsync(chunk, ct)) > 0 && buffer.Length < limit)
                    buffer.Write(chunk, 0, read);
                var headers = http.Request.Headers;
                return Results.Ok(await svc.ReceiveAsync(provider, buffer.ToArray(), name => headers.TryGetValue(name, out var v) ? v.ToString() : null, ct));
            })
            .WithName("receiveBillingWebhook")
            .AllowAnonymous();
        return api;
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

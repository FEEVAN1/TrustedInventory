using Microsoft.AspNetCore.Mvc;
using TrustedInventory.Api.Http;
using TrustedInventory.Api.Security;
using TrustedInventory.Application.Administration;
using TrustedInventory.Application.Inbound;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Api.Endpoints;

/// <summary>Purchase orders (docs/openapi.yaml tag PurchaseOrders, INB-001). Handlers only translate HTTP to service calls.</summary>
public static class InboundEndpoints
{
    public static RouteGroupBuilder MapInboundEndpoints(this RouteGroupBuilder tenant)
    {
        var orders = tenant.MapGroup("/purchase-orders");
        orders.MapGet("", (PurchaseOrderService svc, [AsParameters] ListQuery q, [FromQuery] Guid? warehouseId, [FromQuery] Guid? supplierId,
                [FromQuery] PurchaseOrderStatus? status, [FromQuery] string? reference, CancellationToken ct) =>
                svc.ListAsync(new PurchaseOrderFilter(warehouseId, supplierId, status, reference), q.ToPage(), ct))
            .WithName("listPurchaseOrders").RequirePermission(Permissions.StockRead);
        orders.MapPost("", async (HttpContext http, PurchaseOrderService svc, CreatePurchaseOrder body, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(body, ct);
                return Conventions.Created(http, $"/api/v1/purchase-orders/{created.Id}", created);
            })
            .WithName("createPurchaseOrder").RequirePermission(Permissions.PurchaseOrdersCreate);
        orders.MapGet("/{id:guid}", async (HttpContext http, PurchaseOrderService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getPurchaseOrder").RequirePermission(Permissions.StockRead);
        orders.MapPatch("/{id:guid}", async (HttpContext http, PurchaseOrderService svc, Guid id, UpdatePurchaseOrder body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.UpdateAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("updatePurchaseOrder").RequirePermission(Permissions.PurchaseOrdersCreate);
        orders.MapPost("/{id:guid}/approve", async (HttpContext http, PurchaseOrderService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.ApproveAsync(id, Conventions.IfMatch(http), ct)))
            .WithName("approvePurchaseOrder").RequirePermission(Permissions.PurchaseOrdersApprove);
        orders.MapPost("/{id:guid}/short-close", async (HttpContext http, PurchaseOrderService svc, Guid id, ReasonRequest body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.ShortCloseAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("shortClosePurchaseOrder").RequirePermission(Permissions.PurchaseOrdersApprove);
        orders.MapPost("/{id:guid}/cancel", async (HttpContext http, PurchaseOrderService svc, Guid id, ReasonRequest body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.CancelAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("cancelPurchaseOrder").RequirePermission(Permissions.PurchaseOrdersApprove);
        return tenant;
    }
}

using Microsoft.AspNetCore.Mvc;
using TrustedInventory.Api.Http;
using TrustedInventory.Api.Security;
using TrustedInventory.Application.Administration;
using TrustedInventory.Application.Outbound;
using TrustedInventory.Domain.Outbound;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Api.Endpoints;

/// <summary>Outbound orders (docs/openapi.yaml tag Orders, OUT-001). Handlers only translate HTTP to service calls.</summary>
public static class OutboundEndpoints
{
    public static RouteGroupBuilder MapOutboundEndpoints(this RouteGroupBuilder tenant)
    {
        var orders = tenant.MapGroup("/orders");
        orders.MapGet("", (OutboundOrderService svc, [AsParameters] ListQuery q, [FromQuery] Guid? warehouseId, [FromQuery] Guid? customerId,
                [FromQuery] OutboundOrderStatus? status, [FromQuery] string? reference, CancellationToken ct) =>
                svc.ListAsync(new OutboundOrderFilter(warehouseId, customerId, status, reference), q.ToPage(), ct))
            .WithName("listOutboundOrders").RequirePermission(Permissions.StockRead);
        orders.MapPost("", async (HttpContext http, OutboundOrderService svc, CreateOutboundOrder body, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(body, ct);
                return Conventions.Created(http, $"/api/v1/orders/{created.Id}", created);
            })
            .WithName("createOutboundOrder").RequirePermission(Permissions.OrdersCreate);
        orders.MapGet("/{id:guid}", async (HttpContext http, OutboundOrderService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getOutboundOrder").RequirePermission(Permissions.StockRead);
        orders.MapPatch("/{id:guid}", async (HttpContext http, OutboundOrderService svc, Guid id, UpdateOutboundOrder body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.UpdateAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("updateOutboundOrder").RequirePermission(Permissions.OrdersCreate);
        orders.MapPost("/{id:guid}/confirm", async (HttpContext http, OutboundOrderService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.ConfirmAsync(id, Conventions.IfMatch(http), ct)))
            .WithName("confirmOutboundOrder").RequirePermission(Permissions.OrdersCreate);
        orders.MapPost("/{id:guid}/cancel", async (HttpContext http, OutboundOrderService svc, Guid id, ReasonRequest body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.CancelAsync(Conventions.IdempotencyKey(http), id, Conventions.IfMatch(http), body, ct)))
            .WithName("cancelOutboundOrder").RequirePermission(Permissions.OrdersCreate);

        // Allocation (OUT-002, STK-005, STK-006, DEC-053).
        orders.MapPost("/{id:guid}/allocate", async (HttpContext http, AllocationService svc, Guid id, AllocateRequest? body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.AllocateAsync(Conventions.IdempotencyKey(http), id, Conventions.IfMatch(http), body?.AllowPartial ?? false,
                    forceFefo: false, ct)))
            .WithName("allocateOutboundOrder").RequirePermission(Permissions.OrdersAllocate);
        orders.MapPost("/allocate-fefo", async (HttpContext http, AllocationService svc, AllocateFefoRequest body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.AllocateAsync(Conventions.IdempotencyKey(http), body.OrderId, expectedVersion: null,
                    body.AllowPartial ?? false, forceFefo: true, ct)))
            .WithName("allocateOutboundOrderFefo").RequirePermission(Permissions.OrdersAllocate);
        orders.MapPost("/{id:guid}/deallocate", async (HttpContext http, AllocationService svc, Guid id, DeallocateRequest body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.DeallocateAsync(Conventions.IdempotencyKey(http), id, Conventions.IfMatch(http), body, ct)))
            .WithName("deallocateOutboundOrder").RequirePermission(Permissions.OrdersAllocate);
        tenant.MapGet("/reservations", (AllocationService svc, [AsParameters] ListQuery q, [FromQuery] Guid? orderId, [FromQuery] Guid? warehouseId,
                [FromQuery] ReservationState? state, CancellationToken ct) =>
                svc.ListReservationsAsync(new ReservationFilter(orderId, warehouseId, state), q.ToPage(), ct))
            .WithName("listReservations").RequirePermission(Permissions.StockRead);

        // Picking (OUT-003, OUT-004, DEC-054).
        var picks = tenant.MapGroup("/picks");
        picks.MapGet("", (PickingService svc, [AsParameters] ListQuery q, [FromQuery] Guid? warehouseId, [FromQuery] Guid? orderId,
                [FromQuery] ReservationState? state, CancellationToken ct) =>
                svc.ListAsync(new PickFilter(warehouseId, orderId, state), q.ToPage(), ct))
            .WithName("listPicks").RequirePermission(Permissions.PicksConfirm);
        picks.MapGet("/{id:guid}", async (HttpContext http, PickingService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getPick").RequirePermission(Permissions.PicksConfirm);
        picks.MapPost("/{id:guid}/confirm", async (HttpContext http, PickingService svc, Guid id, ConfirmPick body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.ConfirmAsync(Conventions.IdempotencyKey(http), id, Conventions.IfMatch(http), body, ct)))
            .WithName("confirmPick").RequirePermission(Permissions.PicksConfirm);
        picks.MapPost("/{id:guid}/unpick", async (HttpContext http, PickingService svc, Guid id, UnpickRequest body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.UnpickAsync(Conventions.IdempotencyKey(http), id, Conventions.IfMatch(http), body, ct)))
            .WithName("unpick").RequirePermission(Permissions.PicksConfirm);

        // Dispatch (OUT-005, OUT-006, DEC-055).
        orders.MapPost("/{id:guid}/dispatch", async (HttpContext http, DispatchService svc, Guid id, DispatchRequest body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.DispatchAsync(Conventions.IdempotencyKey(http), id, Conventions.IfMatch(http), body, ct)))
            .WithName("dispatchOutboundOrder").RequirePermission(Permissions.DispatchPost);
        var shipments = tenant.MapGroup("/shipments");
        shipments.MapGet("", (DispatchService svc, [AsParameters] ListQuery q, [FromQuery] Guid? warehouseId, [FromQuery] Guid? orderId,
                [FromQuery] Guid? customerId, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct) =>
                svc.ListAsync(new ShipmentFilter(warehouseId, orderId, customerId, from, to), q.ToPage(), ct))
            .WithName("listShipments").RequirePermission(Permissions.StockRead);
        shipments.MapGet("/{id:guid}", async (HttpContext http, DispatchService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getShipment").RequirePermission(Permissions.StockRead);
        return tenant;
    }
}

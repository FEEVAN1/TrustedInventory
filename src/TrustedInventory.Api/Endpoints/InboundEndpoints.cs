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

        // Receiving (INB-002 to INB-004, DEC-048): one idempotent stock command.
        tenant.MapPost("/inventory/receive", async (HttpContext http, ReceivingService svc, ReceiveInventory body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.ReceiveAsync(Conventions.IdempotencyKey(http), body, ct)))
            .WithName("receiveInventory").RequirePermission(Permissions.ReceiptsPost);
        var receipts = tenant.MapGroup("/receipts");
        receipts.MapGet("", (ReceivingService svc, [AsParameters] ListQuery q, [FromQuery] Guid? warehouseId, [FromQuery] Guid? purchaseOrderId,
                [FromQuery] ReceiptStatus? status, CancellationToken ct) =>
                svc.ListAsync(new ReceiptFilter(warehouseId, purchaseOrderId, status), q.ToPage(), ct))
            .WithName("listReceipts").RequirePermission(Permissions.StockRead);
        receipts.MapGet("/{id:guid}", async (HttpContext http, ReceivingService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getReceipt").RequirePermission(Permissions.StockRead);

        // Stock projection and ledger (STK-001, STK-004, DEC-049).
        var stock = tenant.MapGroup("/stock");
        stock.MapGet("", (StockService svc, [AsParameters] ListQuery q, [FromQuery] Guid? warehouseId, [FromQuery] Guid? productId,
                [FromQuery] Guid? lotId, [FromQuery] Guid? binId, [FromQuery] QualityStatus? qualityStatus, [FromQuery] bool? eligibleOnly,
                [FromQuery] bool? includeEmpty, CancellationToken ct) =>
                svc.ListAsync(new StockFilter(warehouseId, productId, lotId, binId, qualityStatus, eligibleOnly ?? false, includeEmpty ?? false),
                    q.ToPage(), ct))
            .WithName("listStock").RequirePermission(Permissions.StockRead);
        stock.MapGet("/{id:guid}", async (HttpContext http, StockService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getStockBucket").RequirePermission(Permissions.StockRead);
        stock.MapGet("/movements", (StockService svc, [AsParameters] ListQuery q, [FromQuery] DocumentType? documentType, [FromQuery] Guid? documentId,
                [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] Guid? productId, [FromQuery] Guid? lotId,
                [FromQuery] Guid? warehouseId, [FromQuery] MovementType? type, CancellationToken ct) =>
                svc.ListMovementsAsync(new MovementFilter(documentType, documentId, from, to, productId, lotId, warehouseId, type), q.ToPage(), ct))
            .WithName("listStockMovements").RequirePermission(Permissions.StockRead);
        stock.MapGet("/movements/{id:guid}", (StockService svc, Guid id, CancellationToken ct) => svc.GetMovementAsync(id, ct))
            .WithName("getStockMovement").RequirePermission(Permissions.StockRead);

        // Putaway (INB-005, INB-006, DEC-050).
        var putaway = tenant.MapGroup("/putaway");
        putaway.MapGet("/tasks", (PutawayService svc, [AsParameters] ListQuery q, [FromQuery] Guid? warehouseId,
                [FromQuery] Domain.Inbound.TaskStatus? status, [FromQuery] Guid? receiptId, CancellationToken ct) =>
                svc.ListAsync(new TaskFilter(warehouseId, status, receiptId), q.ToPage(), ct))
            .WithName("listPutawayTasks").RequirePermission(Permissions.PutawayConfirm);
        putaway.MapPost("/confirm", async (HttpContext http, PutawayService svc, ConfirmPutaway body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.ConfirmAsync(Conventions.IdempotencyKey(http), body, ct)))
            .WithName("confirmPutaway").RequirePermission(Permissions.PutawayConfirm);

        // Warehouse tasks of every type; rework and exception tasks complete by hand (DEC-016, DEC-085).
        var tasks = tenant.MapGroup("/tasks");
        tasks.MapGet("", (TaskService svc, [AsParameters] ListQuery q, [FromQuery] Guid? warehouseId, [FromQuery] TaskType? type,
                [FromQuery] Domain.Inbound.TaskStatus? status, CancellationToken ct) =>
                svc.ListAsync(new WorkTaskFilter(warehouseId, type, status), q.ToPage(), ct))
            .WithName("listTasks").RequirePermission(Permissions.StockRead);
        tasks.MapGet("/{id:guid}", async (HttpContext http, TaskService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getTask").RequirePermission(Permissions.StockRead);
        tasks.MapPost("/{id:guid}/complete", async (HttpContext http, TaskService svc, Guid id, CompleteTask body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.CompleteAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("completeTask").RequirePermission(Permissions.TasksManage);

        // Lots and serials, read only (section 07, DEC-087); both are created by receipt posting.
        tenant.MapGet("/lots", (LotService svc, [AsParameters] ListQuery q, [FromQuery] Guid? productId, [FromQuery] string? lotNumber,
                [FromQuery] DateOnly? expiresBefore, CancellationToken ct) =>
                svc.ListLotsAsync(new LotFilter(productId, lotNumber, expiresBefore), q.ToPage(), ct))
            .WithName("listLots").RequirePermission(Permissions.StockRead);
        tenant.MapGet("/lots/{id:guid}", async (HttpContext http, LotService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetLotAsync(id, ct)))
            .WithName("getLot").RequirePermission(Permissions.StockRead);
        tenant.MapPost("/lots/{id:guid}/expiry-correction", async (HttpContext http, LotService svc, Guid id, ExpiryCorrectionRequest body,
                CancellationToken ct) => Conventions.Outcome(http, await svc.CorrectExpiryAsync(Conventions.IdempotencyKey(http), id, Conventions.IfMatch(http), body, ct)))
            .WithName("correctLotExpiry").RequirePermission(Permissions.QualityHold);
        tenant.MapGet("/serials", (LotService svc, [AsParameters] ListQuery q, [FromQuery] Guid? productId, [FromQuery] Guid? lotId,
                [FromQuery] string? serialNumber, CancellationToken ct) =>
                svc.ListSerialsAsync(new SerialFilter(productId, lotId, serialNumber), q.ToPage(), ct))
            .WithName("listSerials").RequirePermission(Permissions.StockRead);
        return tenant;
    }
}

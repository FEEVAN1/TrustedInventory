using Microsoft.AspNetCore.Mvc;
using TrustedInventory.Api.Http;
using TrustedInventory.Api.Security;
using TrustedInventory.Application.Administration;
using TrustedInventory.Application.Inbound;
using TrustedInventory.Application.Operations;
using TrustedInventory.Domain.Operations;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Api.Endpoints;

/// <summary>Approvals and adjustments (docs/openapi.yaml tags Approvals and Adjustments, DEC-057). Handlers only translate HTTP.</summary>
public static class OperationsEndpoints
{
    private const string Inbox = Permissions.ApprovalsRequest + "|" + Permissions.ApprovalsSignInventory + "|" + Permissions.ApprovalsSignQuality;
    private const string Signers = Permissions.ApprovalsSignInventory + "|" + Permissions.ApprovalsSignQuality;

    public static RouteGroupBuilder MapOperationsEndpoints(this RouteGroupBuilder tenant)
    {
        var approvals = tenant.MapGroup("/approvals");
        approvals.MapGet("", (ApprovalService svc, [AsParameters] ListQuery q, [FromQuery] ApprovalStatus? status, [FromQuery] ApprovalSubjectType? subjectType,
                [FromQuery] Guid? subjectId, [FromQuery] bool? awaitingMySignature, CancellationToken ct) =>
                svc.ListAsync(new ApprovalFilter(status, subjectType, subjectId, awaitingMySignature ?? false), q.ToPage(), ct))
            .WithName("listApprovals").RequirePermission(Inbox);
        approvals.MapPost("", async (HttpContext http, ApprovalService svc, CreateApproval body, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(body, ct);
                return Conventions.Created(http, $"/api/v1/approvals/{created.Id}", created);
            })
            .WithName("createApproval").RequirePermission(Permissions.ApprovalsRequest);
        approvals.MapGet("/{id:guid}", async (HttpContext http, ApprovalService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getApproval").RequirePermission(Inbox);
        approvals.MapPost("/{id:guid}/sign", async (HttpContext http, ApprovalService svc, Guid id, SignApproval body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.SignAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("signApproval").RequirePermission(Signers);
        approvals.MapPost("/{id:guid}/reject", async (HttpContext http, ApprovalService svc, Guid id, ReasonRequest body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.RejectAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("rejectApproval").RequirePermission(Signers);

        var adjustments = tenant.MapGroup("/adjustments");
        adjustments.MapGet("", (AdjustmentService svc, [AsParameters] ListQuery q, [FromQuery] Guid? warehouseId, [FromQuery] AdjustmentStatus? status,
                CancellationToken ct) =>
                svc.ListAsync(new AdjustmentFilter(warehouseId, status), q.ToPage(), ct))
            .WithName("listAdjustments").RequirePermission(Permissions.StockRead);
        adjustments.MapPost("", async (HttpContext http, AdjustmentService svc, CreateAdjustment body, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(body, ct);
                return Conventions.Created(http, $"/api/v1/adjustments/{created.Id}", created);
            })
            .WithName("createAdjustment").RequirePermission(Permissions.AdjustmentsRequest);
        adjustments.MapGet("/{id:guid}", async (HttpContext http, AdjustmentService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getAdjustment").RequirePermission(Permissions.StockRead);
        adjustments.MapPatch("/{id:guid}", async (HttpContext http, AdjustmentService svc, Guid id, UpdateAdjustment body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.UpdateAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("updateAdjustment").RequirePermission(Permissions.AdjustmentsRequest);
        adjustments.MapPost("/{id:guid}/post", async (HttpContext http, AdjustmentService svc, Guid id, ApprovalReference body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.PostAsync(Conventions.IdempotencyKey(http), id, Conventions.IfMatch(http), body, ct)))
            .WithName("postAdjustment").RequirePermission(Permissions.AdjustmentsPost);
        adjustments.MapPost("/{id:guid}/cancel", async (HttpContext http, AdjustmentService svc, Guid id, ReasonRequest body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.CancelAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("cancelAdjustment").RequirePermission(Permissions.AdjustmentsRequest);

        // Bin and warehouse transfers (EXC-001, EXC-002, STK-003, DEC-058).
        tenant.MapPost("/inventory/bin-transfers", async (HttpContext http, BinTransferService svc, BinTransfer body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.TransferAsync(Conventions.IdempotencyKey(http), body, ct)))
            .WithName("transferBetweenBins").RequirePermission(Permissions.PutawayConfirm);
        var transfers = tenant.MapGroup("/transfers");
        transfers.MapGet("", (TransferService svc, [AsParameters] ListQuery q, [FromQuery] Guid? sourceWarehouseId, [FromQuery] Guid? destinationWarehouseId,
                [FromQuery] TransferStatus? status, CancellationToken ct) =>
                svc.ListAsync(new TransferFilter(sourceWarehouseId, destinationWarehouseId, status), q.ToPage(), ct))
            .WithName("listTransfers").RequirePermission(Permissions.StockRead);
        transfers.MapPost("", async (HttpContext http, TransferService svc, CreateTransfer body, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(body, ct);
                return Conventions.Created(http, $"/api/v1/transfers/{created.Id}", created);
            })
            .WithName("createTransfer").RequirePermission(Permissions.TransfersManage);
        transfers.MapGet("/{id:guid}", async (HttpContext http, TransferService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getTransfer").RequirePermission(Permissions.StockRead);
        transfers.MapPost("/{id:guid}/approve", async (HttpContext http, TransferService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.ApproveAsync(id, Conventions.IfMatch(http), ct)))
            .WithName("approveTransfer").RequirePermission(Permissions.TransfersManage);
        transfers.MapPost("/{id:guid}/dispatch", async (HttpContext http, TransferService svc, Guid id, TransferDispatch body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.DispatchAsync(Conventions.IdempotencyKey(http), id, Conventions.IfMatch(http), body, ct)))
            .WithName("dispatchTransfer").RequirePermission(Permissions.TransfersManage);
        transfers.MapPost("/{id:guid}/receive", async (HttpContext http, TransferService svc, Guid id, TransferReceive body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.ReceiveAsync(Conventions.IdempotencyKey(http), id, Conventions.IfMatch(http), body, ct)))
            .WithName("receiveTransfer").RequirePermission(Permissions.TransfersManage);
        transfers.MapPost("/{id:guid}/disposition", async (HttpContext http, TransferService svc, Guid id, TransitDisposition body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.DisposeAsync(Conventions.IdempotencyKey(http), id, Conventions.IfMatch(http), body, ct)))
            .WithName("dispositionTransitQuantity").RequirePermission(Permissions.TransfersManage);
        transfers.MapPost("/{id:guid}/cancel", async (HttpContext http, TransferService svc, Guid id, ReasonRequest body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.CancelAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("cancelTransfer").RequirePermission(Permissions.TransfersManage);

        // Stock counts (EXC-003, DEC-008, DEC-059).
        var counts = tenant.MapGroup("/counts");
        counts.MapGet("", (CountService svc, [AsParameters] ListQuery q, [FromQuery] Guid? warehouseId, [FromQuery] CountStatus? status, CancellationToken ct) =>
                svc.ListAsync(new CountFilter(warehouseId, status), q.ToPage(), ct))
            .WithName("listCounts").RequirePermission(Permissions.CountsPerform);
        counts.MapPost("", async (HttpContext http, CountService svc, CreateCount body, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(body, ct);
                return Conventions.Created(http, $"/api/v1/counts/{created.Id}", created);
            })
            .WithName("createCount").RequirePermission(Permissions.CountsPerform);
        counts.MapGet("/{id:guid}", async (HttpContext http, CountService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getCount").RequirePermission(Permissions.CountsPerform);
        counts.MapPost("/{id:guid}/start", async (HttpContext http, CountService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.StartAsync(id, Conventions.IfMatch(http), ct)))
            .WithName("startCount").RequirePermission(Permissions.CountsPerform);
        counts.MapPost("/{id:guid}/submit", async (HttpContext http, CountService svc, Guid id, SubmitCount body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.SubmitAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("submitCount").RequirePermission(Permissions.CountsPerform);
        counts.MapPost("/{id:guid}/approve", async (HttpContext http, CountService svc, Guid id, ApprovalReference body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.ApproveAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("approveCount").RequirePermission(Permissions.CountsManage);
        counts.MapPost("/{id:guid}/reject", async (HttpContext http, CountService svc, Guid id, ReasonRequest body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.RejectAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("rejectCount").RequirePermission(Permissions.CountsManage);
        counts.MapPost("/{id:guid}/post", async (HttpContext http, CountService svc, Guid id, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.PostAsync(Conventions.IdempotencyKey(http), id, Conventions.IfMatch(http), ct)))
            .WithName("postCount").RequirePermission(Permissions.CountsManage);
        counts.MapPost("/{id:guid}/cancel", async (HttpContext http, CountService svc, Guid id, ReasonRequest body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.CancelAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("cancelCount").RequirePermission(Permissions.CountsManage);

        // Customer returns (EXC-005, AT-EXC-03, DEC-060).
        var returns = tenant.MapGroup("/returns");
        returns.MapGet("", (ReturnService svc, [AsParameters] ListQuery q, [FromQuery] Guid? warehouseId, [FromQuery] ReturnStatus? status, CancellationToken ct) =>
                svc.ListAsync(new ReturnFilter(warehouseId, status), q.ToPage(), ct))
            .WithName("listReturns").RequirePermission(Permissions.StockRead);
        returns.MapPost("", async (HttpContext http, ReturnService svc, CreateReturn body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.CreateAsync(Conventions.IdempotencyKey(http), body, ct)))
            .WithName("createReturn").RequirePermission(Permissions.ReturnsManage);
        returns.MapGet("/{id:guid}", async (HttpContext http, ReturnService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getReturn").RequirePermission(Permissions.StockRead);
        returns.MapPost("/{id:guid}/inspect", async (HttpContext http, ReturnService svc, Guid id, InspectReturn body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.InspectAsync(Conventions.IdempotencyKey(http), id, Conventions.IfMatch(http), body, ct)))
            .WithName("inspectReturn").RequirePermission(Permissions.ReturnsInspect);

        // Quality hold, release and disposition (MED-001, MED-002, EXC-007, DEC-075).
        var quality = tenant.MapGroup("/quality");
        quality.MapPost("/hold", async (HttpContext http, QualityService svc, QualityHoldRequest body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.HoldAsync(Conventions.IdempotencyKey(http), body, ct)))
            .WithName("holdQuality").RequirePermission(Permissions.QualityHold);
        quality.MapPost("/release", async (HttpContext http, QualityService svc, QualityReleaseRequest body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.ReleaseAsync(Conventions.IdempotencyKey(http), body, ct)))
            .WithName("releaseQuality").RequirePermission(Permissions.QualityRelease);
        quality.MapPost("/disposition", async (HttpContext http, QualityService svc, QualityDispositionRequest body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.DispositionAsync(Conventions.IdempotencyKey(http), body, ct)))
            .WithName("dispositionQuality").RequirePermission(Permissions.QualityHold);

        // Recalls (EXC-006, EXC-007, DEC-076).
        var recalls = tenant.MapGroup("/recalls");
        recalls.MapGet("", (RecallService svc, [AsParameters] ListQuery q, [FromQuery] Guid? productId, [FromQuery] RecallStatus? status, CancellationToken ct) =>
                svc.ListAsync(new RecallFilter(productId, status), q.ToPage(), ct))
            .WithName("listRecalls").RequirePermission(Permissions.StockRead);
        recalls.MapPost("", async (HttpContext http, RecallService svc, CreateRecallRequest body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.CreateAsync(Conventions.IdempotencyKey(http), body, ct)))
            .WithName("createRecall").RequirePermission(Permissions.RecallsManage);
        recalls.MapGet("/{id:guid}", async (HttpContext http, RecallService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getRecall").RequirePermission(Permissions.StockRead);
        recalls.MapPost("/{id:guid}/outreach", async (HttpContext http, RecallService svc, Guid id, RecordOutreachRequest body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.RecordOutreachAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("recordRecallOutreach").RequirePermission(Permissions.RecallsManage);
        recalls.MapPost("/{id:guid}/close", async (HttpContext http, RecallService svc, Guid id, CloseRecallRequest body, CancellationToken ct) =>
                Conventions.Outcome(http, await svc.CloseAsync(Conventions.IdempotencyKey(http), id, Conventions.IfMatch(http), body, ct)))
            .WithName("closeRecall").RequirePermission(Permissions.RecallsClose);
        return tenant;
    }
}

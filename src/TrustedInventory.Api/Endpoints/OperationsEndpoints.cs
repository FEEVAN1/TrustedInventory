using Microsoft.AspNetCore.Mvc;
using TrustedInventory.Api.Http;
using TrustedInventory.Api.Security;
using TrustedInventory.Application.Administration;
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
        return tenant;
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Security;
using TaskStatus = TrustedInventory.Domain.Inbound.TaskStatus;

namespace TrustedInventory.Application.Inbound;

public sealed record BinRecommendationDto(Guid BinId, string BinCode, int Rank, string Reason);

public sealed record WarehouseTaskDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, TaskType Type, TaskStatus Status, Guid WarehouseId,
    DocumentType SourceType, Guid SourceId, Guid? SourceLineId, Guid ProductId, Guid? LotId, Guid? SourceBinId, Quantity Quantity,
    Quantity RemainingQuantity, IReadOnlyList<BinRecommendationDto> RecommendedBins, string? Description, string? Resolution,
    DateTimeOffset? CompletedAt, string? CompletedBy) : IVersioned;

public sealed record TaskFilter(Guid? WarehouseId, TaskStatus? Status, Guid? ReceiptId);

public sealed record ConfirmPutaway(Guid ReceiptLineId, Guid SourceBinId, Guid TargetBinId, Quantity Quantity, Guid ScannedProductId,
    string? ScannedLotNumber, string? Reason);

public sealed record PutawayConfirmResult(Guid MovementId, Guid TaskId, Quantity RemainingQuantity, TaskStatus TaskStatus);

public sealed class ConfirmPutawayValidator : AbstractValidator<ConfirmPutaway>
{
    public ConfirmPutawayValidator()
    {
        RuleFor(x => x.ReceiptLineId).NotEmpty();
        RuleFor(x => x.SourceBinId).NotEmpty();
        RuleFor(x => x.TargetBinId).NotEmpty();
        RuleFor(x => x.ScannedProductId).NotEmpty();
        RuleFor(x => x.Quantity).Must(Quantity.IsValidPositive).WithMessage(Quantity.PositiveMessage);
        RuleFor(x => x.ScannedLotNumber!).Must(v => v.Length is >= 1 and <= 100 && v == v.Trim())
            .WithMessage("Use 1 to 100 characters without leading or trailing spaces.").When(x => x.ScannedLotNumber is not null);
        RuleFor(x => x.Reason!).ValidReason().When(x => x.Reason is not null);
    }
}

/// <summary>
/// Putaway (INB-005, INB-006, AT-INB-03, DEC-050). Tasks come from receiving; confirmation moves staged stock into a
/// suitable bin by scan, rechecking the hard constraints against current data, in one idempotent stock command.
/// Lock order: task row → source bucket → target bucket → audit stream (migration-design §5).
/// </summary>
public sealed class PutawayService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IValidator<ConfirmPutaway> validator)
{
    public const string Route = "POST /putaway/confirm";

    public Task<Page<WarehouseTaskDto>> ListAsync(TaskFilter filter, PageRequest page, CancellationToken ct)
    {
        var scope = callers.Caller.WarehouseFilter(Permissions.PutawayConfirm);
        return uow.ReadAsync(async (s, c) =>
        {
            var query = s.Db.Set<WarehouseTask>().AsNoTracking().Where(t => t.Type == TaskType.Putaway || t.Type == TaskType.Exception);
            if (scope is not null)
                query = query.Where(t => scope.Contains(t.WarehouseId));
            if (filter.WarehouseId is { } w)
                query = query.Where(t => t.WarehouseId == w);
            if (filter.Status is { } status)
                query = query.Where(t => t.Status == status);
            if (filter.ReceiptId is { } rid)
            {
                var lineIds = s.Db.Set<ReceiptLine>().Where(l => l.ReceiptId == rid).Select(l => l.Id);
                query = query.Where(t => t.ReceiptLineId != null && lineIds.Contains(t.ReceiptLineId.Value));
            }
            var tasks = await query.ToPageAsync(page, $"putaway-tasks|{filter.WarehouseId}|{filter.Status}|{filter.ReceiptId}", cursors, t => t, c);
            var dtos = new List<WarehouseTaskDto>();
            foreach (var task in tasks.Items)
                dtos.Add(await DtoAsync(s, task, c));
            return new Page<WarehouseTaskDto>(dtos, tasks.NextCursor);
        }, ct);
    }

    public Task<CommandOutcome> ConfirmAsync(Guid idempotencyKey, ConfirmPutaway cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.PutawayConfirm);
        caller.RequireMutable();
        validator.ValidateOrThrow(cmd);
        var payload = JsonSerializer.SerializeToNode(cmd, JsonDefaults.Options);
        var operationKey = $"putaway:{caller.Subject}:{idempotencyKey:D}";

        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, Route, payload), async (s, c) =>
        {
            // 1. Lock the line's task first; a concurrent retry of the same key then sees the committed operation key.
            var taskIds = await s.Db.Database.SqlQuery<Guid>(
                $"SELECT id AS \"Value\" FROM ti.warehouse_task WHERE receipt_line_id = {cmd.ReceiptLineId} ORDER BY created_at FOR UPDATE").ToListAsync(c);
            var earlier = await s.Db.Database.SqlQuery<string>(
                $"SELECT result_reference::text AS \"Value\" FROM ti.document_operation WHERE operation_key = {operationKey}").ToListAsync(c);
            if (earlier.Count == 1)
                return new CommandOutcome(200, JsonNode.Parse(earlier[0]));
            if (taskIds.Count == 0)
                throw AppException.NotFound("Putaway task");

            var result = await PostAsync(s, cmd, operationKey, caller, c);
            return new CommandOutcome(200, JsonSerializer.SerializeToNode(result, JsonDefaults.Options));
        }, ct);
    }

    private static async Task<PutawayConfirmResult> PostAsync(ITenantSession s, ConfirmPutaway cmd, string operationKey, CallerContext caller, CancellationToken ct)
    {
        var task = await s.Db.Set<WarehouseTask>().Where(t => t.ReceiptLineId == cmd.ReceiptLineId && (t.Status == TaskStatus.Open || t.Status == TaskStatus.InProgress))
            .SingleOrDefaultAsync(ct) ?? throw AppException.InvalidState("The putaway task for this line is already complete.");
        caller.RequireWarehouse(Permissions.PutawayConfirm, task.WarehouseId);
        var line = await s.Db.Set<ReceiptLine>().AsNoTracking().SingleAsync(l => l.Id == cmd.ReceiptLineId, ct);
        var product = await s.Db.Set<Product>().AsNoTracking().SingleAsync(p => p.Id == line.ProductId, ct);

        // 2. Scan confirmation (INB-006).
        if (cmd.SourceBinId != task.SourceBinId)
            throw AppException.Validation("sourceBinId", "The goods are staged in a different bin.");
        if (cmd.ScannedProductId != line.ProductId)
            throw Coded(ErrorCode.BarcodeConflict, "scannedProductId", "The scanned product is not the product of this task.");
        if (line.LotId is { } lotId)
        {
            var lotNumber = await s.Db.Set<Lot>().AsNoTracking().Where(l => l.Id == lotId).Select(l => l.LotNumber).SingleAsync(ct);
            if (cmd.ScannedLotNumber != lotNumber)
                throw Coded(ErrorCode.BarcodeConflict, "scannedLotNumber", "Scan the lot of this task.");
        }
        var quantity = cmd.Quantity.Value;
        if (decimal.Round(quantity, product.BasePrecision) != quantity)
            throw AppException.Validation("quantity", $"The quantity exceeds the product's precision of {product.BasePrecision} decimal places.");
        if (quantity > task.RemainingQty)
            throw AppException.Validation("quantity", $"Only {Quantity.Format(task.RemainingQty ?? 0)} remains on this task.");

        // 3. Hard constraints and ranking against current data (INB-005, AT-INB-03).
        var candidates = await PutawayAdvisor.CandidatesAsync(s, task.WarehouseId, product, line.LotId, quantity, ct);
        var target = candidates.FirstOrDefault(b => b.BinId == cmd.TargetBinId);
        if (target is null || !PutawayPlanner.IsSuitable(target, line.QualityStatus))
            throw Coded(ErrorCode.IncompatibleBin, "targetBinId", "This bin cannot take these goods (compatibility, bin kind or capacity).");
        var ranked = PutawayPlanner.Rank(candidates, line.QualityStatus);
        if ((ranked[0].BinId != cmd.TargetBinId || task.Type == TaskType.Exception) && cmd.Reason is null)
            throw AppException.Validation("reason", "Give a reason for using a bin other than the top recommendation.");

        // 4. Move the stock: staging bucket → target bucket with the same identity and quality (never released, INB-006).
        var source = (await s.Db.Database.SqlQuery<SourceRow>($"""
            SELECT id AS "Id", warehouse_id AS "WarehouseId", product_id AS "ProductId", lot_id AS "LotId", serial_id AS "SerialId",
                   quality_status::text AS "Quality", received_at AS "ReceivedAt", on_hand_qty AS "OnHand", reserved_qty AS "Reserved"
            FROM ti.stock_bucket WHERE id = {line.BucketId} FOR UPDATE
            """).ToListAsync(ct)).Single();
        if (source.OnHand - source.Reserved < quantity)
            throw AppException.InvalidState("The staging bucket no longer holds that quantity.");
        await s.Db.Database.ExecuteSqlAsync($"""
            UPDATE ti.stock_bucket SET on_hand_qty = on_hand_qty - {quantity}, updated_at = {s.Now}, version = version + 1 WHERE id = {source.Id}
            """, ct);
        var targetBucket = (await s.Db.Database.SqlQuery<Guid>($"""
            INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, lot_id, serial_id, quality_status, received_at,
                                         on_hand_qty, created_by, created_at, updated_at)
            VALUES ({caller.TenantId}, {source.WarehouseId}, {cmd.TargetBinId}, {source.ProductId}, {source.LotId}, {source.SerialId},
                    {source.Quality}::ti.quality_status, {source.ReceivedAt}, {quantity}, {caller.Subject}, {s.Now}, {s.Now})
            ON CONFLICT (tenant_id, bin_id, product_id, lot_id, serial_id, quality_status)
            DO UPDATE SET on_hand_qty = ti.stock_bucket.on_hand_qty + EXCLUDED.on_hand_qty,
                          received_at = LEAST(ti.stock_bucket.received_at, EXCLUDED.received_at),
                          updated_at = EXCLUDED.updated_at, version = ti.stock_bucket.version + 1
            RETURNING id AS "Value"
            """).ToListAsync(ct)).Single();
        await StockBlocks.EnsureNotBlockedAsync(s, [source.Id, targetBucket], ct);

        var movement = StockMovement.Putaway(caller.TenantId, $"putaway:{task.Id:D}:{Guid.CreateVersion7():D}", source.ProductId, source.LotId, source.SerialId,
            source.Id, targetBucket, quantity, task.Id, line.Id, cmd.Reason, caller.Subject, s.Now, caller.CorrelationId);
        s.Db.Add(movement);
        var before = await DtoAsync(s, task, ct, withRecommendations: false);
        DomainGuard.Run(() => task.Confirm(quantity, caller.Subject, cmd.Reason, s.Now));

        // 5. The receipt closes when every task of its lines is complete (DEC-005).
        await s.Db.SaveChangesAsync(ct);
        var receipt = await s.Db.Set<Receipt>().SingleAsync(r => r.Id == line.ReceiptId, ct);
        var receiptLineIds = s.Db.Set<ReceiptLine>().Where(l => l.ReceiptId == receipt.Id).Select(l => l.Id);
        var open = await s.Db.Set<WarehouseTask>().AnyAsync(t => t.ReceiptLineId != null && receiptLineIds.Contains(t.ReceiptLineId.Value)
            && (t.Status == TaskStatus.Open || t.Status == TaskStatus.InProgress), ct);
        if (!open && receipt.Status == ReceiptStatus.Posted)
        {
            DomainGuard.Run(() => receipt.Close(s.Now));
            s.Audit(new AuditEntry("ReceiptClosed", "Receipt", receipt.Id, receipt.Version, null, new { Status = ReceiptStatus.Posted },
                new { receipt.Status, receipt.ClosedAt }));
        }

        var result = new PutawayConfirmResult(movement.Id, task.Id, Quantity.From(task.RemainingQty ?? 0), task.Status);
        await s.Db.Database.ExecuteSqlAsync($"""
            INSERT INTO ti.document_operation (tenant_id, operation_key, operation_type, result_reference, created_by, created_at)
            VALUES ({caller.TenantId}, {operationKey}, 'Putaway', {JsonSerializer.Serialize(result, JsonDefaults.Options)}::jsonb, {caller.Subject}, {s.Now})
            """, ct);
        s.Audit(new AuditEntry("PutawayConfirmed", "WarehouseTask", task.Id, task.Version, cmd.Reason, before,
            await DtoAsync(s, task, ct, withRecommendations: false)));
        s.Publish("StockPutAway", new { taskId = task.Id, movementId = movement.Id, targetBinId = cmd.TargetBinId, quantity = Quantity.Format(quantity) });
        await s.Db.SaveChangesAsync(ct);
        return result;
    }

    private static async Task<WarehouseTaskDto> DtoAsync(ITenantSession s, WarehouseTask t, CancellationToken ct, bool withRecommendations = true)
    {
        var line = t.ReceiptLineId is { } lid ? await s.Db.Set<ReceiptLine>().AsNoTracking().SingleAsync(l => l.Id == lid, ct) : null;
        IReadOnlyList<BinRecommendationDto> recommendations = [];
        if (withRecommendations && t.Type == TaskType.Putaway && t.IsOpen && line is not null)
        {
            var product = await s.Db.Set<Product>().AsNoTracking().SingleAsync(p => p.Id == line.ProductId, ct);
            recommendations = PutawayPlanner.Rank(await PutawayAdvisor.CandidatesAsync(s, t.WarehouseId, product, t.LotId, t.RemainingQty ?? 0, ct),
                line.QualityStatus).Select(r => new BinRecommendationDto(r.BinId, r.Code, r.Rank, r.Reason)).ToList();
        }
        return new WarehouseTaskDto(t.Id, t.Version, t.CreatedAt, t.CreatedBy, t.UpdatedAt, t.Type, t.Status, t.WarehouseId, DocumentType.Receipt,
            line?.ReceiptId ?? Guid.Empty, t.ReceiptLineId, t.ProductId ?? Guid.Empty, t.LotId, t.SourceBinId, Quantity.From(t.TaskQty ?? 0),
            Quantity.From(t.RemainingQty ?? 0), recommendations, t.Reason, t.Resolution, t.CompletedAt, t.CompletedBy);
    }

    private static AppException Coded(ErrorCode code, string field, string message) =>
        new(code, message, [new FieldError(field, code, message)]);

    private sealed record SourceRow(Guid Id, Guid WarehouseId, Guid ProductId, Guid? LotId, Guid? SerialId, string Quality, DateTimeOffset ReceivedAt,
        decimal OnHand, decimal Reserved);
}

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Inbound;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Operations;

public sealed record BinTransfer(Guid SourceBucketId, Guid TargetBinId, Quantity Quantity, IReadOnlyList<string>? SerialNumbers, string? Reason);

public sealed record BinTransferResult(Guid MovementId, Guid SourceBucketId, Guid TargetBucketId, int SourceVersion, int TargetVersion);

public sealed class BinTransferValidator : AbstractValidator<BinTransfer>
{
    public BinTransferValidator()
    {
        RuleFor(x => x.SourceBucketId).NotEmpty();
        RuleFor(x => x.TargetBinId).NotEmpty();
        RuleFor(x => x.Quantity).Must(Quantity.IsValidPositive).WithMessage(Quantity.PositiveMessage);
        RuleFor(x => x.SerialNumbers!).Must(TransferRules.ValidSerials).WithMessage(TransferRules.SerialMessage).When(x => x.SerialNumbers is not null);
        RuleFor(x => x.Reason!).ValidReason().When(x => x.Reason is not null);
    }
}

/// <summary>
/// Atomic move between bins of one warehouse (EXC-002, STK-003, DEC-058). Unreserved stock only; lot, serial and quality
/// are preserved. Lock order: source and target buckets (by id) → audit stream.
/// </summary>
public sealed class BinTransferService(ITenantUnitOfWork uow, ICallerAccessor callers, IValidator<BinTransfer> validator)
{
    public Task<CommandOutcome> TransferAsync(Guid idempotencyKey, BinTransfer cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.PutawayConfirm);
        caller.RequireMutable();
        validator.ValidateOrThrow(cmd);
        var payload = JsonSerializer.SerializeToNode(cmd, JsonDefaults.Options);
        var operationKey = $"bin-transfer:{caller.Subject}:{idempotencyKey:D}";

        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, "POST /inventory/bin-transfers", payload), async (s, c) =>
        {
            var source = await s.Db.Set<StockBucket>().AsNoTracking().SingleOrDefaultAsync(b => b.Id == cmd.SourceBucketId, c)
                ?? throw AppException.NotFound("Stock bucket");
            caller.RequireWarehouse(Permissions.PutawayConfirm, source.WarehouseId);
            var earlier = await s.Db.Database.SqlQuery<string>(
                $"SELECT result_reference::text AS \"Value\" FROM ti.document_operation WHERE operation_key = {operationKey}").ToListAsync(c);
            if (earlier.Count == 1)
                return new CommandOutcome(200, JsonNode.Parse(earlier[0]));

            var sourceBin = await s.Db.Set<Bin>().AsNoTracking().SingleAsync(b => b.Id == source.BinId, c);
            if (sourceBin.Kind == BinKind.Transit)
                throw AppException.Validation("sourceBucketId", "Transit stock moves only through its transfer (DEC-010).");
            var product = await s.Db.Set<Product>().AsNoTracking().SingleAsync(p => p.Id == source.ProductId, c);
            var quantity = cmd.Quantity.Value;
            if (decimal.Round(quantity, product.BasePrecision) != quantity)
                throw AppException.Validation("quantity", $"The quantity exceeds the product's precision of {product.BasePrecision} decimal places.");
            await TransferRules.CheckSerialsAsync(s, product, source.SerialId, quantity, cmd.SerialNumbers, "serialNumbers", c);
            BinKind[] kinds = source.QualityStatus == QualityStatus.Available ? [BinKind.Storage, BinKind.Staging] : [BinKind.Storage, BinKind.Quarantine];
            var target = await StockLocations.RequireBinAsync(s, cmd.TargetBinId, source.WarehouseId, product, kinds, "targetBinId", c);
            if (target.Id == source.BinId)
                throw AppException.Validation("targetBinId", "Choose a different bin.");

            var targetBucket = await StockLocations.MatchingBucketAsync(s, caller.TenantId, caller.Subject, source.WarehouseId, target.Id, source.ProductId,
                source.LotId, source.SerialId, source.QualityStatus.ToString(), source.ReceivedAt, c);
            var locked = await StockLocations.LockBucketsAsync(s, [source.Id, targetBucket], c);
            if (locked[source.Id].OnHand - locked[source.Id].Reserved < quantity)
                throw new AppException(ErrorCode.InsufficientStock,
                    $"Only {Quantity.Format(locked[source.Id].OnHand - locked[source.Id].Reserved)} is unreserved in this bucket.");
            await StockBlocks.EnsureNotBlockedAsync(s, [source.Id, targetBucket], c);
            await StockLocations.MoveOnHandAsync(s, source.Id, targetBucket, quantity, c);

            var operationId = Guid.CreateVersion7();
            var movement = StockMovement.BinTransfer(caller.TenantId, $"bin-transfer:{operationId:D}", source.ProductId, source.LotId, source.SerialId,
                source.Id, targetBucket, quantity, operationId, cmd.Reason, caller.Subject, s.Now, caller.CorrelationId);
            s.Db.Add(movement);
            await s.Db.SaveChangesAsync(c);
            var versions = (await s.Db.Database.SqlQuery<VersionRow>($"""
                SELECT id AS "Id", version AS "Version" FROM ti.stock_bucket WHERE id IN ({source.Id}, {targetBucket})
                """).ToListAsync(c)).ToDictionary(v => v.Id, v => v.Version);
            var result = new BinTransferResult(movement.Id, source.Id, targetBucket, versions[source.Id], versions[targetBucket]);
            await s.Db.Database.ExecuteSqlAsync($"""
                INSERT INTO ti.document_operation (tenant_id, operation_key, operation_type, result_reference, created_by, created_at)
                VALUES ({caller.TenantId}, {operationKey}, 'BinTransfer', {JsonSerializer.Serialize(result, JsonDefaults.Options)}::jsonb, {caller.Subject}, {s.Now})
                """, c);
            s.Audit(new AuditEntry("StockMovedBetweenBins", "StockMovement", movement.Id, 1, cmd.Reason, null, result));
            s.Publish("StockMovedBetweenBins", new { movementId = movement.Id, sourceBucketId = source.Id, targetBucketId = targetBucket, quantity = Quantity.Format(quantity) });
            await s.Db.SaveChangesAsync(c);
            return new CommandOutcome(200, JsonSerializer.SerializeToNode(result, JsonDefaults.Options));
        }, ct);
    }

    private sealed record VersionRow(Guid Id, int Version);
}

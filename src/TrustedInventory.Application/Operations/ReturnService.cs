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
using TrustedInventory.Domain.Operations;
using TrustedInventory.Domain.Outbound;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Operations;

public sealed record ReturnDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, Guid ShipmentLineId, Guid WarehouseId, Quantity Quantity,
    Guid QuarantineBinId, IReadOnlyList<string> SerialNumbers, string Reason, ReturnStatus Status, Guid BucketId, ReturnDisposition? Disposition,
    Quantity InspectedQuantity, Guid? ApprovalId) : IVersioned;

public sealed record CreateReturn(Guid ShipmentLineId, Quantity Quantity, Guid QuarantineBinId, IReadOnlyList<string>? SerialNumbers, string Reason);

public sealed record InspectReturn(ReturnDisposition Disposition, Quantity Quantity, Guid? TargetBinId, Guid? ApprovalId, string Reason);

public sealed record ReturnFilter(Guid? WarehouseId, ReturnStatus? Status);

public sealed class CreateReturnValidator : AbstractValidator<CreateReturn>
{
    public CreateReturnValidator()
    {
        RuleFor(x => x.ShipmentLineId).NotEmpty();
        RuleFor(x => x.QuarantineBinId).NotEmpty();
        RuleFor(x => x.Quantity).Must(Quantity.IsValidPositive).WithMessage(Quantity.PositiveMessage);
        RuleFor(x => x.Reason).ValidReason();
        RuleFor(x => x.SerialNumbers!).Must(TransferRules.ValidSerials).WithMessage(TransferRules.SerialMessage).When(x => x.SerialNumbers is not null);
    }
}

public sealed class InspectReturnValidator : AbstractValidator<InspectReturn>
{
    public InspectReturnValidator()
    {
        RuleFor(x => x.Quantity).Must(Quantity.IsValidPositive).WithMessage(Quantity.PositiveMessage);
        RuleFor(x => x.Reason).ValidReason();
        RuleFor(x => x.TargetBinId).NotEmpty().When(x => x.Disposition == ReturnDisposition.Release).WithMessage("Name the bin released goods go to.");
    }
}

/// <summary>
/// Customer returns (EXC-005, AT-EXC-03, DEC-060): received into Quarantine against a shipment line, never beyond what
/// was shipped less earlier returns, then inspected and dispositioned. Lock order: shipment line or return row →
/// buckets (by id) → audit stream.
/// </summary>
public sealed class ReturnService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IValidator<CreateReturn> createValidator,
    IValidator<InspectReturn> inspectValidator)
{
    public Task<CommandOutcome> CreateAsync(Guid idempotencyKey, CreateReturn cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.ReturnsManage);
        caller.RequireMutable();
        createValidator.ValidateOrThrow(cmd);
        var payload = JsonSerializer.SerializeToNode(cmd, JsonDefaults.Options);
        var operationKey = $"return:{caller.Subject}:{idempotencyKey:D}";

        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, "POST /returns", payload), async (s, c) =>
        {
            // 1. The shipment line row serialises returns against it (AT-EXC-03).
            var locked = await s.Db.Database.SqlQuery<ShipmentLineRow>($"""
                SELECT l.id AS "Id", l.product_id AS "ProductId", l.lot_id AS "LotId", l.serial_id AS "SerialId", l.base_qty AS "Shipped",
                       l.returned_qty AS "Returned", s.warehouse_id AS "WarehouseId"
                FROM ti.shipment_line l JOIN ti.shipment s ON s.tenant_id = l.tenant_id AND s.id = l.shipment_id
                WHERE l.id = {cmd.ShipmentLineId} FOR UPDATE OF l
                """).ToListAsync(c);
            var line = locked.SingleOrDefault() ?? throw AppException.NotFound("Shipment line");
            caller.RequireWarehouse(Permissions.ReturnsManage, line.WarehouseId);
            var earlier = await s.Db.Database.SqlQuery<string>(
                $"SELECT result_reference::text AS \"Value\" FROM ti.document_operation WHERE operation_key = {operationKey}").ToListAsync(c);
            if (earlier.Count == 1)
                return new CommandOutcome(201, JsonNode.Parse(earlier[0]));

            var product = await s.Db.Set<Product>().AsNoTracking().SingleAsync(p => p.Id == line.ProductId, c);
            var quantity = cmd.Quantity.Value;
            if (decimal.Round(quantity, product.BasePrecision) != quantity)
                throw AppException.Validation("quantity", $"The quantity exceeds the product's precision of {product.BasePrecision} decimal places.");
            if (quantity > line.Shipped - line.Returned)
                throw new AppException(ErrorCode.ReturnExceedsShipped,
                    $"Only {Quantity.Format(line.Shipped - line.Returned)} of this shipment line is still unreturned (EXC-005).",
                    [new FieldError("quantity", ErrorCode.ReturnExceedsShipped, "The quantity exceeds what was shipped less earlier returns.")]);
            await TransferRules.CheckSerialsAsync(s, product, line.SerialId, quantity, cmd.SerialNumbers, "serialNumbers", c);
            await StockLocations.RequireBinAsync(s, cmd.QuarantineBinId, line.WarehouseId, product, [BinKind.Quarantine], "quarantineBinId", c);

            // 2. Every return enters Quarantine, general goods included (EXC-005).
            var bucket = await StockLocations.MatchingBucketAsync(s, caller.TenantId, caller.Subject, line.WarehouseId, cmd.QuarantineBinId, line.ProductId,
                line.LotId, line.SerialId, nameof(QualityStatus.Quarantine), s.Now, c);
            await StockLocations.LockBucketsAsync(s, [bucket], c);
            await StockBlocks.EnsureNotBlockedAsync(s, [bucket], c);
            await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.stock_bucket SET on_hand_qty = on_hand_qty + {quantity}, updated_at = {s.Now}, version = version + 1 WHERE id = {bucket}
                """, c);
            await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.shipment_line SET returned_qty = returned_qty + {quantity}, updated_at = {s.Now}, version = version + 1 WHERE id = {line.Id}
                """, c);
            var returnId = Guid.CreateVersion7();
            var movement = StockMovement.Return(MovementType.ReturnReceipt, caller.TenantId, $"return:{returnId:D}:receipt", line.ProductId, line.LotId,
                line.SerialId, null, bucket, quantity, returnId, cmd.Reason, caller.Subject, s.Now, caller.CorrelationId);
            s.Db.Add(movement);
            var ret = DomainGuard.Run(() => CustomerReturn.Receive(returnId, caller.TenantId, line.WarehouseId, line.Id, line.ProductId, line.SerialId, quantity,
                cmd.QuarantineBinId, bucket, movement.Id, cmd.Reason, caller.Subject, s.Now));
            s.Db.Add(ret);
            await s.Db.SaveChangesAsync(c);

            var dto = await DtoAsync(s, ret, c);
            await RecordAsync(s, caller, operationKey, "ReturnReceipt", dto, c);
            s.Audit(new AuditEntry("ReturnReceived", "CustomerReturn", ret.Id, ret.Version, cmd.Reason, null, dto));
            s.Publish("ReturnReceived", new { returnId = ret.Id, shipmentLineId = line.Id, movementId = movement.Id, quantity = Quantity.Format(quantity) });
            await s.Db.SaveChangesAsync(c);
            return new CommandOutcome(201, JsonSerializer.SerializeToNode(dto, JsonDefaults.Options));
        }, ct);
    }

    /// <summary>The inspector's decision for part or all of the returned quantity (EXC-005, DEC-060).</summary>
    public Task<CommandOutcome> InspectAsync(Guid idempotencyKey, Guid id, int expectedVersion, InspectReturn cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.ReturnsInspect);
        caller.RequireMutable();
        inspectValidator.ValidateOrThrow(cmd);
        var payload = JsonSerializer.SerializeToNode(new { id, expectedVersion, cmd }, JsonDefaults.Options);
        var operationKey = $"return-inspect:{caller.Subject}:{idempotencyKey:D}";

        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, "POST /returns/{id}/inspect", payload), async (s, c) =>
        {
            if ((await s.Db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.customer_return WHERE id = {id} FOR UPDATE").ToListAsync(c)).Count == 0)
                throw AppException.NotFound("Return");
            var ret = await s.Db.Set<CustomerReturn>().SingleAsync(r => r.Id == id, c);
            caller.RequireWarehouse(Permissions.ReturnsInspect, ret.WarehouseId);
            var earlier = await s.Db.Database.SqlQuery<string>(
                $"SELECT result_reference::text AS \"Value\" FROM ti.document_operation WHERE operation_key = {operationKey}").ToListAsync(c);
            if (earlier.Count == 1)
                return new CommandOutcome(200, JsonNode.Parse(earlier[0]));
            Versioning.EnsureVersion(ret.Version, expectedVersion);
            if (ret.Status != ReturnStatus.Received)
                throw AppException.InvalidState("This return is already fully inspected.");
            var product = await s.Db.Set<Product>().AsNoTracking().SingleAsync(p => p.Id == ret.ProductId, c);
            var quantity = cmd.Quantity.Value;
            if (decimal.Round(quantity, product.BasePrecision) != quantity)
                throw AppException.Validation("quantity", $"The quantity exceeds the product's precision of {product.BasePrecision} decimal places.");
            if (quantity > ret.RemainingToInspect)
                throw AppException.Validation("quantity", $"Only {Quantity.Format(ret.RemainingToInspect)} of this return remains to inspect.");
            if (cmd.Disposition == ReturnDisposition.Release && product.HandlingProfile == HandlingProfile.Medical)
                throw AppException.Validation("disposition", "Medical returns are released only through QA quality release with an approval, which arrives with M5.");

            var source = await s.Db.Set<StockBucket>().AsNoTracking().SingleAsync(b => b.Id == ret.BucketId, c);
            Guid? target = cmd.Disposition switch
            {
                ReturnDisposition.Release => await ReleaseTargetAsync(s, caller, ret, source, product, cmd.TargetBinId!.Value, c),
                ReturnDisposition.Damaged => await StockLocations.MatchingBucketAsync(s, caller.TenantId, caller.Subject, ret.WarehouseId, source.BinId,
                    source.ProductId, source.LotId, source.SerialId, nameof(QualityStatus.Damaged), source.ReceivedAt, c),
                _ => null,
            };
            var locked = await StockLocations.LockBucketsAsync(s, target is { } t ? [source.Id, t] : [source.Id], c);
            if (locked[source.Id].OnHand - locked[source.Id].Reserved < quantity)
                throw AppException.InvalidState("The quarantine bucket no longer holds the returned quantity; run reconciliation (OPS-001).");
            await StockBlocks.EnsureNotBlockedAsync(s, locked.Keys, c);
            await StockLocations.MoveOnHandAsync(s, source.Id, target, quantity, c);

            var type = cmd.Disposition switch
            {
                ReturnDisposition.SupplierReturn => MovementType.SupplierReturn,
                ReturnDisposition.Disposal => MovementType.Disposal,
                _ => MovementType.QualityChange,
            };
            var movement = StockMovement.Return(type, caller.TenantId, $"return:{ret.Id:D}:{Guid.CreateVersion7():D}", source.ProductId, source.LotId,
                source.SerialId, source.Id, target, quantity, ret.Id, $"{cmd.Disposition}: {cmd.Reason}", caller.Subject, s.Now, caller.CorrelationId);
            s.Db.Add(movement);
            var before = await DtoAsync(s, ret, c);
            DomainGuard.Run(() => ret.Inspect(cmd.Disposition, quantity, s.Now));
            await s.Db.SaveChangesAsync(c);
            await s.Db.Database.ExecuteSqlAsync($"""
                INSERT INTO ti.customer_return_inspection (tenant_id, return_id, disposition, qty, reason, movement_id, approval_id, created_at, created_by)
                VALUES ({caller.TenantId}, {ret.Id}, {cmd.Disposition.ToString()}::ti.return_disposition, {quantity}, {cmd.Reason}, {movement.Id}, NULL,
                        {s.Now}, {caller.Subject})
                """, c);

            var dto = await DtoAsync(s, ret, c);
            await RecordAsync(s, caller, operationKey, "ReturnInspection", dto, c);
            s.Audit(new AuditEntry("ReturnInspected", "CustomerReturn", ret.Id, ret.Version, cmd.Reason, before,
                new { @return = dto, disposition = cmd.Disposition.ToString(), quantity = Quantity.Format(quantity), movementId = movement.Id }));
            s.Publish("ReturnInspected", new { returnId = ret.Id, disposition = cmd.Disposition.ToString(), movementId = movement.Id });
            await s.Db.SaveChangesAsync(c);
            return new CommandOutcome(200, JsonSerializer.SerializeToNode(dto, JsonDefaults.Options));
        }, ct);
    }

    public Task<ReturnDto> GetAsync(Guid id, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var ret = await s.Db.Set<CustomerReturn>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, c) ?? throw AppException.NotFound("Return");
            caller.RequireWarehouse(Permissions.StockRead, ret.WarehouseId);
            return await DtoAsync(s, ret, c);
        }, ct);
    }

    public Task<Page<ReturnDto>> ListAsync(ReturnFilter filter, PageRequest page, CancellationToken ct)
    {
        var scope = callers.Caller.WarehouseFilter(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var query = s.Db.Set<CustomerReturn>().AsNoTracking();
            if (scope is not null)
                query = query.Where(r => scope.Contains(r.WarehouseId));
            if (filter.WarehouseId is { } w)
                query = query.Where(r => r.WarehouseId == w);
            if (filter.Status is { } status)
                query = query.Where(r => r.Status == status);
            var rows = await query.ToPageAsync(page, $"returns|{filter.WarehouseId}|{filter.Status}", cursors, r => r, c);
            var dtos = new List<ReturnDto>();
            foreach (var ret in rows.Items)
                dtos.Add(await DtoAsync(s, ret, c));
            return new Page<ReturnDto>(dtos, rows.NextCursor);
        }, ct);
    }

    /// <summary>Released goods go to a suitable Storage bin as Available (DEC-012, DEC-060).</summary>
    private static async Task<Guid> ReleaseTargetAsync(ITenantSession s, CallerContext caller, CustomerReturn ret, StockBucket source, Product product, Guid binId,
        CancellationToken ct)
    {
        await StockLocations.RequireBinAsync(s, binId, ret.WarehouseId, product, [BinKind.Storage], "targetBinId", ct);
        return await StockLocations.MatchingBucketAsync(s, caller.TenantId, caller.Subject, ret.WarehouseId, binId, source.ProductId, source.LotId,
            source.SerialId, nameof(QualityStatus.Available), source.ReceivedAt, ct);
    }

    private static Task<int> RecordAsync(ITenantSession s, CallerContext caller, string operationKey, string type, ReturnDto dto, CancellationToken ct) =>
        s.Db.Database.ExecuteSqlAsync($"""
            INSERT INTO ti.document_operation (tenant_id, operation_key, operation_type, result_reference, created_by, created_at)
            VALUES ({caller.TenantId}, {operationKey}, {type}, {JsonSerializer.Serialize(dto, JsonDefaults.Options)}::jsonb, {caller.Subject}, {s.Now})
            """, ct);

    private static async Task<ReturnDto> DtoAsync(ITenantSession s, CustomerReturn r, CancellationToken ct)
    {
        IReadOnlyList<string> serials = r.SerialId is { } sid
            ? await s.Db.Set<Serial>().AsNoTracking().Where(x => x.Id == sid).Select(x => x.SerialNumber).ToListAsync(ct)
            : [];
        return new ReturnDto(r.Id, r.Version, r.CreatedAt, r.CreatedBy, r.UpdatedAt, r.ShipmentLineId, r.WarehouseId, Quantity.From(r.ReturnedQty),
            r.QuarantineBinId, serials, r.Reason, r.Status, r.BucketId, r.Disposition, Quantity.From(r.InspectedQty), r.ApprovalId);
    }

    private sealed record ShipmentLineRow(Guid Id, Guid ProductId, Guid? LotId, Guid? SerialId, decimal Shipped, decimal Returned, Guid WarehouseId);
}

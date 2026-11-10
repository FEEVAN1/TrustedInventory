using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Administration;
using TrustedInventory.Application.Audit;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Inbound;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Operations;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Operations;

public sealed record TransferLineDto(Guid Id, Guid ProductId, Guid? LotId, Quantity Quantity, Quantity DispatchedQuantity, Quantity ReceivedQuantity,
    Quantity InTransitQuantity, Quantity DisposedQuantity);

public sealed record TransferDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, Guid SourceWarehouseId, Guid DestinationWarehouseId,
    string Reference, TransferStatus Status, DateTimeOffset? DispatchedAt, DateTimeOffset? ReceivedAt, IReadOnlyList<TransferLineDto> Lines) : IVersioned;

public sealed record TransferLineInput(Guid ProductId, Guid? LotId, Quantity Quantity);

public sealed record CreateTransfer(Guid SourceWarehouseId, Guid DestinationWarehouseId, string Reference, IReadOnlyList<TransferLineInput> Lines);

public sealed record TransferDispatchLine(Guid LineId, Guid SourceBucketId, Quantity Quantity, IReadOnlyList<string>? SerialNumbers);

public sealed record TransferDispatch(IReadOnlyList<TransferDispatchLine> Lines);

public sealed record TransferReceiveLine(Guid LineId, Quantity Quantity, Guid StagingBinId, IReadOnlyList<string>? SerialNumbers);

public sealed record TransferReceive(IReadOnlyList<TransferReceiveLine> Lines);

public sealed record TransitDisposition(Guid LineId, Quantity Quantity, TransitDispositionType Disposition, string Reason, Guid ApprovalId);

public sealed record TransferFilter(Guid? SourceWarehouseId, Guid? DestinationWarehouseId, TransferStatus? Status);

public sealed class CreateTransferValidator : AbstractValidator<CreateTransfer>
{
    public CreateTransferValidator()
    {
        RuleFor(x => x.SourceWarehouseId).NotEmpty();
        RuleFor(x => x.DestinationWarehouseId).NotEmpty().NotEqual(x => x.SourceWarehouseId).WithMessage("Transfer to a different warehouse.");
        RuleFor(x => x.Reference).ValidCode();
        RuleFor(x => x.Lines).NotNull().Must(l => l is { Count: >= 1 and <= 500 }).WithMessage("Send 1 to 500 lines.")
            .Must(l => l is null || l.Select(x => (x.ProductId, x.LotId)).Distinct().Count() == l.Count).WithMessage("Each product and lot appears once.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ProductId).NotEmpty();
            line.RuleFor(l => l.Quantity).Must(Quantity.IsValidPositive).WithMessage(Quantity.PositiveMessage);
        });
    }
}

public sealed class TransferDispatchValidator : AbstractValidator<TransferDispatch>
{
    public TransferDispatchValidator()
    {
        RuleFor(x => x.Lines).NotNull().Must(l => l is { Count: >= 1 and <= 1000 }).WithMessage("Send 1 to 1000 lines.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.LineId).NotEmpty();
            line.RuleFor(l => l.SourceBucketId).NotEmpty();
            line.RuleFor(l => l.Quantity).Must(Quantity.IsValidPositive).WithMessage(Quantity.PositiveMessage);
            line.RuleFor(l => l.SerialNumbers!).Must(TransferRules.ValidSerials).WithMessage(TransferRules.SerialMessage).When(l => l.SerialNumbers is not null);
        });
    }
}

public sealed class TransferReceiveValidator : AbstractValidator<TransferReceive>
{
    public TransferReceiveValidator()
    {
        RuleFor(x => x.Lines).NotNull().Must(l => l is { Count: >= 1 and <= 1000 }).WithMessage("Send 1 to 1000 lines.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.LineId).NotEmpty();
            line.RuleFor(l => l.StagingBinId).NotEmpty();
            line.RuleFor(l => l.Quantity).Must(Quantity.IsValidPositive).WithMessage(Quantity.PositiveMessage);
            line.RuleFor(l => l.SerialNumbers!).Must(TransferRules.ValidSerials).WithMessage(TransferRules.SerialMessage).When(l => l.SerialNumbers is not null);
        });
    }
}

public sealed class TransitDispositionValidator : AbstractValidator<TransitDisposition>
{
    public TransitDispositionValidator()
    {
        RuleFor(x => x.LineId).NotEmpty();
        RuleFor(x => x.ApprovalId).NotEmpty();
        RuleFor(x => x.Quantity).Must(Quantity.IsValidPositive).WithMessage(Quantity.PositiveMessage);
        RuleFor(x => x.Reason).ValidReason();
    }
}

/// <summary>Transfer rules shared with the approval registry (EXC-001, STK-003, DEC-058).</summary>
internal static class TransferRules
{
    public const string SerialMessage = "Send at most 10000 distinct serial numbers of 1 to 100 characters.";

    public static bool ValidSerials(IReadOnlyList<string> serials) =>
        serials.Count <= 10000 && serials.All(v => v is { Length: >= 1 and <= 100 }) && serials.Distinct(StringComparer.Ordinal).Count() == serials.Count;

    /// <summary>Locks the transfer row, then its lines in line order.</summary>
    public static async Task<(Transfer Transfer, List<TransferLine> Lines)> LockAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        if ((await s.Db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.transfer WHERE id = {id} FOR UPDATE").ToListAsync(ct)).Count == 0)
            throw AppException.NotFound("Transfer");
        await s.Db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.transfer_line WHERE transfer_id = {id} ORDER BY line_number FOR UPDATE").ToListAsync(ct);
        var transfer = await s.Db.Set<Transfer>().SingleAsync(t => t.Id == id, ct);
        var lines = await s.Db.Set<TransferLine>().Where(l => l.TransferId == id).OrderBy(l => l.LineNumber).ToListAsync(ct);
        return (transfer, lines);
    }

    /// <summary>SHA-256 of the canonical transfer state a transit disposition approval binds (IAM-003, DEC-058).</summary>
    public static byte[] Digest(Transfer t, IEnumerable<TransferLine> lines)
    {
        var body = new JsonObject
        {
            ["id"] = t.Id.ToString(), ["sourceWarehouseId"] = t.SourceWarehouseId.ToString(), ["destinationWarehouseId"] = t.DestinationWarehouseId.ToString(),
            ["reference"] = t.Reference, ["status"] = t.Status.ToString(),
            ["lines"] = new JsonArray(lines.OrderBy(l => l.LineNumber).Select(l => (JsonNode)new JsonObject
            {
                ["id"] = l.Id.ToString(), ["productId"] = l.ProductId.ToString(), ["lotId"] = l.LotId?.ToString(),
                ["requested"] = Quantity.Format(l.RequestedQty), ["dispatched"] = Quantity.Format(l.DispatchedQty),
                ["received"] = Quantity.Format(l.ReceivedQty), ["disposed"] = Quantity.Format(l.DisposedQty),
            }).ToArray()),
        };
        return SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(body)));
    }

    /// <summary>Inventory signing authority, plus quality authority when a line is a medical product (SOD-2).</summary>
    public static async Task<IReadOnlyList<string>> SignPermissionsAsync(ITenantSession s, IEnumerable<TransferLine> lines, CancellationToken ct)
    {
        var products = lines.Select(l => l.ProductId).Distinct().ToList();
        var medical = await s.Db.Set<Product>().AsNoTracking().AnyAsync(p => products.Contains(p.Id) && p.HandlingProfile == HandlingProfile.Medical, ct);
        return medical ? [Permissions.ApprovalsSignInventory, Permissions.ApprovalsSignQuality] : [Permissions.ApprovalsSignInventory];
    }

    public static async Task CheckSerialsAsync(ITenantSession s, Product product, Guid? serialId, decimal quantity, IReadOnlyList<string>? scanned,
        string field, CancellationToken ct)
    {
        if (!product.IsSerialTracked)
        {
            if (scanned is { Count: > 0 })
                throw AppException.Validation(field, "This product is not serial-tracked.");
            return;
        }
        var expected = serialId is { } sid ? await s.Db.Set<Serial>().AsNoTracking().Where(x => x.Id == sid).Select(x => x.SerialNumber).SingleAsync(ct) : null;
        if (scanned is null || scanned.Count != quantity || expected is null || scanned.Any(v => v != expected))
            throw AppException.Validation(field, "Name the serial number of the unit being moved.");
    }

    /// <summary>
    /// A line's in-transit stock by transit bucket, from the ledger: its dispatches minus its receipts and dispositions
    /// (DEC-058). Oldest bucket first.
    /// </summary>
    public static Task<List<TransitStock>> InTransitAsync(ITenantSession s, Guid lineId, CancellationToken ct) =>
        s.Db.Database.SqlQuery<TransitStock>($"""
            SELECT b.id AS "BucketId", b.lot_id AS "LotId", b.serial_id AS "SerialId", b.quality_status::text AS "Quality", b.received_at AS "ReceivedAt",
                   x.qty AS "Quantity"
            FROM (SELECT bucket_id, sum(q) AS qty FROM (
                    SELECT destination_bucket_id AS bucket_id, base_qty AS q FROM ti.stock_movement
                    WHERE document_type = 'Transfer' AND document_line_id = {lineId} AND movement_type = 'TransferDispatch'
                    UNION ALL
                    SELECT source_bucket_id, -base_qty FROM ti.stock_movement
                    WHERE document_type = 'Transfer' AND document_line_id = {lineId} AND movement_type IN ('TransferReceipt', 'TransitDisposition')
                  ) m GROUP BY bucket_id HAVING sum(q) > 0) x
            JOIN ti.stock_bucket b ON b.id = x.bucket_id
            ORDER BY b.received_at, b.id
            """).ToListAsync(ct);

    /// <summary>Takes <paramref name="quantity"/> from the line's transit buckets, oldest first, or the named serials.</summary>
    public static List<(TransitStock Stock, decimal Quantity)> Draw(IReadOnlyList<TransitStock> stock, decimal quantity, IReadOnlyList<Guid>? serialIds)
    {
        var draws = new List<(TransitStock, decimal)>();
        var left = quantity;
        foreach (var bucket in stock.Where(b => serialIds is null || (b.SerialId is { } sid && serialIds.Contains(sid))))
        {
            var take = Math.Min(left, bucket.Quantity);
            if (take <= 0)
                break;
            draws.Add((bucket, take));
            left -= take;
        }
        return left == 0 ? draws : throw AppException.Validation("quantity", $"Only {Quantity.Format(quantity - left)} of that is in transit on the line.");
    }

    internal sealed record TransitStock(Guid BucketId, Guid? LotId, Guid? SerialId, string Quality, DateTimeOffset ReceivedAt, decimal Quantity);
}

/// <summary>
/// Warehouse transfers (EXC-001, EXC-002, STK-003, AT-EXC-01, DEC-058): Draft → Approved → dispatch into the source
/// Transit bin → receipt into a destination staging bin; lost or damaged transit stock leaves only through an approved
/// disposition. Lock order: transfer row → lines → buckets (by id) → audit stream.
/// </summary>
public sealed class TransferService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IValidator<CreateTransfer> createValidator,
    IValidator<TransferDispatch> dispatchValidator,
    IValidator<TransferReceive> receiveValidator,
    IValidator<TransitDisposition> dispositionValidator,
    IValidator<ReasonRequest> reasonValidator)
{
    public Task<TransferDto> CreateAsync(CreateTransfer cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.TransfersManage);
        caller.RequireMutable();
        createValidator.ValidateOrThrow(cmd);
        caller.RequireWarehouse(Permissions.TransfersManage, cmd.SourceWarehouseId);
        return uow.WriteAsync(async (s, c) =>
        {
            var warehouses = await s.Db.Set<Warehouse>().AsNoTracking()
                .Where(w => w.Id == cmd.SourceWarehouseId || w.Id == cmd.DestinationWarehouseId).ToListAsync(c);
            if (warehouses.SingleOrDefault(w => w.Id == cmd.SourceWarehouseId) is not { Active: true })
                throw AppException.Validation("sourceWarehouseId", "Use an active warehouse.");
            if (warehouses.SingleOrDefault(w => w.Id == cmd.DestinationWarehouseId) is not { Active: true })
                throw AppException.Validation("destinationWarehouseId", "Use an active warehouse.");
            if (await s.Db.Set<Transfer>().AnyAsync(t => t.Reference == cmd.Reference, c))
                throw AppException.Validation("reference", "A transfer with this reference already exists.");
            var transfer = DomainGuard.Run(() => Transfer.Create(caller.TenantId, cmd.SourceWarehouseId, cmd.DestinationWarehouseId, cmd.Reference, caller.Subject, s.Now));
            s.Db.Add(transfer);
            var lines = new List<TransferLine>();
            for (var i = 0; i < cmd.Lines.Count; i++)
            {
                var input = cmd.Lines[i];
                var product = await s.Db.Set<Product>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == input.ProductId, c)
                    ?? throw AppException.Validation($"lines[{i}].productId", "The product does not exist.");
                if (input.LotId is { } lid && !await s.Db.Set<Lot>().AnyAsync(l => l.Id == lid && l.ProductId == product.Id, c))
                    throw AppException.Validation($"lines[{i}].lotId", "The lot is not a lot of this product.");
                var quantity = input.Quantity.Value;
                if (decimal.Round(quantity, product.BasePrecision) != quantity)
                    throw AppException.Validation($"lines[{i}].quantity", $"The quantity exceeds the product's precision of {product.BasePrecision} decimal places.");
                var line = TransferLine.Create(caller.TenantId, transfer.Id, i + 1, product.Id, input.LotId, quantity, caller.Subject, s.Now);
                s.Db.Add(line);
                lines.Add(line);
            }
            var dto = Dto(transfer, lines);
            s.Audit(new AuditEntry("TransferCreated", "Transfer", transfer.Id, transfer.Version, null, null, dto));
            await s.Db.SaveChangesAsync(c);
            return dto;
        }, ct);
    }

    public Task<TransferDto> ApproveAsync(Guid id, int expectedVersion, CancellationToken ct) =>
        DecideAsync(id, expectedVersion, "TransferApproved", null, (t, _, actor, now) => t.Approve(actor, now), ct);

    public Task<TransferDto> CancelAsync(Guid id, int expectedVersion, ReasonRequest cmd, CancellationToken ct)
    {
        reasonValidator.ValidateOrThrow(cmd);
        return DecideAsync(id, expectedVersion, "TransferCancelled", cmd.Reason, (t, lines, actor, now) => t.Cancel(actor, lines, now), ct);
    }

    /// <summary>Source buckets → the source warehouse's Transit bin; tenant stock unchanged (STK-003, DEC-010).</summary>
    public Task<CommandOutcome> DispatchAsync(Guid idempotencyKey, Guid id, int expectedVersion, TransferDispatch cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.TransfersManage);
        caller.RequireMutable();
        dispatchValidator.ValidateOrThrow(cmd);
        var payload = JsonSerializer.SerializeToNode(new { id, expectedVersion, cmd }, JsonDefaults.Options);
        var operationKey = $"transfer-dispatch:{caller.Subject}:{idempotencyKey:D}";

        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, "POST /transfers/{id}/dispatch", payload), async (s, c) =>
        {
            var (transfer, lines) = await TransferRules.LockAsync(s, id, c);
            if (await EarlierAsync(s, operationKey, c) is { } earlier)
                return earlier;
            caller.RequireWarehouse(Permissions.TransfersManage, transfer.SourceWarehouseId);
            Versioning.EnsureVersion(transfer.Version, expectedVersion);
            if (!transfer.IsMoving)
                throw AppException.InvalidState("Only an approved, unfinished transfer can be dispatched.");
            var transit = await s.Db.Set<Bin>().AsNoTracking().Where(b => b.WarehouseId == transfer.SourceWarehouseId && b.Kind == BinKind.Transit)
                .Select(b => b.Id).SingleAsync(c);

            var plan = new List<(TransferLine Line, StockBucket Source, Guid Transit, decimal Quantity)>();
            for (var i = 0; i < cmd.Lines.Count; i++)
            {
                var input = cmd.Lines[i];
                var line = lines.SingleOrDefault(l => l.Id == input.LineId) ?? throw AppException.Validation($"lines[{i}].lineId", "The line is not on this transfer.");
                var source = await s.Db.Set<StockBucket>().AsNoTracking().SingleOrDefaultAsync(b => b.Id == input.SourceBucketId, c);
                if (source is null || source.WarehouseId != transfer.SourceWarehouseId || source.ProductId != line.ProductId
                    || (line.LotId is { } lot && source.LotId != lot) || source.BinId == transit)
                    throw AppException.Validation($"lines[{i}].sourceBucketId", "Dispatch the line's product (and lot) from a bucket of the source warehouse.");
                var product = await s.Db.Set<Product>().AsNoTracking().SingleAsync(p => p.Id == line.ProductId, c);
                var quantity = input.Quantity.Value;
                if (decimal.Round(quantity, product.BasePrecision) != quantity)
                    throw AppException.Validation($"lines[{i}].quantity", $"The quantity exceeds the product's precision of {product.BasePrecision} decimal places.");
                if (quantity > line.RemainingToDispatch - plan.Where(p => p.Line == line).Sum(p => p.Quantity))
                    throw AppException.Validation($"lines[{i}].quantity", $"Only {Quantity.Format(line.RemainingToDispatch)} remains to dispatch on this line.");
                await TransferRules.CheckSerialsAsync(s, product, source.SerialId, quantity, input.SerialNumbers, $"lines[{i}].serialNumbers", c);
                var transitBucket = await StockLocations.MatchingBucketAsync(s, caller.TenantId, caller.Subject, transfer.SourceWarehouseId, transit,
                    source.ProductId, source.LotId, source.SerialId, source.QualityStatus.ToString(), source.ReceivedAt, c);
                plan.Add((line, source, transitBucket, quantity));
            }

            var locked = await StockLocations.LockBucketsAsync(s, plan.SelectMany(p => new[] { p.Source.Id, p.Transit }), c);
            foreach (var group in plan.GroupBy(p => p.Source.Id))
                if (locked[group.Key].OnHand - locked[group.Key].Reserved < group.Sum(p => p.Quantity))
                    throw new AppException(ErrorCode.InsufficientStock,
                        $"Only {Quantity.Format(locked[group.Key].OnHand - locked[group.Key].Reserved)} is unreserved in bucket {group.Key}.");
            await StockBlocks.EnsureNotBlockedAsync(s, locked.Keys, c);

            var movements = new List<Guid>();
            foreach (var (line, source, transitBucket, quantity) in plan)
            {
                await StockLocations.MoveOnHandAsync(s, source.Id, transitBucket, quantity, c);
                var movement = StockMovement.Transfer(MovementType.TransferDispatch, caller.TenantId, $"transfer:{transfer.Id:D}:{Guid.CreateVersion7():D}",
                    source.ProductId, source.LotId, source.SerialId, source.Id, transitBucket, quantity, transfer.Id, line.Id, null, caller.Subject, s.Now,
                    caller.CorrelationId);
                s.Db.Add(movement);
                movements.Add(movement.Id);
                DomainGuard.Run(() => line.Dispatch(quantity, s.Now));
            }
            return await FinishAsync(s, caller, transfer, lines, operationKey, "TransferDispatch", "TransferDispatched", movements, null, c);
        }, ct);
    }

    /// <summary>Transit → a destination staging bin, by actual scanned quantity; shortages stay in transit (EXC-001, AT-EXC-01).</summary>
    public Task<CommandOutcome> ReceiveAsync(Guid idempotencyKey, Guid id, int expectedVersion, TransferReceive cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.TransfersManage);
        caller.RequireMutable();
        receiveValidator.ValidateOrThrow(cmd);
        var payload = JsonSerializer.SerializeToNode(new { id, expectedVersion, cmd }, JsonDefaults.Options);
        var operationKey = $"transfer-receive:{caller.Subject}:{idempotencyKey:D}";

        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, "POST /transfers/{id}/receive", payload), async (s, c) =>
        {
            var (transfer, lines) = await TransferRules.LockAsync(s, id, c);
            if (await EarlierAsync(s, operationKey, c) is { } earlier)
                return earlier;
            caller.RequireWarehouse(Permissions.TransfersManage, transfer.DestinationWarehouseId);
            Versioning.EnsureVersion(transfer.Version, expectedVersion);
            if (transfer.Status is not (TransferStatus.InTransit or TransferStatus.PartReceived))
                throw AppException.InvalidState("Only a transfer with stock in transit can be received.");

            var plan = new List<(TransferLine Line, TransferRules.TransitStock From, Guid To, decimal Quantity)>();
            for (var i = 0; i < cmd.Lines.Count; i++)
            {
                var input = cmd.Lines[i];
                var line = lines.SingleOrDefault(l => l.Id == input.LineId) ?? throw AppException.Validation($"lines[{i}].lineId", "The line is not on this transfer.");
                var product = await s.Db.Set<Product>().AsNoTracking().SingleAsync(p => p.Id == line.ProductId, c);
                var quantity = input.Quantity.Value;
                if (decimal.Round(quantity, product.BasePrecision) != quantity)
                    throw AppException.Validation($"lines[{i}].quantity", $"The quantity exceeds the product's precision of {product.BasePrecision} decimal places.");
                await StockLocations.RequireBinAsync(s, input.StagingBinId, transfer.DestinationWarehouseId, product, [BinKind.Staging], $"lines[{i}].stagingBinId", c);
                List<Guid>? serialIds = null;
                if (product.IsSerialTracked)
                {
                    var numbers = input.SerialNumbers ?? [];
                    serialIds = await s.Db.Set<Serial>().Where(x => x.ProductId == product.Id && numbers.Contains(x.SerialNumber)).Select(x => x.Id).ToListAsync(c);
                    if (numbers.Count != quantity || serialIds.Count != numbers.Count)
                        throw AppException.Validation($"lines[{i}].serialNumbers", "Name the serial number of every unit received.");
                }
                else if (input.SerialNumbers is { Count: > 0 })
                    throw AppException.Validation($"lines[{i}].serialNumbers", "This product is not serial-tracked.");

                var stock = (await TransferRules.InTransitAsync(s, line.Id, c))
                    .Select(b => b with { Quantity = b.Quantity - plan.Where(p => p.From.BucketId == b.BucketId && p.Line == line).Sum(p => p.Quantity) })
                    .Where(b => b.Quantity > 0).ToList();
                foreach (var (from, take) in TransferRules.Draw(stock, quantity, serialIds))
                {
                    var to = await StockLocations.MatchingBucketAsync(s, caller.TenantId, caller.Subject, transfer.DestinationWarehouseId, input.StagingBinId,
                        line.ProductId, from.LotId, from.SerialId, from.Quality, from.ReceivedAt, c);
                    plan.Add((line, from, to, take));
                }
            }

            var locked = await StockLocations.LockBucketsAsync(s, plan.SelectMany(p => new[] { p.From.BucketId, p.To }), c);
            foreach (var group in plan.GroupBy(p => p.From.BucketId))
                if (locked[group.Key].OnHand < group.Sum(p => p.Quantity))
                    throw AppException.InvalidState("The transit bucket no longer holds the dispatched quantity; run reconciliation (OPS-001).");
            await StockBlocks.EnsureNotBlockedAsync(s, locked.Keys, c);

            var movements = new List<Guid>();
            foreach (var (line, from, to, quantity) in plan)
            {
                await StockLocations.MoveOnHandAsync(s, from.BucketId, to, quantity, c);
                var movement = StockMovement.Transfer(MovementType.TransferReceipt, caller.TenantId, $"transfer:{transfer.Id:D}:{Guid.CreateVersion7():D}",
                    line.ProductId, from.LotId, from.SerialId, from.BucketId, to, quantity, transfer.Id, line.Id, null, caller.Subject, s.Now, caller.CorrelationId);
                s.Db.Add(movement);
                movements.Add(movement.Id);
                DomainGuard.Run(() => line.Receive(quantity, s.Now));
            }
            return await FinishAsync(s, caller, transfer, lines, operationKey, "TransferReceipt", "TransferReceived", movements, null, c);
        }, ct);
    }

    /// <summary>Writes off lost or damaged transit stock against a signed approval of this transfer version (STK-003, IAM-003).</summary>
    public Task<CommandOutcome> DisposeAsync(Guid idempotencyKey, Guid id, int expectedVersion, TransitDisposition cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.TransfersManage);
        caller.RequireMutable();
        dispositionValidator.ValidateOrThrow(cmd);
        var payload = JsonSerializer.SerializeToNode(new { id, expectedVersion, cmd }, JsonDefaults.Options);
        var operationKey = $"transfer-disposition:{caller.Subject}:{idempotencyKey:D}";

        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, "POST /transfers/{id}/disposition", payload), async (s, c) =>
        {
            var locked = await TransferRules.LockAsync(s, id, c);
            var (transfer, lines) = locked;
            if (await EarlierAsync(s, operationKey, c) is { } earlier)
                return earlier;
            caller.RequireWarehouse(Permissions.TransfersManage, transfer.SourceWarehouseId);
            Versioning.EnsureVersion(transfer.Version, expectedVersion);
            var line = lines.SingleOrDefault(l => l.Id == cmd.LineId) ?? throw AppException.Validation("lineId", "The line is not on this transfer.");
            var product = await s.Db.Set<Product>().AsNoTracking().SingleAsync(p => p.Id == line.ProductId, c);
            var quantity = cmd.Quantity.Value;
            if (decimal.Round(quantity, product.BasePrecision) != quantity)
                throw AppException.Validation("quantity", $"The quantity exceeds the product's precision of {product.BasePrecision} decimal places.");
            if (quantity > line.InTransitQty)
                throw AppException.Validation("quantity", $"Only {Quantity.Format(line.InTransitQty)} is in transit on this line.");
            var approval = await ApprovalSubjects.RequireBindingAsync(s, await ApprovalSubjects.TransferAsync(s, locked, c), cmd.ApprovalId, c);

            var draws = TransferRules.Draw(await TransferRules.InTransitAsync(s, line.Id, c), quantity, null);
            var buckets = await StockLocations.LockBucketsAsync(s, draws.Select(d => d.Stock.BucketId), c);
            foreach (var group in draws.GroupBy(d => d.Stock.BucketId))
                if (buckets[group.Key].OnHand < group.Sum(d => d.Quantity))
                    throw AppException.InvalidState("The transit bucket no longer holds the dispatched quantity; run reconciliation (OPS-001).");
            await StockBlocks.EnsureNotBlockedAsync(s, buckets.Keys, c);

            var reason = $"{cmd.Disposition}: {cmd.Reason}";
            var movements = new List<Guid>();
            foreach (var (from, take) in draws)
            {
                await StockLocations.MoveOnHandAsync(s, from.BucketId, null, take, c);
                var movement = StockMovement.Transfer(MovementType.TransitDisposition, caller.TenantId, $"transfer:{transfer.Id:D}:{Guid.CreateVersion7():D}",
                    line.ProductId, from.LotId, from.SerialId, from.BucketId, null, take, transfer.Id, line.Id, reason, caller.Subject, s.Now, caller.CorrelationId);
                s.Db.Add(movement);
                movements.Add(movement.Id);
            }
            DomainGuard.Run(() => line.Dispose(quantity, s.Now));
            DomainGuard.Run(() => approval.Consume($"transfer:{transfer.Id:D}:disposition:{approval.Id:D}", s.Now));
            return await FinishAsync(s, caller, transfer, lines, operationKey, "TransitDisposition", "TransitStockDisposed", movements, reason, c);
        }, ct);
    }

    public Task<TransferDto> GetAsync(Guid id, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var transfer = await s.Db.Set<Transfer>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, c) ?? throw AppException.NotFound("Transfer");
            if (!caller.CanAccess(Permissions.StockRead, transfer.SourceWarehouseId) && !caller.CanAccess(Permissions.StockRead, transfer.DestinationWarehouseId))
                throw AppException.Forbidden();
            return Dto(transfer, await s.Db.Set<TransferLine>().AsNoTracking().Where(l => l.TransferId == id).OrderBy(l => l.LineNumber).ToListAsync(c));
        }, ct);
    }

    public Task<Page<TransferDto>> ListAsync(TransferFilter filter, PageRequest page, CancellationToken ct)
    {
        var scope = callers.Caller.WarehouseFilter(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var query = s.Db.Set<Transfer>().AsNoTracking();
            if (scope is not null)
                query = query.Where(t => scope.Contains(t.SourceWarehouseId) || scope.Contains(t.DestinationWarehouseId));
            if (filter.SourceWarehouseId is { } source)
                query = query.Where(t => t.SourceWarehouseId == source);
            if (filter.DestinationWarehouseId is { } destination)
                query = query.Where(t => t.DestinationWarehouseId == destination);
            if (filter.Status is { } status)
                query = query.Where(t => t.Status == status);
            var rows = await query.ToPageAsync(page, $"transfers|{filter.SourceWarehouseId}|{filter.DestinationWarehouseId}|{filter.Status}", cursors, t => t, c);
            var ids = rows.Items.Select(t => t.Id).ToList();
            var lines = (await s.Db.Set<TransferLine>().AsNoTracking().Where(l => ids.Contains(l.TransferId)).ToListAsync(c)).ToLookup(l => l.TransferId);
            return new Page<TransferDto>(rows.Items.Select(t => Dto(t, lines[t.Id].OrderBy(l => l.LineNumber).ToList())).ToList(), rows.NextCursor);
        }, ct);
    }

    private Task<TransferDto> DecideAsync(Guid id, int expectedVersion, string eventType, string? reason,
        Action<Transfer, List<TransferLine>, string, DateTimeOffset> change, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.TransfersManage);
        caller.RequireMutable();
        return uow.WriteAsync(async (s, c) =>
        {
            var (transfer, lines) = await TransferRules.LockAsync(s, id, c);
            caller.RequireWarehouse(Permissions.TransfersManage, transfer.SourceWarehouseId);
            Versioning.EnsureVersion(transfer.Version, expectedVersion);
            var before = Dto(transfer, lines);
            DomainGuard.Run(() => change(transfer, lines, caller.Subject, s.Now));
            await ApprovalSubjects.InvalidateLiveAsync(s, ApprovalSubjectType.TransitDisposition, transfer.Id, "The transfer changed after the approval was requested (IAM-003).", c);
            var after = Dto(transfer, lines);
            s.Audit(new AuditEntry(eventType, "Transfer", id, transfer.Version, reason, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    private static async Task<CommandOutcome?> EarlierAsync(ITenantSession s, string operationKey, CancellationToken ct)
    {
        var earlier = await s.Db.Database.SqlQuery<string>(
            $"SELECT result_reference::text AS \"Value\" FROM ti.document_operation WHERE operation_key = {operationKey}").ToListAsync(ct);
        return earlier.Count == 1 ? new CommandOutcome(200, JsonNode.Parse(earlier[0])) : null;
    }

    private static async Task<CommandOutcome> FinishAsync(ITenantSession s, CallerContext caller, Transfer transfer, List<TransferLine> lines,
        string operationKey, string operationType, string eventType, List<Guid> movements, string? reason, CancellationToken ct)
    {
        DomainGuard.Run(() => transfer.Recalculate(lines, caller.Subject, s.Now));
        await ApprovalSubjects.InvalidateLiveAsync(s, ApprovalSubjectType.TransitDisposition, transfer.Id, "The transfer moved stock after the approval was requested (IAM-003).", ct);
        await s.Db.SaveChangesAsync(ct);
        var dto = Dto(transfer, lines);
        await s.Db.Database.ExecuteSqlAsync($"""
            INSERT INTO ti.document_operation (tenant_id, operation_key, operation_type, result_reference, created_by, created_at)
            VALUES ({caller.TenantId}, {operationKey}, {operationType}, {JsonSerializer.Serialize(dto, JsonDefaults.Options)}::jsonb, {caller.Subject}, {s.Now})
            """, ct);
        s.Audit(new AuditEntry(eventType, "Transfer", transfer.Id, transfer.Version, reason, null, new { transfer = dto, movementIds = movements }));
        s.Publish(eventType, new { transferId = transfer.Id, movementIds = movements });
        await s.Db.SaveChangesAsync(ct);
        return new CommandOutcome(200, JsonSerializer.SerializeToNode(dto, JsonDefaults.Options));
    }

    internal static TransferDto Dto(Transfer t, IReadOnlyList<TransferLine> lines) => new(
        t.Id, t.Version, t.CreatedAt, t.CreatedBy, t.UpdatedAt, t.SourceWarehouseId, t.DestinationWarehouseId, t.Reference, t.Status, t.DispatchedAt,
        t.ReceivedAt, lines.Select(l => new TransferLineDto(l.Id, l.ProductId, l.LotId, Quantity.From(l.RequestedQty), Quantity.From(l.DispatchedQty),
            Quantity.From(l.ReceivedQty), Quantity.From(l.InTransitQty), Quantity.From(l.DisposedQty))).ToList());
}

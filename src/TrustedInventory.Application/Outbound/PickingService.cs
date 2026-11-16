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
using TrustedInventory.Domain.Outbound;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Outbound;

public sealed record PickDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, Guid ReservationId, Guid OrderId, Guid OrderLineId,
    Guid WarehouseId, Guid SourceBinId, string SourceBinCode, Guid ProductId, string Sku, Guid? LotId, string? LotNumber, DateOnly? ExpiryDate,
    Quantity Quantity, Quantity PickedQuantity, Quantity RemainingQuantity, ReservationState State, Guid? StagingBinId,
    IReadOnlyList<string> SerialNumbers, string? PickedBy, DateTimeOffset? PickedAt) : IVersioned;

public sealed record PickFilter(Guid? WarehouseId, Guid? OrderId, ReservationState? State);

public sealed record ConfirmPick(Quantity Quantity, Guid ScannedBinId, Guid ScannedProductId, string? ScannedLotNumber,
    IReadOnlyList<string>? SerialNumbers, Guid StagingBinId);

public sealed record PickConfirmResult(Guid PickId, Guid MovementId, Quantity PickedQuantity, Quantity RemainingQuantity, int Version);

public sealed record UnpickRequest(Quantity Quantity, Guid TargetBinId, IReadOnlyList<string>? SerialNumbers, string Reason);

public sealed record UnpickResult(Guid PickId, Guid MovementId, Guid ReservationId, int Version);

public sealed class ConfirmPickValidator : AbstractValidator<ConfirmPick>
{
    public ConfirmPickValidator()
    {
        RuleFor(x => x.ScannedBinId).NotEmpty();
        RuleFor(x => x.ScannedProductId).NotEmpty();
        RuleFor(x => x.StagingBinId).NotEmpty();
        RuleFor(x => x.Quantity).Must(Quantity.IsValidPositive).WithMessage(Quantity.PositiveMessage);
        RuleFor(x => x.ScannedLotNumber!).Must(v => v.Length is >= 1 and <= 100 && v == v.Trim())
            .WithMessage("Use 1 to 100 characters without leading or trailing spaces.").When(x => x.ScannedLotNumber is not null);
        RuleFor(x => x.SerialNumbers!).Must(PickRules.ValidSerialList).WithMessage(PickRules.SerialListMessage).When(x => x.SerialNumbers is not null);
    }
}

public sealed class UnpickRequestValidator : AbstractValidator<UnpickRequest>
{
    public UnpickRequestValidator()
    {
        RuleFor(x => x.TargetBinId).NotEmpty();
        RuleFor(x => x.Quantity).Must(Quantity.IsValidPositive).WithMessage(Quantity.PositiveMessage);
        RuleFor(x => x.Reason).ValidReason();
        RuleFor(x => x.SerialNumbers!).Must(PickRules.ValidSerialList).WithMessage(PickRules.SerialListMessage).When(x => x.SerialNumbers is not null);
    }
}

internal static class PickRules
{
    public const string SerialListMessage = "Send at most 10000 distinct serial numbers of 1 to 100 characters.";

    public static bool ValidSerialList(IReadOnlyList<string> serials) =>
        serials.Count <= 10000 && serials.All(v => v is { Length: >= 1 and <= 100 }) && serials.Distinct(StringComparer.Ordinal).Count() == serials.Count;
}

/// <summary>
/// Picking (OUT-003, OUT-004, STK-002, DEC-054). Confirmation moves reserved stock and its reservation segment from the
/// storage bucket to a staging bucket by scan in one idempotent stock command; unpick (safety) returns it. Lock order:
/// order row → lines → pick → reservations → buckets (by id) → audit stream.
/// </summary>
public sealed class PickingService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IValidator<ConfirmPick> confirmValidator,
    IValidator<UnpickRequest> unpickValidator)
{
    public Task<Page<PickDto>> ListAsync(PickFilter filter, PageRequest page, CancellationToken ct)
    {
        var scope = callers.Caller.WarehouseFilter(Permissions.PicksConfirm);
        return uow.ReadAsync(async (s, c) =>
        {
            var reservations = s.Db.Set<Reservation>().AsNoTracking();
            var lines = s.Db.Set<OutboundOrderLine>().AsNoTracking();
            var query = s.Db.Set<Pick>().AsNoTracking();
            if (scope is not null)
                query = query.Where(p => scope.Contains(p.WarehouseId));
            if (filter.WarehouseId is { } w)
                query = query.Where(p => p.WarehouseId == w);
            if (filter.OrderId is { } o)
                query = query.Where(p => reservations.Any(r => r.Id == p.ReservationId && lines.Any(l => l.Id == r.OrderLineId && l.OrderId == o)));
            if (filter.State is { } state)
                query = query.Where(p => reservations.Any(r => r.Id == p.ReservationId && r.State == state));
            var picks = await query.ToPageAsync(page, $"picks|{filter.WarehouseId}|{filter.OrderId}|{filter.State}", cursors, p => p, c);
            var dtos = new List<PickDto>();
            foreach (var pick in picks.Items)
                dtos.Add(await DtoAsync(s, pick, c));
            return new Page<PickDto>(dtos, picks.NextCursor);
        }, ct);
    }

    public Task<PickDto> GetAsync(Guid id, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.PicksConfirm);
        return uow.ReadAsync(async (s, c) =>
        {
            var pick = await s.Db.Set<Pick>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, c) ?? throw AppException.NotFound("Pick");
            caller.RequireWarehouse(Permissions.PicksConfirm, pick.WarehouseId);
            return await DtoAsync(s, pick, c);
        }, ct);
    }

    public Task<CommandOutcome> ConfirmAsync(Guid idempotencyKey, Guid pickId, int expectedVersion, ConfirmPick cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.PicksConfirm);
        caller.RequireMutable();
        confirmValidator.ValidateOrThrow(cmd);
        var payload = JsonSerializer.SerializeToNode(new { pickId, expectedVersion, cmd }, JsonDefaults.Options);
        var operationKey = $"pick:{caller.Subject}:{idempotencyKey:D}";

        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, "POST /picks/{id}/confirm", payload), async (s, c) =>
        {
            var w = await LockAsync(s, pickId, operationKey, c);
            if (w.Earlier is not null)
                return new CommandOutcome(200, w.Earlier);
            var (pick, order, line, own, child) = (w.Pick!, w.Order!, w.Line!, w.Own!, w.Child);
            caller.RequireWarehouse(Permissions.PicksConfirm, pick.WarehouseId);
            Versioning.EnsureVersion(pick.Version, expectedVersion);
            if (!pick.IsOpen || own.State != ReservationState.Active)
                throw AppException.InvalidState("This pick task has nothing left to pick.");
            if (own.ExpiresAt <= s.Now)
                throw AppException.InvalidState("The reservation has expired; allocate the order again (STK-008).");

            var source = await BucketAsync(s, own.BucketId, c);
            var product = await s.Db.Set<Product>().AsNoTracking().SingleAsync(p => p.Id == pick.ProductId, c);
            var lot = source.LotId is { } lid ? await s.Db.Set<Lot>().AsNoTracking().SingleAsync(l => l.Id == lid, c) : null;

            // 1. Scan confirmation (OUT-003): wrong bin, product, lot or serial changes nothing; no lot is ever substituted.
            if (cmd.ScannedBinId != source.BinId)
                throw AppException.Validation("scannedBinId", "Pick from the bin named on the task.");
            if (cmd.ScannedProductId != pick.ProductId)
                throw AppException.Validation("scannedProductId", "The scanned product is not the product of this task.");
            if (lot is not null && cmd.ScannedLotNumber != lot.LotNumber)
                throw AppException.Validation("scannedLotNumber",
                    product.HandlingProfile == HandlingProfile.Medical ? "Scan the batch named on the task; medical batches are never substituted."
                        : "Scan the lot named on the task; lots are never substituted.");
            if (lot is null && cmd.ScannedLotNumber is not null)
                throw AppException.Validation("scannedLotNumber", "This stock has no lot.");
            var quantity = cmd.Quantity.Value;
            if (decimal.Round(quantity, product.BasePrecision) != quantity)
                throw AppException.Validation("quantity", $"The quantity exceeds the product's precision of {product.BasePrecision} decimal places.");
            if (quantity > own.Qty)
                throw AppException.Validation("quantity", $"Only {Quantity.Format(own.Qty)} remains on this task.");
            await CheckSerialsAsync(s, product, source.SerialId, quantity, cmd.SerialNumbers, c);

            // 2. Staging bin (DEC-012) and the remaining eligibility of the reserved stock.
            await CheckBinAsync(s, cmd.StagingBinId, pick.WarehouseId, product, BinKind.Staging, "stagingBinId", c);
            if (child is not null && (await BucketAsync(s, child.BucketId, c)).BinId != cmd.StagingBinId)
                throw AppException.Validation("stagingBinId", "Stage the rest of this task in the same bin as its first part.");
            if (source.Quality != nameof(QualityStatus.Available))
                throw AppException.InvalidState("The reserved stock is no longer available (quality status changed).");
            if (!ReceivingRules.IsShippable(lot?.ExpiryDate, await WarehouseTodayAsync(s, pick.WarehouseId, c)))
                throw AppException.InvalidState("The reserved lot has expired and cannot be picked.");
            if (await Inbound.TelemetryChecks.StaleAsync(s, source.Id, c))
                throw new AppException(ErrorCode.StockBlocked, "Temperature monitoring for this zone is stale; monitored stock cannot be picked (TEL-003).");

            // 3. Buckets in id order, then the stock and reservation move together (STK-002).
            var staging = await MatchingBucketAsync(s, caller, source, cmd.StagingBinId, c);
            await LockBucketsAsync(s, source.Id, staging, quantity, c);
            await StockBlocks.EnsureNotBlockedAsync(s, [source.Id, staging], c);
            await MoveAsync(s, source.Id, staging, quantity, c);

            Reservation picked;
            if (quantity < own.Qty)
            {
                DomainGuard.Run(() => own.Shrink(quantity, s.Now));
                if (child is null)
                {
                    picked = Reservation.CreatePicked(caller.TenantId, own.OrderLineId, own.ProductId, staging, quantity, own.Id, caller.Subject, s.Now);
                    s.Db.Add(picked);
                }
                else
                {
                    DomainGuard.Run(() => child.Grow(quantity, s.Now));
                    picked = child;
                }
            }
            else
            {
                DomainGuard.Run(() => own.MoveToStaging(staging, child?.Qty ?? 0, s.Now));
                if (child is not null)
                    DomainGuard.Run(() => child.Fold(s.Now));
                picked = own;
            }
            var before = await DtoAsync(s, pick, c);
            DomainGuard.Run(() => pick.Confirm(quantity, s.Now));
            DomainGuard.Run(() => line.Pick(quantity, s.Now));
            await RecalculateAsync(s, order, c);
            await s.Db.SaveChangesAsync(c);

            var movement = StockMovement.Outbound(MovementType.Pick, caller.TenantId, $"pick:{pick.Id:D}:{Guid.CreateVersion7():D}", source.ProductId,
                source.LotId, source.SerialId, source.Id, staging, quantity, order.Id, line.Id, picked.Id, null, caller.Subject, s.Now, caller.CorrelationId);
            s.Db.Add(movement);
            await s.Db.SaveChangesAsync(c);
            var scanned = JsonSerializer.Serialize(new
            {
                cmd.ScannedBinId, cmd.ScannedProductId, cmd.ScannedLotNumber, serialNumbers = cmd.SerialNumbers ?? [],
            }, JsonDefaults.Options);
            await s.Db.Database.ExecuteSqlAsync($"""
                INSERT INTO ti.pick_confirmation (tenant_id, pick_id, picked_qty, picked_reservation_id, staging_bin_id, warehouse_id, scanned,
                                                  movement_id, created_at, created_by)
                VALUES ({caller.TenantId}, {pick.Id}, {quantity}, {picked.Id}, {cmd.StagingBinId}, {pick.WarehouseId}, {scanned}::jsonb,
                        {movement.Id}, {s.Now}, {caller.Subject})
                """, c);

            var result = new PickConfirmResult(pick.Id, movement.Id, Quantity.From(pick.PickedQty), Quantity.From(pick.RemainingQty), pick.Version);
            await RecordOperationAsync(s, caller, operationKey, "Pick", result, c);
            s.Audit(new AuditEntry("PickConfirmed", "Pick", pick.Id, pick.Version, null, before, await DtoAsync(s, pick, c)));
            s.Publish("StockPicked", new
            {
                pickId = pick.Id, orderId = order.Id, movementId = movement.Id, reservationId = picked.Id, quantity = Quantity.Format(quantity),
            });
            await s.Db.SaveChangesAsync(c);
            return new CommandOutcome(200, JsonSerializer.SerializeToNode(result, JsonDefaults.Options));
        }, ct);
    }

    /// <summary>A safety operation (section 13): picked stock goes back to storage and into the pick's own segment.</summary>
    public Task<CommandOutcome> UnpickAsync(Guid idempotencyKey, Guid pickId, int expectedVersion, UnpickRequest cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.PicksConfirm);
        unpickValidator.ValidateOrThrow(cmd);
        var payload = JsonSerializer.SerializeToNode(new { pickId, expectedVersion, cmd }, JsonDefaults.Options);
        var operationKey = $"unpick:{caller.Subject}:{idempotencyKey:D}";

        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, "POST /picks/{id}/unpick", payload), async (s, c) =>
        {
            var w = await LockAsync(s, pickId, operationKey, c);
            if (w.Earlier is not null)
                return new CommandOutcome(200, w.Earlier);
            var (pick, order, line, own, child) = (w.Pick!, w.Order!, w.Line!, w.Own!, w.Child);
            caller.RequireWarehouse(Permissions.PicksConfirm, pick.WarehouseId);
            Versioning.EnsureVersion(pick.Version, expectedVersion);
            var segment = own.State switch
            {
                ReservationState.Picked => own,
                ReservationState.Active => child,
                _ => null,
            };
            if (segment is null || pick.PickedQty == 0)
                throw AppException.InvalidState("Nothing of this pick task is staged.");

            var staged = await BucketAsync(s, segment.BucketId, c);
            var product = await s.Db.Set<Product>().AsNoTracking().SingleAsync(p => p.Id == pick.ProductId, c);
            var quantity = cmd.Quantity.Value;
            if (decimal.Round(quantity, product.BasePrecision) != quantity)
                throw AppException.Validation("quantity", $"The quantity exceeds the product's precision of {product.BasePrecision} decimal places.");
            if (quantity > segment.Qty)
                throw AppException.Validation("quantity", $"Only {Quantity.Format(segment.Qty)} of this task is staged.");
            await CheckSerialsAsync(s, product, staged.SerialId, quantity, cmd.SerialNumbers, c);
            await CheckBinAsync(s, cmd.TargetBinId, pick.WarehouseId, product, BinKind.Storage, "targetBinId", c);
            if (own.State == ReservationState.Active && cmd.TargetBinId != pick.SourceBinId)
                throw AppException.Validation("targetBinId", "Return part-picked stock to the bin the rest is reserved in.");

            var target = await MatchingBucketAsync(s, caller, staged, cmd.TargetBinId, c);
            if (own.State == ReservationState.Active && target != own.BucketId)
                throw AppException.InvalidState("The returned stock would not join its reservation."); // defensive: same bin implies same bucket
            await LockBucketsAsync(s, staged.Id, target, quantity, c);
            await StockBlocks.EnsureNotBlockedAsync(s, [staged.Id, target], c);
            await MoveAsync(s, staged.Id, target, quantity, c);

            if (own.State == ReservationState.Active)
            {
                DomainGuard.Run(() => own.Grow(quantity, s.Now));
                DomainGuard.Run(() =>
                {
                    if (quantity == segment.Qty)
                        segment.Fold(s.Now);
                    else
                        segment.Shrink(quantity, s.Now);
                });
            }
            else
            {
                var rest = own.Qty - quantity;
                DomainGuard.Run(() => own.ReturnToActive(target, quantity, s.Now + AllocationService.ReservationLifetime, s.Now));
                if (rest > 0)
                    s.Db.Add(Reservation.CreatePicked(caller.TenantId, own.OrderLineId, own.ProductId, staged.Id, rest, own.Id, caller.Subject, s.Now));
            }
            var before = await DtoAsync(s, pick, c);
            DomainGuard.Run(() => pick.Unpick(quantity, cmd.TargetBinId, s.Now));
            DomainGuard.Run(() => line.Unpick(quantity, s.Now));
            // Unpicking works down the pick's open rework tasks (OUT-006, DEC-055).
            var left = quantity;
            foreach (var task in await s.Db.Set<WarehouseTask>()
                         .Where(t => t.Type == TaskType.Rework && t.RelatedType == "Pick" && t.RelatedId == pick.Id
                             && (t.Status == Domain.Inbound.TaskStatus.Open || t.Status == Domain.Inbound.TaskStatus.InProgress))
                         .OrderBy(t => t.CreatedAt).ToListAsync(c))
            {
                var share = Math.Min(left, task.RemainingQty ?? 0);
                if (share <= 0)
                    break;
                DomainGuard.Run(() => task.Confirm(share, caller.Subject, cmd.Reason, s.Now));
                left -= share;
            }
            await RecalculateAsync(s, order, c);
            await s.Db.SaveChangesAsync(c);

            var movement = StockMovement.Outbound(MovementType.Unpick, caller.TenantId, $"unpick:{pick.Id:D}:{Guid.CreateVersion7():D}", staged.ProductId,
                staged.LotId, staged.SerialId, staged.Id, target, quantity, order.Id, line.Id, own.Id, cmd.Reason, caller.Subject, s.Now, caller.CorrelationId);
            s.Db.Add(movement);
            await s.Db.SaveChangesAsync(c);

            var result = new UnpickResult(pick.Id, movement.Id, own.Id, pick.Version);
            await RecordOperationAsync(s, caller, operationKey, "Unpick", result, c);
            s.Audit(new AuditEntry("PickReturned", "Pick", pick.Id, pick.Version, cmd.Reason, before, await DtoAsync(s, pick, c)));
            s.Publish("StockUnpicked", new
            {
                pickId = pick.Id, orderId = order.Id, movementId = movement.Id, reservationId = own.Id, quantity = Quantity.Format(quantity),
            });
            await s.Db.SaveChangesAsync(c);
            return new CommandOutcome(200, JsonSerializer.SerializeToNode(result, JsonDefaults.Options));
        }, ct);
    }

    /// <summary>Takes the document locks in order and returns an earlier answer for a replayed operation key (STK-007).</summary>
    private static async Task<Locked> LockAsync(ITenantSession s, Guid pickId, string operationKey, CancellationToken ct)
    {
        var orderIds = await s.Db.Database.SqlQuery<Guid>($"""
            SELECT l.order_id AS "Value" FROM ti.pick p
            JOIN ti.reservation r ON r.tenant_id = p.tenant_id AND r.id = p.reservation_id
            JOIN ti.outbound_order_line l ON l.tenant_id = r.tenant_id AND l.id = r.order_line_id
            WHERE p.id = {pickId}
            """).ToListAsync(ct);
        if (orderIds.Count == 0)
            throw AppException.NotFound("Pick");
        var orderId = orderIds[0];
        await s.Db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.outbound_order WHERE id = {orderId} FOR UPDATE").ToListAsync(ct);
        await s.Db.Database.SqlQuery<Guid>(
            $"SELECT id AS \"Value\" FROM ti.outbound_order_line WHERE order_id = {orderId} ORDER BY line_number FOR UPDATE").ToListAsync(ct);
        var earlier = await s.Db.Database.SqlQuery<string>(
            $"SELECT result_reference::text AS \"Value\" FROM ti.document_operation WHERE operation_key = {operationKey}").ToListAsync(ct);
        if (earlier.Count == 1)
            return new Locked(JsonNode.Parse(earlier[0]), null, null, null, null, null);

        await s.Db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.pick WHERE id = {pickId} FOR UPDATE").ToListAsync(ct);
        var pick = await s.Db.Set<Pick>().SingleAsync(p => p.Id == pickId, ct);
        var segmentIds = await s.Db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM ti.reservation
            WHERE id = {pick.ReservationId} OR (parent_reservation_id = {pick.ReservationId} AND state = 'Picked')
            ORDER BY id FOR UPDATE
            """).ToListAsync(ct);
        var segments = await s.Db.Set<Reservation>().Where(r => segmentIds.Contains(r.Id)).ToListAsync(ct);
        var own = segments.Single(r => r.Id == pick.ReservationId);
        var child = segments.SingleOrDefault(r => r.Id != own.Id);
        var order = await s.Db.Set<OutboundOrder>().SingleAsync(o => o.Id == orderId, ct);
        var line = await s.Db.Set<OutboundOrderLine>().SingleAsync(l => l.Id == own.OrderLineId, ct);
        return new Locked(null, pick, order, line, own, child);
    }

    private static async Task RecalculateAsync(ITenantSession s, OutboundOrder order, CancellationToken ct)
    {
        var lines = await s.Db.Set<OutboundOrderLine>().Where(l => l.OrderId == order.Id).ToListAsync(ct);
        DomainGuard.Run(() => order.Recalculate(lines.Select(l => l.Progress).ToList(), s.Now));
    }

    private static async Task<BucketRow> BucketAsync(ITenantSession s, Guid bucketId, CancellationToken ct) =>
        (await s.Db.Database.SqlQuery<BucketRow>($"""
            SELECT id AS "Id", warehouse_id AS "WarehouseId", bin_id AS "BinId", product_id AS "ProductId", lot_id AS "LotId", serial_id AS "SerialId",
                   quality_status::text AS "Quality", received_at AS "ReceivedAt"
            FROM ti.stock_bucket WHERE id = {bucketId}
            """).ToListAsync(ct)).Single();

    /// <summary>The bucket with the same product, lot, serial and quality in another bin, created empty when missing.</summary>
    private static async Task<Guid> MatchingBucketAsync(ITenantSession s, CallerContext caller, BucketRow like, Guid binId, CancellationToken ct)
    {
        await s.Db.Database.ExecuteSqlAsync($"""
            INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, lot_id, serial_id, quality_status, received_at,
                                         on_hand_qty, created_by, created_at, updated_at)
            VALUES ({caller.TenantId}, {like.WarehouseId}, {binId}, {like.ProductId}, {like.LotId}, {like.SerialId}, {like.Quality}::ti.quality_status,
                    {like.ReceivedAt}, 0, {caller.Subject}, {s.Now}, {s.Now})
            ON CONFLICT (tenant_id, bin_id, product_id, lot_id, serial_id, quality_status) DO NOTHING
            """, ct);
        return (await s.Db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM ti.stock_bucket
            WHERE bin_id = {binId} AND product_id = {like.ProductId} AND lot_id IS NOT DISTINCT FROM {like.LotId}
              AND serial_id IS NOT DISTINCT FROM {like.SerialId} AND quality_status = {like.Quality}::ti.quality_status
            """).ToListAsync(ct)).Single();
    }

    /// <summary>STK-005: both buckets locked in id order; the source must still hold the reserved quantity.</summary>
    private static async Task LockBucketsAsync(ITenantSession s, Guid from, Guid to, decimal quantity, CancellationToken ct)
    {
        var ids = new[] { from, to };
        var rows = await s.Db.Database.SqlQuery<LockedBucket>($"""
            SELECT id AS "Id", on_hand_qty AS "OnHand", reserved_qty AS "Reserved" FROM ti.stock_bucket WHERE id = ANY({ids}) ORDER BY id FOR UPDATE
            """).ToListAsync(ct);
        var source = rows.Single(r => r.Id == from);
        if (source.OnHand < quantity || source.Reserved < quantity)
            throw AppException.InvalidState("The bucket no longer holds the reserved quantity; run reconciliation (OPS-001).");
    }

    /// <summary>On hand and reserved move together, so warehouse stock is unchanged by pick and unpick (STK-002).</summary>
    private static async Task MoveAsync(ITenantSession s, Guid from, Guid to, decimal quantity, CancellationToken ct)
    {
        await s.Db.Database.ExecuteSqlAsync($"""
            UPDATE ti.stock_bucket SET on_hand_qty = on_hand_qty - {quantity}, reserved_qty = reserved_qty - {quantity}, updated_at = {s.Now},
                   version = version + 1
            WHERE id = {from}
            """, ct);
        await s.Db.Database.ExecuteSqlAsync($"""
            UPDATE ti.stock_bucket SET on_hand_qty = on_hand_qty + {quantity}, reserved_qty = reserved_qty + {quantity}, updated_at = {s.Now},
                   version = version + 1
            WHERE id = {to}
            """, ct);
    }

    private static async Task CheckSerialsAsync(ITenantSession s, Product product, Guid? serialId, decimal quantity, IReadOnlyList<string>? scanned,
        CancellationToken ct)
    {
        if (!product.IsSerialTracked)
        {
            if (scanned is { Count: > 0 })
                throw AppException.Validation("serialNumbers", "This product is not serial-tracked.");
            return;
        }
        var expected = serialId is { } sid
            ? await s.Db.Set<Serial>().AsNoTracking().Where(x => x.Id == sid).Select(x => x.SerialNumber).SingleAsync(ct)
            : null;
        if (scanned is null || scanned.Count != quantity || expected is null || scanned.Any(v => v != expected))
            throw AppException.Validation("serialNumbers", "Scan the serial number of the reserved unit.");
    }

    /// <summary>An active bin of the given kind in the warehouse whose zone suits the product (DEC-012).</summary>
    private static async Task CheckBinAsync(ITenantSession s, Guid binId, Guid warehouseId, Product product, BinKind kind, string field, CancellationToken ct)
    {
        var bin = await s.Db.Set<Bin>().AsNoTracking().SingleOrDefaultAsync(b => b.Id == binId, ct);
        if (bin is null || bin.WarehouseId != warehouseId || bin.Kind != kind || !bin.Active)
            throw AppException.Validation(field, $"Use an active {kind} bin of this warehouse.");
        var zone = await s.Db.Set<Zone>().AsNoTracking().SingleAsync(z => z.Id == bin.ZoneId, ct);
        var profile = product.StorageProfileId is { } pid ? await s.Db.Set<StorageProfile>().AsNoTracking().SingleAsync(p => p.Id == pid, ct) : null;
        var hazards = await s.Db.Set<ZoneAllowedHazardClass>().AsNoTracking().Where(h => h.ZoneId == zone.Id).Select(h => h.HazardClass).ToListAsync(ct);
        if (!zone.Active || !StorageCompatibility.IsCompatible(zone.TempMinC, zone.TempMaxC, profile?.MinC, profile?.MaxC, product.HazardClass, hazards))
            throw new AppException(ErrorCode.IncompatibleBin, "This bin cannot hold this product (DEC-012).",
                [new FieldError(field, ErrorCode.IncompatibleBin, "The bin's zone does not suit the product's storage profile or hazard class.")]);
    }

    private static async Task<DateOnly> WarehouseTodayAsync(ITenantSession s, Guid warehouseId, CancellationToken ct)
    {
        var timeZone = await s.Db.Set<Warehouse>().AsNoTracking().Where(w => w.Id == warehouseId).Select(w => w.TimeZone).SingleAsync(ct);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(s.Now, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).DateTime);
    }

    private static Task<int> RecordOperationAsync<T>(ITenantSession s, CallerContext caller, string operationKey, string type, T result, CancellationToken ct) =>
        s.Db.Database.ExecuteSqlAsync($"""
            INSERT INTO ti.document_operation (tenant_id, operation_key, operation_type, result_reference, created_by, created_at)
            VALUES ({caller.TenantId}, {operationKey}, {type}, {JsonSerializer.Serialize(result, JsonDefaults.Options)}::jsonb, {caller.Subject}, {s.Now})
            """, ct);

    private static async Task<PickDto> DtoAsync(ITenantSession s, Pick p, CancellationToken ct)
    {
        var own = await s.Db.Set<Reservation>().AsNoTracking().SingleAsync(r => r.Id == p.ReservationId, ct);
        var orderId = await s.Db.Set<OutboundOrderLine>().AsNoTracking().Where(l => l.Id == own.OrderLineId).Select(l => l.OrderId).SingleAsync(ct);
        var binCode = await s.Db.Set<Bin>().AsNoTracking().Where(b => b.Id == p.SourceBinId).Select(b => b.Code).SingleAsync(ct);
        var sku = await s.Db.Set<Product>().AsNoTracking().Where(x => x.Id == p.ProductId).Select(x => x.Sku).SingleAsync(ct);
        var lot = p.LotId is { } lid ? await s.Db.Set<Lot>().AsNoTracking().SingleAsync(l => l.Id == lid, ct) : null;

        // The staged segment: the own segment once picked, otherwise its picked child (DEC-054).
        var staged = own.State == ReservationState.Picked ? own
            : await s.Db.Set<Reservation>().AsNoTracking()
                .SingleOrDefaultAsync(r => r.ParentReservationId == own.Id && r.State == ReservationState.Picked, ct);
        var buckets = s.Db.Set<StockBucket>().AsNoTracking();
        Guid? stagingBinId = staged is null ? null : await buckets.Where(b => b.Id == staged.BucketId).Select(b => b.BinId).SingleAsync(ct);
        var serialId = await buckets.Where(b => b.Id == own.BucketId).Select(b => b.SerialId).SingleAsync(ct);
        var serials = serialId is { } sid
            ? await s.Db.Set<Serial>().AsNoTracking().Where(x => x.Id == sid).Select(x => x.SerialNumber).ToListAsync(ct)
            : [];
        var last = (await s.Db.Database.SqlQuery<LastConfirmation>($"""
            SELECT created_by AS "By", created_at AS "At" FROM ti.pick_confirmation WHERE pick_id = {p.Id} ORDER BY created_at DESC, id DESC LIMIT 1
            """).ToListAsync(ct)).SingleOrDefault();
        return new PickDto(p.Id, p.Version, p.CreatedAt, p.CreatedBy, p.UpdatedAt, p.ReservationId, orderId, own.OrderLineId, p.WarehouseId, p.SourceBinId,
            binCode, p.ProductId, sku, p.LotId, lot?.LotNumber, lot?.ExpiryDate, Quantity.From(p.TaskQty), Quantity.From(p.PickedQty),
            Quantity.From(p.RemainingQty), own.State, stagingBinId, serials, last?.By, last?.At);
    }

    private sealed record Locked(JsonNode? Earlier, Pick? Pick, OutboundOrder? Order, OutboundOrderLine? Line, Reservation? Own, Reservation? Child);

    private sealed record BucketRow(Guid Id, Guid WarehouseId, Guid BinId, Guid ProductId, Guid? LotId, Guid? SerialId, string Quality,
        DateTimeOffset ReceivedAt);

    private sealed record LockedBucket(Guid Id, decimal OnHand, decimal Reserved);

    private sealed record LastConfirmation(string By, DateTimeOffset At);
}

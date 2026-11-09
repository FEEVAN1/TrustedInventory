using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Inbound;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Outbound;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Outbound;

public enum ShipmentStatus
{
    Dispatched,
}

public sealed record DispatchRequest(IReadOnlyList<Guid> PickIds, bool? AllowPartial, string DispatchReference);

public sealed record ShipmentLineDto(Guid Id, Guid OrderLineId, Guid ProductId, Guid? LotId, IReadOnlyList<string> SerialNumbers, Quantity Quantity,
    Quantity ReturnedQuantity);

public sealed record DispatchResult(Guid ShipmentId, IReadOnlyList<ShipmentLineDto> Lines, OutboundOrderStatus Status, int Version);

public sealed record ShipmentDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, Guid OrderId, Guid WarehouseId, Guid CustomerId,
    string DispatchReference, DateTimeOffset DispatchedAt, ShipmentStatus Status, IReadOnlyList<ShipmentLineDto> Lines) : IVersioned;

public sealed record ShipmentFilter(Guid? WarehouseId, Guid? OrderId, Guid? CustomerId, DateTimeOffset? From, DateTimeOffset? To);

public sealed class DispatchRequestValidator : AbstractValidator<DispatchRequest>
{
    public DispatchRequestValidator()
    {
        RuleFor(x => x.PickIds).NotNull()
            .Must(l => l is { Count: >= 1 and <= 1000 } && l.Distinct().Count() == l.Count && l.All(id => id != Guid.Empty))
            .WithMessage("Send 1 to 1000 distinct pick ids.");
        RuleFor(x => x.DispatchReference).NotNull().Must(v => v is { Length: >= 1 and <= 100 } && v == v.Trim())
            .WithMessage("Use 1 to 100 characters without leading or trailing spaces.");
    }
}

/// <summary>
/// Dispatch (OUT-005, OUT-006, STK-002, DEC-055). Rechecks every staged segment in the deduction transaction; picked
/// stock that became blocked posts nothing, returns 409 STOCK_BLOCKED and gets rework tasks written separately.
/// Lock order: order row → lines → picks → segments → staging buckets (by id) → audit stream.
/// </summary>
public sealed class DispatchService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IValidator<DispatchRequest> validator)
{
    public async Task<CommandOutcome> DispatchAsync(Guid idempotencyKey, Guid orderId, int expectedVersion, DispatchRequest cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.DispatchPost);
        caller.RequireMutable();
        validator.ValidateOrThrow(cmd);
        var allowPartial = cmd.AllowPartial ?? false;
        var payload = JsonSerializer.SerializeToNode(new { orderId, expectedVersion, cmd.PickIds, allowPartial, cmd.DispatchReference }, JsonDefaults.Options);
        var operationKey = $"dispatch:{caller.Subject}:{idempotencyKey:D}";
        try
        {
            return await uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, "POST /orders/{id}/dispatch", payload),
                (s, c) => PostAsync(s, caller, orderId, expectedVersion, cmd, allowPartial, operationKey, c), ct);
        }
        catch (AppException e) when (e.Code == ErrorCode.StockBlocked && e.Extensions?["affectedPicks"] is JsonArray affected)
        {
            // OUT-006: the dispatch rolled back; the rework tasks are written in their own transaction.
            var taskIds = await uow.WriteAsync((s, c) => ReworkAsync(s, caller, affected, c), ct);
            throw new AppException(ErrorCode.StockBlocked, e.Message)
            {
                Extensions = new JsonObject
                {
                    ["affectedPicks"] = affected.DeepClone(),
                    ["reworkTaskIds"] = new JsonArray(taskIds.Select(id => (JsonNode)JsonValue.Create(id.ToString())!).ToArray()),
                },
            };
        }
    }

    private static async Task<CommandOutcome> PostAsync(ITenantSession s, CallerContext caller, Guid orderId, int expectedVersion, DispatchRequest cmd,
        bool allowPartial, string operationKey, CancellationToken ct)
    {
        // 1. Document locks, then a replayed operation key answers with the stored result (STK-007).
        if ((await s.Db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.outbound_order WHERE id = {orderId} FOR UPDATE").ToListAsync(ct)).Count == 0)
            throw AppException.NotFound("Order");
        await s.Db.Database.SqlQuery<Guid>(
            $"SELECT id AS \"Value\" FROM ti.outbound_order_line WHERE order_id = {orderId} ORDER BY line_number FOR UPDATE").ToListAsync(ct);
        var earlier = await s.Db.Database.SqlQuery<string>(
            $"SELECT result_reference::text AS \"Value\" FROM ti.document_operation WHERE operation_key = {operationKey}").ToListAsync(ct);
        if (earlier.Count == 1)
            return new CommandOutcome(200, JsonNode.Parse(earlier[0]));

        var order = await s.Db.Set<OutboundOrder>().SingleAsync(o => o.Id == orderId, ct);
        var lines = await s.Db.Set<OutboundOrderLine>().Where(l => l.OrderId == orderId).ToDictionaryAsync(l => l.Id, ct);
        caller.RequireWarehouse(Permissions.DispatchPost, order.WarehouseId);
        Versioning.EnsureVersion(order.Version, expectedVersion);
        if (order.Status is not (OutboundOrderStatus.PartPicked or OutboundOrderStatus.Picked or OutboundOrderStatus.PartDispatched))
            throw AppException.InvalidState("Only an order with picked stock waiting for dispatch can be dispatched.");
        if (await s.Db.Set<Shipment>().AnyAsync(x => x.OrderId == orderId && x.DispatchReference == cmd.DispatchReference, ct))
            throw AppException.InvalidState("This dispatch reference is already used on the order.");

        // 2. Picks of this order, then their staged segments (reservation ownership, OUT-005).
        var pickIds = cmd.PickIds.ToArray();
        await s.Db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.pick WHERE id = ANY({pickIds}) ORDER BY id FOR UPDATE").ToListAsync(ct);
        var picks = await s.Db.Set<Pick>().Where(p => pickIds.Contains(p.Id)).ToListAsync(ct);
        var ownIds = picks.Select(p => p.ReservationId).ToArray();
        var segmentIds = await s.Db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM ti.reservation
            WHERE (id = ANY({ownIds}) OR parent_reservation_id = ANY({ownIds})) AND state = 'Picked'
            ORDER BY id FOR UPDATE
            """).ToListAsync(ct);
        var segments = await s.Db.Set<Reservation>().Where(r => segmentIds.Contains(r.Id) || ownIds.Contains(r.Id)).ToListAsync(ct);
        var staged = new List<(Pick Pick, Reservation Segment)>();
        foreach (var id in pickIds)
        {
            var pick = picks.SingleOrDefault(p => p.Id == id);
            var own = pick is null ? null : segments.Single(r => r.Id == pick.ReservationId);
            if (pick is null || own is null || !lines.ContainsKey(own.OrderLineId))
                throw AppException.Validation("pickIds", $"Pick {id} does not belong to this order.");
            var segment = own.State == ReservationState.Picked ? own
                : segments.SingleOrDefault(r => r.ParentReservationId == own.Id && r.State == ReservationState.Picked);
            if (segment is null)
                throw AppException.Validation("pickIds", $"Pick {id} has nothing staged to dispatch.");
            staged.Add((pick, segment));
        }
        if (!allowPartial)
        {
            var chosen = staged.Select(x => x.Segment.Id).ToArray();
            var lineIds = lines.Keys.ToArray();
            var others = (await s.Db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM ti.reservation
                WHERE order_line_id = ANY({lineIds}) AND state = 'Picked' AND NOT (id = ANY({chosen}))
                """).ToListAsync(ct)).Single();
            if (others > 0 || lines.Values.Any(l => l.PickedBaseQty != l.RequestedBaseQty))
                throw AppException.InvalidState("Dispatch ships the whole order unless allowPartial is set: pick every requested quantity and name every staged pick.");
        }

        // 3. Rechecks in the deduction transaction: quality, expiry, blocks (OUT-005, OUT-006, STK-001).
        if (await StockBlocks.TenantBlockedAsync(s, ct))
            throw new AppException(ErrorCode.StockBlocked, "Stock changes are blocked while an audit-chain failure is under investigation (OPS-001).");
        var bucketIds = staged.Select(x => x.Segment.BucketId).Distinct().Order().ToArray();
        var buckets = (await s.Db.Database.SqlQuery<BucketRow>($"""
            SELECT b.id AS "Id", b.bin_id AS "BinId", b.lot_id AS "LotId", b.serial_id AS "SerialId", b.quality_status::text AS "Quality",
                   l.expiry_date AS "Expiry", b.on_hand_qty AS "OnHand", b.reserved_qty AS "Reserved"
            FROM ti.stock_bucket b LEFT JOIN ti.lot l ON l.tenant_id = b.tenant_id AND l.id = b.lot_id
            WHERE b.id = ANY({bucketIds}) ORDER BY b.id FOR UPDATE OF b
            """).ToListAsync(ct)).ToDictionary(b => b.Id);
        var blocked = await StockBlocks.BlockedBucketsAsync(s, bucketIds, ct);
        var timeZone = await s.Db.Set<Warehouse>().AsNoTracking().Where(w => w.Id == order.WarehouseId).Select(w => w.TimeZone).SingleAsync(ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(s.Now, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).DateTime);
        var affected = new JsonArray();
        foreach (var (pick, segment) in staged)
        {
            var bucket = buckets[segment.BucketId];
            var cause = bucket.Quality != nameof(QualityStatus.Available) ? $"The staged stock is on {bucket.Quality}."
                : !ReceivingRules.IsShippable(bucket.Expiry, today) ? $"The lot expired on {bucket.Expiry:yyyy-MM-dd}."
                : blocked.Contains(bucket.Id) ? "The staged stock is blocked by an open reconciliation alert (OPS-001)."
                : null;
            if (cause is not null)
                affected.Add(new JsonObject
                {
                    ["pickId"] = pick.Id.ToString(), ["cause"] = cause, ["stagingBinId"] = bucket.BinId.ToString(),
                    ["productId"] = pick.ProductId.ToString(), ["lotId"] = bucket.LotId?.ToString(), ["quantity"] = Quantity.Format(segment.Qty),
                });
        }
        if (affected.Count > 0)
            throw new AppException(ErrorCode.StockBlocked, "Picked stock became blocked after picking; nothing was dispatched. Rework the affected picks (OUT-006).")
            {
                Extensions = new JsonObject { ["affectedPicks"] = affected },
            };
        foreach (var group in staged.GroupBy(x => x.Segment.BucketId))
        {
            var quantity = group.Sum(x => x.Segment.Qty);
            if (buckets[group.Key].OnHand < quantity || buckets[group.Key].Reserved < quantity)
                throw AppException.InvalidState("The staging bucket no longer holds the picked quantity; run reconciliation (OPS-001).");
        }

        // 4. Ship: one shipment line and one Dispatch movement per segment; on hand and reserved leave together (STK-002).
        var shipment = Shipment.Create(caller.TenantId, order.Id, order.WarehouseId, cmd.DispatchReference, caller.Subject, s.Now);
        s.Db.Add(shipment);
        var shipmentLines = new List<ShipmentLine>();
        foreach (var (pick, segment) in staged.OrderBy(x => x.Segment.Id))
        {
            var bucket = buckets[segment.BucketId];
            var lineId = Guid.CreateVersion7();
            var movement = StockMovement.Dispatch(caller.TenantId, $"dispatch:{shipment.Id:D}:{segment.Id:D}", segment.ProductId, bucket.LotId,
                bucket.SerialId, bucket.Id, segment.Qty, shipment.Id, lineId, segment.Id, caller.Subject, s.Now, caller.CorrelationId);
            s.Db.Add(movement);
            var shipmentLine = ShipmentLine.Create(lineId, caller.TenantId, shipment.Id, segment.OrderLineId, segment.ProductId, bucket.LotId, bucket.SerialId,
                segment.Qty, movement.Id, caller.Subject, s.Now);
            s.Db.Add(shipmentLine);
            shipmentLines.Add(shipmentLine);
            await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.stock_bucket SET on_hand_qty = on_hand_qty - {segment.Qty}, reserved_qty = reserved_qty - {segment.Qty}, updated_at = {s.Now},
                       version = version + 1
                WHERE id = {bucket.Id}
                """, ct);
            DomainGuard.Run(() => segment.Consume(s.Now));
            DomainGuard.Run(() => lines[segment.OrderLineId].Dispatch(segment.Qty, s.Now));
        }
        var dispatchedPicks = staged.Select(x => (Guid?)x.Pick.Id).ToList();
        foreach (var task in await s.Db.Set<WarehouseTask>()
                     .Where(t => t.Type == TaskType.Rework && t.RelatedType == "Pick" && dispatchedPicks.Contains(t.RelatedId)
                         && (t.Status == Domain.Inbound.TaskStatus.Open || t.Status == Domain.Inbound.TaskStatus.InProgress))
                     .ToListAsync(ct))
            DomainGuard.Run(() => task.Confirm(task.RemainingQty ?? 0, caller.Subject, "Dispatched after the block cleared.", s.Now));
        DomainGuard.Run(() => order.Recalculate(lines.Values.Select(l => l.Progress).ToList(), s.Now));
        await s.Db.SaveChangesAsync(ct);

        var result = new DispatchResult(shipment.Id, await LineDtosAsync(s, shipmentLines, ct), order.Status, order.Version);
        await s.Db.Database.ExecuteSqlAsync($"""
            INSERT INTO ti.document_operation (tenant_id, operation_key, operation_type, result_reference, created_by, created_at)
            VALUES ({caller.TenantId}, {operationKey}, 'Dispatch', {JsonSerializer.Serialize(result, JsonDefaults.Options)}::jsonb, {caller.Subject}, {s.Now})
            """, ct);
        s.Audit(new AuditEntry("OrderDispatched", "OutboundOrder", order.Id, order.Version, null, null, result));
        s.Publish("OrderDispatched", new { orderId = order.Id, shipmentId = shipment.Id, dispatchReference = cmd.DispatchReference });
        await s.Db.SaveChangesAsync(ct);
        return new CommandOutcome(200, JsonSerializer.SerializeToNode(result, JsonDefaults.Options));
    }

    /// <summary>One open rework task per affected pick, at its staging bin (OUT-006, DEC-055).</summary>
    private static async Task<List<Guid>> ReworkAsync(ITenantSession s, CallerContext caller, JsonArray affected, CancellationToken ct)
    {
        var ids = new List<Guid>();
        foreach (var item in affected)
        {
            var pickId = Guid.Parse((string)item!["pickId"]!);
            var existing = await s.Db.Set<WarehouseTask>().Where(t => t.Type == TaskType.Rework && t.RelatedType == "Pick" && t.RelatedId == pickId
                    && (t.Status == Domain.Inbound.TaskStatus.Open || t.Status == Domain.Inbound.TaskStatus.InProgress))
                .Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
            if (existing is { } id)
            {
                ids.Add(id);
                continue;
            }
            var pick = await s.Db.Set<Pick>().AsNoTracking().SingleAsync(p => p.Id == pickId, ct);
            var lot = (string?)item["lotId"];
            var task = WarehouseTask.ForRework(caller.TenantId, pick.WarehouseId, pickId, pick.ProductId, lot is null ? null : Guid.Parse(lot),
                Guid.Parse((string)item["stagingBinId"]!), Quantity.Parse((string)item["quantity"]!).Value,
                $"{(string)item["cause"]!} Unpick or segregate the staged stock (OUT-006).", caller.Subject, s.Now);
            s.Db.Add(task);
            s.Audit(new AuditEntry("ReworkTaskCreated", "WarehouseTask", task.Id, task.Version, task.Reason, null, new { task.Id, pickId, task.Reason }));
            ids.Add(task.Id);
        }
        await s.Db.SaveChangesAsync(ct);
        return ids;
    }

    public Task<Page<ShipmentDto>> ListAsync(ShipmentFilter filter, PageRequest page, CancellationToken ct)
    {
        var scope = callers.Caller.WarehouseFilter(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var orders = s.Db.Set<OutboundOrder>().AsNoTracking();
            var query = s.Db.Set<Shipment>().AsNoTracking();
            if (scope is not null)
                query = query.Where(x => scope.Contains(x.WarehouseId));
            if (filter.WarehouseId is { } w)
                query = query.Where(x => x.WarehouseId == w);
            if (filter.OrderId is { } o)
                query = query.Where(x => x.OrderId == o);
            if (filter.CustomerId is { } customer)
                query = query.Where(x => orders.Any(order => order.Id == x.OrderId && order.CustomerId == customer));
            if (filter.From is { } from)
                query = query.Where(x => x.DispatchedAt >= from);
            if (filter.To is { } to)
                query = query.Where(x => x.DispatchedAt < to);
            var rows = await query.ToPageAsync(page,
                $"shipments|{filter.WarehouseId}|{filter.OrderId}|{filter.CustomerId}|{filter.From:O}|{filter.To:O}", cursors, x => x, c);
            var dtos = new List<ShipmentDto>();
            foreach (var shipment in rows.Items)
                dtos.Add(await DtoAsync(s, shipment, c));
            return new Page<ShipmentDto>(dtos, rows.NextCursor);
        }, ct);
    }

    public Task<ShipmentDto> GetAsync(Guid id, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var shipment = await s.Db.Set<Shipment>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, c) ?? throw AppException.NotFound("Shipment");
            caller.RequireWarehouse(Permissions.StockRead, shipment.WarehouseId);
            return await DtoAsync(s, shipment, c);
        }, ct);
    }

    private static async Task<ShipmentDto> DtoAsync(ITenantSession s, Shipment x, CancellationToken ct)
    {
        var customer = await s.Db.Set<OutboundOrder>().AsNoTracking().Where(o => o.Id == x.OrderId).Select(o => o.CustomerId).SingleAsync(ct);
        var lines = await s.Db.Set<ShipmentLine>().AsNoTracking().Where(l => l.ShipmentId == x.Id).OrderBy(l => l.Id).ToListAsync(ct);
        // Append-only: version 1 and the creation time as the update time (DEC-055).
        return new ShipmentDto(x.Id, 1, x.CreatedAt, x.CreatedBy, x.CreatedAt, x.OrderId, x.WarehouseId, customer, x.DispatchReference, x.DispatchedAt,
            ShipmentStatus.Dispatched, await LineDtosAsync(s, lines, ct));
    }

    private static async Task<List<ShipmentLineDto>> LineDtosAsync(ITenantSession s, IReadOnlyCollection<ShipmentLine> lines, CancellationToken ct)
    {
        var serialIds = lines.Where(l => l.SerialId is not null).Select(l => l.SerialId!.Value).ToList();
        var serials = await s.Db.Set<Serial>().AsNoTracking().Where(x => serialIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.SerialNumber, ct);
        return lines.Select(l => new ShipmentLineDto(l.Id, l.OrderLineId, l.ProductId, l.LotId,
            l.SerialId is { } sid ? [serials[sid]] : [], Quantity.From(l.BaseQty), Quantity.From(l.ReturnedQty))).ToList();
    }

    private sealed record BucketRow(Guid Id, Guid BinId, Guid? LotId, Guid? SerialId, string Quality, DateOnly? Expiry, decimal OnHand, decimal Reserved);
}

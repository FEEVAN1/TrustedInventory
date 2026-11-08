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
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Outbound;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Outbound;

public sealed record AllocationLine(Guid ReservationId, Guid OrderLineId, Guid BinId, Guid? LotId, Quantity Quantity, DateTimeOffset ExpiresAt);

public sealed record Shortage(Guid OrderLineId, Quantity Quantity);

public sealed record AllocationResult(Guid OrderId, IReadOnlyList<AllocationLine> Allocations, IReadOnlyList<Shortage> Shortages,
    OutboundOrderStatus Status, int Version);

public sealed record AllocateRequest(bool? AllowPartial);

public sealed record AllocateFefoRequest(Guid OrderId, bool? AllowPartial);

public sealed record DeallocateRequest(IReadOnlyList<Guid>? OrderLineIds, string Reason);

public sealed record DeallocateResult(IReadOnlyList<Guid> ReleasedReservationIds, OutboundOrderStatus Status, int Version);

public sealed record ReservationDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, Guid OrderId, Guid OrderLineId, Guid BucketId,
    Guid BinId, Guid? LotId, Quantity Quantity, ReservationState State, DateTimeOffset? ExpiresAt, Guid? ParentReservationId);

public sealed record ReservationFilter(Guid? OrderId, Guid? WarehouseId, ReservationState? State);

public sealed class DeallocateRequestValidator : AbstractValidator<DeallocateRequest>
{
    public DeallocateRequestValidator()
    {
        RuleFor(x => x.Reason).ValidReason();
        RuleFor(x => x.OrderLineIds!).Must(l => l.Count is >= 1 and <= 500 && l.Distinct().Count() == l.Count)
            .WithMessage("Send 1 to 500 distinct line ids, or omit them to release every active reservation.").When(x => x.OrderLineIds is not null);
    }
}

/// <summary>
/// Allocation (OUT-002, STK-005, STK-006, STK-008, DEC-053). Locks order row → lines → candidate buckets in rotation
/// order → audit stream, revalidates on the locked rows, and reserves the unreserved remainder of each line. Bounded
/// deadlock retries come from the unit of work.
/// </summary>
public sealed class AllocationService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IValidator<DeallocateRequest> deallocateValidator)
{
    public static readonly TimeSpan ReservationLifetime = TimeSpan.FromHours(24);

    public Task<CommandOutcome> AllocateAsync(Guid idempotencyKey, Guid orderId, int? expectedVersion, bool allowPartial, bool forceFefo, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.OrdersAllocate);
        caller.RequireMutable();
        var route = forceFefo ? "POST /orders/allocate-fefo" : "POST /orders/{id}/allocate";
        var payload = JsonSerializer.SerializeToNode(new { orderId, expectedVersion, allowPartial }, JsonDefaults.Options);

        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, route, payload), async (s, c) =>
        {
            if (await StockBlocks.TenantBlockedAsync(s, c))
                throw new AppException(ErrorCode.StockBlocked, "Stock changes are blocked while an audit-chain failure is under investigation (OPS-001).");

            // 1. Document locks: the order row, then its lines in line order.
            var (order, lines) = await LockOrderAsync(s, orderId, c);
            caller.RequireWarehouse(Permissions.OrdersAllocate, order.WarehouseId);
            if (expectedVersion is { } v)
                Versioning.EnsureVersion(order.Version, v);
            if (!order.IsAllocatable)
                throw AppException.InvalidState("Only a confirmed order that is not finished can be allocated.");

            // 2. Reference data: effective dispatch date and the zones each product may be stored in (DEC-012).
            var warehouse = await s.Db.Set<Warehouse>().AsNoTracking().SingleAsync(w => w.Id == order.WarehouseId, c);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(s.Now, TimeZoneInfo.FindSystemTimeZoneById(warehouse.TimeZone)).DateTime);
            var effective = order.PlannedDispatchDate > today ? order.PlannedDispatchDate : today;
            var productIds = lines.Select(l => l.ProductId).Distinct().ToList();
            var products = await s.Db.Set<Product>().AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, c);
            var profileIds = products.Values.Where(p => p.StorageProfileId is not null).Select(p => p.StorageProfileId!.Value).ToList();
            var profiles = await s.Db.Set<StorageProfile>().AsNoTracking().Where(p => profileIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, c);
            var zones = await s.Db.Set<Zone>().AsNoTracking().Where(z => z.WarehouseId == order.WarehouseId && z.Active).ToListAsync(c);
            var zoneIds = zones.Select(z => z.Id).ToList();
            var hazards = (await s.Db.Set<ZoneAllowedHazardClass>().AsNoTracking().Where(h => zoneIds.Contains(h.ZoneId)).ToListAsync(c))
                .ToLookup(h => h.ZoneId, h => h.HazardClass);

            // 3. Each line in order: lock its candidates in rotation order, revalidate, reserve.
            var allocations = new List<AllocationLine>();
            var shortages = new List<Shortage>();
            var expiresAt = s.Now + ReservationLifetime;
            foreach (var line in lines.OrderBy(l => l.LineNumber))
            {
                var remaining = line.Unallocated;
                if (remaining <= 0)
                    continue;
                var product = products[line.ProductId];
                var profile = product.StorageProfileId is { } pid ? profiles.GetValueOrDefault(pid) : null;
                var compatible = zones.Where(z => StorageCompatibility.IsCompatible(z.TempMinC, z.TempMaxC, profile?.MinC, profile?.MaxC, product.HazardClass,
                    hazards[z.Id].ToList())).Select(z => z.Id).ToArray();
                var rule = forceFefo || product.HandlingProfile == HandlingProfile.Medical ? RotationRule.FEFO : product.RotationRule;

                foreach (var candidate in await CandidatesAsync(s, order.WarehouseId, product.Id, compatible, effective, line.MinRemainingShelfLifeDays, rule, c))
                {
                    var take = Math.Min(candidate.OnHand - candidate.Reserved, remaining); // revalidated on the locked row
                    if (take <= 0)
                        continue;
                    var reservation = Reservation.Create(caller.TenantId, line.Id, product.Id, candidate.Id, take, expiresAt, caller.Subject, s.Now);
                    s.Db.Add(reservation);
                    await s.Db.Database.ExecuteSqlAsync($"""
                        UPDATE ti.stock_bucket SET reserved_qty = reserved_qty + {take}, updated_at = {s.Now}, version = version + 1 WHERE id = {candidate.Id}
                        """, c);
                    DomainGuard.Run(() => line.Allocate(take, s.Now));
                    allocations.Add(new AllocationLine(reservation.Id, line.Id, candidate.BinId, candidate.LotId, Quantity.From(take), expiresAt));
                    remaining -= take;
                    if (remaining == 0)
                        break;
                }
                if (remaining > 0)
                    shortages.Add(new Shortage(line.Id, Quantity.From(remaining)));
            }

            // 4. All-or-nothing (default): any shortage rolls every line back (AT-OUT-01).
            if (shortages.Count > 0 && !allowPartial)
                throw new AppException(ErrorCode.InsufficientStock, "Not enough eligible stock for every line; nothing was reserved.")
                {
                    Extensions = new JsonObject
                    {
                        ["shortages"] = new JsonArray(shortages.Select(x => (JsonNode)new JsonObject
                        {
                            ["orderLineId"] = x.OrderLineId.ToString(),
                            ["quantity"] = x.Quantity.Raw,
                        }).ToArray()),
                    },
                };

            DomainGuard.Run(() => order.Recalculate(lines.Select(l => l.Progress).ToList(), s.Now));
            var result = new AllocationResult(order.Id, allocations, shortages, order.Status, order.Version);
            if (allocations.Count > 0)
            {
                s.Audit(new AuditEntry("OutboundOrderAllocated", "OutboundOrder", order.Id, order.Version, null, null, result));
                s.Publish("OutboundOrderAllocated", new { orderId = order.Id, reservations = allocations.Select(a => a.ReservationId) });
            }
            await s.Db.SaveChangesAsync(c);
            return new CommandOutcome(200, JsonSerializer.SerializeToNode(result, JsonDefaults.Options));
        }, ct);
    }

    /// <summary>A safety operation: releases Active reservations of the named lines (or all); picked stock needs unpick first.</summary>
    public Task<CommandOutcome> DeallocateAsync(Guid idempotencyKey, Guid orderId, int expectedVersion, DeallocateRequest cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.OrdersAllocate);
        deallocateValidator.ValidateOrThrow(cmd);
        var payload = JsonSerializer.SerializeToNode(new { orderId, expectedVersion, cmd.OrderLineIds, cmd.Reason }, JsonDefaults.Options);

        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, "POST /orders/{id}/deallocate", payload), async (s, c) =>
        {
            var (order, lines) = await LockOrderAsync(s, orderId, c);
            caller.RequireWarehouse(Permissions.OrdersAllocate, order.WarehouseId);
            Versioning.EnsureVersion(order.Version, expectedVersion);
            var chosen = cmd.OrderLineIds?.ToList() ?? lines.Select(l => l.Id).ToList();
            if (chosen.Except(lines.Select(l => l.Id)).Any())
                throw AppException.Validation("orderLineIds", "Every line must belong to this order.");

            var released = await ReservationRelease.ReleaseActiveAsync(s, chosen, lines.ToDictionary(l => l.Id), c);
            DomainGuard.Run(() => order.Recalculate(lines.Select(l => l.Progress).ToList(), s.Now));
            var result = new DeallocateResult(released, order.Status, order.Version);
            s.Audit(new AuditEntry("OutboundOrderDeallocated", "OutboundOrder", order.Id, order.Version, cmd.Reason, null, result));
            s.Publish("OutboundOrderDeallocated", new { orderId = order.Id, releasedReservations = released });
            await s.Db.SaveChangesAsync(c);
            return new CommandOutcome(200, JsonSerializer.SerializeToNode(result, JsonDefaults.Options));
        }, ct);
    }

    public Task<Page<ReservationDto>> ListReservationsAsync(ReservationFilter filter, PageRequest page, CancellationToken ct)
    {
        var scope = callers.Caller.WarehouseFilter(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var buckets = s.Db.Set<Domain.Inbound.StockBucket>().AsNoTracking();
            var orderLines = s.Db.Set<OutboundOrderLine>().AsNoTracking();
            var query = s.Db.Set<Reservation>().AsNoTracking();
            if (scope is not null)
                query = query.Where(r => buckets.Any(b => b.Id == r.BucketId && scope.Contains(b.WarehouseId)));
            if (filter.WarehouseId is { } w)
                query = query.Where(r => buckets.Any(b => b.Id == r.BucketId && b.WarehouseId == w));
            if (filter.OrderId is { } o)
                query = query.Where(r => orderLines.Any(l => l.Id == r.OrderLineId && l.OrderId == o));
            if (filter.State is { } state)
                query = query.Where(r => r.State == state);
            var rows = await query.ToPageAsync(page, $"reservations|{filter.OrderId}|{filter.WarehouseId}|{filter.State}", cursors, r => r, c);
            var bucketIds = rows.Items.Select(r => r.BucketId).Distinct().ToList();
            var bucketMap = await buckets.Where(b => bucketIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, c);
            var lineIds = rows.Items.Select(r => r.OrderLineId).Distinct().ToList();
            var lineMap = await orderLines.Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.OrderId, c);
            return new Page<ReservationDto>(rows.Items.Select(r => new ReservationDto(r.Id, r.Version, r.CreatedAt, r.CreatedBy, r.UpdatedAt,
                lineMap[r.OrderLineId], r.OrderLineId, r.BucketId, bucketMap[r.BucketId].BinId, bucketMap[r.BucketId].LotId, Quantity.From(r.Qty), r.State,
                r.ExpiresAt, r.ParentReservationId)).ToList(), rows.NextCursor);
        }, ct);
    }

    private static async Task<(OutboundOrder Order, List<OutboundOrderLine> Lines)> LockOrderAsync(ITenantSession s, Guid orderId, CancellationToken ct)
    {
        if ((await s.Db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.outbound_order WHERE id = {orderId} FOR UPDATE").ToListAsync(ct)).Count == 0)
            throw AppException.NotFound("Order");
        await s.Db.Database.SqlQuery<Guid>(
            $"SELECT id AS \"Value\" FROM ti.outbound_order_line WHERE order_id = {orderId} ORDER BY line_number FOR UPDATE").ToListAsync(ct);
        var order = await s.Db.Set<OutboundOrder>().SingleAsync(o => o.Id == orderId, ct);
        var lines = await s.Db.Set<OutboundOrderLine>().Where(l => l.OrderId == orderId).ToListAsync(ct);
        return (order, lines);
    }

    /// <summary>
    /// Eligible buckets (OUT-002, DEC-053) locked FOR UPDATE in deterministic rotation order (STK-005). In READ COMMITTED
    /// a row changed by a concurrent transaction is re-read after the lock, so the returned quantities are current.
    /// </summary>
    private static async Task<List<Candidate>> CandidatesAsync(ITenantSession s, Guid warehouseId, Guid productId, Guid[] zones, DateOnly effective,
        int minShelfLifeDays, RotationRule rule, CancellationToken ct)
    {
        FormattableString sql = rule switch
        {
            RotationRule.FEFO => $"""
                SELECT b.id AS "Id", b.bin_id AS "BinId", b.lot_id AS "LotId", b.on_hand_qty AS "OnHand", b.reserved_qty AS "Reserved"
                FROM ti.stock_bucket b
                JOIN ti.bin bn ON bn.tenant_id = b.tenant_id AND bn.id = b.bin_id
                LEFT JOIN ti.lot l ON l.tenant_id = b.tenant_id AND l.id = b.lot_id
                WHERE b.warehouse_id = {warehouseId} AND b.product_id = {productId} AND b.quality_status = 'Available'
                  AND bn.kind = 'Storage' AND bn.active AND bn.zone_id = ANY({zones}) AND b.on_hand_qty > b.reserved_qty
                  AND (l.expiry_date IS NULL OR (l.expiry_date > {effective} AND l.expiry_date - {effective} >= {minShelfLifeDays}))
                  AND NOT EXISTS (SELECT 1 FROM ti.stock_bucket_block k JOIN ti.alert a ON a.tenant_id = k.tenant_id AND a.id = k.alert_id
                                  WHERE k.bucket_id = b.id AND a.status <> 'Closed')
                ORDER BY l.expiry_date ASC NULLS LAST, b.received_at ASC, b.lot_id ASC, b.id ASC
                FOR UPDATE OF b
                """,
            RotationRule.LIFO => $"""
                SELECT b.id AS "Id", b.bin_id AS "BinId", b.lot_id AS "LotId", b.on_hand_qty AS "OnHand", b.reserved_qty AS "Reserved"
                FROM ti.stock_bucket b
                JOIN ti.bin bn ON bn.tenant_id = b.tenant_id AND bn.id = b.bin_id
                LEFT JOIN ti.lot l ON l.tenant_id = b.tenant_id AND l.id = b.lot_id
                WHERE b.warehouse_id = {warehouseId} AND b.product_id = {productId} AND b.quality_status = 'Available'
                  AND bn.kind = 'Storage' AND bn.active AND bn.zone_id = ANY({zones}) AND b.on_hand_qty > b.reserved_qty
                  AND (l.expiry_date IS NULL OR (l.expiry_date > {effective} AND l.expiry_date - {effective} >= {minShelfLifeDays}))
                  AND NOT EXISTS (SELECT 1 FROM ti.stock_bucket_block k JOIN ti.alert a ON a.tenant_id = k.tenant_id AND a.id = k.alert_id
                                  WHERE k.bucket_id = b.id AND a.status <> 'Closed')
                ORDER BY b.received_at DESC, b.id ASC
                FOR UPDATE OF b
                """,
            _ => $"""
                SELECT b.id AS "Id", b.bin_id AS "BinId", b.lot_id AS "LotId", b.on_hand_qty AS "OnHand", b.reserved_qty AS "Reserved"
                FROM ti.stock_bucket b
                JOIN ti.bin bn ON bn.tenant_id = b.tenant_id AND bn.id = b.bin_id
                LEFT JOIN ti.lot l ON l.tenant_id = b.tenant_id AND l.id = b.lot_id
                WHERE b.warehouse_id = {warehouseId} AND b.product_id = {productId} AND b.quality_status = 'Available'
                  AND bn.kind = 'Storage' AND bn.active AND bn.zone_id = ANY({zones}) AND b.on_hand_qty > b.reserved_qty
                  AND (l.expiry_date IS NULL OR (l.expiry_date > {effective} AND l.expiry_date - {effective} >= {minShelfLifeDays}))
                  AND NOT EXISTS (SELECT 1 FROM ti.stock_bucket_block k JOIN ti.alert a ON a.tenant_id = k.tenant_id AND a.id = k.alert_id
                                  WHERE k.bucket_id = b.id AND a.status <> 'Closed')
                ORDER BY b.received_at ASC, b.id ASC
                FOR UPDATE OF b
                """,
        };
        return await s.Db.Database.SqlQuery<Candidate>(sql).ToListAsync(ct);
    }

    private sealed record Candidate(Guid Id, Guid BinId, Guid? LotId, decimal OnHand, decimal Reserved);
}

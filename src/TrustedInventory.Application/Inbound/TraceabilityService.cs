using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Operations;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Inbound;

public sealed record LotDto(Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, Guid ProductId, string LotNumber,
    DateOnly? ExpiryDate, string? SupplierRef, bool ExpiryCorrected) : IVersioned;

public sealed record TraceProduct(Guid Id, string Sku, string Name);

public sealed record TraceReceipt(Guid ReceiptId, Guid ReceiptLineId, Guid? PurchaseOrderId, Guid? SupplierId, Guid WarehouseId, Quantity Quantity,
    DateTimeOffset ReceivedAt, QualityStatus QualityStatus);

public sealed record TraceQualityChange(DateTimeOffset OccurredAt, Guid BucketId, QualityStatus From, QualityStatus To, string? Reason, Guid? ApprovalId);

public sealed record TraceTransit(Guid TransferId, Quantity Quantity);

public sealed record TraceShipment(Guid ShipmentId, Guid ShipmentLineId, Guid OrderId, Guid CustomerId, Quantity Quantity, Quantity ReturnedQuantity,
    DateTimeOffset DispatchedAt);

public sealed record TraceReturn(Guid ReturnId, Quantity Quantity, ReturnStatus Status, ReturnDisposition? Disposition);

public sealed record TraceRecall(Guid RecallId, RecallStatus Status, DateTimeOffset EffectiveAt);

public sealed record TraceExcursion(Guid ExcursionId, ExcursionStatus Status, DateTimeOffset StartedAt);

public sealed record TraceabilityReport(LotDto Lot, TraceProduct Product, IReadOnlyList<TraceReceipt> Receipts, IReadOnlyList<TraceQualityChange> QualityHistory,
    IReadOnlyList<StockMovementDto> Movements, IReadOnlyList<StockBucketDto> CurrentStock, IReadOnlyList<TraceTransit> InTransit,
    IReadOnlyList<TraceShipment> Shipments, IReadOnlyList<TraceReturn> Returns, IReadOnlyList<TraceRecall> Recalls, IReadOnlyList<TraceExcursion> Excursions);

/// <summary>
/// Receipt-to-customer lineage of one lot (section 02 success criteria, M4 carry-over, DEC-078): built from the append-only ledger and the
/// documents that reference the lot, in one read-only tenant transaction. Historical facts are never recomputed from current state.
/// </summary>
public sealed class TraceabilityService(ITenantUnitOfWork uow, ICallerAccessor callers)
{
    public Task<TraceabilityReport> GetAsync(Guid lotId, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.StockRead);
        return uow.ReadAsync((s, c) => BuildAsync(s, lotId, c), ct);
    }

    internal static async Task<TraceabilityReport> BuildAsync(ITenantSession s, Guid lotId, CancellationToken ct)
    {
        var lot = await s.Db.Set<Lot>().AsNoTracking().SingleOrDefaultAsync(l => l.Id == lotId, ct) ?? throw AppException.NotFound("Lot");
        var product = await s.Db.Set<Product>().AsNoTracking().SingleAsync(p => p.Id == lot.ProductId, ct);
        var receipts = await s.Db.Database.SqlQuery<ReceiptRow>($"""
            SELECT r.id AS "ReceiptId", rl.id AS "LineId", r.purchase_order_id AS "PurchaseOrderId", po.supplier_id AS "SupplierId", r.warehouse_id AS "WarehouseId",
                   rl.base_qty AS "Qty", coalesce(r.received_at, rl.created_at) AS "ReceivedAt", rl.quality_status::text AS "Quality"
            FROM ti.receipt_line rl JOIN ti.receipt r ON r.tenant_id = rl.tenant_id AND r.id = rl.receipt_id
            LEFT JOIN ti.purchase_order po ON po.tenant_id = r.tenant_id AND po.id = r.purchase_order_id
            WHERE rl.lot_id = {lotId} ORDER BY coalesce(r.received_at, rl.created_at), rl.id
            """).ToListAsync(ct);
        var quality = await s.Db.Database.SqlQuery<QualityRow>($"""
            SELECT m.occurred_at AS "OccurredAt", m.destination_bucket_id AS "BucketId", src.quality_status::text AS "From", dst.quality_status::text AS "To",
                   m.reason AS "Reason"
            FROM ti.stock_movement m
            JOIN ti.stock_bucket src ON src.tenant_id = m.tenant_id AND src.id = m.source_bucket_id
            JOIN ti.stock_bucket dst ON dst.tenant_id = m.tenant_id AND dst.id = m.destination_bucket_id
            WHERE m.lot_id = {lotId} AND m.movement_type = 'QualityChange' ORDER BY m.occurred_at, m.id
            """).ToListAsync(ct);
        var movements = await s.Db.Set<StockMovement>().AsNoTracking().Where(m => m.LotId == lotId).OrderBy(m => m.OccurredAt).ThenBy(m => m.Id).ToListAsync(ct);
        var buckets = await s.Db.Set<StockBucket>().AsNoTracking().Where(b => b.LotId == lotId && b.OnHandQty > 0).OrderBy(b => b.Id).ToListAsync(ct);
        var transit = await s.Db.Database.SqlQuery<TransitRow>($"""
            SELECT l.transfer_id AS "TransferId", l.dispatched_qty - l.received_qty - l.disposed_qty AS "Qty"
            FROM ti.transfer_line l JOIN ti.transfer t ON t.tenant_id = l.tenant_id AND t.id = l.transfer_id
            WHERE l.lot_id = {lotId} AND t.status IN ('InTransit', 'PartReceived') AND l.dispatched_qty > l.received_qty + l.disposed_qty
            ORDER BY l.transfer_id
            """).ToListAsync(ct);
        var shipments = await s.Db.Database.SqlQuery<ShipmentRow>($"""
            SELECT sh.id AS "ShipmentId", sl.id AS "LineId", sh.order_id AS "OrderId", o.customer_id AS "CustomerId", sl.base_qty AS "Qty",
                   sl.returned_qty AS "Returned", sh.dispatched_at AS "DispatchedAt"
            FROM ti.shipment_line sl JOIN ti.shipment sh ON sh.tenant_id = sl.tenant_id AND sh.id = sl.shipment_id
            JOIN ti.outbound_order o ON o.tenant_id = sh.tenant_id AND o.id = sh.order_id
            WHERE sl.lot_id = {lotId} ORDER BY sh.dispatched_at, sl.id
            """).ToListAsync(ct);
        var returns = await s.Db.Database.SqlQuery<ReturnRow>($"""
            SELECT cr.id AS "ReturnId", cr.returned_qty AS "Qty", cr.status::text AS "Status", cr.disposition::text AS "Disposition"
            FROM ti.customer_return cr JOIN ti.shipment_line sl ON sl.tenant_id = cr.tenant_id AND sl.id = cr.shipment_line_id
            WHERE sl.lot_id = {lotId} ORDER BY cr.created_at, cr.id
            """).ToListAsync(ct);
        var recalls = await s.Db.Database.SqlQuery<RecallRow>($"""
            SELECT r.id AS "RecallId", r.status::text AS "Status", r.effective_at AS "EffectiveAt" FROM ti.recall r
            WHERE r.product_id = {lot.ProductId} AND (r.all_lots OR EXISTS (SELECT 1 FROM ti.recall_item i WHERE i.recall_id = r.id AND i.lot_id = {lotId}))
            ORDER BY r.effective_at, r.id
            """).ToListAsync(ct);
        var excursions = await s.Db.Database.SqlQuery<ExcursionRow>($"""
            SELECT DISTINCT e.id AS "ExcursionId", e.status::text AS "Status", e.started_at AS "StartedAt"
            FROM ti.excursion e JOIN ti.excursion_bucket eb ON eb.tenant_id = e.tenant_id AND eb.excursion_id = e.id
            JOIN ti.stock_bucket b ON b.tenant_id = eb.tenant_id AND b.id = eb.bucket_id
            WHERE b.lot_id = {lotId} ORDER BY e.started_at
            """).ToListAsync(ct);

        return new TraceabilityReport(
            LotService.ToDto(lot, await LotService.CorrectedAsync(s, lot.Id, ct)),
            new TraceProduct(product.Id, product.Sku, product.Name),
            receipts.Select(r => new TraceReceipt(r.ReceiptId, r.LineId, r.PurchaseOrderId, r.SupplierId, r.WarehouseId, Quantity.From(r.Qty), r.ReceivedAt,
                Enum.Parse<QualityStatus>(r.Quality))).ToList(),
            quality.Select(q => new TraceQualityChange(q.OccurredAt, q.BucketId, Enum.Parse<QualityStatus>(q.From), Enum.Parse<QualityStatus>(q.To), q.Reason, null)).ToList(),
            await StockService.MovementDtosAsync(s, movements, ct),
            await StockService.DtosAsync(s, buckets, ct),
            transit.Select(t => new TraceTransit(t.TransferId, Quantity.From(t.Qty))).ToList(),
            shipments.Select(x => new TraceShipment(x.ShipmentId, x.LineId, x.OrderId, x.CustomerId, Quantity.From(x.Qty), Quantity.From(x.Returned), x.DispatchedAt)).ToList(),
            returns.Select(r => new TraceReturn(r.ReturnId, Quantity.From(r.Qty), Enum.Parse<ReturnStatus>(r.Status),
                r.Disposition is { } d ? Enum.Parse<ReturnDisposition>(d) : null)).ToList(),
            recalls.Select(r => new TraceRecall(r.RecallId, Enum.Parse<RecallStatus>(r.Status), r.EffectiveAt)).ToList(),
            excursions.Select(e => new TraceExcursion(e.ExcursionId, Enum.Parse<ExcursionStatus>(e.Status), e.StartedAt)).ToList());
    }

    private sealed record ReceiptRow(Guid ReceiptId, Guid LineId, Guid? PurchaseOrderId, Guid? SupplierId, Guid WarehouseId, decimal Qty, DateTimeOffset ReceivedAt,
        string Quality);

    private sealed record QualityRow(DateTimeOffset OccurredAt, Guid BucketId, string From, string To, string? Reason);

    private sealed record TransitRow(Guid TransferId, decimal Qty);

    private sealed record ShipmentRow(Guid ShipmentId, Guid LineId, Guid OrderId, Guid CustomerId, decimal Qty, decimal Returned, DateTimeOffset DispatchedAt);

    private sealed record ReturnRow(Guid ReturnId, decimal Qty, string Status, string? Disposition);

    private sealed record RecallRow(Guid RecallId, string Status, DateTimeOffset EffectiveAt);

    private sealed record ExcursionRow(Guid ExcursionId, string Status, DateTimeOffset StartedAt);
}

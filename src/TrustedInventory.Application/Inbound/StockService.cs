using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Inbound;

/// <summary>
/// OPS-001 stock blocks (DEC-049): an open AuditChainFailure blocks every stock mutation of the tenant; an open
/// ReconciliationMismatch blocks the buckets it lists. Every stock command checks the buckets it changes.
/// </summary>
public static class StockBlocks
{
    public static async Task<bool> TenantBlockedAsync(ITenantSession s, CancellationToken ct) =>
        (await s.Db.Database.SqlQuery<bool>(
            $"SELECT EXISTS (SELECT 1 FROM ti.alert WHERE type = 'AuditChainFailure' AND status <> 'Closed') AS \"Value\"").ToListAsync(ct)).Single();

    public static async Task<HashSet<Guid>> BlockedBucketsAsync(ITenantSession s, IReadOnlyCollection<Guid> bucketIds, CancellationToken ct)
    {
        if (bucketIds.Count == 0)
            return [];
        var ids = bucketIds.ToArray();
        return (await s.Db.Database.SqlQuery<Guid>($"""
            SELECT DISTINCT b.bucket_id AS "Value" FROM ti.stock_bucket_block b
            JOIN ti.alert a ON a.tenant_id = b.tenant_id AND a.id = b.alert_id
            WHERE a.status <> 'Closed' AND b.bucket_id = ANY({ids})
            """).ToListAsync(ct)).ToHashSet();
    }

    /// <summary>Throws 409 STOCK_BLOCKED, rolling the whole command back, when any changed bucket is blocked.</summary>
    public static async Task EnsureNotBlockedAsync(ITenantSession s, IReadOnlyCollection<Guid> bucketIds, CancellationToken ct)
    {
        if (await TenantBlockedAsync(s, ct))
            throw new AppException(ErrorCode.StockBlocked,
                "Stock changes are blocked while an audit-chain failure is under investigation (OPS-001).");
        if ((await BlockedBucketsAsync(s, bucketIds, ct)).Count > 0)
            throw new AppException(ErrorCode.StockBlocked,
                "This stock is blocked by an open reconciliation alert until operations investigate (OPS-001).");
    }
}

public sealed record StockBucketDto(
    Guid Id, int Version, Guid WarehouseId, Guid BinId, string BinCode, BinKind BinKind, Guid ProductId, string Sku, Guid? LotId,
    string? LotNumber, DateOnly? ExpiryDate, Guid? SerialId, string? SerialNumber, QualityStatus QualityStatus, Quantity OnHand,
    Quantity Reserved, Quantity Allocatable, bool Eligible, IReadOnlyList<IneligibilityReason> IneligibilityReasons, DateTimeOffset ReceivedAt,
    DateTimeOffset? LastMovementAt) : IVersioned;

public sealed record MovementLocationDto(Guid BucketId, Guid WarehouseId, Guid BinId, QualityStatus QualityStatus);

public sealed record StockMovementDto(
    Guid Id, MovementType Type, Quantity BaseQuantity, Guid ProductId, Guid? LotId, Guid? SerialId, MovementLocationDto? Source,
    MovementLocationDto? Destination, DocumentType DocumentType, Guid DocumentId, Guid? DocumentLineId, Guid? ReservationId,
    string ActorSubject, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt, string? Reason, Guid? ReversalOfId, Quantity UnreversedQuantity);

public sealed record StockFilter(Guid? WarehouseId, Guid? ProductId, Guid? LotId, Guid? BinId, QualityStatus? QualityStatus, bool EligibleOnly,
    bool IncludeEmpty);

public sealed record MovementFilter(DocumentType? DocumentType, Guid? DocumentId, DateTimeOffset? From, DateTimeOffset? To, Guid? ProductId,
    Guid? LotId, Guid? WarehouseId, MovementType? Type);

/// <summary>
/// Stock reads (STK-001, STK-004, DEC-049): buckets with computed eligibility and allocatable quantity, and the
/// append-only movement ledger. Scoped to the warehouses the caller may read (stock.read).
/// </summary>
public sealed class StockService(ITenantUnitOfWork uow, ICallerAccessor callers, ICursorCodec cursors)
{
    public Task<Page<StockBucketDto>> ListAsync(StockFilter filter, PageRequest page, CancellationToken ct)
    {
        var scope = callers.Caller.WarehouseFilter(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var query = s.Db.Set<StockBucket>().AsNoTracking();
            if (scope is not null)
                query = query.Where(b => scope.Contains(b.WarehouseId));
            if (!filter.IncludeEmpty)
                query = query.Where(b => b.OnHandQty > 0);
            if (filter.WarehouseId is { } w)
                query = query.Where(b => b.WarehouseId == w);
            if (filter.ProductId is { } p)
                query = query.Where(b => b.ProductId == p);
            if (filter.LotId is { } l)
                query = query.Where(b => b.LotId == l);
            if (filter.BinId is { } bin)
                query = query.Where(b => b.BinId == bin);
            if (filter.QualityStatus is { } q)
                query = query.Where(b => b.QualityStatus == q);
            if (filter.EligibleOnly)
                query = query.Where(b => b.QualityStatus == QualityStatus.Available); // the rest is checked per row below
            var key = $"stock|{filter.WarehouseId}|{filter.ProductId}|{filter.LotId}|{filter.BinId}|{filter.QualityStatus}|{filter.EligibleOnly}|{filter.IncludeEmpty}";
            var buckets = await query.ToPageAsync(page, key, cursors, b => b, c);
            var dtos = await DtosAsync(s, buckets.Items, c);
            // eligibleOnly is a filter on the computed result: a page can hold fewer items, the cursor still continues.
            return new Page<StockBucketDto>(filter.EligibleOnly ? dtos.Where(d => d.Eligible).ToList() : dtos, buckets.NextCursor);
        }, ct);
    }

    public Task<StockBucketDto> GetAsync(Guid id, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var bucket = await s.Db.Set<StockBucket>().AsNoTracking().SingleOrDefaultAsync(b => b.Id == id, c) ?? throw AppException.NotFound("Stock bucket");
            caller.RequireWarehouse(Permissions.StockRead, bucket.WarehouseId);
            return (await DtosAsync(s, [bucket], c))[0];
        }, ct);
    }

    public Task<Page<StockMovementDto>> ListMovementsAsync(MovementFilter filter, PageRequest page, CancellationToken ct)
    {
        var scope = callers.Caller.WarehouseFilter(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            page.EnsureSupportedSort();
            var limit = page.EffectiveLimit;
            var key = $"movements|{filter.DocumentType}|{filter.DocumentId}|{filter.From:O}|{filter.To:O}|{filter.ProductId}|{filter.LotId}|{filter.WarehouseId}|{filter.Type}";
            var buckets = s.Db.Set<StockBucket>().AsNoTracking();
            var query = s.Db.Set<StockMovement>().AsNoTracking();
            if (scope is not null)
                query = query.Where(m => buckets.Any(b => (b.Id == m.SourceBucketId || b.Id == m.DestinationBucketId) && scope.Contains(b.WarehouseId)));
            if (filter.WarehouseId is { } w)
                query = query.Where(m => buckets.Any(b => (b.Id == m.SourceBucketId || b.Id == m.DestinationBucketId) && b.WarehouseId == w));
            if (filter.DocumentType is { } dt)
                query = query.Where(m => m.DocumentType == dt);
            if (filter.DocumentId is { } did)
                query = query.Where(m => m.DocumentId == did);
            if (filter.From is { } from)
                query = query.Where(m => m.OccurredAt >= from);
            if (filter.To is { } to)
                query = query.Where(m => m.OccurredAt < to);
            if (filter.ProductId is { } p)
                query = query.Where(m => m.ProductId == p);
            if (filter.LotId is { } l)
                query = query.Where(m => m.LotId == l);
            if (filter.Type is { } t)
                query = query.Where(m => m.MovementType == t);
            if (page.Cursor is { } cursor)
            {
                var at = cursors.Decode(cursor, key);
                query = query.Where(m => m.RecordedAt < at.CreatedAt || (m.RecordedAt == at.CreatedAt && m.Id.CompareTo(at.Id) < 0));
            }
            var rows = await query.OrderByDescending(m => m.RecordedAt).ThenByDescending(m => m.Id).Take(limit + 1).ToListAsync(c);
            string? next = null;
            if (rows.Count > limit)
            {
                rows.RemoveAt(limit);
                next = cursors.Encode(new CursorPosition(rows[^1].RecordedAt, rows[^1].Id), key);
            }
            return new Page<StockMovementDto>(await MovementDtosAsync(s, rows, c), next);
        }, ct);
    }

    public Task<StockMovementDto> GetMovementAsync(Guid id, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var movement = await s.Db.Set<StockMovement>().AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, c)
                ?? throw AppException.NotFound("Stock movement");
            var dto = (await MovementDtosAsync(s, [movement], c))[0];
            var warehouse = (dto.Destination ?? dto.Source)!.WarehouseId;
            caller.RequireWarehouse(Permissions.StockRead, warehouse);
            return dto;
        }, ct);
    }

    internal static async Task<List<StockBucketDto>> DtosAsync(ITenantSession s, IReadOnlyList<StockBucket> buckets, CancellationToken ct)
    {
        var binIds = buckets.Select(b => b.BinId).Distinct().ToList();
        var bins = await s.Db.Set<Bin>().AsNoTracking().Where(b => binIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        var zoneIds = bins.Values.Select(b => b.ZoneId).Distinct().ToList();
        var zones = await s.Db.Set<Zone>().AsNoTracking().Where(z => zoneIds.Contains(z.Id)).ToDictionaryAsync(z => z.Id, ct);
        var hazards = (await s.Db.Set<ZoneAllowedHazardClass>().AsNoTracking().Where(h => zoneIds.Contains(h.ZoneId)).ToListAsync(ct))
            .ToLookup(h => h.ZoneId, h => h.HazardClass);
        var productIds = buckets.Select(b => b.ProductId).Distinct().ToList();
        var products = await s.Db.Set<Product>().AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var profileIds = products.Values.Where(p => p.StorageProfileId is not null).Select(p => p.StorageProfileId!.Value).Distinct().ToList();
        var profiles = await s.Db.Set<StorageProfile>().AsNoTracking().Where(p => profileIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var lotIds = buckets.Where(b => b.LotId is not null).Select(b => b.LotId!.Value).Distinct().ToList();
        var lots = await s.Db.Set<Lot>().AsNoTracking().Where(l => lotIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
        var serialIds = buckets.Where(b => b.SerialId is not null).Select(b => b.SerialId!.Value).Distinct().ToList();
        var serials = await s.Db.Set<Serial>().AsNoTracking().Where(x => serialIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var warehouseIds = buckets.Select(b => b.WarehouseId).Distinct().ToList();
        var timeZones = await s.Db.Set<Warehouse>().AsNoTracking().Where(w => warehouseIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.TimeZone, ct);
        var ids = buckets.Select(b => b.Id).ToList();
        // Latest movement per bucket through the newest-first indexes (0029): one index probe per bucket and direction, never the
        // bucket's whole history (DEC-081).
        var idArray = ids.ToArray();
        var lastMovement = (await s.Db.Database.SqlQuery<LastMovementRow>($"""
            SELECT b.id AS "BucketId",
                   GREATEST((SELECT m.occurred_at FROM ti.stock_movement m WHERE m.source_bucket_id = b.id ORDER BY m.occurred_at DESC LIMIT 1),
                            (SELECT m.occurred_at FROM ti.stock_movement m WHERE m.destination_bucket_id = b.id ORDER BY m.occurred_at DESC LIMIT 1)) AS "At"
            FROM ti.stock_bucket b WHERE b.id = ANY({idArray})
            """).ToListAsync(ct)).ToDictionary(r => r.BucketId, r => r.At);
        var tenantBlocked = await StockBlocks.TenantBlockedAsync(s, ct);
        var blocked = await StockBlocks.BlockedBucketsAsync(s, ids, ct);

        return buckets.Select(b =>
        {
            var bin = bins[b.BinId];
            var zone = zones[bin.ZoneId];
            var product = products[b.ProductId];
            var profile = product.StorageProfileId is { } pid ? profiles.GetValueOrDefault(pid) : null;
            var lot = b.LotId is { } lid ? lots.GetValueOrDefault(lid) : null;
            var facts = new BucketFacts(b.QualityStatus, b.OnHandQty, b.ReservedQty, lot?.ExpiryDate, WarehouseToday(timeZones[b.WarehouseId], s.Now),
                bin.Kind, bin.Active,
                StorageCompatibility.IsCompatible(zone.TempMinC, zone.TempMaxC, profile?.MinC, profile?.MaxC, product.HazardClass, hazards[zone.Id].ToList()),
                tenantBlocked || blocked.Contains(b.Id));
            var reasons = StockEligibility.Reasons(facts);
            var last = lastMovement.GetValueOrDefault(b.Id);
            return new StockBucketDto(b.Id, b.Version, b.WarehouseId, b.BinId, bin.Code, bin.Kind, b.ProductId, product.Sku, b.LotId, lot?.LotNumber,
                lot?.ExpiryDate, b.SerialId, b.SerialId is { } sid ? serials.GetValueOrDefault(sid)?.SerialNumber : null, b.QualityStatus,
                Quantity.From(b.OnHandQty), Quantity.From(b.ReservedQty), Quantity.From(StockEligibility.Allocatable(facts)), reasons.Count == 0, reasons,
                b.ReceivedAt, last);
        }).ToList();
    }

    private sealed record LastMovementRow(Guid BucketId, DateTimeOffset? At);

    internal static async Task<List<StockMovementDto>> MovementDtosAsync(ITenantSession s, IReadOnlyList<StockMovement> movements, CancellationToken ct)
    {
        var bucketIds = movements.SelectMany(m => new[] { m.SourceBucketId, m.DestinationBucketId }).Where(id => id is not null).Select(id => id!.Value)
            .Distinct().ToList();
        var buckets = await s.Db.Set<StockBucket>().AsNoTracking().Where(b => bucketIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        var ids = movements.Select(m => m.Id).ToList();
        var reversed = (await s.Db.Set<StockMovement>().AsNoTracking().Where(m => m.ReversalOfId != null && ids.Contains(m.ReversalOfId.Value))
            .Select(m => new { m.ReversalOfId, m.BaseQty }).ToListAsync(ct)).ToLookup(m => m.ReversalOfId!.Value, m => m.BaseQty);
        MovementLocationDto? Location(Guid? id) =>
            id is { } bid && buckets.TryGetValue(bid, out var b) ? new MovementLocationDto(b.Id, b.WarehouseId, b.BinId, b.QualityStatus) : null;
        return movements.Select(m => new StockMovementDto(m.Id, m.MovementType, Quantity.From(m.BaseQty), m.ProductId, m.LotId, m.SerialId,
            Location(m.SourceBucketId), Location(m.DestinationBucketId), m.DocumentType, m.DocumentId, m.DocumentLineId, m.ReservationId, m.ActorSubject,
            m.OccurredAt, m.RecordedAt, m.Reason, m.ReversalOfId, Quantity.From(m.BaseQty - reversed[m.Id].Sum()))).ToList();
    }

    private static DateOnly WarehouseToday(string timeZone, DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).DateTime);
}

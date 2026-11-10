using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Locations;

namespace TrustedInventory.Application.Inbound;

/// <summary>Where stock may go: an active bin of the right kind in the right warehouse whose zone suits the product (DEC-012).</summary>
internal static class StockLocations
{
    public static async Task<Bin> RequireBinAsync(ITenantSession s, Guid binId, Guid warehouseId, Product product, IReadOnlyCollection<BinKind> kinds,
        string field, CancellationToken ct)
    {
        var bin = await s.Db.Set<Bin>().AsNoTracking().SingleOrDefaultAsync(b => b.Id == binId, ct);
        if (bin is null || bin.WarehouseId != warehouseId || !kinds.Contains(bin.Kind) || !bin.Active)
            throw AppException.Validation(field, $"Use an active {string.Join(" or ", kinds)} bin of this warehouse.");
        var zone = await s.Db.Set<Zone>().AsNoTracking().SingleAsync(z => z.Id == bin.ZoneId, ct);
        var profile = product.StorageProfileId is { } pid ? await s.Db.Set<StorageProfile>().AsNoTracking().SingleAsync(p => p.Id == pid, ct) : null;
        var hazards = await s.Db.Set<ZoneAllowedHazardClass>().AsNoTracking().Where(h => h.ZoneId == zone.Id).Select(h => h.HazardClass).ToListAsync(ct);
        if (!zone.Active || !StorageCompatibility.IsCompatible(zone.TempMinC, zone.TempMaxC, profile?.MinC, profile?.MaxC, product.HazardClass, hazards))
            throw new AppException(ErrorCode.IncompatibleBin, "This bin cannot hold this product (DEC-012).",
                [new FieldError(field, ErrorCode.IncompatibleBin, "The bin's zone does not suit the product's storage profile or hazard class.")]);
        return bin;
    }

    /// <summary>The bucket with the same product, lot, serial and quality in another bin, created empty when missing.</summary>
    public static async Task<Guid> MatchingBucketAsync(ITenantSession s, Guid tenantId, string actor, Guid warehouseId, Guid binId, Guid productId,
        Guid? lotId, Guid? serialId, string quality, DateTimeOffset receivedAt, CancellationToken ct)
    {
        await s.Db.Database.ExecuteSqlAsync($"""
            INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, lot_id, serial_id, quality_status, received_at,
                                         on_hand_qty, created_by, created_at, updated_at)
            VALUES ({tenantId}, {warehouseId}, {binId}, {productId}, {lotId}, {serialId}, {quality}::ti.quality_status, {receivedAt}, 0, {actor}, {s.Now}, {s.Now})
            ON CONFLICT (tenant_id, bin_id, product_id, lot_id, serial_id, quality_status) DO NOTHING
            """, ct);
        return (await s.Db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM ti.stock_bucket
            WHERE bin_id = {binId} AND product_id = {productId} AND lot_id IS NOT DISTINCT FROM {lotId}
              AND serial_id IS NOT DISTINCT FROM {serialId} AND quality_status = {quality}::ti.quality_status
            """).ToListAsync(ct)).Single();
    }

    /// <summary>Locks the buckets in id order (STK-005) and returns their current quantities.</summary>
    public static async Task<Dictionary<Guid, BucketQuantities>> LockBucketsAsync(ITenantSession s, IEnumerable<Guid> bucketIds, CancellationToken ct)
    {
        var ids = bucketIds.Distinct().Order().ToArray();
        return (await s.Db.Database.SqlQuery<BucketQuantities>($"""
            SELECT id AS "Id", on_hand_qty AS "OnHand", reserved_qty AS "Reserved" FROM ti.stock_bucket WHERE id = ANY({ids}) ORDER BY id FOR UPDATE
            """).ToListAsync(ct)).ToDictionary(b => b.Id);
    }

    /// <summary>Moves on hand only (unreserved stock) from one bucket to another.</summary>
    public static async Task MoveOnHandAsync(ITenantSession s, Guid from, Guid? to, decimal quantity, CancellationToken ct)
    {
        await s.Db.Database.ExecuteSqlAsync($"""
            UPDATE ti.stock_bucket SET on_hand_qty = on_hand_qty - {quantity}, updated_at = {s.Now}, version = version + 1 WHERE id = {from}
            """, ct);
        if (to is { } target)
            await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.stock_bucket SET on_hand_qty = on_hand_qty + {quantity}, updated_at = {s.Now}, version = version + 1 WHERE id = {target}
                """, ct);
    }

    internal sealed record BucketQuantities(Guid Id, decimal OnHand, decimal Reserved);
}

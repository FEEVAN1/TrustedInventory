using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Locations;

namespace TrustedInventory.Application.Inbound;

/// <summary>
/// Gathers the facts for <see cref="PutawayPlanner"/> from current data (INB-005, DEC-050): the warehouse's bins with
/// zone compatibility for the product, capacity load in each bin's capacity unit, and whether a bin already holds the
/// same product and lot. Used by receiving (to create tasks) and by putaway confirmation (to recheck).
/// </summary>
internal static class PutawayAdvisor
{
    public static async Task<List<BinCandidate>> CandidatesAsync(ITenantSession s, Guid warehouseId, Product product, Guid? lotId, decimal quantity,
        CancellationToken ct)
    {
        var bins = await s.Db.Set<Bin>().AsNoTracking().Where(b => b.WarehouseId == warehouseId && b.Active).ToListAsync(ct);
        var zoneIds = bins.Select(b => b.ZoneId).Distinct().ToList();
        var zones = await s.Db.Set<Zone>().AsNoTracking().Where(z => zoneIds.Contains(z.Id)).ToDictionaryAsync(z => z.Id, ct);
        var hazards = (await s.Db.Set<ZoneAllowedHazardClass>().AsNoTracking().Where(h => zoneIds.Contains(h.ZoneId)).ToListAsync(ct))
            .ToLookup(h => h.ZoneId, h => h.HazardClass);
        var profile = product.StorageProfileId is { } pid
            ? await s.Db.Set<StorageProfile>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == pid, ct)
            : null;

        var binIds = bins.Select(b => b.Id).ToList();
        var stock = await s.Db.Set<StockBucket>().AsNoTracking().Where(b => binIds.Contains(b.BinId) && b.OnHandQty > 0)
            .Select(b => new { b.BinId, b.ProductId, b.LotId, b.OnHandQty }).ToListAsync(ct);
        var capacityUnits = bins.Where(b => b.CapacityUomId is not null).Select(b => b.CapacityUomId!.Value).Distinct().ToList();
        var productIds = stock.Select(x => x.ProductId).Append(product.Id).Distinct().ToList();
        var baseUnits = await s.Db.Set<Product>().AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.BaseUomId, ct);
        var conversions = await s.Db.Set<ProductConversion>().AsNoTracking()
            .Where(c => productIds.Contains(c.ProductId) && capacityUnits.Contains(c.AlternateUomId) && c.Active)
            .ToListAsync(ct);

        // Base quantity of a product expressed in a capacity unit; null when no conversion exists (fail closed).
        decimal? InUnit(Guid productId, decimal baseQty, Guid unit)
        {
            if (baseUnits.TryGetValue(productId, out var baseUnit) && baseUnit == unit)
                return baseQty;
            var factor = conversions.FirstOrDefault(c => c.ProductId == productId && c.AlternateUomId == unit)?.FactorToBase;
            return factor is { } f ? baseQty / f : null;
        }

        return bins.Select(b =>
        {
            var zone = zones[b.ZoneId];
            decimal? moved = null, load = null;
            if (b.CapacityUomId is { } unit)
            {
                moved = InUnit(product.Id, quantity, unit);
                var parts = stock.Where(x => x.BinId == b.Id).Select(x => InUnit(x.ProductId, x.OnHandQty, unit)).ToList();
                load = parts.Any(p => p is null) ? null : parts.Sum(p => p!.Value);
            }
            return new BinCandidate(b.Id, b.Code, b.Kind, b.Active, b.PickPriority,
                StorageCompatibility.IsCompatible(zone.TempMinC, zone.TempMaxC, profile?.MinC, profile?.MaxC, product.HazardClass, hazards[zone.Id].ToList()),
                b.Capacity, moved, load, stock.Any(x => x.BinId == b.Id && x.ProductId == product.Id && x.LotId == lotId));
        }).ToList();
    }
}

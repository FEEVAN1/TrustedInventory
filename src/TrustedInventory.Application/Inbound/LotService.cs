using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Inbound;

public sealed record LotFilter(Guid? ProductId, string? LotNumber, DateOnly? ExpiresBefore);

public sealed record SerialFilter(Guid? ProductId, Guid? LotId, string? SerialNumber);

/// <summary>
/// Serials are append-only (created by receipt posting only), so the API reports version 1 and <c>updatedAt = createdAt</c>, as
/// shipments do (DEC-055).
/// </summary>
public sealed record SerialDto(Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, Guid ProductId,
    string SerialNumber, Guid? LotId) : IVersioned;

/// <summary>
/// Read access to lots and serials (section 07, DEC-087). Both are created only by receipt posting and identify product units across
/// warehouses, so reading them needs <c>stock.read</c> in the tenant, as the lot traceability report does (DEC-078). Number filters
/// match a prefix. Nothing here changes a lot: expiry correction is not implemented yet (DEC-085, DEC-087).
/// </summary>
public sealed class LotService(ITenantUnitOfWork uow, ICallerAccessor callers, ICursorCodec cursors)
{
    public Task<Page<LotDto>> ListLotsAsync(LotFilter filter, PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.StockRead);
        if (filter.LotNumber is { Length: > 100 })
            throw AppException.Validation("lotNumber", "Use at most 100 characters.");
        return uow.ReadAsync((s, c) =>
        {
            var query = s.Db.Set<Lot>().AsNoTracking();
            if (filter.ProductId is { } product)
                query = query.Where(l => l.ProductId == product);
            if (!string.IsNullOrEmpty(filter.LotNumber))
                query = query.Where(l => l.LotNumber.StartsWith(filter.LotNumber));
            if (filter.ExpiresBefore is { } before)
                query = query.Where(l => l.ExpiryDate != null && l.ExpiryDate < before);
            return query.ToPageAsync(page, $"lots|{filter.ProductId}|{filter.LotNumber}|{filter.ExpiresBefore:O}", cursors, ToDto, c);
        }, ct);
    }

    public Task<LotDto> GetLotAsync(Guid id, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
            ToDto(await s.Db.Set<Lot>().AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, c) ?? throw AppException.NotFound("Lot")), ct);
    }

    public Task<Page<SerialDto>> ListSerialsAsync(SerialFilter filter, PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.StockRead);
        if (filter.SerialNumber is { Length: > 100 })
            throw AppException.Validation("serialNumber", "Use at most 100 characters.");
        page.EnsureSupportedSort();
        var limit = page.EffectiveLimit;
        var key = $"serials|{filter.ProductId}|{filter.LotId}|{filter.SerialNumber}";
        return uow.ReadAsync(async (s, c) =>
        {
            var query = s.Db.Set<Serial>().AsNoTracking();
            if (filter.ProductId is { } product)
                query = query.Where(x => x.ProductId == product);
            if (filter.LotId is { } lot)
                query = query.Where(x => x.LotId == lot);
            if (!string.IsNullOrEmpty(filter.SerialNumber))
                query = query.Where(x => x.SerialNumber.StartsWith(filter.SerialNumber));
            // Serial is not a TenantEntity, so it is paged here in the same order and with the same cursor rules (API-004).
            if (page.Cursor is { } cursor)
            {
                var p = cursors.Decode(cursor, key);
                query = query.Where(x => x.CreatedAt < p.CreatedAt || (x.CreatedAt == p.CreatedAt && x.Id.CompareTo(p.Id) < 0));
            }
            var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(limit + 1).ToListAsync(c);
            string? next = null;
            if (rows.Count > limit)
            {
                rows.RemoveAt(limit);
                next = cursors.Encode(new CursorPosition(rows[^1].CreatedAt, rows[^1].Id), key);
            }
            return new Page<SerialDto>(rows.Select(x => new SerialDto(x.Id, 1, x.CreatedAt, x.CreatedBy, x.CreatedAt, x.ProductId, x.SerialNumber, x.LotId))
                .ToList(), next);
        }, ct);
    }

    /// <summary><c>expiryCorrected</c> stays false until expiry correction exists (DEC-087).</summary>
    internal static LotDto ToDto(Lot lot) =>
        new(lot.Id, lot.Version, lot.CreatedAt, lot.CreatedBy, lot.UpdatedAt, lot.ProductId, lot.LotNumber, lot.ExpiryDate, lot.SupplierRef, false);
}

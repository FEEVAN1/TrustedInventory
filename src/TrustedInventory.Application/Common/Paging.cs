using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Errors;
using TrustedInventory.Domain.Common;

namespace TrustedInventory.Application.Common;

/// <summary>List request (API-004): limit 1..200 (default 50) and an opaque cursor.</summary>
public sealed record PageRequest(int? Limit, string? Cursor, string? Sort = null)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const string DefaultSort = "-createdAt";

    /// <summary>Only the default order is documented in M1; anything else is rejected (API-004).</summary>
    public void EnsureSupportedSort()
    {
        if (Sort is not (null or DefaultSort))
            throw AppException.Validation("sort", $"Supported sort: {DefaultSort}.");
    }

    public int EffectiveLimit => Limit switch
    {
        null => DefaultLimit,
        < 1 or > MaxLimit => throw AppException.Validation("limit", $"limit must be between 1 and {MaxLimit}."),
        { } l => l,
    };
}

public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>Stable sort position: createdAt descending, then id descending.</summary>
public sealed record CursorPosition(DateTimeOffset CreatedAt, Guid Id);

/// <summary>
/// Opaque, tamper-evident cursor bound to the filter that produced it. A cursor reused with a different
/// filter, or altered, is rejected with INVALID_CURSOR (API-004).
/// </summary>
public interface ICursorCodec
{
    string Encode(CursorPosition position, string filterDigest);

    CursorPosition Decode(string cursor, string filterDigest);
}

public static class PagingExtensions
{
    /// <summary>
    /// Pages a tenant-entity query in the default order. <paramref name="filterKey"/> must describe every
    /// filter applied to <paramref name="query"/> so cursors cannot be replayed against another filter.
    /// </summary>
    public static async Task<Page<TOut>> ToPageAsync<T, TOut>(this IQueryable<T> query, PageRequest request,
        string filterKey, ICursorCodec codec, Func<T, TOut> map, CancellationToken ct)
        where T : TenantEntity
    {
        request.EnsureSupportedSort();
        var limit = request.EffectiveLimit;
        if (request.Cursor is { } cursor)
        {
            var p = codec.Decode(cursor, filterKey);
            query = query.Where(e => e.CreatedAt < p.CreatedAt || (e.CreatedAt == p.CreatedAt && e.Id.CompareTo(p.Id) < 0));
        }

        var rows = await query
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .Take(limit + 1)
            .ToListAsync(ct);

        string? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1];
            next = codec.Encode(new CursorPosition(last.CreatedAt, last.Id), filterKey);
        }
        return new Page<TOut>(rows.Select(map).ToList(), next);
    }
}

using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Application.Saas;

public enum SetupItemStatus
{
    Done,
    Pending,
    Unavailable,
}

/// <summary>Wire keys of the SetupItemKey enum (docs/enums.md), in display order.</summary>
public static class SetupItemKeys
{
    public const string BusinessProfile = "business-profile";
    public const string WarehouseTimezone = "warehouse-timezone";
    public const string ZonesBins = "zones-bins";
    public const string ProductsUnits = "products-units";
    public const string TeamInvitations = "team-invitations";
    public const string Import = "import";
}

public sealed record SetupItemDto(string Key, SetupItemStatus Status, bool Required);

public sealed record SetupChecklistDto(IReadOnlyList<SetupItemDto> Items, int RequiredCompleted, int RequiredTotal, DateTimeOffset? TrialEnd);

/// <summary>
/// Setup checklist (ONB-004, DEC-046). Every item is computed from current records in one read-only tenant
/// transaction; nothing is stored and nothing is written, so reading it can never create stock or records.
/// </summary>
public sealed class SetupChecklistService(ITenantUnitOfWork uow, ICallerAccessor callers)
{
    public Task<SetupChecklistDto> GetAsync(CancellationToken ct)
    {
        var caller = callers.Caller;
        var state = caller.Access ?? throw AppException.NotFound("Setup checklist");
        return uow.ReadAsync(async (s, c) =>
        {
            var businessProfile = await s.Db.Database.SqlQuery<bool>($"""
                SELECT EXISTS (SELECT 1 FROM cp.billing_account
                               WHERE btrim(business_legal_name) <> '' AND country IS NOT NULL) AS "Value"
                """).SingleAsync(c);
            // Sample data (DEC-098) is for exploring; it never completes a setup step.
            var sample = await s.Db.Database.SqlQuery<Guid>($"SELECT object_id AS \"Value\" FROM ti.sample_data_item").ToListAsync(c);
            var warehouse = await s.Db.Set<Warehouse>().AnyAsync(w => w.Active && !sample.Contains(w.Id), c);
            var userZones = s.Db.Set<Zone>().Where(z => z.Active && !z.IsSystem && !sample.Contains(z.Id)).Select(z => z.Id);
            var zonesBins = await s.Db.Set<Bin>().AnyAsync(b => b.Active && b.Kind != BinKind.Transit && userZones.Contains(b.ZoneId) && !sample.Contains(b.Id), c);
            var productsUnits = await s.Db.Set<Unit>().AnyAsync(u => u.Active && !sample.Contains(u.Id), c)
                && await s.Db.Set<Product>().AnyAsync(p => p.Active && !sample.Contains(p.Id), c);
            var team = await s.Db.Set<Invitation>().AnyAsync(c) || await s.Db.Set<Membership>().CountAsync(m => m.Active, c) > 1;
            var imported = await s.Db.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM ti.product_import) AS \"Value\"").SingleAsync(c);

            SetupItemDto[] items =
            [
                Required(SetupItemKeys.BusinessProfile, businessProfile),
                Required(SetupItemKeys.WarehouseTimezone, warehouse),
                Required(SetupItemKeys.ZonesBins, zonesBins),
                Required(SetupItemKeys.ProductsUnits, productsUnits),
                Required(SetupItemKeys.TeamInvitations, team),
                new(SetupItemKeys.Import, imported ? SetupItemStatus.Done : SetupItemStatus.Pending, Required: false), // DEC-099
            ];
            var required = items.Where(i => i.Required).ToList();
            var trialEnd = state.Subscription == SubscriptionState.Trialing ? state.TrialEnd : null;
            return new SetupChecklistDto(items, required.Count(i => i.Status == SetupItemStatus.Done), required.Count, trialEnd);
        }, ct);
    }

    private static SetupItemDto Required(string key, bool done) =>
        new(key, done ? SetupItemStatus.Done : SetupItemStatus.Pending, Required: true);
}

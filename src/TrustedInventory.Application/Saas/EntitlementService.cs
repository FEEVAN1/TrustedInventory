using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Application.Saas;

public sealed record EntitlementsDto(
    Guid TenantId, long EntitlementVersion, long SubscriptionRevision, IReadOnlyList<string> Features,
    IReadOnlyDictionary<string, long> Limits, IReadOnlyDictionary<string, long> Usage, DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo, AccessMode AccessMode, IReadOnlyList<RestrictionReason> RestrictionReasons);

/// <summary>
/// Feature and limit decisions for any member (ENT-002, SUI-003), so upgrade prompts can name the exact limit.
/// Usage comes from the quota counters; a metric never counted yet reads from its authoritative records.
/// </summary>
public sealed class EntitlementService(ITenantUnitOfWork uow, ICallerAccessor callers)
{
    public Task<EntitlementsDto> GetAsync(CancellationToken ct)
    {
        var caller = callers.Caller;
        var state = caller.Access ?? throw AppException.NotFound("Entitlements");
        return uow.ReadAsync(async (s, c) =>
        {
            var snapshot = await s.Db.Database.SqlQuery<SnapshotRow>($"""
                SELECT effective_from AS "EffectiveFrom", subscription_revision AS "SubscriptionRevision"
                FROM cp.entitlement_snapshot WHERE tenant_id = {caller.TenantId} AND is_current
                """).SingleOrDefaultAsync(c) ?? throw AppException.NotFound("Entitlements");

            var counted = await s.Db.Database.SqlQuery<UsageRow>(
                $"SELECT metric::text AS \"Metric\", used AS \"Used\" FROM ti.usage_counter").ToListAsync(c);
            var live = await s.Db.Database.SqlQuery<UsageRow>($"""
                SELECT 'active_seats' AS "Metric", count(*) AS "Used" FROM ti.membership WHERE active
                UNION ALL SELECT 'active_warehouses', count(*) FROM ti.warehouse WHERE active
                UNION ALL SELECT 'active_skus', count(*) FROM ti.product WHERE active
                """).ToListAsync(c);
            IReadOnlyDictionary<string, long> usage = Enum.GetValues<QuotaKey>().Select(SaasWire.Quota).ToDictionary(k => k, k =>
                (long?)counted.FirstOrDefault(u => u.Metric == k)?.Used ?? live.FirstOrDefault(u => u.Metric == k)?.Used ?? 0L);

            return new EntitlementsDto(caller.TenantId, state.EntitlementVersion ?? 0, snapshot.SubscriptionRevision,
                state.Features.Order(StringComparer.Ordinal).ToList(),
                state.Limits.ToDictionary(l => SaasWire.Quota(l.Key), l => l.Value), usage,
                snapshot.EffectiveFrom, state.EntitlementEffectiveTo, state.Mode(s.Now), state.Reasons(s.Now));
        }, ct);
    }

    private sealed record SnapshotRow(DateTimeOffset EffectiveFrom, long SubscriptionRevision);

    private sealed record UsageRow(string Metric, long Used);
}

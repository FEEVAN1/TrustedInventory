using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using TrustedInventory.Application.Billing;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Jobs;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Infrastructure.Audit;
using TrustedInventory.Infrastructure.Jobs;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Billing;

/// <summary>
/// Applies scheduled plan changes at their boundary (ENT-005, ENT-006, DEC-065). Stock blockers are recomputed in the tenant's own
/// transaction; the apply transaction then locks the subscription, the change and the tenant's usage counters (the rows QuotaGate
/// locks) and rechecks the quota blockers, so no reservation slips in between the check and the new entitlement snapshot.
/// </summary>
public sealed partial class PlanChangeJob(ControlDataSource control, TenantJobRunner runner, IBillingProvider provider, TimeProvider clock,
    ILogger<PlanChangeJob> log) : IJob
{
    public const string Subject = "system:plan-change";

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var due = new List<Guid>();
        await using (var cmd = control.Source.CreateCommand("""
            SELECT id FROM cp.subscription_change WHERE state = 'Scheduled' AND effective_at <= @now ORDER BY effective_at, id LIMIT 50
            """))
        {
            cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                due.Add(r.GetGuid(0));
        }
        foreach (var id in due)
        {
            try
            {
                var outcome = await ApplyAsync(id, ct);
                LogOutcome(log, id, outcome);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(log, e, id);
            }
        }
    }

    /// <summary>Applied, Blocked, Cancelled, or Scheduled when it still waits (payment or provider).</summary>
    public async Task<SubscriptionChangeState> ApplyAsync(Guid changeId, CancellationToken ct)
    {
        var info = await LoadAsync(changeId, ct);
        if (info is null)
            return SubscriptionChangeState.Cancelled;
        var now = clock.GetUtcNow();
        if (info.Lifecycle == nameof(TenantLifecycle.Deleted))
            return await CloseAsync(info, SubscriptionChangeState.Cancelled, [], now, ct);

        // 1. Stock-dependent blockers in the tenant's transaction (ti_app); the control role reads no stock (SAS-004).
        var status = info.SecuritySuspended ? TenantStatus.Suspended : TenantStatus.Active;
        var blockers = await runner.UnitOfWorkFor(info.TenantId, status)
            .ReadAsync((s, c) => PlanChangeBlockers.ComputeAsync(s, info.Current, info.Target, c), ct);
        if (blockers.Count > 0)
        {
            if (!await CancelAtProviderAsync(info, ct))
                return SubscriptionChangeState.Scheduled; // provider unreachable: the old price must stay at the provider too; retry
            return await CloseAsync(info, SubscriptionChangeState.Blocked, blockers, now, ct);
        }

        // 2. An upgrade waits for the verified payment of its first period (ENT-005).
        if (PlanChangeRules.IsUpgrade(info.Current.Features, info.Current.Limits, info.Target.Features, info.Target.Limits) && !info.FirstPeriodPaid)
            return SubscriptionChangeState.Scheduled;

        // 3. Apply under the counter locks, rechecking quotas against the authoritative counters.
        var applied = await CloseAsync(info, SubscriptionChangeState.Applied, [], now, ct);
        if (applied == SubscriptionChangeState.Blocked)
            await CancelAtProviderAsync(info, ct); // usage rose between the checks; best effort, reconciliation catches a miss
        return applied;
    }

    private async Task<bool> CancelAtProviderAsync(ChangeInfo info, CancellationToken ct)
    {
        if (info.ProviderSubscriptionRef is null)
            return true;
        try
        {
            await provider.CancelScheduledChangeAsync(info.ChangeId, info.ProviderSubscriptionRef, ct);
            return true;
        }
        catch (BillingProviderUnavailableException)
        {
            return false;
        }
    }

    private async Task<SubscriptionChangeState> CloseAsync(ChangeInfo info, SubscriptionChangeState wanted, IReadOnlyList<ChangeBlocker> blockers,
        DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var revision = await Sql.ScalarAsync<long?>(conn, tx, "SELECT revision FROM cp.subscription WHERE id = @s FOR UPDATE", ct, ("s", info.SubscriptionId));
        var state = await Sql.ScalarAsync<string>(conn, tx, "SELECT state::text FROM cp.subscription_change WHERE id = @id FOR UPDATE", ct, ("id", info.ChangeId));
        if (revision is null || state != nameof(SubscriptionChangeState.Scheduled))
        {
            await tx.RollbackAsync(ct);
            return Enum.TryParse<SubscriptionChangeState>(state, out var current) ? current : SubscriptionChangeState.Cancelled;
        }

        var audit = new List<AuditEntry>();
        if (wanted == SubscriptionChangeState.Applied)
        {
            // Same rows QuotaGate locks before it reads the limit: a reservation either committed before us or sees the new snapshot.
            var counters = new Dictionary<QuotaKey, long>();
            await using (var cmd = new NpgsqlCommand(
                "SELECT metric::text, used FROM ti.usage_counter WHERE tenant_id = @t ORDER BY metric FOR UPDATE", conn, tx))
            {
                cmd.Parameters.AddWithValue("t", info.TenantId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    if (Enum.GetValues<QuotaKey>().Any(q => SaasWire.Quota(q) == r.GetString(0)))
                        counters[SaasWire.ParseQuota(r.GetString(0))] = r.GetInt64(1);
            }
            var recheck = PlanChangeRules.QuotaBlockers(counters, info.Target.Limits);
            if (recheck.Count > 0)
            {
                wanted = SubscriptionChangeState.Blocked;
                blockers = recheck;
            }
        }

        if (wanted == SubscriptionChangeState.Applied)
        {
            var newRevision = (await Sql.ScalarAsync<long?>(conn, tx, """
                UPDATE cp.subscription SET plan_version_id = @plan, price_version_id = @price, revision = revision + 1, updated_at = @now
                WHERE id = @s RETURNING revision
                """, ct, ("plan", info.Target.PlanVersionId), ("price", info.TargetPriceVersionId), ("now", now), ("s", info.SubscriptionId)))!.Value;
            var version = await EntitlementSnapshots.ReplaceAsync(conn, tx, info.TenantId, newRevision, info.Target.PlanVersionId, now, ct);
            await Sql.ExecAsync(conn, tx, """
                UPDATE cp.subscription_change SET state = 'Applied', applied_at = @now, updated_at = @now, version = version + 1 WHERE id = @id
                """, ct, ("now", now), ("id", info.ChangeId));
            audit.Add(new AuditEntry("SubscriptionChangeApplied", "SubscriptionChange", info.ChangeId, null, null,
                new { planVersionId = info.Current.PlanVersionId },
                new { planVersionId = info.Target.PlanVersionId, priceVersionId = info.TargetPriceVersionId, entitlementVersion = version }));
            await NoticeAsync(conn, tx, info, "PlanChangeApplied", new { targetPlanVersionId = info.Target.PlanVersionId }, now, ct);
        }
        else
        {
            var dtos = blockers.Select(PlanChangeBlockers.Dto).ToList();
            await using (var cmd = new NpgsqlCommand("""
                UPDATE cp.subscription_change SET state = @st::cp.subscription_change_state, blockers = @b, updated_at = @now, version = version + 1
                WHERE id = @id
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("st", wanted.ToString());
                cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Jsonb)
                {
                    Value = wanted == SubscriptionChangeState.Blocked ? JsonSerializer.Serialize(dtos, JsonDefaults.Options) : DBNull.Value,
                });
                cmd.Parameters.AddWithValue("now", now);
                cmd.Parameters.AddWithValue("id", info.ChangeId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            if (wanted == SubscriptionChangeState.Blocked)
            {
                // Usage rose before the boundary: the owner is told and nothing is removed (ENT-006).
                audit.Add(new AuditEntry("SubscriptionChangeBlocked", "SubscriptionChange", info.ChangeId, null, null, null, new { blockers = dtos }));
                await NoticeAsync(conn, tx, info, "PlanChangeBlocked", new { targetPlanVersionId = info.Target.PlanVersionId, blockers = dtos }, now, ct);
            }
        }
        if (audit.Count > 0)
            await AuditAppender.AppendAsync(conn, tx, info.TenantId, new AuditActor(Subject, ActorType.System, null, info.ChangeId.ToString()), now, audit, ct);
        await tx.CommitAsync(ct);
        return wanted;
    }

    private static async Task NoticeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, ChangeInfo info, string kind, object detail, DateTimeOffset now,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO cp.billing_notice (tenant_id, kind, dedupe_key, detail, created_at)
            VALUES (@t, @k::cp.billing_notice_kind, @d, @detail, @now) ON CONFLICT (dedupe_key) DO NOTHING
            """, conn, tx);
        cmd.Parameters.AddWithValue("t", info.TenantId);
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.AddWithValue("d", $"{kind}:{info.ChangeId}");
        cmd.Parameters.Add(new NpgsqlParameter("detail", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(detail, detail.GetType(), JsonDefaults.Options) });
        cmd.Parameters.AddWithValue("now", now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed record ChangeInfo(Guid ChangeId, Guid TenantId, Guid SubscriptionId, string Lifecycle, bool SecuritySuspended,
        string? ProviderSubscriptionRef, Guid TargetPriceVersionId, PlanTerms Current, PlanTerms Target, bool FirstPeriodPaid);

    private async Task<ChangeInfo?> LoadAsync(Guid changeId, CancellationToken ct)
    {
        await using var cmd = control.Source.CreateCommand("""
            SELECT c.id, c.tenant_id, s.id, coalesce(t.lifecycle::text, 'Ready'), t.security_suspended_at IS NOT NULL, s.provider_subscription_ref,
                   c.target_price_version_id, coalesce(e.plan_version_id, s.plan_version_id), coalesce(e.features, '{}'), coalesce(e.limits, '{}')::text,
                   p.id, p.features, p.quotas::text,
                   EXISTS (SELECT 1 FROM cp.invoice i WHERE i.subscription_id = s.id AND i.status = 'Paid' AND i.period_start >= c.effective_at)
            FROM cp.subscription_change c
            JOIN cp.subscription s ON s.id = c.subscription_id
            JOIN ti.tenant t ON t.id = c.tenant_id
            JOIN cp.plan_version p ON p.id = c.target_plan_version_id
            LEFT JOIN cp.entitlement_snapshot e ON e.tenant_id = c.tenant_id AND e.is_current
            WHERE c.id = @id AND c.state = 'Scheduled'
            """);
        cmd.Parameters.AddWithValue("id", changeId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;
        return new ChangeInfo(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetBoolean(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetGuid(6),
            new PlanTerms(r.GetGuid(7), PlanChangeStore.Features(r.GetFieldValue<string[]>(8)), PlanChangeStore.Limits(r.GetString(9))),
            new PlanTerms(r.GetGuid(10), PlanChangeStore.Features(r.GetFieldValue<string[]>(11)), PlanChangeStore.Limits(r.GetString(12))),
            r.GetBoolean(13));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Plan change {ChangeId}: {Outcome}")]
    private static partial void LogOutcome(ILogger logger, Guid changeId, SubscriptionChangeState outcome);

    [LoggerMessage(Level = LogLevel.Error, Message = "Plan change {ChangeId} failed; retried next run")]
    private static partial void LogFailed(ILogger logger, Exception error, Guid changeId);
}

/// <summary>Exactly one current entitlement snapshot per tenant (ENT-002): closes the current one and opens the plan's.</summary>
internal static class EntitlementSnapshots
{
    public static async Task<int> ReplaceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, long subscriptionRevision, Guid planVersionId,
        DateTimeOffset now, CancellationToken ct)
    {
        await Sql.ExecAsync(conn, tx, """
            UPDATE cp.entitlement_snapshot SET is_current = false, effective_to = GREATEST(@now, effective_from + interval '1 millisecond')
            WHERE tenant_id = @t AND is_current
            """, ct, ("t", tenantId), ("now", now));
        return (await Sql.ScalarAsync<int?>(conn, tx, """
            INSERT INTO cp.entitlement_snapshot (tenant_id, subscription_revision, entitlement_version, plan_version_id, features, limits, effective_from, is_current)
            SELECT @t, @rev, coalesce((SELECT max(entitlement_version) FROM cp.entitlement_snapshot WHERE tenant_id = @t), 0) + 1, p.id, p.features, p.quotas, @now, true
            FROM cp.plan_version p WHERE p.id = @plan
            RETURNING entitlement_version
            """, ct, ("t", tenantId), ("rev", subscriptionRevision), ("plan", planVersionId), ("now", now)))!.Value;
    }
}

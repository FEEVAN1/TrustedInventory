using System.Data;
using System.Text.Json;
using Npgsql;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Saas;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Saas;

/// <summary>
/// Reads cp.tenant_access() for one tenant. The function returns only the tenant named by app.tenant_id, so it
/// runs in its own read-only transaction with that context set (DEC-042); a request can never read another
/// tenant's state.
/// </summary>
public sealed class TenantAccessReader(AppDataSource dataSource) : ITenantAccessReader
{
    public async Task<TenantAccessState?> ReadAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = await dataSource.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using (var ro = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx))
            await ro.ExecuteNonQueryAsync(ct);
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", tenantId.ToString());
            await set.ExecuteNonQueryAsync(ct);
        }
        var state = await QueryAsync(conn, tx, ct);
        await tx.CommitAsync(ct);
        return state;
    }

    /// <summary>Reads the state on an existing tenant transaction (quota checks read it alongside the counter).</summary>
    public static async Task<TenantAccessState?> QueryAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT tenant_id, lifecycle::text, security_suspended_at, subscription_state::text, trial_end, period_end,
                   grace_end, cancel_at_period_end, subscription_revision, entitlement_version, features, limits::text,
                   entitlement_effective_to, complimentary_until, complimentary_features, complimentary_limits::text
            FROM cp.tenant_access()
            """, conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;

        return new TenantAccessState(
            r.GetGuid(0),
            Enum.Parse<TenantLifecycle>(r.IsDBNull(1) ? nameof(TenantLifecycle.Ready) : r.GetString(1)),
            r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2),
            r.IsDBNull(3) ? null : Enum.Parse<SubscriptionState>(r.GetString(3)),
            r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
            r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
            r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
            !r.IsDBNull(7) && r.GetBoolean(7),
            r.IsDBNull(8) ? null : r.GetInt64(8),
            r.IsDBNull(9) ? null : r.GetInt32(9),
            r.IsDBNull(10) ? new HashSet<string>() : new HashSet<string>(r.GetFieldValue<string[]>(10), StringComparer.Ordinal),
            r.IsDBNull(11) ? new Dictionary<QuotaKey, long>() : ParseLimits(r.GetString(11)),
            r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12),
            r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13),
            r.IsDBNull(14) ? null : new HashSet<string>(r.GetFieldValue<string[]>(14), StringComparer.Ordinal),
            r.IsDBNull(15) ? null : ParseLimits(r.GetString(15)));
    }

    internal static Dictionary<QuotaKey, long> ParseLimits(string json)
    {
        var limits = new Dictionary<QuotaKey, long>();
        using var doc = JsonDocument.Parse(json);
        foreach (var p in doc.RootElement.EnumerateObject())
            if (Enum.GetValues<QuotaKey>().Any(q => SaasWire.Quota(q) == p.Name) && p.Value.TryGetInt64(out var v))
                limits[SaasWire.ParseQuota(p.Name)] = v;
        return limits;
    }
}

/// <summary>
/// Quota reservation on the command's own connection and transaction (DEC-038). The counter row lock is taken
/// before the audit stream lock, which the unit of work always takes last (DEC-024).
/// </summary>
public sealed class QuotaGate(TimeProvider clock) : IQuotaGate
{
    public async Task ReserveAsync(ITenantSession session, QuotaKey quota, long amount = 1, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        var s = (TenantUnitOfWork.Session)session;
        var used = await LockAsync(s, quota, ct);
        var state = await TenantAccessReader.QueryAsync(s.Connection, s.Transaction, ct);
        var limit = state?.Limit(quota, clock.GetUtcNow()) ?? 0;
        if (used + amount > limit)
            throw AccessGate.PlanLimit(quota, used, limit);
        await UpdateAsync(s, quota, used + amount, ct);
    }

    public async Task ReleaseAsync(ITenantSession session, QuotaKey quota, long amount = 1, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        var s = (TenantUnitOfWork.Session)session;
        var used = await LockAsync(s, quota, ct);
        await UpdateAsync(s, quota, Math.Max(0, used - amount), ct);
    }

    private static async Task<long> LockAsync(TenantUnitOfWork.Session s, QuotaKey quota, CancellationToken ct)
    {
        // A counter's first row is seeded from the authoritative active records (ENT-003); RLS limits each
        // count to the caller's tenant. Metrics whose tables arrive later (devices M5, attachments M4) start at 0.
        await using (var ensure = new NpgsqlCommand("""
            INSERT INTO ti.usage_counter (tenant_id, metric, used)
            SELECT @t, @m::ti.quota_key, CASE @m
                WHEN 'active_seats' THEN (SELECT count(*) FROM ti.membership WHERE active)
                WHEN 'active_warehouses' THEN (SELECT count(*) FROM ti.warehouse WHERE active)
                WHEN 'active_skus' THEN (SELECT count(*) FROM ti.product WHERE active)
                ELSE 0 END
            ON CONFLICT DO NOTHING
            """, s.Connection, s.Transaction))
        {
            ensure.Parameters.AddWithValue("t", s.Caller.TenantId);
            ensure.Parameters.AddWithValue("m", SaasWire.Quota(quota));
            await ensure.ExecuteNonQueryAsync(ct);
        }
        await using var lockCmd = new NpgsqlCommand(
            "SELECT used FROM ti.usage_counter WHERE tenant_id = @t AND metric = @m::ti.quota_key FOR UPDATE", s.Connection, s.Transaction);
        lockCmd.Parameters.AddWithValue("t", s.Caller.TenantId);
        lockCmd.Parameters.AddWithValue("m", SaasWire.Quota(quota));
        return (long)(await lockCmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task UpdateAsync(TenantUnitOfWork.Session s, QuotaKey quota, long used, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            UPDATE ti.usage_counter SET used = @u, version = version + 1, updated_at = now()
            WHERE tenant_id = @t AND metric = @m::ti.quota_key
            """, s.Connection, s.Transaction);
        cmd.Parameters.AddWithValue("u", used);
        cmd.Parameters.AddWithValue("t", s.Caller.TenantId);
        cmd.Parameters.AddWithValue("m", SaasWire.Quota(quota));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

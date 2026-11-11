using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TrustedInventory.Application.Billing;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Infrastructure.Audit;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Billing;

/// <summary>
/// Plan change, cancel and resume records on the ti_control pool (DEC-065). Lock order: billing account → subscription →
/// subscription changes → audit stream (last). The tenant id always comes from the validated caller.
/// </summary>
public sealed class PlanChangeStore(ControlDataSource control) : IPlanChangeStore
{
    private const string ChangeColumns = """
        c.id, c.target_price_version_id, c.target_plan_version_id, encode(c.preview_digest, 'hex'), c.state::text, c.provider_change_ref,
        c.requested_at, c.requested_by, c.effective_at, s.revision
        """;

    public async Task<PlanChangeContext?> LoadAsync(Guid tenantId, Guid targetPriceVersionId, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        PlanChangeContext context;
        await using (var cmd = new NpgsqlCommand("""
            SELECT s.id, s.state::text, s.revision, s.cancel_at_period_end, s.period_end, s.provider_subscription_ref, s.price_version_id,
                   s.plan_version_id, coalesce(e.plan_version_id, s.plan_version_id), coalesce(e.features, '{}'), coalesce(e.limits, '{}')::text
            FROM cp.subscription s LEFT JOIN cp.entitlement_snapshot e ON e.tenant_id = s.tenant_id AND e.is_current
            WHERE s.tenant_id = @t
            """, conn))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return null;
            context = new PlanChangeContext(r.GetGuid(0), Enum.Parse<SubscriptionState>(r.GetString(1)), r.GetInt64(2), r.GetBoolean(3),
                r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetGuid(6),
                r.GetGuid(7), new PlanTerms(r.GetGuid(8), Features(r.GetFieldValue<string[]>(9)), Limits(r.GetString(10))), null, null);
        }
        if (targetPriceVersionId == Guid.Empty)
            return context;

        await using (var cmd = new NpgsqlCommand("""
            SELECT pr.id, p.id, p.plan_code, p.published_at IS NOT NULL, p.is_public, pr.currency, pr.minor_unit_amount, pr.exponent,
                   pr.billing_interval::text, pr.active_from, pr.active_to, pr.provider, pr.provider_price_ref, p.features, p.quotas::text
            FROM cp.price_version pr JOIN cp.plan_version p ON p.id = pr.plan_version_id WHERE pr.id = @id
            """, conn))
        {
            cmd.Parameters.AddWithValue("id", targetPriceVersionId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                var price = new CheckoutPriceRow(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetBoolean(3), r.GetBoolean(4), r.GetString(5).Trim(),
                    r.GetInt64(6), r.GetInt16(7), Enum.Parse<BillingInterval>(r.GetString(8)), r.GetFieldValue<DateTimeOffset>(9),
                    r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10), r.IsDBNull(11) ? null : r.GetString(11),
                    r.IsDBNull(12) ? null : r.GetString(12));
                context = context with
                {
                    Target = price,
                    TargetTerms = new PlanTerms(price.PlanVersionId, Features(r.GetFieldValue<string[]>(13)), Limits(r.GetString(14))),
                };
            }
        }
        return context;
    }

    public async Task<PlanChangeRow?> FindByKeyAsync(Guid tenantId, Guid idempotencyKey, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var row = await ReadChangeAsync(conn, tx, "c.tenant_id = @t AND c.idempotency_key = @k", ct, ("t", tenantId), ("k", idempotencyKey));
        await tx.CommitAsync(ct);
        return row;
    }

    public async Task<PlanChangeRow> BeginAsync(BillingActor actor, Guid idempotencyKey, int expectedRevision, CheckoutPriceRow target, string digest,
        DateTimeOffset effectiveAt, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await Sql.ExecAsync(conn, tx, "SELECT id FROM cp.billing_account WHERE tenant_id = @t FOR UPDATE", ct, ("t", actor.TenantId));
        var existing = await ReadChangeAsync(conn, tx, "c.tenant_id = @t AND c.idempotency_key = @k", ct, ("t", actor.TenantId), ("k", idempotencyKey));
        if (existing is not null)
        {
            await tx.CommitAsync(ct);
            return existing;
        }

        var (subscriptionId, revision) = await LockSubscriptionAsync(conn, tx, actor.TenantId, expectedRevision, ct);
        var audit = await CancelOpenChangesAsync(conn, tx, subscriptionId, "Replaced by a new plan change", now, ct);
        var id = Guid.CreateVersion7();
        var acceptance = JsonSerializer.Serialize(new { acceptedBy = actor.Subject, acceptedAt = now, previewDigest = digest, requestId = actor.RequestId });
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO cp.subscription_change (id, tenant_id, subscription_id, target_plan_version_id, target_price_version_id, state, requested_by,
                                                requested_at, effective_at, preview_digest, acceptance, idempotency_key, created_at, updated_at)
            VALUES (@id, @t, @s, @plan, @price, 'Pending', @by, @now, @eff, decode(@d, 'hex'), @acc, @k, @now, @now)
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("t", actor.TenantId);
            insert.Parameters.AddWithValue("s", subscriptionId);
            insert.Parameters.AddWithValue("plan", target.PlanVersionId);
            insert.Parameters.AddWithValue("price", target.PriceVersionId);
            insert.Parameters.AddWithValue("by", actor.Subject);
            insert.Parameters.AddWithValue("now", now);
            insert.Parameters.AddWithValue("eff", effectiveAt);
            insert.Parameters.AddWithValue("d", digest);
            insert.Parameters.Add(new NpgsqlParameter("acc", NpgsqlDbType.Jsonb) { Value = acceptance });
            insert.Parameters.AddWithValue("k", idempotencyKey);
            await insert.ExecuteNonQueryAsync(ct);
        }
        var newRevision = await BumpAsync(conn, tx, subscriptionId, now, ct);
        audit.Add(new AuditEntry("SubscriptionChangeRequested", "SubscriptionChange", id, null, null, null,
            new { targetPriceVersionId = target.PriceVersionId, targetPlanVersionId = target.PlanVersionId, effectiveAt, acceptedPreviewDigest = digest }));
        await AuditAppender.AppendAsync(conn, tx, actor.TenantId, Actor(actor), now, audit, ct);
        await tx.CommitAsync(ct);
        _ = revision;
        return new PlanChangeRow(id, target.PriceVersionId, target.PlanVersionId, digest, SubscriptionChangeState.Pending, null, now, actor.Subject,
            effectiveAt, newRevision, Created: true);
    }

    public async Task AttachProviderChangeAsync(Guid changeId, string providerChangeRef, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            UPDATE cp.subscription_change SET state = 'Scheduled', provider_change_ref = @r, updated_at = @now, version = version + 1
            WHERE id = @id AND state = 'Pending'
            """, conn);
        cmd.Parameters.AddWithValue("r", providerChangeRef);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("id", changeId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CancelAsync(BillingActor actor, int expectedRevision, string reason, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await Sql.ExecAsync(conn, tx, "SELECT id FROM cp.billing_account WHERE tenant_id = @t FOR UPDATE", ct, ("t", actor.TenantId));
        var (subscriptionId, _) = await LockSubscriptionAsync(conn, tx, actor.TenantId, expectedRevision, ct);
        var periodEnd = await Sql.ScalarAsync<DateTime?>(conn, tx, """
            UPDATE cp.subscription SET cancel_at_period_end = true, revision = revision + 1, updated_at = @now
            WHERE id = @id AND state IN ('Active','PastDue') AND NOT cancel_at_period_end RETURNING period_end
            """, ct, ("now", now), ("id", subscriptionId))
            ?? throw AppException.InvalidState("Only a paid subscription without a pending cancellation can be cancelled.");
        var audit = await CancelOpenChangesAsync(conn, tx, subscriptionId, "Subscription cancelled at period end", now, ct);
        audit.Add(new AuditEntry("SubscriptionCancellationRequested", "Subscription", subscriptionId, expectedRevision + 1, reason,
            new { cancelAtPeriodEnd = false }, new { cancelAtPeriodEnd = true, effectiveAt = new DateTimeOffset(periodEnd, TimeSpan.Zero) }));
        await AuditAppender.AppendAsync(conn, tx, actor.TenantId, Actor(actor), now, audit, ct);
        await tx.CommitAsync(ct);
    }

    public async Task ResumeAsync(BillingActor actor, int expectedRevision, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await Sql.ExecAsync(conn, tx, "SELECT id FROM cp.billing_account WHERE tenant_id = @t FOR UPDATE", ct, ("t", actor.TenantId));
        var (subscriptionId, _) = await LockSubscriptionAsync(conn, tx, actor.TenantId, expectedRevision, ct);
        var resumed = await Sql.ExecAsync(conn, tx, """
            UPDATE cp.subscription SET cancel_at_period_end = false, revision = revision + 1, updated_at = @now
            WHERE id = @id AND state IN ('Active','PastDue') AND cancel_at_period_end AND period_end > @now
            """, ct, ("now", now), ("id", subscriptionId));
        if (resumed == 0)
            throw AppException.InvalidState("There is no pending cancellation to withdraw. Ended service needs a new checkout.");
        await AuditAppender.AppendAsync(conn, tx, actor.TenantId, Actor(actor), now,
            [new AuditEntry("SubscriptionResumed", "Subscription", subscriptionId, expectedRevision + 1, null, new { cancelAtPeriodEnd = true },
                new { cancelAtPeriodEnd = false })], ct);
        await tx.CommitAsync(ct);
    }

    private static async Task<(Guid Id, long Revision)> LockSubscriptionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId,
        int expectedRevision, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT id, revision FROM cp.subscription WHERE tenant_id = @t FOR UPDATE", conn, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            throw AppException.NotFound("Subscription");
        var (id, revision) = (r.GetGuid(0), r.GetInt64(1));
        if (revision != expectedRevision)
            throw AppException.VersionConflict();
        return (id, revision);
    }

    private static async Task<long> BumpAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid subscriptionId, DateTimeOffset now, CancellationToken ct) =>
        (await Sql.ScalarAsync<long?>(conn, tx, "UPDATE cp.subscription SET revision = revision + 1, updated_at = @now WHERE id = @id RETURNING revision",
            ct, ("now", now), ("id", subscriptionId)))!.Value;

    private static async Task<List<AuditEntry>> CancelOpenChangesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid subscriptionId, string reason,
        DateTimeOffset now, CancellationToken ct)
    {
        var audit = new List<AuditEntry>();
        await using var cmd = new NpgsqlCommand("""
            UPDATE cp.subscription_change SET state = 'Cancelled', updated_at = @now, version = version + 1
            WHERE subscription_id = @s AND state IN ('Pending','Scheduled') RETURNING id, state::text
            """, conn, tx);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("s", subscriptionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            audit.Add(new AuditEntry("SubscriptionChangeCancelled", "SubscriptionChange", r.GetGuid(0), null, reason, null, new { state = "Cancelled" }));
        return audit;
    }

    private static async Task<PlanChangeRow?> ReadChangeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string where, CancellationToken ct,
        params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand($"""
            SELECT {ChangeColumns} FROM cp.subscription_change c JOIN cp.subscription s ON s.id = c.subscription_id WHERE {where}
            """, conn, tx);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new PlanChangeRow(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), Enum.Parse<SubscriptionChangeState>(r.GetString(4)),
                r.IsDBNull(5) ? null : r.GetString(5), r.GetFieldValue<DateTimeOffset>(6), r.GetString(7), r.GetFieldValue<DateTimeOffset>(8),
                r.GetInt64(9), Created: false)
            : null;
    }

    internal static IReadOnlySet<string> Features(string[] features) => new HashSet<string>(features, StringComparer.Ordinal);

    internal static IReadOnlyDictionary<QuotaKey, long> Limits(string json)
    {
        var limits = new Dictionary<QuotaKey, long>();
        using var doc = JsonDocument.Parse(json);
        foreach (var p in doc.RootElement.EnumerateObject())
            if (Enum.GetValues<QuotaKey>().Any(q => SaasWire.Quota(q) == p.Name) && p.Value.TryGetInt64(out var v))
                limits[SaasWire.ParseQuota(p.Name)] = v;
        return limits;
    }

    private static AuditActor Actor(BillingActor a) => new(a.Subject, ActorType.User, a.RequestId, a.CorrelationId);
}

/// <summary>Small Npgsql helpers for control-plane stores.</summary>
internal static class Sql
{
    public static async Task<int> ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct,
        params (string Name, object Value)[] args)
    {
        await using var cmd = Command(conn, tx, sql, args);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<T?> ScalarAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct,
        params (string Name, object Value)[] args)
    {
        await using var cmd = Command(conn, tx, sql, args);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? default : (T)result;
    }

    private static NpgsqlCommand Command(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, (string Name, object Value)[] args)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }
}

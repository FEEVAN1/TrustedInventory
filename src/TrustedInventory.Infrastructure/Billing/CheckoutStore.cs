using System.Data;
using Npgsql;
using TrustedInventory.Application.Billing;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Infrastructure.Audit;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Billing;

/// <summary>
/// Checkout and portal records on the ti_control pool (DEC-035, DEC-063). The tenant id always comes from the validated caller;
/// audit entries are appended to that tenant's own chain in the same transaction (AUD-001).
/// </summary>
public sealed class CheckoutStore(ControlDataSource control, IBillingProvider provider) : ICheckoutStore
{
    public async Task<CheckoutContext?> LoadAsync(Guid tenantId, Guid priceVersionId, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        SubscriptionState state;
        Guid contractedPlan;
        BillingAccountRow account;
        await using (var cmd = new NpgsqlCommand("""
            SELECT s.state::text, s.plan_version_id, a.id, a.business_legal_name, a.country, a.billing_contact_email, a.provider, a.provider_customer_id
            FROM cp.subscription s JOIN cp.billing_account a ON a.tenant_id = s.tenant_id AND a.id = s.billing_account_id
            WHERE s.tenant_id = @t
            """, conn))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return null;
            state = Enum.Parse<SubscriptionState>(r.GetString(0));
            contractedPlan = r.GetGuid(1);
            account = new BillingAccountRow(r.GetGuid(2), r.GetString(3), r.GetString(4).Trim(), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7));
        }

        CheckoutPriceRow? price = null;
        await using (var cmd = new NpgsqlCommand("""
            SELECT pr.id, p.id, p.plan_code, p.published_at IS NOT NULL, p.is_public, pr.currency, pr.minor_unit_amount, pr.exponent,
                   pr.billing_interval::text, pr.active_from, pr.active_to, pr.provider, pr.provider_price_ref
            FROM cp.price_version pr JOIN cp.plan_version p ON p.id = pr.plan_version_id WHERE pr.id = @id
            """, conn))
        {
            cmd.Parameters.AddWithValue("id", priceVersionId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
                price = new CheckoutPriceRow(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetBoolean(3), r.GetBoolean(4), r.GetString(5).Trim(),
                    r.GetInt64(6), r.GetInt16(7), Enum.Parse<BillingInterval>(r.GetString(8)), r.GetFieldValue<DateTimeOffset>(9),
                    r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10), r.IsDBNull(11) ? null : r.GetString(11),
                    r.IsDBNull(12) ? null : r.GetString(12));
        }
        return new CheckoutContext(state, contractedPlan, account, price);
    }

    public async Task<CheckoutRow> BeginAsync(BillingActor actor, Guid idempotencyKey, Guid priceVersionId, string digest, DateTimeOffset now,
        DateTimeOffset expiresAt, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // One checkout command per tenant at a time; the account row is the tenant's billing lock.
        var accountId = await ScalarAsync<Guid?>(conn, tx, "SELECT id FROM cp.billing_account WHERE tenant_id = @t FOR UPDATE", ct, ("t", actor.TenantId))
            ?? throw new InvalidOperationException("Tenant has no billing account.");

        var existing = await ReadAsync(conn, tx, "tenant_id = @t AND idempotency_key = @k", ct, ("t", actor.TenantId), ("k", idempotencyKey));
        if (existing is not null)
        {
            await tx.CommitAsync(ct);
            return existing;
        }

        var audit = new List<AuditEntry>();
        await using (var expire = new NpgsqlCommand("""
            UPDATE cp.checkout_session SET status = 'Expired' WHERE tenant_id = @t AND status = 'Open' RETURNING id
            """, conn, tx))
        {
            expire.Parameters.AddWithValue("t", actor.TenantId);
            await using var r = await expire.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                audit.Add(new AuditEntry("CheckoutExpired", "CheckoutSession", r.GetGuid(0), null, "Replaced by a new checkout", new { status = "Open" },
                    new { status = "Expired" }));
        }

        var id = Guid.CreateVersion7();
        await ExecAsync(conn, tx, """
            INSERT INTO cp.checkout_session (id, tenant_id, billing_account_id, price_version_id, status, accepted_preview_digest, idempotency_key,
                                             provider, expires_at, created_at, created_by)
            VALUES (@id, @t, @a, @p, 'Open', decode(@d, 'hex'), @k, @provider, @exp, @now, @by)
            """, ct, ("id", id), ("t", actor.TenantId), ("a", accountId), ("p", priceVersionId), ("d", digest), ("k", idempotencyKey),
            ("provider", provider.Code), ("exp", expiresAt), ("now", now), ("by", actor.Subject));
        audit.Add(new AuditEntry("CheckoutStarted", "CheckoutSession", id, null, null, null,
            new { priceVersionId, acceptedPreviewDigest = digest, expiresAt, provider = provider.Code }));
        await AuditAppender.AppendAsync(conn, tx, actor.TenantId, Actor(actor), now, audit, ct);
        await tx.CommitAsync(ct);
        return new CheckoutRow(id, priceVersionId, digest, idempotencyKey, CheckoutStatus.Open, null, expiresAt, Created: true);
    }

    public async Task SetCustomerAsync(Guid billingAccountId, string providerCode, string providerCustomerId, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            UPDATE cp.billing_account SET provider = @p, provider_customer_id = @c, version = version + 1, updated_at = now()
            WHERE id = @id AND provider_customer_id IS NULL
            """, conn);
        cmd.Parameters.AddWithValue("id", billingAccountId);
        cmd.Parameters.AddWithValue("p", providerCode);
        cmd.Parameters.AddWithValue("c", providerCustomerId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetPriceRefAsync(Guid priceVersionId, string providerCode, string providerPriceRef, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            UPDATE cp.price_version SET provider = @p, provider_price_ref = @r WHERE id = @id AND provider_price_ref IS NULL
            """, conn);
        cmd.Parameters.AddWithValue("id", priceVersionId);
        cmd.Parameters.AddWithValue("p", providerCode);
        cmd.Parameters.AddWithValue("r", providerPriceRef);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AttachProviderSessionAsync(Guid checkoutId, string providerSessionRef, string hostedUrl, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            UPDATE cp.checkout_session SET provider_session_ref = @r, hosted_url = @u WHERE id = @id AND provider_session_ref IS NULL
            """, conn);
        cmd.Parameters.AddWithValue("id", checkoutId);
        cmd.Parameters.AddWithValue("r", providerSessionRef);
        cmd.Parameters.AddWithValue("u", hostedUrl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordPortalAsync(BillingActor actor, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await AuditAppender.AppendAsync(conn, tx, actor.TenantId, Actor(actor), now,
            [new AuditEntry("BillingPortalOpened", "BillingAccount", null, null, null, null, new { provider = provider.Code, expiresAt })], ct);
        await tx.CommitAsync(ct);
    }

    private static AuditActor Actor(BillingActor a) => new(a.Subject, ActorType.User, a.RequestId, a.CorrelationId);

    private static async Task<CheckoutRow?> ReadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string where, CancellationToken ct,
        params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand($"""
            SELECT id, price_version_id, encode(accepted_preview_digest, 'hex'), idempotency_key, status::text, hosted_url, expires_at
            FROM cp.checkout_session WHERE {where}
            """, conn, tx);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new CheckoutRow(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), Enum.Parse<CheckoutStatus>(r.GetString(4)),
                r.IsDBNull(5) ? null : r.GetString(5), r.GetFieldValue<DateTimeOffset>(6), Created: false)
            : null;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct,
        params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct,
        params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? default! : (T)result;
    }
}

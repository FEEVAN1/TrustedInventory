using System.Text.Json;
using Npgsql;

namespace TrustedInventory.Migrator;

/// <summary>
/// SYNTHETIC development seed (AGT-004): the fixture plan catalog (docs/entitlement-catalog.json, currency XTS,
/// never chargeable), two Ready tenants with Active fixture subscriptions and current entitlement snapshots, and
/// memberships for the users in deploy/keycloak/trustedinventory-realm.json. Runs as ti_control, which can write
/// provisioning and control-plane rows but no stock (DEC-035). Fixed ids and natural keys make it idempotent.
/// Never run against a production database.
/// </summary>
public static class DevSeed
{
    public const string Actor = "seed:development";
    public const string SeedPlan = "enterprise";

    public static readonly Guid TenantA = Guid.Parse("10000000-0000-4000-8000-00000000000a");
    public static readonly Guid TenantB = Guid.Parse("10000000-0000-4000-8000-00000000000b");

    // Keycloak user ids, which become the token "sub" claim.
    public const string Alice = "00000000-0000-4000-8000-000000000002"; // tenant A owner, administrator + warehouse manager
    public const string Quinn = "00000000-0000-4000-8000-000000000003"; // tenant A QA officer
    public const string Bob = "00000000-0000-4000-8000-000000000004";   // tenant B owner + administrator

    public static async Task ApplyAsync(string controlConnectionString, TextWriter log, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(controlConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await CatalogAsync(conn, tx, ct);
        await Tenant(conn, tx, TenantA, "Synthetic Tenant A", "synthetic-a", ct);
        await Tenant(conn, tx, TenantB, "Synthetic Tenant B", "synthetic-b", ct);
        await Member(conn, tx, TenantA, Alice, "Alice (synthetic)", ["TenantOwner", "TenantAdministrator", "WarehouseManager"], ct);
        await Member(conn, tx, TenantA, Quinn, "Quinn (synthetic)", ["QaOfficer"], ct);
        await Member(conn, tx, TenantB, Bob, "Bob (synthetic)", ["TenantOwner", "TenantAdministrator"], ct);
        await Subscription(conn, tx, TenantA, "Synthetic Tenant A", ct);
        await Subscription(conn, tx, TenantB, "Synthetic Tenant B", ct);

        await tx.CommitAsync(ct);
        await log.WriteLineAsync("synthetic development seed applied (fixture catalog, 2 tenants)");
    }

    /// <summary>Publishes the fixture plans and prices once; existing versions are immutable and left alone (ENT-001).</summary>
    private static async Task CatalogAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var stream = typeof(DevSeed).Assembly.GetManifestResourceStream("catalog/entitlement-catalog.json")!;
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        foreach (var plan in doc.RootElement.GetProperty("plans").EnumerateArray())
        {
            var code = plan.GetProperty("planCode").GetString()!;
            var version = plan.GetProperty("version").GetInt32();
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO cp.plan_version (plan_code, version, display_name, features, quotas, is_public, published_at, created_by)
                VALUES (@code, @version, @name, @features, @quotas::jsonb, @public, now(), @by)
                ON CONFLICT (plan_code, version) DO NOTHING
                """, conn, tx))
            {
                insert.Parameters.AddWithValue("code", code);
                insert.Parameters.AddWithValue("version", version);
                insert.Parameters.AddWithValue("name", plan.GetProperty("displayName").GetString()!);
                insert.Parameters.AddWithValue("features", plan.GetProperty("features").EnumerateArray().Select(f => f.GetString()!).ToArray());
                insert.Parameters.AddWithValue("quotas", plan.GetProperty("quotas").GetRawText());
                insert.Parameters.AddWithValue("public", plan.GetProperty("public").GetBoolean());
                insert.Parameters.AddWithValue("by", Actor);
                await insert.ExecuteNonQueryAsync(ct);
            }
            foreach (var price in plan.GetProperty("prices").EnumerateArray())
            {
                await using var insertPrice = new NpgsqlCommand("""
                    INSERT INTO cp.price_version (plan_version_id, currency, minor_unit_amount, exponent, billing_interval, active_from, created_by)
                    SELECT p.id, @currency, @amount, @exponent, @interval::cp.billing_interval, now(), @by
                    FROM cp.plan_version p
                    WHERE p.plan_code = @code AND p.version = @version
                      AND NOT EXISTS (SELECT 1 FROM cp.price_version x WHERE x.plan_version_id = p.id
                                      AND x.billing_interval = @interval::cp.billing_interval AND x.currency = @currency)
                    """, conn, tx);
                insertPrice.Parameters.AddWithValue("currency", price.GetProperty("currency").GetString()!);
                insertPrice.Parameters.AddWithValue("amount", price.GetProperty("minorUnitAmount").GetInt64());
                insertPrice.Parameters.AddWithValue("exponent", (short)price.GetProperty("exponent").GetInt32());
                insertPrice.Parameters.AddWithValue("interval", price.GetProperty("interval").GetString()!);
                insertPrice.Parameters.AddWithValue("code", code);
                insertPrice.Parameters.AddWithValue("version", version);
                insertPrice.Parameters.AddWithValue("by", Actor);
                await insertPrice.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private static async Task Tenant(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, string name, string slug, CancellationToken ct)
    {
        await Exec(conn, tx, """
            INSERT INTO ti.tenant (id, name, slug, lifecycle, home_region, ready_at, created_by)
            VALUES (@id, @name, @slug, 'Ready', 'default', now(), @by) ON CONFLICT (id) DO NOTHING
            """, ct, ("id", id), ("name", name), ("slug", slug), ("by", Actor));
        await Exec(conn, tx, "INSERT INTO ti.audit_stream (tenant_id) VALUES (@id) ON CONFLICT (tenant_id) DO NOTHING", ct, ("id", id));
    }

    private static async Task Member(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, string subject, string displayName,
        string[] roles, CancellationToken ct)
    {
        await Exec(conn, tx, """
            INSERT INTO ti.membership (tenant_id, user_subject, display_name, created_by)
            VALUES (@t, @s, @n, @by) ON CONFLICT (tenant_id, user_subject) DO NOTHING
            """, ct, ("t", tenantId), ("s", subject), ("n", displayName), ("by", Actor));
        foreach (var role in roles)
            await Exec(conn, tx, """
                INSERT INTO ti.role_grant (tenant_id, membership_id, grantee_subject, role, reason, granted_by, created_by)
                SELECT m.tenant_id, m.id, m.user_subject, @r::ti.role_name, 'synthetic development seed', @by, @by
                FROM ti.membership m
                WHERE m.tenant_id = @t AND m.user_subject = @s
                  AND NOT EXISTS (SELECT 1 FROM ti.role_grant g WHERE g.membership_id = m.id AND g.role = @r::ti.role_name
                                  AND g.revoked_at IS NULL)
                """, ct, ("t", tenantId), ("s", subject), ("r", role), ("by", Actor));
    }

    /// <summary>Active fixture subscription for a year plus its current entitlement snapshot (SAS-001).</summary>
    private static async Task Subscription(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, string name, CancellationToken ct)
    {
        await Exec(conn, tx, """
            INSERT INTO cp.billing_account (tenant_id, billing_contact_email, business_legal_name, country, created_by)
            VALUES (@t, 'billing@example.invalid', @n, 'NG', @by) ON CONFLICT (tenant_id) DO NOTHING
            """, ct, ("t", tenantId), ("n", name), ("by", Actor));
        await Exec(conn, tx, """
            INSERT INTO cp.subscription (tenant_id, billing_account_id, plan_version_id, price_version_id, state, period_start, period_end)
            SELECT @t, a.id, p.id, pr.id, 'Active', now(), now() + interval '365 days'
            FROM cp.billing_account a, cp.plan_version p
            JOIN cp.price_version pr ON pr.plan_version_id = p.id AND pr.billing_interval = 'Month'
            WHERE a.tenant_id = @t AND p.plan_code = @plan AND p.version = 1
            ON CONFLICT (tenant_id) DO NOTHING
            """, ct, ("t", tenantId), ("plan", SeedPlan));
        await Exec(conn, tx, """
            INSERT INTO cp.entitlement_snapshot (tenant_id, subscription_revision, entitlement_version, plan_version_id, features, limits,
                                                 effective_from, is_current)
            SELECT s.tenant_id, s.revision, 1, p.id, p.features, p.quotas, now(), true
            FROM cp.subscription s JOIN cp.plan_version p ON p.id = s.plan_version_id
            WHERE s.tenant_id = @t AND NOT EXISTS (SELECT 1 FROM cp.entitlement_snapshot e WHERE e.tenant_id = @t AND e.is_current)
            """, ct, ("t", tenantId));
    }

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct,
        params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

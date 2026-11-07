using Npgsql;

namespace TrustedInventory.IntegrationTests.Support;

/// <summary>
/// Two isolated synthetic tenants (section 19 seed data). Inserted as the superuser purely to arrange test
/// state; the API under test then runs as ti_app with RLS. Subjects are synthetic, not real identities.
/// </summary>
public sealed class SeedData
{
    public const string PlatformAdmin = "user-platform-admin";

    public required TenantSeed A { get; init; }
    public required TenantSeed B { get; init; }

    public static async Task<SeedData> CreateAsync(string superuserConnection)
    {
        await using var conn = new NpgsqlConnection(superuserConnection);
        await conn.OpenAsync();
        var plan = await SaasSeed.PlanAsync(conn, "test-unlimited", SaasSeed.AllFeatures, 1_000_000);
        var a = await TenantSeed.CreateAsync(conn, "a");
        var b = await TenantSeed.CreateAsync(conn, "b");
        await SaasSeed.ActiveSubscriptionAsync(conn, a.TenantId, plan);
        await SaasSeed.ActiveSubscriptionAsync(conn, b.TenantId, plan);
        return new SeedData { A = a, B = b };
    }
}

/// <summary>
/// Control-plane fixtures (SYNTHETIC, currency XTS): plans, billing accounts, subscriptions and entitlement
/// snapshots, so tenants created by tests have a defined access state (SAS-001).
/// </summary>
public static class SaasSeed
{
    public static readonly string[] AllFeatures = ["core_inventory", "service_api", "advanced_reporting", "medical_storage", "telemetry"];

    public static async Task<Guid> PlanAsync(NpgsqlConnection conn, string code, string[] features, long limitEach)
    {
        var id = Guid.CreateVersion7();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO cp.plan_version (id, plan_code, version, display_name, features, quotas, published_at, created_by)
            VALUES (@id, @code, 1, @code, @f, @q::jsonb, now(), 'seed')
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("code", code);
        cmd.Parameters.AddWithValue("f", features);
        cmd.Parameters.AddWithValue("q", $$"""
            {"active_seats":{{limitEach}},"active_warehouses":{{limitEach}},"active_skus":{{limitEach}},"active_devices":{{limitEach}},"attachment_bytes":{{limitEach}}}
            """);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary>Active paid subscription for a year plus a current entitlement snapshot copied from the plan.</summary>
    public static async Task ActiveSubscriptionAsync(NpgsqlConnection conn, Guid tenantId, Guid planId)
    {
        await using var cmd = new NpgsqlCommand("""
            WITH price AS (
              INSERT INTO cp.price_version (plan_version_id, currency, minor_unit_amount, exponent, billing_interval, active_from, created_by)
              VALUES (@plan, 'XTS', 1000, 2, 'Month', now() - interval '1 day', 'seed') RETURNING id),
            account AS (
              INSERT INTO cp.billing_account (tenant_id, billing_contact_email, business_legal_name, country, created_by)
              VALUES (@t, 'billing@example.invalid', 'Synthetic business', 'NG', 'seed') RETURNING id),
            sub AS (
              INSERT INTO cp.subscription (tenant_id, billing_account_id, plan_version_id, price_version_id, state, period_start, period_end)
              SELECT @t, account.id, @plan, price.id, 'Active', now() - interval '1 day', now() + interval '365 days'
              FROM account, price RETURNING revision)
            INSERT INTO cp.entitlement_snapshot (tenant_id, subscription_revision, entitlement_version, plan_version_id, features,
                                                 limits, effective_from, is_current)
            SELECT @t, sub.revision, 1, p.id, p.features, p.quotas, now() - interval '1 day', true
            FROM sub, cp.plan_version p WHERE p.id = @plan
            """, conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("plan", planId);
        await cmd.ExecuteNonQueryAsync();
    }
}

public sealed class TenantSeed
{
    public required Guid TenantId { get; init; }
    public required string Admin { get; init; }
    public required string Manager { get; init; }
    public required string Picker { get; init; }
    public required string Auditor { get; init; }
    /// <summary>Member with no role grants at all.</summary>
    public required string NoRoles { get; init; }
    /// <summary>Warehouse manager limited to <see cref="ScopedWarehouseId"/>.</summary>
    public required string ScopedManager { get; init; }
    public required Guid ScopedWarehouseId { get; init; }

    public static async Task<TenantSeed> CreateAsync(NpgsqlConnection conn, string key)
    {
        var tenantId = Guid.CreateVersion7();
        await Exec(conn, "INSERT INTO ti.tenant (id, name, created_by) VALUES (@id, @name, 'seed')", ("id", tenantId), ("name", $"Tenant {key.ToUpperInvariant()}"));
        await Exec(conn, "INSERT INTO ti.audit_stream (tenant_id) VALUES (@id)", ("id", tenantId));

        var scopedWarehouse = Guid.CreateVersion7();
        await Exec(conn, """
            INSERT INTO ti.warehouse (id, tenant_id, code, name, timezone, created_by)
            VALUES (@id, @t, 'SEED-WH', 'Seed warehouse', 'Africa/Lagos', 'seed')
            """, ("id", scopedWarehouse), ("t", tenantId));

        var seed = new TenantSeed
        {
            TenantId = tenantId,
            Admin = $"user-{key}-admin",
            Manager = $"user-{key}-manager",
            Picker = $"user-{key}-picker",
            Auditor = $"user-{key}-auditor",
            NoRoles = $"user-{key}-noroles",
            ScopedManager = $"user-{key}-scoped-manager",
            ScopedWarehouseId = scopedWarehouse,
        };
        await Member(conn, tenantId, seed.Admin, ("TenantAdministrator", null));
        await Member(conn, tenantId, seed.Manager, ("WarehouseManager", null));
        await Member(conn, tenantId, seed.Picker, ("Picker", null));
        await Member(conn, tenantId, seed.Auditor, ("Auditor", null));
        await Member(conn, tenantId, seed.NoRoles);
        await Member(conn, tenantId, seed.ScopedManager, ("TenantAdministrator", scopedWarehouse));
        return seed;
    }

    private static async Task Member(NpgsqlConnection conn, Guid tenantId, string subject, params (string Role, Guid? Warehouse)[] roles)
    {
        var membershipId = Guid.CreateVersion7();
        await Exec(conn, "INSERT INTO ti.membership (id, tenant_id, user_subject, created_by) VALUES (@id, @t, @s, 'seed')",
            ("id", membershipId), ("t", tenantId), ("s", subject));
        foreach (var (role, warehouse) in roles)
            await Exec(conn, """
                INSERT INTO ti.role_grant (tenant_id, membership_id, grantee_subject, role, warehouse_id, reason, granted_by, created_by)
                VALUES (@t, @m, @s, @r::ti.role_name, @w, 'test seed', 'seed', 'seed')
                """, ("t", tenantId), ("m", membershipId), ("s", subject), ("r", role), ("w", (object?)warehouse ?? DBNull.Value));
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}

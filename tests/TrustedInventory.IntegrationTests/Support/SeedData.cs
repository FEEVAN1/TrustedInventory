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
        return new SeedData
        {
            A = await TenantSeed.CreateAsync(conn, "a"),
            B = await TenantSeed.CreateAsync(conn, "b"),
        };
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

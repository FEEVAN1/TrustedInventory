using Npgsql;

namespace TrustedInventory.Migrator;

/// <summary>
/// SYNTHETIC development seed (AGT-004): two tenants and memberships for the users in
/// deploy/keycloak/trustedinventory-realm.json. Runs as ti_platform, so it can only touch provisioning tables.
/// Fixed ids make it idempotent. Never run against a production database.
/// </summary>
public static class DevSeed
{
    public const string Actor = "seed:development";

    public static readonly Guid TenantA = Guid.Parse("10000000-0000-4000-8000-00000000000a");
    public static readonly Guid TenantB = Guid.Parse("10000000-0000-4000-8000-00000000000b");

    // Keycloak user ids, which become the token "sub" claim.
    public const string Alice = "00000000-0000-4000-8000-000000000002"; // tenant A administrator + warehouse manager
    public const string Quinn = "00000000-0000-4000-8000-000000000003"; // tenant A QA officer
    public const string Bob = "00000000-0000-4000-8000-000000000004";   // tenant B administrator

    public static async Task ApplyAsync(string platformConnectionString, TextWriter log, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(platformConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await Tenant(conn, tx, TenantA, "Synthetic Tenant A", ct);
        await Tenant(conn, tx, TenantB, "Synthetic Tenant B", ct);
        await Member(conn, tx, TenantA, Alice, "Alice (synthetic)", ["TenantAdministrator", "WarehouseManager"], ct);
        await Member(conn, tx, TenantA, Quinn, "Quinn (synthetic)", ["QaOfficer"], ct);
        await Member(conn, tx, TenantB, Bob, "Bob (synthetic)", ["TenantAdministrator"], ct);

        await tx.CommitAsync(ct);
        await log.WriteLineAsync("synthetic development seed applied");
    }

    private static async Task Tenant(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, string name, CancellationToken ct)
    {
        await Exec(conn, tx, "INSERT INTO ti.tenant (id, name, created_by) VALUES (@id, @name, @by) ON CONFLICT (id) DO NOTHING",
            ct, ("id", id), ("name", name), ("by", Actor));
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

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct,
        params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

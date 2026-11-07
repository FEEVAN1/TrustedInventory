using Npgsql;
using TrustedInventory.Infrastructure;
using TrustedInventory.IntegrationTests.Support;
using TrustedInventory.Migrator;

namespace TrustedInventory.IntegrationTests.Foundation;

/// <summary>Database-level isolation guarantees, checked on the migrated schema (docs/migration-design.md §2).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class SchemaConformanceTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "TEN-001")]
    [Trait("Requirement", "TEN-002")]
    public async Task Every_tenant_table_has_forced_rls_policy_and_tenant_composite_key()
    {
        var problems = await QueryAsync("""
            SELECT c.relname,
                   c.relrowsecurity AND c.relforcerowsecurity AS forced,
                   EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid
                           AND p.polname IN ('tenant_isolation', 'tenant_self')) AS has_policy,
                   c.relname = 'audit_stream' OR EXISTS (
                     SELECT 1 FROM pg_constraint k WHERE k.conrelid = c.oid AND k.contype = 'u'
                     AND (SELECT array_agg(a.attname::text ORDER BY a.attname) FROM unnest(k.conkey) col
                          JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = col) = ARRAY['id','tenant_id']) AS has_key
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'ti' AND c.relkind = 'r'
              AND EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attname IN ('tenant_id') AND NOT a.attisdropped)
            """, r => r.GetBoolean(1) && r.GetBoolean(2) && r.GetBoolean(3) ? null : r.GetString(0));
        Assert.Empty(problems);
        Assert.True(await CountAsync("SELECT count(*) FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid WHERE c.relname = 'tenant'") > 0);
    }

    [Fact]
    [Trait("Requirement", "TEN-002")]
    [Trait("Requirement", "TEN-004")]
    public async Task Runtime_roles_own_nothing_and_cannot_bypass_rls()
    {
        Assert.Empty(await QueryAsync("""
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'ti' AND pg_get_userbyid(c.relowner) <> 'ti_owner' AND c.relkind IN ('r','v','S')
            """, r => r.GetString(0)));
        Assert.Empty(await QueryAsync("""
            SELECT rolname FROM pg_roles WHERE rolname LIKE 'ti\_%' AND (rolbypassrls OR rolsuper OR rolcreaterole OR rolcreatedb)
            """, r => r.GetString(0)));
        // No universal stock bypass: neither the platform role nor the owner has a policy on catalog/stock tables.
        Assert.Equal(0, await CountAsync("""
            SELECT count(*) FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid
            WHERE c.relname IN ('warehouse','product','bin') AND p.polroles && ARRAY(SELECT oid FROM pg_roles WHERE rolname IN ('ti_platform','ti_owner'))
            """));
    }

    [Fact]
    [Trait("Requirement", "AUD-003")]
    public async Task Runtime_role_cannot_update_or_delete_audit_rows()
    {
        Assert.Equal(0, await CountAsync("""
            SELECT count(*) FROM information_schema.role_table_grants
            WHERE grantee IN ('ti_app','ti_platform') AND table_schema = 'ti'
              AND table_name IN ('audit_event','audit_checkpoint') AND privilege_type IN ('UPDATE','DELETE','TRUNCATE')
            """));

        await using var conn = new NpgsqlConnection(fx.AppConnection);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{fx.Seed.A.TenantId}', true)", conn, tx))
            await set.ExecuteNonQueryAsync();
        await using var update = new NpgsqlCommand("UPDATE ti.audit_event SET reason = 'tampered'", conn, tx);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    [Trait("Requirement", "NFR-MIGRATIONS")]
    public async Task Migrations_are_recorded_rerun_is_a_no_op_and_build_expects_latest()
    {
        Assert.Equal(SchemaVersion.Required, MigrationRunner.LatestMigrationId);
        var rerun = await new MigrationRunner(fx.MigratorConnection, TextWriter.Null).ApplyAsync();
        Assert.Empty(rerun);
        var applied = await QueryAsync("SELECT id || ':' || checksum FROM ti.schema_migration ORDER BY id", r => r.GetString(0));
        Assert.Equal(SqlScripts.Migrations.Select(m => $"{m.Id}:{m.Checksum}"), applied);
    }

    private async Task<List<string>> QueryAsync(string sql, Func<NpgsqlDataReader, string?> map)
    {
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync())
            if (map(reader) is { } value)
                result.Add(value);
        return result;
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}

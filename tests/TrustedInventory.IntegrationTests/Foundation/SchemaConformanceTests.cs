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
        // ti: tenant-safe referencing key = UNIQUE (tenant_id, id), or a primary key that includes tenant_id
        // for rows nothing references by id (usage_counter, audit_stream). cp: forced RLS with a policy.
        var problems = await QueryAsync("""
            SELECT n.nspname || '.' || c.relname,
                   c.relrowsecurity AND c.relforcerowsecurity AS forced,
                   EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid
                           AND (n.nspname = 'cp' OR p.polname IN ('tenant_isolation', 'tenant_self'))) AS has_policy,
                   n.nspname = 'cp' OR EXISTS (
                     SELECT 1 FROM pg_constraint k WHERE k.conrelid = c.oid
                     AND ((k.contype = 'u'
                           AND (SELECT array_agg(a.attname::text ORDER BY a.attname) FROM unnest(k.conkey) col
                                JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = col) = ARRAY['id','tenant_id'])
                       OR (k.contype = 'p'
                           AND EXISTS (SELECT 1 FROM unnest(k.conkey) col JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = col
                                       WHERE a.attname = 'tenant_id')))) AS has_key
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('ti', 'cp') AND c.relkind = 'r'
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
            WHERE n.nspname IN ('ti', 'cp') AND pg_get_userbyid(c.relowner) <> 'ti_owner' AND c.relkind IN ('r','v','S')
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
    [Trait("Requirement", "SAS-004")]
    public async Task Control_plane_and_platform_roles_have_no_inventory_privileges()
    {
        // Billing and platform operators never read stock or catalog data (SAS-004, DEC-035).
        var inventory = new[] { "warehouse", "zone", "bin", "product", "product_conversion", "product_barcode", "unit",
            "partner", "storage_profile", "configuration_version", "alert", "outbox_event", "idempotency_record" };
        var leaks = await QueryAsync($"""
            SELECT r || ' on ' || t FROM unnest(ARRAY['ti_control','ti_platform']) r,
                 unnest(ARRAY['{string.Join("','", inventory)}']) t
            WHERE has_table_privilege(r, 'ti.' || t, 'SELECT') OR has_table_privilege(r, 'ti.' || t, 'INSERT')
               OR has_table_privilege(r, 'ti.' || t, 'UPDATE') OR has_table_privilege(r, 'ti.' || t, 'DELETE')
            """, r => r.GetString(0));
        Assert.Empty(leaks);
        // The platform role cannot even see the control-plane schema.
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM pg_namespace WHERE nspname = 'cp' AND has_schema_privilege('ti_platform', oid, 'USAGE')"));
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

    [Fact]
    [Trait("Requirement", "NFR-MIGRATIONS")]
    [Trait("Requirement", "SOP-005")]
    public async Task Tenant_contract_step_removed_the_v2_status_and_enforces_lifecycle()
    {
        // 0005 (DEC-042): the v2.0 status column, its type, the compatibility trigger and the v1 membership function are gone.
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM information_schema.columns WHERE table_schema = 'ti' AND table_name = 'tenant' AND column_name = 'status'"));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace WHERE n.nspname = 'ti' AND t.typname = 'tenant_status'"));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM pg_trigger WHERE tgname = 'tenant_sync_legacy_status'"));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'ti' AND p.proname IN ('tenant_sync_legacy_status', 'user_memberships')"));
        // Lifecycle, slug and home region are mandatory; an omitted lifecycle means a fresh signup.
        var required = await QueryAsync("""
            SELECT column_name || ':' || is_nullable || ':' || coalesce(column_default, '')
            FROM information_schema.columns
            WHERE table_schema = 'ti' AND table_name = 'tenant' AND column_name IN ('lifecycle', 'slug', 'home_region')
            ORDER BY column_name
            """, r => r.GetString(0));
        Assert.Equal(["home_region:NO:", "lifecycle:NO:'Provisioning'::ti.tenant_lifecycle", "slug:NO:"], required);
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

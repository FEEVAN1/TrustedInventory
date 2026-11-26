using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TrustedInventory.Application.Operations;
using TrustedInventory.Application.Saas;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Infrastructure;
using TrustedInventory.Infrastructure.Jobs;
using TrustedInventory.Infrastructure.Persistence;
using TrustedInventory.Infrastructure.Saas;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;
using TrustedInventory.Migrator;

namespace TrustedInventory.IntegrationTests.Saas;

/// <summary>
/// Approved deletion, retention holds and the deletion register replayed on a restored backup (LFC-004, AT-LFC-01, AT-LFC-02, DEC-072).
/// Destructive deletion runs only on the throwaway test database (section 30).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class DeletionTests(ApiFixture fx)
{
    private static readonly string[] Retained = ["audit_event", "audit_checkpoint", "audit_stream"];

    [Fact]
    [Trait("Requirement", "LFC-004")]
    [Trait("Requirement", "AT-LFC-01")]
    [Trait("Requirement", "AT-LFC-02")]
    [Trait("Requirement", "AUD-003")]
    public async Task Approved_deletion_waits_for_the_window_and_holds_then_removes_live_data_and_keeps_evidence()
    {
        var t = await TenantWithDataAsync("del-flow");
        var closure = await (await OffboardingTests.Close(t.Owner, t.Subject, $"CLOSE {t.Slug}", deletion: true)).ExpectAsync(HttpStatusCode.Accepted);
        var recordId = Guid.Parse((string)closure["id"]!);
        var windowEnds = DateTimeOffset.Parse((string)closure["exportWindowEndsAt"]!, System.Globalization.CultureInfo.InvariantCulture);
        var platform = fx.Client(SeedData.PlatformAdmin, tenantId: null);

        // A legal hold blocks approval; clearing it (reasoned, audited) allows it. Approval is a platform decision, not the owner's.
        var version = (await platform.GetAsync($"/platform/v1/tenants/{t.TenantId}")).ETag();
        var held = await platform.PostJsonAsync($"/platform/v1/tenants/{t.TenantId}/retention-hold",
            new { hold = true, reason = "Regulator enquiry", reference = "REG-17" }, version);
        await held.ExpectAsync(HttpStatusCode.OK);
        await (await platform.PostJsonAsync($"/platform/v1/tenants/{t.TenantId}/deletion/approve", new { reason = "Owner request verified" }, held.ETag()))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "RETENTION_BLOCKED");
        var cleared = await platform.PostJsonAsync($"/platform/v1/tenants/{t.TenantId}/retention-hold",
            new { hold = false, reason = "Enquiry closed", reference = (string?)null }, held.ETag());
        await cleared.ExpectAsync(HttpStatusCode.OK);
        await (await platform.PostJsonAsync($"/platform/v1/tenants/{t.TenantId}/deletion/approve", new { reason = "Owner request verified" }, cleared.ETag()))
            .ExpectAsync(HttpStatusCode.OK);

        // Before the export window ends nothing happens, and the database refuses a purge it has not approved.
        await RunJobAsync(windowEnds.AddMinutes(-1));
        Assert.Equal("Archived", await ScalarAsync<string>("SELECT lifecycle::text FROM ti.tenant WHERE id = @t", ("t", t.TenantId)));
        var refused = await Assert.ThrowsAsync<PostgresException>(() => PurgeAsControlAsync(t.TenantId));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);

        var auditBefore = await ScalarAsync<long>("SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t", ("t", t.TenantId));
        Assert.True(Directory.Exists(TenantObjects(t.TenantId)));
        await RunJobAsync(windowEnds.AddMinutes(1));

        Assert.Equal("Deleted", await ScalarAsync<string>("SELECT lifecycle::text FROM ti.tenant WHERE id = @t", ("t", t.TenantId)));
        Assert.Equal("Deleted", await ScalarAsync<string>("SELECT status::text FROM cp.deletion_record WHERE id = @id", ("id", recordId)));
        Assert.True(await ScalarAsync<bool>("SELECT backup_expiry_at > completed_at FROM cp.deletion_record WHERE id = @id", ("id", recordId)));
        foreach (var (table, rows) in await LiveRowsAsync(t.TenantId))
            Assert.True(rows == 0, $"ti.{table} still has {rows} rows of the deleted tenant");
        Assert.False(Directory.Exists(TenantObjects(t.TenantId)));

        // Evidence is retained and the chain still verifies: earlier events are untouched, the deletion steps are appended.
        Assert.True(await ScalarAsync<long>("SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t", ("t", t.TenantId)) > auditBefore);
        foreach (var evt in new[] { "TenantDeletionApproved", "TenantDeletionStarted", "TenantDeleted" })
            Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = @e",
                ("t", t.TenantId), ("e", evt)));
        Assert.True(await ScalarAsync<long>("SELECT count(*) FROM cp.subscription WHERE tenant_id = @t", ("t", t.TenantId)) == 1);

        // No access remains: the former owner is no member, and a platform view shows the Deleted mode.
        await (await t.Owner.GetAsync("/api/v1/warehouses")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        var view = await (await platform.GetAsync($"/platform/v1/tenants/{t.TenantId}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Deleted", (string?)view["tenant"]!["accessMode"]);

        // A rerun changes nothing.
        await RunJobAsync(windowEnds.AddDays(1));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'TenantDeleted'",
            ("t", t.TenantId)));
    }

    [Fact]
    [Trait("Requirement", "LFC-004")]
    [Trait("Requirement", "LFC-002")]
    public async Task Deletion_needs_the_owners_request_and_stops_at_safety_obligations_found_on_the_deletion_date()
    {
        var keep = await TenantWithDataAsync("del-keep");
        await (await OffboardingTests.Close(keep.Owner, keep.Subject, $"CLOSE {keep.Slug}", deletion: false)).ExpectAsync(HttpStatusCode.Accepted);
        var platform = fx.Client(SeedData.PlatformAdmin, tenantId: null);
        var v = (await platform.GetAsync($"/platform/v1/tenants/{keep.TenantId}")).ETag();
        await (await platform.PostJsonAsync($"/platform/v1/tenants/{keep.TenantId}/deletion/approve", new { reason = "Tidy up" }, v))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");

        var late = await TenantWithDataAsync("del-late");
        var closure = await (await OffboardingTests.Close(late.Owner, late.Subject, $"CLOSE {late.Slug}", deletion: true)).ExpectAsync(HttpStatusCode.Accepted);
        var windowEnds = DateTimeOffset.Parse((string)closure["exportWindowEndsAt"]!, System.Globalization.CultureInfo.InvariantCulture);
        var lv = (await platform.GetAsync($"/platform/v1/tenants/{late.TenantId}")).ETag();
        await (await platform.PostJsonAsync($"/platform/v1/tenants/{late.TenantId}/deletion/approve", new { reason = "Owner request verified" }, lv))
            .ExpectAsync(HttpStatusCode.OK);
        // Controlled stock appears during the window (for example a returned recall arriving): deletion stops, nothing is removed.
        await TenantProvisioning.ExecAsSuperuserAsync(fx, """
            UPDATE ti.stock_bucket SET quality_status = 'Hold' WHERE tenant_id = @t
            """, ("t", late.TenantId));
        await RunJobAsync(windowEnds.AddMinutes(1));
        Assert.Equal("Archived", await ScalarAsync<string>("SELECT lifecycle::text FROM ti.tenant WHERE id = @t", ("t", late.TenantId)));
        Assert.Equal("Blocked", await ScalarAsync<string>("SELECT status::text FROM cp.deletion_record WHERE id = @id",
            ("id", Guid.Parse((string)closure["id"]!))));
        Assert.True(await ScalarAsync<long>("SELECT count(*) FROM ti.stock_bucket WHERE tenant_id = @t", ("t", late.TenantId)) > 0);
    }

    [Fact]
    [Trait("Requirement", "AT-LFC-02")]
    [Trait("Requirement", "LFC-004")]
    public async Task A_restored_backup_replays_the_deletion_register_so_the_deleted_tenant_stays_inaccessible()
    {
        var t = await TenantWithDataAsync("del-restore");
        var other = await TenantWithDataAsync("del-restore-other");
        var backup = $"lfc02-{Guid.NewGuid():N}";
        await fx.BackupAsync(backup); // taken while both workspaces are live

        var closure = await (await OffboardingTests.Close(t.Owner, t.Subject, $"CLOSE {t.Slug}", deletion: true)).ExpectAsync(HttpStatusCode.Accepted);
        var platform = fx.Client(SeedData.PlatformAdmin, tenantId: null);
        var v = (await platform.GetAsync($"/platform/v1/tenants/{t.TenantId}")).ETag();
        await (await platform.PostJsonAsync($"/platform/v1/tenants/{t.TenantId}/deletion/approve", new { reason = "Owner request verified" }, v))
            .ExpectAsync(HttpStatusCode.OK);
        await RunJobAsync(DateTimeOffset.Parse((string)closure["exportWindowEndsAt"]!, System.Globalization.CultureInfo.InvariantCulture).AddMinutes(1));
        var register = (await DeletionRegister.ReadAsync(fx.ControlConnection)).Where(e => e.TenantId == t.TenantId).ToList();
        Assert.Single(register);

        // Restore the earlier backup: it still holds the tenant, its members and its stock.
        var restored = await fx.RestoreAsync(backup, $"restored_{Guid.NewGuid():N}"[..30], "ti_control", ApiFixture.ControlRolePassword);
        await using (var conn = new NpgsqlConnection(restored))
        {
            await conn.OpenAsync();
            var before = await DeletionRegister.VerifyAsync(conn, register);
            Assert.False(Assert.Single(before).Inaccessible);
        }

        // Replay before opening access: the tenant is deleted again; the other tenant is untouched.
        var log = new StringWriter();
        Assert.True(await DeletionRegister.ReplayAsync(restored, register, log), log.ToString());
        await using (var conn = new NpgsqlConnection(restored))
        {
            await conn.OpenAsync();
            Assert.True(Assert.Single(await DeletionRegister.VerifyAsync(conn, register)).Inaccessible);
            await using var cmd = new NpgsqlCommand("""
                SELECT (SELECT lifecycle::text FROM ti.tenant WHERE id = @o), (SELECT count(*) FROM ti.membership WHERE tenant_id = @o),
                       (SELECT status::text FROM cp.deletion_record WHERE id = @r)
                """, conn);
            cmd.Parameters.AddWithValue("o", other.TenantId);
            cmd.Parameters.AddWithValue("r", register[0].RecordId);
            await using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            Assert.Equal("Ready", r.GetString(0));
            Assert.True(r.GetInt64(1) > 0);
            Assert.Equal("Deleted", r.GetString(2));
        }

        // A second replay is harmless (the register may be replayed after every restore).
        Assert.True(await DeletionRegister.ReplayAsync(restored, register, TextWriter.Null));
    }

    [Fact]
    [Trait("Requirement", "AUD-003")]
    [Trait("Requirement", "LFC-004")]
    public async Task No_runtime_role_can_delete_append_only_or_audit_rows_outside_an_approved_purge()
    {
        var t = await TenantWithDataAsync("del-guard");
        await using var conn = new NpgsqlConnection(fx.AppConnection);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true), set_config('ti.purge_tenant', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", t.TenantId.ToString());
            await set.ExecuteNonQueryAsync();
        }
        // Even with the purge marker set by hand, ti_app has no DELETE privilege on the ledger or the audit trail.
        foreach (var table in new[] { "stock_movement", "audit_event" })
        {
            await using var sp = new NpgsqlCommand("SAVEPOINT s", conn, tx);
            await sp.ExecuteNonQueryAsync();
            await using var del = new NpgsqlCommand($"DELETE FROM ti.{table} WHERE tenant_id = @t", conn, tx);
            del.Parameters.AddWithValue("t", t.TenantId);
            var e = await Assert.ThrowsAsync<PostgresException>(() => del.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, e.SqlState);
            await using var back = new NpgsqlCommand("ROLLBACK TO SAVEPOINT s", conn, tx);
            await back.ExecuteNonQueryAsync();
        }
        // ti_control may call the purge, but the database refuses a tenant that is not approved for deletion.
        var refused = await Assert.ThrowsAsync<PostgresException>(() => PurgeAsControlAsync(t.TenantId));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        Assert.True(await ScalarAsync<long>("SELECT count(*) FROM ti.membership WHERE tenant_id = @t", ("t", t.TenantId)) > 0);
    }

    // ---- helpers ----

    private sealed record Workspace(Guid TenantId, string Subject, string Slug, HttpClient Owner);

    /// <summary>A Ready workspace with catalog, locations, a stock bucket and one stored object.</summary>
    private async Task<Workspace> TenantWithDataAsync(string label)
    {
        var signed = await TenantProvisioning.SignUpAsync(fx, label);
        var owner = fx.Client(signed.Admin, signed.TenantId);
        async Task<Guid> Create(string path, object body) =>
            Guid.Parse((string)(await (await owner.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!);
        var warehouse = await Create("/api/v1/warehouses", new { code = "W1", name = "Main", timeZone = "UTC" });
        var zone = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "Z1", name = "Ambient" });
        var bin = await Create("/api/v1/bins", new { zoneId = zone, code = "B1", kind = "Storage" });
        var unit = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        var product = await Create("/api/v1/products",
            new { sku = "GEN-1", name = "Synthetic item", handlingProfile = "General", trackingMode = "Lot", baseUomId = unit, basePrecision = 0 });
        var lot = await ScalarAsync<Guid>("""
            INSERT INTO ti.lot (tenant_id, product_id, handling_profile, lot_number, expiry_date, created_by)
            VALUES (@t, @p, 'General', 'LOT-G-1', current_date + 365, 'test') RETURNING id
            """, ("t", signed.TenantId), ("p", product));
        await TenantProvisioning.ExecAsSuperuserAsync(fx, """
            INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, lot_id, quality_status, received_at, on_hand_qty, created_by)
            VALUES (@t, @w, @b, @p, @l, 'Available', now(), 3, 'test')
            """, ("t", signed.TenantId), ("w", warehouse), ("b", bin), ("p", product), ("l", lot));
        // An object in the tenant's storage prefix, as an attachment or export would leave.
        await fx.Factory.Services.GetRequiredService<IObjectStore>().PutAsync($"{signed.TenantId:D}/attachments/{Guid.NewGuid():D}",
            Encoding.ASCII.GetBytes("%PDF-1.7 synthetic"), CancellationToken.None);
        var slug = await ScalarAsync<string>("SELECT slug FROM ti.tenant WHERE id = @t", ("t", signed.TenantId));
        return new Workspace(signed.TenantId, signed.Admin, slug, owner);
    }

    private async Task RunJobAsync(DateTimeOffset at)
    {
        await using var app = DependencyInjection.BuildDataSource(fx.AppConnection);
        await using var platform = DependencyInjection.BuildDataSource(fx.PlatformConnection);
        var clock = new FakeTimeProvider(at);
        var runner = new TenantJobRunner(new PlatformDataSource(platform), new AppDataSource(app), clock);
        var job = new TenantDeletionJob(fx.Factory.Services.GetRequiredService<ControlDataSource>(), runner,
            fx.Factory.Services.GetRequiredService<IObjectStore>(), new LifecycleOptions(), clock, NullLogger<TenantDeletionJob>.Instance);
        await job.RunOnceAsync(CancellationToken.None);
    }

    private async Task PurgeAsControlAsync(Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(fx.ControlConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT cp.purge_tenant(@t)", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        await cmd.ExecuteScalarAsync();
    }

    private async Task<List<(string Table, long Rows)>> LiveRowsAsync(Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        var tables = new List<string>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT c.table_name FROM information_schema.columns c JOIN information_schema.tables x
              ON x.table_schema = c.table_schema AND x.table_name = c.table_name AND x.table_type = 'BASE TABLE'
            WHERE c.table_schema = 'ti' AND c.column_name = 'tenant_id' ORDER BY 1
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                tables.Add(r.GetString(0));
        var result = new List<(string, long)>();
        foreach (var table in tables.Where(x => !Retained.Contains(x)))
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM ti.{table} WHERE tenant_id = @t", conn);
            count.Parameters.AddWithValue("t", tenantId);
            result.Add((table, (long)(await count.ExecuteScalarAsync())!));
        }
        Assert.True(result.Count > 40);
        return result;
    }

    private string TenantObjects(Guid tenantId) =>
        Path.Combine(fx.Factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Storage:Directory"]!, tenantId.ToString("D"));

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args) =>
        (T)(await TenantProvisioning.ScalarAsSuperuserAsync(fx, sql, args))!;
}

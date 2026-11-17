using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TrustedInventory.Application.Audit;
using TrustedInventory.Infrastructure;
using TrustedInventory.Infrastructure.Jobs;
using TrustedInventory.Infrastructure.Persistence;
using TrustedInventory.IntegrationTests.Medical;
using TrustedInventory.IntegrationTests.Support;
using TrustedInventory.Migrator;
using Xunit.Abstractions;

namespace TrustedInventory.IntegrationTests.Foundation;

/// <summary>
/// Backup restore drill (AT-OPS-01, NFR-RECOVERY, DEC-080) on throwaway data: a real pg_dump is restored into a new database, the
/// deletion register is replayed, and an API instance pointed at the restored database proves the audit chain, the stock and the
/// ledger reconcile. The drill measures the restore time (RTO) and shows what a backup-only restore loses (RPO = time since backup).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class RecoveryDrillTests(ApiFixture fx, ITestOutputHelper output)
{
    [Fact]
    [Trait("Requirement", "AT-OPS-01")]
    [Trait("Requirement", "NFR-RECOVERY")]
    [Trait("Requirement", "LFC-004")]
    public async Task A_restored_backup_verifies_its_audit_chain_reconciles_its_ledger_and_serves_the_same_stock()
    {
        var w = await MedicalWorld.CreateAsync(fx, "drill");
        var bucket = await w.BucketAsync(w.General, w.A01, 10);
        var order = await w.OrderAsync(w.General, "3");
        await (await w.AllocateAsync(order)).ExpectAsync(HttpStatusCode.OK);
        var auditor = $"user-auditor-{Guid.NewGuid():N}";
        await w.ExecAsync("""
            WITH m AS (INSERT INTO ti.membership (tenant_id, user_subject, created_by) VALUES (@t, @s, 'seed') RETURNING id, tenant_id, user_subject)
            INSERT INTO ti.role_grant (tenant_id, membership_id, grantee_subject, role, reason, granted_by, created_by)
            SELECT tenant_id, id, user_subject, 'Auditor', 'drill', 'seed', 'seed' FROM m
            """, ("t", w.Tenant), ("s", auditor));
        var before = await w.StateAsync(bucket);

        var backup = $"drill-{Guid.NewGuid():N}";
        var backupAt = DateTimeOffset.UtcNow;
        await fx.BackupAsync(backup);
        // Written after the backup: a backup-only restore loses it, which is why production needs point-in-time recovery (D06).
        var late = await (await w.Operator.PostJsonAsync("/api/v1/warehouses", new { code = "LATE", name = "After backup", timeZone = "UTC" }))
            .ExpectAsync(HttpStatusCode.Created);
        var lostWriteAt = DateTimeOffset.UtcNow;

        var clock = Stopwatch.StartNew();
        var database = $"drill_{Guid.NewGuid():N}"[..30];
        var app = await fx.RestoreAsync(backup, database, "ti_app", ApiFixture.AppRolePassword);
        string For(string user, string password) => new NpgsqlConnectionStringBuilder(app) { Username = user, Password = password }.ConnectionString;
        var control = For("ti_control", ApiFixture.ControlRolePassword);
        var platform = For("ti_platform", ApiFixture.PlatformRolePassword);
        // Before opening access: replay the live deletion register (docs/tenant-offboarding.md).
        Assert.True(await DeletionRegister.ReplayAsync(control, await DeletionRegister.ReadAsync(fx.ControlConnection), TextWriter.Null));
        var restoredAt = clock.Elapsed;

        // Ledger reconciliation on the restored data: no mismatch for this tenant.
        await using (var appSource = DependencyInjection.BuildDataSource(app))
        await using (var platformSource = DependencyInjection.BuildDataSource(platform))
        {
            var runner = new TenantJobRunner(new PlatformDataSource(platformSource), new AppDataSource(appSource), TimeProvider.System);
            await new LedgerReconciliationJob(runner, fx.Factory.Services.GetRequiredService<IAuditSupport>(), NullLogger<LedgerReconciliationJob>.Instance)
                .RunOnceAsync(CancellationToken.None);
        }
        await using (var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fx.SuperuserConnection) { Database = database }.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM ti.alert WHERE tenant_id = @t AND type = 'ReconciliationMismatch' AND status <> 'Closed'", conn);
            cmd.Parameters.AddWithValue("t", w.Tenant);
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
        }

        // An API instance on the restored database: the audit chain verifies and the stock is what it was at the backup.
        await using var restoredHost = fx.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:App", app);
            b.UseSetting("ConnectionStrings:Platform", platform);
            b.UseSetting("ConnectionStrings:Control", control);
        });
        HttpClient Client(string subject)
        {
            var c = restoredHost.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue(subject));
            c.DefaultRequestHeaders.Add("X-Tenant-Id", w.Tenant.ToString());
            return c;
        }
        var verified = await (await Client(auditor).PostJsonAsync("/api/v1/audit/verify", new { })).ExpectAsync(HttpStatusCode.OK);
        Assert.True((bool)verified["verified"]!, verified.ToJsonString());
        Assert.True((long)verified["eventsChecked"]! > 0);
        var stock = await (await Client(w.OperatorSubject).GetAsync($"/api/v1/stock/{bucket}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(before.Split('|')[0], stock["onHand"]!.ToString());
        Assert.Equal(before.Split('|')[1], stock["reserved"]!.ToString());
        await (await Client(w.OperatorSubject).GetAsync($"/api/v1/warehouses/{late["id"]}")).ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        clock.Stop();

        var rpo = lostWriteAt - backupAt;
        output.WriteLine($"AT-OPS-01 drill: restore and register replay {restoredAt.TotalSeconds:0.0} s; restore to verified service {clock.Elapsed.TotalSeconds:0.0} s;"
            + $" writes lost with a backup-only restore: those after the backup ({rpo.TotalSeconds:0.0} s window in this drill).");
        Assert.True(clock.Elapsed < TimeSpan.FromHours(4), "RTO budget is 4 hours (NFR-RECOVERY).");
    }
}

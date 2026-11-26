using System.Net;
using Npgsql;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Saas;

/// <summary>Concurrent reservations of the last unit of a quota (AT-ENT-01, AT-ENT-03, ENT-003, DEC-038, DEC-066).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class QuotaRaceTests(ApiFixture fx)
{
    private const int Racers = 8;

    [Fact]
    [Trait("Requirement", "AT-ENT-01")]
    [Trait("Requirement", "ENT-003")]
    public async Task Concurrent_requests_for_the_final_warehouse_slot_create_exactly_one()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "race-wh");
        await MoveToPlanAsync(tenant.TenantId, limitEach: 1);
        var responses = await Task.WhenAll(Enumerable.Range(0, Racers).Select(i =>
            fx.Client(tenant.Admin, tenant.TenantId).PostJsonAsync("/api/v1/warehouses", new { code = $"RACE-{i}", name = $"Race {i}", timeZone = "UTC" })));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
            await refused.ExpectProblemAsync(HttpStatusCode.Conflict, "PLAN_LIMIT_REACHED");
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM ti.warehouse WHERE tenant_id = @t AND active", tenant.TenantId));
        Assert.Equal(1L, await ScalarAsync("SELECT used FROM ti.usage_counter WHERE tenant_id = @t AND metric = 'active_warehouses'", tenant.TenantId));
    }

    [Fact]
    [Trait("Requirement", "AT-ENT-03")]
    [Trait("Requirement", "ENT-003")]
    public async Task Concurrent_uploads_cannot_exceed_the_attachment_byte_limit()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "race-att");
        await MoveToPlanAsync(tenant.TenantId, limitEach: 400);
        await TenantProvisioning.GrantSelfAsync(fx, tenant, "WarehouseManager");
        var pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.7\n" + new string('x', 141)); // 150 bytes: two fit in 400

        var responses = await Task.WhenAll(Enumerable.Range(0, Racers).Select(_ =>
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(pdf);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            form.Add(file, "file", "evidence.pdf");
            return fx.Client(tenant.Admin, tenant.TenantId).PostAsync("/api/v1/attachments", form);
        }));

        Assert.Equal(2, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
            await refused.ExpectProblemAsync(HttpStatusCode.Conflict, "PLAN_LIMIT_REACHED");
        Assert.Equal(300L, await ScalarAsync("SELECT used FROM ti.usage_counter WHERE tenant_id = @t AND metric = 'attachment_bytes'", tenant.TenantId));
        Assert.Equal(2L, await ScalarAsync("SELECT count(*) FROM ti.attachment WHERE tenant_id = @t", tenant.TenantId));
    }

    private async Task MoveToPlanAsync(Guid tenantId, long limitEach)
    {
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        var plan = await SaasSeed.PlanAsync(conn, $"race-{Guid.NewGuid():N}"[..20], ["core_inventory"], limitEach);
        await TenantProvisioning.ExecAsSuperuserAsync(fx, "UPDATE cp.subscription SET plan_version_id = @p WHERE tenant_id = @t", ("p", plan), ("t", tenantId));
        await TenantProvisioning.ExecAsSuperuserAsync(fx, """
            UPDATE cp.entitlement_snapshot e SET plan_version_id = p.id, features = p.features, limits = p.quotas
            FROM cp.plan_version p WHERE p.id = @p AND e.tenant_id = @t AND e.is_current
            """, ("p", plan), ("t", tenantId));
    }

    private async Task<long> ScalarAsync(string sql, Guid tenantId) =>
        Convert.ToInt64(await TenantProvisioning.ScalarAsSuperuserAsync(fx, sql, ("t", tenantId)), System.Globalization.CultureInfo.InvariantCulture);
}

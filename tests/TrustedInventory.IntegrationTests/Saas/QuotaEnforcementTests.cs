using System.Net;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TrustedInventory.Api.Security;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Saas;

/// <summary>
/// Every implemented operation that declares x-quota reserves it (SAS-005: no endpoint bypasses a gate; DEC-045).
/// At the limit only increases are refused (ENT-004); retiring frees the slot.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class QuotaEnforcementTests(ApiFixture fx)
{
    /// <summary>
    /// Implemented x-quota operations and the tests that prove each one reserves its quota. A new x-quota endpoint
    /// fails <see cref="Every_implemented_x_quota_operation_has_enforcement_evidence"/> until it is added here with a test.
    /// </summary>
    private static readonly Dictionary<string, string> Evidence = new()
    {
        ["createMembership"] = "PipelineOrderTests.Quota_is_reserved_before_domain_validation_and_rolled_back_with_it; TeamTests.Memberships_use_seats_and_deactivation_frees_one",
        ["acceptInvitation"] = "TeamTests.Concurrent_acceptance_of_the_final_seat_admits_exactly_one",
        ["createWarehouse"] = nameof(Warehouse_quota_blocks_only_new_warehouses_and_retiring_frees_a_slot),
        ["createProduct"] = nameof(Sku_quota_blocks_only_new_products_and_retiring_frees_a_slot),
        ["uploadAttachment"] = nameof(Attachment_bytes_are_counted_and_an_upload_over_the_limit_is_refused),
    };

    [Fact]
    [Trait("Requirement", "SAS-005")]
    [Trait("Requirement", "ENT-003")]
    public void Every_implemented_x_quota_operation_has_enforcement_evidence()
    {
        var implemented = fx.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(e => e.Metadata.GetMetadata<QuotaMetadata>() is not null)
            .Select(e => e.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName)
            .Order().ToList();
        Assert.Equal(Evidence.Keys.Order(), implemented);
    }

    [Fact]
    [Trait("Requirement", "SAS-005")]
    [Trait("Requirement", "ENT-003")]
    [Trait("Requirement", "ENT-004")]
    public async Task Warehouse_quota_blocks_only_new_warehouses_and_retiring_frees_a_slot()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "quota-wh");
        await MoveToPlanAsync(tenant.TenantId, limitEach: 2);
        var admin = fx.Client(tenant.Admin, tenant.TenantId);

        var first = await CreateWarehouseAsync(admin, "Q-WH-1");
        // Below the limit a domain rejection rolls its reservation back.
        await (await admin.PostJsonAsync("/api/v1/warehouses", Warehouse("Q-WH-1")))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.Equal(1L, await UsedAsync(tenant.TenantId, "active_warehouses"));
        var second = await CreateWarehouseAsync(admin, "Q-WH-2");

        var limited = await (await admin.PostJsonAsync("/api/v1/warehouses", Warehouse("Q-WH-3")))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "PLAN_LIMIT_REACHED");
        Assert.Equal("active_warehouses", (string?)limited["quota"]!["quota"]);
        Assert.Equal(2L, (long)limited["quota"]!["used"]!);
        Assert.Equal(2L, (long)limited["quota"]!["limit"]!);
        // At the limit the quota answers before domain validation (SAS-005), and edits stay allowed (ENT-004).
        await (await admin.PostJsonAsync("/api/v1/warehouses", Warehouse("Q-WH-1")))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "PLAN_LIMIT_REACHED");
        await (await admin.PatchJsonAsync($"/api/v1/warehouses/{first}", new { name = "Renamed at limit", reason = "quota test" }, "\"1\""))
            .ExpectAsync(HttpStatusCode.OK);

        await (await admin.PostEmptyAsync($"/api/v1/warehouses/{second}/retire", "\"1\"")).ExpectAsync(HttpStatusCode.NoContent);
        Assert.Equal(1L, await UsedAsync(tenant.TenantId, "active_warehouses"));
        await CreateWarehouseAsync(admin, "Q-WH-3");
        Assert.Equal(2L, await UsedAsync(tenant.TenantId, "active_warehouses"));
    }

    [Fact]
    [Trait("Requirement", "SAS-005")]
    [Trait("Requirement", "ENT-003")]
    [Trait("Requirement", "ENT-004")]
    public async Task Sku_quota_blocks_only_new_products_and_retiring_frees_a_slot()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "quota-sku");
        await MoveToPlanAsync(tenant.TenantId, limitEach: 2);
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        var unit = (string)(await (await admin.PostJsonAsync("/api/v1/units", new { code = "EA", name = "Each" }))
            .ExpectAsync(HttpStatusCode.Created))["id"]!;

        var first = await CreateProductAsync(admin, unit, "Q-SKU-1");
        await CreateProductAsync(admin, unit, "Q-SKU-2");

        var limited = await (await admin.PostJsonAsync("/api/v1/products", Product(unit, "Q-SKU-3")))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "PLAN_LIMIT_REACHED");
        Assert.Equal("active_skus", (string?)limited["quota"]!["quota"]);
        await (await admin.PatchJsonAsync($"/api/v1/products/{first}", new { name = "Renamed at limit", reason = "quota test" }, "\"1\""))
            .ExpectAsync(HttpStatusCode.OK);

        await (await admin.PostEmptyAsync($"/api/v1/products/{first}/retire", "\"2\"")).ExpectAsync(HttpStatusCode.NoContent);
        Assert.Equal(1L, await UsedAsync(tenant.TenantId, "active_skus"));
        await CreateProductAsync(admin, unit, "Q-SKU-3");
        Assert.Equal(2L, await UsedAsync(tenant.TenantId, "active_skus"));
    }

    [Fact]
    [Trait("Requirement", "SAS-005")]
    [Trait("Requirement", "ENT-003")]
    [Trait("Requirement", "TEN-005")]
    public async Task Attachment_bytes_are_counted_and_an_upload_over_the_limit_is_refused()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "quota-att");
        await MoveToPlanAsync(tenant.TenantId, limitEach: 200);
        await TenantProvisioning.GrantSelfAsync(fx, tenant, "WarehouseManager");
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        var pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.7\n" + new string('x', 141)); // 150 bytes

        await (await UploadAsync(admin, pdf)).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(150L, await UsedAsync(tenant.TenantId, "attachment_bytes"));
        var limited = await (await UploadAsync(admin, pdf)).ExpectProblemAsync(HttpStatusCode.Conflict, "PLAN_LIMIT_REACHED");
        Assert.Equal("attachment_bytes", (string?)limited["quota"]!["quota"]);
        Assert.Equal(150L, await UsedAsync(tenant.TenantId, "attachment_bytes"));
        Assert.Equal(1L, (long)(await TenantProvisioning.ScalarAsSuperuserAsync(fx, "SELECT count(*) FROM ti.attachment WHERE tenant_id = @t", ("t", tenant.TenantId)))!);
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] content)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "evidence.pdf");
        return client.PostAsync("/api/v1/attachments", form);
    }

    private static object Warehouse(string code) => new { code, name = code, timeZone = "UTC" };

    private static object Product(string unit, string sku) =>
        new { sku, name = sku, handlingProfile = "General", trackingMode = "Quantity", baseUomId = unit, basePrecision = 0 };

    private static async Task<string> CreateWarehouseAsync(HttpClient client, string code) =>
        (string)(await (await client.PostJsonAsync("/api/v1/warehouses", Warehouse(code))).ExpectAsync(HttpStatusCode.Created))["id"]!;

    private static async Task<string> CreateProductAsync(HttpClient client, string unit, string sku) =>
        (string)(await (await client.PostJsonAsync("/api/v1/products", Product(unit, sku))).ExpectAsync(HttpStatusCode.Created))["id"]!;

    private async Task MoveToPlanAsync(Guid tenantId, long limitEach)
    {
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        var plan = await SaasSeed.PlanAsync(conn, $"quota-{Guid.NewGuid():N}"[..20], ["core_inventory"], limitEach);
        await TenantProvisioning.ExecAsSuperuserAsync(fx, "UPDATE cp.subscription SET plan_version_id = @p WHERE tenant_id = @t",
            ("p", plan), ("t", tenantId));
        await TenantProvisioning.ExecAsSuperuserAsync(fx, """
            UPDATE cp.entitlement_snapshot e SET plan_version_id = p.id, features = p.features, limits = p.quotas
            FROM cp.plan_version p WHERE p.id = @p AND e.tenant_id = @t AND e.is_current
            """, ("p", plan), ("t", tenantId));
    }

    private async Task<long> UsedAsync(Guid tenantId, string metric) =>
        (long)(await TenantProvisioning.ScalarAsSuperuserAsync(fx,
            "SELECT used FROM ti.usage_counter WHERE tenant_id = @t AND metric = @m::ti.quota_key", ("t", tenantId), ("m", metric)))!;
}

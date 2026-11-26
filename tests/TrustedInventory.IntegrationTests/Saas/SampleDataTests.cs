using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Saas;

/// <summary>Opt-in, labelled sample data that never holds stock and never completes a setup step (ONB-004, DEC-098).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class SampleDataTests(ApiFixture fx)
{
    private const string Root = "/api/v1/saas/sample-data";

    [Fact]
    [Trait("Requirement", "ONB-004")]
    [Trait("Requirement", "ENT-003")]
    public async Task Sample_data_is_opt_in_labelled_never_holds_stock_and_is_removed_with_its_quota()
    {
        var tenant = await TenantProvisioning.SignUpAsync(fx, "sample");
        var owner = fx.Client(tenant.Admin, tenant.TenantId);
        await TenantProvisioning.GrantSelfAsync(fx, tenant, "WarehouseManager");
        await TenantProvisioning.GrantSelfAsync(fx, tenant, "Receiver");

        // Nothing until asked for; a member without tenant.config.manage cannot add it.
        await (await owner.GetAsync(Root)).ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        var member = $"user-sample-member-{Guid.NewGuid():N}";
        await TenantProvisioning.ExecAsSuperuserAsync(fx, "INSERT INTO ti.membership (tenant_id, user_subject, created_by) VALUES (@t, @s, 'seed')",
            ("t", tenant.TenantId), ("s", member));
        await (await fx.Client(member, tenant.TenantId).PostJsonAsync(Root, new { timeZone = "Africa/Lagos" }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await (await owner.PostJsonAsync(Root, new { timeZone = "Mars/Olympus" })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");

        var set = await (await owner.PostJsonAsync(Root, new { timeZone = "Africa/Lagos" })).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("Active", (string?)set["status"]);
        var items = set["items"]!.AsArray().Select(i => i!.AsObject()).ToList();
        Assert.Equal(["Warehouse", "Zone", "Bin", "Bin", "Bin", "Unit", "Product", "Product", "Product", "Partner", "Partner"],
            items.Select(i => (string)i["objectType"]!));
        Assert.All(items, i => Assert.StartsWith("SAMPLE-", (string)i["code"]!, StringComparison.Ordinal));
        Assert.All(items.Where(i => (string)i["objectType"]! != "Bin"), i => Assert.EndsWith("(sample)", (string)i["name"]!, StringComparison.Ordinal));
        await (await owner.PostJsonAsync(Root, new { timeZone = "Africa/Lagos" })).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        Assert.Equal((string?)set["id"], (string?)(await (await owner.GetAsync(Root)).ExpectAsync(HttpStatusCode.OK))["id"]);

        // It counts against the plan like any record, and it never completes a setup step.
        Assert.Equal(1L, await UsageAsync(tenant.TenantId, "active_warehouses"));
        Assert.Equal(3L, await UsageAsync(tenant.TenantId, "active_skus"));
        var checklist = await (await owner.GetAsync("/api/v1/saas/setup-checklist")).ExpectAsync(HttpStatusCode.OK);
        Assert.All(checklist["items"]!.AsArray().Where(i => (string)i!["key"]! is "warehouse-timezone" or "zones-bins" or "products-units"),
            i => Assert.Equal("Pending", (string?)i!["status"]));

        // Isolated from production quantities: receiving a sample product into the sample warehouse is refused and nothing posts.
        string Id(string type, string codePart) => (string)items.First(i => (string)i["objectType"]! == type && ((string)i["code"]!).Contains(codePart, StringComparison.Ordinal))["objectId"]!;
        var warehouse = Id("Warehouse", "SAMPLE-");
        var product = Id("Product", "P001");
        var unit = Id("Unit", "EA");
        var order = await (await owner.PostJsonAsync("/api/v1/purchase-orders", new
        {
            supplierId = Id("Partner", "SUP"), warehouseId = warehouse, reference = "PO-SAMPLE", lines = new[] { new { productId = product, quantity = "5", uomId = unit } },
        })).ExpectAsync(HttpStatusCode.Created);
        await (await owner.PostEmptyAsync($"/api/v1/purchase-orders/{order["id"]}/approve", "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        var receive = new HttpRequestMessage(HttpMethod.Post, "/api/v1/inventory/receive")
        {
            Content = JsonContent.Create(new
            {
                warehouseId = warehouse, purchaseOrderId = (string)order["id"]!,
                items = new[] { new { purchaseOrderLineId = (string)order["lines"]![0]!["id"]!, productId = product, uomId = unit, quantity = "5", targetBinId = Id("Bin", "-IN-") } },
            }),
        };
        receive.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var refused = await (await owner.SendAsync(receive)).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        Assert.Contains("Sample data never holds stock", (string?)refused["detail"], StringComparison.Ordinal);
        Assert.Equal(0L, Convert.ToInt64(await TenantProvisioning.ScalarAsSuperuserAsync(fx,
            "SELECT count(*) FROM ti.stock_bucket WHERE tenant_id = @t", ("t", tenant.TenantId)), System.Globalization.CultureInfo.InvariantCulture));

        // Removal retires every record and frees the quota; a stale version is refused; the set is then Removed and can be added again.
        var url = $"{Root}/{set["id"]}/remove";
        await (await owner.PostEmptyAsync(url, "\"9\"")).ExpectProblemAsync(HttpStatusCode.PreconditionFailed, "VERSION_CONFLICT");
        var removed = await (await owner.PostEmptyAsync(url, "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Removed", (string?)removed["status"]);
        Assert.Equal(tenant.Admin, (string?)removed["removedBy"]);
        Assert.Equal(0L, await UsageAsync(tenant.TenantId, "active_warehouses"));
        Assert.Equal(0L, await UsageAsync(tenant.TenantId, "active_skus"));
        Assert.Equal(0L, Convert.ToInt64(await TenantProvisioning.ScalarAsSuperuserAsync(fx,
            "SELECT count(*) FROM ti.product WHERE tenant_id = @t AND active", ("t", tenant.TenantId)), System.Globalization.CultureInfo.InvariantCulture));
        await (await owner.PostEmptyAsync(url, "\"2\"")).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        Assert.Equal(2L, Convert.ToInt64(await TenantProvisioning.ScalarAsSuperuserAsync(fx,
            "SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type IN ('SampleDataAdded', 'SampleDataRemoved')", ("t", tenant.TenantId)),
            System.Globalization.CultureInfo.InvariantCulture));
        var again = await (await owner.PostJsonAsync(Root, new { timeZone = "UTC" })).ExpectAsync(HttpStatusCode.Created);
        Assert.NotEqual((string?)set["id"], (string?)again["id"]);
    }

    private async Task<long> UsageAsync(Guid tenant, string quota)
    {
        var entitlements = await (await fx.Client((string)(await TenantProvisioning.ScalarAsSuperuserAsync(fx,
            "SELECT user_subject FROM ti.membership WHERE tenant_id = @t ORDER BY created_at LIMIT 1", ("t", tenant)))!, tenant)
            .GetAsync("/api/v1/saas/entitlements")).ExpectAsync(HttpStatusCode.OK);
        return (long?)entitlements["usage"]?[quota] ?? 0;
    }
}

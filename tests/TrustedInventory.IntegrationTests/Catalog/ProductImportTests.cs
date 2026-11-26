using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Catalog;

/// <summary>The optional product import of the setup checklist: all or nothing, every problem reported, idempotent (ONB-004, DEC-099).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ProductImportTests(ApiFixture fx)
{
    private const string Root = "/api/v1/imports/products";

    [Fact]
    [Trait("Requirement", "ONB-004")]
    [Trait("Requirement", "STK-007")]
    [Trait("Requirement", "AUD-001")]
    public async Task A_file_imports_all_or_nothing_with_every_problem_named_and_a_retry_imports_it_once()
    {
        var tenant = await TenantProvisioning.SignUpAsync(fx, "import");
        var owner = fx.Client(tenant.Admin, tenant.TenantId);
        await (await owner.PostJsonAsync("/api/v1/units", new { code = "EA", name = "Each" })).ExpectAsync(HttpStatusCode.Created);
        await (await owner.PostJsonAsync("/api/v1/products", new
        {
            sku = "EXISTING", name = "Already there", handlingProfile = "General", trackingMode = "Quantity",
            baseUomId = (string)(await (await owner.GetAsync("/api/v1/units")).ExpectAsync(HttpStatusCode.OK))["items"]![0]!["id"]!, basePrecision = 0,
        })).ExpectAsync(HttpStatusCode.Created);

        // Every problem is reported by row and column, and nothing is created.
        var bad = "sku,name,unit,tracking,precision\r\nA-1,Valid row,EA,Quantity,0\r\nEXISTING,Duplicate of a product,EA,Quantity,\r\nA-1,Repeated in the file,EA,Lot,0\r\nB-1,,BOX,Weight,9\r\n";
        var refused = await (await Import(owner, Guid.NewGuid(), bad)).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.Equal(["rows[2].sku", "rows[3].sku", "rows[4].name", "rows[4].unit", "rows[4].tracking", "rows[4].precision"],
            refused["errors"]!.AsArray().Select(e => (string)e!["field"]!));
        Assert.Equal(1L, await CountAsync(tenant.TenantId, "SELECT count(*) FROM ti.product WHERE tenant_id = @t"));
        await (await Import(owner, Guid.NewGuid(), "sku,name\r\nX,Y\r\n")).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        await (await Import(owner, Guid.NewGuid(), "sku,name,unit,tracking\r\n\"Q-1,broken")).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");

        // A valid file, with a quoted name holding a comma and a doubled quote, columns in any order, LF line ends.
        var good = "tracking,unit,name,sku\nQuantity,EA,\"Tape, 48 mm \"\"clear\"\"\",IMP-1\nLot,EA,Gloves,IMP-2\n";
        var key = Guid.NewGuid();
        var created = await (await Import(owner, key, good)).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(2, (int)created["rowCount"]!);
        Assert.Matches("^[0-9a-f]{64}$", (string)created["digest"]!);
        var replay = await (await Import(owner, key, good)).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal((string?)created["id"], (string?)replay["id"]);
        Assert.Equal("Tape, 48 mm \"clear\"", await ScalarAsync<string>(tenant.TenantId, "SELECT name FROM ti.product WHERE tenant_id = @t AND sku = 'IMP-1'"));
        Assert.Equal("Lot", await ScalarAsync<string>(tenant.TenantId, "SELECT tracking_mode::text FROM ti.product WHERE tenant_id = @t AND sku = 'IMP-2'"));
        Assert.Equal(3L, await CountAsync(tenant.TenantId, "SELECT count(*) FROM ti.product WHERE tenant_id = @t"));
        Assert.Equal(0L, await CountAsync(tenant.TenantId, "SELECT count(*) FROM ti.stock_bucket WHERE tenant_id = @t"));
        Assert.Equal(1L, await CountAsync(tenant.TenantId, "SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'ProductsImported'"));
        Assert.Equal(3L, await CountAsync(tenant.TenantId, "SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'ProductCreated'"));

        // The import is listed and completes the optional checklist step; a member without catalog.manage cannot import.
        var list = await (await owner.GetAsync(Root)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal((string?)created["id"], (string?)Assert.Single(list["items"]!.AsArray())!["id"]);
        var checklist = await (await owner.GetAsync("/api/v1/saas/setup-checklist")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Done", (string?)checklist["items"]!.AsArray().Single(i => (string)i!["key"]! == "import")!["status"]);
        var member = $"user-import-member-{Guid.NewGuid():N}";
        await TenantProvisioning.ExecAsSuperuserAsync(fx, "INSERT INTO ti.membership (tenant_id, user_subject, created_by) VALUES (@t, @s, 'seed')",
            ("t", tenant.TenantId), ("s", member));
        await (await Import(fx.Client(member, tenant.TenantId), Guid.NewGuid(), good)).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    private static Task<HttpResponseMessage> Import(HttpClient client, Guid key, string csv)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Root) { Content = JsonContent.Create(new { csv }) };
        request.Headers.Add("Idempotency-Key", key.ToString());
        return client.SendAsync(request);
    }

    private async Task<long> CountAsync(Guid tenant, string sql) =>
        Convert.ToInt64(await TenantProvisioning.ScalarAsSuperuserAsync(fx, sql, ("t", tenant)), CultureInfo.InvariantCulture);

    private async Task<T> ScalarAsync<T>(Guid tenant, string sql) => (T)(await TenantProvisioning.ScalarAsSuperuserAsync(fx, sql, ("t", tenant)))!;
}

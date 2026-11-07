using System.Net;
using System.Text.Json.Nodes;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Catalog;

[Collection(ApiTestGroup.Name)]
public sealed class CatalogTests(ApiFixture fx)
{
    private HttpClient AdminA => fx.Client(fx.Seed.A.Admin, fx.Seed.A.TenantId);
    private HttpClient AdminB => fx.Client(fx.Seed.B.Admin, fx.Seed.B.TenantId);

    private static string Code(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static async Task<string> CreateUnitAsync(HttpClient client) =>
        (string)(await (await client.PostJsonAsync("/api/v1/units", new { code = Code("U"), name = "Each" }))
            .ExpectAsync(HttpStatusCode.Created))["id"]!;

    private static async Task<JsonObject> CreateProfileAsync(HttpClient client) =>
        await (await client.PostJsonAsync("/api/v1/storage-profiles", new
        {
            name = Code("Cold"), minC = 2, maxC = 8, monitoringRequired = true,
            excursionPolicy = new { immediateHold = true, notes = "QA reviewed" }, reason = "new cold chain profile",
        })).ExpectAsync(HttpStatusCode.Created);

    private static async Task<JsonObject> CreateProductAsync(HttpClient client, string unitId, object? overrides = null)
    {
        var body = new JsonObject
        {
            ["sku"] = Code("SKU"), ["name"] = "Widget", ["handlingProfile"] = "General", ["trackingMode"] = "Quantity",
            ["baseUomId"] = unitId, ["basePrecision"] = 0,
        };
        if (overrides is not null)
            foreach (var (k, v) in System.Text.Json.JsonSerializer.SerializeToNode(overrides)!.AsObject())
                body[k] = v?.DeepClone();
        return await (await client.PostJsonAsync("/api/v1/products", body.ToJsonString())).ExpectAsync(HttpStatusCode.Created);
    }

    private static HashSet<string?> Fields(JsonObject problem) =>
        problem["errors"]!.AsArray().Select(e => (string?)e!["field"]).ToHashSet();

    [Fact]
    [Trait("Requirement", "DATA-07")]
    [Trait("Requirement", "API-002")]
    [Trait("Requirement", "API-003")]
    public async Task Unit_lifecycle_uses_etags_and_preconditions()
    {
        var admin = AdminA;
        var created = await admin.PostJsonAsync("/api/v1/units", new { code = Code("BOX"), name = "Box" });
        var unit = await created.ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("\"1\"", created.ETag());
        Assert.Equal($"/api/v1/units/{unit["id"]}", created.Headers.Location?.ToString());

        await (await admin.PostJsonAsync("/api/v1/units", new { code = (string)unit["code"]!, name = "Dup" }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        await (await admin.PatchJsonAsync($"/api/v1/units/{unit["id"]}", new { name = "Carton" }, null))
            .ExpectProblemAsync((HttpStatusCode)428, "PRECONDITION_REQUIRED");
        var renamed = await admin.PatchJsonAsync($"/api/v1/units/{unit["id"]}", new { name = "Carton" }, "\"1\"");
        Assert.Equal("Carton", (string?)(await renamed.ExpectAsync(HttpStatusCode.OK))["name"]);
        await (await admin.PostEmptyAsync($"/api/v1/units/{unit["id"]}/retire", "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.PreconditionFailed, "VERSION_CONFLICT");
        var retired = await admin.PostEmptyAsync($"/api/v1/units/{unit["id"]}/retire", "\"2\"");
        await retired.ExpectAsync(HttpStatusCode.NoContent);
        Assert.Equal("\"3\"", retired.ETag());
    }

    [Fact]
    [Trait("Requirement", "CFG-002")]
    [Trait("Requirement", "DEC-027")]
    public async Task Storage_profiles_carry_limits_and_an_immediate_hold_policy()
    {
        var admin = AdminA;
        var profile = await CreateProfileAsync(admin);
        Assert.True((bool)profile["excursionPolicy"]!["immediateHold"]!);
        Assert.Equal("QA reviewed", (string?)profile["excursionPolicy"]!["notes"]);
        Assert.Equal(300, (int)profile["staleAfterSeconds"]!);
        Assert.Null(profile["permittedHazardClasses"]);

        var invalid = await admin.PostJsonAsync("/api/v1/storage-profiles", new
        {
            name = Code("Bad"), minC = 8, maxC = 2, monitoringRequired = true, staleAfterSeconds = 5,
            excursionPolicy = new { immediateHold = false }, reason = "x",
        });
        var fields = Fields(await invalid.ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"));
        Assert.Superset(new HashSet<string?> { "maxC", "staleAfterSeconds", "excursionPolicy.immediateHold" }, fields);

        var updated = await admin.PatchJsonAsync($"/api/v1/storage-profiles/{profile["id"]}",
            new { maxC = 6, excursionPolicy = new { immediateHold = true, notes = "tightened" }, reason = "QA change" }, "\"1\"");
        var after = await updated.ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(6m, (decimal)after["maxC"]!);
        Assert.Equal("tightened", (string?)after["excursionPolicy"]!["notes"]);
        await (await admin.PatchJsonAsync($"/api/v1/storage-profiles/{profile["id"]}", new { minC = 7, reason = "invert" }, "\"2\""))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");

        // Reading is catalog.read; managing profiles needs tenant.config.manage.
        var picker = fx.Client(fx.Seed.A.Picker, fx.Seed.A.TenantId);
        await (await picker.GetAsync($"/api/v1/storage-profiles/{profile["id"]}")).ExpectAsync(HttpStatusCode.OK);
        await (await picker.PostJsonAsync("/api/v1/storage-profiles", new
        {
            name = Code("P"), minC = 2, maxC = 8, monitoringRequired = true, excursionPolicy = new { immediateHold = true }, reason = "x",
        })).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    [Trait("Requirement", "CFG-002")]
    public async Task Products_reference_only_active_storage_profiles_and_block_profile_retirement()
    {
        var admin = AdminA;
        var unit = await CreateUnitAsync(admin);
        var profile = await CreateProfileAsync(admin);
        var product = await CreateProductAsync(admin, unit, new { storageProfileId = (string)profile["id"]! });
        Assert.Equal((string?)profile["id"], (string?)product["storageProfileId"]);

        await (await admin.PostEmptyAsync($"/api/v1/storage-profiles/{profile["id"]}/retire", "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");

        var spare = await CreateProfileAsync(admin);
        await (await admin.PostEmptyAsync($"/api/v1/storage-profiles/{spare["id"]}/retire", "\"1\"")).ExpectAsync(HttpStatusCode.NoContent);
        var rejected = await admin.PostJsonAsync("/api/v1/products", new
        {
            sku = Code("SKU"), name = "Cold item", handlingProfile = "General", trackingMode = "Quantity",
            baseUomId = unit, basePrecision = 0, storageProfileId = (string)spare["id"]!,
        });
        Assert.Contains("storageProfileId", Fields(await rejected.ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED")));
    }

    [Fact]
    [Trait("Requirement", "DATA-07")]
    public async Task Medical_and_serial_product_rules_are_enforced()
    {
        var admin = AdminA;
        var unit = await CreateUnitAsync(admin);

        var medical = await CreateProductAsync(admin, unit, new { handlingProfile = "Medical", trackingMode = "Lot" });
        Assert.Equal("FEFO", (string?)medical["rotationRule"]);
        var general = await CreateProductAsync(admin, unit);
        Assert.Equal("FIFO", (string?)general["rotationRule"]);

        async Task<HashSet<string?>> Rejected(object body) =>
            Fields(await (await admin.PostJsonAsync("/api/v1/products", body)).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"));

        Assert.Contains("trackingMode", await Rejected(new
        {
            sku = Code("SKU"), name = "Bad", handlingProfile = "Medical", trackingMode = "Quantity", baseUomId = unit, basePrecision = 0,
        }));
        Assert.Contains("rotationRule", await Rejected(new
        {
            sku = Code("SKU"), name = "Bad", handlingProfile = "Medical", trackingMode = "Lot", baseUomId = unit, basePrecision = 0,
            rotationRule = "FIFO",
        }));
        Assert.Contains("basePrecision", await Rejected(new
        {
            sku = Code("SKU"), name = "Bad", handlingProfile = "General", trackingMode = "Serial", baseUomId = unit, basePrecision = 2,
        }));
        Assert.Contains("baseUomId", await Rejected(new
        {
            sku = Code("SKU"), name = "Bad", handlingProfile = "General", trackingMode = "Quantity", baseUomId = Guid.NewGuid(), basePrecision = 0,
        }));
        Assert.Contains("sku", await Rejected(new
        {
            sku = (string)general["sku"]!, name = "Dup", handlingProfile = "General", trackingMode = "Quantity", baseUomId = unit, basePrecision = 0,
        }));

        // Medical products stay FEFO on update.
        await (await admin.PatchJsonAsync($"/api/v1/products/{medical["id"]}", new { rotationRule = "LIFO", reason = "try" }, "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        var updated = await admin.PatchJsonAsync($"/api/v1/products/{general["id"]}", new { rotationRule = "LIFO", hazardClass = "UN1993", reason = "LIFO for bulk" }, "\"1\"");
        var body = await updated.ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("LIFO", (string?)body["rotationRule"]);
        Assert.Equal("UN1993", (string?)body["hazardClass"]);
        var cleared = await admin.PatchJsonAsync($"/api/v1/products/{general["id"]}", new JsonObject { ["hazardClass"] = null, ["reason"] = "clear" }.ToJsonString(), "\"2\"");
        Assert.Null((await cleared.ExpectAsync(HttpStatusCode.OK))["hazardClass"]);
    }

    [Fact]
    [Trait("Requirement", "INB-002")]
    public async Task Barcodes_are_normalised_to_gtin14_validated_and_unique_while_active()
    {
        var admin = AdminA;
        var unit = await CreateUnitAsync(admin);
        var first = await CreateProductAsync(admin, unit);
        var second = await CreateProductAsync(admin, unit);
        // Unique synthetic GTIN-13 with a correct check digit; stored with a leading zero.
        var gtin13 = WithCheckDigit("20" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture));

        var created = await admin.PostJsonAsync($"/api/v1/products/{first["id"]}/barcodes", new { gtin = gtin13 });
        var barcode = await created.ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("0" + gtin13, (string?)barcode["gtin"]);

        var badCheck = gtin13[..12] + ((gtin13[12] - '0' + 1) % 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains("gtin", Fields(await (await admin.PostJsonAsync($"/api/v1/products/{second["id"]}/barcodes", new { gtin = badCheck }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED")));
        Assert.Contains("gtin", Fields(await (await admin.PostJsonAsync($"/api/v1/products/{second["id"]}/barcodes", new { gtin = "12345" }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED")));
        // Same GTIN in its 14-digit form still collides with the active mapping.
        Assert.Contains("gtin", Fields(await (await admin.PostJsonAsync($"/api/v1/products/{second["id"]}/barcodes", new { gtin = "0" + gtin13 }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED")));

        await (await admin.PostEmptyAsync($"/api/v1/products/{first["id"]}/barcodes/{barcode["id"]}/retire", "\"1\""))
            .ExpectAsync(HttpStatusCode.NoContent);
        await (await admin.PostJsonAsync($"/api/v1/products/{second["id"]}/barcodes", new { gtin = gtin13 })).ExpectAsync(HttpStatusCode.Created);

        var list = await (await admin.GetAsync($"/api/v1/products/{first["id"]}/barcodes")).ExpectAsync(HttpStatusCode.OK);
        Assert.False((bool)Assert.Single(list["items"]!.AsArray())!["active"]!);
    }

    [Fact]
    [Trait("Requirement", "DATA-07")]
    public async Task Conversions_respect_base_unit_and_precision()
    {
        var admin = AdminA;
        var each = await CreateUnitAsync(admin);
        var box = await CreateUnitAsync(admin);
        var product = await CreateProductAsync(admin, each);

        async Task<HashSet<string?>> Rejected(object body) =>
            Fields(await (await admin.PostJsonAsync($"/api/v1/products/{product["id"]}/conversions", body))
                .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"));

        Assert.Contains("alternateUomId", await Rejected(new { alternateUomId = each, factorToBase = "12" }));
        Assert.Contains("factorToBase", await Rejected(new { alternateUomId = box, factorToBase = "2.5" })); // precision 0
        Assert.Contains("factorToBase", await Rejected(new { alternateUomId = box, factorToBase = "0" }));
        Assert.Contains("factorToBase", await Rejected(new { alternateUomId = box, factorToBase = "1.1234567" }));

        var created = await admin.PostJsonAsync($"/api/v1/products/{product["id"]}/conversions", new { alternateUomId = box, factorToBase = "12" });
        var conversion = await created.ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("12", (string?)conversion["factorToBase"]);
        Assert.Contains("alternateUomId", await Rejected(new { alternateUomId = box, factorToBase = "24" }));

        // A unit used by an active conversion cannot be retired.
        await (await admin.PostEmptyAsync($"/api/v1/units/{box}/retire", "\"1\"")).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        await (await admin.PostEmptyAsync($"/api/v1/products/{product["id"]}/conversions/{conversion["id"]}/retire", "\"1\""))
            .ExpectAsync(HttpStatusCode.NoContent);

        // A retired conversion does not block a new one for the same unit (migration 0002, DEC-029).
        var recreated = await (await admin.PostJsonAsync($"/api/v1/products/{product["id"]}/conversions",
            new { alternateUomId = box, factorToBase = "24" })).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("24", (string?)recreated["factorToBase"]);
        await (await admin.PostEmptyAsync($"/api/v1/products/{product["id"]}/conversions/{recreated["id"]}/retire", "\"1\""))
            .ExpectAsync(HttpStatusCode.NoContent);

        await (await admin.PostEmptyAsync($"/api/v1/units/{box}/retire", "\"1\"")).ExpectAsync(HttpStatusCode.NoContent);
    }

    [Fact]
    [Trait("Requirement", "DATA-07")]
    public async Task Partner_lifecycle_and_type_filter()
    {
        var admin = AdminA;
        var code = Code("SUP");
        var partner = await (await admin.PostJsonAsync("/api/v1/partners", new { code, name = "Acme Supplies", type = "Supplier" }))
            .ExpectAsync(HttpStatusCode.Created);
        var updated = await admin.PatchJsonAsync($"/api/v1/partners/{partner["id"]}", new { type = "Both" }, "\"1\"");
        Assert.Equal("Both", (string?)(await updated.ExpectAsync(HttpStatusCode.OK))["type"]);

        var both = await (await admin.GetAsync($"/api/v1/partners?type=Both&search={code}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal((string?)partner["id"], (string?)Assert.Single(both["items"]!.AsArray())!["id"]);
        var suppliers = await (await admin.GetAsync($"/api/v1/partners?type=Supplier&search={code}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Empty(suppliers["items"]!.AsArray());

        await (await admin.PostEmptyAsync($"/api/v1/partners/{partner["id"]}/retire", "\"2\"")).ExpectAsync(HttpStatusCode.NoContent);
        await (await admin.PatchJsonAsync($"/api/v1/partners/{partner["id"]}", new { name = "Retired" }, "\"3\""))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
    }

    [Fact]
    [Trait("Requirement", "AT-TEN-01")]
    [Trait("Requirement", "TEN-001")]
    public async Task Tenant_B_cannot_read_change_or_reference_tenant_A_catalog()
    {
        var unit = await CreateUnitAsync(AdminA);
        var product = await CreateProductAsync(AdminA, unit);
        var profile = await CreateProfileAsync(AdminA);
        var partner = await (await AdminA.PostJsonAsync("/api/v1/partners", new { code = Code("C"), name = "Private", type = "Customer" }))
            .ExpectAsync(HttpStatusCode.Created);
        var b = AdminB;

        foreach (var url in new[]
                 {
                     $"/api/v1/units/{unit}", $"/api/v1/products/{product["id"]}", $"/api/v1/storage-profiles/{profile["id"]}",
                     $"/api/v1/partners/{partner["id"]}", $"/api/v1/products/{product["id"]}/conversions",
                     $"/api/v1/products/{product["id"]}/barcodes",
                 })
            await (await b.GetAsync(url)).ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");

        await (await b.PatchJsonAsync($"/api/v1/products/{product["id"]}", new { name = "Hijack", reason = "x" }, "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await b.PostEmptyAsync($"/api/v1/partners/{partner["id"]}/retire", "\"1\"")).ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await b.PostJsonAsync($"/api/v1/products/{product["id"]}/barcodes", new { gtin = "4006381333931" }))
            .ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");

        // A's unit and profile behave as unknown to B.
        var bUnit = await CreateUnitAsync(b);
        var refsA = await b.PostJsonAsync("/api/v1/products", new
        {
            sku = Code("SKU"), name = "X", handlingProfile = "General", trackingMode = "Quantity", baseUomId = unit, basePrecision = 0,
        });
        Assert.Contains("baseUomId", Fields(await refsA.ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED")));
        var refsProfile = await b.PostJsonAsync("/api/v1/products", new
        {
            sku = Code("SKU"), name = "X", handlingProfile = "General", trackingMode = "Quantity", baseUomId = bUnit, basePrecision = 0,
            storageProfileId = (string)profile["id"]!,
        });
        Assert.Contains("storageProfileId", Fields(await refsProfile.ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED")));

        var list = await (await b.GetAsync("/api/v1/products?limit=200")).ExpectAsync(HttpStatusCode.OK);
        Assert.DoesNotContain(list["items"]!.AsArray(), p => (string?)p!["id"] == (string?)product["id"]);
    }

    [Fact]
    [Trait("Requirement", "IAM-001")]
    public async Task Catalog_permissions_are_enforced()
    {
        var picker = fx.Client(fx.Seed.A.Picker, fx.Seed.A.TenantId);
        await (await picker.GetAsync("/api/v1/units")).ExpectAsync(HttpStatusCode.OK);
        await (await picker.GetAsync("/api/v1/products")).ExpectAsync(HttpStatusCode.OK);
        await (await picker.PostJsonAsync("/api/v1/units", new { code = Code("U"), name = "No" }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await (await picker.PostJsonAsync("/api/v1/partners", new { code = Code("P"), name = "No", type = "Supplier" }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");

        var noRoles = fx.Client(fx.Seed.A.NoRoles, fx.Seed.A.TenantId);
        foreach (var url in new[] { "/api/v1/units", "/api/v1/products", "/api/v1/partners", "/api/v1/storage-profiles" })
            await (await noRoles.GetAsync(url)).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    /// <summary>Appends the GS1 mod-10 check digit to 12 digits.</summary>
    private static string WithCheckDigit(string twelve)
    {
        var padded = twelve.PadLeft(13, '0');
        var sum = 0;
        for (var i = 0; i < 13; i++)
            sum += (padded[i] - '0') * (i % 2 == 0 ? 3 : 1);
        return twelve + ((10 - sum % 10) % 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

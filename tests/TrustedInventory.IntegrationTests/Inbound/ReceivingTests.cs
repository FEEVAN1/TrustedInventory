using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Inbound;

/// <summary>Receiving (INB-002 to INB-004, DEC-048) through the API on real PostgreSQL.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ReceivingTests(ApiFixture fx)
{
    private const string MedicalGtin = "09506000134352";

    [Fact]
    [Trait("Requirement", "AT-INB-01")]
    [Trait("Requirement", "INB-004")]
    [Trait("Requirement", "AT-STK-02")]
    [Trait("Requirement", "STK-007")]
    public async Task Medical_stock_lands_in_quarantine_once_and_a_replay_changes_nothing()
    {
        var w = await WorldAsync("rcv-at01");
        var key = Guid.NewGuid();
        var body = Receive(w, Item(w.MedLine, w.Medical, "100", w.Each, w.ColdStaging,
            lotNumber: "LOT-A", expirationDate: "2030-06-30", supplierReference: "SUP-LOT-A", temperature: 4.2m));

        var first = await (await PostAsync(w, key, body)).ExpectAsync(HttpStatusCode.OK);
        var line = Assert.Single(first["lines"]!.AsArray())!;
        Assert.Equal("Quarantine", (string?)line["qualityStatus"]);
        var bucket = Guid.Parse((string)line["bucketId"]!);
        Assert.Equal("100|0|Quarantine", await BucketAsync(bucket)); // on hand 100, nothing allocatable
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'StockQuarantined'", w.Tenant));

        var replay = await PostAsync(w, key, body);
        var again = await replay.ExpectAsync(HttpStatusCode.OK);
        // Same content; key order may differ because the stored outcome is jsonb.
        Assert.True(JsonNode.DeepEquals(first, again), $"first: {first.ToJsonString()}\nreplay: {again.ToJsonString()}");
        Assert.Equal("\"1\"", replay.ETag());
        Assert.Equal("100|0|Quarantine", await BucketAsync(bucket));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.stock_movement WHERE tenant_id = @t", w.Tenant));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'StockQuarantined'", w.Tenant));
    }

    [Fact]
    [Trait("Requirement", "AT-INB-02")]
    [Trait("Requirement", "INB-001")]
    public async Task Over_receipt_is_refused_whole_and_posts_nothing()
    {
        var w = await WorldAsync("rcv-at02");
        // 51 against 50 outstanding; split across two items of the same line it is still over.
        var problem = await (await PostAsync(w, Guid.NewGuid(), Receive(w, Item(w.FiftyLine, w.General, "51", w.Each, w.AmbientStaging))))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "OVER_RECEIPT");
        Assert.Equal("items[0].quantity", (string?)problem["errors"]![0]!["field"]);
        await (await PostAsync(w, Guid.NewGuid(), Receive(w, Item(w.FiftyLine, w.General, "30", w.Each, w.AmbientStaging),
            Item(w.FiftyLine, w.General, "21", w.Each, w.AmbientStaging)))).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "OVER_RECEIPT");
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM ti.stock_movement WHERE tenant_id = @t", w.Tenant));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM ti.stock_bucket WHERE tenant_id = @t", w.Tenant));

        // Exactly the outstanding quantity completes the line; the order then reports progress.
        await (await PostAsync(w, Guid.NewGuid(), Receive(w, Item(w.FiftyLine, w.General, "2", w.Box, w.AmbientStaging)))).ExpectAsync(HttpStatusCode.OK); // 24
        await (await PostAsync(w, Guid.NewGuid(), Receive(w, Item(w.FiftyLine, w.General, "26", w.Each, w.AmbientStaging)))).ExpectAsync(HttpStatusCode.OK);
        var order = await (await w.Client.GetAsync($"/api/v1/purchase-orders/{w.Order}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("PartReceived", (string?)order["status"]); // other lines still open
        var fifty = order["lines"]!.AsArray().Single(l => (string?)l!["id"] == w.FiftyLine)!;
        Assert.Equal(["50", "0"], new[] { (string)fifty["receivedBaseQuantity"]!, (string)fifty["outstandingBaseQuantity"]! });
        await (await PostAsync(w, Guid.NewGuid(), Receive(w, Item(w.FiftyLine, w.General, "1", w.Each, w.AmbientStaging))))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "OVER_RECEIPT");
    }

    [Fact]
    [Trait("Requirement", "STK-007")]
    [Trait("Requirement", "AT-STK-02")]
    public async Task A_retry_never_posts_twice_even_after_the_idempotency_record_expires_or_when_concurrent()
    {
        var w = await WorldAsync("rcv-retry");
        var key = Guid.NewGuid();
        var body = Receive(w, Item(w.FiftyLine, w.General, "5", w.Each, w.AmbientStaging));

        // Concurrent retries of one key: one receipt.
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => PostAsync(w, key, body)));
        var receipts = new HashSet<string?>();
        foreach (var r in responses)
            receipts.Add((string?)(await r.ExpectAsync(HttpStatusCode.OK))["receiptId"]);
        Assert.Single(receipts);

        // After the 7-day record is gone (simulated), the permanent operation key still prevents a second posting.
        await ExecAsync("DELETE FROM ti.idempotency_record WHERE tenant_id = @t", ("t", w.Tenant));
        var late = await (await PostAsync(w, key, body)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(receipts.Single(), (string?)late["receiptId"]);
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.receipt WHERE tenant_id = @t", w.Tenant));
        Assert.Equal("5", await LedgerAsync(w));

        // The key is mandatory, and the same key with a different body is a different request.
        var noKey = new HttpRequestMessage(HttpMethod.Post, "/api/v1/inventory/receive") { Content = Json(body) };
        await (await w.Client.SendAsync(noKey)).ExpectProblemAsync(HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_REQUIRED");
        await (await PostAsync(w, Guid.NewGuid(), body)).ExpectAsync(HttpStatusCode.OK); // new key: a genuine second receipt
        Assert.Equal("10", await LedgerAsync(w));
    }

    [Fact]
    [Trait("Requirement", "INB-002")]
    [Trait("Requirement", "INB-003")]
    public async Task Scanned_barcodes_fill_or_must_match_the_entered_identifiers()
    {
        var w = await WorldAsync("rcv-scan");
        ReceiveBody Med(string? barcode, string? lot = "LOT-S", string? expiry = "2027-12-31") =>
            Item(w.MedLine, w.Medical, "10", w.Each, w.ColdStaging, lotNumber: lot, expirationDate: expiry, supplierReference: "SR", temperature: 5m, barcode: barcode);

        // The scan supplies lot and expiry the receiver did not type.
        var ok = await (await PostAsync(w, Guid.NewGuid(), Receive(w, Med($"(01){MedicalGtin}(17)271231(10)LOT-S", lot: null, expiry: null))))
            .ExpectAsync(HttpStatusCode.OK);
        var lotId = Guid.Parse((string)ok["lines"]![0]!["lotId"]!);
        Assert.Equal("LOT-S|2027-12-31", await ScalarAsync<string>("SELECT lot_number || '|' || expiry_date FROM ti.lot WHERE id = @id", ("id", lotId)));

        async Task<string?> Rejected(ReceiveBody item, string code) =>
            (string?)(await (await PostAsync(w, Guid.NewGuid(), Receive(w, item))).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code))["errors"]![0]!["field"];
        Assert.Equal("items[0].lotNumber", await Rejected(Med($"(01){MedicalGtin}(10)OTHER"), "BARCODE_CONFLICT"));
        Assert.Equal("items[0].expirationDate", await Rejected(Med($"(01){MedicalGtin}(17)280101(10)LOT-S"), "BARCODE_CONFLICT"));
        Assert.Equal("items[0].productId", await Rejected(
            Item(w.FiftyLine, w.General, "1", w.Each, w.AmbientStaging, barcode: $"(01){MedicalGtin}"), "BARCODE_CONFLICT")); // GTIN of another product
        Assert.Equal("items[0].scannedBarcode", await Rejected(Med("(01)00012345678905"), "UNKNOWN_BARCODE"));   // valid GTIN, no mapping
        Assert.Equal("items[0].scannedBarcode", await Rejected(Med("(01)09506000134353"), "UNKNOWN_BARCODE"));   // bad check digit
        Assert.Equal("items[0].expirationDate", await Rejected(Med(null, expiry: "2029-01-01"), "BARCODE_CONFLICT")); // lot exists with another expiry
    }

    [Fact]
    [Trait("Requirement", "INB-003")]
    [Trait("Requirement", "INB-004")]
    public async Task Medical_evidence_is_required_and_exceptional_arrivals_need_a_reason_and_never_become_available()
    {
        var w = await WorldAsync("rcv-quality");
        ReceiveBody Med(string? supplierRef = "SR", decimal? temp = 5m, string? reason = null, string expiry = "2030-01-01") =>
            Item(w.MedLine, w.Medical, "1", w.Each, w.ColdStaging, lotNumber: $"L{Guid.NewGuid():N}"[..10], expirationDate: expiry,
                supplierReference: supplierRef, temperature: temp, exceptionReason: reason);
        async Task<string?> Rejected(ReceiveBody item, string code) =>
            (string?)(await (await PostAsync(w, Guid.NewGuid(), Receive(w, item))).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code))["errors"]![0]!["field"];
        async Task<string?> Quality(ReceiveBody item) =>
            (string?)(await (await PostAsync(w, Guid.NewGuid(), Receive(w, item))).ExpectAsync(HttpStatusCode.OK))["lines"]![0]!["qualityStatus"];

        Assert.Equal("items[0].supplierReference", await Rejected(Med(supplierRef: null), "MISSING_MEDICAL_EVIDENCE"));
        Assert.Equal("items[0].initialTemperatureCelsius", await Rejected(Med(temp: null), "MISSING_MEDICAL_EVIDENCE"));
        Assert.Equal("items[0].lotNumber", await Rejected(
            Item(w.MedLine, w.Medical, "1", w.Each, w.ColdStaging, expirationDate: "2030-01-01", supplierReference: "SR", temperature: 5m), "MISSING_MEDICAL_EVIDENCE"));

        // Out of the 2–8 °C range or expired: only with a reason, and never Available.
        Assert.Equal("items[0].exceptionReason", await Rejected(Med(temp: 9.5m), "VALIDATION_FAILED"));
        Assert.Equal("Quarantine", await Quality(Med(temp: 9.5m, reason: "Arrived warm; QA to review")));
        Assert.Equal("items[0].exceptionReason", await Rejected(
            Item(w.LotLine, w.Lotted, "1", w.Each, w.AmbientStaging, lotNumber: "OLD", expirationDate: "2020-01-01"), "VALIDATION_FAILED"));
        Assert.Equal("Hold", await Quality(Item(w.LotLine, w.Lotted, "1", w.Each, w.AmbientStaging, lotNumber: "OLD", expirationDate: "2020-01-01",
            exceptionReason: "Expired on arrival; supplier credit requested")));

        // General stock is Available unless inspection failed, which also needs a reason.
        Assert.Equal("Available", await Quality(Item(w.FiftyLine, w.General, "1", w.Each, w.AmbientStaging)));
        Assert.Equal("items[0].exceptionReason", await Rejected(Item(w.FiftyLine, w.General, "1", w.Each, w.AmbientStaging, inspection: "Failed"), "VALIDATION_FAILED"));
        Assert.Equal("Hold", await Quality(Item(w.FiftyLine, w.General, "1", w.Each, w.AmbientStaging, inspection: "Failed", exceptionReason: "Crushed cartons")));
    }

    [Fact]
    [Trait("Requirement", "INB-004")]
    [Trait("Requirement", "STK-PRECISION")]
    public async Task Serials_post_one_unit_per_line_and_are_never_received_twice()
    {
        var w = await WorldAsync("rcv-serial");
        var ok = await (await PostAsync(w, Guid.NewGuid(), Receive(w, Item(w.SerLine, w.Serialized, "3", w.Each, w.AmbientStaging, serials: ["S-1", "S-2", "S-3"]))))
            .ExpectAsync(HttpStatusCode.OK);
        var lines = ok["lines"]!.AsArray();
        Assert.Equal(3, lines.Count);
        Assert.Equal(3, lines.Select(l => (string?)l!["bucketId"]).Distinct().Count());
        var receipt = await (await w.Client.GetAsync($"/api/v1/receipts/{ok["receiptId"]}")).ExpectAsync(HttpStatusCode.OK);
        Assert.All(receipt["lines"]!.AsArray(), l => Assert.Single(l!["serialIds"]!.AsArray()));
        Assert.Equal(3L, await CountAsync("SELECT count(*) FROM ti.stock_bucket WHERE tenant_id = @t AND serial_id IS NOT NULL AND on_hand_qty = 1", w.Tenant));

        async Task<string?> Rejected(ReceiveBody item, string code) =>
            (string?)(await (await PostAsync(w, Guid.NewGuid(), Receive(w, item))).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code))["errors"]![0]!["field"];
        Assert.Equal("items[0].serialNumbers", await Rejected(Item(w.SerLine, w.Serialized, "1", w.Each, w.AmbientStaging, serials: ["S-2"]), "DUPLICATE_SERIAL"));
        Assert.Equal("items[0].serialNumbers", await Rejected(Item(w.SerLine, w.Serialized, "2", w.Each, w.AmbientStaging, serials: ["S-9"]), "VALIDATION_FAILED"));
        Assert.Equal("items[0].serialNumbers", await Rejected(Item(w.FiftyLine, w.General, "1", w.Each, w.AmbientStaging, serials: ["X"]), "VALIDATION_FAILED"));
        Assert.Equal("items[0].quantity", await Rejected(Item(w.FiftyLine, w.General, "1.5", w.Each, w.AmbientStaging), "VALIDATION_FAILED"));
        Assert.Equal("items[0].lotNumber", await Rejected(Item(w.LotLine, w.Lotted, "1", w.Each, w.AmbientStaging), "VALIDATION_FAILED"));
        Assert.Equal(3L, await CountAsync("SELECT count(*) FROM ti.serial WHERE tenant_id = @t", w.Tenant));
    }

    [Fact]
    [Trait("Requirement", "STORE-COMPAT")]
    [Trait("Requirement", "INB-001")]
    public async Task Goods_land_only_in_a_compatible_staging_bin_of_an_approved_order_in_that_warehouse()
    {
        var w = await WorldAsync("rcv-bins");
        ReceiveBody Med(string bin) => Item(w.MedLine, w.Medical, "1", w.Each, bin, lotNumber: "LB", expirationDate: "2030-01-01", supplierReference: "SR", temperature: 5m);
        await (await PostAsync(w, Guid.NewGuid(), Receive(w, Med(w.AmbientStaging)))).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "INCOMPATIBLE_BIN");
        await (await PostAsync(w, Guid.NewGuid(), Receive(w, Item(w.FiftyLine, w.General, "1", w.Each, w.AmbientStorage))))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "INCOMPATIBLE_BIN"); // storage bins come after putaway

        var draft = (string)(await (await w.Client.PostJsonAsync("/api/v1/purchase-orders", new
        {
            supplierId = w.Supplier, warehouseId = w.Warehouse, reference = "PO-DRAFT",
            lines = new[] { new { productId = w.General, quantity = "5", uomId = w.Each } },
        })).ExpectAsync(HttpStatusCode.Created))["id"]!;
        var draftLine = (string)(await (await w.Client.GetAsync($"/api/v1/purchase-orders/{draft}")).ExpectAsync(HttpStatusCode.OK))["lines"]![0]!["id"]!;
        await (await PostAsync(w, Guid.NewGuid(), new { warehouseId = w.Warehouse, purchaseOrderId = draft, items = new[] { Item(draftLine, w.General, "1", w.Each, w.AmbientStaging) } }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        await (await PostAsync(w, Guid.NewGuid(), new { warehouseId = Guid.NewGuid(), purchaseOrderId = w.Order, items = new[] { Item(w.FiftyLine, w.General, "1", w.Each, w.AmbientStaging) } }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"); // the order belongs to another warehouse
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM ti.stock_movement WHERE tenant_id = @t", w.Tenant));
    }

    [Fact]
    [Trait("Requirement", "UI-SCREENS")]
    [Trait("Requirement", "API-16")]
    [Trait("Requirement", "AT-TEN-01")]
    public async Task Lots_and_serials_are_listed_filtered_paged_and_read_within_the_tenant()
    {
        // DEC-087: listLots, getLot and listSerials were in the contract but not mapped; the recall and traceability screens use them.
        var w = await WorldAsync("rcv-lots");
        await (await PostAsync(w, Guid.NewGuid(), Receive(w,
            Item(w.LotLine, w.Lotted, "4", w.Each, w.AmbientStaging, lotNumber: "LOT-A1", expirationDate: "2030-06-30"),
            Item(w.LotLine, w.Lotted, "6", w.Each, w.AmbientStaging, lotNumber: "LOT-B1"),
            Item(w.SerLine, w.Serialized, "2", w.Each, w.AmbientStaging, serials: ["S-10", "S-20"])))).ExpectAsync(HttpStatusCode.OK);

        async Task<JsonArray> Items(string url) => (await (await w.Client.GetAsync(url)).ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray();
        static string[] Numbers(JsonArray items, string field) => items.Select(i => (string)i![field]!).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["LOT-A1", "LOT-B1"], Numbers(await Items($"/api/v1/lots?productId={w.Lotted}"), "lotNumber"));
        Assert.Equal(["LOT-A1"], Numbers(await Items($"/api/v1/lots?productId={w.Lotted}&lotNumber=LOT-A"), "lotNumber")); // a prefix
        Assert.Empty(await Items($"/api/v1/lots?productId={w.Lotted}&lotNumber=lot-a")); // case-sensitive
        Assert.Equal(["LOT-A1"], Numbers(await Items("/api/v1/lots?expiresBefore=2031-01-01"), "lotNumber")); // no expiry, not listed

        // Cursor paging in the default order, bound to the filter.
        var first = await (await w.Client.GetAsync($"/api/v1/lots?productId={w.Lotted}&limit=1")).ExpectAsync(HttpStatusCode.OK);
        var cursor = (string)first["nextCursor"]!;
        var second = await (await w.Client.GetAsync($"/api/v1/lots?productId={w.Lotted}&limit=1&cursor={Uri.EscapeDataString(cursor)}"))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.NotEqual((string?)first["items"]![0]!["id"], (string?)second["items"]![0]!["id"]);
        Assert.Null((string?)second["nextCursor"]);
        await (await w.Client.GetAsync($"/api/v1/lots?limit=1&cursor={Uri.EscapeDataString(cursor)}")).ExpectProblemAsync(HttpStatusCode.BadRequest, "INVALID_CURSOR");

        // One lot with its version as the ETag; an unknown id is 404.
        var lotA = (await Items($"/api/v1/lots?productId={w.Lotted}&lotNumber=LOT-A1"))[0]!;
        var single = await w.Client.GetAsync($"/api/v1/lots/{lotA["id"]}");
        var lot = await single.ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("\"1\"", single.ETag());
        Assert.Equal("LOT-A1", (string?)lot["lotNumber"]);
        Assert.Equal("2030-06-30", (string?)lot["expiryDate"]);
        Assert.False((bool)lot["expiryCorrected"]!);
        await (await w.Client.GetAsync($"/api/v1/lots/{Guid.NewGuid()}")).ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");

        // Serials: append-only, so version 1 and updatedAt = createdAt (DEC-055).
        var serials = await Items($"/api/v1/serials?productId={w.Serialized}");
        Assert.Equal(["S-10", "S-20"], Numbers(serials, "serialNumber"));
        Assert.All(serials, s =>
        {
            Assert.Equal(1, (int)s!["version"]!);
            Assert.Equal((string?)s["createdAt"], (string?)s["updatedAt"]);
        });
        Assert.Equal(["S-20"], Numbers(await Items("/api/v1/serials?serialNumber=S-2"), "serialNumber"));

        // Another tenant sees none of them (RLS), even by id.
        var other = await WorldAsync("rcv-lots-other");
        Assert.Empty((await (await other.Client.GetAsync("/api/v1/lots")).ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray());
        Assert.Empty((await (await other.Client.GetAsync("/api/v1/serials")).ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray());
        await (await other.Client.GetAsync($"/api/v1/lots/{lotA["id"]}")).ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
    }

    // ---------------------------------------------------------------------------------------------------------------

    private sealed record World(Guid Tenant, HttpClient Client, string Warehouse, string AmbientStaging, string AmbientStorage, string ColdStaging,
        string Each, string Box, string General, string Lotted, string Medical, string Serialized, string Supplier, string Order,
        string FiftyLine, string LotLine, string MedLine, string SerLine);

    private async Task<World> WorldAsync(string label)
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, label);
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        await TenantProvisioning.GrantSelfAsync(fx, tenant, "WarehouseManager");
        await TenantProvisioning.GrantSelfAsync(fx, tenant, "Receiver");
        async Task<string> Create(string path, object body) => (string)(await (await admin.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!;

        var warehouse = await Create("/api/v1/warehouses", new { code = "W1", name = "Main", timeZone = "Africa/Lagos" });
        var ambient = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "AMB", name = "Ambient" });
        var cold = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "COLD", name = "Cold", tempMinC = 2, tempMaxC = 8 });
        var ambientStaging = await Create("/api/v1/bins", new { zoneId = ambient, code = "AMB-STG", kind = "Staging" });
        var ambientStorage = await Create("/api/v1/bins", new { zoneId = ambient, code = "AMB-01", kind = "Storage" });
        var coldStaging = await Create("/api/v1/bins", new { zoneId = cold, code = "COLD-STG", kind = "Staging" });
        var each = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        var box = await Create("/api/v1/units", new { code = "BOX", name = "Box" });
        var profile = await Create("/api/v1/storage-profiles", new
        {
            name = "Cold chain", minC = 2, maxC = 8, monitoringRequired = true,
            excursionPolicy = new { immediateHold = true, notes = "QA reviewed" }, reason = "receiving test",
        });
        object Product(string sku, string handling, string tracking, string? storage = null) =>
            new { sku, name = sku, handlingProfile = handling, trackingMode = tracking, baseUomId = each, basePrecision = 0, storageProfileId = storage };
        var general = await Create("/api/v1/products", Product("GEN", "General", "Quantity"));
        var lotted = await Create("/api/v1/products", Product("LOT", "General", "Lot"));
        var medical = await Create("/api/v1/products", Product("MED", "Medical", "Lot", profile));
        var serialized = await Create("/api/v1/products", Product("SER", "General", "Serial"));
        await Create($"/api/v1/products/{general}/conversions", new { alternateUomId = box, factorToBase = "12" });
        await Create($"/api/v1/products/{medical}/barcodes", new { gtin = MedicalGtin });
        var supplier = await Create("/api/v1/partners", new { code = "SUP", name = "Supplier", type = "Supplier" });

        var order = await (await admin.PostJsonAsync("/api/v1/purchase-orders", new
        {
            supplierId = supplier, warehouseId = warehouse, reference = "PO-R1",
            lines = new object[]
            {
                new { productId = general, quantity = "50", uomId = each },
                new { productId = lotted, quantity = "100", uomId = each },
                new { productId = medical, quantity = "1000", uomId = each },
                new { productId = serialized, quantity = "10", uomId = each },
            },
        })).ExpectAsync(HttpStatusCode.Created);
        var orderId = (string)order["id"]!;
        await (await admin.PostEmptyAsync($"/api/v1/purchase-orders/{orderId}/approve", "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        var lines = order["lines"]!.AsArray().Select(l => (string)l!["id"]!).ToList();
        return new World(tenant.TenantId, admin, warehouse, ambientStaging, ambientStorage, coldStaging, each, box, general, lotted, medical, serialized,
            supplier, orderId, lines[0], lines[1], lines[2], lines[3]);
    }

    private sealed record ReceiveBody(string PurchaseOrderLineId, string ProductId, string Quantity, string UomId, string TargetBinId, string? LotNumber,
        string? ExpirationDate, string? SupplierReference, decimal? InitialTemperatureCelsius, string? ScannedBarcode, string[]? SerialNumbers,
        string? InspectionResult, string? ExceptionReason);

    private static ReceiveBody Item(string line, string product, string quantity, string uom, string bin, string? lotNumber = null, string? expirationDate = null,
        string? supplierReference = null, decimal? temperature = null, string? barcode = null, string[]? serials = null, string? inspection = null,
        string? exceptionReason = null) =>
        new(line, product, quantity, uom, bin, lotNumber, expirationDate, supplierReference, temperature, barcode, serials, inspection, exceptionReason);

    private static object Receive(World w, params ReceiveBody[] items) => new { warehouseId = w.Warehouse, purchaseOrderId = w.Order, items };

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body, Options), System.Text.Encoding.UTF8, "application/json");

    private static Task<HttpResponseMessage> PostAsync(World w, Guid key, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/inventory/receive") { Content = Json(body) };
        request.Headers.Add("Idempotency-Key", key.ToString());
        return w.Client.SendAsync(request);
    }

    private async Task<string> BucketAsync(Guid id) => await ScalarAsync<string>(
        "SELECT trim_scale(on_hand_qty)::text || '|' || trim_scale(reserved_qty)::text || '|' || quality_status FROM ti.stock_bucket WHERE id = @id", ("id", id));

    /// <summary>Sum of receipt movements, which must equal the sum of bucket on-hand (the ledger is the authority).</summary>
    private async Task<string> LedgerAsync(World w)
    {
        var movements = await ScalarAsync<string>("SELECT coalesce(trim_scale(sum(base_qty)), 0)::text FROM ti.stock_movement WHERE tenant_id = @t", ("t", w.Tenant));
        var buckets = await ScalarAsync<string>("SELECT coalesce(trim_scale(sum(on_hand_qty)), 0)::text FROM ti.stock_bucket WHERE tenant_id = @t", ("t", w.Tenant));
        Assert.Equal(movements, buckets);
        return movements;
    }

    private async Task<long> CountAsync(string sql, Guid tenant) => await ScalarAsync<long>(sql, ("t", tenant));

    private Task<int> ExecAsync(string sql, params (string Name, object Value)[] args) => TenantProvisioning.ExecAsSuperuserAsync(fx, sql, args);

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args) =>
        (T)(await TenantProvisioning.ScalarAsSuperuserAsync(fx, sql, args))!;
}

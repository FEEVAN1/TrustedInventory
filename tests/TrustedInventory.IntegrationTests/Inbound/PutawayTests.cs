using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TrustedInventory.Application.Audit;
using TrustedInventory.Infrastructure;
using TrustedInventory.Infrastructure.Jobs;
using TrustedInventory.Infrastructure.Persistence;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Inbound;

/// <summary>Putaway (INB-005, INB-006, AT-INB-03, DEC-050) through the API on real PostgreSQL.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class PutawayTests(ApiFixture fx)
{
    private static readonly string[] HazardClasses = ["UN1993"];

    [Fact]
    [Trait("Requirement", "INB-005")]
    [Trait("Requirement", "INB-006")]
    [Trait("Requirement", "STK-004")]
    public async Task Receiving_creates_a_ranked_task_and_confirming_moves_stock_until_the_receipt_closes()
    {
        var w = await WorldAsync("put-flow");
        var receipt = await ReceiveAsync(w, Item(w.GenLine, w.General, "8", w.AmbientStaging));
        var line = (string)receipt["lines"]![0]!["id"]!;
        var staging = (string)receipt["lines"]![0]!["bucketId"]!;

        // Capacity in boxes cannot be judged for a product without a box conversion: B-CAP is excluded despite priority 0.
        var task = await TaskAsync(w, (string)receipt["receiptId"]!);
        Assert.Equal(["Putaway", "Open", "8", "8"], new[] { (string)task["type"]!, (string)task["status"]!, (string)task["quantity"]!, (string)task["remainingQuantity"]! });
        Assert.Equal(["A-02", "C-01", "A-03", "A-01"], Codes(task)); // no storage profile: the cold zone is compatible too (DEC-012)

        var key = Guid.NewGuid();
        var first = await (await ConfirmAsync(w, key, Confirm(line, w.AmbientStaging, w.A02, "5", w.General))).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["3", "InProgress"], new[] { (string)first["remainingQuantity"]!, (string)first["taskStatus"]! });
        var replay = await (await ConfirmAsync(w, key, Confirm(line, w.AmbientStaging, w.A02, "5", w.General))).ExpectAsync(HttpStatusCode.OK);
        Assert.True(JsonNode.DeepEquals(first, replay));
        Assert.Equal("3", (string?)(await StockAsync(w, staging))["onHand"]); // moved once

        // A-02 now holds the product, so it ranks first; any other bin needs a reason.
        Assert.Equal("Already holds this product and lot", (string?)(await TaskAsync(w, (string)receipt["receiptId"]!))["recommendedBins"]![0]!["reason"]);
        await (await ConfirmAsync(w, Guid.NewGuid(), Confirm(line, w.AmbientStaging, w.A01, "3", w.General)))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        var done = await (await ConfirmAsync(w, Guid.NewGuid(), Confirm(line, w.AmbientStaging, w.A01, "3", w.General, reason: "A-02 aisle blocked by a forklift")))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["0", "Completed"], new[] { (string)done["remainingQuantity"]!, (string)done["taskStatus"]! });

        var closed = await (await w.Client.GetAsync($"/api/v1/receipts/{receipt["receiptId"]}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Closed", (string?)closed["status"]);
        Assert.Equal("0", (string?)closed["lines"]![0]!["putawayRemainingQuantity"]);
        var putAway = (await ItemsAsync(w.Client, $"/api/v1/stock?productId={w.General}&eligibleOnly=true")).Select(b => (string)b["binCode"]! + ":" + (string)b["allocatable"]!).Order();
        Assert.Equal(["A-01:3", "A-02:5"], putAway);
        var moves = await ItemsAsync(w.Client, $"/api/v1/stock/movements?type=Putaway&productId={w.General}");
        Assert.Contains(moves, m => (string?)m["reason"] == "A-02 aisle blocked by a forklift");
        Assert.All(moves, m => Assert.Equal(staging, (string?)m["source"]!["bucketId"]));

        // The ledger still balances.
        await ReconcileAsync();
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM ti.alert WHERE tenant_id = @t AND type = 'ReconciliationMismatch'", ("t", w.Tenant)));
    }

    [Fact]
    [Trait("Requirement", "AT-INB-03")]
    [Trait("Requirement", "STORE-COMPAT")]
    [Trait("Requirement", "INB-006")]
    [Trait("Requirement", "MED-001")]
    public async Task An_incompatible_bin_is_refused_and_moving_held_stock_never_releases_it()
    {
        var w = await WorldAsync("put-at03");
        var receipt = await ReceiveAsync(w, Item(w.MedLine, w.Medical, "4", w.ColdStaging, lotNumber: "M-1", expirationDate: "2030-01-01",
            supplierReference: "SR", temperature: 5m));
        var line = (string)receipt["lines"]![0]!["id"]!;
        var staging = (string)receipt["lines"]![0]!["bucketId"]!;
        Assert.Equal(["C-01"], Codes(await TaskAsync(w, (string)receipt["receiptId"]!)));

        // QA cannot release it while it waits in staging: putaway moves this bucket and keeps its quality, so the release would strand
        // the task (DEC-096). Nothing moves; it is released after putaway, where it is stored.
        var qa = $"user-qa-{Guid.NewGuid():N}";
        var membership = await ScalarAsync<Guid>("INSERT INTO ti.membership (tenant_id, user_subject, created_by) VALUES (@t, @s, 'seed') RETURNING id",
            ("t", w.Tenant), ("s", qa));
        await TenantProvisioning.ExecAsSuperuserAsync(fx, """
            INSERT INTO ti.role_grant (tenant_id, membership_id, grantee_subject, role, reason, granted_by, created_by)
            VALUES (@t, @m, @s, 'QaOfficer', 'test seed', 'seed', 'seed')
            """, ("t", w.Tenant), ("m", membership), ("s", qa));
        var release = new HttpRequestMessage(HttpMethod.Post, "/api/v1/quality/release")
        {
            Content = Json(new { bucketId = staging, approvalId = Guid.NewGuid() }),
        };
        release.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var early = await (await fx.Client(qa, w.Tenant).SendAsync(release)).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        Assert.Contains("Put these goods away first", (string?)early["detail"], StringComparison.Ordinal);
        Assert.Equal("4", (string?)(await StockAsync(w, staging))["onHand"]);

        // AT-INB-03: refrigerated stock scanned into an ambient bin is blocked; task and stock stay as they were.
        var refused = await (await ConfirmAsync(w, Guid.NewGuid(), Confirm(line, w.ColdStaging, w.A01, "4", w.Medical, lot: "M-1", reason: "try")))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "INCOMPATIBLE_BIN");
        Assert.Equal("targetBinId", (string?)refused["errors"]![0]!["field"]);
        var task = await TaskAsync(w, (string)receipt["receiptId"]!);
        Assert.Equal(["Open", "4"], new[] { (string)task["status"]!, (string)task["remainingQuantity"]! });
        Assert.Equal("4", (string?)(await StockAsync(w, staging))["onHand"]);
        Assert.Empty(await ItemsAsync(w.Client, $"/api/v1/stock/movements?type=Putaway&productId={w.Medical}"));

        // Into the compatible bin it stays Quarantine: putaway never releases quality.
        await (await ConfirmAsync(w, Guid.NewGuid(), Confirm(line, w.ColdStaging, w.C01, "4", w.Medical, lot: "M-1"))).ExpectAsync(HttpStatusCode.OK);
        var stored = (await ItemsAsync(w.Client, $"/api/v1/stock?productId={w.Medical}")).Single();
        Assert.Equal(["C-01", "Quarantine"], new[] { (string)stored["binCode"]!, (string)stored["qualityStatus"]! });
        Assert.Equal(["Quarantine"], stored["ineligibilityReasons"]!.AsArray().Select(r => (string)r!));

        // Held general stock may go to a quarantine bin and stays on hold.
        var held = await ReceiveAsync(w, Item(w.GenLine, w.General, "2", w.AmbientStaging, inspection: "Failed", exceptionReason: "Torn packaging"));
        var heldTask = await TaskAsync(w, (string)held["receiptId"]!);
        Assert.Contains("AMB-Q", Codes(heldTask));
        await (await ConfirmAsync(w, Guid.NewGuid(), Confirm((string)held["lines"]![0]!["id"]!, w.AmbientStaging, w.AmbientQuarantine, "2", w.General,
            reason: "Hold stock goes to the quarantine cage"))).ExpectAsync(HttpStatusCode.OK);
        var cage = (await ItemsAsync(w.Client, $"/api/v1/stock?binId={w.AmbientQuarantine}")).Single();
        Assert.Equal("Hold", (string?)cage["qualityStatus"]);
    }

    [Fact]
    [Trait("Requirement", "INB-005")]
    public async Task Capacity_limits_recommendations_and_no_suitable_bin_raises_an_exception_task()
    {
        var w = await WorldAsync("put-exception");
        // 12 does not fit A-03 (capacity 10).
        var big = await ReceiveAsync(w, Item(w.GenLine, w.General, "12", w.AmbientStaging));
        Assert.Equal(["A-02", "C-01", "A-01"], Codes(await TaskAsync(w, (string)big["receiptId"]!)));

        // No zone with storage bins allows the hazard class: an exception task and a Warning alert, never an arbitrary bin.
        var hazardous = await ReceiveAsync(w, Item(w.HazLine, w.Hazardous, "2", w.HazardStaging));
        var task = await TaskAsync(w, (string)hazardous["receiptId"]!);
        Assert.Equal("Exception", (string?)task["type"]);
        Assert.Empty(task["recommendedBins"]!.AsArray());
        Assert.NotNull(task["description"]);
        Assert.Equal("Warning", await ScalarAsync<string>(
            "SELECT severity::text FROM ti.alert WHERE tenant_id = @t AND type = 'PutawayException' AND status = 'Open'", ("t", w.Tenant)));
        var line = (string)hazardous["lines"]![0]!["id"]!;

        // Once a zone allows the class, the exception is resolved with a reason.
        await (await w.Client.PatchJsonAsync($"/api/v1/zones/{w.AmbientZone}", new { allowedHazardClasses = HazardClasses, reason = "Hazmat cage installed" }, "\"1\""))
            .ExpectAsync(HttpStatusCode.OK);
        await (await ConfirmAsync(w, Guid.NewGuid(), Confirm(line, w.HazardStaging, w.A01, "2", w.Hazardous)))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"); // reason required for exceptions
        await (await ConfirmAsync(w, Guid.NewGuid(), Confirm(line, w.HazardStaging, w.A01, "2", w.Hazardous, reason: "Placed in new hazmat cage")))
            .ExpectAsync(HttpStatusCode.OK);
        var resolved = await TaskAsync(w, (string)hazardous["receiptId"]!);
        Assert.Equal(["Completed", "Placed in new hazmat cage"], new[] { (string)resolved["status"]!, (string)resolved["resolution"]! });
        Assert.NotNull(resolved["completedBy"]);
    }

    [Fact]
    [Trait("Requirement", "INB-005")]
    [Trait("Requirement", "OUT-006")]
    public async Task An_exception_task_completes_by_hand_only_after_its_stock_left_the_source_bin()
    {
        var w = await WorldAsync("put-task");
        var general = await ReceiveAsync(w, Item(w.GenLine, w.General, "2", w.AmbientStaging));
        var hazardous = await ReceiveAsync(w, Item(w.HazLine, w.Hazardous, "2", w.HazardStaging));
        var receiptId = (string)hazardous["receiptId"]!;
        var bucket = (string)hazardous["lines"]![0]!["bucketId"]!;

        // GET /tasks lists every type, filtered by type and status (DEC-016); GET /tasks/{id} carries the version as the ETag.
        var exceptions = await ItemsAsync(w.Client, "/api/v1/tasks?type=Exception&status=Open");
        var task = Assert.Single(exceptions);
        Assert.Equal(["Exception", "Receipt", receiptId], new[] { (string)task["type"]!, (string)task["sourceType"]!, (string)task["sourceId"]! });
        Assert.Contains(await ItemsAsync(w.Client, "/api/v1/tasks?type=Putaway"), t => (string?)t["sourceId"] == (string?)general["receiptId"]);
        var id = (string)task["id"]!;
        var single = await w.Client.GetAsync($"/api/v1/tasks/{id}");
        Assert.Equal("\"1\"", single.Headers.ETag?.Tag);
        await (await w.Client.GetAsync($"/api/v1/tasks/{Guid.NewGuid()}")).ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");

        // Refused while the stock is still in the staging bin; a putaway task never completes here; If-Match and a resolution are required.
        var resolution = new { resolution = "Moved to the new hazmat cage" };
        await (await w.Client.PostJsonAsync($"/api/v1/tasks/{id}/complete", resolution, "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        var putawayTask = (await ItemsAsync(w.Client, "/api/v1/tasks?type=Putaway")).Single();
        await (await w.Client.PostJsonAsync($"/api/v1/tasks/{putawayTask["id"]}/complete", resolution, "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        await (await w.Client.PostJsonAsync($"/api/v1/tasks/{id}/complete", resolution)).ExpectProblemAsync(HttpStatusCode.PreconditionRequired, "PRECONDITION_REQUIRED");
        await (await w.Client.PostJsonAsync($"/api/v1/tasks/{id}/complete", new { resolution = "" }, "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.Equal("Open", (string?)(await (await w.Client.GetAsync($"/api/v1/tasks/{id}")).ExpectAsync(HttpStatusCode.OK))["status"]);

        // The stock moves another way (a zone now allows the class, then a bin transfer); the task then completes and the receipt closes.
        await (await w.Client.PatchJsonAsync($"/api/v1/zones/{w.AmbientZone}", new { allowedHazardClasses = HazardClasses, reason = "Hazmat cage installed" }, "\"1\""))
            .ExpectAsync(HttpStatusCode.OK);
        var move = new HttpRequestMessage(HttpMethod.Post, "/api/v1/inventory/bin-transfers")
        {
            Content = Json(new { sourceBucketId = bucket, targetBinId = w.A01, quantity = "2", reason = "Into the new hazmat cage" }),
        };
        move.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        await (await w.Client.SendAsync(move)).ExpectAsync(HttpStatusCode.OK);
        await (await w.Client.PostJsonAsync($"/api/v1/tasks/{id}/complete", resolution, "\"2\""))
            .ExpectProblemAsync(HttpStatusCode.PreconditionFailed, "VERSION_CONFLICT");
        var done = await (await w.Client.PostJsonAsync($"/api/v1/tasks/{id}/complete", resolution, "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["Completed", "0", "Moved to the new hazmat cage"], new[] { (string)done["status"]!, (string)done["remainingQuantity"]!, (string)done["resolution"]! });
        await (await w.Client.PostJsonAsync($"/api/v1/tasks/{id}/complete", resolution, $"\"{done["version"]}\""))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        Assert.Equal("Closed", (string?)(await (await w.Client.GetAsync($"/api/v1/receipts/{receiptId}")).ExpectAsync(HttpStatusCode.OK))["status"]);
        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'TaskCompleted' AND object_id = @id",
            ("t", w.Tenant), ("id", Guid.Parse(id))));
    }

    [Fact]
    [Trait("Requirement", "INB-006")]
    [Trait("Requirement", "STK-007")]
    public async Task Confirmation_needs_matching_scans_a_known_source_and_no_more_than_remains()
    {
        var w = await WorldAsync("put-scan");
        var receipt = await ReceiveAsync(w, Item(w.LotLine, w.Lotted, "3", w.AmbientStaging, lotNumber: "L1", expirationDate: "2030-01-01"));
        var line = (string)receipt["lines"]![0]!["id"]!;
        async Task<string?> Rejected(object body, HttpStatusCode status, string code) =>
            (string?)(await (await ConfirmAsync(w, Guid.NewGuid(), body)).ExpectProblemAsync(status, code))["errors"]?[0]?["field"];

        Assert.Equal("scannedProductId", await Rejected(Confirm(line, w.AmbientStaging, w.A02, "3", w.General, lot: "L1"), HttpStatusCode.UnprocessableEntity, "BARCODE_CONFLICT"));
        Assert.Equal("scannedLotNumber", await Rejected(Confirm(line, w.AmbientStaging, w.A02, "3", w.Lotted, lot: "L2"), HttpStatusCode.UnprocessableEntity, "BARCODE_CONFLICT"));
        Assert.Equal("scannedLotNumber", await Rejected(Confirm(line, w.AmbientStaging, w.A02, "3", w.Lotted), HttpStatusCode.UnprocessableEntity, "BARCODE_CONFLICT"));
        Assert.Equal("quantity", await Rejected(Confirm(line, w.AmbientStaging, w.A02, "4", w.Lotted, lot: "L1"), HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"));
        Assert.Equal("sourceBinId", await Rejected(Confirm(line, w.ColdStaging, w.A02, "3", w.Lotted, lot: "L1"), HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"));
        await (await ConfirmAsync(w, Guid.NewGuid(), Confirm(Guid.NewGuid().ToString(), w.AmbientStaging, w.A02, "3", w.Lotted, lot: "L1")))
            .ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        var noKey = new HttpRequestMessage(HttpMethod.Post, "/api/v1/putaway/confirm") { Content = Json(Confirm(line, w.AmbientStaging, w.A02, "3", w.Lotted, lot: "L1")) };
        await (await w.Client.SendAsync(noKey)).ExpectProblemAsync(HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_REQUIRED");
        Assert.Empty(await ItemsAsync(w.Client, $"/api/v1/stock/movements?type=Putaway&productId={w.Lotted}"));
        await (await ConfirmAsync(w, Guid.NewGuid(), Confirm(line, w.AmbientStaging, w.A02, "3", w.Lotted, lot: "L1"))).ExpectAsync(HttpStatusCode.OK);
        await (await ConfirmAsync(w, Guid.NewGuid(), Confirm(line, w.AmbientStaging, w.A02, "1", w.Lotted, lot: "L1")))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE"); // the task is complete
    }

    // ---------------------------------------------------------------------------------------------------------------

    private sealed record World(Guid Tenant, HttpClient Client, string Warehouse, string AmbientZone, string AmbientStaging, string A01, string A02, string A03,
        string AmbientQuarantine, string ColdStaging, string C01, string HazardStaging, string Each, string General, string Lotted, string Medical,
        string Hazardous, string Order, string GenLine, string LotLine, string MedLine, string HazLine);

    private async Task<World> WorldAsync(string label)
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, label);
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        await TenantProvisioning.GrantSelfAsync(fx, tenant, "WarehouseManager");
        await TenantProvisioning.GrantSelfAsync(fx, tenant, "Receiver");
        async Task<string> Create(string path, object body) => (string)(await (await admin.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!;

        var warehouse = await Create("/api/v1/warehouses", new { code = "W1", name = "Main", timeZone = "Africa/Lagos" });
        var each = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        var box = await Create("/api/v1/units", new { code = "BOX", name = "Box" });
        var ambient = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "AMB", name = "Ambient" });
        var cold = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "COLD", name = "Cold", tempMinC = 2, tempMaxC = 8 });
        var hazardZone = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "HZ", name = "Hazard intake", allowedHazardClasses = HazardClasses });
        async Task<string> NewBin(string zone, string code, string kind, int? priority = null, string? capacity = null, string? capacityUom = null) =>
            await Create("/api/v1/bins", new { zoneId = zone, code, kind, pickPriority = priority, capacity, capacityUomId = capacityUom });
        var ambientStaging = await NewBin(ambient, "AMB-STG", "Staging");
        var a01 = await NewBin(ambient, "A-01", "Storage", 5);
        var a02 = await NewBin(ambient, "A-02", "Storage", 1);
        var a03 = await NewBin(ambient, "A-03", "Storage", 3, "10", each);
        await NewBin(ambient, "B-CAP", "Storage", 0, "2", box);
        var ambientQuarantine = await NewBin(ambient, "AMB-Q", "Quarantine", 9);
        var coldStaging = await NewBin(cold, "COLD-STG", "Staging");
        var c01 = await NewBin(cold, "C-01", "Storage", 1);
        var hazardStaging = await NewBin(hazardZone, "HZ-STG", "Staging");
        var profile = await Create("/api/v1/storage-profiles", new
        {
            name = "Cold chain", minC = 2, maxC = 8, monitoringRequired = true,
            excursionPolicy = new { immediateHold = true, notes = "QA reviewed" }, reason = "putaway test",
        });
        object Product(string sku, string handling, string tracking, string? storageProfile = null, string? hazard = null) => new
        {
            sku, name = sku, handlingProfile = handling, trackingMode = tracking, baseUomId = each, basePrecision = 0, storageProfileId = storageProfile,
            hazardClass = hazard,
        };
        var general = await Create("/api/v1/products", Product("GEN", "General", "Quantity"));
        var lotted = await Create("/api/v1/products", Product("LOT", "General", "Lot"));
        var medical = await Create("/api/v1/products", Product("MED", "Medical", "Lot", profile));
        var hazardous = await Create("/api/v1/products", Product("HAZ", "General", "Quantity", hazard: "UN1993"));
        var supplier = await Create("/api/v1/partners", new { code = "SUP", name = "Supplier", type = "Supplier" });
        var order = await (await admin.PostJsonAsync("/api/v1/purchase-orders", new
        {
            supplierId = supplier, warehouseId = warehouse, reference = "PO-P1",
            lines = new object[]
            {
                new { productId = general, quantity = "1000", uomId = each },
                new { productId = lotted, quantity = "1000", uomId = each },
                new { productId = medical, quantity = "1000", uomId = each },
                new { productId = hazardous, quantity = "100", uomId = each },
            },
        })).ExpectAsync(HttpStatusCode.Created);
        var orderId = (string)order["id"]!;
        await (await admin.PostEmptyAsync($"/api/v1/purchase-orders/{orderId}/approve", "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        var lines = order["lines"]!.AsArray().Select(l => (string)l!["id"]!).ToList();
        return new World(tenant.TenantId, admin, warehouse, ambient, ambientStaging, a01, a02, a03, ambientQuarantine, coldStaging, c01, hazardStaging, each,
            general, lotted, medical, hazardous, orderId, lines[0], lines[1], lines[2], lines[3]);
    }

    private sealed record ReceiveBody(string PurchaseOrderLineId, string ProductId, string Quantity, string UomId, string TargetBinId, string? LotNumber,
        string? ExpirationDate, string? SupplierReference, decimal? InitialTemperatureCelsius, string? InspectionResult, string? ExceptionReason);

    private string _each = "";

    private ReceiveBody Item(string line, string product, string quantity, string bin, string? lotNumber = null, string? expirationDate = null,
        string? supplierReference = null, decimal? temperature = null, string? inspection = null, string? exceptionReason = null) =>
        new(line, product, quantity, _each, bin, lotNumber, expirationDate, supplierReference, temperature, inspection, exceptionReason);

    private async Task<JsonObject> ReceiveAsync(World w, ReceiveBody item)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/inventory/receive")
        {
            Content = Json(new { warehouseId = w.Warehouse, purchaseOrderId = w.Order, items = new[] { item with { UomId = w.Each } } }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        _each = w.Each;
        return await (await w.Client.SendAsync(request)).ExpectAsync(HttpStatusCode.OK);
    }

    private static object Confirm(string line, string source, string target, string quantity, string product, string? lot = null, string? reason = null) =>
        new { receiptLineId = line, sourceBinId = source, targetBinId = target, quantity, scannedProductId = product, scannedLotNumber = lot, reason };

    private static Task<HttpResponseMessage> ConfirmAsync(World w, Guid key, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/putaway/confirm") { Content = Json(body) };
        request.Headers.Add("Idempotency-Key", key.ToString());
        return w.Client.SendAsync(request);
    }

    private static async Task<JsonObject> TaskAsync(World w, string receiptId) =>
        Assert.Single(await ItemsAsync(w.Client, $"/api/v1/putaway/tasks?receiptId={receiptId}"));

    private static string[] Codes(JsonObject task) => task["recommendedBins"]!.AsArray().Select(b => (string)b!["binCode"]!).ToArray();

    private static async Task<JsonObject> StockAsync(World w, string bucket) =>
        await (await w.Client.GetAsync($"/api/v1/stock/{bucket}")).ExpectAsync(HttpStatusCode.OK);

    private static async Task<List<JsonObject>> ItemsAsync(HttpClient client, string url) =>
        (await (await client.GetAsync(url)).ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray().Select(i => i!.AsObject()).ToList();

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body, Options), System.Text.Encoding.UTF8, "application/json");

    private async Task ReconcileAsync()
    {
        await using var app = DependencyInjection.BuildDataSource(fx.AppConnection);
        await using var platform = DependencyInjection.BuildDataSource(fx.PlatformConnection);
        var runner = new TenantJobRunner(new PlatformDataSource(platform), new AppDataSource(app), new FakeTimeProvider(DateTimeOffset.UtcNow));
        await new LedgerReconciliationJob(runner, fx.Factory.Services.GetRequiredService<IAuditSupport>(), NullLogger<LedgerReconciliationJob>.Instance)
            .RunOnceAsync(CancellationToken.None);
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args) =>
        (T)(await TenantProvisioning.ScalarAsSuperuserAsync(fx, sql, args))!;
}
